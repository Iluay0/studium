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
    TargetHp? Hp = null) : CombatEvent(Time);

/// <summary>The target's HP when the event arrived (before it was applied). Only read for party members.</summary>
public readonly record struct TargetHp(uint Current, uint Max);

/// <summary>
/// A DoT or HoT tick. The game names the source on the tick itself, but not which DoT / HoT it is:
/// <see cref="StatusIds"/> are the source's DoTs (or HoTs) that were on the target when it ticked.
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
    TargetHp? Hp = null) : CombatEvent(Time);

public static class Overheal
{
    /// <summary>
    /// Healing beyond the target's missing HP, read when the heal arrives (before it's applied).
    /// Approximate: heals landing in the same instant each see the same missing HP.
    /// </summary>
    public static long Estimate(long amount, uint currentHp, uint maxHp) =>
        maxHp == 0 ? 0 : Math.Clamp(amount - Math.Max((long)maxHp - currentHp, 0), 0, amount);
}

public sealed record DeathEvent(DateTime Time, uint TargetId, uint SourceId) : CombatEvent(Time);

public sealed record CastStartEvent(
    DateTime Time,
    uint SourceId,
    uint TargetId,
    uint ActionId,
    float CastTime) : CombatEvent(Time);
