using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorSight.Core;

namespace ErrorSight.Formatting;

/// <summary>
/// Serialises ExceptionDiagnostics to JSON for structured-log sinks
/// (Serilog, Application Insights, CloudWatch, OpenTelemetry, Datadog, ELK).
/// </summary>
public sealed class JsonExceptionFormatter : IExceptionFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Format(ExceptionDiagnostics diagnostics) =>
        JsonSerializer.Serialize(diagnostics, Options);
}
