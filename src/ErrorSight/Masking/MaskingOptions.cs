using System.Collections;

namespace ErrorSight.Masking;

/// <summary>Which captured values are masked (values are only captured at <c>DataCapture.Values</c>).</summary>
public enum MaskingMode
{
    /// <summary>Mask only values whose name marks them as sensitive (default).</summary>
    SensitiveOnly,
    /// <summary>Mask every captured value; only types and null/not-null are reported.</summary>
    All,
    /// <summary>Report every value as-is.</summary>
    None,
}

/// <summary>How a masked value is rendered.</summary>
public enum MaskStyle
{
    /// <summary><c>***</c></summary>
    Redact,
    /// <summary><c>***1234</c> — keeps the last four characters of values at least 8 characters long.</summary>
    Partial,
}

/// <summary>Information passed to <see cref="MaskingOptions.ShouldMask"/>.</summary>
/// <param name="Path">Full path of the value, e.g. <c>customer.Profile.Email</c>.</param>
/// <param name="Name">The member / variable name, e.g. <c>Email</c>.</param>
/// <param name="ValueType">Runtime type of the value.</param>
public readonly record struct MaskingContext(string Path, string Name, Type? ValueType);

/// <summary>
/// Controls how runtime values are masked once you opt in to capturing them (<c>DataCapture.Values</c>).
/// Masking is applied at capture time, so masked values are never held in memory, logged, or exported.
///
/// <code>
///   builder.Services.AddErrorSight(o =>
///   {
///       o.DataCapture = DataCapture.Values;
///       o.Masking.SensitiveNames.Add("iban");
///       o.Masking.ShouldMask = ctx => ctx.Path.StartsWith("patient.");
///   });
/// </code>
/// </summary>
public sealed class MaskingOptions
{
    /// <summary>Which values are masked. Default: <see cref="MaskingMode.SensitiveOnly"/>.</summary>
    public MaskingMode Mode { get; set; } = MaskingMode.SensitiveOnly;

    /// <summary>How masked values are rendered. Default: <see cref="MaskStyle.Redact"/>.</summary>
    public MaskStyle Style { get; set; } = MaskStyle.Redact;

    /// <summary>
    /// Name patterns treated as sensitive. Names are split into words (camelCase, PascalCase, snake_case,
    /// kebab-case) and a pattern matches a run of consecutive words: <c>apikey</c> matches <c>apiKey</c> and
    /// <c>X-Api-Key</c>, <c>password</c> matches <c>userPassword</c>, and <c>pin</c> does not match <c>shipping</c>.
    /// Call <c>Clear()</c> to replace the defaults.
    /// </summary>
    public SensitiveNameSet SensitiveNames { get; } = new(SensitiveNameSet.Defaults);

    /// <summary>Additional predicate: return true to mask a value.</summary>
    public Func<MaskingContext, bool>? ShouldMask { get; set; }

    /// <summary>Custom replacement for masked values (overrides <see cref="Style"/>).</summary>
    public Func<string, string>? Redactor { get; set; }

    /// <summary>
    /// Also redact runtime values that appear inside exception messages (e.g. the key in a
    /// KeyNotFoundException). Always on when <see cref="Mode"/> is <see cref="MaskingMode.All"/>.
    /// </summary>
    public bool MaskExceptionMessages { get; set; }
}

/// <summary>A normalised (lower-case, alphanumeric-only) set of sensitive name patterns.</summary>
public sealed class SensitiveNameSet : ICollection<string>
{
    internal static readonly string[] Defaults =
    [
        "password", "passwd", "pwd", "passphrase", "secret", "token", "apikey", "accesskey", "privatekey",
        "credential", "credentials", "authorization", "auth", "bearer", "cookie", "sessionid", "connectionstring",
        "otp", "ssn", "socialsecurity", "taxid", "creditcard", "cardnumber", "cvv", "cvc", "pin", "iban",
        "accountnumber", "routingnumber", "email", "phone", "dateofbirth", "dob",
    ];

    private readonly HashSet<string> _items = new(StringComparer.Ordinal);

    internal SensitiveNameSet(IEnumerable<string> initial)
    {
        foreach (var item in initial) Add(item);
    }

    /// <summary>Incremented on every change so cached matchers can refresh.</summary>
    internal int Version { get; private set; }

    internal int MaxLength { get; private set; }

    /// <inheritdoc />
    public int Count => _items.Count;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public void Add(string item)
    {
        var normalised = Normalise(item);
        if (normalised.Length == 0 || !_items.Add(normalised)) return;
        MaxLength = Math.Max(MaxLength, normalised.Length);
        Version++;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _items.Clear();
        MaxLength = 0;
        Version++;
    }

    /// <inheritdoc />
    public bool Contains(string item) => _items.Contains(Normalise(item));

    /// <inheritdoc />
    public bool Remove(string item)
    {
        if (!_items.Remove(Normalise(item))) return false;
        MaxLength = _items.Count == 0 ? 0 : _items.Max(i => i.Length);
        Version++;
        return true;
    }

    /// <inheritdoc />
    public void CopyTo(string[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal bool ContainsNormalised(string normalised) => _items.Contains(normalised);

    internal static string Normalise(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        foreach (var c in value)
            if (char.IsLetterOrDigit(c)) buffer[length++] = char.ToLowerInvariant(c);
        return new string(buffer[..length]);
    }
}
