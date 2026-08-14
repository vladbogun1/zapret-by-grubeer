using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.Core;

namespace Zapret.App.Pages;

public partial class SettingsPage : Page
{
    private bool _rendering;

    public SettingsPage()
    {
        InitializeComponent();

        var settings = App.Settings.Read();

        _rendering = true;
        ThemeCombo.SelectedIndex = settings.ThemeOverride?.ToLowerInvariant() switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        NotificationsBox.IsChecked = settings.NotificationsEnabled;
        _rendering = false;

        PathsText.Text = string.Join(Environment.NewLine,
            $"Application   {AppPaths.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar)}",
            $"Engine        {AppPaths.RuntimeVersions}",
            $"Data          {AppPaths.Data}",
            $"Logs          {AppPaths.Logs}",
            $"Your settings {AppPaths.LocalAppData}");

        RepositoriesText.Text = string.Join(Environment.NewLine,
            $"Manager   {settings.ManagerRepository}",
            $"Engine    {settings.EngineRepository}");
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

    private void OnOpenRuntime(object sender, RoutedEventArgs e) => Open(AppPaths.RuntimeVersions);

    private void OnOpenData(object sender, RoutedEventArgs e) => Open(AppPaths.Data);

    private static void Open(string path)
    {
        try
        {
            // The folder may not exist until the service has run once.
            if (!Directory.Exists(path))
            {
                MainWindow.ShowMessage("Folder", $"{path} does not exist yet.", ControlAppearance.Caution);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            MainWindow.ShowMessage("Folder", ex.Message, ControlAppearance.Caution);
        }
    }
}
