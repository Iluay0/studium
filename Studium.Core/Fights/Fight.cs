using System.Text.Json.Serialization;

namespace Studium.Core.Fights;

public enum FightOutcome
{
    Unknown,
    Clear,
    Wipe,
}

public sealed class CombatantStats
{
    public required uint Id { get; init; }
    public uint OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint JobId { get; set; }

    public long Damage { get; set; }
    public long DotDamage { get; set; }
    [JsonIgnore]
    public List<double> PotencySamples { get; } = new();
    [JsonIgnore]
    public List<double> HealPotencySamples { get; } = new();
    [JsonIgnore]
    public Dictionary<uint, List<double>> ActionPotencySamples { get; } = new();
    public int Hits { get; set; }
    public int Crits { get; set; }
    public int DirectHits { get; set; }
    public int Misses { get; set; }
    public long MaxHit { get; set; }
    public uint MaxHitActionId { get; set; }

    public long Healing { get; set; }
    public long Shielding { get; set; }
    public long Overheal { get; set; }
    public int HealHits { get; set; }
    public long HotHealing { get; set; }
    public long HotOverheal { get; set; }
    public int HealCrits { get; set; }

    public long DamageTaken { get; set; }
    public int HitsTaken { get; set; }
    public int Parried { get; set; }
    public int Blocked { get; set; }
    public long HealingReceived { get; set; }
    public int Deaths { get; set; }

    public Dictionary<uint, AbilityStats> DamageAbilities { get; init; } = new();
    public Dictionary<uint, AbilityStats> HealAbilities { get; init; } = new();
    public Dictionary<uint, AbilityStats> TakenAbilities { get; init; } = new();
}

public sealed class AbilityStats
{
    public const uint DotKey = uint.MaxValue - 1;
    public const uint HotKey = uint.MaxValue - 2;

    // Tick keys: status ID with StatusKeyFlag for one DoT, a combo key with ComboKeyFlag for several, SplitTickFlag
    // added for an estimated share, ShieldKeyFlag for absorbed shields.
    public const uint StatusKeyFlag = 0x8000_0000;

    public const uint ComboKeyFlag = 0x4000_0000;

    public const uint SplitTickFlag = 0x1000_0000;

    public static uint StatusKey(uint statusId) => statusId | StatusKeyFlag;

    public static uint SplitTickKey(uint statusId) => statusId | StatusKeyFlag | SplitTickFlag;

    public static bool IsStatusKey(uint key) => (key & StatusKeyFlag) != 0 && key is not (DotKey or HotKey);

    public static bool IsSplitTickKey(uint key) => IsStatusKey(key) && (key & SplitTickFlag) != 0;

    public static uint StatusIdOf(uint key) => key & ~(StatusKeyFlag | SplitTickFlag);

    public static bool IsComboKey(uint key) => (key & (StatusKeyFlag | ComboKeyFlag)) == ComboKeyFlag;

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

public sealed record FightContext(string Zone, string CharacterName, string World, uint LocalPlayerId, string? AreaKey = null);

public sealed class Fight
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required DateTime Start { get; init; }
    public DateTime LastActivity { get; set; }
    public DateTime? EndTime { get; set; }
    // Set while out of combat but the fight may resume. The timer holds here, and the fight ends here if combat
    // doesn't come back.
    public DateTime? HeldAt { get; set; }
    public bool IsActive { get; set; } = true;
    public FightOutcome Outcome { get; set; }
    public string Zone { get; set; } = string.Empty;
    public string CharacterName { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public uint LocalPlayerId { get; set; }
    public string? AreaKey { get; set; }

    [JsonIgnore]
    public string? Title { get; set; }

    public Dictionary<uint, CombatantStats> Combatants { get; init; } = new();
    public Dictionary<uint, EnemyStats> Enemies { get; init; } = new();

    public bool PartyWiped { get; set; }

    public List<DeathRecord> Deaths { get; init; } = new();

    [JsonIgnore]
    public List<DotTick> DotTicks { get; } = new();

    [JsonIgnore]
    public Dictionary<(uint Source, uint Target, uint Action), (DateTime Time, double? Rate)> LastHits { get; } = new();

    [JsonIgnore]
    public List<DotTick> HotTicks { get; } = new();

    public Dictionary<uint, uint> DotActions { get; init; } = new();

    public Dictionary<uint, uint[]> StatusCombos { get; init; } = new();

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

    [JsonIgnore]
    public EnemyStats? MainEnemy => Enemies.Values.MaxBy(e => e.DamageTaken);

    [JsonIgnore]
    public string Name => Title ?? MainEnemy?.Name ?? "Encounter";

    [JsonIgnore]
    public TimeSpan FinalDuration => Duration(EndTime ?? LastActivity);

    public TimeSpan Duration(DateTime now) => (IsActive ? HeldAt ?? now : EndTime ?? LastActivity) - Start;
}
