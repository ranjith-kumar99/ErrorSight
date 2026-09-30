using System.Text.RegularExpressions;
using ExceptionLens.Core;

namespace ExceptionLens.Analyzers;

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
            if (aoore.ActualValue is int idx)
            {
                diagnostics.RequestedIndex = idx;
                diagnostics.Values["requestedIndex"] = idx;
            }
        }

        // Read source line for collection name and index expression
        var failingLine = StackTraceParser.ReadFailingLine(exception);
        if (failingLine is not null)
        {
            diagnostics.FailingExpression = failingLine;
            diagnostics.CollectionName = ExtractCollectionName(failingLine);
        }

        // Pull developer-attached length
        if (exception.Data.Contains("CollectionLength") &&
            exception.Data["CollectionLength"] is int len)
        {
            diagnostics.CollectionLength = len;
            diagnostics.ValidIndexRange = $"0–{len - 1}";
            diagnostics.Values["collectionLength"] = len;
        }

        if (diagnostics.CollectionLength.HasValue)
        {
            diagnostics.PossibleCause = diagnostics.RequestedIndex.HasValue
                ? $"Index {diagnostics.RequestedIndex} is outside the collection's length of {diagnostics.CollectionLength}."
                : $"The requested index is outside the collection's length of {diagnostics.CollectionLength}.";
        }
        else
        {
            var collDisplay = diagnostics.CollectionName ?? "the collection";
            diagnostics.PossibleCause = $"The index exceeds the length of {collDisplay}.";
        }

        diagnostics.Suggestion = "Verify the index is within [0, length - 1], or use ElementAtOrDefault() for safe access.";
    }

    private static string? ExtractCollectionName(string line)
    {
        var m = Regex.Match(line, @"(?<name>\w+)\[");
        return m.Success ? m.Groups["name"].Value : null;
    }
}
