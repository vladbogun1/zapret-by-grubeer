using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.Core.Engine;
using Zapret.Core.Model;

namespace Zapret.App.Pages;

public partial class ServicesPage : Page
{
    private readonly ManagerClient _client;

    /// <summary>Set while controls are being filled in, so programmatic changes do not fire commands.</summary>
    private bool _rendering;

    public ServicesPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        _client.Changed += () => Dispatcher.Invoke(Render);

        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue) await _client.RefreshAsync();
        };

        Render();
    }

    private void Render()
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

                HostsStateText.Text = status.ManagedHostsApplied
                    ? "Applied. Only the block between the ZapretByGrubeer markers is managed; everything else in the hosts file is left alone."
                    : "Not applied. Applying writes a managed block into the hosts file after backing it up.";
            }

            var capabilities = status?.Capabilities ?? UpstreamCapabilities.None;

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
        Report(await _client.SetRunModeAsync(mode), "Run mode");
    }

    private async void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering) return;
        Report(await _client.SetAutostartAsync(AutostartBox.IsChecked == true), "Autostart");
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

        Report(await _client.SetGameFilterAsync(mode), "Game filter");
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

        Report(await _client.SetIpSetModeAsync(mode), "IPSet filter");
    }

    private async void OnUpdateIpSet(object sender, RoutedEventArgs e)
    {
        UpdateIpSetButton.IsEnabled = false;
        Report(await _client.UpdateIpSetListAsync(), "IPSet list");
    }

    private async void OnApplyHosts(object sender, RoutedEventArgs e)
    {
        ApplyHostsButton.IsEnabled = false;
        Report(await _client.ApplyManagedHostsAsync(), "Hosts");
    }

    private async void OnRemoveHosts(object sender, RoutedEventArgs e)
    {
        RemoveHostsButton.IsEnabled = false;
        Report(await _client.RemoveManagedHostsAsync(), "Hosts");
    }

    private void Report(OperationOutcome outcome, string title)
    {
        var message = outcome.Success
            ? outcome.Message ?? "Done."
            : outcome.NeedsElevation
                ? "This action requires administrator rights."
                : outcome.Message ?? "The operation failed.";

        MainWindow.ShowMessage(title, message, outcome.Success ? ControlAppearance.Success : ControlAppearance.Caution);
        Render();
    }
}
