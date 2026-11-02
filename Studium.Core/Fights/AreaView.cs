namespace Studium.Core.Fights;

public static class AreaView
{
    public static Fight Merge(IEnumerable<Fight> fights, string? areaKey, string zone, DateTime now)
    {
        var ordered = string.IsNullOrEmpty(areaKey)
            ? []
            : fights.Where(f => f.AreaKey == areaKey).OrderBy(f => f.Start).ToList();
        var start = ordered.Count > 0 ? ordered[0].Start : now;
        var merged = new Fight
        {
            Start = start,
            IsActive = false,
            Outcome = FightOutcome.Unknown,
            Zone = zone,
            Title = zone,
            AreaKey = areaKey,
        };

        var elapsed = TimeSpan.Zero;
        foreach (var fight in ordered)
        {
            merged.CharacterName = fight.CharacterName;
            merged.World = fight.World;
            merged.LocalPlayerId = fight.LocalPlayerId;
            foreach (var (status, action) in fight.DotActions)
                merged.DotActions[status] = action;

            foreach (var source in fight.Combatants.Values)
            {
                if (!merged.Combatants.TryGetValue(source.Id, out var target))
                    merged.Combatants[source.Id] = target = new CombatantStats { Id = source.Id };
                AddCombatant(target, source, key => MapKey(merged, fight, key));
            }

            foreach (var death in fight.Deaths)
            {
                merged.Deaths.Add(death with
                {
                    SecondsIntoFight = death.SecondsIntoFight + elapsed.TotalSeconds,
                    Events = death.Events.Select(e => e with { AbilityKey = MapKey(merged, fight, e.AbilityKey) }).ToList(),
                });
            }

            elapsed += fight.Duration(now);
        }

        merged.LastActivity = start + elapsed;
        merged.EndTime = start + elapsed;
        return merged;
    }

    private static uint MapKey(Fight merged, Fight source, uint key) =>
        AbilityStats.IsComboKey(key) && source.StatusCombos.TryGetValue(key, out var ids) ? merged.TickKey(ids, false) : key;

    private static void AddCombatant(CombatantStats target, CombatantStats source, Func<uint, uint> mapKey)
    {
        if (source.OwnerId != 0)
            target.OwnerId = source.OwnerId;
        if (!string.IsNullOrEmpty(source.Name))
            target.Name = source.Name;
        if (source.JobId != 0)
            target.JobId = source.JobId;

        target.Damage += source.Damage;
        target.DotDamage += source.DotDamage;
        target.Hits += source.Hits;
        target.Crits += source.Crits;
        target.DirectHits += source.DirectHits;
        target.Misses += source.Misses;
        if (source.MaxHit > target.MaxHit)
        {
            target.MaxHit = source.MaxHit;
            target.MaxHitActionId = source.MaxHitActionId;
        }

        target.Healing += source.Healing;
        target.Shielding += source.Shielding;
        target.Overheal += source.Overheal;
        target.HealHits += source.HealHits;
        target.HotHealing += source.HotHealing;
        target.HotOverheal += source.HotOverheal;
        target.HealCrits += source.HealCrits;

        target.DamageTaken += source.DamageTaken;
        target.HitsTaken += source.HitsTaken;
        target.Parried += source.Parried;
        target.Blocked += source.Blocked;
        target.HealingReceived += source.HealingReceived;
        target.Deaths += source.Deaths;

        AddAbilities(target.DamageAbilities, source.DamageAbilities, mapKey);
        AddAbilities(target.HealAbilities, source.HealAbilities, mapKey);
        AddAbilities(target.TakenAbilities, source.TakenAbilities, mapKey);
    }

    private static void AddAbilities(Dictionary<uint, AbilityStats> target, Dictionary<uint, AbilityStats> source, Func<uint, uint> mapKey)
    {
        foreach (var (key, stats) in source)
        {
            var mapped = mapKey(key);
            if (!target.TryGetValue(mapped, out var sum))
                target[mapped] = sum = new AbilityStats();
            sum.Total += stats.Total;
            sum.Hits += stats.Hits;
            sum.Crits += stats.Crits;
            sum.DirectHits += stats.DirectHits;
            sum.Max = Math.Max(sum.Max, stats.Max);
            sum.Overheal += stats.Overheal;
        }
    }
}
