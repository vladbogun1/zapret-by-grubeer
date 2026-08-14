using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

// WinForms is referenced for the tray icon, so System.Drawing is in scope and collides with WPF's
// brush and colour types. The alias keeps every visual here unambiguously WPF.
using Media = System.Windows.Media;
using Wpf.Ui.Controls;
using Zapret.Core.Engine;
using Zapret.Core.Ipc;
using Zapret.Core.Model;

namespace Zapret.App.Pages;

public partial class HomePage : Page
{
    private readonly ManagerClient _client;
    private readonly DispatcherTimer _timer;

    public HomePage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        _client.Changed += OnClientChanged;

        // Polling only while the page is on screen; the tray does not need a two-second cadence.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await _client.RefreshAsync();

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                _timer.Start();
                _ = _client.RefreshAsync();
            }
            else
            {
                _timer.Stop();
            }
        };

        Render();
    }

    private void OnClientChanged() => Dispatcher.Invoke(Render);

    private void Render()
    {
        var status = _client.Status;

        ServiceBanner.Message = _client.UnavailableReason ?? string.Empty;
        ServiceBanner.Visibility = _client.ServiceAvailable ? Visibility.Collapsed : Visibility.Visible;
        ReadOnlyBanner.Visibility = _client.ServiceAvailable && status?.IsElevatedCaller == false
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!_client.ServiceAvailable || status is null)
        {
            StatusText.Text = "Unknown";
            StatusDetail.Text = "The background service could not be reached, so the engine state is unknown.";
            StatusDot.Fill = Media.Brushes.Gray;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            return;
        }

        (string label, string detail, Media.Brush brush) = status.EngineStatus switch
        {
            EngineStatus.Running => (
                "Running",
                status.StartedUtc is null
                    ? "The engine is running."
                    : $"Running since {status.StartedUtc.Value.ToLocalTime():HH:mm}, {Describe(DateTimeOffset.UtcNow - status.StartedUtc.Value)}.",
                (Media.Brush)new Media.SolidColorBrush(Media.Color.FromRgb(0x10, 0x80, 0x3D))),
            EngineStatus.Starting => ("Starting", "The engine is starting.", Media.Brushes.Goldenrod),
            EngineStatus.Faulted => ("Stopped unexpectedly", status.LastError ?? "The engine stopped on its own.", Media.Brushes.IndianRed),
            _ => ("Stopped", status.EngineVersion is null
                ? "No engine is installed yet."
                : "The engine is not running.", Media.Brushes.Gray),
        };

        StatusText.Text = label;
        StatusDetail.Text = detail;
        StatusDot.Fill = brush;

        var canModify = _client.CanModify;
        StartButton.IsEnabled = canModify && status.EngineStatus != EngineStatus.Running && status.EngineVersion is not null;
        StopButton.IsEnabled = canModify && status.EngineStatus is EngineStatus.Running or EngineStatus.Starting;

        EngineVersionText.Text = status.EngineVersion is null
            ? "not installed"
            : $"{status.EngineVersion} ({status.SupportedStrategyCount} strategies)";

        StrategyText.Text = status.StrategyDisplayName ?? "none selected";

        RunModeText.Text = status.RunMode == EngineRunMode.WindowsService
            ? "Windows service (upstream compatible)"
            : "Managed process";

        GameFilterText.Text = new GameFilterState(status.GameFilter).Description;

        IpSetText.Text = status.IpSet switch
        {
            IpSetMode.Loaded => "loaded",
            IpSetMode.None => "disabled",
            _ => "any address",
        };

        if (status.CompatibilityNotes.Count > 0)
        {
            CompatibilityText.Text = string.Join(Environment.NewLine, status.CompatibilityNotes);
            CompatibilityCard.Visibility = Visibility.Visible;
        }
        else
        {
            CompatibilityCard.Visibility = Visibility.Collapsed;
        }
    }

    private static string Describe(TimeSpan uptime) => uptime switch
    {
        { TotalMinutes: < 1 } => "less than a minute",
        { TotalHours: < 1 } => $"{uptime.Minutes} min",
        { TotalDays: < 1 } => $"{uptime.Hours} h {uptime.Minutes} min",
        _ => $"{(int)uptime.TotalDays} d {uptime.Hours} h",
    };

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        Report(await _client.StartAsync(), "Engine");
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Report(await _client.StopAsync(), "Engine");
    }

    private static void Report(OperationOutcome outcome, string title)
    {
        if (outcome.Success)
        {
            if (!string.IsNullOrWhiteSpace(outcome.Message))
            {
                MainWindow.ShowMessage(title, outcome.Message!, ControlAppearance.Success);
            }

            return;
        }

        var message = outcome.NeedsElevation
            ? "This action requires administrator rights."
            : outcome.Message ?? "The operation failed.";

        MainWindow.ShowMessage(title, message, ControlAppearance.Caution);
    }
}
