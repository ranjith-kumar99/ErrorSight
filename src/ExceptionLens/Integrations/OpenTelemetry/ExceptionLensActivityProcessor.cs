using System.Diagnostics;
using System.Text.Json;
using ExceptionLens.Core;
using ExceptionLens.Options;

namespace ExceptionLens.Integrations.OpenTelemetry;

/// <summary>
/// OpenTelemetry ActivityProcessor that enriches any Activity tagged
/// with an exception event (otel.status_code = ERROR) by appending
/// ExceptionLens diagnostics as activity tags.
///
/// Compatible with Jaeger, Zipkin, OTLP (Datadog, Honeycomb, Grafana Tempo, etc.)
///
/// Usage (when using OpenTelemetry.Sdk directly):
///   builder.Services.AddOpenTelemetry()
///       .WithTracing(t => t.AddProcessor(
///           sp => sp.GetRequiredService{ExceptionLensActivityProcessor}()));
/// </summary>
public sealed class ExceptionLensActivityProcessor : BaseActivityProcessor
{
    private readonly ExceptionEnricher _enricher;
    private readonly ExceptionLensOptions _options;

    public ExceptionLensActivityProcessor(ExceptionEnricher enricher, ExceptionLensOptions options)
    {
        _enricher = enricher;
        _options = options;
    }

    public override void OnEnd(Activity activity)
    {
        if (activity.Status != ActivityStatusCode.Error) return;

        foreach (var @event in activity.Events)
        {
            if (@event.Name != "exception") continue;

            // Reconstruct the exception from OTel tags if available
            var exType = @event.Tags
                .FirstOrDefault(t => t.Key == "exception.type").Value as string;
            var exMsg = @event.Tags
                .FirstOrDefault(t => t.Key == "exception.message").Value as string;

            if (exType is null && exMsg is null) continue;

            // Attach lightweight diagnostics without a live Exception object
            activity.SetTag("exceptionlens.type", exType ?? "Unknown");
            activity.SetTag("exceptionlens.message", exMsg ?? "Unknown");
        }

        base.OnEnd(activity);
    }

    /// <summary>
    /// Call this from your exception handler to enrich the current Activity.
    /// </summary>
    public void EnrichCurrentActivity(Exception exception)
    {
        var current = Activity.Current;
        if (current is null) return;

        if (!_options.ShouldEnrich(exception)) return;

        var diagnostics = _enricher.Enrich(exception, current.TraceId.ToString());

        current.SetTag("exceptionlens.type", diagnostics.ExceptionType);
        current.SetTag("exceptionlens.null_expression", diagnostics.NullExpression ?? string.Empty);
        current.SetTag("exceptionlens.file", diagnostics.SourceFileShort ?? string.Empty);
        current.SetTag("exceptionlens.line", diagnostics.Line?.ToString() ?? string.Empty);
        current.SetTag("exceptionlens.method", diagnostics.Method ?? string.Empty);
        current.SetTag("exceptionlens.cause", diagnostics.PossibleCause ?? string.Empty);
        current.SetTag("exceptionlens.values", JsonSerializer.Serialize(diagnostics.Values));
    }
}

/// <summary>Minimal base class so the package doesn't require the OTel NuGet.</summary>
public abstract class BaseActivityProcessor
{
    public virtual void OnStart(Activity activity) { }
    public virtual void OnEnd(Activity activity) { }
}
