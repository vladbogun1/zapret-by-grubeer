using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using Zapret.App.Localization;
using Zapret.App.ViewModels;
using Zapret.Core.Ipc;

namespace Zapret.App.Pages;

/// <summary>
/// The service catalog: what a user actually wants unblocked, without having to learn which domains belong to
/// which product or edit a text file (SPEC.md §18, §34). Toggling a service rewrites only the manager-owned
/// block of the user list, so anything added there by hand survives.
/// </summary>
public partial class ServicesPage : Page
{
    private static Loc L => Loc.Instance;

    private readonly ManagerClient _client;
    private readonly ObservableCollection<ServiceCategoryViewModel> _categories = new();
    private bool _loaded;

    public ServicesPage(ManagerClient client)
    {
        InitializeComponent();

        _client = client;
        CategoryList.ItemsSource = _categories;
        DataContext = this;

        Loc.Instance.LanguageChanged += () => _ = LoadAsync();

        IsVisibleChanged += async (_, e) =>
        {
            if (!(bool)e.NewValue) return;

            await _client.RefreshAsync();
            if (!_loaded) await LoadAsync();
        };
    }

    private async Task LoadAsync()
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            var catalog = await _client.GetServiceCatalogAsync();

            _categories.Clear();
            if (catalog is null) return;

            // Grouped in the order the specification lists the categories, and an empty category is not shown.
            foreach (var group in catalog.Items
                         .GroupBy(i => i.CategoryKey)
                         .OrderBy(g => CategoryOrder(g.Key)))
            {
                _categories.Add(new ServiceCategoryViewModel(
                    L[group.Key],
                    group.Select(item => new ServiceToggleViewModel(item, _client.CanModify, ToggleAsync, RemoveAsync))));
            }

            // Manual entries are the user's own; say they are respected rather than leaving them a mystery.
            if (catalog.ManualEntryCount > 0)
            {
                ManualNoteText.Text = L.Format("services.manualEntries", catalog.ManualEntryCount);
                ManualNote.Visibility = Visibility.Visible;
            }
            else
            {
                ManualNote.Visibility = Visibility.Collapsed;
            }

            AddButton.IsEnabled = _client.CanModify;
            _loaded = true;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private static int CategoryOrder(string key) => key switch
    {
        "category.messaging" => 0,
        "category.video" => 1,
        "category.infrastructure" => 2,
        "category.ai" => 3,
        "category.social" => 4,
        _ => 5,
    };

    private async Task ToggleAsync(string id, bool enabled)
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            Report(await _client.SetServiceEnabledAsync(id, enabled), id);

            // Reload rather than trust the click: the file is the truth about what is enabled.
            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RemoveAsync(string id)
    {
        Busy.Visibility = Visibility.Visible;
        try
        {
            Report(await _client.RemoveCustomServiceAsync(id), id);

            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnAddCustom(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var domains = DomainsBox.Text
            .Split([',', ';', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        Busy.Visibility = Visibility.Visible;
        AddButton.IsEnabled = false;

        try
        {
            var outcome = await _client.AddCustomServiceAsync(
                NameBox.Text,
                domains,
                string.IsNullOrWhiteSpace(CheckUrlBox.Text) ? null : CheckUrlBox.Text.Trim());

            if (!outcome.Success)
            {
                // The service returns a localisation key for validation failures, so the reason is specific.
                ErrorText.Text = outcome.NeedsElevation
                    ? L["common.readOnly"]
                    : outcome.Message is { } key && key.StartsWith("service.error.", StringComparison.Ordinal)
                        ? L[key]
                        : outcome.Message ?? L["updates.failed"];

                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            NameBox.Clear();
            DomainsBox.Clear();
            CheckUrlBox.Clear();

            _loaded = false;
            await LoadAsync();
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = _client.CanModify;
        }
    }

    private static void Report(OperationOutcome outcome, string title)
    {
        if (outcome.Success) return;

        MainWindow.ShowMessage(
            title,
            outcome.NeedsElevation ? L["common.readOnly"] : outcome.Message ?? L["updates.failed"],
            ControlAppearance.Caution);
    }
}

public sealed class ServiceCategoryViewModel(string title, IEnumerable<ServiceToggleViewModel> items)
{
    public string Title { get; } = title;

    public IReadOnlyList<ServiceToggleViewModel> Items { get; } = items.ToList();
}

public sealed class ServiceToggleViewModel
{
    public ServiceToggleViewModel(
        ServiceCatalogItem item,
        bool canModify,
        Func<string, bool, Task> toggle,
        Func<string, Task> remove)
    {
        Id = item.Id;
        IsEnabled = item.IsEnabled;
        IsCustom = item.IsCustom;
        CanToggle = canModify;
        DomainsText = string.Join(", ", item.Domains);

        ToggleCommand = new RelayCommand(() => toggle(Id, !IsEnabled), () => canModify);
        RemoveCommand = new RelayCommand(() => remove(Id), () => canModify && IsCustom);
    }

    public string Id { get; }
    public string DomainsText { get; }
    public bool IsEnabled { get; }
    public bool IsCustom { get; }
    public bool CanToggle { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand RemoveCommand { get; }
}
