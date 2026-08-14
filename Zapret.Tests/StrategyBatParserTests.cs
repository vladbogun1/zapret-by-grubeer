using Zapret.Core.Flowseal;
using Zapret.Core.Model;

namespace Zapret.Tests;

public sealed class StrategyBatParserTests
{
    private const string Root = @"C:\ProgramData\ZapretByGrubeer\runtime\versions\1.10.1";

    private static StrategyParseContext Context(GameFilterMode mode = GameFilterMode.Off) =>
        new(Root, new GameFilterState(mode));

    private static string Bin(string name) => Path.Combine(Root, "bin", name);

    private static string List(string name) => Path.Combine(Root, "lists", name);

    /// <summary>
    /// Hand-reviewed golden for the real upstream <c>general.bat</c> of 1.10.1, asserted token by
    /// token. This is the contract of docs/flowseal-compatibility.md §4.2.
    /// </summary>
    [Fact]
    public void General_bat_parses_into_the_expected_argv()
    {
        var path = Path.Combine(RuntimeFixture.FixtureDirectory(), "general.bat");
        var strategy = StrategyBatParser.Parse(path, Context());

        Assert.Null(strategy.UnsupportedReason);

        string[] expected =
        [
            "--wf-tcp=80,443,2053,2083,2087,2096,8443,12",
            "--wf-udp=443,19294-19344,50000-50100,12",

            "--filter-udp=443",
            $"--hostlist={List("list-general.txt")}",
            $"--hostlist={List("list-general-user.txt")}",
            $"--hostlist-exclude={List("list-exclude.txt")}",
            $"--hostlist-exclude={List("list-exclude-user.txt")}",
            $"--ipset-exclude={List("ipset-exclude.txt")}",
            $"--ipset-exclude={List("ipset-exclude-user.txt")}",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=6",
            $"--dpi-desync-fake-quic={Bin("quic_initial_www_google_com.bin")}",
            "--new",

            "--filter-udp=19294-19344,50000-50100",
            "--filter-l7=discord,stun",
            "--dpi-desync=fake",
            $"--dpi-desync-fake-discord={Bin("ACTIVE_DISCORD_UDP.bin")}",
            $"--dpi-desync-fake-stun={Bin("ACTIVE_DISCORD_UDP.bin")}",
            "--dpi-desync-repeats=6",
            "--new",

            "--filter-tcp=2053,2083,2087,2096,8443",
            "--hostlist-domains=discord.media",
            "--dpi-desync=multisplit",
            "--dpi-desync-split-seqovl=681",
            "--dpi-desync-split-pos=1",
            $"--dpi-desync-split-seqovl-pattern={Bin("tls_clienthello_www_google_com.bin")}",
            "--new",

            "--filter-tcp=443",
            $"--hostlist={List("list-google.txt")}",
            "--ip-id=zero",
            "--dpi-desync=multisplit",
            "--dpi-desync-split-seqovl=681",
            "--dpi-desync-split-pos=1",
            $"--dpi-desync-split-seqovl-pattern={Bin("tls_clienthello_www_google_com.bin")}",
            "--new",

            "--filter-tcp=80,443",
            $"--hostlist={List("list-general.txt")}",
            $"--hostlist={List("list-general-user.txt")}",
            $"--hostlist-exclude={List("list-exclude.txt")}",
            $"--hostlist-exclude={List("list-exclude-user.txt")}",
            $"--ipset-exclude={List("ipset-exclude.txt")}",
            $"--ipset-exclude={List("ipset-exclude-user.txt")}",
            "--dpi-desync=multisplit",
            "--dpi-desync-split-seqovl=568",
            "--dpi-desync-split-pos=1",
            $"--dpi-desync-split-seqovl-pattern={Bin("tls_clienthello_4pda_to.bin")}",
            "--new",

            "--filter-udp=443",
            $"--ipset={List("ipset-all.txt")}",
            $"--hostlist-exclude={List("list-exclude.txt")}",
            $"--hostlist-exclude={List("list-exclude-user.txt")}",
            $"--ipset-exclude={List("ipset-exclude.txt")}",
            $"--ipset-exclude={List("ipset-exclude-user.txt")}",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=6",
            $"--dpi-desync-fake-quic={Bin("quic_initial_www_google_com.bin")}",
            "--new",

            "--filter-tcp=80,443,8443",
            $"--ipset={List("ipset-all.txt")}",
            $"--hostlist-exclude={List("list-exclude.txt")}",
            $"--hostlist-exclude={List("list-exclude-user.txt")}",
            $"--ipset-exclude={List("ipset-exclude.txt")}",
            $"--ipset-exclude={List("ipset-exclude-user.txt")}",
            "--dpi-desync=multisplit",
            "--dpi-desync-split-seqovl=568",
            "--dpi-desync-split-pos=1",
            $"--dpi-desync-split-seqovl-pattern={Bin("tls_clienthello_4pda_to.bin")}",
            "--new",

            "--filter-tcp=12",
            $"--ipset={List("ipset-all.txt")}",
            $"--ipset-exclude={List("ipset-exclude.txt")}",
            $"--ipset-exclude={List("ipset-exclude-user.txt")}",
            "--dpi-desync=multisplit",
            "--dpi-desync-any-protocol=1",
            "--dpi-desync-cutoff=n3",
            "--dpi-desync-split-seqovl=568",
            "--dpi-desync-split-pos=1",
            $"--dpi-desync-split-seqovl-pattern={Bin("tls_clienthello_4pda_to.bin")}",
            "--new",

            "--filter-udp=12",
            $"--ipset={List("ipset-all.txt")}",
            $"--ipset-exclude={List("ipset-exclude.txt")}",
            $"--ipset-exclude={List("ipset-exclude-user.txt")}",
            "--dpi-desync=fake",
            "--dpi-desync-repeats=12",
            "--dpi-desync-any-protocol=1",
            $"--dpi-desync-fake-unknown-udp={Bin("ACTIVE_GAME_UDP.bin")}",
            "--dpi-desync-cutoff=n2",
        ];

        Assert.Equal(expected, strategy.Arguments);
    }

    [Theory]
    [InlineData(GameFilterMode.Off, "12", "12")]
    [InlineData(GameFilterMode.All, "1024-65535", "1024-65535")]
    [InlineData(GameFilterMode.TcpOnly, "1024-65535", "12")]
    [InlineData(GameFilterMode.UdpOnly, "12", "1024-65535")]
    public void Game_filter_ports_expand_the_way_upstream_expands_them(GameFilterMode mode, string tcp, string udp)
    {
        var path = Path.Combine(RuntimeFixture.FixtureDirectory(), "general.bat");
        var strategy = StrategyBatParser.Parse(path, Context(mode));

        Assert.Contains($"--filter-tcp={tcp}", strategy.Arguments);
        Assert.Contains($"--filter-udp={udp}", strategy.Arguments);
        Assert.Contains($"--wf-tcp=80,443,2053,2083,2087,2096,8443,{tcp}", strategy.Arguments);
        Assert.Contains($"--wf-udp=443,19294-19344,50000-50100,{udp}", strategy.Arguments);
    }

    /// <summary>Every real strategy of the reference build must yield a usable argv.</summary>
    [Fact]
    public void All_reference_strategies_parse_cleanly()
    {
        var files = Directory
            .EnumerateFiles(RuntimeFixture.FixtureDirectory(), "*.bat")
            .Where(f => UpstreamLayout.IsStrategyFile(Path.GetFileName(f)))
            .ToList();

        Assert.Equal(21, files.Count);

        foreach (var file in files)
        {
            var strategy = StrategyBatParser.Parse(file, Context());

            Assert.Null(strategy.UnsupportedReason);
            Assert.NotEmpty(strategy.Arguments);
            Assert.All(strategy.Arguments, argument =>
            {
                Assert.DoesNotContain('%', argument);
                Assert.DoesNotContain('"', argument);
                Assert.NotEqual("^", argument);
            });
            Assert.All(strategy.ReferencedPaths, path => Assert.True(Path.IsPathRooted(path), $"{path} is not absolute"));
            Assert.NotEmpty(strategy.ReferencedPaths);
        }
    }

    [Fact]
    public void Service_bat_is_never_a_strategy()
    {
        Assert.False(UpstreamLayout.IsStrategyFile("service.bat"));
        Assert.False(UpstreamLayout.IsStrategyFile("service_extra.bat"));
        Assert.True(UpstreamLayout.IsStrategyFile("general.bat"));
        Assert.True(UpstreamLayout.IsStrategyFile("general (ALT12).bat"));
    }

    [Theory]
    [InlineData("general", "general")]
    [InlineData("general (ALT11)", "ALT11")]
    [InlineData("general (FAKE TLS AUTO ALT2)", "FAKE TLS AUTO ALT2")]
    [InlineData("general (ISP-RU)", "ISP-RU")]
    public void Display_names_come_from_the_parenthesised_variant(string id, string expected) =>
        Assert.Equal(expected, StrategyBatParser.ToDisplayName(id));

    [Fact]
    public void An_unknown_variable_makes_the_strategy_unsupported_and_names_it()
    {
        const string text = """
            @echo off
            set "BIN=%~dp0bin\"
            start "zapret" /min "%BIN%winws.exe" --wf-tcp=80 --something=%TotallyNewUpstreamVariable%
            """;

        var strategy = StrategyBatParser.ParseText(text, "future", @"C:\x\future.bat", Context());

        Assert.False(strategy.IsSupported);
        Assert.Contains("TotallyNewUpstreamVariable", strategy.UnsupportedReason);
    }

    [Fact]
    public void A_file_without_an_invocation_is_unsupported_rather_than_fatal()
    {
        const string text = """
            @echo off
            echo nothing to see here
            """;

        var strategy = StrategyBatParser.ParseText(text, "empty", @"C:\x\empty.bat", Context());

        Assert.False(strategy.IsSupported);
        Assert.Contains("winws.exe", strategy.UnsupportedReason);
    }

    [Fact]
    public void A_commented_out_invocation_is_ignored()
    {
        const string text = """
            @echo off
            :: start "zapret" /min "%~dp0bin\winws.exe" --old-and-disabled
            rem winws.exe --also-not-this
            set "BIN=%~dp0bin\"
            start "zapret" /min "%BIN%winws.exe" --wf-tcp=443
            """;

        var strategy = StrategyBatParser.ParseText(text, "commented", @"C:\x\commented.bat", Context());

        Assert.True(strategy.IsSupported);
        Assert.Equal(["--wf-tcp=443"], strategy.Arguments);
    }

    /// <summary>
    /// Commas must stay inside their token. Upstream's batch parser needs a state machine to achieve
    /// this; the manager gets it for free and must not regress into splitting on commas.
    /// </summary>
    [Fact]
    public void Commas_and_quoted_paths_survive_tokenisation()
    {
        const string text = """
            @echo off
            set "BIN=%~dp0bin\"
            set "LISTS=%~dp0lists\"
            start "zapret: %~n0" /min "%BIN%winws.exe" --filter-l7=discord,stun --hostlist="%LISTS%my list.txt" ^
            --ipset=@"%LISTS%ipset-all.txt" --new
            """;

        var strategy = StrategyBatParser.ParseText(text, "commas", @"C:\x\commas.bat", Context());

        Assert.Equal(
        [
            "--filter-l7=discord,stun",
            $"--hostlist={List("my list.txt")}",
            $"--ipset=@{List("ipset-all.txt")}",
            "--new",
        ], strategy.Arguments);
    }

    [Fact]
    public void Continuations_join_exactly_as_cmd_does()
    {
        const string text = """
            @echo off
            start "" /min "%~dp0bin\winws.exe" --first=1 ^
            --second=2 ^
            --third=3
            """;

        var strategy = StrategyBatParser.ParseText(text, "join", @"C:\x\join.bat", Context());

        Assert.Equal(["--first=1", "--second=2", "--third=3"], strategy.Arguments);
    }

    [Fact]
    public void Hostnames_are_not_mistaken_for_relative_paths()
    {
        const string text = """
            @echo off
            start "" /min "%~dp0bin\winws.exe" --hostlist-domains=discord.media --sni=www.google.com
            """;

        var strategy = StrategyBatParser.ParseText(text, "hosts", @"C:\x\hosts.bat", Context());

        Assert.Equal(["--hostlist-domains=discord.media", "--sni=www.google.com"], strategy.Arguments);
        Assert.Empty(strategy.ReferencedPaths);
    }
}
