using ExceptionLens.Core;

namespace ExceptionLens.Analyzers;

/// <summary>Unwraps AggregateException to surface the first meaningful inner exception.</summary>
public sealed class AggregateExceptionAnalyzer : IExceptionAnalyzer
{
    public bool CanAnalyze(Exception exception) => exception is AggregateException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        if (exception is not AggregateException ae) return;

        var flattened = ae.Flatten();
        diagnostics.AdditionalData["innerExceptionCount"] = flattened.InnerExceptions.Count;

        if (flattened.InnerExceptions.Count > 0)
        {
            var first = flattened.InnerExceptions[0];
            diagnostics.PossibleCause =
                $"The aggregate contains {flattened.InnerExceptions.Count} inner exception(s). " +
                $"First: {first.GetType().Name}: {first.Message}";
            diagnostics.Suggestion = "Inspect InnerExceptions for each individual failure.";
        }
    }
}
