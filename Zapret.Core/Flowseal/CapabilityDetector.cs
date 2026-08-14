using Zapret.Core.Model;

namespace Zapret.Core.Flowseal;

/// <summary>
/// Detects what the installed build can do, from the files that are actually there.
/// Nothing is inferred from the version number — docs/flowseal-compatibility.md §5.
/// </summary>
public static class CapabilityDetector
{
    public static UpstreamCapabilities Detect(string runtimeDirectory)
    {
        var lists = UpstreamLayout.Lists(runtimeDirectory);
        var utils = UpstreamLayout.Utils(runtimeDirectory);
        var bin = UpstreamLayout.Bin(runtimeDirectory);

        return new UpstreamCapabilities
        {
            SupportsUpstreamServiceMode = File.Exists(UpstreamLayout.EngineExecutable(runtimeDirectory)),
            SupportsGameFilter = Directory.Exists(utils),
            SupportsUpdateCheckToggle = Directory.Exists(utils),
            SupportsStrategyTests = File.Exists(UpstreamLayout.TestScript(runtimeDirectory)),
            SupportsUserDomainLists = Directory.Exists(lists),
            SupportsIpSetFilter = File.Exists(UpstreamLayout.IpSetAll(runtimeDirectory))
                                  || File.Exists(UpstreamLayout.IpSetAllBackup(runtimeDirectory)),
            // Release archives omit the .service directory, so these two are manager-provided features:
            // the payload is fetched from upstream's repository, exactly as upstream's service.bat does.
            // A local copy is used when present, which is the case for a git checkout.
            SupportsIpSetUpdate = Directory.Exists(lists),
            SupportsHostsUpdater = true,
            SupportsFakeReplacement = HasReplaceableFakes(bin),
        };
    }

    /// <summary>
    /// Fake replacement needs at least one ACTIVE_* target and at least one candidate to put there.
    /// </summary>
    private static bool HasReplaceableFakes(string binDirectory)
    {
        if (!Directory.Exists(binDirectory)) return false;

        var hasActive = File.Exists(Path.Combine(binDirectory, UpstreamLayout.ActiveDiscordFakeName))
                        || File.Exists(Path.Combine(binDirectory, UpstreamLayout.ActiveGameFakeName));
        if (!hasActive) return false;

        return EnumerateFakeCandidates(binDirectory).Any();
    }

    /// <summary>Every <c>bin\*.bin</c> whose name does not start with <c>ACTIVE_</c>.</summary>
    public static IEnumerable<string> EnumerateFakeCandidates(string binDirectory)
    {
        if (!Directory.Exists(binDirectory)) return Array.Empty<string>();

        return Directory
            .EnumerateFiles(binDirectory, "*.bin", SearchOption.TopDirectoryOnly)
            .Where(path => !Path.GetFileName(path).StartsWith(UpstreamLayout.ActiveFakePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path)!, NaturalNameComparer.Instance);
    }
}
