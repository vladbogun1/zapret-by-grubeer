using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Zapret.Core;
using Zapret.Core.Ipc;
using Zapret.Core.Model;
using Brush = System.Windows.Media.Brush;

namespace Zapret.Shell;

/// <summary>A command that runs an async action and reports nothing back; enough for a settings surface.</summary>
public sealed class Act(Func<Task> run, bool enabled = true) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => enabled;

    public async void Execute(object? parameter) => await run();

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record StrategyRow(string Name, string Note, string Score, Brush Brush, bool CanUse, Act Use);

public sealed record ServiceRow(string Name, string Domains, bool Enabled, bool CanEdit, Visibility CustomVisibility, Act Toggle, Act Remove);

/// <summary>
/// The expanded surface: manual bypass choice, the full sweep, the service editor, engine settings and
/// diagnostics.
/// <para>
/// It is a separate window on purpose. The main screen answers four questions and offers one action; bolting
/// tabs onto it would undo that. Here the rules invert: this is for someone who wants the mechanism, so nothing
/// is hidden and every control says what it changes. It stays behind a switch that is off by default — the
/// point was never that power users cannot have controls, only that a normal user must never need them
/// (docs/nextgen-ux.md §2, §6).
/// </para>
/// </summary>
public partial class AdvancedWindow : Window
{
    private static Text T => Text.Current;

    private readonly ObservableCollection<StrategyRow> _strategies = new();
    private readonly ObservableCollection<ServiceRow> _services = new();

    private bool _rendering;
    private bool _canModify;

    public AdvancedWindow()
    {
        InitializeComponent();

        StrategyList.ItemsSource = _strategies;
        ServiceList.ItemsSource = _services;

        Loaded += async (_, _) => await LoadAllAsync();
    }

    private async Task LoadAllAsync()
    {
        _canModify = await App.Client.CanModifyAsync();
        AdminNote.Visibility = _canModify ? Visibility.Collapsed : Visibility.Visible;

        await LoadStrategiesAsync();
        await LoadServicesAsync();
        await LoadEngineAsync();
        await LoadDiagnosticsAsync();
    }

    // ---- bypass options ----------------------------------------------------------------------

    private async Task LoadStrategiesAsync()
    {
        var catalog = await App.Client.GetStrategiesAsync();
        var results = await App.Client.GetTestResultsAsync();

        _strategies.Clear();

        if (catalog is null || catalog.Strategies.Count == 0)
        {
            _strategies.Add(new StrategyRow(T["adv.str.noEngine"], string.Empty, string.Empty,
                (Brush)FindResource("Fg3"), false, new Act(() => Task.CompletedTask, false)));
            return;
        }

        // Scores are shown only when they were measured on this engine and this connection.
        var scores = results is { IsCurrent: true }
            ? results.Items.ToDictionary(i => i.StrategyId, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, StrategyResultItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var strategy in catalog.Strategies)
        {
            scores.TryGetValue(strategy.Id, out var score);

            var note =
                !strategy.IsSupported ? strategy.UnsupportedReason ?? T["adv.str.broken"]
                : strategy.IsSelected ? T["adv.str.inUse"]
                : score is null ? T["adv.str.untested"]
                : score.IsBest ? T["adv.str.best"]
                : $"{score.Passed}/{score.Total}";

            var brush = (Brush)FindResource(
                !strategy.IsSupported ? "Bad"
                : strategy.IsSelected || score?.IsBest == true ? "Ok"
                : "Fg2");

            var id = strategy.Id;

            _strategies.Add(new StrategyRow(
                strategy.DisplayName,
                note,
                score is null ? string.Empty : $"{score.SuccessPercent}%",
                brush,
                _canModify && strategy.IsSupported && !strategy.IsSelected,
                new Act(() => ApplyAsync(id))));
        }
    }

    private async Task ApplyAsync(string id)
    {
        await App.Client.ApplyStrategyAsync(id);
        await LoadStrategiesAsync();
    }

    /// <summary>
    /// The full sweep, which is the one thing here that genuinely takes minutes and interrupts the bypass. It
    /// lives behind this switch rather than on the main screen for exactly that reason.
    /// </summary>
    private async void OnFullSweep(object sender, RoutedEventArgs e)
    {
        SweepButton.IsEnabled = false;
        SweepButton.Content = T["adv.str.sweeping"];
        SweepProgress.Visibility = Visibility.Visible;

        try
        {
            await App.Client.RunFullTestAsync();
            await LoadStrategiesAsync();
        }
        finally
        {
            SweepProgress.Visibility = Visibility.Collapsed;
            SweepButton.Content = T["adv.str.fullSweep"];
            SweepButton.IsEnabled = _canModify;
        }
    }

    // ---- services ----------------------------------------------------------------------------

    private async Task LoadServicesAsync()
    {
        var catalog = await App.Client.GetCatalogAsync();

        _services.Clear();
        if (catalog is null) return;

        foreach (var item in catalog.Items)
        {
            var id = item.Id;
            var enabled = item.IsEnabled;

            _services.Add(new ServiceRow(
                id,
                string.Join(", ", item.Domains),
                enabled,
                _canModify,
                item.IsCustom ? Visibility.Visible : Visibility.Collapsed,
                new Act(() => ToggleAsync(id, !enabled), _canModify),
                new Act(() => RemoveAsync(id), _canModify)));
        }

        AddServiceButton.IsEnabled = _canModify;
    }

    private async Task ToggleAsync(string id, bool enabled)
    {
        await App.Client.SetServiceEnabledAsync(id, enabled);
        await LoadServicesAsync();
    }

    private async Task RemoveAsync(string id)
    {
        await App.Client.RemoveCustomServiceAsync(id);
        await LoadServicesAsync();
    }

    private async void OnAddService(object sender, RoutedEventArgs e)
    {
        ServiceError.Visibility = Visibility.Collapsed;
        AddServiceButton.IsEnabled = false;

        try
        {
            var domains = NewDomains.Text
                .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)
                .ToList();

            var result = await App.Client.AddCustomServiceAsync(
                NewName.Text, domains, string.IsNullOrWhiteSpace(NewUrl.Text) ? null : NewUrl.Text.Trim());

            if (result is null || !result.Success)
            {
                // Validation failures come back as localisation keys, so the reason is specific.
                var key = result?.Message;
                ServiceError.Text = key is not null && key.StartsWith("service.error.", StringComparison.Ordinal)
                    ? T[key]
                    : key ?? T["up.offline"];

                ServiceError.Visibility = Visibility.Visible;
                return;
            }

            NewName.Clear();
            NewDomains.Clear();
            NewUrl.Clear();

            await LoadServicesAsync();
        }
        finally
        {
            AddServiceButton.IsEnabled = _canModify;
        }
    }

    // ---- engine ------------------------------------------------------------------------------

    private async Task LoadEngineAsync()
    {
        var status = await App.Client.GetStatusAsync();

        _rendering = true;
        try
        {
            EngineVersionText.Text = status?.EngineVersion ?? T["d.none"];

            GameFilter.ItemsSource = new[] { T["adv.eng.off"], "TCP + UDP", "TCP", "UDP" };
            GameFilter.SelectedIndex = status?.GameFilter switch
            {
                GameFilterMode.All => 1,
                GameFilterMode.TcpOnly => 2,
                GameFilterMode.UdpOnly => 3,
                _ => 0,
            };

            IpSet.ItemsSource = new[] { T["adv.eng.any"], T["adv.eng.off"], T["adv.eng.loaded"] };
            IpSet.SelectedIndex = status?.IpSet switch
            {
                IpSetMode.None => 1,
                IpSetMode.Loaded => 2,
                _ => 0,
            };

            HostsState.Text = status?.ManagedHostsApplied == true ? T["adv.eng.on"] : T["adv.eng.off"];

            var capabilities = status?.Capabilities ?? UpstreamCapabilities.None;

            GameFilter.IsEnabled = _canModify && capabilities.SupportsGameFilter;
            IpSet.IsEnabled = _canModify && capabilities.SupportsIpSetFilter;
            UpdateIpSetButton.IsEnabled = _canModify && capabilities.SupportsIpSetUpdate;
            ApplyHostsButton.IsEnabled = _canModify && capabilities.SupportsHostsUpdater;
            RemoveHostsButton.IsEnabled = _canModify && status?.ManagedHostsApplied == true;
            SweepButton.IsEnabled = _canModify && capabilities.SupportsStrategyTests;
        }
        finally
        {
            _rendering = false;
        }
    }

    private async void OnGameFilter(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;

        var mode = GameFilter.SelectedIndex switch
        {
            1 => GameFilterMode.All,
            2 => GameFilterMode.TcpOnly,
            3 => GameFilterMode.UdpOnly,
            _ => GameFilterMode.Off,
        };

        await App.Client.SetGameFilterAsync(mode);
        await LoadEngineAsync();
    }

    private async void OnIpSet(object sender, SelectionChangedEventArgs e)
    {
        if (_rendering) return;

        var mode = IpSet.SelectedIndex switch
        {
            1 => IpSetMode.None,
            2 => IpSetMode.Loaded,
            _ => IpSetMode.Any,
        };

        await App.Client.SetIpSetModeAsync(mode);
        await LoadEngineAsync();
    }

    private async void OnUpdateIpSet(object sender, RoutedEventArgs e)
    {
        UpdateIpSetButton.IsEnabled = false;
        await App.Client.UpdateIpSetListAsync();
        await LoadEngineAsync();
    }

    private async void OnApplyHosts(object sender, RoutedEventArgs e)
    {
        ApplyHostsButton.IsEnabled = false;
        await App.Client.ApplyHostsAsync();
        await LoadEngineAsync();
    }

    private async void OnRemoveHosts(object sender, RoutedEventArgs e)
    {
        RemoveHostsButton.IsEnabled = false;
        await App.Client.RemoveHostsAsync();
        await LoadEngineAsync();
    }

    // ---- diagnostics -------------------------------------------------------------------------

    private async Task LoadDiagnosticsAsync()
    {
        var status = await App.Client.GetStatusAsync();
        var state = App.Client.State;
        var log = await App.Client.GetLogTailAsync("service", 200);

        var report = new StringBuilder()
            .AppendLine($"{"stage",-22}{state.Stage}")
            .AppendLine($"{"manager",-22}{status?.ManagerVersion ?? "-"}")
            .AppendLine($"{"engine",-22}{status?.EngineVersion ?? "-"}  ({status?.EngineVersionSource ?? "-"})")
            .AppendLine($"{"strategy",-22}{status?.StrategyId ?? "-"}")
            .AppendLine($"{"strategies usable",-22}{status?.SupportedStrategyCount ?? 0}")
            .AppendLine($"{"compatibility",-22}{status?.CompatibilityOutcome?.ToString() ?? "-"}")
            .AppendLine($"{"run mode",-22}{status?.RunMode}")
            .AppendLine($"{"game filter",-22}{status?.GameFilter}")
            .AppendLine($"{"ipset",-22}{status?.IpSet}")
            .AppendLine($"{"hosts block",-22}{status?.ManagedHostsApplied}")
            .AppendLine($"{"network",-22}{status?.NetworkKindKey ?? "-"}")
            .AppendLine($"{"bypass needed",-22}{state.BypassNeeded?.ToString() ?? "-"}")
            .AppendLine($"{"administrator",-22}{status?.IsElevatedCaller}")
            .AppendLine()
            .AppendLine($"runtime  {AppPaths.RuntimeVersions}")
            .AppendLine($"data     {AppPaths.Data}")
            .AppendLine($"logs     {AppPaths.Logs}")
            .AppendLine();

        if (status is not null && status.CompatibilityNotes.Count > 0)
        {
            foreach (var note in status.CompatibilityNotes) report.AppendLine("note: " + note);
            report.AppendLine();
        }

        foreach (var verdict in state.Verdicts)
        {
            report.AppendLine($"service  {verdict.ServiceId,-14}{(verdict.Reachable ? "ok" : "fail")}  {verdict.Milliseconds?.ToString() ?? "-"}");
        }

        report.AppendLine().AppendLine("--- service log ---").AppendLine(log);

        DiagnosticsText.Text = report.ToString();
    }

    private async void OnRefreshDiagnostics(object sender, RoutedEventArgs e) => await LoadDiagnosticsAsync();

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(DiagnosticsText.Text);
        }
        catch (Exception)
        {
            // The clipboard can be held by another process; not worth interrupting anyone over.
        }
    }

    // ---- shell -------------------------------------------------------------------------------

    private void OnSection(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton { Tag: string key }) return;

        Strategies.Visibility = key == "strategies" ? Visibility.Visible : Visibility.Collapsed;
        Services.Visibility = key == "services" ? Visibility.Visible : Visibility.Collapsed;
        Engine.Visibility = key == "engine" ? Visibility.Visible : Visibility.Collapsed;
        Diagnostics.Visibility = key == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
