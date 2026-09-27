using Dalamud.Configuration;
using Studium.Core;

namespace Studium;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    public bool MeterOpen { get; set; } = true;
    public MeterTab MeterTab { get; set; } = MeterTab.Dps;
    public MeterVisibility Visibility { get; set; } = MeterVisibility.Always;
    public bool HideInCutscenes { get; set; } = true;
    public bool LockMeter { get; set; }
    public bool ClickThroughWhenLocked { get; set; }
    public float BackgroundOpacity { get; set; } = 0.7f;
    public const float MinOpacity = 0f;

    public static float ClampOpacity(float opacity) => Math.Clamp(opacity, MinOpacity, 1f);
    public NameDisplay NameDisplay { get; set; } = NameDisplay.Full;
    public bool YouForSelf { get; set; }
    public bool MergePets { get; set; } = true;
    public bool ShowAllPlayers { get; set; } = true;
    public GaugeStyle GaugeStyle { get; set; } = GaugeStyle.Underline;
    public Dictionary<MeterTab, List<string>> MeterColumns { get; set; } = new();

    public int RetentionHours { get; set; } = RetentionPeriod.DefaultHours;
    public RetentionUnit RetentionUnit { get; set; } = RetentionUnit.Days;
    public bool AutoDeleteFights { get; set; } = true;
    public bool SkipShortFights { get; set; } = true;
    public int SkipShortFightsSeconds { get; set; } = 30;
    public int HideShortFightsSeconds { get; set; } = 15;
    public int SessionGapHours { get; set; } = 4;

    public string UploaderPath { get; set; } = string.Empty;

    public bool Migrate()
    {
        if (Version >= 3)
            return false;
        // v2: the skip-short-fights default moved from 10 s to 30 s; move users still on 10.
        if (Version < 2 && SkipShortFightsSeconds == 10)
            SkipShortFightsSeconds = 30;
        BackgroundOpacity = ClampOpacity(BackgroundOpacity);
        Version = 3;
        return true;
    }

    [Newtonsoft.Json.JsonIgnore]
    public Action? Saved;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
        Saved?.Invoke();
    }
}
