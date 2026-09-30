using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ExceptionLens.Masking;
using ExceptionLens.Options;

namespace ExceptionLens.Runtime;

/// <summary>
/// Turns live objects into immutable <see cref="CapturedValue"/> trees, applying masking as it goes.
///
/// Safety rules — this runs inside an exception filter, in the middle of the application's failure:
///   • it never executes user code: no property getters, no ToString() overrides, no user enumerators;
///     objects are read through their fields (auto-property backing fields map to property names),
///     which also avoids side effects such as EF Core lazy loading;
///   • framework objects (System.*, Microsoft.*) are summarised, not walked;
///   • depth, member count, collection size, string length and total node count are all bounded.
/// </summary>
internal sealed class ValueSnapshotter
{
    private const int MaxNodes = 400;

    private static readonly ConditionalWeakTable<Type, MemberCache> Members = new();
    private static readonly ConditionalWeakTable<Type, string> FriendlyNames = new();

    private readonly CaptureOptions _limits;
    private readonly ValueMasker _masker;
    private readonly bool _trackPaths;
    private readonly HashSet<object> _path = new(ReferenceEqualityComparer.Instance);
    private int _nodes;

    public ValueSnapshotter(ExceptionLensOptions options)
    {
        _limits = options.Capture;
        _masker = new ValueMasker(options.Masking);
        _trackPaths = options.Masking.ShouldMask is not null;
    }

    public CapturedValue Snapshot(string name, object? value, bool declaredSensitive) =>
        Build(name, _trackPaths ? name : null, value, declaredSensitive, depth: 0);

    /// <summary>Member paths are only needed by a custom <see cref="MaskingOptions.ShouldMask"/> predicate.</summary>
    private string? Child(string? path, string member) => path is null ? null : path + member;

    private CapturedValue Build(string name, string? path, object? value, bool declaredSensitive, int depth)
    {
        _nodes++;
        if (value is null) return new CapturedValue { Name = name, IsNull = true, Value = "null" };

        var type = value.GetType();
        var typeName = FriendlyName(type);
        var masked = _masker.ShouldMask(path ?? name, name, type, declaredSensitive);

        if (IsScalar(type))
        {
            if (masked)
                return new CapturedValue { Name = name, Type = typeName, Value = _masker.Mask(CapturedValue.RawText(value, _limits.MaxStringLength)), IsMasked = true };
            return new CapturedValue { Name = name, Type = typeName, Raw = value, Rendering = ValueRendering.Scalar, MaxStringLength = _limits.MaxStringLength };
        }

        // A sensitive object is hidden entirely (only in SensitiveOnly mode; Mode=All still walks
        // objects so that null-ness of members stays visible).
        if (masked && _masker.Mode != MaskingMode.All)
            return new CapturedValue { Name = name, Type = typeName, Value = "***", IsMasked = true };

        if (_path.Contains(value))
            return new CapturedValue { Name = name, Type = typeName, Value = $"{typeName} {{ (cycle) }}" };

        if (depth >= _limits.MaxDepth || _nodes >= MaxNodes)
            return new CapturedValue { Name = name, Type = typeName, Rendering = ValueRendering.Truncated, Count = CountOf(value) };

        _path.Add(value);
        try
        {
            if (IsWalkableCollection(value, type))
                return BuildCollection(name, path, value, type, typeName, depth);

            if (IsFrameworkType(type))
                return new CapturedValue { Name = name, Type = typeName, Rendering = ValueRendering.Truncated };

            var members = new List<CapturedValue>();
            foreach (var member in Members.GetValue(type, t => new MemberCache(t)).Get(_limits.IncludePrivateFields))
            {
                if (members.Count >= _limits.MaxMembersPerObject || _nodes >= MaxNodes) break;
                object? memberValue;
                try { memberValue = member.Field.GetValue(value); }
                catch { continue; }
                members.Add(Build(member.Name, Child(path, "." + member.Name), memberValue, member.Sensitive, depth + 1));
            }

            return new CapturedValue { Name = name, Type = typeName, Rendering = ValueRendering.Summary, Members = members };
        }
        finally
        {
            _path.Remove(value);
        }
    }

    private CapturedValue BuildCollection(string name, string? path, object value, Type type, string typeName, int depth)
    {
        var count = CountOf(value);
        var items = new List<CapturedValue>();

        try
        {
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (items.Count >= _limits.MaxCollectionItems || _nodes >= MaxNodes) break;
                    var key = $"[{FormatScalarOrType(entry.Key)}]";
                    items.Add(Build(key, Child(path, key), entry.Value, declaredSensitive: false, depth + 1));
                }
            }
            else
            {
                var index = 0;
                foreach (var item in (IEnumerable)value)
                {
                    if (items.Count >= _limits.MaxCollectionItems || _nodes >= MaxNodes) break;
                    var key = $"[{index++}]";
                    items.Add(Build(key, Child(path, key), item, declaredSensitive: false, depth + 1));
                }
            }
        }
        catch
        {
            // Collection modified concurrently etc. — keep what we have.
        }

        return new CapturedValue { Name = name, Type = typeName, Rendering = ValueRendering.Collection, Count = count, Members = items };
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    private string FormatScalarOrType(object? value) =>
        value is null ? "null" : IsScalar(value.GetType()) ? CapturedValue.FormatScalar(value, _limits.MaxStringLength) : FriendlyName(value.GetType());

    // ── Type classification ──────────────────────────────────────────────────

    internal static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum ||
        type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) ||
        type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Uri) ||
        type == typeof(Version) || type == typeof(BigInteger) || type == typeof(Half) ||
        type == typeof(Int128) || type == typeof(UInt128);

    private static bool IsFrameworkType(Type type)
    {
        var ns = type.Namespace;
        return ns is not null && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) ||
                                  ns == "Microsoft" || ns.StartsWith("Microsoft.", StringComparison.Ordinal));
    }

    /// <summary>Arrays and BCL collections are enumerated; user IEnumerable implementations are not (that would run user code).</summary>
    private static bool IsWalkableCollection(object value, Type type) =>
        value is Array ||
        (value is IEnumerable && type.Namespace is { } ns &&
         (ns.StartsWith("System.Collections", StringComparison.Ordinal)));

    private static int? CountOf(object value)
    {
        switch (value)
        {
            case Array array: return array.Length;
            case ICollection collection when IsFrameworkType(value.GetType()): return collection.Count;
        }

        var type = value.GetType();
        if (!IsFrameworkType(type) || value is not IEnumerable) return null;
        try
        {
            return type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance)?.GetValue(value) as int?;
        }
        catch
        {
            return null;
        }
    }

    internal static string FriendlyName(Type type) => FriendlyNames.GetValue(type, ComputeFriendlyName);

    private static string ComputeFriendlyName(Type type)
    {
        if (type.IsArray) return FriendlyName(type.GetElementType()!) + "[]";
        if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) return "anonymous";

        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0) name = name[..tick];

        if (type.IsGenericType)
            name += "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";

        return type.IsNested && type.DeclaringType is { } outer && !outer.Name.StartsWith('<')
            ? FriendlyName(outer) + "." + name
            : name;
    }

    // ── Member discovery (cached per type) ───────────────────────────────────

    private sealed class MemberCache(Type type)
    {
        private MemberAccessor[]? _public;
        private MemberAccessor[]? _all;

        public MemberAccessor[] Get(bool includePrivate) =>
            includePrivate ? _all ??= Discover(type, true) : _public ??= Discover(type, false);

        private static MemberAccessor[] Discover(Type type, bool includePrivate)
        {
            var result = new List<MemberAccessor>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            for (var current = type; current is not null && current != typeof(object) && !IsFrameworkType(current); current = current.BaseType)
            {
                foreach (var field in current.GetFields(flags))
                {
                    string name;
                    bool include, sensitive;

                    if (field.Name.StartsWith('<') && field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    {
                        name = field.Name[1..field.Name.IndexOf('>')];
                        var property = current.GetProperty(name, flags);
                        include = includePrivate || property?.GetMethod?.IsPublic == true;
                        sensitive = ValueMasker.HasSensitiveAttribute(field) ||
                                    (property is not null && ValueMasker.HasSensitiveAttribute(property));
                    }
                    else if (field.Name.StartsWith('<'))
                    {
                        continue; // compiler-generated storage
                    }
                    else
                    {
                        name = field.Name;
                        include = includePrivate || field.IsPublic;
                        sensitive = ValueMasker.HasSensitiveAttribute(field);
                    }

                    if (!include || field.FieldType.IsPointer || field.FieldType.IsByRefLike) continue;
                    if (names.Add(name)) result.Add(new MemberAccessor(name, field, sensitive));
                }
            }

            return result.ToArray();
        }
    }

    private sealed record MemberAccessor(string Name, FieldInfo Field, bool Sensitive);
}
