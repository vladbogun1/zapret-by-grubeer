using System.ComponentModel;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.App.Pages;

namespace Zapret.App;

public partial class MainWindow : FluentWindow
{
    private static MainWindow? _instance;

    public MainWindow(ManagerClient client)
    {
        InitializeComponent();

        _instance = this;
        Client = client;

        var provider = new PageProvider(new Dictionary<Type, Func<Page>>
        {
            [typeof(HomePage)] = () => new HomePage(client),
            [typeof(StrategiesPage)] = () => new StrategiesPage(client),
            [typeof(AboutPage)] = () => new AboutPage(),
        });

        Navigation.SetPageProviderService(provider);
        Navigation.Loaded += (_, _) => Navigation.Navigate(typeof(HomePage));
    }

    public ManagerClient Client { get; }

    /// <summary>
    /// Closing the window leaves the application in the tray, the way a lifecycle controller should
    /// behave. Exit is an explicit choice in the tray menu.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    /// <summary>Transient in-app feedback. Anything the user must see when the window is closed is a toast.</summary>
    public static void ShowMessage(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary)
    {
        var window = _instance;
        if (window is null || !window.IsVisible) return;

        window.Dispatcher.Invoke(() =>
            new Snackbar(window.Snackbar)
            {
                Title = title,
                Content = message,
                Appearance = appearance,
                Timeout = TimeSpan.FromSeconds(6),
            }.Show());
    }
}
