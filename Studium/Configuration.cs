using Dalamud.Configuration;
using Studium.Core;

namespace Studium;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    // Meter
    public bool MeterOpen { get; set; } = true;
    public MeterTab MeterTab { get; set; } = MeterTab.Dps;
    public MeterVisibility Visibility { get; set; } = MeterVisibility.Always;
    public bool HideInCutscenes { get; set; } = true;
    public bool LockMeter { get; set; }
    public bool ClickThroughWhenLocked { get; set; }
    /// <summary>Meter background (and title bar) opacity, 0–100%. Meter text has shadows, so it stays readable at 0%.</summary>
    public float BackgroundOpacity { get; set; } = 0.7f;
    public const float MinOpacity = 0f;

    public static float ClampOpacity(float opacity) => Math.Clamp(opacity, MinOpacity, 1f);
    public NameDisplay NameDisplay { get; set; } = NameDisplay.Full;
    public bool YouForSelf { get; set; }
    public bool MergePets { get; set; } = true;
    public GaugeStyle GaugeStyle { get; set; } = GaugeStyle.Underline;
    /// <summary>Column IDs per tab, in order (see MeterColumns). Missing tab → that tab's defaults.</summary>
    public Dictionary<MeterTab, List<string>> MeterColumns { get; set; } = new();

    // History
    public int RetentionHours { get; set; } = RetentionPeriod.DefaultHours;
    public RetentionUnit RetentionUnit { get; set; } = RetentionUnit.Days;
    /// <summary>When off, saved fights are kept forever and the retention slider is ignored.</summary>
    public bool AutoDeleteFights { get; set; } = true;
    public bool SkipShortFights { get; set; } = true;
    public int SkipShortFightsSeconds { get; set; } = 30;
    public int HideShortFightsSeconds { get; set; } = 15;
    public int SessionGapHours { get; set; } = 4;

    // FFLogs
    public string UploaderPath { get; set; } = string.Empty;

    /// <summary>Brings older saved configs up to date. Returns true if anything changed.</summary>
    public bool Migrate()
    {
        if (Version >= 3)
            return false;
        // v2: the skip-short-fights default went from 10 s to 30 s; move users still on the old default.
        if (Version < 2 && SkipShortFightsSeconds == 10)
            SkipShortFightsSeconds = 30;
        // v3: opacity is kept within its allowed range.
        BackgroundOpacity = ClampOpacity(BackgroundOpacity);
        Version = 3;
        return true;
    }

    /// <summary>Raised after every save, so settings that live outside our windows can be re-applied.</summary>
    [Newtonsoft.Json.JsonIgnore]
    public Action? Saved;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
        Saved?.Invoke();
    }
}
