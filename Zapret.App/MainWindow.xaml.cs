using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Zapret.App.Localization;
using Zapret.App.Pages;
using Zapret.App.ViewModels;
using Zapret.Core.Update;

namespace Zapret.App;

/// <summary>
/// The shell: sidebar, page host and status strip. It owns page instances so navigating back to a page keeps
/// its state, and it owns the dashboard view model so the status strip and the dashboard cannot disagree.
/// </summary>
public partial class MainWindow : Window
{
    private static MainWindow? _instance;

    private readonly ManagerClient _client;
    private readonly DashboardViewModel _dashboard;
    private readonly Dictionary<string, Func<Page>> _factories;
    private readonly Dictionary<string, Page> _pages = new();

    public MainWindow(ManagerClient client)
    {
        InitializeComponent();

        _instance = this;
        _client = client;
        _dashboard = new DashboardViewModel(client);

        DataContext = _dashboard;
        StatusStrip.DataContext = _dashboard;

        _factories = new Dictionary<string, Func<Page>>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = () => new DashboardPage(_dashboard),
            ["strategies"] = () => new StrategiesPage(client),
            ["services"] = () => new ServicesPage(client),
            ["diagnostics"] = () => new DiagnosticsPage(client),
            ["network"] = () => new NetworkPage(client),
            ["settings"] = () => new SettingsPage(client),
            ["updates"] = () => new UpdatesPage(client),
        };

        App.Navigate = key => Dispatcher.Invoke(() => SelectByKey(key));

        _client.Changed += () => Dispatcher.Invoke(RenderVersions);
        Loc.Instance.LanguageChanged += RenderVersions;

        Loaded += async (_, _) =>
        {
            SelectByKey("home");
            RenderVersions();
            await _dashboard.InitializeAsync();
        };
    }

    /// <summary>Versions come from live state: the engine's from the service, the manager's from its assembly.</summary>
    private void RenderVersions()
    {
        EngineVersionText.Text = _client.Status?.EngineVersion ?? Loc.Instance["version.unknown"];
        ManagerVersionText.Text = ManagerUpdateService.InstalledVersion;
    }

    private void SelectByKey(string key)
    {
        foreach (var item in Nav.Items.OfType<ListBoxItem>())
        {
            if (!string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase)) continue;

            Nav.SelectedItem = item;
            return;
        }
    }

    private void OnNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string key }) return;
        if (!_factories.TryGetValue(key, out var factory)) return;

        if (!_pages.TryGetValue(key, out var page))
        {
            page = factory();
            _pages[key] = page;
        }

        PageHost.Content = page;
    }

    /// <summary>
    /// Cycles the UI language. A language picker also lives in Settings; this is the one-click affordance the
    /// reference has room for in the title bar.
    /// </summary>
    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        var languages = Loc.Instance.Languages;
        var index = languages.ToList().FindIndex(l => l.Tag == Loc.Instance.CurrentTag);
        var next = languages[(index + 1) % languages.Count];

        Loc.Instance.Apply(next.Tag);
        App.Settings.Update(s => s.Language = next.Tag);

        LanguageButton.ToolTip = next.NativeName;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => SelectByKey("settings");

    private void OnOpenUpdates(object sender, RoutedEventArgs e) => SelectByKey("updates");

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Closing leaves the application in the tray, the way a lifecycle controller should behave. Exit is an
    /// explicit choice in the tray menu.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    /// <summary>
    /// Feedback for an action the user just took. Успех виден по самому состоянию на экране, so only problems
    /// interrupt: a failure the user cannot see otherwise gets a native dialog, everything else stays quiet.
    /// </summary>
    public static void ShowMessage(string title, string message, Wpf.Ui.Controls.ControlAppearance appearance = Wpf.Ui.Controls.ControlAppearance.Secondary)
    {
        var window = _instance;
        if (window is null) return;

        if (appearance is not (Wpf.Ui.Controls.ControlAppearance.Caution or Wpf.Ui.Controls.ControlAppearance.Danger)) return;

        window.Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
            window, message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    }
}
