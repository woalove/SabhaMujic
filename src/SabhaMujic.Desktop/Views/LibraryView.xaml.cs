using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>Ctrl+F target.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is TrackItemViewModel track && !WindowPlacement.IsInsideButton(e.OriginalSource))
        {
            track.Owner.PlayNowCommand.Execute(track);
        }
    }

    /// <summary>The row's chevron opens the same menu as a right-click.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackItemViewModel track } button &&
            TrackList.ItemContainerGenerator.ContainerFromItem(track) is ListBoxItem { ContextMenu: { } menu })
        {
            track.Owner.SelectedTrack = track;
            menu.DataContext = track;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnAddToPlaylistClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrackItemViewModel track } &&
            e.OriginalSource is MenuItem { DataContext: PlaylistItemViewModel playlist })
        {
            track.Owner.SelectedTrack = track;
            track.Owner.AddToPlaylistCommand.Execute(playlist);
            e.Handled = true;
        }
    }

    /// <summary>Admin: right-click a category tab for "Add tracks…".</summary>
    private void OnCategoryTabRightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not LibraryViewModel { CanManageCategories: true } ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(CategoryTabs, source) is not ListBoxItem { DataContext: CategoryItemViewModel { IsAll: false } category } tab)
        {
            return;
        }

        var menu = (ContextMenu)FindResource("CategoryTabMenu");
        menu.DataContext = category;
        menu.PlacementTarget = tab;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnAddTracksToCategoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CategoryItemViewModel category } && DataContext is LibraryViewModel library)
        {
            library.AddTracksToCategoryCommand.Execute(category);
        }
    }
}
