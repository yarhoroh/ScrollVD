using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrollVD;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum HotkeyCombo { CtrlShift, CtrlAlt, ShiftAlt, WinShift, CtrlWin }

/// <summary>All application settings. Saved to %AppData%\ScrollVD\settings.json.</summary>
internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool EdgeEnabled { get; set; } = true;
    public bool GrabEnabled { get; set; } = true;

    public int EdgeBaseSpeed { get; set; } = 10;     // px/tick at the start of edge scroll
    public int EdgeMaxSpeed { get; set; } = 60;      // px/tick maximum while held
    public int EdgeAccelPerSec { get; set; } = 50;   // px/tick added per second of holding
    public int EdgeDwellMs { get; set; } = 120;      // delay before scrolling starts
    public int CornerDead { get; set; } = 48;        // "dead" corners, px
    public int EdgeMargin { get; set; } = 2;         // edge trigger zone, px

    public int CanvasFactor { get; set; } = 1;       // how many "screens" you can move in each direction
    public bool ReverseGrab { get; set; }            // reverse grab direction for keys
    public bool SnapMode { get; set; } = false;      // grid jump (full screen) instead of smooth scroll

    public HotkeyCombo GrabHotkey { get; set; } = HotkeyCombo.CtrlShift;

    // Minimap
    public bool MinimapVisible { get; set; } = false;
    public int MinimapX { get; set; } = -1;        // -1 = auto position (top-right corner)
    public int MinimapY { get; set; } = -1;
    public int MinimapWidth { get; set; } = 220;
    public int MinimapHeight { get; set; } = -1;   // -1 = auto based on screen aspect ratio
    public HotkeyCombo MinimapHotkey { get; set; } = HotkeyCombo.CtrlAlt;

    public List<Guid> DisabledDesktops { get; set; } = new();

    // ---- persistence ----
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollVD");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* corrupt file — start with defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* no write permission — silently ignore */ }
    }
}

/// <summary>Current settings held in process memory.</summary>
internal static class Config
{
    public static Settings Current = Settings.Load();
}
