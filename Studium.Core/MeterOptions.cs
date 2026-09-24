namespace Studium.Core;

public enum MeterVisibility
{
    Always,
    InCombat,
    InDuty,
}

public enum NameDisplay
{
    /// <summary>Iluay Dory</summary>
    Full,
    /// <summary>I. D.</summary>
    Initials,
    /// <summary>Iluay D.</summary>
    SurnameInitial,
}

public enum GaugeStyle
{
    Underline,
    Background,
}

public enum MeterTab
{
    Dps,
    Tank,
    Heal,
}

public static class MeterVisibilityRules
{
    /// <summary>
    /// Whether the meter should draw. "In combat" also covers a fight that's still running
    /// (e.g. the short hold after combat drops), so the meter doesn't flicker away mid-fight.
    /// </summary>
    public static bool ShouldShow(MeterVisibility visibility, bool partyInCombat, bool inDuty, bool fightRunning) => visibility switch
    {
        MeterVisibility.InCombat => partyInCombat || fightRunning,
        MeterVisibility.InDuty => inDuty,
        _ => true,
    };
}
