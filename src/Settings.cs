using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScrollVD;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum HotkeyCombo { CtrlShift, CtrlAlt, ShiftAlt, WinShift, CtrlWin }

/// <summary>Все настройки приложения. Сохраняются в %AppData%\ScrollVD\settings.json.</summary>
internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public bool EdgeEnabled { get; set; } = true;
    public bool GrabEnabled { get; set; } = true;

    public int EdgeBaseSpeed { get; set; } = 10;     // px/тик в начале прокрутки у края
    public int EdgeMaxSpeed { get; set; } = 60;      // px/тик максимум при удержании
    public int EdgeAccelPerSec { get; set; } = 50;   // прибавка px/тик за каждую секунду удержания
    public int EdgeDwellMs { get; set; } = 120;      // задержка перед стартом прокрутки
    public int CornerDead { get; set; } = 48;        // «мёртвые» углы, px
    public int EdgeMargin { get; set; } = 2;         // зона срабатывания у края, px

    public int CanvasFactor { get; set; } = 1;       // на сколько «экранов» можно увести в каждую сторону
    public bool ReverseGrab { get; set; }            // обратное направление захвата по клавишам
    public bool SnapMode { get; set; } = false;      // прыжок по сетке (целый экран), а не плавный скролл

    public HotkeyCombo GrabHotkey { get; set; } = HotkeyCombo.CtrlShift;

    // Миникарта
    public bool MinimapVisible { get; set; } = false;
    public int MinimapX { get; set; } = -1;        // -1 = автопозиция (правый верхний угол)
    public int MinimapY { get; set; } = -1;
    public int MinimapWidth { get; set; } = 220;
    public int MinimapHeight { get; set; } = -1;   // -1 = авто по соотношению сторон экрана
    public HotkeyCombo MinimapHotkey { get; set; } = HotkeyCombo.CtrlAlt;

    public List<Guid> DisabledDesktops { get; set; } = new();

    // ---- персистентность ----
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
        catch { /* битый файл — стартуем с дефолтов */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* нет прав на запись — молча игнорируем */ }
    }
}

/// <summary>Текущие настройки в памяти процесса.</summary>
internal static class Config
{
    public static Settings Current = Settings.Load();
}
