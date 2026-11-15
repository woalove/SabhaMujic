using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SabhaMujic.Desktop.Views;
using SabhaMujic.Desktop.ViewModels;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell;
    private readonly UiSettingsStore _settingsStore;
    private readonly Action? _simulateConnectionLoss;
    private UiSettings _settings;
    private MiniPlayerWindow? _mini;
    private bool _closing;

    /// <param name="simulateConnectionLoss">Demo mode only (non-null with <c>--demo</c>): Ctrl+Shift+R triggers it.</param>
    public MainWindow(ShellViewModel shell, UiSettingsStore settingsStore, Action? simulateConnectionLoss)
    {
        InitializeComponent();
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        DataContext = shell;

        _settings = settingsStore.Load();
        shell.IsQueuePanelOpen = _settings.IsQueuePanelOpen;
        ApplyPlacement(_settings);

        _simulateConnectionLoss = simulateConnectionLoss;
        if (simulateConnectionLoss is not null)
        {
            DemoBanner.Visibility = Visibility.Visible;
            Title = "SabhaMujic (demo)";
        }

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    private void ApplyPlacement(UiSettings s)
    {
        Width = s.Width;
        Height = s.Height;
        if (s.Left is { } left && s.Top is { } top && WindowPlacement.IsOnScreen(left, top, s.Width, s.Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (s.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings = _settings with
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            IsQueuePanelOpen = _shell.IsQueuePanelOpen,
        };
        if (_mini is not null)
        {
            _settings = _mini.CaptureSettings(_settings);
            _mini.CloseForShutdown();
        }

        _settingsStore.Save(_settings);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_simulateConnectionLoss is not null && e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _simulateConnectionLoss();
            e.Handled = true;
            return;
        }

        if (!_shell.IsSignedIn)
        {
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _shell.NavigateCommand.Execute(NavPage.Library);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => WindowPlacement.FindDescendant<LibraryView>(PageHost)?.FocusSearch());
            e.Handled = true;
            return;
        }

        e.Handled = KeyboardShortcuts.TryHandle(e, _shell.NowPlaying);
    }

    /// <summary>A click on a category in the sidebar shows the library (programmatic selection changes don't).</summary>
    private void OnCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && CategoryList.IsKeyboardFocusWithin && _shell.CurrentPage != NavPage.Library)
        {
            _shell.NavigateCommand.Execute(NavPage.Library);
        }
    }

    private void OnMiniPlayerRequested(object? sender, EventArgs e)
    {
        if (_mini is null)
        {
            _mini = new MiniPlayerWindow(_shell.NowPlaying, _settings);
            _mini.RestoreRequested += (_, _) => RestoreFromMini();
        }

        Hide();
        _mini.Show();
        _mini.Activate();
    }

    private void RestoreFromMini()
    {
        if (_closing || _mini is null)
        {
            return;
        }

        _settings = _mini.CaptureSettings(_settings);
        _mini.Hide();
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
