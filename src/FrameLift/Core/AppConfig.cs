using System.Text.Json;

namespace FrameLift.Core;

public sealed class AppConfig
{
    /// <summary>Start Game Boost by itself when a fullscreen game has been in front for a while.</summary>
    public bool AutoBoost { get; set; } = true;
    public int AutoDetectSeconds { get; set; } = 10;
    public bool BoostSwitchPowerPlan { get; set; } = true;
    public bool BoostPurgeMemory { get; set; } = true;
    /// <summary>Process names (no .exe) Game Boost must never touch or treat as a game.</summary>
    public List<string> Ignored { get; set; } = new();
    public bool RestorePointOffered { get; set; }
    /// <summary>The "still running in the tray" balloon is shown only the first time the window is hidden.</summary>
    public bool TrayHintShown { get; set; }

    // While boosting, free standby RAM again when it is large and free RAM is low (from StutterFix).
    public int StandbyThresholdMb { get; set; } = 1024;
    public int FreeBelowMb { get; set; } = 1024;
    public int StandbyCooldownSeconds { get; set; } = 60;

    /// <summary>During Game Boost, lower the CPU and GPU power limits when temperatures get high (from ThermalGuard).</summary>
    public bool ThermalEaseOff { get; set; } = true;
    /// <summary>Freeze the game for a moment at the critical level. Off by default: online games can disconnect.</summary>
    public bool ThermalSuspendAtCritical { get; set; }
    public ThermalLimits Thermal { get; set; } = new();

    /// <summary>
    /// "Cool Boost": while boosting, let the CPU rest between frames (no 100 % minimum state, deeper idle, efficient
    /// turbo, fan before throttling) and keep the GPU off its hottest steps. Less heat for the same frames.
    /// </summary>
    public bool BoostEfficiency { get; set; } = true;
    /// <summary>GPU core clock ceiling during Boost, in MHz (0 = leave it). Only used when the card accepts clock locks.</summary>
    public int BoostGpuClockMhz { get; set; } = 1700;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameLift", "config.json");

    public static AppConfig Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
