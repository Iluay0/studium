namespace Studium.Core.Fights;

/// <summary>One row of the meter, after pet merging.</summary>
public sealed record CombatantRow(
    uint Id,
    string Name,
    uint JobId,
    long Damage,
    double DamageShare,
    double Dps,
    double CritRate,
    double DirectHitRate,
    long MaxHit,
    uint MaxHitActionId,
    int Deaths,
    long Healing,
    double HealingShare,
    double Hps,
    double HealCritRate,
    double OverhealRate,
    long DamageTaken,
    double DamageTakenShare,
    double ParryRate,
    double BlockRate,
    long HealingReceived,
    int Hits = 0,
    int Misses = 0);

/// <summary>
/// One ability in a drill-down. <see cref="PetName"/> is set when the ability belongs to a merged pet.
/// Tick rows (<see cref="IsTick"/>) have no crit/DH data.
/// </summary>
public sealed record AbilityRow(
    uint ActionId,
    string? PetName,
    long Total,
    double Share,
    int Hits,
    double CritRate,
    double DirectHitRate,
    long Average,
    long Max,
    double OverhealRate,
    bool IsTick,
    IReadOnlyList<uint> StatusIds);

public sealed record FightSummary(IReadOnlyList<CombatantRow> Rows, double RaidDps, double RaidHps);

/// <summary>Builds meter rows from a fight. DPS divides by the whole fight's duration (ACT's "encdps").</summary>
public static class FightView
{
    public static FightSummary Summarize(Fight fight, DateTime now, bool mergePets)
    {
        var seconds = Math.Max(fight.Duration(now).TotalSeconds, 1);

        var groups = fight.Combatants.Values
            .GroupBy(c => mergePets && c.OwnerId != 0 && fight.Combatants.ContainsKey(c.OwnerId) ? c.OwnerId : c.Id)
            .ToList();

        var totalDamage = fight.Combatants.Values.Sum(c => c.Damage);
        var totalHealing = fight.Combatants.Values.Sum(c => c.Healing);
        var totalTaken = fight.Combatants.Values.Sum(c => c.DamageTaken);

        var rows = new List<CombatantRow>(groups.Count);
        foreach (var group in groups)
        {
            var main = fight.Combatants[group.Key];
            var members = group.ToList();
            long Sum(Func<CombatantStats, long> f) => members.Sum(f);
            int Count(Func<CombatantStats, int> f) => members.Sum(f);

            var best = members.MaxBy(m => m.MaxHit)!;
            var hits = Count(m => m.Hits);
            var healHits = Count(m => m.HealHits);
            var hitsTaken = Count(m => m.HitsTaken);
            var damage = Sum(m => m.Damage);
            var healing = Sum(m => m.Healing);
            var taken = Sum(m => m.DamageTaken);

            rows.Add(new CombatantRow(
                main.Id,
                string.IsNullOrEmpty(main.Name) ? $"#{main.Id:X8}" : main.Name,
                main.JobId,
                damage,
                Share(damage, totalDamage),
                damage / seconds,
                Share(Count(m => m.Crits), hits),
                Share(Count(m => m.DirectHits), hits),
                best.MaxHit,
                best.MaxHitActionId,
                Count(m => m.Deaths),
                healing,
                Share(healing, totalHealing),
                healing / seconds,
                Share(Count(m => m.HealCrits), healHits),
                Share(Sum(m => m.Overheal), healing),
                taken,
                Share(taken, totalTaken),
                Share(Count(m => m.Parried), hitsTaken),
                Share(Count(m => m.Blocked), hitsTaken),
                Sum(m => m.HealingReceived),
                hits,
                Count(m => m.Misses)));
        }

        return new FightSummary(rows, totalDamage / seconds, totalHealing / seconds);
    }

    /// <summary>
    /// Per-ability rows for one meter row: the combatant plus, when merging, its pets.
    /// The tab picks the breakdown: damage dealt, healing done, or damage taken.
    /// </summary>
    public static IReadOnlyList<AbilityRow> Abilities(Fight fight, uint rowId, MeterTab tab, bool mergePets)
    {
        var members = fight.Combatants.Values
            .Where(c => c.Id == rowId || (mergePets && c.OwnerId == rowId && fight.Combatants.ContainsKey(rowId)))
            .ToList();

        var entries = members
            .SelectMany(m => Breakdown(m, tab).Select(kv => (Member: m, Key: kv.Key, Stats: kv.Value)))
            .ToList();
        var total = entries.Sum(e => e.Stats.Total);

        return entries
            .Select(e => new AbilityRow(
                e.Key,
                e.Member.Id == rowId ? null : e.Member.Name,
                e.Stats.Total,
                Share(e.Stats.Total, total),
                e.Stats.Hits,
                Share(e.Stats.Crits, e.Stats.Hits),
                Share(e.Stats.DirectHits, e.Stats.Hits),
                e.Stats.Hits == 0 ? 0 : e.Stats.Total / e.Stats.Hits,
                e.Stats.Max,
                Share(e.Stats.Overheal, e.Stats.Total),
                AbilityStats.IsTick(e.Key),
                TickStatuses(fight, e.Key)))
            .OrderByDescending(r => r.Total)
            .ToList();
    }

    /// <summary>The statuses behind a tick row: one for a single DoT / HoT, several for a combination.</summary>
    private static IReadOnlyList<uint> TickStatuses(Fight fight, uint key) =>
        AbilityStats.IsStatusKey(key) ? [AbilityStats.StatusIdOf(key)]
        : AbilityStats.IsComboKey(key) && fight.StatusCombos.TryGetValue(key, out var ids) ? ids
        : [];

    private static Dictionary<uint, AbilityStats> Breakdown(CombatantStats stats, MeterTab tab) => tab switch
    {
        MeterTab.Heal => stats.HealAbilities,
        MeterTab.Tank => stats.TakenAbilities,
        _ => stats.DamageAbilities,
    };

    private static double Share(double part, double total) => total <= 0 ? 0 : part / total;
}
