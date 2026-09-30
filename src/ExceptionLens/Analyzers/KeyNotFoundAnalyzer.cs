using System.Text.RegularExpressions;
using ExceptionLens.Core;

namespace ExceptionLens.Analyzers;

/// <summary>Enriches KeyNotFoundException with the missing key parsed from the message.</summary>
public sealed class KeyNotFoundAnalyzer : IExceptionAnalyzer
{
    // "The given key 'abc123' was not present in the dictionary."
    private static readonly Regex KeyPattern = new(
        @"[Tt]he given key '?(?<key>[^']+)'? was not",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public bool CanAnalyze(Exception exception) => exception is KeyNotFoundException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        var match = KeyPattern.Match(exception.Message);
        if (match.Success)
        {
            var key = match.Groups["key"].Value.Trim();
            diagnostics.MissingKey = key;
            diagnostics.Values["missingKey"] = key;
        }

        // Try to get the dictionary/collection name from the source line
        var failingLine = StackTraceParser.ReadFailingLine(exception);
        if (failingLine is not null)
        {
            diagnostics.FailingExpression = failingLine;
            diagnostics.CollectionName = ExtractCollectionName(failingLine);
        }

        var keyDisplay = diagnostics.MissingKey is not null ? $"'{diagnostics.MissingKey}'" : "the requested key";
        var collDisplay = diagnostics.CollectionName ?? "the dictionary";

        diagnostics.PossibleCause = $"{keyDisplay} does not exist in {collDisplay}.";
        diagnostics.Suggestion =
            $"Check whether {keyDisplay} was inserted before access, or use TryGetValue() / GetValueOrDefault().";
    }

    private static string? ExtractCollectionName(string line)
    {
        // Matches:   _cache["key"]   dict["key"]   map[variable]
        var m = Regex.Match(line, @"(?<name>\w+)\[");
        return m.Success ? m.Groups["name"].Value : null;
    }
}
