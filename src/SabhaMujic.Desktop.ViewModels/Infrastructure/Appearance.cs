using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// The colour theme chosen with the header's sun/moon toggle. Saved immediately to the per-user settings file
/// (<see cref="UiSettings.Theme"/>), so it survives a restart even if the app is not closed cleanly.
/// </summary>
public sealed partial class ThemeViewModel : ObservableObject
{
    private readonly UiSettingsStore? _store;

    /// <param name="store">Where the choice is persisted; null keeps it in memory only (tests, previews).</param>
    public ThemeViewModel(UiSettingsStore? store = null)
    {
        _store = store;
        Theme = store?.Load().Theme ?? AppTheme.Dark;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDark), nameof(ToggleToolTip))]
    public partial AppTheme Theme { get; private set; }

    public bool IsDark => Theme == AppTheme.Dark;

    public string ToggleToolTip => IsDark ? "Switch to the light theme" : "Switch to the dark theme";

    [RelayCommand]
    private void Toggle() => Set(IsDark ? AppTheme.Light : AppTheme.Dark);

    public void Set(AppTheme theme)
    {
        if (!Enum.IsDefined(theme) || theme == Theme)
        {
            return;
        }

        Theme = theme;

        // Re-read so the window-layout part of the file (saved by the main window) is not overwritten.
        _store?.Save(_store.Load() with { Theme = theme });
    }
}

/// <summary>
/// Colour of a category (tag) dot and pill. Categories cycle accent → violet → amber → blue in category order
/// (docs/DESIGN.md); the WPF layer maps each value to the theme's brushes.
/// </summary>
public enum TagColor
{
    Accent,
    Violet,
    Amber,
    Blue,
}

public static class TagColors
{
    private static readonly TagColor[] Cycle = [TagColor.Accent, TagColor.Violet, TagColor.Amber, TagColor.Blue];

    /// <summary>Colour for the category at <paramref name="position"/> (0-based) in category sort order.</summary>
    public static TagColor ForPosition(int position) => Cycle[((position % Cycle.Length) + Cycle.Length) % Cycle.Length];
}

/// <summary>A category pill on a track row.</summary>
public sealed record CategoryPill(Guid Id, string Name, TagColor Color);
