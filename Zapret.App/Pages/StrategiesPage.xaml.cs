using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.Core.Ipc;

namespace Zapret.App.Pages;

public partial class StrategiesPage : Page
{
    private readonly ManagerClient _client;
    private readonly ObservableCollection<StrategyRow> _rows = new();
    private bool _loaded;

    public StrategiesPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        StrategyList.ItemsSource = _rows;

        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue && !_loaded) await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            var payload = await _client.GetStrategiesAsync();

            _rows.Clear();

            if (payload is null || payload.Strategies.Count == 0)
            {
                SubtitleText.Text = _client.ServiceAvailable
                    ? "No engine is installed yet, so there are no strategies to show."
                    : "The background service could not be reached.";
                return;
            }

            foreach (var strategy in payload.Strategies)
            {
                _rows.Add(new StrategyRow(strategy));
            }

            var usable = payload.Strategies.Count(s => s.IsSupported);
            SubtitleText.Text =
                $"{usable} of {payload.Strategies.Count} strategies from Flowseal Zapret {payload.EngineVersion} are usable. " +
                "Discovered from the installed build — a new upstream strategy appears here without updating Запрет by Grubeer.";

            var selected = _rows.FirstOrDefault(r => r.IsSelected);
            if (selected is not null) StrategyList.SelectedItem = selected;

            _loaded = true;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var canModify = _client.CanModify;
        ApplyButton.IsEnabled = canModify && _rows.Count > 0;
        TestButton.IsEnabled = canModify && _client.Status?.Capabilities.SupportsStrategyTests == true;
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (StrategyList.SelectedItem is not StrategyRow row) return;

        if (!row.IsSupported)
        {
            MainWindow.ShowMessage("Strategy unavailable", row.Detail, ControlAppearance.Caution);
            return;
        }

        Busy.Visibility = Visibility.Visible;
        ApplyButton.IsEnabled = false;

        try
        {
            var outcome = await _client.ApplyStrategyAsync(row.Id);

            MainWindow.ShowMessage(
                row.DisplayName,
                outcome.Success ? outcome.Message ?? "Applied." : outcome.Message ?? "The strategy could not be applied.",
                outcome.Success ? ControlAppearance.Success : ControlAppearance.Caution);

            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private async void OnRunTests(object sender, RoutedEventArgs e)
    {
        Busy.Visibility = Visibility.Visible;
        TestButton.IsEnabled = false;

        MainWindow.ShowMessage("Strategy tests", "The upstream test utility is running. This takes a while.");

        try
        {
            var outcome = await _client.RunStrategyTestsAsync();

            MainWindow.ShowMessage(
                "Strategy tests",
                outcome.Success ? "Testing completed." : outcome.Message ?? "The test utility reported a failure.",
                outcome.Success ? ControlAppearance.Success : ControlAppearance.Caution);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            UpdateButtons();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        await _client.RefreshAsync();
        _loaded = false;
        await LoadAsync();
    }

    /// <summary>One row of the strategy list. Unsupported strategies stay visible, with the reason.</summary>
    private sealed class StrategyRow(StrategyPayload payload)
    {
        public string Id { get; } = payload.Id;

        public string DisplayName { get; } = payload.DisplayName;

        public bool IsSupported { get; } = payload.IsSupported;

        public bool IsSelected { get; } = payload.IsSelected;

        public string Detail { get; } = payload.IsSupported
            ? $"{payload.ArgumentCount} engine arguments"
            : payload.UnsupportedReason ?? "Not usable with the installed engine build.";

        public SymbolRegular Glyph { get; } = payload.IsSupported
            ? (payload.IsSelected ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Circle24)
            : SymbolRegular.Warning24;

        public Visibility SelectedVisibility { get; } = payload.IsSelected ? Visibility.Visible : Visibility.Collapsed;
    }
}
