using System.Text.Json.Serialization;

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
    /// <summary>Raw healing, overheal included, plus damage absorbed by this player's shields (like ACT).</summary>
    public long Healing { get; set; }
    /// <summary>The part of <see cref="Healing"/> that was damage absorbed by this player's shields.</summary>
    public long Shielding { get; set; }
    public long Overheal { get; set; }
    public int HealHits { get; set; }
    public int HealCrits { get; set; }

    // Taken
    public long DamageTaken { get; set; }
    public int HitsTaken { get; set; }
    public int Parried { get; set; }
    public int Blocked { get; set; }
    public long HealingReceived { get; set; }
    public int Deaths { get; set; }

    // Per-ability breakdowns, keyed by action ID (or AbilityStats.DotKey / HotKey for ticks).
    public Dictionary<uint, AbilityStats> DamageAbilities { get; init; } = new();
    public Dictionary<uint, AbilityStats> HealAbilities { get; init; } = new();
    /// <summary>Damage this combatant took, keyed by the enemy's action.</summary>
    public Dictionary<uint, AbilityStats> TakenAbilities { get; init; } = new();
}

public sealed class AbilityStats
{
    /// <summary>Ticks that couldn't be tied to any DoT / HoT.</summary>
    public const uint DotKey = uint.MaxValue - 1;
    public const uint HotKey = uint.MaxValue - 2;

    /// <summary>Ticks from exactly one status (e.g. Dia) are keyed by status ID with this bit set.</summary>
    public const uint StatusKeyFlag = 0x8000_0000;

    /// <summary>
    /// Ticks while several of a player's DoTs (or HoTs) were up: the game sends them combined, so they're
    /// keyed per combination (see <see cref="Fight.StatusCombos"/>), with this bit set.
    /// </summary>
    public const uint ComboKeyFlag = 0x4000_0000;

    public static uint StatusKey(uint statusId) => statusId | StatusKeyFlag;

    public static bool IsStatusKey(uint key) => (key & StatusKeyFlag) != 0 && key is not (DotKey or HotKey);

    public static uint StatusIdOf(uint key) => key & ~StatusKeyFlag;

    public static bool IsComboKey(uint key) => (key & (StatusKeyFlag | ComboKeyFlag)) == ComboKeyFlag;

    /// <summary>Damage absorbed by a shield status (e.g. Galvanize) is keyed by status ID with this bit set.</summary>
    public const uint ShieldKeyFlag = 0x2000_0000;

    public static uint ShieldKey(uint statusId) => statusId | ShieldKeyFlag;

    public static bool IsShieldKey(uint key) => (key & (StatusKeyFlag | ComboKeyFlag | ShieldKeyFlag)) == ShieldKeyFlag;

    public static uint ShieldStatusOf(uint key) => key & ~ShieldKeyFlag;

    public long Total { get; set; }
    public int Hits { get; set; }
    public int Crits { get; set; }
    public int DirectHits { get; set; }
    public long Max { get; set; }
    public long Overheal { get; set; }

    public static bool IsTick(uint key) => key is DotKey or HotKey || IsStatusKey(key) || IsComboKey(key);

    public void Add(long amount, bool crit = false, bool directHit = false, long overheal = 0)
    {
        Total += amount;
        Hits++;
        if (crit)
            Crits++;
        if (directHit)
            DirectHits++;
        Max = Math.Max(Max, amount);
        Overheal += overheal;
    }
}

public sealed class EnemyStats
{
    public string Name { get; set; } = string.Empty;
    public long DamageTaken { get; set; }
    public bool Died { get; set; }
}

/// <summary>Who you were and where, when a fight started.</summary>
public sealed record FightContext(string Zone, string CharacterName, string World, uint LocalPlayerId);

public sealed class Fight
{
    public Guid Id { get; init; } = Guid.NewGuid();
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
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public uint LocalPlayerId { get; set; }

    public Dictionary<uint, CombatantStats> Combatants { get; init; } = new();
    public Dictionary<uint, EnemyStats> Enemies { get; init; } = new();

    /// <summary>At some point every party member in the fight was dead at once (checked at each death).</summary>
    public bool PartyWiped { get; set; }

    /// <summary>Party members' deaths in this fight, oldest first, each with the events leading up to it.</summary>
    public List<DeathRecord> Deaths { get; init; } = new();

    /// <summary>Combo tick keys → the statuses that were up together (sorted status IDs).</summary>
    public Dictionary<uint, uint[]> StatusCombos { get; init; } = new();

    /// <summary>
    /// The ability key for a tick given the source's DoTs (or HoTs) on the target at that moment:
    /// none → the generic tick row; one → that status; several → one row per combination (never split).
    /// </summary>
    public uint TickKey(IReadOnlyList<uint> statusIds, bool isHeal)
    {
        var ids = statusIds.Distinct().Order().ToArray();
        switch (ids.Length)
        {
            case 0:
                return isHeal ? AbilityStats.HotKey : AbilityStats.DotKey;
            case 1:
                return AbilityStats.StatusKey(ids[0]);
        }

        foreach (var (key, combo) in StatusCombos)
        {
            if (combo.SequenceEqual(ids))
                return key;
        }
        var newKey = AbilityStats.ComboKeyFlag | (uint)StatusCombos.Count;
        StatusCombos[newKey] = ids;
        return newKey;
    }

    /// <summary>The enemy that took the most damage; the fight is named after it.</summary>
    [JsonIgnore]
    public EnemyStats? MainEnemy => Enemies.Values.MaxBy(e => e.DamageTaken);

    [JsonIgnore]
    public string Name => MainEnemy?.Name ?? "Encounter";

    /// <summary>First hit until the fight ends. The live timer uses the same rule, so it never jumps when the fight ends.</summary>
    [JsonIgnore]
    public TimeSpan FinalDuration => Duration(EndTime ?? LastActivity);

    public TimeSpan Duration(DateTime now) => (IsActive ? HeldAt ?? now : EndTime ?? LastActivity) - Start;
}
