using System.Reflection;
using ExceptionLens.Runtime.Metadata;

namespace ExceptionLens.Runtime;

/// <summary>Values captured in one instrumented stack frame while an exception passed through it.</summary>
public sealed class CapturedFrame
{
    private readonly RuntimeMethodHandle _methodHandle;
    private readonly RuntimeTypeHandle _typeHandle;
    private MethodBase? _method;
    private bool _methodResolved;

    internal CapturedFrame(
        WovenMethod metadata,
        RuntimeMethodHandle methodHandle,
        RuntimeTypeHandle typeHandle,
        IReadOnlyList<CapturedRoot> roots)
    {
        Metadata = metadata;
        _methodHandle = methodHandle;
        _typeHandle = typeHandle;
        Roots = roots;
    }

    /// <summary>Source-level method name, e.g. <c>OrderService.GetCityAsync</c>.</summary>
    public string Method => Metadata.DisplayName;

    /// <summary>All captured variables, using each variable's primary name.</summary>
    public IReadOnlyList<CapturedValue> Values => Roots.Select(r => r.Value).ToList();

    internal WovenMethod Metadata { get; }

    internal IReadOnlyList<CapturedRoot> Roots { get; }

    internal int MethodId => Metadata.Id;

    internal bool IsSameMethod(RuntimeMethodHandle method, RuntimeTypeHandle type) =>
        _methodHandle.Equals(method) && _typeHandle.Equals(type);

    /// <summary>The runtime method this frame belongs to (the compiled MoveNext for async methods).</summary>
    internal MethodBase? GetMethod()
    {
        if (_methodResolved) return _method;
        try { _method = MethodBase.GetMethodFromHandle(_methodHandle, _typeHandle); }
        catch { _method = null; }
        _methodResolved = true;
        return _method;
    }

    /// <summary>
    /// The variables in scope at <paramref name="ilOffset"/>. Release builds reuse local slots across
    /// scopes, so a slot is reported under the name it has at that offset (or dropped when out of scope).
    /// </summary>
    internal List<CapturedValue> ValuesAt(int? ilOffset)
    {
        var result = new List<CapturedValue>(Roots.Count);
        foreach (var root in Roots)
        {
            var scopes = root.Slot?.Scopes;
            if (scopes is null || scopes.Count == 0)
            {
                result.Add(root.Value);
                continue;
            }

            if (ilOffset is not { } offset)
            {
                result.Add(root.Value);
                continue;
            }

            foreach (var scope in scopes)
            {
                if (offset >= scope.Start && offset < scope.End)
                {
                    result.Add(root.Value.WithName(scope.Name));
                    break;
                }
            }
        }

        // Later entries win on duplicate names (e.g. a closure field shadowing a parameter copy).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = result.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(result[i].Name)) result.RemoveAt(i);
        }

        return result;
    }
}

/// <summary>A captured root value together with the slot it came from (null for closure-expanded fields).</summary>
internal readonly record struct CapturedRoot(WovenSlot? Slot, CapturedValue Value);
