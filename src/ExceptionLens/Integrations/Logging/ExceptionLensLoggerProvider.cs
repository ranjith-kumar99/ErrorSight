using ExceptionLens.Core;
using ExceptionLens.Formatting;
using ExceptionLens.Options;
using Microsoft.Extensions.Logging;

namespace ExceptionLens.Integrations.Logging;

/// <summary>
/// ILoggerProvider that wraps every created ILogger with ExceptionLensLogger.
/// Registered via AddExceptionLens() — no manual setup required.
/// </summary>
[ProviderAlias("ExceptionLens")]
internal sealed class ExceptionLensLoggerProvider : ILoggerProvider
{
    private readonly ILoggerFactory _innerFactory;
    private readonly ExceptionEnricher _enricher;
    private readonly IExceptionFormatter _formatter;
    private readonly ExceptionLensOptions _options;

    public ExceptionLensLoggerProvider(
        ILoggerFactory innerFactory,
        ExceptionEnricher enricher,
        IExceptionFormatter formatter,
        ExceptionLensOptions options)
    {
        _innerFactory = innerFactory;
        _enricher = enricher;
        _formatter = formatter;
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        var inner = _innerFactory.CreateLogger(categoryName);
        return new ExceptionLensLogger(inner, _enricher, _formatter, _options, categoryName);
    }

    public void Dispose() { }
}
