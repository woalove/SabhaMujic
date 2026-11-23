using System.Windows;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Services;

/// <summary>
/// Swaps the colour-token dictionary (Themes/Dark.xaml or Themes/Light.xaml) at runtime. The token dictionary is the
/// first merged dictionary of App.xaml; every style refers to its brushes with DynamicResource, so the whole UI
/// (including open dialogs and the mini player) recolours immediately.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

    /// <summary>Applies <paramref name="theme"/> now and follows later changes of <see cref="ThemeViewModel.Theme"/>.</summary>
    public static void Follow(Application app, ThemeViewModel theme)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(theme);
        Apply(app, theme.Theme);
        theme.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeViewModel.Theme))
            {
                Apply(app, theme.Theme);
            }
        };
    }

    public static void Apply(Application app, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(app);
        var source = theme == AppTheme.Light ? LightUri : DarkUri;
        var dictionaries = app.Resources.MergedDictionaries;
        var tokens = new ResourceDictionary { Source = source };
        var existing = dictionaries.FirstOrDefault(IsTokenDictionary);
        if (existing is null)
        {
            dictionaries.Insert(0, tokens);
        }
        else if (existing.Source != source)
        {
            dictionaries[dictionaries.IndexOf(existing)] = tokens;
        }
    }

    private static bool IsTokenDictionary(ResourceDictionary d) => d.Source == DarkUri || d.Source == LightUri ||
        (d.Source is { } s && (s.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                                s.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Helpers shared by the custom-chrome dialog windows (style "DialogWindow").</summary>
internal static class DialogChrome
{
    /// <summary>Lets the user drag the dialog by any empty area and wires the title-row close button.</summary>
    public static void Attach(Window window)
    {
        window.CommandBindings.Add(new System.Windows.Input.CommandBinding(
            SystemCommands.CloseWindowCommand, (_, _) => window.Close()));
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
            {
                try
                {
                    window.DragMove();
                }
                catch (InvalidOperationException)
                {
                    // DragMove throws if the button was released in the meantime; nothing to do.
                }
            }
        };
    }

    /// <summary>True when a confirm button should be the filled danger style (the confirm step of a destructive action).</summary>
    public static bool IsDestructive(string confirmText) =>
        confirmText.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ||
        confirmText.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) ||
        confirmText.StartsWith("Forget", StringComparison.OrdinalIgnoreCase) ||
        confirmText.StartsWith("Revoke", StringComparison.OrdinalIgnoreCase) ||
        confirmText.StartsWith("Clear", StringComparison.OrdinalIgnoreCase);
}

