using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.App.Localization;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Model;

namespace Zapret.App.Pages;

/// <summary>
/// Everything a user configures: how the engine runs, the upstream filters, and the application's own
/// preferences. The engine plumbing lives here rather than under Сервисы, because that page is the service
/// catalog the reference shows — run mode and packet filters are settings, not services.
/// </summary>
public partial class SettingsPage : Page
{
    private static Loc L => Loc.Instance;

    private readonly ManagerClient _client;

    /// <summary>Set while controls are being filled in, so programmatic changes do not fire commands.</summary>
    private bool _rendering;

    public SettingsPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        _client.Changed += () => Dispatcher.Invoke(RenderEngine);

        RenderPreferences();
        RenderEngine();

        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue) await _client.RefreshAsync();
        };
    }

    // ---- application preferences ---------------------------------------------------------------

    private void RenderPreferences()
    {
        var settings = App.Settings.Read();

        _rendering = true;
        try
        {
            LanguageCombo.ItemsSource = Loc.Instance.Languages;
            LanguageCombo.SelectedItem = Loc.Instance.Languages.FirstOrDefault(l => l.Tag == Loc.Instance.CurrentTag);

            ThemeCombo.SelectedIndex = settings.ThemeOverride?.ToLowerInvariant() switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };

            NotificationsBox.IsChecked = settings.NotificationsEnabled;
        }
        finally
        {
            _rendering = false;
        }

        PathsText.Text = string.Join(Environment.NewLine,
            $"{"app",-14}{AppPaths.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar)}",
            $"{"engine",-14}{AppPaths.RuntimeVersions}",
            $"{"data",-14}{AppPaths.Data}",
            $"{"logs",-14}{AppPaths.Logs}",
            $"{"user",-14}{AppPaths.LocalAppData}");

        RepositoriesText.Text = string.Join(Environment.NewLine,
            $"{"manager",-10}{settings.ManagerRepository}",
            $"{"engine",-10}{settings.EngineRepository}");
    }

    /// <summary>Applies the language immediately: every bound string re-evaluates, no restart.</summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;
        if (LanguageCombo.SelectedItem is not LanguageOption option) return;

        Loc.Instance.Apply(option.Tag);
        App.Settings.Update(s => s.Language = option.Tag);

        RenderPreferences();
        RenderEngine();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;

        var value = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var themeOverride = string.IsNullOrEmpty(value) ? null : value;

        App.Settings.Update(s => s.ThemeOverride = themeOverride);
        App.ApplyTheme(themeOverride);
    }

    private void OnNotificationsChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        App.Settings.Update(s => s.NotificationsEnabled = NotificationsBox.IsChecked == true);
    }

    // ---- engine settings -----------------------------------------------------------------------

    private void RenderEngine()
    {
        var status = _client.Status;
        var canModify = _client.CanModify;

        ReadOnlyBanner.Visibility = _client.ServiceAvailable && !canModify ? Visibility.Visible : Visibility.Collapsed;

        _rendering = true;
        try
        {
            if (status is not null)
            {
                ManagedProcessRadio.IsChecked = status.RunMode == EngineRunMode.ManagedProcess;
                WindowsServiceRadio.IsChecked = status.RunMode == EngineRunMode.WindowsService;
                AutostartBox.IsChecked = status.StartEngineWithWindows;

                GameFilterCombo.SelectedIndex = status.GameFilter switch
                {
                    GameFilterMode.All => 1,
                    GameFilterMode.TcpOnly => 2,
                    GameFilterMode.UdpOnly => 3,
                    _ => 0,
                };

                IpSetCombo.SelectedIndex = status.IpSet switch
                {
                    IpSetMode.None => 1,
                    IpSetMode.Loaded => 2,
                    _ => 0,
                };

                HostsStateText.Text = L[status.ManagedHostsApplied ? "services.hostsApplied" : "services.hostsNotApplied"];
            }

            var capabilities = status?.Capabilities ?? UpstreamCapabilities.None;

            // Each control is disabled with a reason when the installed engine build does not expose it.
            ManagedProcessRadio.IsEnabled = canModify;
            WindowsServiceRadio.IsEnabled = canModify && capabilities.SupportsUpstreamServiceMode;
            AutostartBox.IsEnabled = canModify;
            GameFilterCombo.IsEnabled = canModify && capabilities.SupportsGameFilter;
            IpSetCombo.IsEnabled = canModify && capabilities.SupportsIpSetFilter;
            UpdateIpSetButton.IsEnabled = canModify && capabilities.SupportsIpSetUpdate;
            ApplyHostsButton.IsEnabled = canModify && capabilities.SupportsHostsUpdater;
            RemoveHostsButton.IsEnabled = canModify && status?.ManagedHostsApplied == true;
        }
        finally
        {
            _rendering = false;
        }
    }

    private async void OnRunModeChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;

        var mode = WindowsServiceRadio.IsChecked == true ? EngineRunMode.WindowsService : EngineRunMode.ManagedProcess;
        Report(await _client.SetRunModeAsync(mode), L["services.runMode"]);
    }

    private async void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        Report(await _client.SetAutostartAsync(AutostartBox.IsChecked == true), L["services.autostart"]);
    }

    private async void OnGameFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;

        var mode = GameFilterCombo.SelectedIndex switch
        {
            1 => GameFilterMode.All,
            2 => GameFilterMode.TcpOnly,
            3 => GameFilterMode.UdpOnly,
            _ => GameFilterMode.Off,
        };

        Report(await _client.SetGameFilterAsync(mode), L["system.gameFilter"]);
    }

    private async void OnIpSetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;

        var mode = IpSetCombo.SelectedIndex switch
        {
            1 => IpSetMode.None,
            2 => IpSetMode.Loaded,
            _ => IpSetMode.Any,
        };

        Report(await _client.SetIpSetModeAsync(mode), L["system.ipsetFilter"]);
    }

    private async void OnUpdateIpSet(object sender, RoutedEventArgs e)
    {
        UpdateIpSetButton.IsEnabled = false;
        Report(await _client.UpdateIpSetListAsync(), L["system.ipsetFilter"]);
    }

    private async void OnApplyHosts(object sender, RoutedEventArgs e)
    {
        ApplyHostsButton.IsEnabled = false;
        Report(await _client.ApplyManagedHostsAsync(), L["diagnostics.hosts"]);
    }

    private async void OnRemoveHosts(object sender, RoutedEventArgs e)
    {
        RemoveHostsButton.IsEnabled = false;
        Report(await _client.RemoveManagedHostsAsync(), L["diagnostics.hosts"]);
    }

    private void Report(OperationOutcome outcome, string title)
    {
        if (!outcome.Success)
        {
            MainWindow.ShowMessage(
                title,
                outcome.NeedsElevation ? L["common.readOnly"] : outcome.Message ?? L["updates.failed"],
                ControlAppearance.Caution);
        }

        RenderEngine();
    }

    // ---- folders -------------------------------------------------------------------------------

    private void OnOpenRuntime(object sender, RoutedEventArgs e) => Open(AppPaths.RuntimeVersions);

    private void OnOpenData(object sender, RoutedEventArgs e) => Open(AppPaths.Data);

    private static void Open(string path)
    {
        try
        {
            // The folder may not exist until the service has run once.
            if (!Directory.Exists(path))
            {
                MainWindow.ShowMessage(L["settings.locations"], path, ControlAppearance.Caution);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MainWindow.ShowMessage(L["settings.locations"], ex.Message, ControlAppearance.Caution);
        }
    }
}
