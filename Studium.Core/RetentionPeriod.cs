namespace Studium.Core;

public enum RetentionUnit
{
    Hours,
    Days,
}

/// <summary>
/// Fight history retention. Always stored in hours; the settings UI can show it in hours or days.
/// 0 hours means "this session only": fights stay in memory and are never written to disk.
/// </summary>
public static class RetentionPeriod
{
    public const int MaxHours = 720; // 30 days
    public const int DefaultHours = 168; // 7 days

    public static int Clamp(int hours) => Math.Clamp(hours, 0, MaxHours);

    public static int DisplayMax(RetentionUnit unit) => unit == RetentionUnit.Days ? MaxHours / 24 : MaxHours;

    /// <summary>Converts stored hours to the slider value for the given unit (days round to nearest).</summary>
    public static int ToDisplay(int hours, RetentionUnit unit)
    {
        hours = Clamp(hours);
        return unit == RetentionUnit.Days ? (int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero) : hours;
    }

    /// <summary>Converts a slider value in the given unit back to stored hours.</summary>
    public static int FromDisplay(int value, RetentionUnit unit) =>
        Clamp(unit == RetentionUnit.Days ? value * 24 : value);

    public static bool IsSessionOnly(int hours) => Clamp(hours) == 0;

    public static string Describe(int hours)
    {
        hours = Clamp(hours);
        if (hours == 0)
            return "This session only (nothing saved to disk)";
        if (hours % 24 == 0)
            return hours == 24 ? "1 day" : $"{hours / 24} days";
        var hoursText = hours == 1 ? "1 hour" : $"{hours} hours";
        return hours < 24 ? hoursText : $"{hoursText} ({hours / 24.0:0.#} days)";
    }
}
