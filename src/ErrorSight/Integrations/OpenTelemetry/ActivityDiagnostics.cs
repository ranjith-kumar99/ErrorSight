using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using ErrorSight.Core;

namespace ErrorSight.Integrations.OpenTelemetry;

/// <summary>
/// Attribute and event names ErrorSight adds to OpenTelemetry spans (<see cref="Activity"/>).
/// The standard <c>exception.*</c> attributes/events are never modified.
/// </summary>
public static class ErrorSightSemanticConventions
{
    /// <summary>Span event carrying the diagnosis.</summary>
    public const string DiagnosedEvent = "errorsight.exception.diagnosed";

    // ── Span attributes (small, searchable) ─────────────────────────────────
    /// <summary>Exception type, e.g. <c>System.NullReferenceException</c>.</summary>
    public const string CauseType = "errorsight.cause.type";
    /// <summary>The expression at fault, e.g. <c>order.Customer.Address</c>.</summary>
    public const string CauseExpression = "errorsight.cause.expression";
    /// <summary><c>OrderService.cs:42</c></summary>
    public const string CauseLocation = "errorsight.cause.location";
    /// <summary><c>OrderService.ProcessOrder</c></summary>
    public const string CauseMethod = "errorsight.cause.method";

    // ── Event attributes: structure (always) ─────────────────────────────────
    public const string ExceptionType = "exception.type";
    /// <summary><c>none</c>, <c>metadata</c> or <c>values</c>.</summary>
    public const string DataCapture = "errorsight.data_capture";
    public const string NullExpression = "errorsight.null.expression";
    /// <summary><c>order → Customer → Address</c></summary>
    public const string NullChain = "errorsight.null.chain";
    public const string NullCandidates = "errorsight.null.candidates";
    /// <summary>What the null local variable was assigned, e.g. <c>customer.Address</c>.</summary>
    public const string OriginExpression = "errorsight.origin.expression";
    public const string OriginVariable = "errorsight.origin.variable";
    /// <summary>Line of that assignment.</summary>
    public const string OriginLine = "errorsight.origin.line";
    public const string FailingExpression = "errorsight.failing_expression";
    public const string SourceFile = "errorsight.source.file";
    public const string SourceLine = "errorsight.source.line";
    public const string Method = "errorsight.method";
    public const string Cause = "errorsight.cause";
    public const string Suggestion = "errorsight.suggestion";
    public const string Parameter = "errorsight.parameter";
    public const string CollectionName = "errorsight.collection.name";

    // ── Event attributes: metadata (DataCapture.Metadata and above) ─────────
    /// <summary>Declared type of the null expression, e.g. <c>Address</c>.</summary>
    public const string ObjectType = "errorsight.object.type";
    public const string ObjectNull = "errorsight.object.null";
    /// <summary>The member that was null, e.g. <c>Address</c>.</summary>
    public const string Member = "errorsight.member";
    public const string CollectionType = "errorsight.collection.type";
    public const string CollectionCount = "errorsight.collection.count";
    public const string CollectionEmpty = "errorsight.collection.empty";

    // ── Event attributes: values (DataCapture.Values only, masked) ──────────
    public const string ExceptionMessage = "exception.message";
    /// <summary>JSON object of the (masked) values of the expressions involved in the failure.</summary>
    public const string Values = "errorsight.values";
    public const string MissingKey = "errorsight.missing_key";
    public const string Index = "errorsight.index";
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
    /// Adds the <c>errorsight.cause.*</c> span attributes and one <c>errorsight.exception.diagnosed</c> event to
    /// <paramref name="activity"/>. Only what <see cref="ExceptionDiagnostics.DataCapture"/> allows is written.
    /// </summary>
    public static void Apply(Activity activity, ExceptionDiagnostics d)
    {
        var method = MethodName(d);
        var location = d.SourceFileShort is not null && d.Line is not null ? $"{d.SourceFileShort}:{d.Line}" : d.SourceFileShort;
        var expression = d.NullExpression ?? d.FailingExpression;

        activity.SetTag(ErrorSightSemanticConventions.CauseType, d.FullTypeName);
        if (expression is not null) activity.SetTag(ErrorSightSemanticConventions.CauseExpression, expression);
        if (location is not null) activity.SetTag(ErrorSightSemanticConventions.CauseLocation, location);
        if (method is not null) activity.SetTag(ErrorSightSemanticConventions.CauseMethod, method);

        var level = d.DataCapture;
        var tags = new ActivityTagsCollection
        {
            [ErrorSightSemanticConventions.ExceptionType] = d.FullTypeName,
            [ErrorSightSemanticConventions.DataCapture] = level.ToString().ToLowerInvariant(),
        };

        // Structure: names, types and locations, no application data.
        AddIfPresent(tags, ErrorSightSemanticConventions.NullExpression, d.NullExpression);
        if (d.NullExpression is not null)
            tags[ErrorSightSemanticConventions.NullChain] = string.Join(" → ", CapturedValuesResolver.SplitPath(d.NullExpression));
        if (d.NullCandidates is { Length: > 0 } candidates)
            tags[ErrorSightSemanticConventions.NullCandidates] = candidates;
        if (d.NullOrigin is { } origin)
        {
            tags[ErrorSightSemanticConventions.OriginExpression] = origin.Expression;
            tags[ErrorSightSemanticConventions.OriginVariable] = origin.Variable;
            if (origin.Line is { } originLine) tags[ErrorSightSemanticConventions.OriginLine] = originLine;
        }
        AddIfPresent(tags, ErrorSightSemanticConventions.FailingExpression, d.FailingExpression);
        AddIfPresent(tags, ErrorSightSemanticConventions.SourceFile, d.SourceFileShort);
        if (d.Line is { } line) tags[ErrorSightSemanticConventions.SourceLine] = line;
        AddIfPresent(tags, ErrorSightSemanticConventions.Method, method);
        AddIfPresent(tags, ErrorSightSemanticConventions.Cause, d.PossibleCause);
        AddIfPresent(tags, ErrorSightSemanticConventions.Suggestion, d.Suggestion);
        AddIfPresent(tags, ErrorSightSemanticConventions.Parameter, d.ParameterName);
        AddIfPresent(tags, ErrorSightSemanticConventions.CollectionName, d.CollectionName);

        // Metadata: types and counts, never the values themselves.
        if (level >= Options.DataCapture.Metadata)
        {
            if (d.NullExpression is not null)
            {
                AddIfPresent(tags, ErrorSightSemanticConventions.ObjectType, d.NullType);
                tags[ErrorSightSemanticConventions.ObjectNull] = true;
                tags[ErrorSightSemanticConventions.Member] = CapturedValuesResolver.SplitPath(d.NullExpression)[^1];
            }
            AddIfPresent(tags, ErrorSightSemanticConventions.CollectionType, d.CollectionType);
            if (d.CollectionLength is { } count)
            {
                tags[ErrorSightSemanticConventions.CollectionCount] = count;
                tags[ErrorSightSemanticConventions.CollectionEmpty] = count == 0;
            }
        }

        // Values: only when the application opted in (already masked).
        if (level == Options.DataCapture.Values)
        {
            AddIfPresent(tags, ErrorSightSemanticConventions.ExceptionMessage, d.Message);
            AddIfPresent(tags, ErrorSightSemanticConventions.MissingKey, d.MissingKey);
            if (d.RequestedIndex is { } index) tags[ErrorSightSemanticConventions.Index] = index;
            if (d.Values.Count > 0)
            {
                try { tags[ErrorSightSemanticConventions.Values] = JsonSerializer.Serialize(d.Values, JsonOptions); }
                catch { /* unserialisable value: skip */ }
            }
        }

        activity.AddEvent(new ActivityEvent(ErrorSightSemanticConventions.DiagnosedEvent, tags: tags));
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
