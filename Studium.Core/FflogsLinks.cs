namespace Studium.Core;

public static class FflogsLinks
{
    /// <summary>WorldRegionGroup row (via World → DataCenter → Region) → FFLogs region slug.</summary>
    public static string? RegionSlug(uint regionGroupId) => regionGroupId switch
    {
        1 => "jp",
        2 => "na",
        3 => "eu",
        4 => "oc",
        5 => "cn",
        6 => "kr",
        7 => "na", // NA Cloud (test) data centre
        _ => null,
    };

    /// <summary>e.g. https://www.fflogs.com/character/eu/moogle/Iluay%20Dory, or null if the region is unknown.</summary>
    public static string? CharacterUrl(uint regionGroupId, string world, string name)
    {
        if (RegionSlug(regionGroupId) is not { } region || world.Length == 0 || name.Length == 0)
            return null;
        return $"https://www.fflogs.com/character/{region}/{Uri.EscapeDataString(world.ToLowerInvariant())}/{Uri.EscapeDataString(name)}";
    }
}
