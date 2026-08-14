using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.App.Localization;
using Zapret.Core;
using Zapret.Core.Engine;
using Zapret.Core.Model;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace Zapret.App.Pages;

/// <summary>One health check: a label, a value, and an icon so status never depends on colour alone.</summary>
public sealed record CheckRowViewModel(string Label, string Value, Brush Brush, string Glyph);

/// <summary>
/// Where the technical detail lives, so the dashboard can stay answerable by a non-technical user
/// (SPEC.md §34). Everything here is observed state — nothing is inferred from the engine merely running.
/// </summary>
public partial class DiagnosticsPage : Page
{
    private static Loc L => Loc.Instance;

    private readonly ManagerClient _client;
    private readonly ObservableCollection<CheckRowViewModel> _checks = new();

    public DiagnosticsPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        CheckList.ItemsSource = _checks;
        LogCombo.SelectedIndex = 0;

        Loc.Instance.LanguageChanged += () => Render();

        IsVisibleChanged += async (_, e) =>
        {
            if ((bool)e.NewValue) await RefreshAsync();
        };
    }

    private string SelectedLog => (LogCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "service";

    private async Task RefreshAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            await _client.RefreshAsync();
            Render();

            LogView.Text = await _client.GetLogTailAsync(SelectedLog);
            LogView.ScrollToEnd();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private void Render()
    {
        var status = _client.Status;
        var running = status?.EngineStatus == EngineStatus.Running;
        var capabilities = status?.Capabilities ?? UpstreamCapabilities.None;

        _checks.Clear();

        Add(L["diagnostics.service"], _client.ServiceAvailable, _client.ServiceAvailable ? L["system.running"] : L["system.stopped"]);
        Add(L["diagnostics.rights"], status?.IsElevatedCaller == true,
            status?.IsElevatedCaller == true ? L["system.active"] : L["common.readOnly"]);
        Add(L["version.engine"], status?.EngineVersion is not null, status?.EngineVersion ?? L["version.unknown"]);
        Add("winws.exe", running, running ? L["system.active"] : L["system.inactive"]);
        Add("WinDivert", running, running ? L["system.running"] : L["system.inactive"]);
        Add(L["diagnostics.strategies"], (status?.SupportedStrategyCount ?? 0) > 0, (status?.SupportedStrategyCount ?? 0).ToString());
        Add(L["diagnostics.userLists"], capabilities.SupportsUserDomainLists, Mark(capabilities.SupportsUserDomainLists));
        Add(L["diagnostics.testUtility"], capabilities.SupportsStrategyTests, Mark(capabilities.SupportsStrategyTests));
        Add(L["diagnostics.hosts"], status?.ManagedHostsApplied == true,
            status?.ManagedHostsApplied == true ? L["system.active"] : L["system.disabled"]);

        // Compatibility is informational rather than pass/fail: limitations are normal and survivable.
        var outcome = status?.CompatibilityOutcome;
        _checks.Add(new CheckRowViewModel(
            L["diagnostics.compatibility"],
            outcome?.ToString() ?? L["common.noData"],
            outcome switch
            {
                CompatibilityOutcome.Compatible => Brushes.LimeGreen,
                CompatibilityOutcome.CompatibleWithLimitations => Brushes.Goldenrod,
                CompatibilityOutcome.Incompatible => Brushes.IndianRed,
                _ => Brushes.Gray,
            },
            outcome == CompatibilityOutcome.Compatible ? "" : ""));

        EnvironmentText.Text = BuildEnvironment(status);
        TestButton.IsEnabled = _client.CanModify && capabilities.SupportsStrategyTests;

        void Add(string label, bool healthy, string value) =>
            _checks.Add(new CheckRowViewModel(label, value,
                healthy ? Brushes.LimeGreen : Brushes.Gray,
                healthy ? "" : ""));
    }

    private static string Mark(bool value) => value ? "✓" : "✗";

    /// <summary>Facts worth pasting into a bug report, and nothing that identifies the machine or network.</summary>
    private string BuildEnvironment(Zapret.Core.Ipc.StatusPayload? status) =>
        new StringBuilder()
            .AppendLine($"{L["version.manager"],-22} {status?.ManagerVersion ?? Core.Update.ManagerUpdateService.InstalledVersion}")
            .AppendLine($"{L["version.engine"],-22} {status?.EngineVersion ?? L["version.unknown"]} ({status?.EngineVersionSource ?? "-"})")
            .AppendLine($"{L["hero.networkProfile"],-22} {(status?.NetworkKindKey is { } k ? L[k] : L["common.noData"])}")
            .AppendLine($"{L["hero.strategy"],-22} {status?.StrategyDisplayName ?? L["common.none"]}")
            .AppendLine()
            .AppendLine($"runtime   {AppPaths.RuntimeVersions}")
            .AppendLine($"data      {AppPaths.Data}")
            .Append($"logs      {AppPaths.Logs}")
            .ToString();

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnLogChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;

        LogView.Text = await _client.GetLogTailAsync(SelectedLog);
        LogView.ScrollToEnd();
    }

    private async void OnRunTests(object sender, RoutedEventArgs e)
    {
        Busy.Visibility = Visibility.Visible;
        TestButton.IsEnabled = false;

        try
        {
            var outcome = await _client.RunStrategyTestsAsync();
            if (!string.IsNullOrWhiteSpace(outcome.Message)) LogView.Text = outcome.Message;

            if (!outcome.Success)
            {
                MainWindow.ShowMessage(L["nav.diagnostics"], outcome.Message ?? L["updates.failed"], ControlAppearance.Caution);
            }
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            await RefreshAsync();
        }
    }

    /// <summary>Writes the report next to the logs, then shows it — the user decides where it goes from there.</summary>
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(AppPaths.LocalAppData, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var report = new StringBuilder()
                .AppendLine($"{AppPaths.DisplayName} diagnostics")
                .AppendLine(DateTimeOffset.Now.ToString("u"))
                .AppendLine()
                .AppendLine(EnvironmentText.Text)
                .AppendLine()
                .AppendLine(string.Join(Environment.NewLine, _checks.Select(c => $"{c.Label,-24} {c.Value}")))
                .AppendLine()
                .AppendLine("--- log tail ---")
                .AppendLine(await _client.GetLogTailAsync(SelectedLog, 500))
                .ToString();

            Directory.CreateDirectory(AppPaths.LocalAppData);
            await File.WriteAllTextAsync(path, report, new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MainWindow.ShowMessage(L["nav.diagnostics"], ex.Message, ControlAppearance.Caution);
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.Logs, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MainWindow.ShowMessage(L["nav.diagnostics"], ex.Message, ControlAppearance.Caution);
        }
    }
}
