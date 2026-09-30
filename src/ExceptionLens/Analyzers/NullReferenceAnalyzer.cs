using ExceptionLens.Core;

namespace ExceptionLens.Analyzers;

/// <summary>
/// Enriches NullReferenceException with the failing expression (from source),
/// call location, and any values the developer attached via Capture().
/// </summary>
public sealed class NullReferenceAnalyzer : IExceptionAnalyzer
{
    public bool CanAnalyze(Exception exception) => exception is NullReferenceException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        // Try to read the failing source line from the PDB
        var failingLine = StackTraceParser.ReadFailingLine(exception);
        if (failingLine is not null)
        {
            diagnostics.FailingExpression = failingLine;

            // Identify the null sub-expression from developer-attached context
            if (diagnostics.NullExpression is null)
                diagnostics.NullExpression = TryInferNullExpression(failingLine, diagnostics.Values);
        }

        // Pull developer-attached null expression
        if (exception.Data.Contains(ExceptionDataKeys.NullExpression))
            diagnostics.NullExpression = exception.Data[ExceptionDataKeys.NullExpression] as string;

        // Compose the "possible cause" narrative
        if (diagnostics.NullExpression is not null)
        {
            diagnostics.PossibleCause = $"{diagnostics.NullExpression} is null.";
            diagnostics.Suggestion = $"Check whether {diagnostics.NullExpression} is initialised before accessing its members.";
        }
        else if (diagnostics.FailingExpression is not null)
        {
            diagnostics.PossibleCause = $"One of the members in '{diagnostics.FailingExpression}' is null.";
            diagnostics.Suggestion = "Use null-conditional operators (?.) or guard clauses to validate the chain.";
        }
        else
        {
            diagnostics.PossibleCause = "An object reference is null.";
            diagnostics.Suggestion = "Use exception.Capture(new { variable }) to attach the runtime values.";
        }
    }

    /// <summary>
    /// Cross-references source expression with captured values to find the first null member.
    /// e.g. expression = "customer.Address.City.Name", values has customer.Address = null
    /// → returns "customer.Address"
    /// </summary>
    private static string? TryInferNullExpression(string expression, Dictionary<string, object?> values)
    {
        if (values.Count == 0) return null;

        // Walk the dot-chain left-to-right and return the first key whose value is null
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
