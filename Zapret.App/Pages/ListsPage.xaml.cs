using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Zapret.App.Pages;

public partial class ListsPage : Page
{
    private readonly ManagerClient _client;
    private bool _loaded;

    public ListsPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        ListCombo.SelectedIndex = 0;

        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue) return;

            await _client.RefreshAsync();
            UpdateEnabled();
            if (!_loaded) await LoadAsync();
        };
    }

    private string SelectedList =>
        (ListCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "list-general-user.txt";

    private void UpdateEnabled()
    {
        var canModify = _client.CanModify && _client.Status?.Capabilities.SupportsUserDomainLists == true;
        SaveButton.IsEnabled = canModify;
        Editor.IsReadOnly = !canModify;

        HintText.Text = canModify
            ? string.Empty
            : _client.ServiceAvailable
                ? "Editing requires administrator rights."
                : "The background service is not running.";
    }

    private async Task LoadAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            var payload = await _client.GetUserListAsync(SelectedList);
            Editor.Text = payload?.Content ?? string.Empty;
            _loaded = true;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnListChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        await LoadAsync();
    }

    private async void OnReload(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        Busy.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = false;

        try
        {
            var name = SelectedList;
            var outcome = await _client.SaveUserListAsync(name, Editor.Text);

            // An empty list breaks upstream's strategies, so the service restores the placeholder and
            // the editor is refreshed to show exactly what was stored.
            await LoadAsync();

            MainWindow.ShowMessage(
                name,
                outcome.Success ? outcome.Message ?? "Saved." : outcome.Message ?? "The list could not be saved.",
                outcome.Success ? ControlAppearance.Success : ControlAppearance.Caution);
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            UpdateEnabled();
        }
    }
}
