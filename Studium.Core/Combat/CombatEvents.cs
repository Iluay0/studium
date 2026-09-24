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
    bool DirectHit) : CombatEvent(Time);

/// <summary>A DoT or HoT tick. The game names the source on the tick itself.</summary>
public sealed record PeriodicTickEvent(
    DateTime Time,
    uint SourceId,
    uint SourceOwnerId,
    uint TargetId,
    bool IsHeal,
    long Amount) : CombatEvent(Time);

public sealed record DeathEvent(DateTime Time, uint TargetId, uint SourceId) : CombatEvent(Time);

public sealed record CastStartEvent(
    DateTime Time,
    uint SourceId,
    uint TargetId,
    uint ActionId,
    float CastTime) : CombatEvent(Time);
