using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SabhaMujic.Desktop.ViewModels;

namespace SabhaMujic.Desktop.Views;

public partial class NowPlayingBar : UserControl
{
    public NowPlayingBar()
    {
        InitializeComponent();
        SeekGesture.Attach(SeekSlider, () => DataContext as NowPlayingViewModel);
    }

    public event EventHandler? MiniPlayerRequested;

    private void OnMiniPlayerClick(object sender, RoutedEventArgs e) => MiniPlayerRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Tells the view model when the user starts and finishes interacting with a seek slider, so server updates
/// don't move the thumb under the user's finger and exactly one seek is sent on release (click or drag).
/// </summary>
internal static class SeekGesture
{
    public static void Attach(Slider slider, Func<NowPlayingViewModel?> viewModel)
    {
        // handledEventsToo: with IsMoveToPointEnabled the Slider's class handler marks the mouse-down handled.
        slider.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => viewModel()?.BeginSeek()), true);
        slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => End()), true);
        slider.LostMouseCapture += (_, _) => End();
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => End()));

        void End()
        {
            // Let the slider apply the final value from this mouse event before reading it.
            slider.Dispatcher.BeginInvoke(() =>
            {
                if (Mouse.LeftButton == MouseButtonState.Released)
                {
                    _ = viewModel()?.EndSeek();
                }
            });
        }
    }
}
