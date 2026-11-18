using System;
using System.IO;
using System.Text.Json;

namespace CleanTB;

public enum TaskbarMode
{
    Clear = 0,
    Blur = 1,
    Acrylic = 2,
    TintedDark = 3,
    TintedLight = 4,
    Normal = 5
}

public enum StatsPosition
{
    Right = 0,
    Left = 1
}

public class AppConfig
{
    public TaskbarMode Mode { get; set; } = TaskbarMode.Clear;
    public bool HideBorder { get; set; } = true;
    public bool RunAtStartup { get; set; } = false;

    // Hardware Stats Display Settings
    public bool ShowHardwareStats { get; set; } = true;
    public StatsPosition StatsPosition { get; set; } = StatsPosition.Right;
    public bool ShowDownloadOnly { get; set; } = true;
    public bool ShowUploadSpeed { get; set; } = false;
    public bool ShowNetArrows { get; set; } = false;
    public bool ShowCpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool StatsClickThrough { get; set; } = false;
    public int? StatsCustomX { get; set; } = null;
    public int? StatsCustomY { get; set; } = null;

    // Peripheral Battery HUD Settings
    public bool ShowBatteryStats { get; set; } = true;
    public StatsPosition BatteryPosition { get; set; } = StatsPosition.Right;
    public int? BatteryCustomX { get; set; } = null;
    public int? BatteryCustomY { get; set; } = null;
    public bool ShowBatteryHeadphones { get; set; } = true;
    public bool ShowBatteryMouse { get; set; } = true;
    public bool ShowBatteryKeyboard { get; set; } = true;
    public bool ShowBatteryController { get; set; } = true;
    public bool BatteryPreviewMode { get; set; } = false;

    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory,
        "config.json"
    );

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(json);
                if (config != null) return config;
            }
        }
        catch { }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}
