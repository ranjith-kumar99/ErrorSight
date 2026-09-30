using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ErrorSight.Core;

internal static class StackTraceParser
{
    private static readonly Regex FramePattern = new(
        @"at (?<type>.+)\.(?<method>[^\.(]+(?:\(.*?\))?)\s+in (?<file>.+):line (?<line>\d+)",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private static readonly Regex FrameNoFilePattern = new(
        @"at (?<full>.+\))",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture);

    private static readonly Assembly Self = typeof(StackTraceParser).Assembly;

    /// <summary>Frames that belong to ErrorSight itself are never reported.</summary>
    private static bool IsOwnFrame(MethodBase? method) => method?.DeclaringType?.Assembly == Self;

    /// <summary>Stack frames of <paramref name="exception"/>; file info requires PDB lookups, so ask only when needed.</summary>
    public static StackFrame[] GetFrames(Exception exception, bool needFileInfo)
    {
        try { return new StackTrace(exception, needFileInfo).GetFrames(); }
        catch { return Array.Empty<StackFrame>(); }
    }

    public static List<CallFrame> ParseCallChain(Exception exception, bool includeFileInfo = true) =>
        ParseCallChain(exception, GetFrames(exception, includeFileInfo), includeFileInfo);

    public static List<CallFrame> ParseCallChain(Exception exception, StackFrame[] stackFrames, bool includeFileInfo)
    {
        var frames = new List<CallFrame>();

        try
        {
            foreach (var frame in stackFrames)
            {
                var method = frame.GetMethod();
                if (method is null) continue;

                if (IsOwnFrame(method)) continue;

                var declaringType = UserType(method.DeclaringType);
                var typeName = declaringType?.FullName ?? "<unknown>";
                var methodName = BuildMethodSignature(method);
                var file = includeFileInfo ? frame.GetFileName() : null;
                var line = includeFileInfo ? frame.GetFileLineNumber() : 0;

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

    /// <summary>
    /// The first frame with source information (your code), falling back to the very first frame
    /// (e.g. a framework method that threw on your behalf).
    /// </summary>
    public static (string? file, int? line, string? method, string? typeName) GetThrowSite(Exception exception) =>
        GetThrowSite(GetFrames(exception, needFileInfo: true));

    public static (string? file, int? line, string? method, string? typeName) GetThrowSite(StackFrame[] frames)
    {
        try
        {
            (string?, int?, string?, string?)? fallback = null;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                if (method is null || IsOwnFrame(method)) continue;

                var file = frame.GetFileName();
                var line = frame.GetFileLineNumber();
                var site = (
                    file is not null ? Path.GetFileName(file) : null,
                    line > 0 ? line : (int?)null,
                    BuildMethodSignature(method),
                    UserTypeName(method.DeclaringType));

                if (file is not null) return site;
                fallback ??= site;
            }

            if (fallback is { } f) return f;
        }
        catch { /* ignore */ }

        return (null, null, null, null);
    }

    /// <summary><c>OrderService</c> for both <c>OrderService</c> and its async state machine <c>&lt;GetAsync&gt;d__3</c>.</summary>
    private static string UserTypeName(Type? type) => UserType(type)?.Name ?? "<unknown>";

    private static Type? UserType(Type? type)
    {
        while (type is not null && type.Name.StartsWith('<') && type.DeclaringType is not null)
            type = type.DeclaringType;
        return type;
    }

    public static string[]? ReadSourceContext(Exception exception, int contextLines = 2)
    {
        try
        {
            var st = new StackTrace(exception, fNeedFileInfo: true);
            foreach (var frame in st.GetFrames())
            {
                if (IsOwnFrame(frame.GetMethod())) continue;

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
                if (IsOwnFrame(frame.GetMethod())) continue;

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
        // async/iterator state machine: <GetCityAsync>d__3.MoveNext() → GetCityAsync
        var owner = method.DeclaringType?.Name;
        if (method.Name == "MoveNext" && owner is not null && owner.StartsWith('<') && owner.IndexOf(">d__", StringComparison.Ordinal) > 0)
            return owner[1..owner.IndexOf('>')];

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
