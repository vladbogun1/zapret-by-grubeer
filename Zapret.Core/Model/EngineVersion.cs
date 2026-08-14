using System.Globalization;

namespace Zapret.Core.Model;

/// <summary>Where a detected engine version came from. Provenance is logged, never guessed.</summary>
public enum EngineVersionSource
{
    Unknown,
    ServiceVersionFile,
    ServiceBatConstant,
    ReleaseTag,
}

/// <summary>
/// A lenient engine version. Upstream tags are plain dotted numbers today (<c>1.10.1</c>),
/// but the manager must not fall over on <c>v1.11</c>, <c>1.11.0-rc1</c> or anything else.
/// </summary>
public sealed record EngineVersion(string Raw, EngineVersionSource Source)
{
    public static EngineVersion Unknown { get; } = new("unknown", EngineVersionSource.Unknown);

    public bool IsKnown => Source != EngineVersionSource.Unknown;

    public override string ToString() => Raw;

    /// <summary>
    /// Dotted-numeric comparison with an ordinal fallback. A pre-release suffix sorts below
    /// the same numeric core, so <c>1.11.0-rc1 &lt; 1.11.0</c>.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return 0;
        if (string.IsNullOrWhiteSpace(left)) return string.IsNullOrWhiteSpace(right) ? 0 : -1;
        if (string.IsNullOrWhiteSpace(right)) return 1;

        var (leftCore, leftSuffix) = Split(left);
        var (rightCore, rightSuffix) = Split(right);

        var length = Math.Max(leftCore.Length, rightCore.Length);
        for (var i = 0; i < length; i++)
        {
            var l = i < leftCore.Length ? leftCore[i] : 0;
            var r = i < rightCore.Length ? rightCore[i] : 0;
            if (l != r) return l.CompareTo(r);
        }

        // Same numeric core: a suffix (pre-release) loses to no suffix.
        if (leftSuffix.Length == 0 && rightSuffix.Length == 0) return 0;
        if (leftSuffix.Length == 0) return 1;
        if (rightSuffix.Length == 0) return -1;
        return string.Compare(leftSuffix, rightSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNewer(string? candidate, string? installed) => Compare(candidate, installed) > 0;

    /// <summary>Normalises a release tag for comparison and display: trims and drops a leading v.</summary>
    public static string NormalizeTag(string tag)
    {
        var value = tag.Trim();
        return value.Length > 1 && (value[0] is 'v' or 'V') && char.IsDigit(value[1])
            ? value[1..]
            : value;
    }

    private static (int[] Core, string Suffix) Split(string value)
    {
        var normalized = NormalizeTag(value);

        var cut = normalized.IndexOfAny(new[] { '-', '+', ' ' });
        var suffix = cut >= 0 ? normalized[(cut + 1)..] : string.Empty;
        var core = cut >= 0 ? normalized[..cut] : normalized;

        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number);
            }
            else
            {
                // Non-numeric component: stop numeric comparison here and let it become suffix.
                suffix = suffix.Length == 0 ? part : part + "-" + suffix;
                break;
            }
        }

        return (numbers.ToArray(), suffix);
    }
}
