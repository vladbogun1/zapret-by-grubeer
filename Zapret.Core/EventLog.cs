namespace Zapret.Core;

public enum ManagerEventLevel
{
    Information,
    Success,
    Warning,
    Error,
}

/// <summary>
/// One thing that actually happened. <see cref="MessageKey"/> is a stable identifier the UI localises;
/// <see cref="Argument"/> carries the only variable part (a strategy name, a version). Nothing here is a
/// display string, because the service has no business deciding the user's language.
/// </summary>
public sealed record ManagerEvent(DateTimeOffset Utc, ManagerEventLevel Level, string MessageKey, string? Argument = null);

/// <summary>
/// A bounded in-memory history of real events, shown on the dashboard. Deliberately not persisted and
/// deliberately never synthesised: an empty list means nothing has happened yet, and the UI says so.
/// </summary>
public sealed class ManagerEventLog(int capacity = 100)
{
    private readonly LinkedList<ManagerEvent> _events = new();
    private readonly object _gate = new();

    public void Add(ManagerEventLevel level, string messageKey, string? argument = null)
    {
        lock (_gate)
        {
            _events.AddFirst(new ManagerEvent(DateTimeOffset.UtcNow, level, messageKey, argument));

            while (_events.Count > capacity) _events.RemoveLast();
        }
    }

    /// <summary>Newest first, at most <paramref name="count"/> entries.</summary>
    public IReadOnlyList<ManagerEvent> Snapshot(int count = 20)
    {
        lock (_gate)
        {
            return _events.Take(Math.Clamp(count, 1, capacity)).ToList();
        }
    }
}

/// <summary>Event identifiers. Kept as constants so the UI's translation table cannot drift silently.</summary>
public static class ManagerEvents
{
    public const string EngineStarted = "event.engine.started";
    public const string EngineStopped = "event.engine.stopped";
    public const string EngineFaulted = "event.engine.faulted";
    public const string StrategyApplied = "event.strategy.applied";
    public const string StrategyUnavailable = "event.strategy.unavailable";
    public const string EngineInstalled = "event.engine.installed";
    public const string EngineUpdated = "event.engine.updated";
    public const string EngineUpdateFailed = "event.engine.updateFailed";
    public const string EngineRolledBack = "event.engine.rolledBack";
    public const string ServicesProbed = "event.services.probed";
    public const string TestsCompleted = "event.tests.completed";
    public const string HostsApplied = "event.hosts.applied";
    public const string HostsRemoved = "event.hosts.removed";
    public const string IpSetUpdated = "event.ipset.updated";
    public const string SettingChanged = "event.setting.changed";
}
