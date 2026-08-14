using Zapret.Core;
using Zapret.Core.Flowseal;
using Zapret.Core.Model;

namespace Zapret.Tests;

public sealed class FlowsealAdapterTests
{
    private static readonly FlowsealAdapter Adapter = new();

    [Fact]
    public void A_complete_reference_build_is_compatible_and_fully_discovered()
    {
        using var fixture = RuntimeFixture.CreateComplete();

        var info = Adapter.Inspect(fixture.Root);

        Assert.Equal(CompatibilityOutcome.Compatible, info.Report.Outcome);
        Assert.Equal("1.10.1", info.Version.Raw);
        Assert.Equal(EngineVersionSource.ServiceVersionFile, info.Version.Source);
        Assert.Equal(21, info.Strategies.Count);
        Assert.Equal(21, info.SupportedStrategyCount);
        Assert.DoesNotContain(info.Strategies, s => s.Id.StartsWith("service", StringComparison.OrdinalIgnoreCase));

        var capabilities = info.Capabilities;
        Assert.True(capabilities.SupportsUpstreamServiceMode);
        Assert.True(capabilities.SupportsGameFilter);
        Assert.True(capabilities.SupportsIpSetFilter);
        Assert.True(capabilities.SupportsIpSetUpdate);
        Assert.True(capabilities.SupportsHostsUpdater);
        Assert.True(capabilities.SupportsUserDomainLists);
        Assert.True(capabilities.SupportsStrategyTests);
        Assert.True(capabilities.SupportsFakeReplacement);
    }

    /// <summary>
    /// The expected order is ground truth: it is the output of upstream's own
    /// <c>Get-ChildItem … | Sort-Object { Regex.Replace($_.Name, '(\d+)', PadLeft(8,'0')) }</c>
    /// run against these very fixtures, including the fact that plain <c>general.bat</c> sorts last.
    /// </summary>
    [Fact]
    public void Strategies_are_listed_in_upstream_order()
    {
        using var fixture = RuntimeFixture.CreateComplete();

        var ids = Adapter.Inspect(fixture.Root).Strategies.Select(s => s.Id).ToArray();

        Assert.Equal(
        [
            "general (ALT)",
            "general (ALT2)",
            "general (ALT3)",
            "general (ALT4)",
            "general (ALT5)",
            "general (ALT6)",
            "general (ALT7)",
            "general (ALT8)",
            "general (ALT9)",
            "general (ALT10)",
            "general (ALT11)",
            "general (ALT12)",
            "general (EXP)",
            "general (FAKE TLS AUTO ALT)",
            "general (FAKE TLS AUTO ALT2)",
            "general (FAKE TLS AUTO ALT3)",
            "general (FAKE TLS AUTO)",
            "general (SIMPLE FAKE ALT)",
            "general (SIMPLE FAKE ALT2)",
            "general (SIMPLE FAKE)",
            "general",
        ], ids);
    }

    [Fact]
    public void A_build_without_the_engine_is_incompatible_and_says_why()
    {
        using var fixture = RuntimeFixture.CreateComplete();
        File.Delete(UpstreamLayout.EngineExecutable(fixture.Root));

        var info = Adapter.Inspect(fixture.Root);

        Assert.Equal(CompatibilityOutcome.Incompatible, info.Report.Outcome);
        Assert.False(info.Report.CanActivate);
        Assert.Contains(info.Report.Blockers, c => c.Name == "winws");
    }

    [Fact]
    public void A_build_without_strategies_is_incompatible()
    {
        using var fixture = RuntimeFixture.CreateComplete();
        foreach (var bat in Directory.EnumerateFiles(fixture.Root, "*.bat"))
        {
            if (UpstreamLayout.IsStrategyFile(Path.GetFileName(bat))) File.Delete(bat);
        }

        var info = Adapter.Inspect(fixture.Root);

        Assert.Equal(CompatibilityOutcome.Incompatible, info.Report.Outcome);
        Assert.Contains(info.Report.Blockers, c => c.Name == "strategies");
    }

    /// <summary>
    /// The scenario the whole compatibility layer exists for: an unfamiliar future release with more
    /// strategies, an unknown extra file, and one optional utility gone.
    /// </summary>
    [Fact]
    public void An_unknown_future_build_validates_with_limitations_and_picks_up_new_strategies()
    {
        using var fixture = RuntimeFixture.CreateComplete();

        File.Copy(
            Path.Combine(fixture.Root, "general.bat"),
            Path.Combine(fixture.Root, "general (ISP-RU).bat"));
        File.Copy(
            Path.Combine(fixture.Root, "general.bat"),
            Path.Combine(fixture.Root, "general (ALT20).bat"));
        File.WriteAllText(Path.Combine(UpstreamLayout.Utils(fixture.Root), "brand_new_upstream_toggle.enabled"), "ENABLED");
        File.Delete(UpstreamLayout.TestScript(fixture.Root));
        File.WriteAllText(UpstreamLayout.VersionFile(fixture.Root), "1.11.0\n");

        var info = Adapter.Inspect(fixture.Root);

        Assert.Equal(CompatibilityOutcome.CompatibleWithLimitations, info.Report.Outcome);
        Assert.True(info.Report.CanActivate);
        Assert.Equal("1.11.0", info.Version.Raw);
        Assert.Equal(23, info.SupportedStrategyCount);
        Assert.Contains(info.Strategies, s => s.DisplayName == "ISP-RU");
        Assert.Contains(info.Strategies, s => s.DisplayName == "ALT20");
        Assert.False(info.Capabilities.SupportsStrategyTests);
        Assert.Contains(info.Report.Limitations, c => c.Name == "test utility");
    }

    [Fact]
    public void A_strategy_pointing_at_a_missing_file_is_disabled_without_affecting_the_others()
    {
        using var fixture = RuntimeFixture.CreateComplete();
        File.Delete(Path.Combine(UpstreamLayout.Bin(fixture.Root), "tls_clienthello_www_google_com.bin"));

        var info = Adapter.Inspect(fixture.Root);

        Assert.True(info.Report.CanActivate);
        Assert.Contains(info.Strategies, s => !s.IsSupported);
        Assert.Contains(info.Strategies, s => s.IsSupported);

        var broken = info.Strategies.First(s => !s.IsSupported);
        Assert.Contains("tls_clienthello_www_google_com.bin", broken.UnsupportedReason);
    }

    [Fact]
    public void Version_sources_that_disagree_are_reported_but_not_fatal()
    {
        using var fixture = RuntimeFixture.CreateComplete();
        File.WriteAllText(UpstreamLayout.VersionFile(fixture.Root), "9.9.9\n");

        var info = Adapter.Inspect(fixture.Root);

        Assert.True(info.Report.CanActivate);
        Assert.Equal("9.9.9", info.Version.Raw);
        Assert.Contains(info.Report.Checks, c => c.Name == "engine version" && c.Detail.Contains("disagree"));
    }

    [Fact]
    public void Missing_user_lists_are_created_with_upstream_placeholder_content()
    {
        using var fixture = RuntimeFixture.CreateComplete();
        foreach (var name in UpstreamLayout.UserLists.Keys)
        {
            var path = Path.Combine(UpstreamLayout.Lists(fixture.Root), name);
            if (File.Exists(path)) File.Delete(path);
        }

        Adapter.EnsureUserLists(fixture.Root);

        foreach (var (name, expected) in UpstreamLayout.UserLists)
        {
            var path = Path.Combine(UpstreamLayout.Lists(fixture.Root), name);
            Assert.True(File.Exists(path), $"{name} was not created");
            Assert.Equal(expected, File.ReadAllText(path));
        }
    }

    [Fact]
    public void Game_filter_round_trips_through_the_upstream_flag_file()
    {
        using var fixture = RuntimeFixture.CreateComplete();

        Assert.Equal(GameFilterMode.Off, Adapter.ReadGameFilter(fixture.Root).Mode);

        foreach (var mode in new[] { GameFilterMode.All, GameFilterMode.TcpOnly, GameFilterMode.UdpOnly })
        {
            Adapter.WriteGameFilter(fixture.Root, new GameFilterState(mode));
            Assert.Equal(mode, Adapter.ReadGameFilter(fixture.Root).Mode);
        }

        Adapter.WriteGameFilter(fixture.Root, GameFilterState.Off);
        Assert.False(File.Exists(UpstreamLayout.GameFilterFlag(fixture.Root)));
        Assert.Equal(GameFilterMode.Off, Adapter.ReadGameFilter(fixture.Root).Mode);
    }
}

public sealed class UpstreamStateTests
{
    [Theory]
    [InlineData(null, IpSetMode.Any)]
    [InlineData("", IpSetMode.Any)]
    [InlineData("203.0.113.113/32\n", IpSetMode.None)]
    [InlineData("1.2.3.0/24\n4.5.6.0/24\n", IpSetMode.Loaded)]
    public void IpSet_mode_is_detected_from_content(string? content, IpSetMode expected) =>
        Assert.Equal(expected, IpSetState.Detect(content));

    [Theory]
    [InlineData(null, GameFilterMode.Off)]
    [InlineData("all", GameFilterMode.All)]
    [InlineData("ALL", GameFilterMode.All)]
    [InlineData("tcp", GameFilterMode.TcpOnly)]
    [InlineData("udp", GameFilterMode.UdpOnly)]
    [InlineData("", GameFilterMode.UdpOnly)]
    [InlineData("something upstream invents later", GameFilterMode.UdpOnly)]
    public void Game_filter_flag_content_maps_the_way_upstream_maps_it(string? content, GameFilterMode expected) =>
        Assert.Equal(expected, GameFilterState.FromFlagFile(content).Mode);

    [Fact]
    public void Natural_sort_matches_upstream_padding()
    {
        var names = new[] { "general (ALT10)", "general (ALT2)", "general", "general (ALT)" };
        Array.Sort(names, NaturalNameComparer.Instance);

        Assert.Equal(["general", "general (ALT)", "general (ALT2)", "general (ALT10)"], names);
    }

    [Theory]
    [InlineData("1.10.2", "1.10.1", 1)]
    [InlineData("1.10.1", "1.10.1", 0)]
    [InlineData("1.9.9", "1.10.0", -1)]
    [InlineData("v1.11", "1.10.9", 1)]
    [InlineData("1.11.0", "1.11.0-rc1", 1)]
    [InlineData("2.0", "1.99.99", 1)]
    [InlineData("1.10", "1.10.0", 0)]
    public void Engine_versions_compare_leniently(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(EngineVersion.Compare(left, right)));

    [Fact]
    public void Unknown_versions_never_look_newer() =>
        Assert.False(EngineVersion.IsNewer(null, "1.10.1"));

    [Theory]
    [InlineData(@"C:\ProgramData\ZapretByGrubeer\runtime", true)]
    [InlineData(@"D:\Programs\Zapret by Grubeer", true)]
    [InlineData(@"C:\Program Files\Запрет by Grubeer", false)]
    [InlineData(@"C:\games\zapret (new)", false)]
    public void Engine_paths_reject_what_upstream_cannot_handle(string path, bool expected) =>
        Assert.Equal(expected, EnginePathGuard.IsSafeForEngine(path, out _));
}
