using System.Windows;
using System.Windows.Controls;
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
}
