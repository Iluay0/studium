using Studium.Core.Combat;

namespace Studium.Core.Fights;

public enum RecapKind
{
    Damage,
    Heal,
    Miss,
    /// <summary>Shield gained: Amount is in HP, AbilityKey is the status's key (or 0 when unknown).</summary>
    Shield,
}

/// <summary>One thing that happened to a player shortly before they died.</summary>
public sealed record RecapEvent(
    double SecondsBeforeDeath,
    RecapKind Kind,
    string SourceName,
    uint AbilityKey,
    long Amount,
    long Overheal,
    bool Crit,
    bool DirectHit,
    bool Parried,
    bool Blocked,
    uint? HpAfter,
    uint MaxHp,
    DefenseSnapshot? Defense = null,
    byte ShieldAfterPercent = 0);

/// <summary>A player's death: who, when, and the last <see cref="DeathRecorder.Window"/> of events, newest first.</summary>
public sealed record DeathRecord(
    uint VictimId,
    string VictimName,
    uint JobId,
    double SecondsIntoFight,
    IReadOnlyList<RecapEvent> Events)
{
    /// <summary>The last damage before the death, if any.</summary>
    public RecapEvent? KillingBlow => Events.FirstOrDefault(e => e.Kind == RecapKind.Damage);
}

/// <summary>
/// Keeps each party member's recent damage and healing received, and turns it into a <see cref="DeathRecord"/>
/// when they die.
/// </summary>
public sealed class DeathRecorder
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly record struct Pending(DateTime Time, RecapKind Kind, uint SourceId, uint AbilityKey, long Amount,
        long Overheal, bool Crit, bool DirectHit, bool Parried, bool Blocked, TargetHp? Hp, DefenseSnapshot? Defense);

    private readonly Dictionary<uint, Queue<Pending>> recent = new();

    public void Clear() => recent.Clear();

    public void Damage(uint victimId, DateTime time, uint sourceId, uint abilityKey, long amount, bool crit, bool directHit,
        bool parried, bool blocked, TargetHp? hp, DefenseSnapshot? defense = null) =>
        Add(victimId, new Pending(time, RecapKind.Damage, sourceId, abilityKey, amount, 0, crit, directHit, parried, blocked, hp, defense));

    public void Heal(uint targetId, DateTime time, uint sourceId, uint abilityKey, long amount, long overheal, bool crit, TargetHp? hp,
        DefenseSnapshot? defense = null) =>
        Add(targetId, new Pending(time, RecapKind.Heal, sourceId, abilityKey, amount, overheal, crit, false, false, false, hp, defense));

    /// <summary>A shield landing: HP and shield are read after it applied, so nothing needs computing.</summary>
    public void Shield(uint targetId, DateTime time, uint sourceId, uint statusId, byte percentBefore, byte percentAfter, TargetHp hp,
        DefenseSnapshot? defense)
    {
        var amount = (long)(percentAfter - percentBefore) * hp.Max / 100;
        var key = statusId == 0 ? 0 : AbilityStats.StatusKey(statusId);
        Add(targetId, new Pending(time, RecapKind.Shield, sourceId, key, amount, 0, false, false, false, false, hp,
            defense == null ? new DefenseSnapshot([], [], percentAfter) : defense with { ShieldPercent = percentAfter }));
    }

    public void Miss(uint targetId, DateTime time, uint sourceId, uint abilityKey, DefenseSnapshot? defense = null) =>
        Add(targetId, new Pending(time, RecapKind.Miss, sourceId, abilityKey, 0, 0, false, false, false, false, null, defense));

    /// <summary>Builds the recap for a death, newest event first, and starts the victim's history afresh.</summary>
    public DeathRecord Record(uint victimId, string victimName, uint jobId, DateTime time, DateTime fightStart, Func<uint, string> nameOf)
    {
        var events = new List<RecapEvent>();
        if (recent.TryGetValue(victimId, out var queue))
        {
            foreach (var e in queue.Where(e => time - e.Time <= Window).Reverse())
            {
                var (hpAfter, shieldAfter) = e.Hp is { Max: > 0 } hp ? After(e, hp) : (null, (byte)0);
                events.Add(new RecapEvent((time - e.Time).TotalSeconds, e.Kind, nameOf(e.SourceId), e.AbilityKey, e.Amount,
                    e.Overheal, e.Crit, e.DirectHit, e.Parried, e.Blocked, hpAfter, e.Hp?.Max ?? 0, e.Defense, shieldAfter));
            }
            queue.Clear();
        }
        return new DeathRecord(victimId, victimName, jobId, (time - fightStart).TotalSeconds, events);
    }

    private void Add(uint targetId, Pending pending)
    {
        if (!recent.TryGetValue(targetId, out var queue))
        {
            queue = new Queue<Pending>();
            recent[targetId] = queue;
        }
        queue.Enqueue(pending);
        while (queue.Count > 0 && pending.Time - queue.Peek().Time > Window)
            queue.Dequeue();
    }

    /// <summary>
    /// HP and shield once the event has landed. HP and shield are read as the event arrives (before it
    /// applies), and damage hits the shield first: only what the shield can't absorb comes off HP.
    /// The game gives the shield in whole percent of max HP, so this is accurate to about 1% of max HP.
    /// </summary>
    public static (uint? Hp, byte ShieldPercent) After(RecapKindAmount e, TargetHp hp, byte shieldPercent)
    {
        var shield = (long)shieldPercent * hp.Max / 100;
        switch (e.Kind)
        {
            case RecapKind.Damage:
                var absorbed = Math.Min(shield, e.Amount);
                var hpAfter = (uint)Math.Max((long)hp.Current - (e.Amount - absorbed), 0);
                return (hpAfter, ToPercent(shield - absorbed, hp.Max));
            case RecapKind.Heal:
                return ((uint)Math.Min((long)hp.Current + e.Amount - e.Overheal, hp.Max), shieldPercent);
            default:
                return (hp.Current, shieldPercent);
        }
    }

    private static (uint? Hp, byte ShieldPercent) After(Pending e, TargetHp hp) =>
        After(new RecapKindAmount(e.Kind, e.Amount, e.Overheal), hp, e.Defense?.ShieldPercent ?? 0);

    private static byte ToPercent(long amount, uint max) => (byte)Math.Clamp((amount * 100 + max - 1) / max, 0, 100);

    public readonly record struct RecapKindAmount(RecapKind Kind, long Amount, long Overheal);
}
