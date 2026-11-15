using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SabhaMujic.Client;
using SabhaMujic.Contracts;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.ViewModels.Admin;

/// <summary>Admin: create, rename, delete and reorder categories.</summary>
public sealed partial class CategoriesAdminViewModel : ViewModelBase
{
    private readonly ISabhaClient _client;
    private readonly UiServices _ui;

    public CategoriesAdminViewModel(ISabhaClient client, UiServices ui)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
    }

    /// <summary>Categories in display order.</summary>
    public ObservableCollection<CategoryDto> Categories { get; } = [];

    [ObservableProperty]
    public partial CategoryDto? SelectedCategory { get; set; }

    /// <summary>Raised after categories were loaded, so other sections can refresh their pickers.</summary>
    public event Action<IReadOnlyList<CategoryDto>>? CategoriesLoaded;

    public Task<bool> LoadAsync() => TryAsync(ReloadAsync, clearError: false);

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = await _ui.Dialogs.PromptTextAsync("New category", "Category name:").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await TryAsync(async () =>
        {
            var created = await _client.Admin.CreateCategoryAsync(name.Trim()).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == created.Id);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RenameAsync(CategoryDto? category)
    {
        category ??= SelectedCategory;
        if (category is null)
        {
            return;
        }

        var name = await _ui.Dialogs.PromptTextAsync("Rename category", "New name:", category.Name).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == category.Name)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Admin.RenameCategoryAsync(category.Id, name.Trim()).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(CategoryDto? category)
    {
        category ??= SelectedCategory;
        if (category is null)
        {
            return;
        }

        var confirmed = await _ui.Dialogs.ConfirmAsync(
            "Delete category",
            $"Delete the category \"{category.Name}\"? Its {category.TrackCount} track(s) stay in the library without a category.",
            "Delete").ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await TryAsync(async () =>
        {
            await _client.Admin.DeleteCategoryAsync(category.Id).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task MoveUpAsync(CategoryDto? category) => MoveAsync(category ?? SelectedCategory, -1);

    [RelayCommand]
    private Task MoveDownAsync(CategoryDto? category) => MoveAsync(category ?? SelectedCategory, +1);

    private Task MoveAsync(CategoryDto? category, int delta)
    {
        if (category is null)
        {
            return Task.CompletedTask;
        }

        var order = Categories.Select(c => c.Id).ToList();
        var from = order.IndexOf(category.Id);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= order.Count)
        {
            return Task.CompletedTask;
        }

        order.RemoveAt(from);
        order.Insert(to, category.Id);
        return TryAsync(async () =>
        {
            await _client.Admin.ReorderCategoriesAsync(order).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            SelectedCategory = Categories.FirstOrDefault(c => c.Id == category.Id);
        });
    }

    private async Task ReloadAsync()
    {
        var selected = SelectedCategory?.Id;
        var list = await _client.Library.GetCategoriesAsync().ConfigureAwait(true);
        Categories.Clear();
        foreach (var c in list.OrderBy(c => c.SortOrder))
        {
            Categories.Add(c);
        }

        SelectedCategory = Categories.FirstOrDefault(c => c.Id == selected);
        CategoriesLoaded?.Invoke(Categories.ToArray());
    }
}
