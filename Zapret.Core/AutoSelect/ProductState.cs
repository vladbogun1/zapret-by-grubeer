namespace Zapret.Core.AutoSelect;

/// <summary>
/// The one state the whole interface is a function of. Named after the user's situation, not the engine's:
/// there is no "service stopped" or "strategy applied" here, because neither is something a user has an
/// opinion about (docs/nextgen-ux.md §3).
/// </summary>
public enum ProductStage
{
    /// <summary>Never configured. The only stage that asks the user anything up front.</summary>
    FirstRun,

    /// <summary>Installing, probing and selecting. Progress is real steps, not a fake bar.</summary>
    Preparing,

    /// <summary>The resting state: everything the user named is reachable.</summary>
    Working,

    /// <summary>Something the user named is failing. Visible only as the reason for Repairing.</summary>
    Degraded,

    /// <summary>Trying other candidates against the user's own services.</summary>
    Repairing,

    /// <summary>Every candidate failed. Asks for something a person can actually do.</summary>
    Stuck,

    /// <summary>The user turned it off. Nothing is broken.</summary>
    Off,

    /// <summary>The privileged service is unreachable, so the product cannot know or do anything.</summary>
    Unavailable,
}

/// <summary>Per-service verdict, as the user's own words would put it.</summary>
public sealed record ServiceVerdict(string ServiceId, bool Reachable, int? Milliseconds, DateTimeOffset CheckedUtc)
{
    /// <summary>Four-word explanation of the latency, or null when there is nothing to say.</summary>
    public string? SpeedKey => Milliseconds switch
    {
        null => null,
        < 120 => "speed.fast",
        < 350 => "speed.normal",
        _ => "speed.slow",
    };
}

/// <summary>One real step that happened, for the Preparing and Repairing stages.</summary>
public sealed record ProgressStep(string MessageKey, string? Argument, bool Done);

/// <summary>
/// The complete projection the service pushes to the interface. The service decides what the situation means;
/// the interface only renders it. This replaces the 1.x arrangement where the view assembled state from six
/// queries and drew its own conclusions (docs/nextgen-ux.md §8).
/// </summary>
public sealed record ProductState
{
    public required ProductStage Stage { get; init; }

    /// <summary>What the user chose to care about. Empty in FirstRun.</summary>
    public IReadOnlyList<string> WatchedServices { get; init; } = Array.Empty<string>();

    public IReadOnlyList<ServiceVerdict> Verdicts { get; init; } = Array.Empty<ServiceVerdict>();

    /// <summary>Steps of the work in progress, newest last. Empty when nothing is running.</summary>
    public IReadOnlyList<ProgressStep> Steps { get; init; } = Array.Empty<ProgressStep>();

    /// <summary>
    /// The strategy in use. Present so «Подробнее» can show it, and deliberately never required to render the
    /// main screen: a user must be able to use the product without ever seeing this.
    /// </summary>
    public string? StrategyId { get; init; }

    public string? EngineVersion { get; init; }

    public DateTimeOffset? RunningSinceUtc { get; init; }

    /// <summary>Whether the user's connection needs a bypass at all, once measured with it off.</summary>
    public bool? BypassNeeded { get; init; }

    /// <summary>What to tell the user in Stuck: a localisation key naming a next step they can take.</summary>
    public string? AdviceKey { get; init; }

    /// <summary>True while a repair can still be cancelled back to the last state that worked.</summary>
    public bool CanCancel { get; init; }

    public int FailingCount => Verdicts.Count(v => !v.Reachable);

    public bool AllWorking => Verdicts.Count > 0 && Verdicts.All(v => v.Reachable);

    public static ProductState Unreachable { get; } = new() { Stage = ProductStage.Unavailable };
}
