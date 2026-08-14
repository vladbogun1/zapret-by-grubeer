using System.Text.RegularExpressions;

namespace Zapret.Core.Testing;

/// <summary>One target's result inside a strategy run.</summary>
public sealed record TargetResult(string Name, bool Passed, int? PingMilliseconds);

/// <summary>Everything upstream's test utility reported about one strategy.</summary>
public sealed record StrategyTestResult
{
    public required string StrategyId { get; init; }
    public IReadOnlyList<TargetResult> Targets { get; init; } = Array.Empty<TargetResult>();

    public int PassedCount => Targets.Count(t => t.Passed);
    public int TotalCount => Targets.Count;

    /// <summary>0..1. Zero targets means the strategy produced nothing, which ranks last.</summary>
    public double SuccessRatio => TotalCount == 0 ? 0 : (double)PassedCount / TotalCount;

    public int SuccessPercent => (int)Math.Round(SuccessRatio * 100);

    public int? AveragePing
    {
        get
        {
            var pings = Targets.Where(t => t is { Passed: true, PingMilliseconds: not null })
                .Select(t => t.PingMilliseconds!.Value).ToList();

            return pings.Count == 0 ? null : (int)Math.Round(pings.Average());
        }
    }
}

/// <summary>
/// Parses the output of upstream's <c>utils\test zapret.ps1</c>, which already walks every discovered
/// strategy and probes a list of targets. The manager drives that script rather than reimplementing the
/// sweep: upstream owns what a meaningful test is, and reinventing it would drift from their behaviour.
/// <para>
/// Observed output shape (1.10.1):
/// </para>
/// <code>
/// [1/21] general (ALT).bat
/// DiscordMain      HTTP:OK    TLS1.2:OK   TLS1.3:OK  | Ping: 10 ms
/// CloudflareDNS1111    Ping: 10 ms
/// </code>
/// <para>
/// The parser is deliberately tolerant: an unknown line is ignored, a renamed check column still counts
/// through the generic OK/FAIL match, and a truncated run yields the strategies that did complete.
/// </para>
/// </summary>
public static class StrategyTestParser
{
    private static readonly Regex Header = new(
        @"^\s*\[(?<index>\d+)\s*/\s*(?<total>\d+)\]\s*(?<name>.+?)(?:\.bat)?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex PingOnly = new(
        @"^\s*(?<name>\S+)\s+Ping:\s*(?<ping>\d+)\s*ms\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CheckLine = new(
        @"^\s*(?<name>\S+)\s+(?<checks>(?:\S+:\s*(?:OK|FAIL|ERROR|TIMEOUT)\s*)+)(?:\|\s*Ping:\s*(?<ping>\d+)\s*ms)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CheckToken = new(
        @"(?<label>\S+?):\s*(?<value>OK|FAIL|ERROR|TIMEOUT)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Total strategy count announced by the header, when the run got that far.</summary>
    public static int? ParseTotal(string output)
    {
        foreach (var line in Lines(output))
        {
            var match = Header.Match(line);
            if (match.Success) return int.Parse(match.Groups["total"].Value);
        }

        return null;
    }

    public static IReadOnlyList<StrategyTestResult> Parse(string output)
    {
        var results = new List<StrategyTestResult>();

        string? currentStrategy = null;
        var targets = new List<TargetResult>();

        void Flush()
        {
            if (currentStrategy is null) return;

            results.Add(new StrategyTestResult { StrategyId = currentStrategy, Targets = targets.ToList() });
            targets.Clear();
        }

        foreach (var line in Lines(output))
        {
            var header = Header.Match(line);
            if (header.Success)
            {
                Flush();
                currentStrategy = header.Groups["name"].Value.Trim();
                continue;
            }

            if (currentStrategy is null) continue;

            var checks = CheckLine.Match(line);
            if (checks.Success)
            {
                var tokens = CheckToken.Matches(checks.Groups["checks"].Value);

                // A target counts as passed only when every check on its line passed.
                var passed = tokens.Count > 0 && tokens.All(t =>
                    t.Groups["value"].Value.Equals("OK", StringComparison.OrdinalIgnoreCase));

                targets.Add(new TargetResult(
                    checks.Groups["name"].Value,
                    passed,
                    checks.Groups["ping"].Success ? int.Parse(checks.Groups["ping"].Value) : null));

                continue;
            }

            // A ping-only line (DNS targets) is a pass: it answered.
            var ping = PingOnly.Match(line);
            if (ping.Success)
            {
                targets.Add(new TargetResult(ping.Groups["name"].Value, true, int.Parse(ping.Groups["ping"].Value)));
            }
        }

        Flush();
        return results;
    }

    /// <summary>
    /// Best first: highest success ratio, then lowest average ping. Ties keep upstream's order, so an
    /// equally good lower-numbered variant wins, which is the more conservative choice.
    /// </summary>
    public static IReadOnlyList<StrategyTestResult> Rank(IEnumerable<StrategyTestResult> results) =>
        results
            .Select((result, index) => (result, index))
            .OrderByDescending(x => x.result.SuccessRatio)
            .ThenBy(x => x.result.AveragePing ?? int.MaxValue)
            .ThenBy(x => x.index)
            .Select(x => x.result)
            .ToList();

    /// <summary>The strategy to recommend, or null when nothing passed a single target.</summary>
    public static StrategyTestResult? SelectBest(IEnumerable<StrategyTestResult> results)
    {
        var best = Rank(results).FirstOrDefault();
        return best is null || best.PassedCount == 0 ? null : best;
    }

    /// <summary>
    /// Whether the sweep actually separated the strategies. On a connection where nothing under test is
    /// blocked, every strategy passes every target with the same latency — and then there is no winner, only
    /// a tie-break artefact. Observed on a real run: all 21 strategies at 100% and 27 ms.
    /// <para>
    /// Crowning the first of twenty-one identical results as "recommended" would be a lie the user cannot
    /// see through, so callers use this to say "no difference measured" instead.
    /// </para>
    /// </summary>
    /// <summary>
    /// Latency spread below this is measurement jitter, not a difference between strategies. Chosen from a
    /// real sweep where all 21 strategies passed every target and the averages split into 27 ms and 28 ms:
    /// exact comparison called that "discriminating" and would have crowned one of ten equally fast
    /// strategies over a single millisecond.
    /// </summary>
    public const int LatencyToleranceMilliseconds = 10;

    public static bool IsDiscriminating(IEnumerable<StrategyTestResult> results)
    {
        var measured = results.Where(r => r.TotalCount > 0).ToList();
        if (measured.Count < 2) return false;

        // Any difference in what actually worked is real and decides it.
        var scores = measured.Select(r => r.SuccessPercent).ToList();
        if (scores.Max() != scores.Min()) return true;

        var latencies = measured.Select(r => r.AveragePing).Where(p => p is not null).Select(p => p!.Value).ToList();
        if (latencies.Count < 2) return false;

        return latencies.Max() - latencies.Min() >= LatencyToleranceMilliseconds;
    }

    private static IEnumerable<string> Lines(string output) =>
        output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
