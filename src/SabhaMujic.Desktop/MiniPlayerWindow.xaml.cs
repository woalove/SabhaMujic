using System.ComponentModel;
using System.Windows;
using SabhaMujic.Desktop.Views;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop;

/// <summary>Compact player (current track, transport, volume) with an always-on-top option.</summary>
public partial class MiniPlayerWindow : Window
{
    private readonly NowPlayingViewModel _nowPlaying;
    private bool _shuttingDown;

    public MiniPlayerWindow(NowPlayingViewModel nowPlaying, UiSettings settings)
    {
        InitializeComponent();
        _nowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        DataContext = nowPlaying;
        SeekGesture.Attach(SeekSlider, () => _nowPlaying);

        Topmost = settings.MiniAlwaysOnTop;
        if (settings.MiniLeft is { } left && settings.MiniTop is { } top && WindowPlacement.IsOnScreen(left, top, Width, 160))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        PreviewKeyDown += (_, e) => e.Handled = KeyboardShortcuts.TryHandle(e, _nowPlaying);

        // No system caption: drag the mini player by any empty area.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                try
                {
                    DragMove();
                }
                catch (InvalidOperationException)
                {
                    // The button was released before the drag started.
                }
            }
        };
    }

    /// <summary>Raised when the user wants the full window back (Restore button or closing the mini player).</summary>
    public event EventHandler? RestoreRequested;

    public UiSettings CaptureSettings(UiSettings settings) => settings with
    {
        MiniLeft = Left,
        MiniTop = Top,
        MiniAlwaysOnTop = Topmost,
    };

    public void CloseForShutdown()
    {
        _shuttingDown = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the mini player returns to the main window instead of leaving an invisible app running.
        if (!_shuttingDown)
        {
            e.Cancel = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke(this, EventArgs.Empty);
}
