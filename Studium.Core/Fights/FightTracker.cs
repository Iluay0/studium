using Studium.Core.Combat;

namespace Studium.Core.Fights;

public sealed record ActorSnapshot(string Name, uint JobId, uint OwnerId);

/// <summary>What the tracker needs to know about the world. The plugin implements it; tests fake it.</summary>
public interface ICombatWorld
{
    /// <summary>True for you and your party members (not pets; pets are resolved through their owner).</summary>
    bool IsAlly(uint entityId);

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
    public static readonly TimeSpan OutOfCombatGrace = TimeSpan.FromSeconds(10);

    private readonly ICombatWorld world;
    private readonly DeathRecorder deaths = new();
    private bool sawPartyInCombat;

    public FightTracker(ICombatWorld world) => this.world = world;

    public Fight? Current { get; private set; }
    public Fight? Last { get; private set; }

    /// <summary>The fight to display: the live one, else the one that just ended.</summary>
    public Fight? Displayed => Current ?? Last;

    public event Action<Fight>? FightEnded;

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
                    deaths.Shield(shield.TargetId, shield.Time, shield.SourceId, shield.StatusId, shield.ShieldPercentBefore,
                        shield.ShieldPercentAfter, shield.Hp, shield.Defense);
                break;
            case DeathEvent death:
                if (Current == null)
                    break;
                if (world.IsAlly(death.TargetId))
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
        var sourceIsAlly = IsAllyOrAllyPet(hit.SourceId, hit.SourceOwnerId);
        var targetIsAlly = world.IsAlly(hit.TargetId);

        if (hit.Kind is HitKind.Damage or HitKind.BlockedDamage or HitKind.ParriedDamage)
        {
            if (!EnsureFight(hit.Time, sourceIsAlly, targetIsAlly))
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
        var sourceIsAlly = IsAllyOrAllyPet(tick.SourceId, tick.SourceOwnerId);
        var targetIsAlly = world.IsAlly(tick.TargetId);

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

        if (!EnsureFight(tick.Time, sourceIsAlly, targetIsAlly))
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
        }
    }

    /// <summary>Starts a fight if this damage is between an ally and a non-ally. Returns whether a fight is running.</summary>
    private bool EnsureFight(DateTime time, bool sourceIsAlly, bool targetIsAlly)
    {
        var involvesParty = sourceIsAlly != targetIsAlly;
        if (!involvesParty)
            return false;

        if (Current == null)
        {
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
            sawPartyInCombat = false;
        }

        if (time > Current.LastActivity)
            Current.LastActivity = time;
        return true;
    }

    private bool IsAllyOrAllyPet(uint id, uint ownerId) =>
        world.IsAlly(id) || (ownerId != 0 && world.IsAlly(ownerId));

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
