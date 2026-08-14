using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.Core;
using Zapret.Core.Model;

namespace Zapret.App.Pages;

public partial class DiagnosticsPage : Page
{
    private readonly ManagerClient _client;

    public DiagnosticsPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        LogCombo.SelectedIndex = 0;

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
            RenderSelfCheck();
            LogView.Text = await _client.GetLogTailAsync(SelectedLog);
            LogView.ScrollToEnd();

            TestButton.IsEnabled = _client.CanModify && _client.Status?.Capabilities.SupportsStrategyTests == true;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Facts a user can act on or paste into a bug report: what is installed, where, and what the
    /// compatibility layer thinks of it.
    /// </summary>
    private void RenderSelfCheck()
    {
        var status = _client.Status;
        var builder = new StringBuilder();

        builder.AppendLine($"Manager           {status?.ManagerVersion ?? Core.Update.ManagerUpdateService.InstalledVersion}");
        builder.AppendLine($"Service           {(_client.ServiceAvailable ? "reachable" : _client.UnavailableReason ?? "not reachable")}");
        builder.AppendLine($"Administrator     {(status?.IsElevatedCaller == true ? "yes" : "no")}");
        builder.AppendLine($"Engine            {status?.EngineVersion ?? "not installed"}" +
                           (status?.EngineVersionSource is null ? string.Empty : $"  (from {status.EngineVersionSource})"));
        builder.AppendLine($"Strategies        {status?.SupportedStrategyCount ?? 0} usable");
        builder.AppendLine($"Compatibility     {status?.CompatibilityOutcome?.ToString() ?? "unknown"}");
        builder.AppendLine($"Engine runtime    {AppPaths.RuntimeVersions}");
        builder.AppendLine($"Data              {AppPaths.Data}");
        builder.AppendLine($"Logs              {AppPaths.Logs}");

        if (status is not null && status.CompatibilityNotes.Count > 0)
        {
            builder.AppendLine();
            foreach (var note in status.CompatibilityNotes) builder.AppendLine("• " + note);
        }

        var capabilities = status?.Capabilities ?? UpstreamCapabilities.None;
        builder.AppendLine();
        builder.AppendLine("Upstream capabilities detected in the installed build:");
        builder.AppendLine($"  service mode {Mark(capabilities.SupportsUpstreamServiceMode)}   game filter {Mark(capabilities.SupportsGameFilter)}   IPSet {Mark(capabilities.SupportsIpSetFilter)}");
        builder.AppendLine($"  user lists {Mark(capabilities.SupportsUserDomainLists)}   tests {Mark(capabilities.SupportsStrategyTests)}   hosts {Mark(capabilities.SupportsHostsUpdater)}   fakes {Mark(capabilities.SupportsFakeReplacement)}");

        SelfCheckText.Text = builder.ToString().TrimEnd();
    }

    private static string Mark(bool value) => value ? "✓" : "✗";

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnLogChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        LogView.Text = await _client.GetLogTailAsync(SelectedLog);
        LogView.ScrollToEnd();
    }

    private async void OnRunTests(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;

        MainWindow.ShowMessage("Engine tests", "The upstream test utility is running. This takes a while.");

        try
        {
            var outcome = await _client.RunStrategyTestsAsync();

            if (!string.IsNullOrWhiteSpace(outcome.Message)) LogView.Text = outcome.Message;

            MainWindow.ShowMessage(
                "Engine tests",
                outcome.Success ? "Testing completed." : outcome.Message ?? "The test utility reported a failure.",
                outcome.Success ? ControlAppearance.Success : ControlAppearance.Caution);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            await RefreshAsync();
        }
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.Logs, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MainWindow.ShowMessage("Logs", ex.Message, ControlAppearance.Caution);
        }
    }
}
