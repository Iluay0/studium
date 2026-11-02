namespace Studium.Core;

public enum MeterVisibility
{
    Always,
    InCombat,
    InDuty,
}

public enum NameDisplay
{
    Full,
    Initials,
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

public enum MeterScope
{
    Fight,
    Area,
}

public static class MeterVisibilityRules
{
    public static bool ShouldShow(MeterVisibility visibility, bool partyInCombat, bool inDuty, bool fightRunning) => visibility switch
    {
        MeterVisibility.InCombat => partyInCombat || fightRunning,
        MeterVisibility.InDuty => inDuty,
        _ => true,
    };
}
