using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ErrorSight.Options;

namespace ErrorSight.Runtime;

/// <summary>How a captured value's display text is produced (lazily, when first read).</summary>
internal enum ValueRendering : byte
{
    /// <summary>No value was captured (below <see cref="DataCapture.Values"/>): <see cref="CapturedValue.Value"/> is null.</summary>
    None,
    Fixed,
    Scalar,
    Summary,
    Collection,
    Truncated,
}

/// <summary>
/// An immutable snapshot of one variable, parameter or member, taken when an exception was thrown. What it holds
/// depends on <see cref="ErrorSightOptions.DataCapture"/>:
/// <list type="bullet">
///   <item><see cref="DataCapture.None"/>: the name, the type and whether the value was null;</item>
///   <item><see cref="DataCapture.Metadata"/>: also the <see cref="Count"/> of collections;</item>
///   <item><see cref="DataCapture.Values"/>: also <see cref="Value"/> (already masked) and <see cref="Members"/>.</item>
/// </list>
/// </summary>
public sealed class CapturedValue
{
    /// <summary>Variable or member name, e.g. <c>customer</c>, <c>Address</c> or <c>[0]</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Friendly type name, e.g. <c>Customer</c>, <c>string</c> or <c>List&lt;Order&gt;</c>: the runtime type, or the
    /// declared type when the value is null.
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>True when the value was null.</summary>
    [JsonPropertyName("isNull")]
    public bool IsNull { get; init; }

    /// <summary>
    /// The value as text: a formatted scalar (<c>"Jane"</c>, <c>42</c>), an object summary
    /// (<c>Customer { Id = 1837, Address = null }</c>), <c>null</c>, or the masked text. Only captured at
    /// <see cref="DataCapture.Values"/>; null otherwise.
    /// </summary>
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value
    {
        // Formatting is deferred: exceptions that are captured but never diagnosed (handled by the
        // application) never pay for it. Masked values are always fixed at capture time.
        get => _value ??= Render();
        init => _value = value;
    }

    private string? _value;

    /// <summary>True when <see cref="Value"/> was masked.</summary>
    [JsonPropertyName("masked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMasked { get; init; }

    /// <summary>Element count for collections (<see cref="DataCapture.Metadata"/> and above).</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }

    /// <summary>Captured members / elements, limited by depth and size (<see cref="DataCapture.Values"/> only).</summary>
    [JsonPropertyName("members")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CapturedValue>? Members => Level == DataCapture.Values ? Children : null;

    /// <summary>
    /// Text to show: <see cref="Value"/> when values were captured, otherwise a description of what was captured,
    /// e.g. <c>null</c>, <c>Order</c> or <c>List&lt;Order&gt; (Count = 2)</c>.
    /// </summary>
    [JsonIgnore]
    public string Display => Value ?? Describe();

    /// <summary>Finds a direct member by name (<see cref="DataCapture.Values"/> only).</summary>
    public CapturedValue? Member(string name) => Find(Members, name);

    /// <summary>The level this value was captured at.</summary>
    internal DataCapture Level { get; init; }

    /// <summary>
    /// Members and elements, used internally to evaluate expressions such as <c>order.Customer.Address</c> at every
    /// level. Below <see cref="DataCapture.Values"/> they hold only names, types and null-ness.
    /// </summary>
    internal IReadOnlyList<CapturedValue>? Children { get; init; }

    /// <summary>For collections: every element is in <see cref="Children"/>.</summary>
    internal bool Complete { get; init; }

    /// <summary>A non-sensitive scalar (boxed primitive, string, ...) formatted on first read.</summary>
    internal object? Raw { get; init; }

    internal ValueRendering Rendering { get; init; }

    internal int MaxStringLength { get; init; } = 256;

    internal CapturedValue? Child(string name) => Find(Children, name);

    private static CapturedValue? Find(IReadOnlyList<CapturedValue>? values, string name)
    {
        if (values is null) return null;
        foreach (var value in values)
            if (value.Name == name) return value;
        return null;
    }

    internal CapturedValue WithName(string name) => name == Name ? this : new CapturedValue
    {
        Name = name,
        Type = Type,
        IsNull = IsNull,
        Value = _value,
        IsMasked = IsMasked,
        Count = Count,
        Level = Level,
        Children = Children,
        Complete = Complete,
        Raw = Raw,
        Rendering = Rendering,
        MaxStringLength = MaxStringLength,
    };

    /// <summary>
    /// This value as reported at a lower level than it was captured at: nothing above <paramref name="level"/> is
    /// visible through the view.
    /// </summary>
    internal CapturedValue ViewAt(DataCapture level)
    {
        if (level >= Level) return this;
        return new CapturedValue
        {
            Name = Name,
            Type = Type,
            IsNull = IsNull,
            Count = level >= DataCapture.Metadata ? Count : null,
            Level = level,
            Children = Children,
            Complete = Complete,
            Rendering = ValueRendering.None,
        };
    }

    private const int SummaryMembers = 4;

    private string? Render() => Rendering switch
    {
        ValueRendering.None => null,
        ValueRendering.Scalar => FormatScalar(Raw, MaxStringLength),
        ValueRendering.Summary => Summary(),
        ValueRendering.Collection => Count is { } count ? $"{Type} (Count = {count})" : Type ?? string.Empty,
        ValueRendering.Truncated => $"{Type} {{…}}",
        _ => IsNull ? "null" : string.Empty,
    };

    private string Describe()
    {
        if (IsNull) return "null";
        var type = Type ?? "?";
        return Count is { } count ? $"{type} (Count = {count})" : type;
    }

    private string Summary()
    {
        if (Children is not { Count: > 0 } members) return $"{Type} {{ }}";

        var sb = new StringBuilder(Type).Append(" { ");
        for (var i = 0; i < members.Count && i < SummaryMembers; i++)
        {
            if (i > 0) sb.Append(", ");
            var member = members[i];
            sb.Append(member.Name).Append(" = ");
            if (member.IsNull || member.IsMasked || member.Children is null) sb.Append(member.Display);
            else if (member.Count is { } count) sb.Append($"[{count}]");
            else sb.Append("{…}");
        }
        if (members.Count > SummaryMembers) sb.Append(", …");
        return sb.Append(" }").ToString();
    }

    internal static string FormatScalar(object? value, int maxStringLength) => value switch
    {
        null => "null",
        string s => $"\"{Truncate(s, maxStringLength)}\"",
        char c => $"'{c}'",
        _ => RawText(value, maxStringLength),
    };

    internal static string RawText(object value, int maxStringLength) => value switch
    {
        string s => Truncate(s, maxStringLength),
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty, // only BCL scalar types reach this point
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <inheritdoc />
    public override string ToString() => $"{Name} = {Display}";
}
