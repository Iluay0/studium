using Studium.Core.Combat;

namespace Studium.Core.Fights;

public enum RecapKind
{
    Damage,
    Heal,
    Miss,
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
    DefenseSnapshot? Defense = null);

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
                uint? hpAfter = e.Hp is { Max: > 0 } hp ? HpAfter(e, hp) : null;
                events.Add(new RecapEvent((time - e.Time).TotalSeconds, e.Kind, nameOf(e.SourceId), e.AbilityKey, e.Amount,
                    e.Overheal, e.Crit, e.DirectHit, e.Parried, e.Blocked, hpAfter, e.Hp?.Max ?? 0, e.Defense));
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

    private static uint HpAfter(Pending e, TargetHp hp) => e.Kind switch
    {
        RecapKind.Damage => (uint)Math.Max((long)hp.Current - e.Amount, 0),
        RecapKind.Heal => (uint)Math.Min((long)hp.Current + e.Amount - e.Overheal, hp.Max),
        _ => hp.Current,
    };
}
