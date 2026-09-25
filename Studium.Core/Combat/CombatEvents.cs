namespace Studium.Core.Combat;

public enum HitKind
{
    Damage,
    BlockedDamage,
    ParriedDamage,
    Heal,
    Miss,
    Invulnerable,
}

/// <summary>A single thing that happened in combat, as seen by the game client.</summary>
public abstract record CombatEvent(DateTime Time);

/// <summary>
/// One effect of an action (or auto-attack) landing on one target.
/// <see cref="SourceOwnerId"/> is the owner's entity ID when the source is a pet, 0 otherwise.
/// <see cref="Potency"/> is the action's potency for its user's job and level, only when it always hits at that
/// potency (no combo, positional or falloff); null for pets, NPCs, auto-attacks and situational potencies.
/// </summary>
public sealed record ActionHitEvent(
    DateTime Time,
    uint SourceId,
    uint SourceOwnerId,
    uint TargetId,
    uint ActionId,
    byte ActionType,
    HitKind Kind,
    long Amount,
    bool Crit,
    bool DirectHit,
    long Overheal = 0,
    TargetHp? Hp = null,
    DefenseSnapshot? Defense = null,
    int? Potency = null) : CombatEvent(Time);

/// <summary>The target's HP when the event arrived (before it was applied). Only read for party members.</summary>
public readonly record struct TargetHp(uint Current, uint Max);

/// <summary>A status at the moment of a hit: which one, its stack count, and who applied it.</summary>
public sealed record StatusSnapshot(uint StatusId, byte Stacks, string SourceName);

/// <summary>
/// A party member's defences when an event hit them (for death recaps): their statuses (minus noise like food),
/// the debuffs the party had put on the attacker (Reprisal, Addle...), and their shield as % of max HP.
/// </summary>
public sealed record DefenseSnapshot(IReadOnlyList<StatusSnapshot> Target, IReadOnlyList<StatusSnapshot> OnAttacker, byte ShieldPercent);

/// <summary>
/// One DoT (or HoT) on the target when a tick landed: the status, who applied it (and their owner, for pets like
/// a faerie), the action it comes from (0 when unknown) and its per-tick potency for that player's job and level
/// (0 when unknown).
/// </summary>
public sealed record DotOnTarget(uint StatusId, uint SourceId, uint ActionId, int Potency, uint SourceOwnerId = 0);

/// <summary>
/// A DoT or HoT tick. The game names one source on the tick, but the amount is the sum of every DoT (HoT) on
/// the target from every player, except ground effects, which tick on their own.
/// <see cref="StatusIds"/> are the named source's DoTs (or HoTs) on the target when it ticked.
/// <see cref="Dots"/> is every DoT on an enemy (or HoT on a player) from anyone: the tick is split between them.
/// </summary>
public sealed record PeriodicTickEvent(
    DateTime Time,
    uint SourceId,
    uint SourceOwnerId,
    uint TargetId,
    bool IsHeal,
    long Amount,
    long Overheal = 0,
    IReadOnlyList<uint>? StatusIds = null,
    TargetHp? Hp = null,
    DefenseSnapshot? Defense = null,
    IReadOnlyList<DotOnTarget>? Dots = null) : CombatEvent(Time);

public static class Overheal
{
    /// <summary>
    /// Healing beyond the target's missing HP, read when the heal arrives (before it's applied).
    /// Approximate: heals landing in the same instant each see the same missing HP.
    /// </summary>
    public static long Estimate(long amount, uint currentHp, uint maxHp) =>
        maxHp == 0 ? 0 : Math.Clamp(amount - Math.Max((long)maxHp - currentHp, 0), 0, amount);
}

/// <summary>
/// A party member's shield went up (seen on the game's shield gauge). <see cref="StatusId"/> / <see cref="SourceId"/>
/// are the status that appeared with it and who applied it, or 0 when none could be matched.
/// </summary>
public sealed record ShieldGainedEvent(
    DateTime Time,
    uint TargetId,
    uint SourceId,
    uint StatusId,
    byte ShieldPercentBefore,
    byte ShieldPercentAfter,
    TargetHp Hp,
    DefenseSnapshot? Defense = null) : CombatEvent(Time);

/// <summary>
/// A party member's shield went down (seen on the game's shield gauge): either damage it absorbed or a
/// shield expiring. The tracker tells them apart by whether the player was just hit.
/// </summary>
public sealed record ShieldLostEvent(DateTime Time, uint TargetId, byte ShieldPercentBefore, byte ShieldPercentAfter, uint MaxHp)
    : CombatEvent(Time);

public sealed record DeathEvent(DateTime Time, uint TargetId, uint SourceId) : CombatEvent(Time);

public sealed record CastStartEvent(
    DateTime Time,
    uint SourceId,
    uint TargetId,
    uint ActionId,
    float CastTime) : CombatEvent(Time);
