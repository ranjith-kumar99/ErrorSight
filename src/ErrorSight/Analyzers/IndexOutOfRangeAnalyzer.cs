using System.Text.RegularExpressions;
using ErrorSight.Core;
using ErrorSight.Options;

namespace ErrorSight.Analyzers;

/// <summary>Enriches IndexOutOfRangeException and ArgumentOutOfRangeException.</summary>
public sealed class IndexOutOfRangeAnalyzer : IExceptionAnalyzer
{
    // "Index was outside the bounds of the array."
    // "Specified argument was out of the range of valid values. (Parameter 'index')"
    private static readonly Regex IndexValuePattern = new(
        @"[Ii]ndex\s+(?:was\s+)?(?:out|outside|'(?<idx>-?\d+)')",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public bool CanAnalyze(Exception exception) =>
        exception is IndexOutOfRangeException or ArgumentOutOfRangeException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        // Try to read index from ArgumentOutOfRangeException
        if (exception is ArgumentOutOfRangeException aoore)
        {
            diagnostics.ParameterName = aoore.ParamName;
            if (aoore.ActualValue is int idx && diagnostics.DataCapture == DataCapture.Values)
                diagnostics.RequestedIndex ??= idx;
        }

        // Collection name, index and length come from the captured values when the method was
        // instrumented; the source line is a fallback for the collection name.
        var failingLine = diagnostics.FailingSourceLine;
        if (failingLine is not null)
        {
            diagnostics.FailingExpression ??= failingLine;
            diagnostics.CollectionName ??= ExtractCollectionName(failingLine);
        }

        var collDisplay = diagnostics.CollectionName ?? "the collection";
        if (diagnostics.CollectionLength.HasValue)
        {
            diagnostics.PossibleCause = diagnostics.RequestedIndex.HasValue
                ? $"Index {diagnostics.RequestedIndex} is outside the collection's length of {diagnostics.CollectionLength}."
                : $"The requested index is outside {collDisplay}, which has {diagnostics.CollectionLength} element(s).";
        }
        else
        {
            diagnostics.PossibleCause = $"The index is outside the bounds of {collDisplay}.";
        }

        diagnostics.Suggestion = "Verify the index is within [0, length - 1], or use ElementAtOrDefault() for safe access.";
    }

    private static string? ExtractCollectionName(string line)
    {
        var m = Regex.Match(line, @"(?<name>\w+)\[");
        return m.Success ? m.Groups["name"].Value : null;
    }
}
