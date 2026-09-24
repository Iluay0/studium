using Studium.Core.Combat;

namespace Studium.Core.Fights;

public sealed record ActorSnapshot(string Name, uint JobId, uint OwnerId);

/// <summary>What the tracker needs to know about the world. The plugin implements it; tests fake it.</summary>
public interface ICombatWorld
{
    /// <summary>True for you and your party members (not pets; pets are resolved through their owner).</summary>
    bool IsAlly(uint entityId);

    ActorSnapshot? Lookup(uint entityId);
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
    private bool sawPartyInCombat;

    public FightTracker(ICombatWorld world) => this.world = world;

    public Fight? Current { get; private set; }
    public Fight? Last { get; private set; }

    /// <summary>The fight to display: the live one, else the one that just ended.</summary>
    public Fight? Displayed => Current ?? Last;

    public event Action<Fight>? FightEnded;

    /// <summary>Called by the plugin to name the zone when a fight starts.</summary>
    public Func<string>? ZoneProvider { get; set; }

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
            case DeathEvent death:
                if (Current != null && world.IsAlly(death.TargetId))
                    Stats(death.TargetId).Deaths++;
                break;
        }
    }

    /// <summary>Called every frame with whether you or any party member is in combat.</summary>
    public void Update(DateTime now, bool partyInCombat)
    {
        if (Current == null)
            return;

        if (partyInCombat)
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
        if (outcome != FightOutcome.Unknown)
            fight.Outcome = outcome;
        Current = null;
        Last = fight;
        sawPartyInCombat = false;
        FightEnded?.Invoke(fight);
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
                AddEnemyDamage(hit.TargetId, hit.Amount);
            }

            if (targetIsAlly && !sourceIsAlly)
            {
                var victim = Stats(hit.TargetId);
                victim.DamageTaken += hit.Amount;
                victim.HitsTaken++;
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
                }
                if (targetIsAlly)
                    Stats(hit.TargetId).HealingReceived += hit.Amount;
                break;
            case HitKind.Miss:
                if (sourceIsAlly && !targetIsAlly)
                    Stats(hit.SourceId, hit.SourceOwnerId).Misses++;
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
            }
            if (targetIsAlly)
                Stats(tick.TargetId).HealingReceived += tick.Amount;
            return;
        }

        if (!EnsureFight(tick.Time, sourceIsAlly, targetIsAlly))
            return;

        if (sourceIsAlly && !targetIsAlly)
        {
            var attacker = Stats(tick.SourceId, tick.SourceOwnerId);
            attacker.Damage += tick.Amount;
            attacker.DotDamage += tick.Amount;
            AddEnemyDamage(tick.TargetId, tick.Amount);
        }
        if (targetIsAlly && !sourceIsAlly)
            Stats(tick.TargetId).DamageTaken += tick.Amount;
    }

    /// <summary>Starts a fight if this damage is between an ally and a non-ally. Returns whether a fight is running.</summary>
    private bool EnsureFight(DateTime time, bool sourceIsAlly, bool targetIsAlly)
    {
        var involvesParty = sourceIsAlly != targetIsAlly;
        if (!involvesParty)
            return false;

        if (Current == null)
        {
            Current = new Fight { Start = time, LastActivity = time, Zone = ZoneProvider?.Invoke() ?? string.Empty };
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

    private void AddEnemyDamage(uint enemyId, long amount)
    {
        var fight = Current!;
        var name = fight.Enemies.TryGetValue(enemyId, out var existing) && !string.IsNullOrEmpty(existing.Name)
            ? existing.Name
            : world.Lookup(enemyId)?.Name ?? string.Empty;
        fight.Enemies[enemyId] = (name, existing.DamageTaken + amount);
    }
}
