using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace ExceptionLens.Runtime;

/// <summary>How a captured value's display text is produced (lazily, when first read).</summary>
internal enum ValueRendering : byte
{
    Fixed,
    Scalar,
    Summary,
    Collection,
    Truncated,
}

/// <summary>
/// An immutable snapshot of one variable, parameter or member taken at the moment an exception was thrown.
/// Masking has already been applied: a masked value's <see cref="Value"/> is the masked text.
/// </summary>
public sealed class CapturedValue
{
    /// <summary>Variable or member name, e.g. <c>customer</c>, <c>Address</c> or <c>[0]</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Friendly runtime type name, e.g. <c>Customer</c> or <c>List&lt;Order&gt;</c>. Null when the value is null.</summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    /// <summary>True when the value was null.</summary>
    [JsonPropertyName("isNull")]
    public bool IsNull { get; init; }

    /// <summary>
    /// Display text: a formatted scalar (<c>"Jane"</c>, <c>42</c>), an object summary
    /// (<c>Customer { Id = 1837, Address = null }</c>), <c>null</c>, or the masked text.
    /// </summary>
    [JsonPropertyName("value")]
    public string Value
    {
        // Formatting is deferred: exceptions that are captured but never diagnosed (handled by the
        // application) never pay for it. Masked values are always fixed at capture time.
        get => _value ??= Render();
        init => _value = value;
    }

    private string? _value;

    /// <summary>A non-sensitive scalar (boxed primitive, string, ...) formatted on first read.</summary>
    internal object? Raw { get; init; }

    internal ValueRendering Rendering { get; init; }

    internal int MaxStringLength { get; init; } = 256;

    /// <summary>True when <see cref="Value"/> was masked.</summary>
    [JsonPropertyName("masked")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMasked { get; init; }

    /// <summary>Element count for collections.</summary>
    [JsonPropertyName("count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }

    /// <summary>Captured members / elements (limited by depth and size).</summary>
    [JsonPropertyName("members")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CapturedValue>? Members { get; init; }

    /// <summary>Finds a direct member by name.</summary>
    public CapturedValue? Member(string name)
    {
        if (Members is null) return null;
        foreach (var member in Members)
            if (member.Name == name) return member;
        return null;
    }

    internal CapturedValue WithName(string name) => name == Name ? this : new CapturedValue
    {
        Name = name,
        Type = Type,
        IsNull = IsNull,
        Value = Value,
        IsMasked = IsMasked,
        Count = Count,
        Members = Members,
    };

    private const int SummaryMembers = 4;

    private string Render() => Rendering switch
    {
        ValueRendering.Scalar => FormatScalar(Raw, MaxStringLength),
        ValueRendering.Summary => Summary(),
        ValueRendering.Collection => Count is { } count ? $"{Type} (Count = {count})" : Type ?? string.Empty,
        ValueRendering.Truncated => $"{Type} {{…}}",
        _ => IsNull ? "null" : string.Empty,
    };

    private string Summary()
    {
        if (Members is not { Count: > 0 } members) return $"{Type} {{ }}";

        var sb = new StringBuilder(Type).Append(" { ");
        for (var i = 0; i < members.Count && i < SummaryMembers; i++)
        {
            if (i > 0) sb.Append(", ");
            var member = members[i];
            sb.Append(member.Name).Append(" = ");
            if (member.IsNull || member.IsMasked || member.Members is null) sb.Append(member.Value);
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
    public override string ToString() => $"{Name} = {Value}";
}
