using System.Text.Json;
using System.Text.Json.Serialization;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>Per-user window layout. Stored as JSON in %LOCALAPPDATA%\SabhaMujic\desktop-settings.json.</summary>
public sealed record UiSettings
{
    public const double MinWidth = 760;
    public const double MinHeight = 480;

    public double? Left { get; init; }

    public double? Top { get; init; }

    public double Width { get; init; } = 1180;

    public double Height { get; init; } = 720;

    public bool IsMaximized { get; init; }

    public bool IsQueuePanelOpen { get; init; } = true;

    public double? MiniLeft { get; init; }

    public double? MiniTop { get; init; }

    public bool MiniAlwaysOnTop { get; init; } = true;

    /// <summary>Clamps nonsense values (corrupt file, tiny window) back to usable ones.</summary>
    public UiSettings Sanitized() => this with
    {
        Width = Finite(Width) ? Math.Max(Width, MinWidth) : 1180,
        Height = Finite(Height) ? Math.Max(Height, MinHeight) : 720,
        Left = Left is { } l && Finite(l) ? l : null,
        Top = Top is { } t && Finite(t) ? t : null,
        MiniLeft = MiniLeft is { } ml && Finite(ml) ? ml : null,
        MiniTop = MiniTop is { } mt && Finite(mt) ? mt : null,
    };

    private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
}

/// <summary>Loads/saves <see cref="UiSettings"/>. Failures fall back to defaults; settings are never worth a crash.</summary>
public sealed class UiSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public UiSettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>%LOCALAPPDATA%\SabhaMujic\desktop-settings.json for the current user.</summary>
    public static UiSettingsStore ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SabhaMujic",
        "desktop-settings.json"));

    public UiSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new UiSettings();
            }

            var json = File.ReadAllText(FilePath);
            return (JsonSerializer.Deserialize<UiSettings>(json, Options) ?? new UiSettings()).Sanitized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }

    /// <summary>Writes atomically (temp file + move). Returns false if the file could not be written.</summary>
    public bool Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings.Sanitized(), Options));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
