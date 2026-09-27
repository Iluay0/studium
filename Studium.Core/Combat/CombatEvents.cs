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

public abstract record CombatEvent(DateTime Time);

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

public readonly record struct TargetHp(uint Current, uint Max);

public sealed record StatusSnapshot(uint StatusId, byte Stacks, string SourceName);

public sealed record DefenseSnapshot(IReadOnlyList<StatusSnapshot> Target, IReadOnlyList<StatusSnapshot> OnAttacker, byte ShieldPercent);

public sealed record DotOnTarget(uint StatusId, uint SourceId, uint ActionId, int Potency, uint SourceOwnerId = 0);

// A tick names one source but carries the sum of every DoT (HoT) on the target. Ground effects tick alone.
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
    // Approximate: heals landing in the same instant all see the same missing HP.
    public static long Estimate(long amount, uint currentHp, uint maxHp) =>
        maxHp == 0 ? 0 : Math.Clamp(amount - Math.Max((long)maxHp - currentHp, 0), 0, amount);
}

public sealed record ShieldGainedEvent(
    DateTime Time,
    uint TargetId,
    uint SourceId,
    uint StatusId,
    byte ShieldPercentBefore,
    byte ShieldPercentAfter,
    TargetHp Hp,
    DefenseSnapshot? Defense = null) : CombatEvent(Time);

public sealed record ShieldLostEvent(DateTime Time, uint TargetId, byte ShieldPercentBefore, byte ShieldPercentAfter, uint MaxHp)
    : CombatEvent(Time);

public sealed record DeathEvent(DateTime Time, uint TargetId, uint SourceId) : CombatEvent(Time);

public sealed record CastStartEvent(
    DateTime Time,
    uint SourceId,
    uint TargetId,
    uint ActionId,
    float CastTime) : CombatEvent(Time);
