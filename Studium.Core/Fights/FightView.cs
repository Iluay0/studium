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
    long HealingReceived);

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
                Sum(m => m.HealingReceived)));
        }

        return new FightSummary(rows, totalDamage / seconds, totalHealing / seconds);
    }

    private static double Share(double part, double total) => total <= 0 ? 0 : part / total;
}
