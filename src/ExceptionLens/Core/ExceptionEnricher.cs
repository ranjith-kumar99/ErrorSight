using System.Runtime.CompilerServices;
using ExceptionLens.Analyzers;
using ExceptionLens.Masking;
using ExceptionLens.Options;
using ExceptionLens.Runtime;

namespace ExceptionLens.Core;

/// <summary>
/// Orchestrates the full enrichment pipeline for an exception:
///   1. Stack-trace parsing (file / line / method / call chain)
///   2. Runtime values captured automatically by the build-time instrumentation,
///      including the exact null expression for NullReferenceException
///   3. Type-specific analyzers (null, arg, key, index, operation)
///   4. Masking of values that surface through exception data and messages
/// </summary>
public sealed class ExceptionEnricher
{
    private const int MaxInnerDepth = 5;

    private readonly IReadOnlyList<IExceptionAnalyzer> _analyzers;
    private readonly ExceptionLensOptions _options;
    private readonly ConditionalWeakTable<Exception, CacheEntry> _cache = new();

    public ExceptionEnricher(IEnumerable<IExceptionAnalyzer> analyzers, ExceptionLensOptions options)
    {
        _analyzers = analyzers.ToList().AsReadOnly();
        _options = options;
    }

    /// <summary>An enricher with all built-in analyzers, for code that does not use dependency injection.</summary>
    public static ExceptionEnricher CreateDefault(ExceptionLensOptions? options = null) => new(
        new IExceptionAnalyzer[]
        {
            new NullReferenceAnalyzer(),
            new ArgumentNullAnalyzer(),
            new KeyNotFoundAnalyzer(),
            new IndexOutOfRangeAnalyzer(),
            new InvalidOperationAnalyzer(),
            new AggregateExceptionAnalyzer(),
        },
        options ?? ExceptionLensRuntime.Options);

    /// <summary>
    /// Builds diagnostics for <paramref name="exception"/>. Results are cached per exception, so the
    /// OpenTelemetry hook, middleware and your own code can all call this cheaply.
    /// </summary>
    public ExceptionDiagnostics Enrich(Exception exception, string? correlationId = null)
    {
        var frames = _options.CaptureRuntimeValues ? CaptureStore.Get(exception) : null;
        var frameCount = frames?.Count ?? 0;

        if (_cache.TryGetValue(exception, out var cached) && cached.FrameCount == frameCount)
        {
            if (correlationId is not null) cached.Diagnostics.CorrelationId ??= correlationId;
            return cached.Diagnostics;
        }

        var diagnostics = EnrichCore(exception, correlationId, frames, depth: 0);
        _cache.AddOrUpdate(exception, new CacheEntry(diagnostics, frameCount));
        return diagnostics;
    }

    private ExceptionDiagnostics EnrichCore(Exception exception, string? correlationId, IReadOnlyList<CapturedFrame>? frames, int depth)
    {
        var masker = new ValueMasker(_options.Masking);
        var diagnostics = new ExceptionDiagnostics
        {
            ExceptionType = exception.GetType().Name,
            FullTypeName = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        // 1 ── Stack trace: throw-site location + call chain (one StackTrace, shared)
        var stackFrames = StackTraceParser.GetFrames(exception, _options.CaptureSourceLocation);
        diagnostics.CallChain.AddRange(StackTraceParser.ParseCallChain(exception, stackFrames, _options.CaptureSourceLocation));
        var (file, line, method, typeName) = StackTraceParser.GetThrowSite(stackFrames);
        diagnostics.Method = method;
        diagnostics.TypeName = typeName;
        diagnostics.AssemblyName = exception.TargetSite?.DeclaringType?.Assembly.GetName().Name;
        if (_options.CaptureSourceLocation)
        {
            diagnostics.SourceFile = file;
            diagnostics.SourceFileShort = file is not null ? Path.GetFileName(file) : null;
            diagnostics.Line = line;

            if (_options.IncludeSourceContext)
                diagnostics.SourceContext = StackTraceParser.ReadSourceContext(exception, _options.SourceContextLines);
        }

        // 2 ── Exception.Data entries (masked by name)
        foreach (System.Collections.DictionaryEntry entry in exception.Data)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            var value = entry.Value;
            diagnostics.AdditionalData[key] = value is not null && masker.ShouldMask(key, key, value.GetType(), false)
                ? masker.Mask(value.ToString())
                : value;
        }

        // 3 ── Runtime values captured by the build-time instrumentation
        if (frames is not null)
        {
            try { CapturedValuesResolver.Apply(diagnostics, stackFrames, frames, _options); }
            catch { /* diagnostics must never crash the app */ }
        }

        // 4 ── Type-specific analyzers
        foreach (var analyzer in _analyzers)
        {
            if (analyzer.CanAnalyze(exception))
            {
                try { analyzer.Enrich(diagnostics, exception); }
                catch { /* analyzers must never crash the app */ }
            }
        }

        // 5 ── Values that analyzers lift out of exception messages
        MaskMessageValues(diagnostics, masker);

        // 6 ── Inner exception
        if (exception.InnerException is not null && _options.IncludeInnerException && depth < MaxInnerDepth)
        {
            var innerFrames = _options.CaptureRuntimeValues ? CaptureStore.Get(exception.InnerException) : null;
            diagnostics.InnerException = EnrichCore(exception.InnerException, correlationId, innerFrames, depth + 1);
        }

        return diagnostics;
    }

    /// <summary>
    /// The missing key of a KeyNotFoundException comes from the exception message, not from captured
    /// values, so it is masked here (always in Mode=All, or when the dictionary's name is sensitive).
    /// </summary>
    private static void MaskMessageValues(ExceptionDiagnostics d, ValueMasker masker)
    {
        if (d.MissingKey is not { Length: > 0 } key) return;

        var sensitiveCollection = d.CollectionName is { } collection && masker.IsSensitiveName(collection);
        if (masker.Mode == MaskingMode.None || !(masker.MaskMessages || sensitiveCollection)) return;

        var masked = masker.Mask(key);
        d.MissingKey = masked;
        d.Message = d.Message.Replace(key, masked, StringComparison.Ordinal);
        d.PossibleCause = d.PossibleCause?.Replace(key, masked, StringComparison.Ordinal);
        d.Suggestion = d.Suggestion?.Replace(key, masked, StringComparison.Ordinal);
        if (d.Values.ContainsKey("missingKey")) d.Values["missingKey"] = new ValueDisplay(masked);
    }

    private sealed record CacheEntry(ExceptionDiagnostics Diagnostics, int FrameCount);
}
