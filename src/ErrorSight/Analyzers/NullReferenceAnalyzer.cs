using ErrorSight.Core;

namespace ErrorSight.Analyzers;

/// <summary>
/// Explains a NullReferenceException. The null expression itself is resolved automatically from the
/// build-time instrumentation (see <c>CapturedValuesResolver</c>); this analyzer adds the source line
/// when available and composes the cause / suggestion.
/// </summary>
public sealed class NullReferenceAnalyzer : IExceptionAnalyzer
{
    public bool CanAnalyze(Exception exception) => exception is NullReferenceException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        if (diagnostics.FailingExpression is null && diagnostics.SourceFile is not null)
            diagnostics.FailingExpression = StackTraceParser.ReadFailingLine(exception);

        if (diagnostics.NullExpression is null && diagnostics.FailingExpression is not null)
            diagnostics.NullExpression = TryInferNullExpression(diagnostics.FailingExpression, diagnostics.Values);

        if (diagnostics.NullExpression is not null)
        {
            diagnostics.PossibleCause = $"{diagnostics.NullExpression} is null.";
            diagnostics.Suggestion = $"Check whether {diagnostics.NullExpression} is initialised before accessing its members.";
        }
        else if (diagnostics.NullCandidates is { Length: > 0 } candidates)
        {
            diagnostics.PossibleCause = $"One of these is null: {string.Join(", ", candidates)}.";
            diagnostics.Suggestion = "Use null-conditional operators (?.) or guard clauses to validate the chain.";
        }
        else if (diagnostics.FailingExpression is not null)
        {
            diagnostics.PossibleCause = $"One of the members in '{diagnostics.FailingExpression}' is null.";
            diagnostics.Suggestion = "Use null-conditional operators (?.) or guard clauses to validate the chain.";
        }
        else
        {
            diagnostics.PossibleCause = "An object reference is null.";
            diagnostics.Suggestion =
                "The failing method was not instrumented (build-time weaving disabled, [ErrorSightIgnore], " +
                "or a framework method), so the null expression could not be determined.";
        }
    }

    /// <summary>
    /// Cross-references a source expression with captured values to find the first null member.
    /// e.g. expression = "customer.Address.City.Name", values has customer.Address = null
    /// → returns "customer.Address"
    /// </summary>
    private static string? TryInferNullExpression(string expression, Dictionary<string, object?> values)
    {
        if (values.Count == 0) return null;

        var parts = expression.Split('.');
        for (int i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('.', parts[..i]);
            if (values.TryGetValue(path, out var val) && val is null)
                return path;
        }

        return null;
    }
}
