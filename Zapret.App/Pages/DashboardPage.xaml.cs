using System.Windows;
using System.Windows.Controls;
using Zapret.App.ViewModels;

namespace Zapret.App.Pages;

/// <summary>
/// The dashboard view. Code-behind carries no business logic: it owns the view model's lifetime and turns
/// two navigation clicks into shell navigation, which is a view concern.
/// </summary>
public partial class DashboardPage : Page
{
    private readonly DashboardViewModel _model;
    private bool _initialized;

    public DashboardPage(DashboardViewModel model)
    {
        InitializeComponent();

        _model = model;
        DataContext = _model;

        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue || _initialized) return;

            _initialized = true;
            await _model.InitializeAsync();
        };
    }

    private void OnOpenDiagnostics(object sender, RoutedEventArgs e) => App.Navigate?.Invoke("diagnostics");

    private void OnOpenStrategies(object sender, RoutedEventArgs e) => App.Navigate?.Invoke("strategies");
}
