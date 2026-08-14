using System.Text.Json;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Flowseal;
using Zapret.Core.GitHub;
using Zapret.Core.Ipc;
using Zapret.Core.Model;
using Zapret.Core.SystemIntegration;

namespace Zapret.Service;

/// <summary>
/// The privileged core of the product: it owns the engine lifecycle and every machine-wide change, and
/// answers the intents the UI sends over the pipe. Clients never name paths — every path here is
/// resolved from the manager's own state (ADR-0002).
/// </summary>
public sealed class EngineHost(
    ISettingsStore settings,
    IFlowsealAdapter adapter,
    IEngineStateStore engineState,
    WinwsProcessController processController,
    UpstreamServiceEngineController serviceController,
    IGitHubReleaseClient releases,
    ArchiveExtractor extractor,
    HostsManager hosts,
    TcpTimestamps timestamps,
    ITargetProbe targetProbe,
    HttpClient http,
    ILoggerFactory loggerFactory,
    ILogger<EngineHost> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private EngineRuntimeInfo? _runtime;

    public IEngineController ActiveController =>
        settings.Read().RunMode == EngineRunMode.WindowsService ? serviceController : processController;

    /// <summary>The active engine build, inspected lazily and re-inspected after any change to it.</summary>
    public EngineRuntimeInfo? Runtime
    {
        get
        {
            if (_runtime is not null) return _runtime;

            var current = engineState.Read().Current;
            if (string.IsNullOrEmpty(current)) return null;

            var directory = AppPaths.VersionDirectory(current);
            if (!Directory.Exists(directory)) return null;

            _runtime = adapter.Inspect(directory);
            return _runtime;
        }
    }

    public void InvalidateRuntime() => _runtime = null;

    /// <summary>
    /// Startup work that must never block the pipe from accepting clients: adopt an engine installed by
    /// upstream tooling, then start the engine if the user asked for that.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppPaths.EnsureMachineDirectories();

        AdoptUpstreamInstallation();

        var current = settings.Read();
        if (!current.StartEngineWithWindows) return;

        var state = engineState.Read();
        if (Runtime is null || string.IsNullOrEmpty(state.StrategyId)) return;

        var strategy = Runtime.Strategies.FirstOrDefault(s => s.Id == state.StrategyId);
        if (strategy is null || !strategy.IsSupported)
        {
            logger.LogWarning("Not starting the engine: strategy {Strategy} is unavailable in engine {Version}",
                state.StrategyId, Runtime.Version.Raw);
            return;
        }

        await StartAsync(strategy.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// If upstream's own service or registry marker is present, record what it selected rather than
    /// fighting it. SPEC.md §7.
    /// </summary>
    private void AdoptUpstreamInstallation()
    {
        var state = engineState.Read();
        if (!string.IsNullOrEmpty(state.StrategyId)) return;

        var marker = serviceController.ReadStrategyMarker();
        if (string.IsNullOrEmpty(marker)) return;

        logger.LogInformation("Adopting the strategy {Strategy} recorded by an engine installed outside {Product}",
            marker, AppPaths.DisplayName);

        engineState.Write(state with { StrategyId = marker });
    }

    // ---- operations -------------------------------------------------------------------------

    public StatusPayload GetStatus(bool callerIsAdministrator)
    {
        var current = settings.Read();
        var runtime = Runtime;
        var controller = ActiveController;

        var notes = runtime is null
            ? Array.Empty<string>()
            : runtime.Report.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}").ToArray();

        var strategyId = controller.State.StrategyId ?? engineState.Read().StrategyId;

        return new StatusPayload
        {
            EngineStatus = controller.State.Status,
            EngineVersion = runtime?.Version.Raw,
            EngineVersionSource = runtime?.Version.Source.ToString(),
            ManagerVersion = typeof(EngineHost).Assembly.GetName().Version?.ToString(3),
            StrategyId = strategyId,
            StrategyDisplayName = strategyId is null ? null : StrategyBatParser.ToDisplayName(strategyId),
            StartedUtc = controller.State.StartedUtc,
            LastError = controller.State.LastError,
            RunMode = current.RunMode,
            StartEngineWithWindows = current.StartEngineWithWindows,
            GameFilter = runtime?.GameFilter.Mode ?? GameFilterMode.Off,
            IpSet = runtime?.IpSet ?? IpSetMode.Any,
            Capabilities = runtime?.Capabilities ?? UpstreamCapabilities.None,
            CompatibilityOutcome = runtime?.Report.Outcome,
            CompatibilityNotes = notes,
            ManagedHostsApplied = hosts.IsApplied(),
            SupportedStrategyCount = runtime?.SupportedStrategyCount ?? 0,
            IsElevatedCaller = callerIsAdministrator,
        };
    }

    public StrategyListPayload ListStrategies()
    {
        var runtime = Runtime;
        if (runtime is null) return new StrategyListPayload();

        var selected = engineState.Read().StrategyId;

        return new StrategyListPayload
        {
            EngineVersion = runtime.Version.Raw,
            Strategies = runtime.Strategies.Select(s => new StrategyPayload
            {
                Id = s.Id,
                DisplayName = s.DisplayName,
                IsSupported = s.IsSupported,
                UnsupportedReason = s.UnsupportedReason,
                IsSelected = string.Equals(s.Id, selected, StringComparison.OrdinalIgnoreCase),
                ArgumentCount = s.Arguments.Count,
            }).ToList(),
        };
    }

    public async Task<OperationResultPayload> StartAsync(string? strategyId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtime = Runtime;
            if (runtime is null) return new OperationResultPayload(false, "No engine is installed yet.");

            var id = strategyId ?? engineState.Read().StrategyId;
            if (string.IsNullOrEmpty(id)) return new OperationResultPayload(false, "No strategy has been selected.");

            var strategy = runtime.Strategies.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (strategy is null) return new OperationResultPayload(false, $"Strategy '{id}' does not exist in engine {runtime.Version.Raw}.");
            if (!strategy.IsSupported) return new OperationResultPayload(false, strategy.UnsupportedReason);

            await EnsureTcpTimestampsAsync(cancellationToken).ConfigureAwait(false);
            adapter.EnsureUserLists(runtime.Directory);

            var started = await ActiveController.StartAsync(runtime, strategy, cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                return new OperationResultPayload(false, ActiveController.State.LastError ?? "The engine did not start.");
            }

            engineState.Write(engineState.Read() with { StrategyId = strategy.Id });
            return new OperationResultPayload(true, $"{strategy.DisplayName} is running.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OperationResultPayload> StopAsync(CancellationToken cancellationToken)
    {
        await ActiveController.StopAsync(cancellationToken).ConfigureAwait(false);
        return new OperationResultPayload(true, "The engine is stopped.");
    }

    public async Task<OperationResultPayload> SetRunModeAsync(EngineRunMode mode, CancellationToken cancellationToken)
    {
        var current = settings.Read();
        if (current.RunMode == mode) return new OperationResultPayload(true);

        var wasRunning = ActiveController.State.Status == EngineStatus.Running;
        await ActiveController.StopAsync(cancellationToken).ConfigureAwait(false);

        // Leaving service mode must not leave an orphaned upstream service behind.
        if (current.RunMode == EngineRunMode.WindowsService)
        {
            await serviceController.RemoveServiceAsync(cancellationToken).ConfigureAwait(false);
        }

        settings.Update(s => s.RunMode = mode);

        return wasRunning
            ? await StartAsync(null, cancellationToken).ConfigureAwait(false)
            : new OperationResultPayload(true);
    }

    /// <summary>
    /// Whether the service starts the engine at boot. This is the manager's own autostart, separate from
    /// the upstream service's <c>start=auto</c>, and is what the uninstaller removes.
    /// </summary>
    public OperationResultPayload SetAutostart(bool enabled)
    {
        settings.Update(s => s.StartEngineWithWindows = enabled);

        return new OperationResultPayload(true, enabled
            ? "The engine will start with Windows."
            : "The engine will no longer start with Windows.");
    }

    public async Task<OperationResultPayload> SetGameFilterAsync(GameFilterMode mode, CancellationToken cancellationToken)
    {
        var runtime = Runtime;
        if (runtime is null) return new OperationResultPayload(false, "No engine is installed yet.");
        if (!runtime.Capabilities.SupportsGameFilter)
        {
            return new OperationResultPayload(false, "The installed engine version does not expose the game filter.");
        }

        adapter.WriteGameFilter(runtime.Directory, new GameFilterState(mode));
        InvalidateRuntime();

        // The filter is baked into the engine arguments, so it only takes effect on restart.
        if (ActiveController.State.Status != EngineStatus.Running) return new OperationResultPayload(true);

        return await StartAsync(engineState.Read().StrategyId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResultPayload> SetIpSetModeAsync(IpSetMode mode, CancellationToken cancellationToken)
    {
        var runtime = Runtime;
        if (runtime is null) return new OperationResultPayload(false, "No engine is installed yet.");
        if (!runtime.Capabilities.SupportsIpSetFilter)
        {
            return new OperationResultPayload(false, "The installed engine version does not ship an IPSet list.");
        }

        var listFile = UpstreamLayout.IpSetAll(runtime.Directory);
        var backupFile = UpstreamLayout.IpSetAllBackup(runtime.Directory);

        try
        {
            switch (mode)
            {
                case IpSetMode.None:
                    if (File.Exists(listFile) && runtime.IpSet == IpSetMode.Loaded) MoveOver(listFile, backupFile);
                    File.WriteAllText(listFile, IpSetState.Sentinel + Environment.NewLine);
                    break;

                case IpSetMode.Any:
                    if (File.Exists(listFile) && runtime.IpSet == IpSetMode.Loaded) MoveOver(listFile, backupFile);
                    File.WriteAllText(listFile, string.Empty);
                    break;

                case IpSetMode.Loaded:
                    if (!File.Exists(backupFile))
                    {
                        return new OperationResultPayload(false, "There is no IPSet list to restore. Update the IPSet list first.");
                    }

                    MoveOver(backupFile, listFile);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not switch the IPSet mode");
            return new OperationResultPayload(false, ex.Message);
        }

        InvalidateRuntime();

        if (ActiveController.State.Status != EngineStatus.Running) return new OperationResultPayload(true);
        return await StartAsync(engineState.Read().StrategyId, cancellationToken).ConfigureAwait(false);
    }

    private static void MoveOver(string source, string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        File.Move(source, destination);
    }

    public string? GetUserList(string name)
    {
        if (!UpstreamLayout.IsUserList(name)) return null;

        var authoritative = Path.Combine(AppPaths.DataLists, name);
        if (File.Exists(authoritative)) return File.ReadAllText(authoritative);

        var runtime = Runtime;
        if (runtime is null) return UpstreamLayout.UserLists[name];

        var inRuntime = Path.Combine(UpstreamLayout.Lists(runtime.Directory), name);
        return File.Exists(inRuntime) ? File.ReadAllText(inRuntime) : UpstreamLayout.UserLists[name];
    }

    public async Task<OperationResultPayload> SaveUserListAsync(string name, string content, CancellationToken cancellationToken)
    {
        if (!UpstreamLayout.IsUserList(name))
        {
            return new OperationResultPayload(false, $"'{name}' is not a user-editable list.");
        }

        // Upstream's strategies break on an empty user list, so the placeholder is restored instead.
        var payload = string.IsNullOrWhiteSpace(content) ? UpstreamLayout.UserLists[name] : content;

        Directory.CreateDirectory(AppPaths.DataLists);
        await File.WriteAllTextAsync(Path.Combine(AppPaths.DataLists, name), payload, cancellationToken).ConfigureAwait(false);

        var runtime = Runtime;
        if (runtime is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(UpstreamLayout.Lists(runtime.Directory), name), payload, cancellationToken)
                .ConfigureAwait(false);
        }

        if (ActiveController.State.Status != EngineStatus.Running) return new OperationResultPayload(true, "Saved.");

        // winws reads the lists at startup only.
        return await StartAsync(engineState.Read().StrategyId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EngineUpdatePayload> InstallEngineAsync(string tag, CancellationToken cancellationToken)
    {
        var current = settings.Read();

        // The client only names a tag; the service resolves it against GitHub itself.
        var check = await releases
            .CheckAsync(current.EngineRepository, current.EngineFeed, installedVersion: null, current.AllowPreviewReleases, cancellationToken)
            .ConfigureAwait(false);

        settings.Update(s => s.EngineFeed = current.EngineFeed);

        var release = check.Release;
        if (release is null || !string.Equals(EngineVersion.NormalizeTag(release.Tag), EngineVersion.NormalizeTag(tag), StringComparison.OrdinalIgnoreCase))
        {
            return new EngineUpdatePayload
            {
                Success = false,
                FailedStep = nameof(EngineUpdateStep.Resolve),
                Error = check.Message ?? $"Release {tag} could not be resolved from {current.EngineRepository}.",
            };
        }

        var service = new EngineUpdateService(
            EnginePathOptions.Default, adapter, engineState, ActiveController, releases, extractor, targetProbe,
            loggerFactory.CreateLogger<EngineUpdateService>());

        var outcome = await service.InstallAsync(release, progress: null, cancellationToken).ConfigureAwait(false);
        InvalidateRuntime();

        return ToPayload(outcome);
    }

    public async Task<EngineUpdatePayload> RollBackEngineAsync(CancellationToken cancellationToken)
    {
        var service = new EngineUpdateService(
            EnginePathOptions.Default, adapter, engineState, ActiveController, releases, extractor, targetProbe,
            loggerFactory.CreateLogger<EngineUpdateService>());

        var outcome = await service.RollBackToPreviousAsync(cancellationToken).ConfigureAwait(false);
        InvalidateRuntime();

        return ToPayload(outcome);
    }

    private static EngineUpdatePayload ToPayload(EngineUpdateOutcome outcome) => new()
    {
        Success = outcome.Success,
        FailedStep = outcome.FailedStep?.ToString(),
        Error = outcome.Error,
        RolledBack = outcome.RolledBack,
        PreviousVersion = outcome.PreviousVersion,
        ActiveVersion = outcome.ActiveVersion,
        StrategyCount = outcome.Runtime?.SupportedStrategyCount ?? 0,
        SelectedStrategyId = outcome.StrategySelection?.Strategy?.Id,
        StrategyMessage = outcome.StrategySelection?.Message,
        EngineRunning = outcome.EngineRunning,
        TargetResults = outcome.TargetResults,
        CompatibilityNotes = outcome.Report?.Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}").ToArray()
                             ?? Array.Empty<string>(),
    };

    public async Task<OperationResultPayload> UpdateIpSetListAsync(CancellationToken cancellationToken)
    {
        var runtime = Runtime;
        if (runtime is null) return new OperationResultPayload(false, "No engine is installed yet.");
        if (!runtime.Capabilities.SupportsIpSetUpdate)
        {
            return new OperationResultPayload(false, "The installed engine version does not ship an IPSet payload.");
        }

        var (payload, error) = await ReadPayloadAsync(runtime.Directory, UpstreamLayout.IpSetPayloadName, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null) return new OperationResultPayload(false, error);

        try
        {
            var listFile = UpstreamLayout.IpSetAll(runtime.Directory);
            var backupFile = UpstreamLayout.IpSetAllBackup(runtime.Directory);

            // Keep upstream's backup convention intact, so switching back to "loaded" still works.
            if (File.Exists(listFile) && runtime.IpSet == IpSetMode.Loaded) MoveOver(listFile, backupFile);

            await File.WriteAllTextAsync(listFile, payload, cancellationToken).ConfigureAwait(false);
            InvalidateRuntime();

            var count = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            return new OperationResultPayload(true, $"The IPSet list was updated: {count} entries.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not update the IPSet list");
            return new OperationResultPayload(false, ex.Message);
        }
    }

    /// <summary>
    /// Reads a <c>.service</c> payload: from the installed build when it is a git checkout, otherwise
    /// from upstream's repository, because release archives do not contain that directory. The URL is
    /// always built from the configured repository — never from anything a client sent.
    /// </summary>
    private async Task<(string? Payload, string? Error)> ReadPayloadAsync(
        string runtimeDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var local = Path.Combine(UpstreamLayout.ServiceDirectory(runtimeDirectory), fileName);
        if (File.Exists(local))
        {
            return (await File.ReadAllTextAsync(local, cancellationToken).ConfigureAwait(false), null);
        }

        var repository = settings.Read().EngineRepository;
        var url = UpstreamLayout.PayloadUrl(repository, fileName);

        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Payload {File} returned {Status} from {Repository}", fileName, (int)response.StatusCode, repository);
                return (null, $"{repository} no longer publishes {fileName} ({(int)response.StatusCode}).");
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Fetched {File} from {Repository} ({Bytes} bytes)", fileName, repository, content.Length);
            return (content, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not fetch {File} from {Repository}", fileName, repository);
            return (null, "Could not reach GitHub to fetch the list.");
        }
    }

    public async Task<OperationResultPayload> ApplyManagedHostsAsync(CancellationToken cancellationToken)
    {
        var runtime = Runtime;
        if (runtime is null) return new OperationResultPayload(false, "No engine is installed yet.");
        if (!runtime.Capabilities.SupportsHostsUpdater)
        {
            return new OperationResultPayload(false, "The installed engine version does not ship a hosts payload.");
        }

        var (payload, error) = await ReadPayloadAsync(runtime.Directory, UpstreamLayout.HostsPayloadName, cancellationToken)
            .ConfigureAwait(false);

        if (payload is null) return new OperationResultPayload(false, error);

        return hosts.Apply(payload, $"Flowseal Zapret {runtime.Version.Raw}")
            ? new OperationResultPayload(true, "The managed hosts entries were applied.")
            : new OperationResultPayload(false, "The hosts file could not be written.");
    }

    public OperationResultPayload RemoveManagedHosts() =>
        hosts.Remove()
            ? new OperationResultPayload(true, "The managed hosts entries were removed.")
            : new OperationResultPayload(false, "The hosts file could not be written.");

    public async Task<OperationResultPayload> RunStrategyTestsAsync(CancellationToken cancellationToken)
    {
        var runtime = Runtime;
        if (runtime is null) return new OperationResultPayload(false, "No engine is installed yet.");
        if (!runtime.Capabilities.SupportsStrategyTests)
        {
            return new OperationResultPayload(false, "The installed engine version does not ship the test utility.");
        }

        var script = UpstreamLayout.TestScript(runtime.Directory);
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            WorkingDirectory = runtime.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return new OperationResultPayload(false, "The test utility could not be started.");

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            return new OperationResultPayload(process.ExitCode == 0, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            logger.LogError(ex, "The upstream test utility failed to run");
            return new OperationResultPayload(false, ex.Message);
        }
    }

    public string GetLogTail(string source, int lines)
    {
        var directory = AppPaths.Logs;
        if (!Directory.Exists(directory)) return string.Empty;

        var file = Directory
            .EnumerateFiles(directory, $"{source}-*.log")
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (file is null) return string.Empty;

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            return string.Join('\n', all.TakeLast(Math.Clamp(lines, 1, 2000)));
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Could not read the log tail");
            return string.Empty;
        }
    }

    private async Task EnsureTcpTimestampsAsync(CancellationToken cancellationToken)
    {
        var current = settings.Read();
        if (current.TcpTimestampsEnabledByManager) return;

        var before = await timestamps.TryReadEnabledAsync(cancellationToken).ConfigureAwait(false);
        if (before == true) return;

        if (await timestamps.EnableAsync(cancellationToken).ConfigureAwait(false))
        {
            settings.Update(s =>
            {
                s.TcpTimestampsEnabledByManager = true;
                s.TcpTimestampsValueBeforeManager = before;
            });
        }
    }
}
