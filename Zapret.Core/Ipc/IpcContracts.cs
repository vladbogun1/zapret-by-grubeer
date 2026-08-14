using System.Text.Json;
using System.Text.Json.Serialization;
using Zapret.Core.Engine;
using Zapret.Core.Model;

namespace Zapret.Core.Ipc;

/// <summary>
/// Operation names understood by the privileged service. Clients name intents; the service resolves
/// them against its own state. No client ever supplies a path, executable or URL — ADR-0002.
/// </summary>
public static class IpcOperations
{
    public const string GetStatus = "get-status";
    public const string ListStrategies = "list-strategies";
    public const string GetUserList = "get-user-list";
    public const string GetLogTail = "get-log-tail";
    public const string GetEvents = "get-events";

    /// <summary>Network reads only, so any signed-in user may run it.</summary>
    public const string ProbeServices = "probe-services";

    public const string StartEngine = "start-engine";
    public const string StopEngine = "stop-engine";
    public const string ApplyStrategy = "apply-strategy";
    public const string SetRunMode = "set-run-mode";
    public const string SetAutostart = "set-autostart";
    public const string SetGameFilter = "set-game-filter";
    public const string SetIpSetMode = "set-ipset-mode";
    public const string SaveUserList = "save-user-list";
    public const string InstallEngine = "install-engine";
    public const string RollBackEngine = "rollback-engine";
    public const string UpdateIpSetList = "update-ipset-list";
    public const string ApplyManagedHosts = "apply-managed-hosts";
    public const string RemoveManagedHosts = "remove-managed-hosts";
    public const string RunStrategyTests = "run-strategy-tests";

    /// <summary>Full sweep of every discovered strategy, driven through upstream's own test utility.</summary>
    public const string RunFullTest = "run-full-test";

    /// <summary>Applies the strategy the last sweep ranked first.</summary>
    public const string ApplyBestStrategy = "apply-best-strategy";

    public const string GetTestResults = "get-test-results";

    /// <summary>
    /// Operations that change machine state. These require a caller in the local Administrators
    /// group; everything else is readable by any signed-in user.
    /// </summary>
    public static readonly IReadOnlySet<string> Mutating = new HashSet<string>(StringComparer.Ordinal)
    {
        StartEngine, StopEngine, ApplyStrategy, SetRunMode, SetAutostart, SetGameFilter, SetIpSetMode,
        SaveUserList, InstallEngine, RollBackEngine, UpdateIpSetList,
        ApplyManagedHosts, RemoveManagedHosts, RunStrategyTests, RunFullTest, ApplyBestStrategy,
    };

    public static bool RequiresAdministrator(string operation) => Mutating.Contains(operation);
}

public sealed record IpcRequest
{
    /// <summary>Bumped when the payload shape changes, so a mismatched pair says so instead of misbehaving.</summary>
    public const int CurrentProtocolVersion = 1;

    [JsonPropertyName("v")] public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
    [JsonPropertyName("op")] public string Operation { get; init; } = string.Empty;
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
}

public sealed record IpcResponse
{
    [JsonPropertyName("v")] public int ProtocolVersion { get; init; } = IpcRequest.CurrentProtocolVersion;
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("code")] public IpcErrorCode Code { get; init; } = IpcErrorCode.None;
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }

    public static IpcResponse Success(JsonElement? payload = null) => new() { Ok = true, Payload = payload };

    public static IpcResponse Failure(IpcErrorCode code, string error) => new() { Ok = false, Code = code, Error = error };
}

public enum IpcErrorCode
{
    None,
    Unauthorized,
    UnknownOperation,
    ProtocolMismatch,
    InvalidPayload,
    NoEngineInstalled,
    CapabilityUnavailable,
    OperationFailed,
    ServiceUnavailable,
}

// ---- payloads -------------------------------------------------------------------------------

public sealed record StatusPayload
{
    public required EngineStatus EngineStatus { get; init; }
    public string? EngineVersion { get; init; }
    public string? EngineVersionSource { get; init; }
    public string? ManagerVersion { get; init; }
    public string? StrategyId { get; init; }
    public string? StrategyDisplayName { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public string? LastError { get; init; }
    public EngineRunMode RunMode { get; init; }
    public bool StartEngineWithWindows { get; init; }
    public GameFilterMode GameFilter { get; init; }
    public IpSetMode IpSet { get; init; }
    public UpstreamCapabilities Capabilities { get; init; } = UpstreamCapabilities.None;
    public CompatibilityOutcome? CompatibilityOutcome { get; init; }
    public IReadOnlyList<string> CompatibilityNotes { get; init; } = Array.Empty<string>();
    public bool ManagedHostsApplied { get; init; }
    public int SupportedStrategyCount { get; init; }
    public bool IsElevatedCaller { get; init; }

    /// <summary>Localisation key for the connection kind. The service never sends display text.</summary>
    public string? NetworkKindKey { get; init; }

    /// <summary>One-way fingerprint of the current connection, used to remember a strategy per network.</summary>
    public string? NetworkId { get; init; }

    public string? NetworkAdapter { get; init; }

    /// <summary>Strategy remembered for this connection, if any.</summary>
    public string? NetworkStrategyId { get; init; }
}

public sealed record StrategyResultItem(
    string StrategyId,
    string DisplayName,
    int SuccessPercent,
    int? AveragePing,
    int Passed,
    int Total,
    bool IsBest);

public sealed record TestResultsPayload
{
    public DateTimeOffset? CompletedUtc { get; init; }
    public string? EngineVersion { get; init; }
    public string? NetworkId { get; init; }
    public IReadOnlyList<StrategyResultItem> Items { get; init; } = Array.Empty<StrategyResultItem>();

    /// <summary>True when the stored sweep was measured on the engine and network in use right now.</summary>
    public bool IsCurrent { get; init; }
}

public sealed record StrategyPayload
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsSupported { get; init; }
    public string? UnsupportedReason { get; init; }
    public bool IsSelected { get; init; }
    public int ArgumentCount { get; init; }
}

public sealed record StrategyListPayload
{
    public IReadOnlyList<StrategyPayload> Strategies { get; init; } = Array.Empty<StrategyPayload>();
    public string? EngineVersion { get; init; }
}

public sealed record IdPayload(string Id);

public sealed record RunModePayload(EngineRunMode Mode);

public sealed record AutostartPayload(bool Enabled);

public sealed record GameFilterPayload(GameFilterMode Mode);

public sealed record IpSetPayload(IpSetMode Mode);

public sealed record UserListPayload(string Name, string? Content = null);

public sealed record LogTailPayload(string Source, int Lines = 200, string? Content = null);

/// <summary>The client asks for a tag it saw in a release feed; the service re-resolves it itself.</summary>
public sealed record InstallEnginePayload(string Tag, bool AllowPreview = false);

public sealed record EngineUpdatePayload
{
    public required bool Success { get; init; }
    public string? FailedStep { get; init; }
    public string? Error { get; init; }
    public bool RolledBack { get; init; }
    public string? PreviousVersion { get; init; }
    public string? ActiveVersion { get; init; }
    public int StrategyCount { get; init; }
    public string? SelectedStrategyId { get; init; }
    public string? StrategyMessage { get; init; }
    public bool EngineRunning { get; init; }
    public IReadOnlyDictionary<string, bool>? TargetResults { get; init; }
    public IReadOnlyList<string> CompatibilityNotes { get; init; } = Array.Empty<string>();
}

public sealed record OperationResultPayload(bool Success, string? Message = null);

public sealed record ServiceProbeItem(string Name, bool Reachable, int? Milliseconds);

public sealed record ServiceProbePayload
{
    public IReadOnlyList<ServiceProbeItem> Items { get; init; } = Array.Empty<ServiceProbeItem>();
    public DateTimeOffset? CheckedUtc { get; init; }

    public int ReachableCount => Items.Count(i => i.Reachable);

    /// <summary>Average latency across reachable services, or null when nothing answered.</summary>
    public int? AverageMilliseconds
    {
        get
        {
            var values = Items.Where(i => i is { Reachable: true, Milliseconds: not null }).Select(i => i.Milliseconds!.Value).ToList();
            return values.Count == 0 ? null : (int)Math.Round(values.Average());
        }
    }
}

public sealed record EventItem(DateTimeOffset Utc, ManagerEventLevel Level, string MessageKey, string? Argument);

public sealed record EventsPayload
{
    public IReadOnlyList<EventItem> Items { get; init; } = Array.Empty<EventItem>();
}
