using System.Text.Json.Serialization;

namespace ExceptionLens.Core;

/// <summary>
/// Structured runtime diagnostic snapshot of a captured exception.
/// Serializes cleanly to JSON for shipping to Serilog, Application Insights,
/// OpenTelemetry, Datadog, CloudWatch, or any structured-log sink.
/// </summary>
public sealed class ExceptionDiagnostics
{
    // ── Identity ────────────────────────────────────────────────────────────
    public string ExceptionType { get; init; } = string.Empty;
    public string FullTypeName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? CorrelationId { get; set; }

    // ── Source location ──────────────────────────────────────────────────────
    public string? SourceFile { get; set; }
    public string? SourceFileShort { get; set; }
    public int? Line { get; set; }
    public string? Method { get; set; }
    public string? TypeName { get; set; }
    public string? AssemblyName { get; set; }

    // ── Failing expression ───────────────────────────────────────────────────
    /// <summary>Sub-expression that evaluated to null (NullReferenceException).</summary>
    public string? NullExpression { get; set; }
    /// <summary>Full source expression on the faulting line.</summary>
    public string? FailingExpression { get; set; }
    public string[]? SourceContext { get; set; }

    // ── Type-specific fields ─────────────────────────────────────────────────
    /// <summary>Parameter name (ArgumentNullException / ArgumentException).</summary>
    public string? ParameterName { get; set; }
    /// <summary>Missing key (KeyNotFoundException).</summary>
    public string? MissingKey { get; set; }
    /// <summary>Requested index (IndexOutOfRangeException).</summary>
    public int? RequestedIndex { get; set; }
    /// <summary>Collection length at time of access.</summary>
    public int? CollectionLength { get; set; }
    /// <summary>Collection or dictionary variable name.</summary>
    public string? CollectionName { get; set; }
    public string? ValidIndexRange { get; set; }
    /// <summary>Operation that failed (InvalidOperationException).</summary>
    public string? OperationName { get; set; }

    // ── Captured runtime values ──────────────────────────────────────────────
    /// <summary>
    /// Variable/expression → value pairs captured at the throw site.
    /// Populated automatically where possible; add more via exception.Capture(new { x, y }).
    /// </summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    // ── Call chain ───────────────────────────────────────────────────────────
    public List<CallFrame> CallChain { get; } = new();

    // ── Diagnosis ───────────────────────────────────────────────────────────
    public string? PossibleCause { get; set; }
    public string? Suggestion { get; set; }

    // ── Extra context ────────────────────────────────────────────────────────
    public Dictionary<string, object?> AdditionalData { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ── Inner exception ──────────────────────────────────────────────────────
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExceptionDiagnostics? InnerException { get; set; }
}

/// <summary>A single frame in the reconstructed call chain.</summary>
public sealed class CallFrame
{
    public string TypeName { get; init; } = string.Empty;
    public string MethodName { get; init; } = string.Empty;
    public string? SourceFile { get; init; }
    public int? Line { get; init; }

    public string Display => Line.HasValue
        ? $"{TypeName}.{MethodName}() — {SourceFile}:{Line}"
        : $"{TypeName}.{MethodName}()";
}
