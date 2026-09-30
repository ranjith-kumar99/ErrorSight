using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ExceptionLens.Core;

internal static class StackTraceParser
{
    private static readonly Regex FramePattern = new(
        @"at (?<type>.+)\.(?<method>[^\.(]+(?:\(.*?\))?)\s+in (?<file>.+):line (?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private static readonly Regex FrameNoFilePattern = new(
        @"at (?<full>.+\))",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    public static List<CallFrame> ParseCallChain(Exception exception)
    {
        var frames = new List<CallFrame>();

        try
        {
            var st = new StackTrace(exception, fNeedFileInfo: true);
            foreach (var frame in st.GetFrames())
            {
                var method = frame.GetMethod();
                if (method is null) continue;

                // Skip ExceptionLens internal frames
                var declaringType = method.DeclaringType;
                if (declaringType is not null &&
                    declaringType.Namespace?.StartsWith("ExceptionLens", StringComparison.Ordinal) == true)
                    continue;

                var typeName = declaringType?.FullName ?? "<unknown>";
                var methodName = BuildMethodSignature(method);
                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();

                frames.Add(new CallFrame
                {
                    TypeName = SimplifyTypeName(typeName),
                    MethodName = methodName,
                    SourceFile = file is not null ? Path.GetFileName(file) : null,
                    Line = line > 0 ? line : null
                });
            }
        }
        catch
        {
            // Fall back to text parsing
            frames.AddRange(ParseFromText(exception.StackTrace ?? string.Empty));
        }

        return frames;
    }

    public static (string? file, int? line, string? method, string? typeName) GetThrowSite(Exception exception)
    {
        try
        {
            var st = new StackTrace(exception, fNeedFileInfo: true);
            foreach (var frame in st.GetFrames())
            {
                var method = frame.GetMethod();
                if (method is null) continue;

                var declaringType = method.DeclaringType;
                if (declaringType?.Namespace?.StartsWith("ExceptionLens", StringComparison.Ordinal) == true)
                    continue;

                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                var methodName = BuildMethodSignature(method);
                var typeName = declaringType?.Name ?? "<unknown>";

                return (
                    file is not null ? Path.GetFileName(file) : null,
                    line > 0 ? line : null,
                    methodName,
                    typeName
                );
            }
        }
        catch { /* ignore */ }

        return (null, null, null, null);
    }

    public static string[]? ReadSourceContext(Exception exception, int contextLines = 2)
    {
        try
        {
            var st = new StackTrace(exception, fNeedFileInfo: true);
            foreach (var frame in st.GetFrames())
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType?.Namespace?.StartsWith("ExceptionLens", StringComparison.Ordinal) == true)
                    continue;

                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                if (file is null || line <= 0 || !File.Exists(file)) continue;

                var allLines = File.ReadAllLines(file);
                int start = Math.Max(0, line - 1 - contextLines);
                int end = Math.Min(allLines.Length - 1, line - 1 + contextLines);
                return allLines[start..(end + 1)];
            }
        }
        catch { /* source not available */ }

        return null;
    }

    public static string? ReadFailingLine(Exception exception)
    {
        try
        {
            var st = new StackTrace(exception, fNeedFileInfo: true);
            foreach (var frame in st.GetFrames())
            {
                var method = frame.GetMethod();
                if (method?.DeclaringType?.Namespace?.StartsWith("ExceptionLens", StringComparison.Ordinal) == true)
                    continue;

                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                if (file is null || line <= 0 || !File.Exists(file)) continue;

                var allLines = File.ReadAllLines(file);
                if (line - 1 < allLines.Length)
                    return allLines[line - 1].Trim();
            }
        }
        catch { /* source not available */ }

        return null;
    }

    private static IEnumerable<CallFrame> ParseFromText(string stackTrace)
    {
        foreach (var rawLine in stackTrace.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var match = FramePattern.Match(line);
            if (match.Success)
            {
                yield return new CallFrame
                {
                    TypeName = match.Groups["type"].Value.Trim(),
                    MethodName = match.Groups["method"].Value.Trim(),
                    SourceFile = Path.GetFileName(match.Groups["file"].Value.Trim()),
                    Line = int.TryParse(match.Groups["line"].Value, out var ln) ? ln : null
                };
                continue;
            }

            var m2 = FrameNoFilePattern.Match(line);
            if (!m2.Success) continue;
            var full = m2.Groups["full"].Value.Trim();
            var paren = full.IndexOf('(');
            if (paren < 0) continue;
            var prefix = full[..paren];
            var lastDot = prefix.LastIndexOf('.');
            if (lastDot < 0) continue;

            yield return new CallFrame
            {
                TypeName = prefix[..lastDot],
                MethodName = prefix[(lastDot + 1)..] + full[paren..]
            };
        }
    }

    private static string BuildMethodSignature(MethodBase method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0) return method.Name;
        var paramStr = string.Join(", ", parameters.Select(p => $"{SimplifyTypeName(p.ParameterType.Name)} {p.Name}"));
        return $"{method.Name}({paramStr})";
    }

    private static string SimplifyTypeName(string typeName)
    {
        return typeName
            .Replace("`1", "")
            .Replace("`2", "")
            .Replace("+", ".");
    }
}
