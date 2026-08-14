namespace Zapret.Core.AutoSelect;

/// <summary>What the monitor concluded after a round of probing.</summary>
public enum HealthVerdict
{
    /// <summary>Nothing to report; keep going.</summary>
    Steady,

    /// <summary>A sustained failure. This is the signal that starts an automatic repair.</summary>
    Degraded,

    /// <summary>Recovered on its own, without a repair. Worth telling the user, worth doing nothing about.</summary>
    Recovered,
}

/// <summary>
/// Decides when a failing service is a real problem rather than one unlucky request.
/// <para>
/// This is what lets the 2.0 product delete the manual test buttons: nobody has to ask whether things work,
/// because something is always asking. It is also why hysteresis matters — a monitor that reacted to every
/// dropped packet would restart the engine constantly and be worse than no monitor at all.
/// </para>
/// </summary>
public sealed class HealthMonitor(int failuresBeforeDegraded = 3, int successesBeforeRecovered = 2)
{
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private bool _degraded;

    /// <summary>Rounds a service must fail in a row before the product acts. Default three.</summary>
    public int FailuresBeforeDegraded { get; } = Math.Max(1, failuresBeforeDegraded);

    /// <summary>Rounds everything must pass before a degraded state is considered over. Default two.</summary>
    public int SuccessesBeforeRecovered { get; } = Math.Max(1, successesBeforeRecovered);

    public bool IsDegraded => _degraded;

    /// <summary>Services failing in the most recent round, in the order they were probed.</summary>
    public IReadOnlyList<string> Failing { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Feeds one round of results in and reports what, if anything, changed. Only a transition is reported:
    /// a monitor that keeps shouting "still broken" gives the caller nothing to act on.
    /// </summary>
    public HealthVerdict Observe(IReadOnlyList<ServiceVerdict> verdicts)
    {
        if (verdicts.Count == 0) return HealthVerdict.Steady;

        Failing = verdicts.Where(v => !v.Reachable).Select(v => v.ServiceId).ToList();

        if (Failing.Count > 0)
        {
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;

            if (_degraded || _consecutiveFailures < FailuresBeforeDegraded) return HealthVerdict.Steady;

            _degraded = true;
            return HealthVerdict.Degraded;
        }

        _consecutiveFailures = 0;
        _consecutiveSuccesses++;

        if (!_degraded || _consecutiveSuccesses < SuccessesBeforeRecovered) return HealthVerdict.Steady;

        _degraded = false;
        return HealthVerdict.Recovered;
    }

    /// <summary>
    /// Clears the history. Called after a repair, so the counters do not carry a verdict about the old
    /// strategy into the measurement of the new one.
    /// </summary>
    public void Reset()
    {
        _consecutiveFailures = 0;
        _consecutiveSuccesses = 0;
        _degraded = false;
        Failing = Array.Empty<string>();
    }

    /// <summary>
    /// How often to probe. Deliberately unhurried: this runs for hours on someone's machine, and the cost of
    /// noticing a problem twenty seconds later is far lower than the cost of a product that heats a laptop.
    /// </summary>
    public static TimeSpan Interval { get; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A longer interval used while the window is hidden in the tray, where there is nobody to show a result
    /// to and only a real outage is worth waking up for.
    /// </summary>
    public static TimeSpan BackgroundInterval { get; } = TimeSpan.FromMinutes(2);
}
