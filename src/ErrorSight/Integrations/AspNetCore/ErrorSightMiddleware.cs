using System.Net;
using System.Text.Json;
using ErrorSight.Core;
using ErrorSight.Formatting;
using ErrorSight.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ErrorSight.Integrations.AspNetCore;

/// <summary>
/// ASP.NET Core exception middleware that enriches unhandled exceptions
/// and logs them with full ErrorSight diagnostics.
///
/// Register after UseRouting / before MapControllers:
///   app.UseErrorSight();
/// </summary>
public sealed class ErrorSightMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ExceptionEnricher _enricher;
    private readonly IExceptionFormatter _textFormatter;
    private readonly ErrorSightOptions _options;
    private readonly ILogger<ErrorSightMiddleware> _logger;

    public ErrorSightMiddleware(
        RequestDelegate next,
        ExceptionEnricher enricher,
        IExceptionFormatter textFormatter,
        ErrorSightOptions options,
        ILogger<ErrorSightMiddleware> logger)
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
                ["ErrorSight.Type"] = diagnostics.ExceptionType,
                ["ErrorSight.File"] = diagnostics.SourceFileShort ?? string.Empty,
                ["ErrorSight.Line"] = diagnostics.Line ?? 0,
                ["ErrorSight.Method"] = diagnostics.Method ?? string.Empty,
                ["ErrorSight.NullExpression"] = diagnostics.NullExpression ?? string.Empty,
                ["ErrorSight.PossibleCause"] = diagnostics.PossibleCause ?? string.Empty,
                ["ErrorSight.Values"] = JsonSerializer.Serialize(diagnostics.Values),
                ["ErrorSight.CorrelationId"] = diagnostics.CorrelationId ?? string.Empty
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
                errorSight = new
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
