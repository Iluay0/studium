namespace Studium.Core;

public enum RetentionUnit
{
    Hours,
    Days,
}

public static class RetentionPeriod
{
    public const int MaxHours = 720;
    public const int DefaultHours = 168;

    public static int Clamp(int hours) => Math.Clamp(hours, 0, MaxHours);

    public static int DisplayMax(RetentionUnit unit) => unit == RetentionUnit.Days ? MaxHours / 24 : MaxHours;

    public static int ToDisplay(int hours, RetentionUnit unit)
    {
        hours = Clamp(hours);
        return unit == RetentionUnit.Days ? (int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero) : hours;
    }

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
