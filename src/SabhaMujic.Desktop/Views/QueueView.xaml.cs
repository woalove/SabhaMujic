using System.Windows.Controls;
using System.Windows.Input;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        ListBoxReorder.Attach(QueueList, (from, to) => DataContext is QueueViewModel vm ? vm.MoveItemAsync(from, to) : Task.CompletedTask);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueList.SelectedItem is QueueItemViewModel item && !WindowPlacement.IsInsideButton(e.OriginalSource))
        {
            item.Owner.PlayItemCommand.Execute(item);
        }
    }
}
