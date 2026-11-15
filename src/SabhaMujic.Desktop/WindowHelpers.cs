using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop;

internal static class WindowPlacement
{
    /// <summary>True when at least a 100×60 corner of the window would be visible on the virtual screen.</summary>
    public static bool IsOnScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screen, new Rect(left, top, Math.Max(width, 1), Math.Max(height, 1)));
        return !visible.IsEmpty && visible.Width >= 100 && visible.Height >= 60;
    }

    /// <summary>True when <paramref name="element"/> is (inside) a button within its list row.</summary>
    public static bool IsInsideButton(object? element)
    {
        for (var current = element as DependencyObject; current is not null; current = Parent(current))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            if (current is ListBoxItem)
            {
                return false;
            }
        }

        return false;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}

/// <summary>
/// Playback shortcuts shared by the main window and the mini player:
/// Space = play/pause, Ctrl+→ = next, Ctrl+← = previous. Ignored while typing in a text field.
/// </summary>
internal static class KeyboardShortcuts
{
    public static bool TryHandle(KeyEventArgs e, NowPlayingViewModel nowPlaying)
    {
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true })
        {
            return false;
        }

        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Space && modifiers == ModifierKeys.None)
        {
            nowPlaying.PlayPauseCommand.Execute(null);
            return true;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.Right)
        {
            nowPlaying.NextCommand.Execute(null);
            return true;
        }

        if (modifiers == ModifierKeys.Control && e.Key == Key.Left)
        {
            nowPlaying.PreviousCommand.Execute(null);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Drag-and-drop reordering for a ListBox: reports (oldIndex, newIndex) where newIndex is the final position
/// of the dragged item. Used by the queue and playlist views.
/// </summary>
internal sealed class ListBoxReorder
{
    private readonly ListBox _list;
    private readonly Func<int, int, Task> _move;
    private Point _start;
    private int _dragIndex = -1;

    private ListBoxReorder(ListBox list, Func<int, int, Task> move)
    {
        _list = list;
        _move = move;
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += OnMouseDown;
        list.PreviewMouseMove += OnMouseMove;
        list.DragOver += OnDragOver;
        list.Drop += OnDrop;
    }

    public static void Attach(ListBox list, Func<int, int, Task> move) => _ = new ListBoxReorder(list, move);

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_list);
        _dragIndex = e.OriginalSource is DependencyObject d && !WindowPlacement.IsInsideButton(d) ? IndexAt(d) : -1;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(_list) - _start;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
            Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        var from = _dragIndex;
        _dragIndex = -1;
        DragDrop.DoDragDrop(_list, new DataObject(typeof(ReorderPayload), new ReorderPayload(_list, from)), DragDropEffects.Move);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(typeof(ReorderPayload)) is ReorderPayload p && ReferenceEquals(p.Source, _list)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ReorderPayload)) is not ReorderPayload p || !ReferenceEquals(p.Source, _list))
        {
            return;
        }

        var target = e.OriginalSource is DependencyObject d ? IndexAt(d) : -1;
        if (target < 0)
        {
            target = _list.Items.Count - 1; // dropped below the last row
        }

        e.Handled = true;
        if (target != p.FromIndex)
        {
            _ = _move(p.FromIndex, target);
        }
    }

    private int IndexAt(DependencyObject element)
    {
        var container = ItemsControl.ContainerFromElement(_list, element) as ListBoxItem;
        return container is null ? -1 : _list.ItemContainerGenerator.IndexFromContainer(container);
    }

    private sealed record ReorderPayload(ListBox Source, int FromIndex);
}
