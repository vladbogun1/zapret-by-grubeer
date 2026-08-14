using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.Flowseal;
using Zapret.Core.GitHub;
using Zapret.Core.Model;

namespace Zapret.Core.Engine;

public enum EngineUpdateStep
{
    Resolve,
    Download,
    Extract,
    Inspect,
    Validate,
    Seed,
    Stop,
    Activate,
    Reapply,
    HealthCheck,
    VerifyTargets,
    Commit,
}

public sealed record EngineUpdateOutcome
{
    public required bool Success { get; init; }
    public EngineUpdateStep? FailedStep { get; init; }
    public string? Error { get; init; }
    public bool RolledBack { get; init; }
    public string? PreviousVersion { get; init; }
    public string? ActiveVersion { get; init; }
    public EngineRuntimeInfo? Runtime { get; init; }
    public CompatibilityReport? Report { get; init; }
    public StrategySelection? StrategySelection { get; init; }
    public IReadOnlyDictionary<string, bool>? TargetResults { get; init; }
    public bool EngineRunning { get; init; }
}

public sealed record EnginePathOptions(string Versions, string Staging, string StateFile, string DataLists)
{
    public static EnginePathOptions Default { get; } =
        new(AppPaths.RuntimeVersions, AppPaths.RuntimeStaging, AppPaths.CurrentStateFile, AppPaths.DataLists);
}

/// <summary>
/// The engine update transaction of docs/flowseal-compatibility.md §6–§8: nothing destructive happens
/// before the candidate has proven itself, and a failure after the point of no return puts the
/// previous build back and starts it.
/// </summary>
public sealed class EngineUpdateService(
    EnginePathOptions paths,
    IFlowsealAdapter adapter,
    IEngineStateStore stateStore,
    IEngineController controller,
    IGitHubReleaseClient releases,
    ArchiveExtractor extractor,
    ITargetProbe? targetProbe = null,
    ILogger<EngineUpdateService>? logger = null)
{
    /// <summary>How long a freshly started engine must stay up before the update counts as healthy.</summary>
    public static readonly TimeSpan HealthSettleWindow = TimeSpan.FromSeconds(15);

    private readonly ILogger _logger = logger ?? NullLogger<EngineUpdateService>.Instance;

    public async Task<EngineUpdateOutcome> InstallAsync(
        GitHubRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tag = EngineVersion.NormalizeTag(release.Tag);
        var state = stateStore.Read();
        var currentVersion = state.Current;
        var previousStrategyId = state.StrategyId;

        _logger.LogInformation("Engine update starting: {Current} -> {Tag}", currentVersion ?? "(none)", tag);

        // ---- Steps 1-8 touch nothing the running engine depends on. -------------------------

        var asset = release.SelectZipAsset();
        if (asset is null)
        {
            return Failure(EngineUpdateStep.Resolve, currentVersion,
                $"release {release.Tag} does not publish a .zip asset the manager can extract");
        }

        Directory.CreateDirectory(paths.Staging);
        var archivePath = Path.Combine(paths.Staging, $"{tag}.zip");

        if (!await releases.DownloadAssetAsync(asset, archivePath, progress, cancellationToken).ConfigureAwait(false))
        {
            return Failure(EngineUpdateStep.Download, currentVersion, $"could not download {asset.Name}");
        }

        var candidateDirectory = Path.Combine(paths.Staging, tag);
        var extraction = extractor.Extract(archivePath, candidateDirectory);
        if (!extraction.Success || extraction.Directory is null)
        {
            return Failure(EngineUpdateStep.Extract, currentVersion, extraction.Error ?? "extraction failed");
        }

        var candidate = adapter.Inspect(extraction.Directory, release.Tag);

        if (!candidate.Report.CanActivate)
        {
            var blockers = string.Join("; ", candidate.Report.Blockers.Select(b => $"{b.Name}: {b.Detail}"));
            _logger.LogWarning("Candidate {Tag} is incompatible ({Blockers}); keeping {Current}", tag, blockers, currentVersion);

            CleanUp(archivePath, candidateDirectory, failedTag: tag);
            RecordRejection(tag);

            return new EngineUpdateOutcome
            {
                Success = false,
                FailedStep = EngineUpdateStep.Validate,
                Error = blockers,
                PreviousVersion = currentVersion,
                ActiveVersion = currentVersion,
                Runtime = candidate,
                Report = candidate.Report,
                EngineRunning = controller.State.Status == EngineStatus.Running,
            };
        }

        try
        {
            SeedUserState(currentVersion, extraction.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CleanUp(archivePath, candidateDirectory, failedTag: null);
            return Failure(EngineUpdateStep.Seed, currentVersion, ex.Message);
        }

        // ---- From here on the running engine is disturbed, so every failure rolls back. -----

        var wasRunning = controller.State.Status is EngineStatus.Running or EngineStatus.Starting;
        var activatedDirectory = Path.Combine(paths.Versions, tag);

        try
        {
            await controller.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not stop the running engine; the update is abandoned");
            return Failure(EngineUpdateStep.Stop, currentVersion, ex.Message);
        }

        try
        {
            Directory.CreateDirectory(paths.Versions);
            if (Directory.Exists(activatedDirectory)) Directory.Delete(activatedDirectory, recursive: true);
            Directory.Move(extraction.Directory, activatedDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not activate candidate {Tag}", tag);
            var rolled = await RollBackAsync(currentVersion, previousStrategyId, wasRunning, cancellationToken).ConfigureAwait(false);
            return Failure(EngineUpdateStep.Activate, currentVersion, ex.Message, rolled);
        }

        var activated = adapter.Inspect(activatedDirectory, release.Tag);
        var selection = StrategyMatcher.Select(previousStrategyId, activated.Strategies);

        stateStore.Write(new EngineCurrentState
        {
            Current = tag,
            Previous = currentVersion,
            ActivatedUtc = DateTimeOffset.UtcNow,
            ActivatedBy = currentVersion is null ? "install" : "update",
            VersionSource = activated.Version.Source.ToString(),
            StrategyId = selection.Kind == StrategySelectionKind.Reapplied ? selection.Strategy!.Id : previousStrategyId,
            StrategyCount = activated.SupportedStrategyCount,
            RejectedTags = state.RejectedTags,
        });

        // The user's strategy is gone: activate the build, but never pick a different one silently.
        if (selection.Kind != StrategySelectionKind.Reapplied)
        {
            _logger.LogInformation("Engine {Tag} activated but left stopped: {Message}", tag, selection.Message);
            CleanUp(archivePath, candidateDirectory, failedTag: null);
            ApplyRetention(tag, currentVersion);

            return new EngineUpdateOutcome
            {
                Success = true,
                PreviousVersion = currentVersion,
                ActiveVersion = tag,
                Runtime = activated,
                Report = activated.Report,
                StrategySelection = selection,
                EngineRunning = false,
            };
        }

        var strategy = selection.Strategy!;

        bool started;
        try
        {
            started = await controller.StartAsync(activated, strategy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Engine {Tag} could not be started with strategy {Strategy}", tag, strategy.Id);
            started = false;
        }

        if (!started)
        {
            var rolled = await RollBackAsync(currentVersion, previousStrategyId, wasRunning, cancellationToken).ConfigureAwait(false);
            return Failure(EngineUpdateStep.Reapply, currentVersion, $"engine {tag} did not start with strategy {strategy.DisplayName}", rolled);
        }

        if (!await controller.IsHealthyAsync(HealthSettleWindow, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Engine {Tag} did not stay up for {Seconds}s", tag, HealthSettleWindow.TotalSeconds);
            var rolled = await RollBackAsync(currentVersion, previousStrategyId, wasRunning, cancellationToken).ConfigureAwait(false);
            return Failure(EngineUpdateStep.HealthCheck, currentVersion, $"engine {tag} stopped within {HealthSettleWindow.TotalSeconds:0} seconds of starting", rolled);
        }

        // Informational only — an unreachable target does not discard a healthy build.
        IReadOnlyDictionary<string, bool>? targets = null;
        if (targetProbe is not null)
        {
            try
            {
                targets = await targetProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogInformation(ex, "Target verification could not run");
            }
        }

        CleanUp(archivePath, candidateDirectory, failedTag: null);
        ApplyRetention(tag, currentVersion);

        _logger.LogInformation("Engine update committed: {Current} -> {Tag}, strategy {Strategy}", currentVersion ?? "(none)", tag, strategy.Id);

        return new EngineUpdateOutcome
        {
            Success = true,
            PreviousVersion = currentVersion,
            ActiveVersion = tag,
            Runtime = activated,
            Report = activated.Report,
            StrategySelection = selection,
            TargetResults = targets,
            EngineRunning = true,
        };
    }

    /// <summary>Explicit user-driven rollback from the Updates page.</summary>
    public async Task<EngineUpdateOutcome> RollBackToPreviousAsync(CancellationToken cancellationToken = default)
    {
        var state = stateStore.Read();

        if (string.IsNullOrEmpty(state.Previous) || !Directory.Exists(Path.Combine(paths.Versions, state.Previous)))
        {
            return Failure(EngineUpdateStep.Activate, state.Current, "there is no previous engine version to return to");
        }

        var rolled = await RollBackAsync(state.Previous, state.StrategyId, restart: true, cancellationToken).ConfigureAwait(false);

        return new EngineUpdateOutcome
        {
            Success = rolled,
            RolledBack = rolled,
            PreviousVersion = state.Current,
            ActiveVersion = rolled ? state.Previous : state.Current,
            EngineRunning = controller.State.Status == EngineStatus.Running,
        };
    }

    private async Task<bool> RollBackAsync(string? version, string? strategyId, bool restart, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(version))
        {
            // First-ever install: there is nothing to go back to, and pretending otherwise would lie.
            _logger.LogWarning("Nothing to roll back to; no engine is active");
            return false;
        }

        var directory = Path.Combine(paths.Versions, version);
        if (!Directory.Exists(directory))
        {
            _logger.LogError("Rollback target {Version} is missing from {Path}", version, directory);
            return false;
        }

        try
        {
            await controller.StopAsync(cancellationToken).ConfigureAwait(false);

            var restored = adapter.Inspect(directory);
            var selection = StrategyMatcher.Select(strategyId, restored.Strategies);

            var state = stateStore.Read();
            stateStore.Write(state with
            {
                Current = version,
                ActivatedUtc = DateTimeOffset.UtcNow,
                ActivatedBy = "rollback",
                StrategyId = strategyId,
                StrategyCount = restored.SupportedStrategyCount,
            });

            if (restart && selection.Kind == StrategySelectionKind.Reapplied)
            {
                await controller.StartAsync(restored, selection.Strategy!, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation("Rolled back to engine {Version}", version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback to {Version} failed", version);
            return false;
        }
    }

    /// <summary>
    /// Carries the user's own data into the candidate: authoritative lists from <c>data\lists</c>, and
    /// the upstream toggles from the build being replaced.
    /// </summary>
    private void SeedUserState(string? currentVersion, string candidateDirectory)
    {
        adapter.EnsureUserLists(candidateDirectory);

        var candidateLists = UpstreamLayout.Lists(candidateDirectory);

        if (Directory.Exists(paths.DataLists))
        {
            foreach (var source in Directory.EnumerateFiles(paths.DataLists, "*.txt"))
            {
                File.Copy(source, Path.Combine(candidateLists, Path.GetFileName(source)), overwrite: true);
            }
        }

        if (string.IsNullOrEmpty(currentVersion)) return;

        var currentDirectory = Path.Combine(paths.Versions, currentVersion);
        if (!Directory.Exists(currentDirectory)) return;

        // Upstream toggles live as files inside the runtime, so they must be carried across.
        CopyIfPresent(UpstreamLayout.GameFilterFlag(currentDirectory), UpstreamLayout.GameFilterFlag(candidateDirectory));
        CopyIfPresent(UpstreamLayout.UpdateCheckFlag(currentDirectory), UpstreamLayout.UpdateCheckFlag(candidateDirectory));
        CopyIfPresent(UpstreamLayout.IpSetAll(currentDirectory), UpstreamLayout.IpSetAll(candidateDirectory));
        CopyIfPresent(UpstreamLayout.IpSetAllBackup(currentDirectory), UpstreamLayout.IpSetAllBackup(candidateDirectory));

        foreach (var name in UpstreamLayout.UserLists.Keys)
        {
            CopyIfPresent(Path.Combine(UpstreamLayout.Lists(currentDirectory), name), Path.Combine(candidateLists, name));
        }
    }

    private static void CopyIfPresent(string source, string destination)
    {
        if (!File.Exists(source)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    /// <summary>Keeps the current version plus one previous working version, and nothing else.</summary>
    private void ApplyRetention(string current, string? previous)
    {
        if (!Directory.Exists(paths.Versions)) return;

        foreach (var directory in Directory.EnumerateDirectories(paths.Versions))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase)) continue;
            if (previous is not null && string.Equals(name, previous, StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                Directory.Delete(directory, recursive: true);
                _logger.LogInformation("Removed outdated engine version {Version}", name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove outdated engine version {Version}", name);
            }
        }
    }

    /// <summary>
    /// Staging is cleaned on success and on failure. A rejected candidate is kept once, renamed, so
    /// diagnostics can look at it.
    /// </summary>
    private void CleanUp(string archivePath, string candidateDirectory, string? failedTag)
    {
        try
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);

            if (failedTag is not null && Directory.Exists(candidateDirectory))
            {
                var kept = Path.Combine(paths.Staging, $"failed-{failedTag}");
                if (Directory.Exists(kept)) Directory.Delete(kept, recursive: true);
                Directory.Move(candidateDirectory, kept);
                return;
            }

            if (Directory.Exists(candidateDirectory)) Directory.Delete(candidateDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not clean the staging directory");
        }
    }

    private void RecordRejection(string tag)
    {
        var state = stateStore.Read();
        if (state.RejectedTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return;

        var rejected = new List<string>(state.RejectedTags) { tag };
        stateStore.Write(state with { RejectedTags = rejected });
    }

    private EngineUpdateOutcome Failure(EngineUpdateStep step, string? activeVersion, string error, bool rolledBack = false) => new()
    {
        Success = false,
        FailedStep = step,
        Error = error,
        RolledBack = rolledBack,
        ActiveVersion = activeVersion,
        PreviousVersion = activeVersion,
        EngineRunning = controller.State.Status == EngineStatus.Running,
    };
}
