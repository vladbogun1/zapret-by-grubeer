using System.Text.RegularExpressions;
using Zapret.Core.Model;

namespace Zapret.Core.Engine;

public enum StrategySelectionKind
{
    /// <summary>The previously selected strategy still exists and was applied.</summary>
    Reapplied,

    /// <summary>The previous strategy is gone; a replacement is proposed but not applied.</summary>
    ReplacementProposed,

    /// <summary>Nothing was selected before, so nothing is applied automatically.</summary>
    NothingSelected,
}

public sealed record StrategySelection(
    StrategySelectionKind Kind,
    string? PreviousId,
    StrategyDescriptor? Strategy,
    string? Message = null);

/// <summary>
/// Continuity of the user's choice across engine updates. When the exact strategy is gone the
/// manager proposes the nearest equivalent and says so — it never silently switches (SPEC.md §8.6).
/// </summary>
public static class StrategyMatcher
{
    private static readonly Regex VariantNumber = new(@"^(?<family>.*?)(?<number>\d+)$", RegexOptions.Compiled);

    public static StrategySelection Select(string? previousId, IReadOnlyList<StrategyDescriptor> strategies)
    {
        var usable = strategies.Where(s => s.IsSupported).ToList();

        if (string.IsNullOrEmpty(previousId))
        {
            return new StrategySelection(StrategySelectionKind.NothingSelected, null, null,
                "No strategy has been selected yet.");
        }

        var exact = usable.FirstOrDefault(s => string.Equals(s.Id, previousId, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new StrategySelection(StrategySelectionKind.Reapplied, previousId, exact);
        }

        var nearest = FindNearest(previousId, usable);
        var message = nearest is null
            ? $"Your previous strategy '{StrategyBatParserDisplay(previousId)}' is no longer available in this engine version, and no comparable replacement was found."
            : $"Your previous strategy '{StrategyBatParserDisplay(previousId)}' is no longer available in this engine version. Recommended compatible strategy: {nearest.DisplayName}.";

        return new StrategySelection(StrategySelectionKind.ReplacementProposed, previousId, nearest, message);
    }

    /// <summary>
    /// Nearest match: same family, then the numerically closest variant, preferring a lower number
    /// over a higher one because lower variants are the more conservative ones upstream.
    /// </summary>
    public static StrategyDescriptor? FindNearest(string previousId, IReadOnlyList<StrategyDescriptor> candidates)
    {
        if (candidates.Count == 0) return null;

        var previous = Decompose(StrategyBatParserDisplay(previousId));

        var sameFamily = candidates
            .Select(c => (Candidate: c, Parts: Decompose(c.DisplayName)))
            .Where(x => string.Equals(x.Parts.Family, previous.Family, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (sameFamily.Count == 0) return null;

        if (previous.Number is null)
        {
            return sameFamily.OrderBy(x => x.Parts.Number ?? int.MaxValue).First().Candidate;
        }

        return sameFamily
            .OrderBy(x => Math.Abs((x.Parts.Number ?? 0) - previous.Number.Value))
            .ThenBy(x => x.Parts.Number > previous.Number ? 1 : 0)
            .ThenBy(x => x.Parts.Number ?? 0)
            .First()
            .Candidate;
    }

    private static (string Family, int? Number) Decompose(string displayName)
    {
        var trimmed = displayName.Trim();
        var match = VariantNumber.Match(trimmed);

        return match.Success
            ? (match.Groups["family"].Value.Trim(), int.Parse(match.Groups["number"].Value))
            : (trimmed, null);
    }

    private static string StrategyBatParserDisplay(string id) => Flowseal.StrategyBatParser.ToDisplayName(id);
}
