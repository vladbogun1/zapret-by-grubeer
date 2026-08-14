using Zapret.Core.Model;

namespace Zapret.Core.Engine;

public enum EngineRunMode
{
    /// <summary>The manager service launches and supervises winws.exe itself. Default.</summary>
    ManagedProcess,

    /// <summary>Upstream-compatible Windows service named <c>zapret</c>.</summary>
    WindowsService,
}

public enum EngineStatus
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

public sealed record EngineState(
    EngineStatus Status,
    string? StrategyId = null,
    string? Version = null,
    DateTimeOffset? StartedUtc = null,
    string? LastError = null)
{
    public static EngineState Stopped { get; } = new(EngineStatus.Stopped);
}

/// <summary>
/// Starting and stopping the engine, whichever run mode is in effect. Implemented by the privileged
/// service; faked in tests so the update transaction can be exercised without a driver.
/// </summary>
public interface IEngineController
{
    EngineState State { get; }

    Task<bool> StartAsync(EngineRuntimeInfo runtime, StrategyDescriptor strategy, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the engine is still running after the settle window. A build that starts and then
    /// dies is a failed update, not a successful one.
    /// </summary>
    Task<bool> IsHealthyAsync(TimeSpan settle, CancellationToken cancellationToken = default);
}

/// <summary>Reachability probe used for the post-update report. Never a rollback trigger on its own.</summary>
public interface ITargetProbe
{
    Task<IReadOnlyDictionary<string, bool>> ProbeAsync(CancellationToken cancellationToken = default);
}
