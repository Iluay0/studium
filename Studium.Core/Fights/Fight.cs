namespace Studium.Core.Fights;

public enum FightOutcome
{
    Unknown,
    Clear,
    Wipe,
}

/// <summary>Accumulated stats for one ally (player or pet) in one fight.</summary>
public sealed class CombatantStats
{
    public required uint Id { get; init; }
    public uint OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint JobId { get; set; }

    // Dealt
    public long Damage { get; set; }
    public long DotDamage { get; set; }
    /// <summary>Direct hits that landed (DoT ticks excluded: the game doesn't flag their crits).</summary>
    public int Hits { get; set; }
    public int Crits { get; set; }
    public int DirectHits { get; set; }
    public int Misses { get; set; }
    public long MaxHit { get; set; }
    public uint MaxHitActionId { get; set; }

    // Healing
    public long Healing { get; set; }
    public int HealHits { get; set; }
    public int HealCrits { get; set; }

    // Taken
    public long DamageTaken { get; set; }
    public int HitsTaken { get; set; }
    public int Parried { get; set; }
    public int Blocked { get; set; }
    public long HealingReceived { get; set; }
    public int Deaths { get; set; }
}

public sealed class Fight
{
    public Guid Id { get; } = Guid.NewGuid();
    public required DateTime Start { get; init; }
    /// <summary>Time of the last damage event.</summary>
    public DateTime LastActivity { get; set; }
    /// <summary>When the fight ended: the moment combat ended (or the last hit, for idle timeouts).</summary>
    public DateTime? EndTime { get; set; }
    /// <summary>
    /// Set while the party is out of combat but the fight may still resume: the timer holds here.
    /// If combat resumes, the timer continues and the gap counts; if not, the fight ends here.
    /// </summary>
    public DateTime? HeldAt { get; set; }
    public bool IsActive { get; set; } = true;
    public FightOutcome Outcome { get; set; }
    public string Zone { get; set; } = string.Empty;

    public Dictionary<uint, CombatantStats> Combatants { get; } = new();
    public Dictionary<uint, (string Name, long DamageTaken)> Enemies { get; } = new();

    /// <summary>The fight is named after the enemy that took the most damage.</summary>
    public string Name =>
        Enemies.Count == 0 ? "Encounter" : Enemies.Values.MaxBy(e => e.DamageTaken).Name;

    /// <summary>First hit until the fight ends. The live timer uses the same rule, so it never jumps when the fight ends.</summary>
    public TimeSpan Duration(DateTime now) => (IsActive ? HeldAt ?? now : EndTime ?? LastActivity) - Start;
}
