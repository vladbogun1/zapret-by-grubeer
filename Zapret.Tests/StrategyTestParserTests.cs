using Zapret.Core.Testing;

namespace Zapret.Tests;

public sealed class StrategyTestParserTests
{
    /// <summary>
    /// Transcribed from a real run of upstream's <c>test zapret.ps1</c> on engine 1.10.1, including the
    /// ping-only DNS targets and the interactive preamble the manager has to skip past.
    /// </summary>
    private const string RealOutput = """
        [2] DPI checkers (TCP 16-20 freeze)
        Enter 1 or 2: 1

        Select test run mode:
         [1] All configs
         [2] Selected configs
        Enter 1 or 2: 1

        [INFO] Loaded targets from targets.txt
        [INFO] Targets loaded: 17

        ============================================================
                        ZAPRET CONFIG TESTS
                        Mode: STANDARD
                        Total configs: 21
        ============================================================
        [WARNING] Tests may take several minutes to complete. Please wait...

        ------------------------------------------------------------
         [1/21] general (ALT).bat
        ------------------------------------------------------------
         > Starting config...
         > Running tests...
        DiscordMain              HTTP:OK      TLS1.2:OK     TLS1.3:OK    | Ping: 10 ms
        DiscordGateway           HTTP:OK      TLS1.2:OK     TLS1.3:OK    | Ping: 12 ms
        YouTubeWeb               HTTP:OK      TLS1.2:OK     TLS1.3:OK    | Ping: 22 ms
        CloudflareDNS1111        Ping: 10 ms
        GoogleDNS8888            Ping: 25 ms

        ------------------------------------------------------------
         [2/21] general (ALT2).bat
        ------------------------------------------------------------
         > Starting config...
         > Running tests...
        DiscordMain              HTTP:OK      TLS1.2:FAIL   TLS1.3:OK    | Ping: 30 ms
        DiscordGateway           HTTP:FAIL    TLS1.2:FAIL   TLS1.3:FAIL
        YouTubeWeb               HTTP:OK      TLS1.2:OK     TLS1.3:OK    | Ping: 40 ms
        CloudflareDNS1111        Ping: 20 ms
        GoogleDNS8888            Ping: 26 ms
        """;

    [Fact]
    public void The_real_run_is_parsed_into_per_strategy_results()
    {
        var results = StrategyTestParser.Parse(RealOutput);

        Assert.Equal(2, results.Count);
        Assert.Equal(21, StrategyTestParser.ParseTotal(RealOutput));

        // The .bat suffix is dropped, so the id matches what discovery and the registry marker use.
        Assert.Equal("general (ALT)", results[0].StrategyId);
        Assert.Equal("general (ALT2)", results[1].StrategyId);
    }

    [Fact]
    public void A_target_passes_only_when_every_check_on_its_line_passed()
    {
        var results = StrategyTestParser.Parse(RealOutput);

        var first = results[0];
        Assert.Equal(5, first.TotalCount);
        Assert.Equal(5, first.PassedCount);
        Assert.Equal(100, first.SuccessPercent);

        var second = results[1];
        Assert.Equal(5, second.TotalCount);

        // DiscordMain has one FAIL and DiscordGateway failed outright; the rest passed.
        Assert.Equal(3, second.PassedCount);
        Assert.Equal(60, second.SuccessPercent);
        Assert.False(second.Targets.Single(t => t.Name == "DiscordMain").Passed);
        Assert.False(second.Targets.Single(t => t.Name == "DiscordGateway").Passed);
        Assert.True(second.Targets.Single(t => t.Name == "YouTubeWeb").Passed);
    }

    [Fact]
    public void Ping_only_targets_count_as_reachable_and_feed_the_average()
    {
        var results = StrategyTestParser.Parse(RealOutput);

        var dns = results[0].Targets.Single(t => t.Name == "CloudflareDNS1111");
        Assert.True(dns.Passed);
        Assert.Equal(10, dns.PingMilliseconds);

        // (10 + 12 + 22 + 10 + 25) / 5
        Assert.Equal(16, results[0].AveragePing);
    }

    [Fact]
    public void A_failed_target_without_a_ping_does_not_distort_the_average()
    {
        var results = StrategyTestParser.Parse(RealOutput);

        // Only passing targets with a ping contribute: (40 + 20 + 26) / 3
        Assert.Equal(29, results[1].AveragePing);
    }

    [Fact]
    public void Ranking_prefers_success_then_latency_then_upstream_order()
    {
        var results = new[]
        {
            new StrategyTestResult { StrategyId = "a", Targets = [new("t", true, 100), new("u", false, null)] },
            new StrategyTestResult { StrategyId = "b", Targets = [new("t", true, 50), new("u", true, 50)] },
            new StrategyTestResult { StrategyId = "c", Targets = [new("t", true, 10), new("u", true, 10)] },
            new StrategyTestResult { StrategyId = "d", Targets = [new("t", true, 10), new("u", true, 10)] },
        };

        var ranked = StrategyTestParser.Rank(results);

        Assert.Equal(["c", "d", "b", "a"], ranked.Select(r => r.StrategyId));
        Assert.Equal("c", StrategyTestParser.SelectBest(results)!.StrategyId);
    }

    [Fact]
    public void A_run_where_nothing_passed_yields_no_recommendation()
    {
        var results = new[]
        {
            new StrategyTestResult { StrategyId = "a", Targets = [new("t", false, null)] },
            new StrategyTestResult { StrategyId = "b", Targets = [] },
        };

        Assert.Null(StrategyTestParser.SelectBest(results));
    }

    /// <summary>A run cut short mid-strategy must still yield the strategies that completed.</summary>
    [Fact]
    public void A_truncated_run_keeps_what_finished()
    {
        const string truncated = """
             [1/21] general (ALT).bat
            DiscordMain              HTTP:OK      TLS1.2:OK     TLS1.3:OK    | Ping: 10 ms

             [2/21] general (ALT2).bat
             > Starting config...
            """;

        var results = StrategyTestParser.Parse(truncated);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, results[0].TotalCount);
        Assert.Equal(0, results[1].TotalCount);
        Assert.Equal(0, results[1].SuccessPercent);
    }

    /// <summary>
    /// Upstream is free to rename or add a check column. An unfamiliar label must still be counted through
    /// the generic OK/FAIL match rather than silently dropping the target.
    /// </summary>
    [Fact]
    public void An_unfamiliar_check_column_is_still_counted()
    {
        const string future = """
             [1/1] general (NEW).bat
            SomeTarget    HTTP:OK    TLS1.3:OK    QUIC:OK    HTTP3:FAIL   | Ping: 15 ms
            """;

        var results = StrategyTestParser.Parse(future);

        Assert.Single(results);
        Assert.Equal(1, results[0].TotalCount);
        Assert.Equal(0, results[0].PassedCount);
    }
}
