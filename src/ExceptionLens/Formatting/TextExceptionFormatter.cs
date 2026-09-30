using System.Text;
using ExceptionLens.Core;

namespace ExceptionLens.Formatting;

/// <summary>
/// Produces the human-readable ExceptionLens banner, e.g.:
///
///   ExceptionLens
///   ────────────────────────────────────────
///
///   NullReferenceException
///
///   ❌ NULL VALUE:
///   customer.Address
///   ...
/// </summary>
public sealed class TextExceptionFormatter : IExceptionFormatter
{
    private const string Divider = "────────────────────────────────────────";
    private const string Indent = "  ";

    public string Format(ExceptionDiagnostics d)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("ExceptionLens");
        sb.AppendLine(Divider);
        sb.AppendLine();
        sb.AppendLine(d.ExceptionType);
        sb.AppendLine();

        // ── Null expression ──────────────────────────────────────────────
        if (d.NullExpression is not null)
        {
            sb.AppendLine("❌ NULL VALUE:");
            sb.AppendLine(Indent + d.NullExpression);
            sb.AppendLine();
        }

        // ── Failing expression / source line ─────────────────────────────
        if (d.FailingExpression is not null)
        {
            sb.AppendLine("Expression:");
            sb.AppendLine(Indent + d.FailingExpression);
            sb.AppendLine();
        }

        // ── Location ──────────────────────────────────────────────────────
        if (d.SourceFileShort is not null || d.Method is not null)
        {
            sb.AppendLine("Location:");
            if (d.SourceFileShort is not null && d.Line.HasValue)
                sb.AppendLine(Indent + $"{d.SourceFileShort}:{d.Line}");
            else if (d.SourceFileShort is not null)
                sb.AppendLine(Indent + d.SourceFileShort);
            if (d.TypeName is not null && d.Method is not null)
                sb.AppendLine(Indent + $"{d.TypeName}.{d.Method}");
            sb.AppendLine();
        }

        // ── Parameter name ────────────────────────────────────────────────
        if (d.ParameterName is not null)
        {
            sb.AppendLine("Parameter:");
            sb.AppendLine(Indent + d.ParameterName);
            sb.AppendLine();
        }

        // ── Missing key ───────────────────────────────────────────────────
        if (d.MissingKey is not null)
        {
            sb.AppendLine("Missing key:");
            sb.AppendLine(Indent + d.MissingKey);
            if (d.CollectionName is not null)
            {
                sb.AppendLine();
                sb.AppendLine("Dictionary:");
                sb.AppendLine(Indent + d.CollectionName);
            }
            sb.AppendLine();
        }

        // ── Index info ────────────────────────────────────────────────────
        if (d.RequestedIndex.HasValue || d.CollectionLength.HasValue)
        {
            if (d.CollectionName is not null)
            {
                sb.AppendLine("Collection:");
                sb.AppendLine(Indent + d.CollectionName);
            }
            if (d.RequestedIndex.HasValue)
            {
                sb.AppendLine("Requested index:");
                sb.AppendLine(Indent + d.RequestedIndex);
            }
            if (d.CollectionLength.HasValue)
            {
                sb.AppendLine("Collection length:");
                sb.AppendLine(Indent + d.CollectionLength);
            }
            if (d.ValidIndexRange is not null)
            {
                sb.AppendLine("Valid range:");
                sb.AppendLine(Indent + d.ValidIndexRange);
            }
            sb.AppendLine();
        }

        // ── LINQ operation ────────────────────────────────────────────────
        if (d.OperationName is not null)
        {
            sb.AppendLine("Operation:");
            sb.AppendLine(Indent + d.OperationName);
            if (d.CollectionName is not null)
            {
                sb.AppendLine("Collection:");
                sb.AppendLine(Indent + d.CollectionName);
            }
            sb.AppendLine();
        }

        // ── Runtime values ────────────────────────────────────────────────
        if (d.Values.Count > 0)
        {
            sb.AppendLine("Runtime values:");
            var maxKeyLen = d.Values.Keys.Max(k => k.Length);
            foreach (var (key, val) in d.Values)
            {
                var valStr = FormatValue(val);
                sb.AppendLine(Indent + key.PadRight(maxKeyLen) + "  =  " + valStr);
            }
            sb.AppendLine();
        }

        // ── Call chain ────────────────────────────────────────────────────
        if (d.CallChain.Count > 0)
        {
            sb.AppendLine("Call path:");
            for (int i = 0; i < Math.Min(d.CallChain.Count, 6); i++)
            {
                var frame = d.CallChain[i];
                if (i > 0) sb.AppendLine(Indent + "↓");
                sb.AppendLine(Indent + $"{frame.TypeName}.{frame.MethodName}");
            }
            if (d.CallChain.Count > 6)
                sb.AppendLine(Indent + $"… ({d.CallChain.Count - 6} more frames)");
            sb.AppendLine();
        }

        // ── Possible cause ────────────────────────────────────────────────
        if (d.PossibleCause is not null)
        {
            sb.AppendLine("Possible cause:");
            sb.AppendLine(Indent + d.PossibleCause);
            sb.AppendLine();
        }

        // ── Suggestion ────────────────────────────────────────────────────
        if (d.Suggestion is not null)
        {
            sb.AppendLine("Suggestion:");
            sb.AppendLine(Indent + d.Suggestion);
            sb.AppendLine();
        }

        // ── Source context ────────────────────────────────────────────────
        if (d.SourceContext is { Length: > 0 })
        {
            sb.AppendLine("Source context:");
            foreach (var line in d.SourceContext)
                sb.AppendLine(Indent + line);
            sb.AppendLine();
        }

        // ── Inner exception ───────────────────────────────────────────────
        if (d.InnerException is not null)
        {
            sb.AppendLine("Inner exception:");
            sb.AppendLine(Indent + $"{d.InnerException.ExceptionType}: {d.InnerException.Message}");
            sb.AppendLine();
        }

        sb.AppendLine(Divider);

        return sb.ToString();
    }

    private static string FormatValue(object? val) =>
        val switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            _ => val.ToString() ?? "null"
        };
}
