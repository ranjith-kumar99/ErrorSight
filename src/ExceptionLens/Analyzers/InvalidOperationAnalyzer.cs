using System.Text.RegularExpressions;
using ExceptionLens.Core;

namespace ExceptionLens.Analyzers;

/// <summary>Enriches InvalidOperationException with LINQ-operation context.</summary>
public sealed class InvalidOperationAnalyzer : IExceptionAnalyzer
{
    private static readonly Regex LinqOperationPattern = new(
        @"\b(?<op>First|FirstOrDefault|Single|SingleOrDefault|Last|LastOrDefault|Min|Max|Average|Sum|Aggregate)\(\)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public bool CanAnalyze(Exception exception) => exception is InvalidOperationException;

    public void Enrich(ExceptionDiagnostics diagnostics, Exception exception)
    {
        var msg = exception.Message;

        // "Sequence contains no elements"
        if (msg.Contains("no elements", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.PossibleCause = "A LINQ operation was called on an empty sequence.";
            diagnostics.Suggestion = "Use FirstOrDefault() / SingleOrDefault() which return null instead of throwing, or guard with Any().";
        }
        // "Sequence contains more than one element"
        else if (msg.Contains("more than one element", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.PossibleCause = "Single() or SingleOrDefault() matched more than one element.";
            diagnostics.Suggestion = "Use First() / FirstOrDefault() if multiple matches are acceptable, or add a more specific filter.";
        }
        // "An item with the same key has already been added"
        else if (msg.Contains("same key", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.PossibleCause = "A duplicate key was inserted into a dictionary or unique collection.";
            diagnostics.Suggestion = "Use TryAdd() or check ContainsKey() before inserting.";
        }
        // "Object is not in a valid state" / "Collection was modified"
        else if (msg.Contains("modified", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.PossibleCause = "A collection was modified while it was being enumerated.";
            diagnostics.Suggestion = "Enumerate a snapshot (e.g. .ToList()) before modifying, or use a concurrent collection.";
        }

        // Try to identify the LINQ operation from the source line
        var failingLine = StackTraceParser.ReadFailingLine(exception);
        if (failingLine is not null)
        {
            diagnostics.FailingExpression = failingLine;
            var match = LinqOperationPattern.Match(failingLine);
            if (match.Success)
            {
                diagnostics.OperationName = $".{match.Groups["op"].Value}()";
                diagnostics.CollectionName = ExtractCollectionName(failingLine, match.Groups["op"].Value);
            }
        }

        if (diagnostics.OperationName is not null && diagnostics.CollectionName is not null)
        {
            diagnostics.PossibleCause ??=
                $"{diagnostics.OperationName} on '{diagnostics.CollectionName}' produced an invalid result.";
        }
    }

    private static string? ExtractCollectionName(string line, string operation)
    {
        // orders.First() → orders
        var m = Regex.Match(line, $@"(?<name>\w+)\.{operation}\(");
        return m.Success ? m.Groups["name"].Value : null;
    }
}
