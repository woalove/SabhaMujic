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
            Subtitle.Text = "SABHA MUSIC · DEMO";
        }

        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateChromeForState();
        SourceInitialized += (_, _) => UpdateChromeForState();
        DpiChanged += (_, _) => UpdateChromeForState();
    }

    /// <summary>
    /// Maximised: pad the content by the off-screen sizing frame so nothing is cut off, and show the restore glyph.
    /// </summary>
    private void UpdateChromeForState()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? WindowFrame.MaximizedOverhang(this) : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximise";
        System.Windows.Automation.AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximise");
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>Signed out: open the admin sign-in. Signed in: show the account menu (sign out).</summary>
    private void OnAccountChipClick(object sender, RoutedEventArgs e)
    {
        if (!_shell.IsSignedIn)
        {
            _shell.OpenAdminSignInCommand.Execute(null);
            return;
        }

        var menu = (ContextMenu)FindResource("AccountMenu");
        menu.DataContext = _shell;
        menu.PlacementTarget = AccountChip;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
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
            Theme = _shell.Theme.Theme,
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

        if (!_shell.IsMainVisible)
        {
            return; // a sign-in/setup form is showing
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
