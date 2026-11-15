using System.Windows.Controls;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class PlaylistsView : UserControl
{
    public PlaylistsView()
    {
        InitializeComponent();
        ListBoxReorder.Attach(TrackList, (from, to) => DataContext is PlaylistsViewModel vm ? vm.MoveTrackAsync(from, to) : Task.CompletedTask);
    }
}
