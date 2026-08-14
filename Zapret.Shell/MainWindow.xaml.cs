using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Zapret.Core.AutoSelect;
using Brush = System.Windows.Media.Brush;

namespace Zapret.Shell;

/// <summary>One row of the service list, already in the words the user reads.</summary>
public sealed record ServiceTile(string Name, string Verdict, Brush Brush, string Glyph);

/// <summary>One step of work that actually happened.</summary>
public sealed record StepLine(string Text, Brush Brush, string Glyph);

/// <summary>
/// The whole interface. One screen whose content is a function of one state, plus the onboarding question.
/// <para>
/// There is no navigation, no test button and no strategy picker. Everything the 1.x product asked the user to
/// do — test, choose, re-test after a network change — the service now does, and this window's job is to say
/// what is true and offer at most one action (docs/nextgen-ux.md §2, §3, §6).
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    private static Text T => Text.Current;

    private readonly List<ToggleButton> _tiles = new();
    private ProductState _state = ProductState.Unreachable;
    private bool _detailsOpen;

    public MainWindow()
    {
        InitializeComponent();

        App.Client.StateChanged += Render;
        Text.Current.Changed += () => Render(_state);

        Loaded += async (_, _) =>
        {
            var settings = App.Settings.Read();
            NotificationsBox.IsChecked = settings.NotificationsEnabled;
            AdvancedBox.IsChecked = settings.AdvancedMode;
            AdvancedButton.Visibility = settings.AdvancedMode ? Visibility.Visible : Visibility.Collapsed;

            Render(App.Client.State);
            await LoadTilesAsync();
        };
    }

    // ---- the expanded surface ----------------------------------------------------------------

    private AdvancedWindow? _advanced;

    private void OnAdvancedChanged(object sender, RoutedEventArgs e)
    {
        var on = AdvancedBox.IsChecked == true;

        App.Settings.Update(s => s.AdvancedMode = on);
        AdvancedButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (!on) _advanced?.Close();
    }

    /// <summary>
    /// A separate window rather than a section of this one: the main screen keeps its promise of four answers and
    /// one action, and the mechanism gets a place where nothing has to be hidden.
    /// </summary>
    public void OnOpenAdvanced(object? sender, RoutedEventArgs? e)
    {
        if (_advanced is { IsLoaded: true })
        {
            _advanced.Activate();
            return;
        }

        _advanced = new AdvancedWindow { Owner = IsVisible ? this : null };
        _advanced.Closed += (_, _) => _advanced = null;
        _advanced.Show();
    }

    private void OnNotificationsChanged(object sender, RoutedEventArgs e)
    {
        var on = NotificationsBox.IsChecked == true;

        App.Settings.Update(s => s.NotificationsEnabled = on);
        App.Notifications.Enabled = on;
    }

    // ---- onboarding --------------------------------------------------------------------------

    private async Task LoadTilesAsync()
    {
        var catalog = await App.Client.GetCatalogAsync();
        if (catalog is null) return;

        _tiles.Clear();
        Tiles.Items.Clear();

        // Pre-ticked: the four most common. Everything else is one click away, and nothing is required.
        var common = new[] { "Discord", "YouTube", "Telegram", "Instagram" };

        foreach (var item in catalog.Items)
        {
            var tile = new ToggleButton
            {
                Style = (Style)FindResource("Tile"),
                Content = item.Id,
                Tag = item.Id,
                IsChecked = item.IsEnabled || common.Contains(item.Id, StringComparer.OrdinalIgnoreCase),
            };

            _tiles.Add(tile);
            Tiles.Items.Add(tile);
        }
    }

    private IReadOnlyList<string> Selected =>
        _tiles.Where(t => t.IsChecked == true).Select(t => (string)t.Tag).ToList();

    private async void OnSetUp(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        try
        {
            await App.Client.SetUpAsync(Selected);
        }
        finally
        {
            StartButton.IsEnabled = true;
        }
    }

    /// <summary>Choosing nothing is a valid answer, not an error to be argued with.</summary>
    private async void OnSkip(object sender, RoutedEventArgs e) => await App.Client.SetUpAsync([]);

    // ---- rendering ---------------------------------------------------------------------------

    private void Render(ProductState state)
    {
        _state = state;

        var onboarding = state.Stage == ProductStage.FirstRun;
        Onboarding.Visibility = onboarding ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = onboarding ? Visibility.Collapsed : Visibility.Visible;

        // The language button shows the language it will switch to, which is clearer than a globe.
        LanguageGlyph.Text = Text.Current.Tag == "ru" ? "EN" : "RU";

        if (onboarding) return;

        var working = state.Stage == ProductStage.Working;
        var busy = state.Stage is ProductStage.Preparing or ProductStage.Repairing;

        Dot.Fill = Brushes(state.Stage);
        Dot.Effect = working ? (System.Windows.Media.Effects.Effect)FindResource("GlowOk") : null;

        Headline.Text = state.Stage switch
        {
            ProductStage.Working => T[state.BypassNeeded == false ? "stage.workingNoBypass" : "stage.working"],
            ProductStage.Preparing => T["stage.preparing"],
            ProductStage.Repairing or ProductStage.Degraded => T["stage.repairing"],
            ProductStage.Stuck => T["stage.stuck"],
            ProductStage.Off => T["stage.off"],
            _ => T["stage.unavailable"],
        };

        Subtitle.Text = state.Stage switch
        {
            ProductStage.Working => T[state.BypassNeeded == false ? "sub.workingNoBypass" : "sub.working"],
            ProductStage.Preparing or ProductStage.Repairing or ProductStage.Degraded => T["sub.repairing"],
            ProductStage.Off => T["sub.off"],
            ProductStage.Stuck => string.Empty,
            _ => T["sub.unavailable"],
        };

        RenderSteps(state, busy);
        RenderMetrics(state, busy);
        RenderServices(state);
        RenderAdvice(state);
        RenderAction(state, busy);
        RenderDetails(state);
    }

    private void RenderSteps(ProductState state, bool busy)
    {
        if (!busy || state.Steps.Count == 0)
        {
            Steps.Visibility = Visibility.Collapsed;
            return;
        }

        Steps.ItemsSource = state.Steps.Select(s => new StepLine(
            s.Argument is null ? T[s.MessageKey] : T.Format(s.MessageKey, s.Argument),
            (Brush)FindResource(s.Done ? "Ok" : "Fg2"),
            s.Done ? "" : "")).ToList();

        Steps.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Facts about the option in use, in the simple mode too. Knowing which one is running, for how long, and
    /// how it is doing is not an expert need — it is what stops a working product from feeling like a black box.
    /// The manual-choice button is here for the same reason: taking the wheel should never require a hunt.
    /// </summary>
    private void RenderMetrics(ProductState state, bool busy)
    {
        var show = !busy && state.Stage is ProductStage.Working or ProductStage.Degraded;
        MetricsCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (!show) return;

        MetricOption.Text = state.StrategyId is null
            ? T["m.none"]
            : Zapret.Core.Flowseal.StrategyBatParser.ToDisplayName(state.StrategyId);

        MetricUptime.Text = state.RunningSinceUtc is { } since && DateTimeOffset.UtcNow - since is var elapsed && elapsed > TimeSpan.Zero
            ? elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours} ч {elapsed.Minutes} мин"
                : $"{elapsed.Minutes} мин"
            : "—";

        // Average across what actually answered; a failing service contributes no latency to flatter it.
        var latencies = state.Verdicts.Where(v => v is { Reachable: true, Milliseconds: not null })
            .Select(v => v.Milliseconds!.Value).ToList();

        MetricResponse.Text = latencies.Count == 0 ? "—" : $"{(int)latencies.Average()} ms";

        MetricServices.Text = state.Verdicts.Count == 0
            ? "—"
            : $"{state.Verdicts.Count(v => v.Reachable)}/{state.Verdicts.Count}";

        MetricServices.Foreground = (Brush)FindResource(state.AllWorking ? "Ok" : "Warn");
    }

    private void RenderServices(ProductState state)
    {
        // A service the user named but which has not been measured yet says "checking", not "broken".
        var tiles = state.WatchedServices.Select(id =>
        {
            var verdict = state.Verdicts.FirstOrDefault(v => v.ServiceId == id);

            if (verdict is null)
            {
                return new ServiceTile(id, T["v.checking"], (Brush)FindResource("Fg3"), "");
            }

            var speed = verdict.SpeedKey is null ? null : T[verdict.SpeedKey];
            var text = verdict.Reachable
                ? speed is null ? T["v.ok"] : $"{T["v.ok"]} · {speed}"
                : T["v.fail"];

            return new ServiceTile(id, text,
                (Brush)FindResource(verdict.Reachable ? "Ok" : "Bad"),
                verdict.Reachable ? "" : "");
        }).ToList();

        Services.ItemsSource = tiles;
        Services.Visibility = tiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RenderAdvice(ProductState state)
    {
        if (state.AdviceKey is null || state.Stage != ProductStage.Stuck)
        {
            AdviceCard.Visibility = Visibility.Collapsed;
            return;
        }

        AdviceText.Text = T[state.AdviceKey];
        AdviceCard.Visibility = Visibility.Visible;
    }

    /// <summary>Exactly one primary action per stage. Never two buttons of equal weight.</summary>
    private void RenderAction(ProductState state, bool busy)
    {
        MainAction.Content = state.Stage switch
        {
            ProductStage.Off => T["do.turnOn"],
            ProductStage.Working => T["do.turnOff"],
            ProductStage.Stuck => T["do.retry"],
            ProductStage.Unavailable => T["do.retry"],
            _ => T["do.cancel"],
        };

        MainAction.IsEnabled = !busy || state.CanCancel;
        ReportButton.Visibility = state.Stage == ProductStage.Stuck ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderDetails(ProductState state)
    {
        var uptime = state.RunningSinceUtc is { } since
            ? (DateTimeOffset.UtcNow - since) is var elapsed && elapsed > TimeSpan.Zero
                ? $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}"
                : "—"
            : "—";

        DetailsText.Text = new StringBuilder()
            .AppendLine($"{T["d.strategy"],-18}{state.StrategyId ?? T["d.none"]}")
            .AppendLine($"{T["d.engine"],-18}{state.EngineVersion ?? T["d.none"]}")
            .Append($"{T["d.uptime"],-18}{uptime}")
            .ToString();
    }

    private Brush Brushes(ProductStage stage) => (Brush)FindResource(stage switch
    {
        ProductStage.Working => "Ok",
        ProductStage.Preparing or ProductStage.Repairing or ProductStage.Degraded => "Warn",
        ProductStage.Stuck or ProductStage.Unavailable => "Bad",
        _ => "Fg3",
    });

    // ---- actions -----------------------------------------------------------------------------

    private async void OnMainAction(object sender, RoutedEventArgs e)
    {
        MainAction.IsEnabled = false;
        try
        {
            switch (_state.Stage)
            {
                case ProductStage.Working:
                    await App.Client.TurnOffAsync();
                    break;

                case ProductStage.Preparing or ProductStage.Repairing:
                    await App.Client.CancelAsync();
                    break;

                default:
                    await App.Client.TurnOnAsync();
                    break;
            }
        }
        finally
        {
            MainAction.IsEnabled = true;
        }
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        var report = new StringBuilder()
            .AppendLine("Запрет by Grubeer")
            .AppendLine(DateTimeOffset.Now.ToString("u"))
            .AppendLine($"stage       {_state.Stage}")
            .AppendLine($"strategy    {_state.StrategyId ?? "-"}")
            .AppendLine($"engine      {_state.EngineVersion ?? "-"}")
            .AppendLine($"advice      {_state.AdviceKey ?? "-"}")
            .AppendLine()
            .AppendLine(string.Join(Environment.NewLine,
                _state.Verdicts.Select(v => $"  {v.ServiceId,-14}{(v.Reachable ? "ok" : "fail")}  {v.Milliseconds?.ToString() ?? "-"}")))
            .AppendLine()
            .AppendLine(string.Join(Environment.NewLine, _state.Steps.Select(s => $"  {s.MessageKey} {s.Argument}")))
            .ToString();

        try
        {
            System.Windows.Clipboard.SetText(report);
        }
        catch (Exception)
        {
            // The clipboard can be held by another process; failing to copy is not worth interrupting anyone.
        }
    }

    private async void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        _detailsOpen = !_detailsOpen;

        Details.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Content = T[_detailsOpen ? "do.hideDetails" : "do.details"];

        // Checked when the user opens the drawer, not on a timer: nobody needs an update badge on a screen
        // whose whole point is that nothing needs doing.
        if (_detailsOpen && !_updateChecked) await CheckUpdatesAsync();
    }

    // ---- updates: the only decision the product ever asks the user to make -------------------

    private bool _updateChecked;
    private Zapret.Core.GitHub.GitHubRelease? _managerRelease;

    private async void OnCheckUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync();

    private async Task CheckUpdatesAsync()
    {
        CheckButton.IsEnabled = false;
        UpdateText.Text = T["up.checking"];

        try
        {
            var info = await App.Updates.CheckAsync(force: true);
            _updateChecked = true;
            _managerRelease = info.Release;

            if (info.Status == Zapret.Core.GitHub.ReleaseCheckStatus.Unavailable)
            {
                UpdateText.Text = T["up.offline"];
                UpdateButton.Visibility = Visibility.Collapsed;
                return;
            }

            if (info.UpdateAvailable)
            {
                UpdateText.Text = T.Format("up.available", info.LatestVersion);
                UpdateButton.Content = T["up.update"];
                UpdateButton.Visibility = Visibility.Visible;
                return;
            }

            UpdateText.Text = T.Format("up.current", info.InstalledVersion, _state.EngineVersion ?? T["d.none"]);
            UpdateButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_managerRelease is null) return;

        UpdateButton.IsEnabled = false;
        try
        {
            var (started, error) = await App.Updates.DownloadAndRunInstallerAsync(_managerRelease);
            UpdateText.Text = started ? T["up.started"] : error ?? T["up.offline"];
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void OnLanguage(object sender, RoutedEventArgs e)
    {
        var languages = Text.Current.Languages;
        var index = languages.ToList().FindIndex(l => l.Tag == Text.Current.Tag);
        var next = languages[(index + 1) % languages.Count];

        Text.Current.Apply(next.Tag);
        App.Settings.Update(s => s.Language = next.Tag);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Closing leaves the product watching in the tray; exit is an explicit tray action.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
