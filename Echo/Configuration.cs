using Dalamud.Configuration;
using Echo.Core;

namespace Echo;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Meter
    public bool MeterOpen { get; set; } = true;
    public MeterTab MeterTab { get; set; } = MeterTab.Dps;
    public MeterVisibility Visibility { get; set; } = MeterVisibility.Always;
    public bool HideInCutscenes { get; set; } = true;
    public bool LockMeter { get; set; }
    public bool ClickThroughWhenLocked { get; set; }
    public float BackgroundOpacity { get; set; } = 0.7f;
    public NameDisplay NameDisplay { get; set; } = NameDisplay.Full;
    public bool MergePets { get; set; } = true;
    public GaugeStyle GaugeStyle { get; set; } = GaugeStyle.Underline;

    // History
    public int RetentionHours { get; set; } = RetentionPeriod.DefaultHours;
    public RetentionUnit RetentionUnit { get; set; } = RetentionUnit.Days;
    public bool NeverDeleteFights { get; set; }
    public bool SkipShortFights { get; set; } = true;
    public int SkipShortFightsSeconds { get; set; } = 10;
    public int HideShortFightsSeconds { get; set; } = 15;
    public int SessionGapHours { get; set; } = 4;

    // FFLogs
    public string UploaderPath { get; set; } = string.Empty;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
