using System.IO.Compression;
using Zapret.Core.Engine;
using Zapret.Core.Flowseal;
using Zapret.Core.GitHub;
using Zapret.Core.Model;

namespace Zapret.Tests;

public sealed class EngineUpdateServiceTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "zapret-tests", "tx-" + Guid.NewGuid().ToString("n"));
    private readonly List<RuntimeFixture> _fixtures = new();

    private EnginePathOptions Paths => new(
        Path.Combine(_work, "runtime", "versions"),
        Path.Combine(_work, "runtime", "staging"),
        Path.Combine(_work, "runtime", "current.json"),
        Path.Combine(_work, "data", "lists"));

    // ---- fakes ------------------------------------------------------------------------------

    private sealed class FakeController : IEngineController
    {
        public EngineState State { get; private set; } = EngineState.Stopped;
        public bool FailStart { get; set; }
        public bool DieAfterStart { get; set; }
        public bool ThrowOnStop { get; set; }
        public List<string> StartedStrategies { get; } = new();
        public int StopCount { get; private set; }

        public Task<bool> StartAsync(EngineRuntimeInfo runtime, StrategyDescriptor strategy, CancellationToken cancellationToken = default)
        {
            if (FailStart)
            {
                State = EngineState.Stopped with { Status = EngineStatus.Faulted, LastError = "test failure" };
                return Task.FromResult(false);
            }

            StartedStrategies.Add(strategy.Id);
            State = new EngineState(EngineStatus.Running, strategy.Id, runtime.Version.Raw, DateTimeOffset.UtcNow);
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (ThrowOnStop) throw new InvalidOperationException("the engine refuses to stop");

            State = EngineState.Stopped;
            return Task.CompletedTask;
        }

        public Task<bool> IsHealthyAsync(TimeSpan settle, CancellationToken cancellationToken = default)
        {
            if (!DieAfterStart) return Task.FromResult(State.Status == EngineStatus.Running);

            State = EngineState.Stopped with { Status = EngineStatus.Faulted, LastError = "exited early" };
            return Task.FromResult(false);
        }
    }

    /// <summary>Serves a local zip instead of GitHub; the asset URL is a file path.</summary>
    private sealed class LocalReleaseClient : IGitHubReleaseClient
    {
        public bool FailDownload { get; set; }

        public Task<ReleaseCheckResult> CheckAsync(string repository, ReleaseFeedState state, string? installedVersion, bool allowPreview, CancellationToken cancellationToken = default) =>
            Task.FromResult(ReleaseCheckResult.Unavailable("not used in this test"));

        public Task<bool> DownloadAssetAsync(GitHubAsset asset, string destinationPath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (FailDownload) return Task.FromResult(false);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(asset.DownloadUrl, destinationPath, overwrite: true);
            progress?.Report(1.0);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeProbe(bool discord, bool youtube) : ITargetProbe
    {
        public Task<IReadOnlyDictionary<string, bool>> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, bool>>(new Dictionary<string, bool>
            {
                ["Discord"] = discord,
                ["YouTube"] = youtube,
            });
    }

    // ---- helpers ----------------------------------------------------------------------------

    /// <summary>Packs a complete engine build, optionally mutated, into a zip and returns the asset.</summary>
    private GitHubAsset BuildRelease(string version, Action<string>? mutate = null)
    {
        var fixture = RuntimeFixture.CreateComplete();
        _fixtures.Add(fixture);

        File.WriteAllText(UpstreamLayout.VersionFile(fixture.Root), version + "\n");
        mutate?.Invoke(fixture.Root);

        Directory.CreateDirectory(_work);
        var zip = Path.Combine(_work, $"engine-{version}-{Guid.NewGuid():n}.zip");
        ZipFile.CreateFromDirectory(fixture.Root, zip, CompressionLevel.NoCompression, includeBaseDirectory: false);

        return new GitHubAsset { Name = $"zapret-{version}.zip", DownloadUrl = zip, Size = new FileInfo(zip).Length };
    }

    private static GitHubRelease Release(string tag, GitHubAsset asset) => new()
    {
        Tag = tag,
        Name = tag,
        Assets = [asset],
    };

    private (EngineUpdateService Service, FakeController Controller, EngineStateStore Store) CreateService(
        LocalReleaseClient? client = null,
        ITargetProbe? probe = null)
    {
        var controller = new FakeController();
        var store = new EngineStateStore(Paths.StateFile);
        var service = new EngineUpdateService(
            Paths,
            new FlowsealAdapter(),
            store,
            controller,
            client ?? new LocalReleaseClient(),
            new ArchiveExtractor(),
            probe);

        return (service, controller, store);
    }

    // ---- tests ------------------------------------------------------------------------------

    [Fact]
    public async Task A_first_install_activates_the_build_but_selects_no_strategy_on_its_own()
    {
        var (service, controller, store) = CreateService();

        var outcome = await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));

        Assert.True(outcome.Success);
        Assert.Equal("1.10.1", outcome.ActiveVersion);
        Assert.Equal(StrategySelectionKind.NothingSelected, outcome.StrategySelection!.Kind);
        Assert.False(outcome.EngineRunning);
        Assert.Empty(controller.StartedStrategies);

        Assert.True(File.Exists(UpstreamLayout.EngineExecutable(Path.Combine(Paths.Versions, "1.10.1"))));
        Assert.Equal("1.10.1", store.Read().Current);
        Assert.Equal(21, store.Read().StrategyCount);
    }

    [Fact]
    public async Task An_update_reapplies_the_selected_strategy_and_keeps_one_previous_version()
    {
        var (service, controller, store) = CreateService(probe: new FakeProbe(true, true));

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        var outcome = await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        Assert.True(outcome.Success);
        Assert.Null(outcome.FailedStep);
        Assert.Equal("1.10.2", outcome.ActiveVersion);
        Assert.Equal("1.10.1", outcome.PreviousVersion);
        Assert.Equal(StrategySelectionKind.Reapplied, outcome.StrategySelection!.Kind);
        Assert.Equal("general (ALT11)", controller.StartedStrategies.Single());
        Assert.True(outcome.EngineRunning);
        Assert.Equal(new Dictionary<string, bool> { ["Discord"] = true, ["YouTube"] = true }, outcome.TargetResults);

        var state = store.Read();
        Assert.Equal("1.10.2", state.Current);
        Assert.Equal("1.10.1", state.Previous);
        Assert.True(Directory.Exists(Path.Combine(Paths.Versions, "1.10.1")), "the previous version must survive for rollback");
        Assert.Empty(Directory.EnumerateDirectories(Paths.Staging));
    }

    [Fact]
    public async Task Retention_keeps_only_the_current_and_previous_versions()
    {
        var (service, _, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });
        await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));
        await service.InstallAsync(Release("1.10.3", BuildRelease("1.10.3")));

        var kept = Directory.EnumerateDirectories(Paths.Versions)
            .Select(d => Path.GetFileName(d)!)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["1.10.2", "1.10.3"], kept);
    }

    [Fact]
    public async Task An_incompatible_candidate_never_touches_the_working_engine()
    {
        var (service, controller, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        var stopsBefore = controller.StopCount;

        var broken = BuildRelease("1.11.0", root => File.Delete(UpstreamLayout.EngineExecutable(root)));
        var outcome = await service.InstallAsync(Release("1.11.0", broken));

        Assert.False(outcome.Success);
        Assert.Equal(EngineUpdateStep.Validate, outcome.FailedStep);
        Assert.Equal("1.10.1", outcome.ActiveVersion);
        Assert.Equal(stopsBefore, controller.StopCount);
        Assert.False(Directory.Exists(Path.Combine(Paths.Versions, "1.11.0")));
        Assert.Equal("1.10.1", store.Read().Current);
        Assert.Contains("1.11.0", store.Read().RejectedTags);
        Assert.True(Directory.Exists(Path.Combine(Paths.Staging, "failed-1.11.0")), "a rejected candidate is kept once for diagnostics");
    }

    [Fact]
    public async Task An_engine_that_refuses_to_start_is_rolled_back_and_the_old_one_is_restarted()
    {
        var (service, controller, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        var newRuntime = adapterInspect(Path.Combine(Paths.Versions, "1.10.1"));
        await controller.StartAsync(newRuntime, newRuntime.Strategies.First(s => s.Id == "general (ALT11)"));
        controller.StartedStrategies.Clear();

        controller.FailStart = true;
        var outcome = await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        Assert.False(outcome.Success);
        Assert.Equal(EngineUpdateStep.Reapply, outcome.FailedStep);
        Assert.True(outcome.RolledBack);
        Assert.Equal("1.10.1", store.Read().Current);
        Assert.Equal("rollback", store.Read().ActivatedBy);
        Assert.Equal("general (ALT11)", store.Read().StrategyId);
    }

    [Fact]
    public async Task An_engine_that_dies_during_the_settle_window_is_rolled_back()
    {
        var (service, controller, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        controller.DieAfterStart = true;
        var outcome = await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        Assert.False(outcome.Success);
        Assert.Equal(EngineUpdateStep.HealthCheck, outcome.FailedStep);
        Assert.True(outcome.RolledBack);
        Assert.Equal("1.10.1", store.Read().Current);
    }

    [Fact]
    public async Task An_engine_that_cannot_be_stopped_aborts_the_update_without_activating_anything()
    {
        var (service, controller, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        controller.ThrowOnStop = true;
        var outcome = await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        Assert.False(outcome.Success);
        Assert.Equal(EngineUpdateStep.Stop, outcome.FailedStep);
        Assert.Equal("1.10.1", store.Read().Current);
        Assert.False(Directory.Exists(Path.Combine(Paths.Versions, "1.10.2")));
    }

    [Fact]
    public async Task A_vanished_strategy_is_reported_and_nothing_is_applied_silently()
    {
        var (service, controller, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        var trimmed = BuildRelease("1.11.0", root =>
        {
            foreach (var name in new[] { "general (ALT11).bat", "general (ALT12).bat", "general (ALT10).bat" })
            {
                File.Delete(Path.Combine(root, name));
            }
        });

        var outcome = await service.InstallAsync(Release("1.11.0", trimmed));

        Assert.True(outcome.Success);
        Assert.Equal("1.11.0", outcome.ActiveVersion);
        Assert.False(outcome.EngineRunning);
        Assert.Empty(controller.StartedStrategies);

        var selection = outcome.StrategySelection!;
        Assert.Equal(StrategySelectionKind.ReplacementProposed, selection.Kind);
        Assert.Equal("general (ALT11)", selection.PreviousId);
        Assert.Equal("general (ALT9)", selection.Strategy!.Id);
        Assert.Contains("no longer available", selection.Message);
    }

    [Fact]
    public async Task A_failed_download_leaves_everything_alone()
    {
        var client = new LocalReleaseClient();
        var (service, controller, store) = CreateService(client);

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        var stopsBefore = controller.StopCount;

        client.FailDownload = true;
        var outcome = await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        Assert.False(outcome.Success);
        Assert.Equal(EngineUpdateStep.Download, outcome.FailedStep);
        Assert.Equal(stopsBefore, controller.StopCount);
        Assert.Equal("1.10.1", store.Read().Current);
    }

    [Fact]
    public async Task A_release_without_a_zip_asset_fails_at_resolution()
    {
        var (service, _, _) = CreateService();

        var release = new GitHubRelease
        {
            Tag = "1.10.2",
            Assets = [new GitHubAsset { Name = "zapret-1.10.2.rar", DownloadUrl = "http://example.invalid/x.rar", Size = 1 }],
        };

        var outcome = await service.InstallAsync(release);

        Assert.False(outcome.Success);
        Assert.Equal(EngineUpdateStep.Resolve, outcome.FailedStep);
        Assert.Contains(".zip", outcome.Error);
    }

    [Fact]
    public async Task An_explicit_rollback_returns_to_the_previous_version()
    {
        var (service, controller, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });
        await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        var outcome = await service.RollBackToPreviousAsync();

        Assert.True(outcome.Success);
        Assert.True(outcome.RolledBack);
        Assert.Equal("1.10.1", store.Read().Current);
        Assert.Equal("general (ALT11)", controller.StartedStrategies.Last());
    }

    [Fact]
    public async Task User_lists_survive_an_engine_update()
    {
        var (service, _, store) = CreateService();

        Directory.CreateDirectory(Paths.DataLists);
        File.WriteAllText(Path.Combine(Paths.DataLists, "list-general-user.txt"), "my.custom.domain\r\n");

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });
        await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        var listPath = Path.Combine(UpstreamLayout.Lists(Path.Combine(Paths.Versions, "1.10.2")), "list-general-user.txt");

        Assert.Equal("my.custom.domain\r\n", File.ReadAllText(listPath));
    }

    [Fact]
    public async Task Upstream_toggles_carry_over_to_the_new_build()
    {
        var (service, _, store) = CreateService();

        await service.InstallAsync(Release("1.10.1", BuildRelease("1.10.1")));
        store.Write(store.Read() with { StrategyId = "general (ALT11)" });

        var adapter = new FlowsealAdapter();
        adapter.WriteGameFilter(Path.Combine(Paths.Versions, "1.10.1"), new GameFilterState(GameFilterMode.TcpOnly));

        await service.InstallAsync(Release("1.10.2", BuildRelease("1.10.2")));

        Assert.Equal(GameFilterMode.TcpOnly, adapter.ReadGameFilter(Path.Combine(Paths.Versions, "1.10.2")).Mode);
    }

    private static EngineRuntimeInfo adapterInspect(string directory) => new FlowsealAdapter().Inspect(directory);

    public void Dispose()
    {
        foreach (var fixture in _fixtures) fixture.Dispose();

        try
        {
            if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // Leaked temp files must never fail a test run.
        }
    }
}

public sealed class ArchiveExtractorTests
{
    [Fact]
    public void An_entry_escaping_the_target_directory_is_rejected()
    {
        var work = Path.Combine(Path.GetTempPath(), "zapret-tests", "zip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(work);

        try
        {
            var archive = Path.Combine(work, "evil.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("nope");
            }

            var result = new ArchiveExtractor().Extract(archive, Path.Combine(work, "out"));

            Assert.False(result.Success);
            Assert.Contains("outside the target directory", result.Error);
            Assert.False(File.Exists(Path.Combine(work, "escaped.txt")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void A_single_wrapper_folder_is_flattened()
    {
        var work = Path.Combine(Path.GetTempPath(), "zapret-tests", "zip-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(work);

        try
        {
            var source = Path.Combine(work, "src", "zapret-1.11.0", "bin");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "winws.exe"), string.Empty);
            File.WriteAllText(Path.Combine(work, "src", "zapret-1.11.0", "general.bat"), string.Empty);

            var archive = Path.Combine(work, "wrapped.zip");
            ZipFile.CreateFromDirectory(Path.Combine(work, "src"), archive);

            var destination = Path.Combine(work, "out");
            var result = new ArchiveExtractor().Extract(archive, destination);

            Assert.True(result.Success);
            Assert.Equal(destination, result.Directory);
            Assert.True(File.Exists(Path.Combine(destination, "general.bat")));
            Assert.True(File.Exists(Path.Combine(destination, "bin", "winws.exe")));
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}

public sealed class StrategyMatcherTests
{
    private static IReadOnlyList<StrategyDescriptor> Catalog(params string[] ids) =>
        ids.Select(id => new StrategyDescriptor
        {
            Id = id,
            DisplayName = StrategyBatParser.ToDisplayName(id),
            FilePath = id + ".bat",
            Arguments = ["--wf-tcp=443"],
        }).ToList();

    [Fact]
    public void An_existing_strategy_is_simply_reapplied()
    {
        var selection = StrategyMatcher.Select("general (ALT8)", Catalog("general", "general (ALT8)", "general (ALT9)"));

        Assert.Equal(StrategySelectionKind.Reapplied, selection.Kind);
        Assert.Equal("general (ALT8)", selection.Strategy!.Id);
    }

    [Fact]
    public void The_nearest_lower_variant_of_the_same_family_wins()
    {
        var selection = StrategyMatcher.Select("general (ALT11)", Catalog("general", "general (ALT8)", "general (ALT10)", "general (ALT20)"));

        Assert.Equal(StrategySelectionKind.ReplacementProposed, selection.Kind);
        Assert.Equal("general (ALT10)", selection.Strategy!.Id);
    }

    [Fact]
    public void A_family_that_disappeared_entirely_yields_no_recommendation()
    {
        var selection = StrategyMatcher.Select("general (SIMPLE FAKE ALT2)", Catalog("general", "general (ALT8)"));

        Assert.Equal(StrategySelectionKind.ReplacementProposed, selection.Kind);
        Assert.Null(selection.Strategy);
        Assert.Contains("no comparable replacement", selection.Message);
    }

    [Fact]
    public void Unsupported_candidates_are_never_recommended()
    {
        var catalog = new List<StrategyDescriptor>
        {
            new() { Id = "general (ALT10)", DisplayName = "ALT10", FilePath = "x.bat", UnsupportedReason = "missing file" },
            new() { Id = "general (ALT8)", DisplayName = "ALT8", FilePath = "y.bat", Arguments = ["--wf-tcp=443"] },
        };

        var selection = StrategyMatcher.Select("general (ALT11)", catalog);

        Assert.Equal("general (ALT8)", selection.Strategy!.Id);
    }
}
