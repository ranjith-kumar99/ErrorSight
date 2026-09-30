using System.Text.Json;
using System.Text.Json.Serialization;
using ErrorSight.Options;
using ErrorSight.Runtime;

namespace ErrorSight.Core;

/// <summary>
/// Structured diagnosis of an exception. What it contains depends on <see cref="DataCapture"/>: by default only
/// diagnostic structure (type, location, the null expression and its origin), never application data.
/// Serializes cleanly to JSON for shipping to Serilog, Application Insights,
/// OpenTelemetry, Datadog, CloudWatch, or any structured-log sink.
/// </summary>
public sealed class ExceptionDiagnostics
{
    // ── Identity ────────────────────────────────────────────────────────────
    public string ExceptionType { get; init; } = string.Empty;
    public string FullTypeName { get; init; } = string.Empty;
    /// <summary>
    /// The exception message. Messages often embed application data, so it is only included at
    /// <see cref="DataCapture.Values"/>; null otherwise.
    /// </summary>
    public string? Message { get; set; }
    /// <summary>The data-capture level these diagnostics were produced with.</summary>
    public DataCapture DataCapture { get; init; }
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
    /// <summary>Sub-expression that evaluated to null (NullReferenceException), e.g. <c>order.Customer.Address</c>.</summary>
    public string? NullExpression { get; set; }
    /// <summary>
    /// When the null expression could not be pinned down, the expressions on the failing line that
    /// could have been null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? NullCandidates { get; set; }
    /// <summary>Declared type of the null expression, e.g. <c>Address</c> (<see cref="DataCapture.Metadata"/> and above).</summary>
    public string? NullType { get; set; }
    /// <summary>
    /// Where the null came from when the null expression starts at a local variable, e.g. <c>address</c> was
    /// assigned <c>customer.Address</c> on line 128.
    /// </summary>
    public NullOrigin? NullOrigin { get; set; }
    /// <summary>Full source expression on the faulting line.</summary>
    public string? FailingExpression { get; set; }
    public string[]? SourceContext { get; set; }
    /// <summary>The failing source line, read only when <see cref="ErrorSightOptions.IncludeSourceContext"/> is on.</summary>
    [JsonIgnore]
    internal string? FailingSourceLine { get; set; }

    // ── Type-specific fields ─────────────────────────────────────────────────
    /// <summary>Parameter name (ArgumentNullException / ArgumentException).</summary>
    public string? ParameterName { get; set; }
    /// <summary>Missing key (KeyNotFoundException; <see cref="DataCapture.Values"/> only).</summary>
    public string? MissingKey { get; set; }
    /// <summary>Requested index (IndexOutOfRangeException; <see cref="DataCapture.Values"/> only).</summary>
    public int? RequestedIndex { get; set; }
    /// <summary>Collection length at time of access (<see cref="DataCapture.Metadata"/> and above).</summary>
    public int? CollectionLength { get; set; }
    /// <summary>Collection or dictionary expression, e.g. <c>order.Lines</c>.</summary>
    public string? CollectionName { get; set; }
    /// <summary>Collection type, e.g. <c>List&lt;OrderLine&gt;</c> (<see cref="DataCapture.Metadata"/> and above).</summary>
    public string? CollectionType { get; set; }
    public string? ValidIndexRange { get; set; }
    /// <summary>Operation that failed (InvalidOperationException).</summary>
    public string? OperationName { get; set; }

    // ── Captured runtime values ──────────────────────────────────────────────
    /// <summary>
    /// Expression → value pairs for the expressions involved in the failure (e.g. the chain that was null),
    /// already masked. Only filled at <see cref="DataCapture.Values"/>. Captured values are
    /// <see cref="ValueDisplay"/> text; null means the value was null.
    /// </summary>
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Each instrumented frame, innermost (throw site) first, with its variables at the configured level
    /// (names and types; values only at <see cref="DataCapture.Values"/>).
    /// </summary>
    public List<FrameDiagnostics> Frames { get; } = new();

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

/// <summary>
/// Where a null value came from: the assignment to the local variable at the start of the null expression.
/// </summary>
public sealed class NullOrigin
{
    /// <summary>The local variable, e.g. <c>address</c>.</summary>
    public string Variable { get; init; } = string.Empty;
    /// <summary>What was assigned to it, e.g. <c>customer.Address</c>, <c>this.FindAddress(…)</c> or <c>null</c>.</summary>
    public string Expression { get; init; } = string.Empty;
    /// <summary>Source line of the assignment.</summary>
    public int? Line { get; init; }
}

/// <summary>Values captured in one instrumented stack frame.</summary>
public sealed class FrameDiagnostics
{
    /// <summary>Source-level method name, e.g. <c>OrderService.ProcessOrder</c>.</summary>
    public string Method { get; init; } = string.Empty;
    public string? SourceFile { get; init; }
    public int? Line { get; init; }
    [JsonIgnore]
    public int? ILOffset { get; init; }
    /// <summary>Parameters, locals and <c>this</c> in scope at the failure point.</summary>
    public IReadOnlyList<CapturedValue> Values { get; init; } = Array.Empty<CapturedValue>();
}

/// <summary>
/// Pre-formatted display text for a captured value (e.g. <c>"Jane"</c>, <c>Customer { Id = 1837 }</c>, <c>***</c>).
/// Serialises to JSON as a plain string.
/// </summary>
[JsonConverter(typeof(ValueDisplayJsonConverter))]
public sealed class ValueDisplay
{
    public ValueDisplay(string text) => Text = text;

    public string Text { get; }

    public override string ToString() => Text;

    public override bool Equals(object? obj) => obj is ValueDisplay other && other.Text == Text;

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);
}

internal sealed class ValueDisplayJsonConverter : JsonConverter<ValueDisplay>
{
    public override ValueDisplay? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } text ? new ValueDisplay(text) : null;

    public override void Write(Utf8JsonWriter writer, ValueDisplay value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
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
