using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zapret.Core.GitHub;
using Zapret.Core.Model;

namespace Zapret.Core.Update;

public sealed record ManagerUpdateInfo(
    string InstalledVersion,
    string? LatestVersion,
    GitHubRelease? Release,
    ReleaseCheckStatus Status,
    string? Message)
{
    public bool UpdateAvailable => Status == ReleaseCheckStatus.UpdateAvailable && Release is not null;

    /// <summary>
    /// A release may declare itself required for compatibility by tagging its notes. Absent that, an
    /// update is never forced (SPEC.md §8.1).
    /// </summary>
    public bool IsCritical => Release?.Body?.Contains("[critical]", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>
/// Updates for <c>Запрет by Grubeer</c> itself, kept strictly separate from engine updates. Runs in the
/// UI process: it neither needs nor wants the privileged service, because applying an update means
/// launching the signed installer, which prompts for elevation itself.
/// </summary>
public sealed class ManagerUpdateService(
    ISettingsStore settings,
    IGitHubReleaseClient releases,
    ILogger<ManagerUpdateService>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ManagerUpdateService>.Instance;

    public static string InstalledVersion { get; } = ResolveInstalledVersion();

    /// <summary>
    /// Checks the manager's own repository. Honours the polling interval unless <paramref name="force"/>
    /// is set by an explicit "Check for updates" click, and never throws when GitHub is unreachable.
    /// </summary>
    public async Task<ManagerUpdateInfo> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var current = settings.Read();

        if (!force && !current.CheckForUpdatesAutomatically)
        {
            return new ManagerUpdateInfo(InstalledVersion, current.ManagerFeed.LastSeenTag, null,
                ReleaseCheckStatus.UpToDate, "Automatic update checks are turned off.");
        }

        if (!force && !current.ManagerFeed.IsDue(DateTimeOffset.UtcNow, current.UpdateCheckInterval))
        {
            return new ManagerUpdateInfo(InstalledVersion, current.ManagerFeed.LastSeenTag, null,
                ReleaseCheckStatus.UpToDate, null);
        }

        var result = await releases
            .CheckAsync(current.ManagerRepository, current.ManagerFeed, InstalledVersion, current.AllowPreviewReleases, cancellationToken)
            .ConfigureAwait(false);

        settings.Update(s => s.ManagerFeed = current.ManagerFeed);

        var latest = result.Release is null ? null : EngineVersion.NormalizeTag(result.Release.Tag);
        _logger.LogInformation("Manager update check: installed {Installed}, latest {Latest}, status {Status}",
            InstalledVersion, latest ?? "unknown", result.Status);

        return new ManagerUpdateInfo(InstalledVersion, latest, result.Release, result.Status, result.Message);
    }

    /// <summary>Remembers a dismissed release so it is never announced again (SPEC.md §8.3).</summary>
    public void Dismiss(string tag) => settings.Update(s => s.ManagerFeed.DismissedTag = tag);

    /// <summary>
    /// Downloads the installer for a release and hands it to Windows. The application does not replace
    /// its own files: the installer does, which keeps the service and the UI in lockstep.
    /// </summary>
    public async Task<(bool Started, string? Error)> DownloadAndRunInstallerAsync(
        GitHubRelease release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var asset = release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (asset is null)
        {
            return (false, $"Release {release.Tag} does not publish an installer.");
        }

        AppPaths.EnsureUserDirectories();
        var destination = Path.Combine(AppPaths.LocalAppData, "updates", asset.Name);

        if (!await releases.DownloadAssetAsync(asset, destination, progress, cancellationToken).ConfigureAwait(false))
        {
            return (false, $"{asset.Name} could not be downloaded.");
        }

        try
        {
            // UseShellExecute lets the installer's own manifest request elevation.
            Process.Start(new ProcessStartInfo { FileName = destination, UseShellExecute = true });
            _logger.LogInformation("Started the installer for manager version {Tag}", release.Tag);
            return (true, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not start the downloaded installer");
            return (false, ex.Message);
        }
    }

    private static string ResolveInstalledVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends +<commit>; the version is the part before it.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
