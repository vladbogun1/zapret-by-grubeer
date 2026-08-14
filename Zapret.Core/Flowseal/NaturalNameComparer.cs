using System.Text.RegularExpressions;

namespace Zapret.Core.Flowseal;

/// <summary>
/// Reproduces upstream's strategy ordering so the manager's list matches what a user sees in
/// <c>service.bat</c>. Upstream sorts by name with every digit run left-padded to 8 characters
/// (<c>[Regex]::Replace($_.Name, '(\d+)', { PadLeft(8, '0') })</c>), which is a natural sort:
/// ALT2 before ALT10.
/// </summary>
public sealed class NaturalNameComparer : IComparer<string>
{
    public static NaturalNameComparer Instance { get; } = new();

    private static readonly Regex DigitRun = new(@"\d+", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        return string.Compare(Pad(x), Pad(y), StringComparison.OrdinalIgnoreCase);
    }

    internal static string Pad(string value) =>
        DigitRun.Replace(value, match => match.Value.PadLeft(8, '0'));
}
