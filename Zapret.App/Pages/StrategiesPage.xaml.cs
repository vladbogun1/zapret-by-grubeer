using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Zapret.App.Localization;
using Zapret.App.ViewModels;
using Zapret.Core.Ipc;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Zapret.App.Pages;

/// <summary>
/// The strategy catalog. Everything shown is discovered from the installed engine build and, where a sweep
/// has run, annotated with what it measured. Upstream .bat filenames stay out of the normal view: the id is
/// used internally, the variant name is what a user reads (SPEC.md §17).
/// </summary>
public partial class StrategiesPage : Page
{
    private readonly ManagerClient _client;
    private readonly ObservableCollection<StrategyCardViewModel> _rows = new();
    private bool _loaded;

    public StrategiesPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        StrategyList.ItemsSource = _rows;
        DataContext = this;

        QuickTestCommand = new RelayCommand(QuickTestAsync, () => _client.ServiceAvailable);
        FullTestCommand = new RelayCommand(FullTestAsync, () => _client.CanModify);
        PickBestCommand = new RelayCommand(PickBestAsync, () => _client.CanModify && _rows.Any(r => r.HasScore));

        Loc.Instance.LanguageChanged += () => _ = LoadAsync();

        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue) return;

            await _client.RefreshAsync();
            if (!_loaded) await LoadAsync();
        };
    }

    public RelayCommand QuickTestCommand { get; }
    public RelayCommand FullTestCommand { get; }
    public RelayCommand PickBestCommand { get; }

    public string Subtitle { get; private set; } = string.Empty;

    public string FullTestText { get; private set; } = Loc.Instance["quick.fullTest"];

    private async Task LoadAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            var catalog = await _client.GetStrategiesAsync();
            var results = await _client.GetTestResultsAsync();

            _rows.Clear();

            if (catalog is null || catalog.Strategies.Count == 0)
            {
                Subtitle = _client.ServiceAvailable
                    ? Loc.Instance["hero.subtitle.noEngine"]
                    : Loc.Instance["hero.subtitle.noService"];

                Refresh();
                return;
            }

            // Scores are only trustworthy for the engine and network they were measured on.
            var scores = results is { IsCurrent: true }
                ? results.Items.ToDictionary(i => i.StrategyId, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, StrategyResultItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var strategy in catalog.Strategies)
            {
                scores.TryGetValue(strategy.Id, out var score);
                _rows.Add(new StrategyCardViewModel(strategy, score, ApplyAsync));
            }

            var usable = catalog.Strategies.Count(s => s.IsSupported);
            Subtitle = scores.Count > 0
                ? Loc.Instance.Format("strategies.subtitleTested", usable, catalog.EngineVersion)
                : Loc.Instance.Format("strategies.subtitle", usable, catalog.EngineVersion);

            _loaded = true;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            Refresh();
        }
    }

    private void Refresh()
    {
        FullTestText = Loc.Instance["quick.fullTest"];

        QuickTestCommand.Refresh();
        FullTestCommand.Refresh();
        PickBestCommand.Refresh();

        // The page is its own small view model; these are the only computed strings it exposes.
        DataContext = null;
        DataContext = this;
        StrategyList.ItemsSource = _rows;
    }

    private async Task ApplyAsync(string strategyId)
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            var outcome = await _client.ApplyStrategyAsync(strategyId);
            if (!outcome.Success)
            {
                MainWindow.ShowMessage(
                    Loc.Instance["nav.strategies"],
                    outcome.NeedsElevation ? Loc.Instance["common.readOnly"] : outcome.Message ?? string.Empty,
                    Wpf.Ui.Controls.ControlAppearance.Caution);
            }

            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async Task QuickTestAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            await _client.ProbeServicesAsync();
            await _client.RefreshAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async Task FullTestAsync()
    {
        Busy.Visibility = Visibility.Visible;
        FullTestText = Loc.Instance["hero.testing"];
        Refresh();

        try
        {
            await _client.RunFullTestAsync();
            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async Task PickBestAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            var outcome = await _client.ApplyBestStrategyAsync();
            if (!outcome.Success)
            {
                MainWindow.ShowMessage(Loc.Instance["nav.strategies"], outcome.Message ?? string.Empty,
                    Wpf.Ui.Controls.ControlAppearance.Caution);
            }

            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await _client.RefreshAsync();
        _loaded = false;
        await LoadAsync();
    }
}

/// <summary>One row of the catalog, combining what discovery found with what the last sweep measured.</summary>
public sealed class StrategyCardViewModel
{
    private readonly Func<string, Task> _apply;

    public StrategyCardViewModel(StrategyPayload payload, StrategyResultItem? score, Func<string, Task> apply)
    {
        _apply = apply;

        Id = payload.Id;
        DisplayName = payload.DisplayName;
        IsSelected = payload.IsSelected;
        IsRecommended = score?.IsBest == true && !payload.IsSelected;
        HasScore = score is not null;
        CanApply = payload.IsSupported && !payload.IsSelected;

        ScoreText = score is null ? "—" : $"{score.SuccessPercent}%";
        LatencyText = score?.AveragePing is { } ping ? $"{ping} ms" : string.Empty;

        ScoreBrush = score is null ? Brushes.Gray
            : score.SuccessPercent >= 90 ? Brushes.LimeGreen
            : score.SuccessPercent >= 50 ? Brushes.Goldenrod
            : Brushes.IndianRed;

        Detail = !payload.IsSupported
            ? payload.UnsupportedReason ?? Loc.Instance["strategy.notTested"]
            : score is not null
                ? Loc.Instance.Format("strategy.servicesFormat", score.Passed, score.Total)
                : Loc.Instance["strategy.notTested"];

        StateText = !payload.IsSupported ? payload.UnsupportedReason ?? string.Empty
            : payload.IsSelected ? Loc.Instance["strategy.inUse"]
            : Loc.Instance["strategies.use"];

        Glyph = !payload.IsSupported ? "" : payload.IsSelected ? "" : "";

        StateBrush = !payload.IsSupported ? Brushes.IndianRed
            : payload.IsSelected ? Brushes.LimeGreen
            : (Brush)Brushes.Gray;

        ApplyCommand = new RelayCommand(() => _apply(Id), () => CanApply);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Detail { get; }
    public string StateText { get; }
    public string Glyph { get; }
    public Brush StateBrush { get; }
    public bool IsSelected { get; }
    public bool IsRecommended { get; }
    public bool HasScore { get; }
    public bool CanApply { get; }
    public string ScoreText { get; }
    public string LatencyText { get; }
    public Brush ScoreBrush { get; }
    public RelayCommand ApplyCommand { get; }
}
