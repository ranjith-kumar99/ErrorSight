using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using ExceptionLens.Core;

namespace ExceptionLens.Integrations.OpenTelemetry;

/// <summary>
/// Attribute and event names ExceptionLens adds to OpenTelemetry spans (<see cref="Activity"/>).
/// The standard <c>exception.*</c> attributes/events are never modified.
/// </summary>
public static class ExceptionLensSemanticConventions
{
    /// <summary>Span event carrying the full diagnosis.</summary>
    public const string DiagnosedEvent = "exceptionlens.exception.diagnosed";

    // ── Span attributes (small, searchable) ─────────────────────────────────
    /// <summary>Exception type, e.g. <c>System.NullReferenceException</c>.</summary>
    public const string CauseType = "exceptionlens.cause.type";
    /// <summary>The expression at fault, e.g. <c>order.Customer.Address</c>.</summary>
    public const string CauseExpression = "exceptionlens.cause.expression";
    /// <summary><c>OrderService.cs:42</c></summary>
    public const string CauseLocation = "exceptionlens.cause.location";
    /// <summary><c>OrderService.ProcessOrder</c></summary>
    public const string CauseMethod = "exceptionlens.cause.method";

    // ── Event attributes ────────────────────────────────────────────────────
    public const string ExceptionType = "exception.type";
    public const string ExceptionMessage = "exception.message";
    public const string NullExpression = "exceptionlens.null.expression";
    /// <summary><c>order → Customer → Address</c></summary>
    public const string NullChain = "exceptionlens.null.chain";
    public const string NullCandidates = "exceptionlens.null.candidates";
    public const string FailingExpression = "exceptionlens.failing_expression";
    public const string SourceFile = "exceptionlens.source.file";
    public const string SourceLine = "exceptionlens.source.line";
    public const string Method = "exceptionlens.method";
    public const string Cause = "exceptionlens.cause";
    public const string Suggestion = "exceptionlens.suggestion";
    /// <summary>JSON object of the (masked) runtime values at the throw site.</summary>
    public const string Values = "exceptionlens.values";
    public const string Parameter = "exceptionlens.parameter";
    public const string MissingKey = "exceptionlens.missing_key";
    public const string Collection = "exceptionlens.collection";
    public const string Index = "exceptionlens.index";
}

/// <summary>Writes <see cref="ExceptionDiagnostics"/> onto a span.</summary>
public static class ActivityDiagnostics
{
    // Attribute values are telemetry strings, not HTML: keep "…" and quotes readable in backends.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Adds the <c>exceptionlens.cause.*</c> span attributes and one
    /// <c>exceptionlens.exception.diagnosed</c> event to <paramref name="activity"/>.
    /// </summary>
    public static void Apply(Activity activity, ExceptionDiagnostics d)
    {
        var method = MethodName(d);
        var location = d.SourceFileShort is not null && d.Line is not null ? $"{d.SourceFileShort}:{d.Line}" : d.SourceFileShort;
        var expression = d.NullExpression ?? d.FailingExpression;

        activity.SetTag(ExceptionLensSemanticConventions.CauseType, d.FullTypeName);
        if (expression is not null) activity.SetTag(ExceptionLensSemanticConventions.CauseExpression, expression);
        if (location is not null) activity.SetTag(ExceptionLensSemanticConventions.CauseLocation, location);
        if (method is not null) activity.SetTag(ExceptionLensSemanticConventions.CauseMethod, method);

        var tags = new ActivityTagsCollection
        {
            [ExceptionLensSemanticConventions.ExceptionType] = d.FullTypeName,
            [ExceptionLensSemanticConventions.ExceptionMessage] = d.Message,
        };

        AddIfPresent(tags, ExceptionLensSemanticConventions.NullExpression, d.NullExpression);
        if (d.NullExpression is not null)
            tags[ExceptionLensSemanticConventions.NullChain] = string.Join(" → ", CapturedValuesResolver.SplitPath(d.NullExpression));
        if (d.NullCandidates is { Length: > 0 } candidates)
            tags[ExceptionLensSemanticConventions.NullCandidates] = candidates;
        AddIfPresent(tags, ExceptionLensSemanticConventions.FailingExpression, d.FailingExpression);
        AddIfPresent(tags, ExceptionLensSemanticConventions.SourceFile, d.SourceFileShort);
        if (d.Line is { } line) tags[ExceptionLensSemanticConventions.SourceLine] = line;
        AddIfPresent(tags, ExceptionLensSemanticConventions.Method, method);
        AddIfPresent(tags, ExceptionLensSemanticConventions.Cause, d.PossibleCause);
        AddIfPresent(tags, ExceptionLensSemanticConventions.Suggestion, d.Suggestion);
        AddIfPresent(tags, ExceptionLensSemanticConventions.Parameter, d.ParameterName);
        AddIfPresent(tags, ExceptionLensSemanticConventions.MissingKey, d.MissingKey);
        AddIfPresent(tags, ExceptionLensSemanticConventions.Collection, d.CollectionName);
        if (d.RequestedIndex is { } index) tags[ExceptionLensSemanticConventions.Index] = index;
        if (d.Values.Count > 0)
        {
            try { tags[ExceptionLensSemanticConventions.Values] = JsonSerializer.Serialize(d.Values, JsonOptions); }
            catch { /* unserialisable value: skip */ }
        }

        activity.AddEvent(new ActivityEvent(ExceptionLensSemanticConventions.DiagnosedEvent, tags: tags));
    }

    /// <summary><c>OrderService.ProcessOrder</c>: the instrumented throw-site frame, else the stack-trace frame.</summary>
    private static string? MethodName(ExceptionDiagnostics d)
    {
        if (d.Frames.Count > 0) return d.Frames[0].Method;
        if (d.Method is null) return null;
        var paren = d.Method.IndexOf('(');
        var name = paren < 0 ? d.Method : d.Method[..paren];
        return d.TypeName is null ? name : $"{d.TypeName}.{name}";
    }

    private static void AddIfPresent(ActivityTagsCollection tags, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value)) tags[key] = value;
    }
}
