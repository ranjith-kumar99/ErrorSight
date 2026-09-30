using System.Text.Json;
using ExceptionLens.Core;
using ExceptionLens.Formatting;
using ExceptionLens.Options;
using Microsoft.Extensions.Logging;

namespace ExceptionLens.Integrations.Logging;

/// <summary>
/// ILogger decorator that intercepts log calls with exceptions and enriches them
/// before passing to the underlying logger.
///
/// Compatible with Serilog, NLog, log4net, and any ILogger-backed sink.
/// </summary>
internal sealed class ExceptionLensLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly ExceptionEnricher _enricher;
    private readonly IExceptionFormatter _formatter;
    private readonly ExceptionLensOptions _options;
    private readonly string _categoryName;

    public ExceptionLensLogger(
        ILogger inner,
        ExceptionEnricher enricher,
        IExceptionFormatter formatter,
        ExceptionLensOptions options,
        string categoryName)
    {
        _inner = inner;
        _enricher = enricher;
        _formatter = formatter;
        _options = options;
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception is null || !_options.ShouldEnrich(exception))
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
            return;
        }

        var diagnostics = _enricher.Enrich(exception);

        if (_options.StructuredLogging)
        {
            using (_inner.BeginScope(BuildDiagnosticsScope(diagnostics)))
            {
                var enrichedMessage = _options.LogEnrichedDiagnostics
                    ? formatter(state, exception) + _formatter.Format(diagnostics)
                    : formatter(state, exception);

                _inner.Log(logLevel, eventId, state, exception,
                    (_, _) => enrichedMessage);
            }
        }
        else
        {
            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    private static Dictionary<string, object> BuildDiagnosticsScope(ExceptionDiagnostics d) =>
        new(StringComparer.Ordinal)
        {
            ["ExceptionLens.Type"] = d.ExceptionType,
            ["ExceptionLens.File"] = d.SourceFileShort ?? string.Empty,
            ["ExceptionLens.Line"] = d.Line ?? 0,
            ["ExceptionLens.Method"] = d.Method ?? string.Empty,
            ["ExceptionLens.NullExpression"] = d.NullExpression ?? string.Empty,
            ["ExceptionLens.PossibleCause"] = d.PossibleCause ?? string.Empty,
            ["ExceptionLens.Values"] = JsonSerializer.Serialize(d.Values)
        };
}
