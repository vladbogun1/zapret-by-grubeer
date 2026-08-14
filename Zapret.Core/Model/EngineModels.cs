namespace Zapret.Core.Model;

/// <summary>Upstream's game filter modes, driven by <c>utils\game_filter.enabled</c>.</summary>
public enum GameFilterMode
{
    Off,
    All,
    TcpOnly,
    UdpOnly,
}

/// <summary>
/// Game filter state and the port expansion upstream performs. The <c>12</c> value is
/// upstream's deliberate no-op placeholder and is passed through verbatim — see
/// flowseal-compatibility.md §4.2 rule 7.
/// </summary>
public sealed record GameFilterState(GameFilterMode Mode)
{
    public const string DisabledPorts = "12";
    public const string EnabledPorts = "1024-65535";

    public static GameFilterState Off { get; } = new(GameFilterMode.Off);

    public string TcpPorts => Mode is GameFilterMode.All or GameFilterMode.TcpOnly ? EnabledPorts : DisabledPorts;
    public string UdpPorts => Mode is GameFilterMode.All or GameFilterMode.UdpOnly ? EnabledPorts : DisabledPorts;

    /// <summary>Expansion of bare <c>%GameFilter%</c>: enabled in any mode but Off.</summary>
    public string AnyPorts => Mode is GameFilterMode.Off ? DisabledPorts : EnabledPorts;

    public string Description => Mode switch
    {
        GameFilterMode.All => "enabled (TCP and UDP)",
        GameFilterMode.TcpOnly => "enabled (TCP)",
        GameFilterMode.UdpOnly => "enabled (UDP)",
        _ => "disabled",
    };

    /// <summary>Flag file body, or null when the flag file must not exist.</summary>
    public string? FlagFileContent => Mode switch
    {
        GameFilterMode.All => "all",
        GameFilterMode.TcpOnly => "tcp",
        GameFilterMode.UdpOnly => "udp",
        _ => null,
    };

    /// <summary>
    /// Mirrors upstream <c>:game_switch_status</c> exactly: a missing file is Off, and any
    /// present-but-unrecognised content means UDP.
    /// </summary>
    public static GameFilterState FromFlagFile(string? firstLineOrNullWhenAbsent)
    {
        if (firstLineOrNullWhenAbsent is null) return Off;

        var value = firstLineOrNullWhenAbsent.Trim();
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase)) return new GameFilterState(GameFilterMode.All);
        if (value.Equals("tcp", StringComparison.OrdinalIgnoreCase)) return new GameFilterState(GameFilterMode.TcpOnly);
        return new GameFilterState(GameFilterMode.UdpOnly);
    }
}

/// <summary>Upstream's three-state IPSet filter, detected from file content only.</summary>
public enum IpSetMode
{
    /// <summary>Empty file: no IPSet restriction.</summary>
    Any,

    /// <summary>Sentinel-only file: IPSet effectively disabled.</summary>
    None,

    /// <summary>Real list loaded.</summary>
    Loaded,
}

public static class IpSetState
{
    public const string Sentinel = "203.0.113.113/32";

    /// <summary>Mirrors upstream <c>:ipset_switch_status</c>.</summary>
    public static IpSetMode Detect(string? fileContentOrNullWhenAbsent)
    {
        if (string.IsNullOrEmpty(fileContentOrNullWhenAbsent)) return IpSetMode.Any;

        var lines = fileContentOrNullWhenAbsent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return IpSetMode.Any;

        return fileContentOrNullWhenAbsent.Contains(Sentinel, StringComparison.Ordinal)
            ? IpSetMode.None
            : IpSetMode.Loaded;
    }
}

/// <summary>
/// What the installed upstream build can actually do. Detected from the files present, never
/// inferred from a version number — flowseal-compatibility.md §5.
/// </summary>
public sealed record UpstreamCapabilities
{
    public bool SupportsGameFilter { get; init; }
    public bool SupportsIpSetFilter { get; init; }
    public bool SupportsIpSetUpdate { get; init; }
    public bool SupportsHostsUpdater { get; init; }
    public bool SupportsUserDomainLists { get; init; }
    public bool SupportsStrategyTests { get; init; }
    public bool SupportsFakeReplacement { get; init; }
    public bool SupportsUpdateCheckToggle { get; init; }
    public bool SupportsUpstreamServiceMode { get; init; }

    /// <summary>Manager-side diagnostics never depend on upstream.</summary>
    public bool SupportsDiagnostics => true;

    public static UpstreamCapabilities None { get; } = new();
}

/// <summary>A discovered strategy. Unsupported ones stay in the list, with a reason.</summary>
public sealed record StrategyDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string FilePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReferencedPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public string? UnsupportedReason { get; init; }

    public bool IsSupported => UnsupportedReason is null;
}

public enum CompatibilitySeverity
{
    Information,
    Limitation,
    Blocker,
}

public sealed record CompatibilityCheck(string Name, bool Passed, string Detail, CompatibilitySeverity Severity);

public enum CompatibilityOutcome
{
    Compatible,
    CompatibleWithLimitations,
    Incompatible,
}

/// <summary>Result of validating a build, including one newer than anything ever tested.</summary>
public sealed record CompatibilityReport(CompatibilityOutcome Outcome, IReadOnlyList<CompatibilityCheck> Checks)
{
    public bool CanActivate => Outcome != CompatibilityOutcome.Incompatible;

    public IEnumerable<CompatibilityCheck> Blockers =>
        Checks.Where(c => !c.Passed && c.Severity == CompatibilitySeverity.Blocker);

    public IEnumerable<CompatibilityCheck> Limitations =>
        Checks.Where(c => !c.Passed && c.Severity == CompatibilitySeverity.Limitation);
}

/// <summary>Everything the manager knows about one extracted engine build.</summary>
public sealed record EngineRuntimeInfo
{
    public required string Directory { get; init; }
    public required EngineVersion Version { get; init; }
    public required UpstreamCapabilities Capabilities { get; init; }
    public required IReadOnlyList<StrategyDescriptor> Strategies { get; init; }
    public required CompatibilityReport Report { get; init; }
    public GameFilterState GameFilter { get; init; } = GameFilterState.Off;
    public IpSetMode IpSet { get; init; } = IpSetMode.Any;
    public DateTimeOffset InspectedUtc { get; init; } = DateTimeOffset.UtcNow;

    public int SupportedStrategyCount => Strategies.Count(s => s.IsSupported);
}
