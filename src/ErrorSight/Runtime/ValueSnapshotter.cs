using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using ErrorSight.Masking;
using ErrorSight.Options;

namespace ErrorSight.Runtime;

/// <summary>
/// Turns live objects into immutable <see cref="CapturedValue"/> trees at the configured
/// <see cref="ErrorSightOptions.DataCapture"/> level:
///   • None: names, types and null-ness only, which is enough to tell which expression was null. No values
///     are kept, and dictionary entries (whose keys are application data) are not read;
///   • Metadata: also collection counts;
///   • Values: also the values themselves, masked as they are captured.
///
/// Safety rules. This runs inside an exception filter, in the middle of the application's failure:
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

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(string)] = "string", [typeof(int)] = "int", [typeof(long)] = "long", [typeof(short)] = "short",
        [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(uint)] = "uint", [typeof(ulong)] = "ulong",
        [typeof(ushort)] = "ushort", [typeof(bool)] = "bool", [typeof(char)] = "char", [typeof(decimal)] = "decimal",
        [typeof(double)] = "double", [typeof(float)] = "float", [typeof(object)] = "object",
        [typeof(nint)] = "nint", [typeof(nuint)] = "nuint",
    };

    private readonly CaptureOptions _limits;
    private readonly ValueMasker _masker;
    private readonly DataCapture _level;
    private readonly bool _values;
    private readonly bool _trackPaths;
    private readonly HashSet<object> _path = new(ReferenceEqualityComparer.Instance);
    private int _nodes;

    public ValueSnapshotter(ErrorSightOptions options)
    {
        _limits = options.Capture;
        _masker = new ValueMasker(options.Masking);
        _level = options.DataCapture;
        _values = _level == DataCapture.Values;
        _trackPaths = _values && options.Masking.ShouldMask is not null;
    }

    public DataCapture Level => _level;

    public bool IsSensitiveName(string name) => _masker.Mode != MaskingMode.None && _masker.IsSensitiveName(name);

    public CapturedValue Snapshot(string name, object? value, bool forceMask, string? declaredType) =>
        Build(name, _trackPaths ? name : null, value, forceMask, declaredType, depth: 0);

    /// <summary>Member paths are only needed by a custom <see cref="MaskingOptions.ShouldMask"/> predicate.</summary>
    private static string? Child(string? path, string member) => path is null ? null : path + member;

    private CapturedValue Build(string name, string? path, object? value, bool forceMask, string? declaredType, int depth)
    {
        _nodes++;
        if (value is null)
        {
            return new CapturedValue
            {
                Name = name,
                IsNull = true,
                Type = string.IsNullOrEmpty(declaredType) ? null : declaredType,
                Level = _level,
                Value = _values ? "null" : null,
            };
        }

        var type = value.GetType();
        var typeName = FriendlyName(type);
        var masked = _values && (forceMask || _masker.ShouldMask(path ?? name, name, type));

        if (IsScalar(type))
        {
            if (!_values) return new CapturedValue { Name = name, Type = typeName, Level = _level };
            if (masked)
                return new CapturedValue { Name = name, Type = typeName, Level = _level, Value = _masker.Mask(CapturedValue.RawText(value, _limits.MaxStringLength)), IsMasked = true };
            return new CapturedValue { Name = name, Type = typeName, Level = _level, Raw = value, Rendering = ValueRendering.Scalar, MaxStringLength = _limits.MaxStringLength };
        }

        // A sensitive object is hidden entirely (Mode=All still walks objects so that null-ness of members stays visible).
        if (masked && _masker.Mode != MaskingMode.All)
            return new CapturedValue { Name = name, Type = typeName, Level = _level, Value = "***", IsMasked = true };

        if (_path.Contains(value))
            return new CapturedValue { Name = name, Type = typeName, Level = _level, Value = _values ? $"{typeName} {{ (cycle) }}" : null };

        if (depth >= _limits.MaxDepth || _nodes >= MaxNodes)
        {
            return new CapturedValue
            {
                Name = name, Type = typeName, Level = _level,
                Rendering = _values ? ValueRendering.Truncated : ValueRendering.None,
                Count = _level >= DataCapture.Metadata ? CountOf(value) : null,
            };
        }

        _path.Add(value);
        try
        {
            if (IsWalkableCollection(value, type))
                return BuildCollection(name, path, value, typeName, depth);

            if (IsFrameworkType(type))
                return new CapturedValue { Name = name, Type = typeName, Level = _level, Rendering = _values ? ValueRendering.Truncated : ValueRendering.None };

            var members = new List<CapturedValue>();
            foreach (var member in Members.GetValue(type, t => new MemberCache(t)).Get(_limits.IncludePrivateFields))
            {
                if (members.Count >= _limits.MaxMembersPerObject || _nodes >= MaxNodes) break;

                if (!_values && member.NeverNull)
                {
                    // Only null-ness matters without values: a non-nullable scalar is not even read (no boxing).
                    _nodes++;
                    members.Add(new CapturedValue { Name = member.Name, Type = member.TypeName, Level = _level });
                    continue;
                }

                object? memberValue;
                try { memberValue = member.Field.GetValue(value); }
                catch { continue; }
                members.Add(Build(member.Name, Child(path, "." + member.Name), memberValue, forceMask: false, member.TypeName, depth + 1));
            }

            return new CapturedValue
            {
                Name = name, Type = typeName, Level = _level,
                Rendering = _values ? ValueRendering.Summary : ValueRendering.None,
                Children = members,
            };
        }
        finally
        {
            _path.Remove(value);
        }
    }

    private CapturedValue BuildCollection(string name, string? path, object value, string typeName, int depth)
    {
        var count = CountOf(value);
        var items = new List<CapturedValue>();

        try
        {
            if (value is IDictionary dictionary)
            {
                // Keys are application data (ids, emails, ...): entries are only read when values are captured.
                if (_values)
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (items.Count >= _limits.MaxCollectionItems || _nodes >= MaxNodes) break;
                        var key = $"[{FormatScalarOrType(entry.Key)}]";
                        items.Add(Build(key, Child(path, key), entry.Value, forceMask: false, declaredType: null, depth + 1));
                    }
                }
            }
            else
            {
                var index = 0;
                foreach (var item in (IEnumerable)value)
                {
                    if (items.Count >= _limits.MaxCollectionItems || _nodes >= MaxNodes) break;
                    var key = $"[{index++}]";
                    items.Add(Build(key, Child(path, key), item, forceMask: false, declaredType: null, depth + 1));
                }
            }
        }
        catch
        {
            // Collection modified concurrently etc. Keep what we have.
        }

        return new CapturedValue
        {
            Name = name,
            Type = typeName,
            Level = _level,
            Rendering = _values ? ValueRendering.Collection : ValueRendering.None,
            Count = _level >= DataCapture.Metadata ? count : null,
            Children = items,
            Complete = count is { } total && items.Count == total,
        };
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
        if (Keywords.TryGetValue(type, out var keyword)) return keyword;
        if (type.IsArray) return FriendlyName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        if (Nullable.GetUnderlyingType(type) is { } underlying) return FriendlyName(underlying) + "?";
        if (type.IsGenericParameter) return type.Name;
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
                    bool include;

                    if (field.Name.StartsWith('<') && field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    {
                        name = field.Name[1..field.Name.IndexOf('>')];
                        var property = current.GetProperty(name, flags);
                        include = includePrivate || property?.GetMethod?.IsPublic == true;
                    }
                    else if (field.Name.StartsWith('<'))
                    {
                        continue; // compiler-generated storage
                    }
                    else
                    {
                        name = field.Name;
                        include = includePrivate || field.IsPublic;
                    }

                    if (!include || field.FieldType.IsPointer || field.FieldType.IsByRefLike) continue;
                    var neverNull = field.FieldType.IsValueType && IsScalar(field.FieldType);
                    if (names.Add(name)) result.Add(new MemberAccessor(name, field, FriendlyName(field.FieldType), neverNull));
                }
            }

            return result.ToArray();
        }
    }

    /// <param name="NeverNull">A non-nullable scalar (int, decimal, enum, ...): its null-ness and type are known without reading it.</param>
    private sealed record MemberAccessor(string Name, FieldInfo Field, string TypeName, bool NeverNull);
}
