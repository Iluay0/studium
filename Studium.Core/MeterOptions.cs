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
