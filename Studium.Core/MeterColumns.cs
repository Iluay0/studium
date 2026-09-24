using Studium.Core.Fights;

namespace Studium.Core;

/// <summary>A column the meter can show; the ID is what the settings store.</summary>
public sealed record MeterColumn(string Id, string Header, string Description, Func<CombatantRow, string> Value);

/// <summary>
/// The meter's columns. Each tab has its own set (all shown by default); the settings can hide and reorder
/// them, but not move columns between tabs. Name is always first and isn't part of these lists.
/// </summary>
public static class MeterColumns
{
    public static readonly IReadOnlyList<MeterColumn> All =
    [
        new("dps", "DPS", "Damage per second", r => r.Dps.ToString("N0")),
        new("dmgShare", "D%", "Share of the party's damage", r => Format.Percent(r.DamageShare)),
        new("damage", "Total", "Total damage", r => Format.Compact(r.Damage)),
        new("crit", "Crit", "Critical hit rate (damage)", r => Format.Percent(r.CritRate)),
        new("dh", "DH", "Direct hit rate", r => Format.Percent(r.DirectHitRate)),
        new("maxHit", "Max hit", "Biggest single hit", r => r.MaxHit.ToString("N0")),
        new("hits", "Hits", "Damaging hits landed (not DoT ticks)", r => r.Hits.ToString("N0")),
        new("misses", "Misses", "Attacks that missed", r => r.Misses.ToString("N0")),
        new("deaths", "Deaths", "Deaths (click for the death recap)", r => r.Deaths.ToString()),
        new("hps", "HPS", "Healing per second, shields included", r => r.Hps.ToString("N0")),
        new("healShare", "H%", "Share of the party's healing, shields included", r => Format.Percent(r.HealingShare)),
        new("heal", "Heal", "Healing done, overheal included (shields not included)", r => Format.Compact(r.Healing - r.Shielding)),
        new("shield", "Shield", "Damage absorbed by this player's shields", r => Format.Compact(r.Shielding)),
        new("healing", "Total", "Heal + Shield", r => Format.Compact(r.Healing)),
        new("overheal", "Overheal", "Share of heals that was overheal (shields can't overheal)", r => Format.Percent(r.OverhealRate)),
        new("healCrit", "Heal crit", "Critical hit rate (healing)", r => Format.Percent(r.HealCritRate)),
        new("taken", "Taken", "Damage taken", r => Format.Compact(r.DamageTaken)),
        new("takenShare", "T%", "Share of the party's damage taken", r => Format.Percent(r.DamageTakenShare)),
        new("parry", "Parry", "Share of hits taken that were parried", r => Format.Percent(r.ParryRate)),
        new("block", "Block", "Share of hits taken that were blocked", r => Format.Percent(r.BlockRate)),
        new("healedOn", "Healed-on", "Healing received", r => Format.Compact(r.HealingReceived)),
    ];

    private static readonly Dictionary<string, MeterColumn> ById = All.ToDictionary(c => c.Id);

    /// <summary>The columns a tab can show, in their default order.</summary>
    public static IReadOnlyList<string> Available(MeterTab tab) => tab switch
    {
        MeterTab.Tank => ["taken", "takenShare", "parry", "block", "healedOn", "deaths"],
        MeterTab.Heal => ["hps", "healShare", "healing", "heal", "shield", "overheal", "healCrit", "deaths"],
        _ => ["dps", "dmgShare", "damage", "crit", "dh", "maxHit", "hits", "misses", "deaths"],
    };

    /// <summary>What a tab shows out of the box (Hits and Misses are available on DPS but start hidden).</summary>
    public static IReadOnlyList<string> Defaults(MeterTab tab) => tab switch
    {
        MeterTab.Dps => ["dps", "dmgShare", "damage", "crit", "dh", "maxHit", "deaths"],
        _ => Available(tab),
    };

    public static MeterColumn Get(string id) => ById[id];

    /// <summary>
    /// The columns to show for a tab, in order: the configured list without unknown, repeated or other-tab
    /// IDs, or the tab's defaults when nothing usable is configured.
    /// </summary>
    public static IReadOnlyList<MeterColumn> Resolve(IReadOnlyList<string>? configured, MeterTab tab)
    {
        var available = Available(tab);
        var columns = (configured ?? [])
            .Distinct()
            .Where(available.Contains)
            .Select(id => ById.GetValueOrDefault(id))
            .OfType<MeterColumn>()
            .ToList();
        return columns.Count > 0 ? columns : Defaults(tab).Select(id => ById[id]).ToList();
    }
}
