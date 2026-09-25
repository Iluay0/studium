using Studium.Core.Combat;

namespace Studium.Core.Fights;

public sealed record ActorSnapshot(string Name, uint JobId, uint OwnerId);

/// <summary>What the tracker needs to know about the world. The plugin implements it; tests fake it.</summary>
public interface ICombatWorld
{
    /// <summary>True for you, your party and your alliance (not pets; pets are resolved through their owner).</summary>
    bool IsAlly(uint entityId);

    /// <summary>True for any other player character (hunts, FATEs): shown when enabled, but they never start fights.</summary>
    bool IsOtherPlayer(uint entityId) => false;

    ActorSnapshot? Lookup(uint entityId);

    /// <summary>Whether the actor is currently dead in the game world (false if unknown / gone).</summary>
    bool IsDead(uint entityId) => false;

    /// <summary>Whether an enemy is alive and still fighting (in combat or targeting someone).</summary>
    bool IsEngaged(uint entityId) => false;
}

/// <summary>
/// Turns combat events into fights. A fight starts on the first damage between an ally and a
/// non-ally, and ends when the party leaves combat (or, as a fallback, after a stretch with no damage).
/// A fight lasts from its first hit until it ends, so time in combat without damage counts.
/// </summary>
public sealed class FightTracker
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    /// <summary>
    /// How long the party may be out of combat before the fight ends. The timer holds meanwhile;
    /// re-entering combat resumes the same fight.
    /// </summary>
    public static readonly TimeSpan OutOfCombatGrace = TimeSpan.FromSeconds(1);

    private readonly ICombatWorld world;
    private readonly DeathRecorder deaths = new();
    private readonly ShieldLedger shields = new();
    private readonly Dictionary<uint, DateTime> lastHitOnAlly = new();
    /// <summary>Gauge drops seen before their hit arrived: absorbed if a hit follows within the window, else expired.</summary>
    private readonly List<(uint TargetId, DateTime Time, long Amount)> pendingDrops = new();

    /// <summary>A shield gauge drop this soon after a hit is damage absorbed; later, it's a shield expiring.</summary>
    public static readonly TimeSpan AbsorbWindow = TimeSpan.FromSeconds(2);
    private bool sawPartyInCombat;

    public FightTracker(ICombatWorld world) => this.world = world;

    public Fight? Current { get; private set; }
    public Fight? Last { get; private set; }

    /// <summary>The fight to display: the live one, else the one that just ended.</summary>
    public Fight? Displayed => Current ?? Last;

    public event Action<Fight>? FightEnded;

    /// <summary>
    /// Also count players outside your party / alliance. They're shown and keep a running fight going, but
    /// only you, your party and your alliance start fights, and only they count for wipes.
    /// </summary>
    public bool IncludeOtherPlayers { get; set; } = true;

    /// <summary>Called by the plugin when a fight starts, to record zone and character.</summary>
    public Func<FightContext>? ContextProvider { get; set; }

    public void Handle(CombatEvent e)
    {
        switch (e)
        {
            case ActionHitEvent hit:
                HandleHit(hit);
                break;
            case PeriodicTickEvent tick:
                HandleTick(tick);
                break;
            case ShieldGainedEvent shield:
                if (Current != null && world.IsAlly(shield.TargetId))
                {
                    deaths.Shield(shield.TargetId, shield.Time, shield.SourceId, shield.StatusId, shield.ShieldPercentBefore,
                        shield.ShieldPercentAfter, shield.Hp, shield.Defense);
                    shields.Gained(shield.TargetId, shield.StatusId, shield.SourceId,
                        (long)(shield.ShieldPercentAfter - shield.ShieldPercentBefore) * shield.Hp.Max / 100);
                }
                break;
            case ShieldLostEvent lost:
                HandleShieldLost(lost);
                break;
            case DeathEvent death:
                if (Current == null)
                    break;
                if (Counts(death.TargetId, 0))
                {
                    var victim = Stats(death.TargetId);
                    victim.Deaths++;
                    Current.Deaths.Add(deaths.Record(death.TargetId, victim.Name, victim.JobId, death.Time, Current.Start,
                        id => world.Lookup(id)?.Name ?? string.Empty));
                    // Checked now, not at fight end: respawning or a raise afterwards doesn't undo a wipe.
                    if (Current.Combatants.Keys.Where(world.IsAlly).All(id => id == death.TargetId || world.IsDead(id)))
                        Current.PartyWiped = true;
                }
                else if (Current.Enemies.TryGetValue(death.TargetId, out var enemy))
                    enemy.Died = true;
                break;
        }
    }

    /// <summary>
    /// Called every frame with whether you or any party member is in combat. The fight also stays open while
    /// its main enemy is still engaged: with your party dead, others may still be fighting it (hunts, FATEs).
    /// </summary>
    public void Update(DateTime now, bool partyInCombat)
    {
        if (Current == null)
            return;

        ExpirePendingDrops(now);

        if (partyInCombat || MainEnemyEngaged(Current))
        {
            sawPartyInCombat = true;
            Current.HeldAt = null;
            return;
        }

        if (sawPartyInCombat)
        {
            // Hold the timer; the fight ends where it was held unless combat resumes within the grace.
            Current.HeldAt ??= now;
            if (now - Current.HeldAt >= OutOfCombatGrace)
                End(FightOutcome.Unknown, Current.HeldAt.Value);
            return;
        }

        // Never saw the combat flag (e.g. it didn't flip): fall back to idling out, ending at the last hit.
        if (now - Current.LastActivity >= IdleTimeout)
            End(FightOutcome.Unknown, Current.LastActivity);
    }

    /// <summary>Ends the current fight at <paramref name="endTime"/>, with a known outcome when the duty reports one.</summary>
    public void End(FightOutcome outcome, DateTime endTime)
    {
        if (Current == null)
            return;

        var fight = Current;
        // Hits and heals since the last tick may have moved the medians.
        if (fight.DotTicks.Count > 0)
            Resplit(heal: false);
        if (fight.HotTicks.Count > 0)
            Resplit(heal: true);
        fight.IsActive = false;
        fight.HeldAt = null;
        fight.EndTime = endTime < fight.Start ? fight.Start : endTime;
        // Duties report wipes and clears. Elsewhere: killing the main enemy is a clear; the whole party
        // (or you, solo) dead at once, with the enemy alive, is a wipe; anything else stays unknown.
        if (outcome == FightOutcome.Unknown && MainEnemyDied(fight))
            outcome = FightOutcome.Clear;
        else if (outcome == FightOutcome.Unknown && fight.PartyWiped)
            outcome = FightOutcome.Wipe;
        if (outcome != FightOutcome.Unknown)
            fight.Outcome = outcome;
        Current = null;
        Last = fight;
        sawPartyInCombat = false;
        FightEnded?.Invoke(fight);
    }

    private bool MainEnemyEngaged(Fight fight)
    {
        if (fight.Enemies.Count == 0)
            return false;
        var (id, enemy) = fight.Enemies.MaxBy(e => e.Value.DamageTaken);
        return !enemy.Died && world.IsEngaged(id);
    }

    /// <summary>The death packet usually marks it; failing that, ask the game whether it's dead now.</summary>
    private bool MainEnemyDied(Fight fight)
    {
        if (fight.Enemies.Count == 0)
            return false;
        var (id, enemy) = fight.Enemies.MaxBy(e => e.Value.DamageTaken);
        if (!enemy.Died && world.IsDead(id))
            enemy.Died = true;
        return enemy.Died;
    }

    private void HandleHit(ActionHitEvent hit)
    {
        // "IsAlly" here means "counted": your party / alliance, plus other players when enabled.
        var sourceIsAlly = Counts(hit.SourceId, hit.SourceOwnerId);
        var targetIsAlly = Counts(hit.TargetId, 0);
        var allyInvolved = IsAllyOrAllyPet(hit.SourceId, hit.SourceOwnerId) || world.IsAlly(hit.TargetId);
        // Other players only count against enemies your party / alliance is already fighting, so people
        // fighting unrelated mobs nearby don't leak into (or prolong) your fight.
        if (!allyInvolved && !FightsKnownEnemy(hit.SourceId, hit.SourceOwnerId, hit.TargetId, sourceIsAlly))
            return;

        if (hit.Kind is HitKind.Damage or HitKind.BlockedDamage or HitKind.ParriedDamage)
        {
            if (!EnsureFight(hit.Time, sourceIsAlly, targetIsAlly, allyInvolved))
                return;

            if (sourceIsAlly && !targetIsAlly)
            {
                var attacker = Stats(hit.SourceId, hit.SourceOwnerId);
                attacker.Damage += hit.Amount;
                attacker.Hits++;
                if (hit.Crit)
                    attacker.Crits++;
                if (hit.DirectHit)
                    attacker.DirectHits++;
                if (hit.Amount > attacker.MaxHit)
                {
                    attacker.MaxHit = hit.Amount;
                    attacker.MaxHitActionId = hit.ActionId;
                }
                Ability(attacker.DamageAbilities, hit.ActionId).Add(hit.Amount, hit.Crit, hit.DirectHit);
                if (hit is { Kind: HitKind.Damage, Crit: false, DirectHit: false, Potency: > 0 } && hit.Amount > 0)
                    attacker.PotencySamples.Add((double)hit.Amount / hit.Potency.Value);
                AddEnemyDamage(hit.TargetId, hit.Amount);
            }

            if (targetIsAlly && !sourceIsAlly)
            {
                var victim = Stats(hit.TargetId);
                victim.DamageTaken += hit.Amount;
                victim.HitsTaken++;
                AddEnemyDamage(hit.SourceOwnerId != 0 ? hit.SourceOwnerId : hit.SourceId, 0); // an enemy even if never hit back
                deaths.Damage(hit.TargetId, hit.Time, hit.SourceId, hit.ActionId, hit.Amount, hit.Crit, hit.DirectHit,
                    hit.Kind == HitKind.ParriedDamage, hit.Kind == HitKind.BlockedDamage, hit.Hp, hit.Defense);
                NoteHitOnAlly(hit.TargetId, hit.Time);
                Ability(victim.TakenAbilities, hit.ActionId).Add(hit.Amount, hit.Crit, hit.DirectHit);
                if (hit.Kind == HitKind.ParriedDamage)
                    victim.Parried++;
                if (hit.Kind == HitKind.BlockedDamage)
                    victim.Blocked++;
            }
            return;
        }

        if (Current == null)
            return;

        switch (hit.Kind)
        {
            case HitKind.Heal:
                if (sourceIsAlly)
                {
                    var healer = Stats(hit.SourceId, hit.SourceOwnerId);
                    healer.Healing += hit.Amount;
                    healer.Overheal += hit.Overheal;
                    healer.HealHits++;
                    if (hit.Crit)
                        healer.HealCrits++;
                    if (hit is { Crit: false, Potency: > 0 } && hit.Amount > 0)
                        healer.HealPotencySamples.Add((double)hit.Amount / hit.Potency.Value);
                    Ability(healer.HealAbilities, hit.ActionId).Add(hit.Amount, hit.Crit, overheal: hit.Overheal);
                }
                if (targetIsAlly)
                {
                    Stats(hit.TargetId).HealingReceived += hit.Amount;
                    deaths.Heal(hit.TargetId, hit.Time, hit.SourceId, hit.ActionId, hit.Amount, hit.Overheal, hit.Crit, hit.Hp, hit.Defense);
                }
                break;
            case HitKind.Miss:
                if (sourceIsAlly && !targetIsAlly)
                    Stats(hit.SourceId, hit.SourceOwnerId).Misses++;
                if (targetIsAlly && !sourceIsAlly)
                    deaths.Miss(hit.TargetId, hit.Time, hit.SourceId, hit.ActionId, hit.Defense);
                break;
        }
    }

    private void HandleTick(PeriodicTickEvent tick)
    {
        if (tick is { IsHeal: false, Dots.Count: > 0 } && !Counts(tick.TargetId, 0))
        {
            HandleDotTick(tick, tick.Dots);
            return;
        }
        if (tick is { IsHeal: true, Dots.Count: > 0 })
        {
            HandleHotTick(tick, tick.Dots);
            return;
        }

        // "IsAlly" here means "counted": your party / alliance, plus other players when enabled.
        var sourceIsAlly = Counts(tick.SourceId, tick.SourceOwnerId);
        var targetIsAlly = Counts(tick.TargetId, 0);
        var allyInvolved = IsAllyOrAllyPet(tick.SourceId, tick.SourceOwnerId) || world.IsAlly(tick.TargetId);
        // Other players only count against enemies your party / alliance is already fighting, so people
        // fighting unrelated mobs nearby don't leak into (or prolong) your fight.
        if (!allyInvolved && !FightsKnownEnemy(tick.SourceId, tick.SourceOwnerId, tick.TargetId, sourceIsAlly))
            return;

        if (tick.IsHeal)
        {
            if (Current == null)
                return;
            if (sourceIsAlly)
            {
                var healer = Stats(tick.SourceId, tick.SourceOwnerId);
                healer.Healing += tick.Amount;
                healer.Overheal += tick.Overheal;
                RecordTick(healer.HealAbilities, tick);
            }
            if (targetIsAlly)
            {
                Stats(tick.TargetId).HealingReceived += tick.Amount;
                deaths.Heal(tick.TargetId, tick.Time, tick.SourceId, Current.TickKey(tick.StatusIds ?? [], true), tick.Amount, tick.Overheal, false,
                    tick.Hp, tick.Defense);
            }
            return;
        }

        if (!EnsureFight(tick.Time, sourceIsAlly, targetIsAlly, allyInvolved))
            return;

        if (sourceIsAlly && !targetIsAlly)
        {
            var attacker = Stats(tick.SourceId, tick.SourceOwnerId);
            attacker.Damage += tick.Amount;
            attacker.DotDamage += tick.Amount;
            RecordTick(attacker.DamageAbilities, tick);
            AddEnemyDamage(tick.TargetId, tick.Amount);
        }
        if (targetIsAlly && !sourceIsAlly)
        {
            var victim = Stats(tick.TargetId);
            victim.DamageTaken += tick.Amount;
            AddEnemyDamage(tick.SourceOwnerId != 0 ? tick.SourceOwnerId : tick.SourceId, 0);
            RecordTick(victim.TakenAbilities, tick);
            deaths.Damage(tick.TargetId, tick.Time, tick.SourceId, Current!.TickKey(tick.StatusIds ?? [], false), tick.Amount,
                false, false, false, false, tick.Hp, tick.Defense);
            NoteHitOnAlly(tick.TargetId, tick.Time);
        }
    }

    /// <summary>
    /// A DoT tick on an enemy: the sum of every DoT on it, from every player. Kept raw, then every tick of the
    /// fight is split again, so earlier ticks use each player's latest damage per potency.
    /// </summary>
    private void HandleDotTick(PeriodicTickEvent tick, IReadOnlyList<DotOnTarget> dots)
    {
        var allyInvolved = world.IsAlly(tick.SourceId) || dots.Any(d => IsAllyOrAllyPet(d.SourceId, d.SourceOwnerId));
        var counted = dots.Where(d => Counts(d.SourceId, d.SourceOwnerId)).Select(d => d.SourceId).ToHashSet();
        if (!allyInvolved && !FightsKnownEnemy(tick.SourceId, 0, tick.TargetId, sourceIsPlayer: true))
            return;
        if (!EnsureFight(tick.Time, counted.Count > 0, false, allyInvolved))
            return;

        var fight = Current!;
        fight.DotTicks.Add(new DotTick(tick.Time, tick.TargetId, tick.Amount, dots, counted));
        NoteActions(fight, dots);
        AddEnemyDamage(tick.TargetId, tick.Amount);
        Resplit(heal: false);
    }

    /// <summary>A HoT tick: the sum of every HoT on the player, from every healer (and faerie). Kept and re-split like DoTs.</summary>
    private void HandleHotTick(PeriodicTickEvent tick, IReadOnlyList<DotOnTarget> dots)
    {
        if (Current == null)
            return;
        var allyInvolved = world.IsAlly(tick.TargetId) || dots.Any(d => IsAllyOrAllyPet(d.SourceId, d.SourceOwnerId));
        if (!allyInvolved)
            return;

        var fight = Current;
        var counted = dots.Where(d => Counts(d.SourceId, d.SourceOwnerId)).Select(d => d.SourceId).ToHashSet();
        fight.HotTicks.Add(new DotTick(tick.Time, tick.TargetId, tick.Amount, dots, counted, tick.Overheal));
        NoteActions(fight, dots);
        if (Counts(tick.TargetId, 0))
        {
            Stats(tick.TargetId).HealingReceived += tick.Amount;
            deaths.Heal(tick.TargetId, tick.Time, tick.SourceId, fight.TickKey(tick.StatusIds ?? [], true), tick.Amount, tick.Overheal, false,
                tick.Hp, tick.Defense);
        }
        Resplit(heal: true);
    }

    private static void NoteActions(Fight fight, IReadOnlyList<DotOnTarget> dots)
    {
        foreach (var dot in dots)
        {
            if (dot.ActionId != 0)
                fight.DotActions[dot.StatusId] = dot.ActionId;
        }
    }

    /// <summary>
    /// Rebuilds every player's DoT (or HoT) shares from all of the fight's ticks, using the latest damage (healing)
    /// per potency.
    /// </summary>
    private void Resplit(bool heal)
    {
        var fight = Current!;
        var rates = new Dictionary<uint, double>();
        foreach (var combatant in fight.Combatants.Values)
        {
            var samples = heal ? combatant.HealPotencySamples : combatant.PotencySamples;
            if (samples.Count > 0)
                rates[combatant.Id] = DotSplit.Median(new List<double>(samples));

            if (heal)
            {
                combatant.Healing -= combatant.HotHealing;
                combatant.Overheal -= combatant.HotOverheal;
                combatant.HotHealing = 0;
                combatant.HotOverheal = 0;
            }
            else
            {
                combatant.Damage -= combatant.DotDamage;
                combatant.DotDamage = 0;
            }
            var abilities = heal ? combatant.HealAbilities : combatant.DamageAbilities;
            foreach (var key in abilities.Keys.Where(AbilityStats.IsSplitTickKey).ToList())
                abilities.Remove(key);
        }

        foreach (var tick in heal ? fight.HotTicks : fight.DotTicks)
        {
            foreach (var share in DotSplit.Split(tick, rates))
            {
                if (!tick.Counted.Contains(share.SourceId))
                    continue;
                var owner = Stats(share.SourceId, share.SourceOwnerId);
                var key = AbilityStats.SplitTickKey(share.StatusId);
                if (heal)
                {
                    owner.Healing += share.Amount;
                    owner.Overheal += share.Overheal;
                    owner.HotHealing += share.Amount;
                    owner.HotOverheal += share.Overheal;
                    Ability(owner.HealAbilities, key).Add(share.Amount, overheal: share.Overheal);
                }
                else
                {
                    owner.Damage += share.Amount;
                    owner.DotDamage += share.Amount;
                    Ability(owner.DamageAbilities, key).Add(share.Amount);
                }
            }
        }
    }

    /// <summary>
    /// For damage between a counted player and an enemy: starts a fight if your party / alliance is involved,
    /// otherwise only keeps a running one going. Returns whether a fight is running.
    /// </summary>
    private bool EnsureFight(DateTime time, bool sourceCounts, bool targetCounts, bool allyInvolved)
    {
        if (sourceCounts == targetCounts)
            return false;

        if (Current == null)
        {
            if (!allyInvolved)
                return false;
            var context = ContextProvider?.Invoke();
            Current = new Fight
            {
                Start = time,
                LastActivity = time,
                Zone = context?.Zone ?? string.Empty,
                CharacterName = context?.CharacterName ?? string.Empty,
                World = context?.World ?? string.Empty,
                LocalPlayerId = context?.LocalPlayerId ?? 0,
            };
            deaths.Clear();
            shields.Clear();
            lastHitOnAlly.Clear();
            pendingDrops.Clear();
            sawPartyInCombat = false;
        }

        if (time > Current.LastActivity)
            Current.LastActivity = time;
        return true;
    }

    private bool IsAllyOrAllyPet(uint id, uint ownerId) =>
        world.IsAlly(id) || (ownerId != 0 && world.IsAlly(ownerId));

    /// <summary>Whether the enemy side of this event is already part of the current fight.</summary>
    private bool FightsKnownEnemy(uint sourceId, uint sourceOwnerId, uint targetId, bool sourceIsPlayer)
    {
        if (Current == null)
            return false;
        var enemyId = sourceIsPlayer ? targetId : sourceOwnerId != 0 ? sourceOwnerId : sourceId;
        return Current.Enemies.ContainsKey(enemyId);
    }

    /// <summary>Whether this actor's numbers are recorded: allies, and other players (and their pets) when enabled.</summary>
    private bool Counts(uint id, uint ownerId) =>
        IsAllyOrAllyPet(id, ownerId)
        || (IncludeOtherPlayers && (world.IsOtherPlayer(id) || (ownerId != 0 && world.IsOtherPlayer(ownerId))));

    private CombatantStats Stats(uint id, uint ownerId = 0)
    {
        var fight = Current!;
        if (!fight.Combatants.TryGetValue(id, out var stats))
        {
            stats = new CombatantStats { Id = id, OwnerId = ownerId };
            fight.Combatants[id] = stats;
        }

        if (string.IsNullOrEmpty(stats.Name) && world.Lookup(id) is { } actor)
        {
            stats.Name = actor.Name;
            stats.JobId = actor.JobId;
            if (stats.OwnerId == 0)
                stats.OwnerId = actor.OwnerId;
        }

        return stats;
    }

    /// <summary>
    /// A shield gauge went down. Right after a hit, that's damage absorbed: it counts as healing for whoever
    /// cast the shield (like ACT), as its own "shield" ability. Otherwise a shield expired and is just dropped.
    /// </summary>
    private void HandleShieldLost(ShieldLostEvent lost)
    {
        if (Current == null || !world.IsAlly(lost.TargetId) || lost.MaxHp == 0)
            return;

        var amount = (long)(lost.ShieldPercentBefore - lost.ShieldPercentAfter) * lost.MaxHp / 100;
        var justHit = lastHitOnAlly.TryGetValue(lost.TargetId, out var hitTime) && lost.Time - hitTime <= AbsorbWindow;
        if (justHit)
            CreditAbsorbed(lost.TargetId, amount);
        else
            pendingDrops.Add((lost.TargetId, lost.Time, amount)); // the hit's packet may still be on its way
    }

    /// <summary>A party member was hit: gauge drops that were waiting for a hit were damage absorbed.</summary>
    private void NoteHitOnAlly(uint targetId, DateTime time)
    {
        lastHitOnAlly[targetId] = time;
        for (var i = pendingDrops.Count - 1; i >= 0; i--)
        {
            var drop = pendingDrops[i];
            if (drop.TargetId != targetId || time - drop.Time > AbsorbWindow)
                continue;
            pendingDrops.RemoveAt(i);
            CreditAbsorbed(targetId, drop.Amount);
        }
    }

    /// <summary>Drops no hit came for: shields that expired. Removed from the ledger, not credited.</summary>
    private void ExpirePendingDrops(DateTime now)
    {
        for (var i = pendingDrops.Count - 1; i >= 0; i--)
        {
            var drop = pendingDrops[i];
            if (now - drop.Time <= AbsorbWindow)
                continue;
            pendingDrops.RemoveAt(i);
            shields.Expire(drop.TargetId, drop.Amount);
        }
    }

    private void CreditAbsorbed(uint targetId, long amount)
    {
        foreach (var (statusId, sourceId, absorbed) in shields.Absorb(targetId, amount))
        {
            if (sourceId == 0 || !IsAllyOrAllyPet(sourceId, world.Lookup(sourceId)?.OwnerId ?? 0))
                continue;
            var caster = Stats(sourceId);
            caster.Healing += absorbed;
            caster.Shielding += absorbed;
            Ability(caster.HealAbilities, AbilityStats.ShieldKey(statusId)).Add(absorbed);
            Stats(targetId).HealingReceived += absorbed;
        }
    }

    /// <summary>Credits a tick to the DoT / HoT (or combination of them) it came from.</summary>
    private void RecordTick(Dictionary<uint, AbilityStats> abilities, PeriodicTickEvent tick) =>
        Ability(abilities, Current!.TickKey(tick.StatusIds ?? [], tick.IsHeal)).Add(tick.Amount, overheal: tick.Overheal);

    private static AbilityStats Ability(Dictionary<uint, AbilityStats> abilities, uint key)
    {
        if (!abilities.TryGetValue(key, out var ability))
        {
            ability = new AbilityStats();
            abilities[key] = ability;
        }
        return ability;
    }

    private void AddEnemyDamage(uint enemyId, long amount)
    {
        var fight = Current!;
        if (!fight.Enemies.TryGetValue(enemyId, out var enemy))
        {
            enemy = new EnemyStats();
            fight.Enemies[enemyId] = enemy;
        }
        if (string.IsNullOrEmpty(enemy.Name))
            enemy.Name = world.Lookup(enemyId)?.Name ?? string.Empty;
        enemy.DamageTaken += amount;
    }
}
