namespace Studium.Core;

/// <summary>Number and time formatting shared by all windows.</summary>
public static class Format
{
    public static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");

    public static string Percent(double share) => $"{share * 100:0}%";

    /// <summary>1,234 · 12.3k · 1.23M</summary>
    public static string Compact(long value) => value switch
    {
        < 10_000 => value.ToString("N0"),
        < 1_000_000 => $"{value / 1000.0:0.0}k",
        _ => $"{value / 1_000_000.0:0.00}M",
    };
}
