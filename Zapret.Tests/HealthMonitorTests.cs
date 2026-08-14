using Zapret.Core.AutoSelect;

namespace Zapret.Tests;

public sealed class HealthMonitorTests
{
    private static IReadOnlyList<ServiceVerdict> Round(params (string Id, bool Ok)[] results) =>
        results.Select(r => new ServiceVerdict(r.Id, r.Ok, r.Ok ? 40 : null, DateTimeOffset.UtcNow)).ToList();

    private static IReadOnlyList<ServiceVerdict> AllOk => Round(("Discord", true), ("YouTube", true));

    private static IReadOnlyList<ServiceVerdict> DiscordDown => Round(("Discord", false), ("YouTube", true));

    /// <summary>
    /// The whole reason hysteresis exists: one dropped request must not restart the engine, or the product
    /// spends its life repairing itself.
    /// </summary>
    [Fact]
    public void One_failed_round_is_not_a_problem()
    {
        var monitor = new HealthMonitor();

        Assert.Equal(HealthVerdict.Steady, monitor.Observe(DiscordDown));
        Assert.False(monitor.IsDegraded);
    }

    [Fact]
    public void A_sustained_failure_becomes_degraded_exactly_once()
    {
        var monitor = new HealthMonitor(failuresBeforeDegraded: 3);

        Assert.Equal(HealthVerdict.Steady, monitor.Observe(DiscordDown));
        Assert.Equal(HealthVerdict.Steady, monitor.Observe(DiscordDown));
        Assert.Equal(HealthVerdict.Degraded, monitor.Observe(DiscordDown));

        // Already degraded: no more signals, because the caller has nothing new to act on.
        Assert.Equal(HealthVerdict.Steady, monitor.Observe(DiscordDown));
        Assert.True(monitor.IsDegraded);
        Assert.Equal(["Discord"], monitor.Failing);
    }

    [Fact]
    public void An_intermittent_failure_never_reaches_degraded()
    {
        var monitor = new HealthMonitor(failuresBeforeDegraded: 3);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HealthVerdict.Steady, monitor.Observe(DiscordDown));
            Assert.Equal(HealthVerdict.Steady, monitor.Observe(AllOk));
        }

        Assert.False(monitor.IsDegraded);
    }

    [Fact]
    public void Recovering_without_a_repair_is_reported_once()
    {
        var monitor = new HealthMonitor(failuresBeforeDegraded: 2, successesBeforeRecovered: 2);

        monitor.Observe(DiscordDown);
        Assert.Equal(HealthVerdict.Degraded, monitor.Observe(DiscordDown));

        Assert.Equal(HealthVerdict.Steady, monitor.Observe(AllOk));
        Assert.Equal(HealthVerdict.Recovered, monitor.Observe(AllOk));
        Assert.Equal(HealthVerdict.Steady, monitor.Observe(AllOk));
        Assert.False(monitor.IsDegraded);
    }

    /// <summary>
    /// After a repair the counters must not carry a verdict about the previous strategy into the measurement of
    /// the new one.
    /// </summary>
    [Fact]
    public void Reset_clears_the_history()
    {
        var monitor = new HealthMonitor(failuresBeforeDegraded: 2);

        monitor.Observe(DiscordDown);
        monitor.Observe(DiscordDown);
        Assert.True(monitor.IsDegraded);

        monitor.Reset();

        Assert.False(monitor.IsDegraded);
        Assert.Empty(monitor.Failing);
        Assert.Equal(HealthVerdict.Steady, monitor.Observe(DiscordDown));
    }

    [Fact]
    public void Watching_nothing_reports_nothing()
    {
        var monitor = new HealthMonitor();

        Assert.Equal(HealthVerdict.Steady, monitor.Observe([]));
        Assert.False(monitor.IsDegraded);
    }

    [Fact]
    public void Failing_services_are_named_so_a_repair_can_target_them()
    {
        var monitor = new HealthMonitor(failuresBeforeDegraded: 1);

        monitor.Observe(Round(("Discord", false), ("YouTube", false), ("Telegram", true)));

        Assert.Equal(["Discord", "YouTube"], monitor.Failing);
    }

    /// <summary>
    /// The product may be open for hours. Probing must be unhurried enough not to matter, and rarer still when
    /// the window is hidden and there is nobody to show a result to.
    /// </summary>
    [Fact]
    public void Probing_is_unhurried_and_rarer_in_the_background()
    {
        Assert.True(HealthMonitor.Interval >= TimeSpan.FromSeconds(10));
        Assert.True(HealthMonitor.BackgroundInterval > HealthMonitor.Interval);
    }

    [Fact]
    public void Thresholds_cannot_be_configured_below_one()
    {
        var monitor = new HealthMonitor(failuresBeforeDegraded: 0, successesBeforeRecovered: -5);

        Assert.Equal(1, monitor.FailuresBeforeDegraded);
        Assert.Equal(1, monitor.SuccessesBeforeRecovered);
        Assert.Equal(HealthVerdict.Degraded, monitor.Observe(DiscordDown));
    }
}
