using System.Diagnostics;
using System.Globalization;
using ErrorSight.Options;
using ErrorSight.Runtime;
using ErrorSight.Runtime.Metadata;

namespace ErrorSight.Core;

/// <summary>
/// Turns the frames captured by the build-time instrumentation into diagnostics: for NullReferenceException the
/// exact expression that was null and where it came from, the collection behind index and key errors, and, only
/// at the levels that allow it, metadata and values.
///
/// Expressions are always evaluated against everything that was captured (at <see cref="DataCapture.None"/> that is
/// only structure: types and null-ness); what is reported never exceeds the configured level.
/// </summary>
internal static class CapturedValuesResolver
{
    private const int MaxFlattenedValues = 30;

    public static void Apply(ExceptionDiagnostics d, StackFrame[] stack, IReadOnlyList<CapturedFrame> captured, ErrorSightOptions options)
    {
        // Match each captured frame to its position in the exception's stack trace.
        var located = captured
            .Select((frame, order) => (Frame: frame, Order: order, Index: IndexInStack(frame, stack)))
            .OrderBy(x => x.Index < 0 ? int.MaxValue : x.Index)
            .ThenBy(x => x.Order)
            .ToList();

        foreach (var (frame, _, index) in located)
        {
            var stackFrame = index >= 0 ? stack[index] : null;
            int? ilOffset = stackFrame?.GetILOffset() is { } il && il >= 0 ? il : null;
            var file = options.CaptureSourceLocation ? stackFrame?.GetFileName() : null;
            var line = options.CaptureSourceLocation ? stackFrame?.GetFileLineNumber() ?? 0 : 0;

            d.Frames.Add(new FrameDiagnostics
            {
                Method = frame.Method,
                SourceFile = file is null ? null : Path.GetFileName(file),
                Line = line > 0 ? line : null,
                ILOffset = ilOffset,
                Values = frame.ValuesAt(ilOffset).Select(v => v.ViewAt(options.DataCapture)).ToList(),
            });
        }

        if (d.Frames.Count == 0) return;

        var throwSite = d.Frames[0];
        var throwFrame = located[0].Frame;
        var throwLine = stack.Length > 0 && located[0].Index >= 0 ? stack[located[0].Index].GetFileLineNumber() : 0;
        var roots = throwFrame.ValuesAt(throwSite.ILOffset);
        var level = options.DataCapture;

        var candidates = AccessesAt(throwFrame.Metadata, throwSite.ILOffset, throwLine);

        switch (d.FullTypeName)
        {
            // Only when the NRE was raised in instrumented code itself (top of the stack); otherwise the
            // null dereference happened inside a callee we have no map for.
            case "System.NullReferenceException" when located[0].Index == 0:
                if (ResolveNull(d, throwFrame.Metadata, roots, candidates, level) is { } access)
                {
                    ResolveOrigin(d, throwFrame.Metadata, access, options.CaptureSourceLocation);
                    if (options.CaptureSourceLocation) CorrectLine(d, throwFrame.Metadata, access);
                }
                break;
            case "System.IndexOutOfRangeException":
            case "System.ArgumentOutOfRangeException":
                ResolveIndex(d, roots, candidates, level);
                break;
            case "System.Collections.Generic.KeyNotFoundException":
                if (candidates.FirstOrDefault(a => a.Kind == AccessKind.Element) is { } lookup)
                {
                    d.CollectionName ??= lookup.Receiver;
                    DescribeCollection(d, roots, lookup.Receiver, level);
                    AddValue(d, roots, lookup.Receiver, level);
                    if (lookup.Index is { } key) AddValue(d, roots, key, level);
                }
                break;
        }

        // Nothing specific to show: the throw site's variables (values only).
        if (level == DataCapture.Values && d.Values.Count == 0)
        {
            foreach (var value in throwSite.Values.Take(MaxFlattenedValues))
                d.Values[value.Name] = Display(value);
        }
    }

    /// <summary>Adds the value of <paramref name="expression"/> to <see cref="ExceptionDiagnostics.Values"/> (values level only).</summary>
    private static void AddValue(ExceptionDiagnostics d, IReadOnlyList<CapturedValue> roots, string expression, DataCapture level, bool isNull = false)
    {
        if (level != DataCapture.Values || d.Values.ContainsKey(expression) || IsLiteral(expression)) return;
        if (Evaluate(roots, expression) is { } value) d.Values[expression] = Display(value);
        else if (isNull) d.Values[expression] = null; // the exception proves it was null
    }

    /// <summary>Collection type and count (metadata and values levels).</summary>
    private static void DescribeCollection(ExceptionDiagnostics d, IReadOnlyList<CapturedValue> roots, string expression, DataCapture level)
    {
        if (level == DataCapture.None) return;
        var collection = Evaluate(roots, expression) ?? Evaluate(roots, StripCountPreservingCall(expression));
        if (collection is null) return;
        d.CollectionType ??= Evaluate(roots, expression)?.Type;
        d.CollectionLength ??= collection.Count;
    }

    private static bool IsLiteral(string expression) =>
        expression.Length > 0 && (char.IsDigit(expression[0]) || expression[0] is '"' or '-');

    // ── NullReferenceException ───────────────────────────────────────────────

    /// <summary>Sets NullExpression (or NullCandidates) and returns the access that faulted, when known.</summary>
    private static WovenAccess? ResolveNull(ExceptionDiagnostics d, WovenMethod method, IReadOnlyList<CapturedValue> roots, List<WovenAccess> candidates, DataCapture level)
    {
        if (candidates.Count == 0) return null;

        // Straight-line code executes in IL order, so the first receiver that was null is the one that threw.
        WovenAccess? culprit = null;
        foreach (var access in candidates)
        {
            if (Evaluate(roots, access.Receiver) is { IsNull: true })
            {
                culprit = access;
                break;
            }
        }

        if (culprit is null)
        {
            // Receivers we could not evaluate (method-call results, un-captured members, depth limit).
            var unknown = candidates.Where(a => Evaluate(roots, a.Receiver) is null).ToList();
            var distinct = unknown.Select(a => a.Receiver).Distinct().ToArray();
            if (distinct.Length == 1) culprit = unknown[0];
            else if (distinct.Length > 1) d.NullCandidates = distinct;
        }

        var nullExpression = culprit?.Receiver;
        if (nullExpression is not null)
        {
            d.NullExpression = nullExpression;
            if (level != DataCapture.None) d.NullType = Evaluate(roots, nullExpression)?.Type;

            // order, order.Customer, order.Customer.Address: only the chain that failed, not whole objects.
            var segments = SplitPath(nullExpression);
            for (var i = 1; i <= segments.Count; i++)
                AddValue(d, roots, string.Join('.', segments.Take(i)), level, isNull: i == segments.Count);
        }

        // The longest chain through the null value within the failing statement,
        // e.g. order.Customer.Address.City.Name
        var statement = culprit is null ? null : StatementRange(method, culprit.Offset);
        var chain = candidates
            .Where(a => statement is not { } range || (a.Offset >= range.Start && a.Offset < range.End))
            .Select(a => a.Result)
            .Where(r => nullExpression is null || r.StartsWith(nullExpression, StringComparison.Ordinal))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
        d.FailingExpression ??= chain;
        return culprit;
    }

    /// <summary>The IL range of the source statement (visible sequence point) containing <paramref name="offset"/>.</summary>
    private static (int Start, int End)? StatementRange(WovenMethod method, int offset)
    {
        int? start = null;
        var end = int.MaxValue;
        foreach (var point in method.SequencePoints)
        {
            if (point.Line < 0) continue;
            if (point.Offset <= offset) start = point.Offset;
            else { end = point.Offset; break; }
        }
        return start is { } s ? (s, end) : null;
    }

    /// <summary>
    /// When the null expression starts at a local variable, the last assignment to it before the failing access
    /// says where the null came from: <c>address = customer.Address</c> on line 128.
    /// </summary>
    private static void ResolveOrigin(ExceptionDiagnostics d, WovenMethod method, WovenAccess culprit, bool withLine)
    {
        var variable = SplitPath(d.NullExpression!)[0];
        WovenAssignment? origin = null;
        foreach (var assignment in method.Assignments)
        {
            if (assignment.Variable != variable || assignment.Offset > culprit.Offset) continue;
            if (origin is null || assignment.Offset > origin.Offset) origin = assignment;
        }
        if (origin is null) return;

        d.NullOrigin = new NullOrigin
        {
            Variable = variable,
            Expression = origin.Value,
            Line = withLine ? LineAt(method, origin.Offset) : null,
        };
    }

    private static int? LineAt(WovenMethod method, int offset)
    {
        int? line = null;
        foreach (var point in method.SequencePoints)
        {
            if (point.Offset > offset) break;
            if (point.Line >= 0) line = point.Line;
        }
        return line;
    }

    /// <summary>
    /// Optimized code can report the line of an earlier statement; the faulting access tells the real one.
    /// </summary>
    private static void CorrectLine(ExceptionDiagnostics d, WovenMethod method, WovenAccess access)
    {
        var line = -1;
        foreach (var point in method.SequencePoints)
        {
            if (point.Offset > access.Offset) break;
            if (point.Line >= 0) line = point.Line;
        }
        if (line <= 0 || d.Frames.Count == 0) return;

        var throwSite = d.Frames[0];
        if (throwSite.Line == line) return;
        if (d.Line == throwSite.Line) d.Line = line;
        d.Frames[0] = new FrameDiagnostics
        {
            Method = throwSite.Method,
            SourceFile = throwSite.SourceFile,
            Line = line,
            ILOffset = throwSite.ILOffset,
            Values = throwSite.Values,
        };
    }

    // ── Index / key errors ───────────────────────────────────────────────────

    private static void ResolveIndex(ExceptionDiagnostics d, IReadOnlyList<CapturedValue> roots, List<WovenAccess> candidates, DataCapture level)
    {
        var elementAccesses = candidates.Where(a => a.Kind == AccessKind.Element).ToList();
        WovenAccess? culprit = null;
        int? index = null;

        // With values, the access whose index is outside its collection is the one that failed.
        foreach (var access in elementAccesses)
        {
            var collection = Evaluate(roots, access.Receiver) ?? Evaluate(roots, StripCountPreservingCall(access.Receiver));
            var i = ResolveInt(roots, access.Index);
            if (collection?.Count is not { } count || i is not { } requested) continue;
            if (requested >= 0 && requested < count) continue;
            culprit = access;
            index = requested;
            break;
        }

        if (culprit is null && elementAccesses.Count == 1)
        {
            culprit = elementAccesses[0];
            index = ResolveInt(roots, culprit.Index);
        }
        if (culprit is null) return;

        d.CollectionName = culprit.Receiver;
        DescribeCollection(d, roots, culprit.Receiver, level);
        if (d.CollectionLength is { } length) d.ValidIndexRange = length == 0 ? "(empty)" : $"0–{length - 1}";
        if (level == DataCapture.Values) d.RequestedIndex = index;
        AddValue(d, roots, culprit.Receiver, level);
        if (culprit.Index is { } indexExpression) AddValue(d, roots, indexExpression, level);
    }

    /// <summary><c>order.Lines.ToArray(…)</c> has as many elements as <c>order.Lines</c>.</summary>
    private static string StripCountPreservingCall(string expression)
    {
        foreach (var call in new[] { ".ToArray(…)", ".ToList(…)", ".ToImmutableArray(…)", ".AsSpan(…)", ".AsReadOnly(…)" })
            if (expression.EndsWith(call, StringComparison.Ordinal)) return expression[..^call.Length];
        return expression;
    }

    private static int? ResolveInt(IReadOnlyList<CapturedValue> roots, string? expression)
    {
        if (expression is null) return null;
        if (int.TryParse(expression, NumberStyles.Integer, CultureInfo.InvariantCulture, out var literal)) return literal;
        var value = Evaluate(roots, expression);
        return value is { IsNull: false, IsMasked: false } &&
               int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    // ── Sequence-point lookup ────────────────────────────────────────────────

    /// <summary>
    /// The dereferencing instructions of the statement that failed. The IL offset reported for optimized
    /// (Release) code is imprecise, so the whole statement (sequence point) is considered.
    /// </summary>
    internal static List<WovenAccess> AccessesAt(WovenMethod method, int? ilOffset, int line)
    {
        var points = method.SequencePoints;
        if (ilOffset is { } offset && points.Count > 0)
        {
            var at = -1;
            for (var i = 0; i < points.Count && points[i].Offset <= offset; i++)
                if (points[i].Line >= 0) at = i;

            if (at >= 0)
            {
                var start = points[at].Offset;
                var end = int.MaxValue;
                for (var i = at + 1; i < points.Count; i++)
                {
                    if (points[i].Line >= 0 && points[i].Offset > start) { end = points[i].Offset; break; }
                }

                // Optimized code reports the last stack-empty boundary before the fault, which can belong to an
                // earlier statement: extend to the next stack-empty boundary after the reported offset.
                var nextEmpty = method.StackEmptyOffsets.FirstOrDefault(o => o > offset);
                if (nextEmpty > end) end = nextEmpty;

                return method.Accesses.Where(a => a.Offset >= start && a.Offset < end).OrderBy(a => a.Offset).ToList();
            }
        }

        if (line > 0 && points.Count > 0)
        {
            var result = new List<WovenAccess>();
            for (var i = 0; i < points.Count; i++)
            {
                if (points[i].Line != line) continue;
                var start = points[i].Offset;
                var end = i + 1 < points.Count ? points[i + 1].Offset : int.MaxValue;
                result.AddRange(method.Accesses.Where(a => a.Offset >= start && a.Offset < end));
            }
            return result.OrderBy(a => a.Offset).ToList();
        }

        if (ilOffset is { } exact)
            return method.Accesses.Where(a => a.Offset == exact).ToList();

        return new List<WovenAccess>();
    }

    // ── Evaluating expressions against the snapshot ──────────────────────────

    /// <summary>
    /// Evaluates <c>order.Customer.Address</c> against captured values. Returns null when the value is
    /// unknown (not captured, a method call, beyond the depth limit, or an intermediate was already null).
    /// </summary>
    internal static CapturedValue? Evaluate(IReadOnlyList<CapturedValue> roots, string path)
    {
        var segments = SplitPath(path);
        if (segments.Count == 0) return null;

        CapturedValue? current = null;
        for (var s = 0; s < segments.Count; s++)
        {
            var segment = segments[s];
            if (segment.Contains('(')) return null;

            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];

            if (s == 0)
            {
                current = roots.LastOrDefault(r => r.Name == name);
            }
            else
            {
                if (current is null || current.IsNull) return null;
                current = current.Child(name);
            }
            if (current is null) return null;

            while (bracket >= 0)
            {
                var close = segment.IndexOf(']', bracket);
                if (close < 0 || current.IsNull) return null;
                var rawKey = segment[(bracket + 1)..close];

                if (rawKey == "…")
                {
                    // "some element" (loop variable the compiler kept on the stack): evaluate the rest of the
                    // path against every captured element; a null in any of them is what failed.
                    var rest = segment[(close + 1)..] + (s + 1 < segments.Count ? "." + string.Join('.', segments.Skip(s + 1)) : string.Empty);
                    return EvaluateAnyElement(roots, current, rest.TrimStart('.'));
                }

                var key = ResolveKey(roots, rawKey);
                if (key is null) return null;
                current = current.Child($"[{key}]");
                if (current is null) return null;
                bracket = segment.IndexOf('[', close);
            }
        }

        return current;
    }

    private static CapturedValue? EvaluateAnyElement(IReadOnlyList<CapturedValue> roots, CapturedValue collection, string rest)
    {
        if (collection.Children is not { Count: > 0 } elements) return null;

        CapturedValue? firstKnown = null;
        foreach (var element in elements)
        {
            var value = rest.Length == 0 ? element : Evaluate(new[] { element.WithName("_") }, "_." + rest);
            if (value is { IsNull: true }) return value;
            firstKnown ??= value;
        }

        // Every captured element was fine: only conclusive if all elements were captured.
        return collection.Complete ? firstKnown : null;
    }

    private static string? ResolveKey(IReadOnlyList<CapturedValue> roots, string expression)
    {
        if (expression.Length == 0 || expression == "?") return null;
        if (char.IsDigit(expression[0]) || expression[0] == '"' || expression[0] == '-') return expression;
        var value = Evaluate(roots, expression);
        return value is { IsNull: false, IsMasked: false, Children: null, Value: { } text } ? text : null;
    }

    /// <summary>Splits on top-level dots: <c>a.b[x.y].c(…)</c> → a, b[x.y], c(…).</summary>
    internal static List<string> SplitPath(string path)
    {
        var segments = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < path.Length; i++)
        {
            switch (path[i])
            {
                case '[' or '(': depth++; break;
                case ']' or ')': depth--; break;
                case '.' when depth == 0:
                    segments.Add(path[start..i]);
                    start = i + 1;
                    break;
            }
        }
        segments.Add(path[start..]);
        return segments.Where(s => s.Length > 0).ToList();
    }

    private static int IndexInStack(CapturedFrame frame, StackFrame[] stack)
    {
        var method = frame.GetMethod();
        if (method is null) return -1;
        for (var i = 0; i < stack.Length; i++)
        {
            var candidate = stack[i].GetMethod();
            if (candidate is not null && candidate.MetadataToken == method.MetadataToken && candidate.Module == method.Module)
                return i;
        }
        return -1;
    }

    internal static object? Display(CapturedValue value) => value.IsNull ? null : new ValueDisplay(value.Display);
}
