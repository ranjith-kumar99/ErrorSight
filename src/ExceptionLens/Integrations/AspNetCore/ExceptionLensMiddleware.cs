using System.Net;
using System.Text.Json;
using ExceptionLens.Core;
using ExceptionLens.Formatting;
using ExceptionLens.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ExceptionLens.Integrations.AspNetCore;

/// <summary>
/// ASP.NET Core exception middleware that enriches unhandled exceptions
/// and logs them with full ExceptionLens diagnostics.
///
/// Register after UseRouting / before MapControllers:
///   app.UseExceptionLens();
/// </summary>
public sealed class ExceptionLensMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ExceptionEnricher _enricher;
    private readonly IExceptionFormatter _textFormatter;
    private readonly ExceptionLensOptions _options;
    private readonly ILogger<ExceptionLensMiddleware> _logger;

    public ExceptionLensMiddleware(
        RequestDelegate next,
        ExceptionEnricher enricher,
        IExceptionFormatter textFormatter,
        ExceptionLensOptions options,
        ILogger<ExceptionLensMiddleware> logger)
    {
        _next = next;
        _enricher = enricher;
        _textFormatter = textFormatter;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted && _options.ShouldEnrich(ex))
        {
            var correlationId = context.TraceIdentifier;
            var diagnostics = _enricher.Enrich(ex, correlationId);

            if (_options.LogEnrichedDiagnostics)
                LogDiagnostics(diagnostics, ex);

            await WriteErrorResponse(context, diagnostics);

            // Re-throw so the host records the request as failed
            throw;
        }
        catch (Exception ex) when (_options.ShouldEnrich(ex))
        {
            // Response already started — just enrich and log
            var correlationId = context.TraceIdentifier;
            var diagnostics = _enricher.Enrich(ex, correlationId);
            if (_options.LogEnrichedDiagnostics)
                LogDiagnostics(diagnostics, ex);
            throw;
        }
    }

    private void LogDiagnostics(ExceptionDiagnostics diagnostics, Exception ex)
    {
        if (_options.StructuredLogging)
        {
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["ExceptionLens.Type"] = diagnostics.ExceptionType,
                ["ExceptionLens.File"] = diagnostics.SourceFileShort ?? string.Empty,
                ["ExceptionLens.Line"] = diagnostics.Line ?? 0,
                ["ExceptionLens.Method"] = diagnostics.Method ?? string.Empty,
                ["ExceptionLens.NullExpression"] = diagnostics.NullExpression ?? string.Empty,
                ["ExceptionLens.PossibleCause"] = diagnostics.PossibleCause ?? string.Empty,
                ["ExceptionLens.Values"] = JsonSerializer.Serialize(diagnostics.Values),
                ["ExceptionLens.CorrelationId"] = diagnostics.CorrelationId ?? string.Empty
            }))
            {
                _logger.LogError(ex, "{Banner}", _textFormatter.Format(diagnostics));
            }
        }
        else
        {
            _logger.LogError(ex, "{Banner}", _textFormatter.Format(diagnostics));
        }
    }

    private async Task WriteErrorResponse(HttpContext context, ExceptionDiagnostics diagnostics)
    {
        if (_options.IncludeDiagnosticsInResponse)
        {
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            context.Response.ContentType = "application/problem+json";

            var problem = new
            {
                type = "https://httpstatuses.com/500",
                title = diagnostics.ExceptionType,
                status = 500,
                detail = diagnostics.Message,
                correlationId = diagnostics.CorrelationId,
                exceptionLens = new
                {
                    diagnostics.NullExpression,
                    diagnostics.FailingExpression,
                    diagnostics.SourceFileShort,
                    diagnostics.Line,
                    diagnostics.Method,
                    diagnostics.PossibleCause,
                    diagnostics.Suggestion,
                    diagnostics.Values,
                }
            };

            await context.Response.WriteAsJsonAsync(problem);
        }
    }
}
