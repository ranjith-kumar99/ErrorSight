using System.Reflection;
using System.Runtime.CompilerServices;
using ErrorSight.Runtime.Metadata;

namespace ErrorSight.Runtime;

/// <summary>
/// Holds captured frames per exception. Entries live exactly as long as the exception object
/// (<see cref="ConditionalWeakTable{TKey,TValue}"/>), so nothing leaks when exceptions are handled.
/// </summary>
internal static class CaptureStore
{
    private static readonly ConditionalWeakTable<Exception, FrameList> Captures = new();
    private static readonly ConditionalWeakTable<Assembly, Dictionary<int, WovenMethod>> MetadataByAssembly = new();

    private static long s_windowStart;
    private static int s_windowCount;

    public static void Record(Exception exception, RuntimeMethodHandle method, RuntimeTypeHandle declaringType, int methodId, object?[]? values)
    {
        var options = ErrorSightRuntime.Options;
        if (!options.Capture.Enabled || !options.Capture.ShouldCapture(exception)) return;

        var frames = Captures.GetValue(exception, static _ => new FrameList());
        lock (frames)
        {
            if (frames.Count >= options.Capture.MaxFramesPerException) return;
            // Awaiting a faulted task rethrows the same exception object; record each frame once.
            foreach (var existing in frames)
                if (existing.MethodId == methodId && existing.IsSameMethod(method, declaringType)) return;
        }

        if (!TryAcquireBudget(options.Capture.MaxCapturesPerSecond)) return;

        var type = Type.GetTypeFromHandle(declaringType);
        if (type is null) return;
        var metadata = GetMetadata(type.Assembly, methodId);
        if (metadata is null) return;

        var snapshotter = new ValueSnapshotter(options);
        var roots = new List<CapturedRoot>();
        var slots = metadata.Slots;

        for (var i = 0; i < slots.Count && values is not null && i < values.Length; i++)
        {
            var slot = slots[i];
            var value = values[i];

            if ((slot.Flags & SlotFlags.Closure) != 0)
            {
                ExpandClosure(value, snapshotter, roots, depth: 0);
                continue;
            }

            // A reused local slot is masked if any of its names is sensitive.
            var sensitive = slot.Scopes is { Count: > 1 } && slot.Scopes.Any(s => snapshotter.IsSensitiveName(s.Name));
            roots.Add(new CapturedRoot(slot, snapshotter.Snapshot(slot.Name, value, sensitive, slot.TypeName)));
        }

        var frame = new CapturedFrame(metadata, method, declaringType, roots);
        lock (frames)
        {
            if (frames.Count < options.Capture.MaxFramesPerException) frames.Add(frame);
        }
    }

    public static IReadOnlyList<CapturedFrame>? Get(Exception exception)
    {
        if (!Captures.TryGetValue(exception, out var frames)) return null;
        lock (frames) return frames.Count == 0 ? null : frames.ToArray();
    }

    internal static WovenMethod? GetMetadata(Assembly assembly, int methodId)
    {
        var map = MetadataByAssembly.GetValue(assembly, LoadMetadata);
        return map.TryGetValue(methodId, out var method) ? method : null;
    }

    private static Dictionary<int, WovenMethod> LoadMetadata(Assembly assembly)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(WeaveMetadataSerializer.ResourceName);
            return stream is null ? new() : WeaveMetadataSerializer.Read(stream);
        }
        catch
        {
            return new();
        }
    }

    /// <summary>
    /// Closures (lambda display classes, hoisted display classes in async methods) hold the real variables
    /// as fields: expand them into top-level values.
    /// </summary>
    private static void ExpandClosure(object? closure, ValueSnapshotter snapshotter, List<CapturedRoot> roots, int depth)
    {
        if (closure is null || depth > 4) return;

        foreach (var field in closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            object? value;
            try { value = field.GetValue(closure); }
            catch { continue; }

            if (field.FieldType.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
            {
                ExpandClosure(value, snapshotter, roots, depth + 1);
                continue;
            }

            var name = field.Name switch
            {
                "<>4__this" => "this",
                _ when field.Name.StartsWith('<') => null,
                _ => field.Name,
            };
            if (name is null || field.FieldType.IsByRefLike || field.FieldType.IsPointer) continue;

            roots.Add(new CapturedRoot(null, snapshotter.Snapshot(name, value, forceMask: false, ValueSnapshotter.FriendlyName(field.FieldType))));
        }
    }

    /// <summary>Fixed one-second window rate limit across the process.</summary>
    private static bool TryAcquireBudget(int perSecond)
    {
        if (perSecond <= 0) return true;

        var now = Environment.TickCount64;
        var start = Volatile.Read(ref s_windowStart);
        if (now - start >= 1000 && Interlocked.CompareExchange(ref s_windowStart, now, start) == start)
            Interlocked.Exchange(ref s_windowCount, 0);

        return Interlocked.Increment(ref s_windowCount) <= perSecond;
    }

    private sealed class FrameList : List<CapturedFrame>;
}
