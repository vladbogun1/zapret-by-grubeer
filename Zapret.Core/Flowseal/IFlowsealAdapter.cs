using Zapret.Core.Model;

namespace Zapret.Core.Flowseal;

/// <summary>
/// The compatibility layer between the manager and whatever upstream build happens to be
/// installed. Version checks live here and nowhere else; version-specific implementations are
/// added only when a real upstream break requires one — docs/flowseal-compatibility.md §1.
/// </summary>
public interface IFlowsealAdapter
{
    /// <summary>
    /// Full inspection of an extracted build: version, capabilities, strategy catalog and a
    /// compatibility verdict. Never throws for a merely unfamiliar build.
    /// </summary>
    EngineRuntimeInfo Inspect(string runtimeDirectory, string? releaseTag = null);

    /// <summary>Dynamic strategy discovery. Count, names and filenames are never assumed.</summary>
    IReadOnlyList<StrategyDescriptor> DiscoverStrategies(string runtimeDirectory, GameFilterState gameFilter);

    GameFilterState ReadGameFilter(string runtimeDirectory);

    void WriteGameFilter(string runtimeDirectory, GameFilterState state);

    IpSetMode ReadIpSet(string runtimeDirectory);

    /// <summary>Creates any missing user list with upstream's placeholder content.</summary>
    void EnsureUserLists(string runtimeDirectory);

    /// <summary>Absolute path to the engine executable inside a given build.</summary>
    string ResolveEngineExecutable(string runtimeDirectory);
}
