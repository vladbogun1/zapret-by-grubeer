using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.Core.GitHub;
using Zapret.Core.Ipc;
using Zapret.Core.Model;
using Zapret.Core.Update;

namespace Zapret.App.Pages;

public partial class UpdatesPage : Page
{
    /// <summary>Shorthand for the translation table; this page reads a lot of strings.</summary>
    private static Localization.Loc L => Localization.Loc.Instance;

    private readonly ManagerClient _client;
    private GitHubRelease? _managerRelease;
    private GitHubRelease? _engineRelease;
    private bool _loaded;

    public UpdatesPage(ManagerClient client)
    {
        InitializeComponent();
        _client = client;

        LoadOptions();

        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue) return;

            RenderEngineInstalled();
            if (_loaded) return;

            _loaded = true;
            await CheckManagerAsync(force: false);
            await CheckEngineAsync(force: false);
        };
    }

    private void LoadOptions()
    {
        var settings = App.Settings.Read();
        AutoCheckBox.IsChecked = settings.CheckForUpdatesAutomatically;
        NotifyManagerBox.IsChecked = settings.NotifyAboutManagerUpdates;
        NotifyEngineBox.IsChecked = settings.NotifyAboutEngineUpdates;
        PreviewBox.IsChecked = settings.AllowPreviewReleases;

        ManagerInstalledText.Text = ManagerUpdateService.InstalledVersion;
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        App.Settings.Update(s =>
        {
            s.CheckForUpdatesAutomatically = AutoCheckBox.IsChecked == true;
            s.NotifyAboutManagerUpdates = NotifyManagerBox.IsChecked == true;
            s.NotifyAboutEngineUpdates = NotifyEngineBox.IsChecked == true;
            s.AllowPreviewReleases = PreviewBox.IsChecked == true;
        });
    }

    // ---- manager ----------------------------------------------------------------------------

    private async void OnCheckManager(object sender, RoutedEventArgs e) => await CheckManagerAsync(force: true);

    private async Task CheckManagerAsync(bool force)
    {
        ManagerCheckButton.IsEnabled = false;
        try
        {
            var info = await App.ManagerUpdates.CheckAsync(force);
            _managerRelease = info.Release;

            ManagerInstalledText.Text = info.InstalledVersion;
            ManagerLatestText.Text = info.LatestVersion ?? L["common.noData"];

            if (info.Status == ReleaseCheckStatus.Unavailable)
            {
                OfflineBanner.Visibility = Visibility.Visible;
                ManagerStatusText.Text = L["updates.offline"];
                return;
            }

            OfflineBanner.Visibility = Visibility.Collapsed;

            if (info.UpdateAvailable)
            {
                ManagerStatusText.Text = L.Format(
                    info.IsCritical ? "updates.availableCritical" : "updates.available", info.LatestVersion);
                ManagerUpdateButton.Visibility = Visibility.Visible;
                ManagerLaterButton.Visibility = info.IsCritical ? Visibility.Collapsed : Visibility.Visible;
                ShowNotes(info.Release?.Body);
            }
            else
            {
                ManagerStatusText.Text = "✓ " + L["updates.upToDate"];
                ManagerUpdateButton.Visibility = Visibility.Collapsed;
                ManagerLaterButton.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            ManagerCheckButton.IsEnabled = true;
        }
    }

    private async void OnUpdateManager(object sender, RoutedEventArgs e)
    {
        if (_managerRelease is null) return;

        ManagerUpdateButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;

        var progress = new Progress<double>(value => Progress.Value = value);

        try
        {
            var (started, error) = await App.ManagerUpdates.DownloadAndRunInstallerAsync(_managerRelease, progress);

            if (started)
            {
                MainWindow.ShowMessage("Update", "The installer has been started.", ControlAppearance.Success);
            }
            else
            {
                MainWindow.ShowMessage("Update", error ?? "The update could not be started.", ControlAppearance.Caution);

                // Without a published installer the honest fallback is the release page itself.
                if (_managerRelease.HtmlUrl is not null) OpenInBrowser(_managerRelease.HtmlUrl);
            }
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            ManagerUpdateButton.IsEnabled = true;
        }
    }

    private void OnDismissManager(object sender, RoutedEventArgs e)
    {
        if (_managerRelease is null) return;

        App.ManagerUpdates.Dismiss(_managerRelease.Tag);
        ManagerUpdateButton.Visibility = Visibility.Collapsed;
        ManagerLaterButton.Visibility = Visibility.Collapsed;
        ManagerStatusText.Text = $"Version {_managerRelease.Tag} was dismissed and will not be offered again.";
    }

    // ---- engine ----------------------------------------------------------------------------

    private void RenderEngineInstalled()
    {
        var installed = _client.Status?.EngineVersion;
        EngineInstalledText.Text = installed ?? L["version.unknown"];
        EngineRollbackButton.IsEnabled = _client.CanModify && installed is not null;
    }

    private async void OnCheckEngine(object sender, RoutedEventArgs e) => await CheckEngineAsync(force: true);

    private async Task CheckEngineAsync(bool force)
    {
        EngineCheckButton.IsEnabled = false;
        try
        {
            var settings = App.Settings.Read();

            if (!force && !settings.EngineFeed.IsDue(DateTimeOffset.UtcNow, settings.UpdateCheckInterval))
            {
                return;
            }

            var installed = _client.Status?.EngineVersion;

            // Read-only metadata, so the UI asks GitHub directly; applying an update goes through the service.
            var result = await App.Releases.CheckAsync(settings.EngineRepository, settings.EngineFeed, installed, settings.AllowPreviewReleases);
            App.Settings.Update(s => s.EngineFeed = settings.EngineFeed);

            _engineRelease = result.Release;
            RenderEngineInstalled();
            EngineLatestText.Text = result.Release is null
                ? L["common.noData"]
                : EngineVersion.NormalizeTag(result.Release.Tag);

            if (result.Status == ReleaseCheckStatus.Unavailable)
            {
                OfflineBanner.Visibility = Visibility.Visible;
                EngineStatusText.Text = L["updates.offline"];
                return;
            }

            if (installed is null && result.Release is not null)
            {
                EngineStatusText.Text = L["updates.engineMissing"];
                EngineUpdateButton.Content = L["updates.installEngine"];
                EngineUpdateButton.Visibility = _client.CanModify ? Visibility.Visible : Visibility.Collapsed;
                ShowNotes(result.Release.Body);
                return;
            }

            if (result.Status == ReleaseCheckStatus.UpdateAvailable && result.Release is not null)
            {
                EngineStatusText.Text =
                    $"Flowseal Zapret {EngineVersion.NormalizeTag(result.Release.Tag)} is available. " +
                    "The update contains new or modified bypass strategies. Your custom lists and settings will be preserved.";
                EngineUpdateButton.Content = "Update engine";
                EngineUpdateButton.Visibility = _client.CanModify ? Visibility.Visible : Visibility.Collapsed;
                ShowNotes(result.Release.Body);
            }
            else
            {
                EngineStatusText.Text = "✓ " + L["updates.upToDate"];
                EngineUpdateButton.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            EngineCheckButton.IsEnabled = true;
        }
    }

    private async void OnUpdateEngine(object sender, RoutedEventArgs e)
    {
        if (_engineRelease is null) return;

        EngineUpdateButton.IsEnabled = false;
        Progress.IsIndeterminate = true;
        Progress.Visibility = Visibility.Visible;

        try
        {
            var result = await _client.InstallEngineAsync(_engineRelease.Tag);
            ReportEngineOutcome(result);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            Progress.IsIndeterminate = false;
            EngineUpdateButton.IsEnabled = true;
            await _client.RefreshAsync();
            RenderEngineInstalled();
        }
    }

    private async void OnRollBackEngine(object sender, RoutedEventArgs e)
    {
        EngineRollbackButton.IsEnabled = false;
        try
        {
            ReportEngineOutcome(await _client.RollBackEngineAsync());
        }
        finally
        {
            await _client.RefreshAsync();
            RenderEngineInstalled();
        }
    }

    /// <summary>
    /// The post-update report of SPEC.md §8.6: what happened, which strategy is active, and whether the
    /// engine is running — including the case where the previous strategy no longer exists.
    /// </summary>
    private void ReportEngineOutcome(EngineUpdatePayload? payload)
    {
        if (payload is null)
        {
            MainWindow.ShowMessage("Engine", "The service did not complete the request.", ControlAppearance.Caution);
            return;
        }

        if (!payload.Success)
        {
            var failure = payload.RolledBack
                ? $"The update failed at step {payload.FailedStep} and engine {payload.ActiveVersion} was restored. {payload.Error}"
                : $"The update failed at step {payload.FailedStep}. Engine {payload.ActiveVersion ?? "none"} is unchanged. {payload.Error}";

            EngineStatusText.Text = failure;
            MainWindow.ShowMessage("Engine update failed", failure, ControlAppearance.Danger);
            return;
        }

        var lines = new List<string>
        {
            payload.PreviousVersion is null
                ? $"Engine {payload.ActiveVersion} installed."
                : $"Engine updated: {payload.PreviousVersion} → {payload.ActiveVersion}.",
            $"{payload.StrategyCount} strategies discovered.",
        };

        if (payload.StrategyMessage is not null) lines.Add(payload.StrategyMessage);
        lines.Add(payload.EngineRunning ? "Status: running." : "Status: stopped.");

        if (payload.TargetResults is not null)
        {
            lines.Add(string.Join("   ", payload.TargetResults.Select(t => $"{t.Key}: {(t.Value ? "✓" : "✗")}")));
        }

        if (payload.CompatibilityNotes.Count > 0)
        {
            lines.Add("Notes: " + string.Join("; ", payload.CompatibilityNotes));
        }

        EngineStatusText.Text = string.Join(Environment.NewLine, lines);
        EngineUpdateButton.Visibility = Visibility.Collapsed;
        MainWindow.ShowMessage("Engine", lines[0], ControlAppearance.Success);
    }

    private void ShowNotes(string? markdown)
    {
        var text = ReleaseNotes.ToPlainText(markdown);
        if (text.Length == 0)
        {
            NotesCard.Visibility = Visibility.Collapsed;
            return;
        }

        NotesText.Text = text;
        NotesCard.Visibility = Visibility.Visible;
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MainWindow.ShowMessage("Update", "Could not open the release page.", ControlAppearance.Caution);
        }
    }
}
