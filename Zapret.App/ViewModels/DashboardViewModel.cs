using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;

// WinForms is referenced for the tray icon, so System.Drawing is in scope and collides with WPF's brush
// types. Explicit aliases keep every colour in this view model unambiguously WPF.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Zapret.App.Localization;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Ipc;
using Zapret.Core.Model;

namespace Zapret.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;

        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        Refresh();

        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>One row of the service-check panel.</summary>
public sealed class ServiceRowViewModel(string name) : ObservableObject
{
    private bool? _reachable;
    private int? _latency;

    public string Name { get; } = name;

    /// <summary>null means not checked yet — never rendered as a failure.</summary>
    public bool? Reachable
    {
        get => _reachable;
        set { if (Set(ref _reachable, value)) { Raise(nameof(StatusBrush)); Raise(nameof(Glyph)); Raise(nameof(StatusText)); } }
    }

    public int? Latency
    {
        get => _latency;
        set { if (Set(ref _latency, value)) Raise(nameof(StatusText)); }
    }

    public Brush StatusBrush => Reachable switch
    {
        true => Brushes.LimeGreen,
        false => Brushes.IndianRed,
        _ => Brushes.Gray,
    };

    /// <summary>Segoe MDL2 glyph, so status never depends on colour alone.</summary>
    public string Glyph => Reachable switch
    {
        true => "",
        false => "",
        _ => "",
    };

    public string StatusText => Reachable switch
    {
        true => Latency is null ? Loc.Instance["system.active"] : $"{Latency} ms",
        false => Loc.Instance["statusbar.problems"],
        _ => Loc.Instance["common.notChecked"],
    };
}

public sealed class SystemRowViewModel(string label, string value, Brush brush, string glyph)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
    public Brush ValueBrush { get; } = brush;
    public string Glyph { get; } = glyph;
}

public sealed class EventRowViewModel(EventItem item)
{
    public string Time { get; } = item.Utc.ToLocalTime().ToString("HH:mm");

    public string Text { get; } = item.Argument is null
        ? Loc.Instance[item.MessageKey]
        : Loc.Instance.Format(item.MessageKey, item.Argument);

    public Brush Dot { get; } = item.Level switch
    {
        ManagerEventLevel.Success => Brushes.LimeGreen,
        ManagerEventLevel.Warning => Brushes.Goldenrod,
        ManagerEventLevel.Error => Brushes.IndianRed,
        _ => Brushes.DodgerBlue,
    };
}

/// <summary>
/// The dashboard. Every value here comes from the service or from a completed test — when something has not
/// been measured, the corresponding property reports "no data" rather than inventing a number.
/// </summary>
public sealed class DashboardViewModel : ObservableObject
{
    private readonly ManagerClient _client;
    private readonly DispatcherTimer _timer;

    private StatusPayload? _status;
    private ServiceProbePayload? _probe;
    private DateTimeOffset? _lastProbeUtc;
    private bool _busy;
    private string? _busyText;

    public DashboardViewModel(ManagerClient client)
    {
        _client = client;

        Services = new ObservableCollection<ServiceRowViewModel>(
            Core.SystemIntegration.HttpTargetProbe.Targets.Select(t => new ServiceRowViewModel(t.Name)));

        ToggleCommand = new RelayCommand(ToggleAsync, () => CanModify && HasEngine);
        ProbeCommand = new RelayCommand(ProbeAsync, () => _client.ServiceAvailable);
        RestartServiceCommand = new RelayCommand(RestartEngineAsync, () => CanModify && HasEngine);

        _client.Changed += () => System.Windows.Application.Current?.Dispatcher.Invoke(OnStatusChanged);
        Loc.Instance.LanguageChanged += RaiseAllText;

        // One second, only for the uptime readout; everything else refreshes on push or on demand.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Raise(nameof(Uptime));
        _timer.Start();
    }

    public ObservableCollection<ServiceRowViewModel> Services { get; }

    public ObservableCollection<SystemRowViewModel> SystemRows { get; } = new();

    public ObservableCollection<EventRowViewModel> Events { get; } = new();

    /// <summary>Ranked strategy results. Empty until a test has actually run.</summary>
    public ObservableCollection<object> StrategyResults { get; } = new();

    public RelayCommand ToggleCommand { get; }
    public RelayCommand ProbeCommand { get; }
    public RelayCommand RestartServiceCommand { get; }

    public bool IsRunning => _status?.EngineStatus == EngineStatus.Running;

    public bool HasEngine => _status?.EngineVersion is not null;

    public bool CanModify => _client.CanModify;

    public bool IsBusy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) Raise(nameof(ShowBusy)); }
    }

    public bool ShowBusy => IsBusy;

    public string? BusyText
    {
        get => _busyText;
        private set => Set(ref _busyText, value);
    }

    // ---- hero ------------------------------------------------------------------------------

    public string HeadlinePrefix => Loc.Instance[IsRunning ? "hero.active" : "hero.inactive"];

    public string HeadlineAccent => Loc.Instance[IsRunning ? "hero.activeAccent" : "hero.inactiveAccent"];

    public Brush HeadlineAccentBrush => IsRunning ? Brushes.LimeGreen : Brushes.Goldenrod;

    public string Subtitle =>
        !_client.ServiceAvailable ? Loc.Instance["hero.subtitle.noService"]
        : !HasEngine ? Loc.Instance["hero.subtitle.noEngine"]
        : Loc.Instance[IsRunning ? "hero.subtitle.active" : "hero.subtitle.inactive"];

    public Brush StatusDotBrush => IsRunning ? Brushes.LimeGreen : Brushes.Gray;

    public string StrategyName => _status?.StrategyDisplayName ?? Loc.Instance["common.none"];

    /// <summary>
    /// Reported by the network subsystem when there is one. There is no profile detection yet, so this is
    /// honest about it instead of showing a plausible-looking placeholder.
    /// </summary>
    public string NetworkProfile => Loc.Instance["common.noData"];

    public string Uptime
    {
        get
        {
            if (!IsRunning || _status?.StartedUtc is null) return "—";

            var elapsed = DateTimeOffset.UtcNow - _status.StartedUtc.Value;
            return elapsed < TimeSpan.Zero ? "—" : $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        }
    }

    public string ToggleText => Loc.Instance[IsRunning ? "hero.stop" : "hero.start"];

    // ---- current strategy --------------------------------------------------------------------

    public bool HasTestData => _probe is not null;

    public string SuccessRateText => _probe is null
        ? "—"
        : $"{(int)Math.Round(100.0 * _probe.ReachableCount / Math.Max(1, _probe.Items.Count))}%";

    public double SuccessRatio => _probe is null
        ? 0
        : (double)_probe.ReachableCount / Math.Max(1, _probe.Items.Count);

    public string TestSuccessText => _probe is null
        ? Loc.Instance["common.notChecked"]
        : Loc.Instance.Format("strategy.servicesFormat", _probe.ReachableCount, _probe.Items.Count);

    public string AverageResponseText => _probe?.AverageMilliseconds is { } ms
        ? $"{ms} ms"
        : Loc.Instance["common.noData"];

    /// <summary>
    /// Packet loss is not measured yet — an HTTP reachability probe cannot produce it. Rather than print a
    /// fabricated percentage, the metric reports that no data exists.
    /// </summary>
    public string PacketLossText => Loc.Instance["common.noData"];

    public string LastTestText => _lastProbeUtc is null
        ? Loc.Instance["strategy.notTested"]
        : Loc.Instance.Format("strategy.lastTest", Relative(_lastProbeUtc.Value));

    public string StrategyBadge => Loc.Instance[IsRunning ? "strategy.inUse" : "strategy.recommended"];

    // ---- services ----------------------------------------------------------------------------

    public string ServicesSummary
    {
        get
        {
            if (_probe is null) return Loc.Instance["services.notChecked"];

            var failed = _probe.Items.Count - _probe.ReachableCount;
            return failed == 0
                ? Loc.Instance["services.allWorking"]
                : Loc.Instance.Format("services.problems", Loc.Instance.Format("services.serviceProblems", failed));
        }
    }

    public Brush ServicesSummaryBrush =>
        _probe is null ? (Brush)Brushes.Gray
        : _probe.ReachableCount == _probe.Items.Count ? Brushes.LimeGreen
        : Brushes.Goldenrod;

    // ---- bottom status bar -------------------------------------------------------------------

    public string StatusBarText =>
        IsBusy ? Loc.Instance["statusbar.testing"]
        : !_client.ServiceAvailable ? Loc.Instance["statusbar.noService"]
        : !HasEngine ? Loc.Instance["statusbar.noEngine"]
        : !IsRunning ? Loc.Instance["statusbar.stopped"]
        : _probe is not null && _probe.ReachableCount < _probe.Items.Count ? Loc.Instance["statusbar.problems"]
        : Loc.Instance["statusbar.healthy"];

    public Brush StatusBarBrush =>
        !_client.ServiceAvailable || !HasEngine ? (Brush)Brushes.Gray
        : !IsRunning ? Brushes.Goldenrod
        : _probe is not null && _probe.ReachableCount < _probe.Items.Count ? Brushes.Goldenrod
        : Brushes.LimeGreen;

    // ---- behaviour ---------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        await _client.RefreshAsync().ConfigureAwait(true);
        await LoadEventsAsync().ConfigureAwait(true);
    }

    private void OnStatusChanged()
    {
        _status = _client.Status;
        RebuildSystemRows();
        RaiseAllText();
        ToggleCommand.Refresh();
        ProbeCommand.Refresh();
        RestartServiceCommand.Refresh();
    }

    private async Task ToggleAsync()
    {
        IsBusy = true;
        BusyText = Loc.Instance[IsRunning ? "hero.stop" : "hero.start"];

        try
        {
            if (IsRunning) await _client.StopAsync().ConfigureAwait(true);
            else await _client.StartAsync().ConfigureAwait(true);

            await LoadEventsAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private async Task RestartEngineAsync()
    {
        IsBusy = true;
        try
        {
            await _client.StopAsync().ConfigureAwait(true);
            await _client.StartAsync().ConfigureAwait(true);
            await LoadEventsAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The service-check panel. Never infers a result from the engine merely being up.</summary>
    private async Task ProbeAsync()
    {
        IsBusy = true;
        BusyText = Loc.Instance["services.checking"];

        foreach (var row in Services)
        {
            row.Reachable = null;
            row.Latency = null;
        }

        try
        {
            var result = await _client.ProbeServicesAsync().ConfigureAwait(true);
            if (result is null) return;

            _probe = result;
            _lastProbeUtc = result.CheckedUtc ?? DateTimeOffset.UtcNow;

            foreach (var item in result.Items)
            {
                var row = Services.FirstOrDefault(s => s.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (row is null) continue;

                row.Reachable = item.Reachable;
                row.Latency = item.Milliseconds;
            }

            await LoadEventsAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
            RaiseAllText();
        }
    }

    private async Task LoadEventsAsync()
    {
        var payload = await _client.GetEventsAsync().ConfigureAwait(true);

        Events.Clear();
        if (payload is null) return;

        foreach (var item in payload.Items) Events.Add(new EventRowViewModel(item));
    }

    private void RebuildSystemRows()
    {
        SystemRows.Clear();
        if (_status is null) return;

        var loc = Loc.Instance;
        var running = IsRunning;

        SystemRows.Add(new SystemRowViewModel(loc["system.zapretService"],
            _client.ServiceAvailable ? loc["system.running"] : loc["system.stopped"],
            _client.ServiceAvailable ? Brushes.LimeGreen : Brushes.IndianRed, ""));

        SystemRows.Add(new SystemRowViewModel("WinDivert",
            running ? loc["system.running"] : loc["system.inactive"],
            running ? Brushes.LimeGreen : Brushes.Gray, ""));

        SystemRows.Add(new SystemRowViewModel("winws.exe",
            running ? loc["system.active"] : loc["system.inactive"],
            running ? Brushes.LimeGreen : Brushes.Gray, ""));

        SystemRows.Add(new SystemRowViewModel(loc["system.gameFilter"],
            _status.GameFilter == GameFilterMode.Off ? loc["system.disabled"] : new GameFilterState(_status.GameFilter).Description,
            _status.GameFilter == GameFilterMode.Off ? Brushes.Gray : Brushes.LimeGreen, ""));

        SystemRows.Add(new SystemRowViewModel(loc["system.ipsetFilter"],
            _status.IpSet switch
            {
                IpSetMode.Loaded => loc["system.loaded"],
                IpSetMode.None => loc["system.recommended"],
                _ => loc["system.any"],
            },
            _status.IpSet == IpSetMode.Loaded ? Brushes.LimeGreen : Brushes.Goldenrod, ""));
    }

    private static string Relative(DateTimeOffset when)
    {
        var elapsed = DateTimeOffset.UtcNow - when;

        return elapsed switch
        {
            { TotalMinutes: < 1 } => Loc.Instance["common.justNow"],
            { TotalHours: < 1 } => Loc.Instance.Format("common.minutesAgo", (int)elapsed.TotalMinutes),
            _ => Loc.Instance.Format("common.hoursAgo", (int)elapsed.TotalHours),
        };
    }

    /// <summary>Everything on this screen is computed, so a state change refreshes the whole surface.</summary>
    private void RaiseAllText()
    {
        foreach (var name in new[]
                 {
                     nameof(IsRunning), nameof(HasEngine), nameof(CanModify), nameof(HeadlinePrefix),
                     nameof(HeadlineAccent), nameof(HeadlineAccentBrush), nameof(Subtitle), nameof(StatusDotBrush),
                     nameof(StrategyName), nameof(NetworkProfile), nameof(Uptime), nameof(ToggleText),
                     nameof(HasTestData), nameof(SuccessRateText), nameof(SuccessRatio), nameof(TestSuccessText),
                     nameof(AverageResponseText), nameof(PacketLossText), nameof(LastTestText), nameof(StrategyBadge),
                     nameof(ServicesSummary), nameof(ServicesSummaryBrush), nameof(StatusBarText), nameof(StatusBarBrush),
                 })
        {
            Raise(name);
        }
    }
}
