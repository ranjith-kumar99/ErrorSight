using System.Reflection;
using System.Text.Json;
using ExceptionLens.Analyzers;
using ExceptionLens.Options;

namespace ExceptionLens.Core;

/// <summary>
/// Orchestrates the full enrichment pipeline for a captured exception:
///   1. Base stack-trace parsing (file / line / method / call chain)
///   2. Developer-attached context from Exception.Data
///   3. Type-specific analyzers (null, arg, key, index, operation)
///   4. Optional source-code context lines
/// </summary>
public sealed class ExceptionEnricher
{
    private readonly IReadOnlyList<IExceptionAnalyzer> _analyzers;
    private readonly ExceptionLensOptions _options;

    public ExceptionEnricher(IEnumerable<IExceptionAnalyzer> analyzers, ExceptionLensOptions options)
    {
        _analyzers = analyzers.ToList().AsReadOnly();
        _options = options;
    }

    public ExceptionDiagnostics Enrich(Exception exception, string? correlationId = null)
    {
        var diagnostics = new ExceptionDiagnostics
        {
            ExceptionType = exception.GetType().Name,
            FullTypeName = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        // 1 ── Stack trace: throw-site location + call chain
        var (file, line, method, typeName) = StackTraceParser.GetThrowSite(exception);
        diagnostics.SourceFile = file;
        diagnostics.SourceFileShort = file is not null ? Path.GetFileName(file) : null;
        diagnostics.Line = line;
        diagnostics.Method = method;
        diagnostics.TypeName = typeName;
        diagnostics.AssemblyName = exception.TargetSite?.DeclaringType?.Assembly.GetName().Name;

        var callChain = StackTraceParser.ParseCallChain(exception);
        diagnostics.CallChain.AddRange(callChain);

        // 2 ── Source context lines (only when source files are present in the environment)
        if (_options.IncludeSourceContext)
        {
            diagnostics.SourceContext = StackTraceParser.ReadSourceContext(exception, _options.SourceContextLines);
        }

        // 3 ── Developer-attached context from Exception.Data
        ApplyExceptionData(diagnostics, exception);

        // 4 ── Type-specific analyzers
        foreach (var analyzer in _analyzers)
        {
            if (analyzer.CanAnalyze(exception))
            {
                try { analyzer.Enrich(diagnostics, exception); }
                catch { /* analyzers must never crash the app */ }
            }
        }

        // 5 ── Recurse into inner exception (one level only to avoid cycles)
        if (exception.InnerException is not null && _options.IncludeInnerException)
        {
            diagnostics.InnerException = Enrich(exception.InnerException);
        }

        return diagnostics;
    }

    private static void ApplyExceptionData(ExceptionDiagnostics diagnostics, Exception exception)
    {
        foreach (System.Collections.DictionaryEntry entry in exception.Data)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            var value = entry.Value;

            switch (key)
            {
                case ExceptionDataKeys.CapturedValues when value is Dictionary<string, object?> captured:
                    foreach (var (k, v) in captured)
                        diagnostics.Values[k] = v;
                    break;

                case ExceptionDataKeys.NullExpression when value is string nullExpr:
                    diagnostics.NullExpression = nullExpr;
                    break;

                case ExceptionDataKeys.FailingExpression when value is string failExpr:
                    diagnostics.FailingExpression = failExpr;
                    break;

                case ExceptionDataKeys.CorrelationId when value is string corrId:
                    diagnostics.CorrelationId ??= corrId;
                    break;

                default:
                    if (!key.StartsWith("ExceptionLens.", StringComparison.Ordinal))
                        diagnostics.AdditionalData[key] = value;
                    break;
            }
        }
    }
}
