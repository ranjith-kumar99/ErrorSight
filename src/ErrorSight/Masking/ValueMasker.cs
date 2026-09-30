using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace ErrorSight.Masking;

/// <summary>Applies <see cref="MaskingOptions"/> to names and values.</summary>
internal sealed class ValueMasker
{
    private static readonly HashSet<string> SensitiveAttributeNames = new(StringComparer.Ordinal)
    {
        "SensitiveAttribute",
        "SensitiveDataAttribute",
        "PersonalDataAttribute",
        "ProtectedPersonalDataAttribute",
    };

    private static readonly ConditionalWeakTable<Type, StrongBox<bool>> SensitiveTypes = new();
    private static readonly ConditionalWeakTable<MaskingOptions, NameCache> NameCaches = new();

    private readonly MaskingOptions _options;

    public ValueMasker(MaskingOptions options) => _options = options;

    public MaskingMode Mode => _options.Mode;

    public bool MaskMessages => _options.MaskExceptionMessages || _options.Mode == MaskingMode.All;

    /// <summary>Decides whether a value must be masked.</summary>
    public bool ShouldMask(string path, string name, Type? valueType, bool declaredSensitive)
    {
        switch (_options.Mode)
        {
            case MaskingMode.None:
                return false;
            case MaskingMode.All:
                return true;
        }

        if (declaredSensitive || IsSensitiveName(name)) return true;
        if (valueType is not null && IsSensitiveType(valueType)) return true;

        if (_options.ShouldMask is { } predicate)
        {
            try { return predicate(new MaskingContext(path, name, valueType)); }
            catch { return true; } // fail closed
        }

        return false;
    }

    /// <summary>Word-aware match of a name against <see cref="MaskingOptions.SensitiveNames"/> (cached per name).</summary>
    public bool IsSensitiveName(string name)
    {
        var patterns = _options.SensitiveNames;
        if (patterns.Count == 0 || string.IsNullOrEmpty(name)) return false;

        var cache = NameCaches.GetValue(_options, static _ => new NameCache());
        if (cache.Version != patterns.Version)
        {
            cache.Results.Clear();
            cache.Version = patterns.Version;
        }

        if (cache.Results.TryGetValue(name, out var cached)) return cached;
        var result = MatchName(name, patterns);
        if (cache.Results.Count < 10_000) cache.Results[name] = result;
        return result;
    }

    private static bool MatchName(string name, SensitiveNameSet patterns)
    {
        var words = SplitWords(name);
        for (var i = 0; i < words.Count; i++)
        {
            var run = string.Empty;
            for (var j = i; j < words.Count; j++)
            {
                run += words[j];
                if (run.Length > patterns.MaxLength + 1) break;
                if (patterns.ContainsNormalised(run)) return true;
                // simple plural: "tokens", "secrets", "cookies"
                if (run.Length > 1 && run[^1] == 's' && patterns.ContainsNormalised(run[..^1])) return true;
            }
        }
        return false;
    }

    /// <summary>Renders a masked value according to the configured style.</summary>
    public string Mask(string? raw)
    {
        raw ??= string.Empty;
        if (_options.Redactor is { } redactor)
        {
            try { return redactor(raw); }
            catch { return "***"; }
        }

        return _options.Style switch
        {
            MaskStyle.Hash => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8].ToLowerInvariant(),
            MaskStyle.Partial => raw.Length >= 8 ? "***" + raw[^4..] : "***",
            _ => "***",
        };
    }

    public static bool HasSensitiveAttribute(MemberInfo member)
    {
        try
        {
            foreach (var data in member.GetCustomAttributesData())
            {
                if (IsSensitiveAttribute(data.AttributeType)) return true;
            }
        }
        catch
        {
            // Attribute metadata unavailable: treat as not annotated.
        }
        return false;
    }

    public static bool IsSensitiveType(Type type) =>
        SensitiveTypes.GetValue(type, static t => new StrongBox<bool>(HasSensitiveAttribute(t))).Value;

    private sealed class NameCache
    {
        public int Version = -1;
        public readonly ConcurrentDictionary<string, bool> Results = new(StringComparer.Ordinal);
    }

    private static bool IsSensitiveAttribute(Type attributeType)
    {
        if (SensitiveAttributeNames.Contains(attributeType.Name)) return true;
        for (var current = attributeType.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "DataClassificationAttribute") return true;
        }
        return false;
    }

    /// <summary>Splits an identifier into lower-case words: <c>XApiKey_value</c> → x, api, key, value.</summary>
    internal static List<string> SplitWords(string name)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) words.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0)
            {
                var previous = name[i - 1];
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next)))
                    Flush();
            }

            current.Append(char.ToLowerInvariant(c));
        }

        Flush();
        return words;
    }
}
