using Studium.Core.Combat;

namespace Studium.Core.Fights;

public sealed record ActorSnapshot(string Name, uint JobId, uint OwnerId);

public interface ICombatWorld
{
    bool IsAlly(uint entityId);

    bool IsOtherPlayer(uint entityId) => false;

    ActorSnapshot? Lookup(uint entityId);

    bool IsDead(uint entityId) => false;

    bool IsEngaged(uint entityId) => false;

    uint LocalPlayerId => 0;

    float? DistanceTo(uint entityId) => null;
}

public sealed class FightTracker
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OutOfCombatGrace = TimeSpan.FromSeconds(1);

    private readonly ICombatWorld world;
    private readonly DeathRecorder deaths = new();
    private readonly ShieldLedger shields = new();
    private readonly Dictionary<uint, DateTime> lastHitOnAlly = new();
    // Gauge drops that arrived before their hit. A hit within the window means absorbed, otherwise expired.
    private readonly List<(uint TargetId, DateTime Time, long Amount)> pendingDrops = new();

    public static readonly TimeSpan AbsorbWindow = TimeSpan.FromSeconds(2);
    private bool sawPartyInCombat;

    public FightTracker(ICombatWorld world) => this.world = world;

    public Fight? Current { get; private set; }
    public Fight? Last { get; private set; }

    public Fight? Displayed => Current ?? Last;

    public event Action<Fight>? FightEnded;

    public bool IncludeOtherPlayers { get; set; } = true;

    public float MaxRange { get; set; } = 128;

    public Func<FightContext>? ContextProvider { get; set; }

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
            case ShieldGainedEvent shield:
                if (Current != null && world.IsAlly(shield.TargetId))
                {
                    deaths.Shield(shield.TargetId, shield.Time, shield.SourceId, shield.StatusId, shield.ShieldPercentBefore,
                        shield.ShieldPercentAfter, shield.Hp, shield.Defense);
                    shields.Gained(shield.TargetId, shield.StatusId, shield.SourceId,
                        (long)(shield.ShieldPercentAfter - shield.ShieldPercentBefore) * shield.Hp.Max / 100);
                }
                break;
            case ShieldLostEvent lost:
                HandleShieldLost(lost);
                break;
            case DeathEvent death:
                if (Current == null)
                    break;
                if (Counts(death.TargetId, 0))
                {
                    var victim = Stats(death.TargetId);
                    victim.Deaths++;
                    Current.Deaths.Add(deaths.Record(death.TargetId, victim.Name, victim.JobId, death.Time, Current.Start,
                        id => world.Lookup(id)?.Name ?? string.Empty));
                    // Checked at the death, not at fight end: a raise later doesn't undo a wipe.
                    if (Current.Combatants.Keys.Where(world.IsAlly).All(id => id == death.TargetId || world.IsDead(id)))
                        Current.PartyWiped = true;
                }
                else if (Current.Enemies.TryGetValue(death.TargetId, out var enemy))
                    enemy.Died = true;
                break;
        }
    }

    public void Update(DateTime now, bool partyInCombat)
    {
        if (Current == null)
            return;

        ExpirePendingDrops(now);

        if (partyInCombat || MainEnemyEngaged(Current))
        {
            sawPartyInCombat = true;
            Current.HeldAt = null;
            return;
        }

        if (sawPartyInCombat)
        {
            Current.HeldAt ??= now;
            if (now - Current.HeldAt >= OutOfCombatGrace)
                End(FightOutcome.Unknown, Current.HeldAt.Value);
            return;
        }

        if (now - Current.LastActivity >= IdleTimeout)
            End(FightOutcome.Unknown, Current.LastActivity);
    }

    public void End(FightOutcome outcome, DateTime endTime)
    {
        if (Current == null)
            return;

        var fight = Current;
        if (fight.DotTicks.Count > 0)
            Resplit(heal: false);
        if (fight.HotTicks.Count > 0)
            Resplit(heal: true);
        fight.IsActive = false;
        fight.HeldAt = null;
        fight.EndTime = endTime < fight.Start ? fight.Start : endTime;
        if (outcome == FightOutcome.Unknown && MainEnemyDied(fight))
            outcome = FightOutcome.Clear;
        else if (outcome == FightOutcome.Unknown && fight.PartyWiped)
            outcome = FightOutcome.Wipe;
        if (outcome != FightOutcome.Unknown)
            fight.Outcome = outcome;
        Current = null;
        Last = fight;
        sawPartyInCombat = false;
        FightEnded?.Invoke(fight);
    }

    private bool MainEnemyEngaged(Fight fight)
    {
        if (fight.Enemies.Count == 0)
            return false;
        var (id, enemy) = fight.Enemies.MaxBy(e => e.Value.DamageTaken);
        return !enemy.Died && world.IsEngaged(id);
    }

    private bool MainEnemyDied(Fight fight)
    {
        if (fight.Enemies.Count == 0)
            return false;
        var (id, enemy) = fight.Enemies.MaxBy(e => e.Value.DamageTaken);
        if (!enemy.Died && world.IsDead(id))
            enemy.Died = true;
        return enemy.Died;
    }

    private void HandleHit(ActionHitEvent hit)
    {
        if (EventOutOfRange(hit.SourceId, hit.SourceOwnerId, hit.TargetId))
            return;

        var sourceIsAlly = Counts(hit.SourceId, hit.SourceOwnerId);
        var targetIsAlly = Counts(hit.TargetId, 0);
        var allyInvolved = IsAllyOrAllyPet(hit.SourceId, hit.SourceOwnerId) || world.IsAlly(hit.TargetId);
        // Other players only count against enemies the party is already fighting, so unrelated nearby fights don't leak
        // in. Once on the meter, everything they hit counts.
        var onlyOnMeter = false;
        if (!allyInvolved && !FightsKnownEnemy(hit.SourceId, hit.SourceOwnerId, hit.TargetId, sourceIsAlly))
        {
            if (hit.Kind is not (HitKind.Damage or HitKind.BlockedDamage or HitKind.ParriedDamage or HitKind.Miss)
                || targetIsAlly || !OnMeter(hit.SourceId, hit.SourceOwnerId))
                return;
            onlyOnMeter = true;
        }

        if (hit.Kind is HitKind.Damage or HitKind.BlockedDamage or HitKind.ParriedDamage)
        {
            if (!onlyOnMeter && !EnsureFight(hit.Time, sourceIsAlly, targetIsAlly, allyInvolved))
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
                Ability(attacker.DamageAbilities, hit.ActionId).Add(hit.Amount, hit.Crit, hit.DirectHit);
                if (hit is { Kind: HitKind.Damage, Crit: false, DirectHit: false, Potency: > 0 } && hit.Amount > 0)
                    attacker.PotencySamples.Add((double)hit.Amount / hit.Potency.Value);
                if (hit is { Kind: HitKind.Damage, Potency: > 0 } && hit.Amount > 0)
                    NoteFixedHit(attacker, hit);
                if (!onlyOnMeter)
                    AddEnemyDamage(hit.TargetId, hit.Amount);
            }

            if (targetIsAlly && !sourceIsAlly)
            {
                var victim = Stats(hit.TargetId);
                victim.DamageTaken += hit.Amount;
                victim.HitsTaken++;
                AddEnemyDamage(hit.SourceOwnerId != 0 ? hit.SourceOwnerId : hit.SourceId, 0);
                deaths.Damage(hit.TargetId, hit.Time, hit.SourceId, hit.ActionId, hit.Amount, hit.Crit, hit.DirectHit,
                    hit.Kind == HitKind.ParriedDamage, hit.Kind == HitKind.BlockedDamage, hit.Hp, hit.Defense);
                NoteHitOnAlly(hit.TargetId, hit.Time);
                Ability(victim.TakenAbilities, hit.ActionId).Add(hit.Amount, hit.Crit, hit.DirectHit);
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
                    if (hit is { Crit: false, Potency: > 0 } && hit.Amount > 0)
                        healer.HealPotencySamples.Add((double)hit.Amount / hit.Potency.Value);
                    Ability(healer.HealAbilities, hit.ActionId).Add(hit.Amount, hit.Crit, overheal: hit.Overheal);
                }
                if (targetIsAlly)
                {
                    Stats(hit.TargetId).HealingReceived += hit.Amount;
                    deaths.Heal(hit.TargetId, hit.Time, hit.SourceId, hit.ActionId, hit.Amount, hit.Overheal, hit.Crit, hit.Hp, hit.Defense);
                }
                break;
            case HitKind.Miss:
                if (sourceIsAlly && !targetIsAlly)
                    Stats(hit.SourceId, hit.SourceOwnerId).Misses++;
                if (targetIsAlly && !sourceIsAlly)
                    deaths.Miss(hit.TargetId, hit.Time, hit.SourceId, hit.ActionId, hit.Defense);
                break;
        }
    }

    private void HandleTick(PeriodicTickEvent tick)
    {
        if (tick is { IsHeal: false, Dots.Count: > 0 } && !Counts(tick.TargetId, 0))
        {
            HandleDotTick(tick, tick.Dots);
            return;
        }
        if (tick is { IsHeal: true, Dots.Count: > 0 })
        {
            HandleHotTick(tick, tick.Dots);
            return;
        }
        if (EventOutOfRange(tick.SourceId, tick.SourceOwnerId, tick.TargetId))
            return;

        var sourceIsAlly = Counts(tick.SourceId, tick.SourceOwnerId);
        var targetIsAlly = Counts(tick.TargetId, 0);
        var allyInvolved = IsAllyOrAllyPet(tick.SourceId, tick.SourceOwnerId) || world.IsAlly(tick.TargetId);
        if (!allyInvolved && !FightsKnownEnemy(tick.SourceId, tick.SourceOwnerId, tick.TargetId, sourceIsAlly))
        {
            if (!tick.IsHeal && !targetIsAlly && OnMeter(tick.SourceId, tick.SourceOwnerId))
            {
                var dotter = Stats(tick.SourceId, tick.SourceOwnerId);
                dotter.Damage += tick.Amount;
                dotter.DotDamage += tick.Amount;
                RecordTick(dotter.DamageAbilities, tick);
            }
            return;
        }

        if (tick.IsHeal)
        {
            if (Current == null)
                return;
            if (sourceIsAlly)
            {
                var healer = Stats(tick.SourceId, tick.SourceOwnerId);
                healer.Healing += tick.Amount;
                healer.Overheal += tick.Overheal;
                RecordTick(healer.HealAbilities, tick);
            }
            if (targetIsAlly)
            {
                Stats(tick.TargetId).HealingReceived += tick.Amount;
                deaths.Heal(tick.TargetId, tick.Time, tick.SourceId, Current.TickKey(tick.StatusIds ?? [], true), tick.Amount, tick.Overheal, false,
                    tick.Hp, tick.Defense);
            }
            return;
        }

        if (!EnsureFight(tick.Time, sourceIsAlly, targetIsAlly, allyInvolved))
            return;

        if (sourceIsAlly && !targetIsAlly)
        {
            var attacker = Stats(tick.SourceId, tick.SourceOwnerId);
            attacker.Damage += tick.Amount;
            attacker.DotDamage += tick.Amount;
            RecordTick(attacker.DamageAbilities, tick);
            AddEnemyDamage(tick.TargetId, tick.Amount);
        }
        if (targetIsAlly && !sourceIsAlly)
        {
            var victim = Stats(tick.TargetId);
            victim.DamageTaken += tick.Amount;
            AddEnemyDamage(tick.SourceOwnerId != 0 ? tick.SourceOwnerId : tick.SourceId, 0);
            RecordTick(victim.TakenAbilities, tick);
            deaths.Damage(tick.TargetId, tick.Time, tick.SourceId, Current!.TickKey(tick.StatusIds ?? [], false), tick.Amount,
                false, false, false, false, tick.Hp, tick.Defense);
            NoteHitOnAlly(tick.TargetId, tick.Time);
        }
    }

    // Ticks are kept raw and re-split as the rates improve.
    private void HandleDotTick(PeriodicTickEvent tick, IReadOnlyList<DotOnTarget> dots)
    {
        var near = dots.Where(d => !OutOfRange(d.SourceId, d.SourceOwnerId)).ToList();
        var allyInvolved = (world.IsAlly(tick.SourceId) && !OutOfRange(tick.SourceId, 0))
            || near.Any(d => IsAllyOrAllyPet(d.SourceId, d.SourceOwnerId));
        var counted = near.Where(d => Counts(d.SourceId, d.SourceOwnerId)).Select(d => d.SourceId).ToHashSet();
        if (!allyInvolved && !FightsKnownEnemy(tick.SourceId, 0, tick.TargetId, sourceIsPlayer: true))
        {
            counted = near.Where(d => OnMeter(d.SourceId, d.SourceOwnerId)).Select(d => d.SourceId).ToHashSet();
            if (counted.Count == 0)
                return;
            Current!.DotTicks.Add(new DotTick(tick.Time, tick.TargetId, tick.Amount, dots, counted,
                AppliedRates: AppliedRates(Current, tick.TargetId, dots)));
            NoteActions(Current, dots);
            Resplit(heal: false);
            return;
        }
        if (!EnsureFight(tick.Time, counted.Count > 0, false, allyInvolved))
            return;

        var fight = Current!;
        fight.DotTicks.Add(new DotTick(tick.Time, tick.TargetId, tick.Amount, dots, counted,
            AppliedRates: AppliedRates(fight, tick.TargetId, dots)));
        NoteActions(fight, dots);
        AddEnemyDamage(tick.TargetId, tick.Amount);
        Resplit(heal: false);
    }

    private void HandleHotTick(PeriodicTickEvent tick, IReadOnlyList<DotOnTarget> dots)
    {
        if (Current == null)
            return;
        var allyInvolved = world.IsAlly(tick.TargetId) || dots.Any(d => IsAllyOrAllyPet(d.SourceId, d.SourceOwnerId));
        if (!allyInvolved)
            return;

        var fight = Current;
        var counted = dots.Where(d => Counts(d.SourceId, d.SourceOwnerId) && !OutOfRange(d.SourceId, d.SourceOwnerId))
            .Select(d => d.SourceId).ToHashSet();
        fight.HotTicks.Add(new DotTick(tick.Time, tick.TargetId, tick.Amount, dots, counted, tick.Overheal));
        NoteActions(fight, dots);
        if (Counts(tick.TargetId, 0))
        {
            Stats(tick.TargetId).HealingReceived += tick.Amount;
            deaths.Heal(tick.TargetId, tick.Time, tick.SourceId, fight.TickKey(tick.StatusIds ?? [], true), tick.Amount, tick.Overheal, false,
                tick.Hp, tick.Defense);
        }
        Resplit(heal: true);
    }

    // Direct hits add exactly this much. Crits depend on gear.
    private const double DirectHitMultiplier = 1.25;

    // Iron Jaws re-applies the Bard's DoTs with the current buffs.
    public const uint IronJaws = 3560;

    private void NoteFixedHit(CombatantStats attacker, ActionHitEvent hit)
    {
        double? rate = hit.Crit ? null : hit.Amount / (double)hit.Potency!.Value / (hit.DirectHit ? DirectHitMultiplier : 1);
        Current!.LastHits[(hit.SourceId, hit.TargetId, hit.ActionId)] = (hit.Time, rate);
        if (rate is not { } clean)
            return;
        if (!attacker.ActionPotencySamples.TryGetValue(hit.ActionId, out var samples))
            attacker.ActionPotencySamples[hit.ActionId] = samples = new List<double>();
        samples.Add(clean);
    }

    private static IReadOnlyList<double?> AppliedRates(Fight fight, uint targetId, IReadOnlyList<DotOnTarget> dots) =>
        dots.Select(dot =>
        {
            if (dot.ActionId == 0)
                return null;
            fight.LastHits.TryGetValue((dot.SourceId, targetId, dot.ActionId), out var own);
            fight.LastHits.TryGetValue((dot.SourceId, targetId, IronJaws), out var jaws);
            return jaws.Time > own.Time ? jaws.Rate : own.Rate;
        }).ToList();

    private static Dictionary<uint, double> DotApplyingRates(Fight fight)
    {
        var applying = fight.DotActions.Values.Append(IronJaws).ToHashSet();
        var rates = new Dictionary<uint, double>();
        foreach (var combatant in fight.Combatants.Values)
        {
            var samples = combatant.ActionPotencySamples.Where(a => applying.Contains(a.Key)).SelectMany(a => a.Value).ToList();
            if (samples.Count > 0)
                rates[combatant.Id] = DotSplit.Median(samples);
        }
        return rates;
    }

    private static void NoteActions(Fight fight, IReadOnlyList<DotOnTarget> dots)
    {
        foreach (var dot in dots)
        {
            if (dot.ActionId != 0)
                fight.DotActions[dot.StatusId] = dot.ActionId;
        }
    }

    private void Resplit(bool heal)
    {
        var fight = Current!;
        var rates = new Dictionary<uint, double>();
        var dotRates = heal ? null : DotApplyingRates(fight);
        foreach (var combatant in fight.Combatants.Values)
        {
            var samples = heal ? combatant.HealPotencySamples : combatant.PotencySamples;
            if (samples.Count > 0)
                rates[combatant.Id] = DotSplit.Median(new List<double>(samples));

            if (heal)
            {
                combatant.Healing -= combatant.HotHealing;
                combatant.Overheal -= combatant.HotOverheal;
                combatant.HotHealing = 0;
                combatant.HotOverheal = 0;
            }
            else
            {
                combatant.Damage -= combatant.DotDamage;
                combatant.DotDamage = 0;
            }
            var abilities = heal ? combatant.HealAbilities : combatant.DamageAbilities;
            foreach (var key in abilities.Keys.Where(AbilityStats.IsSplitTickKey).ToList())
                abilities.Remove(key);
        }

        foreach (var tick in heal ? fight.HotTicks : fight.DotTicks)
        {
            foreach (var share in DotSplit.Split(tick, rates, dotRates))
            {
                if (!tick.Counted.Contains(share.SourceId))
                    continue;
                var owner = Stats(share.SourceId, share.SourceOwnerId);
                var key = AbilityStats.SplitTickKey(share.StatusId);
                if (heal)
                {
                    owner.Healing += share.Amount;
                    owner.Overheal += share.Overheal;
                    owner.HotHealing += share.Amount;
                    owner.HotOverheal += share.Overheal;
                    Ability(owner.HealAbilities, key).Add(share.Amount, overheal: share.Overheal);
                }
                else
                {
                    owner.Damage += share.Amount;
                    owner.DotDamage += share.Amount;
                    Ability(owner.DamageAbilities, key).Add(share.Amount);
                }
            }
        }
    }

    private bool EnsureFight(DateTime time, bool sourceCounts, bool targetCounts, bool allyInvolved)
    {
        if (sourceCounts == targetCounts)
            return false;

        if (Current == null)
        {
            if (!allyInvolved)
                return false;
            var context = ContextProvider?.Invoke();
            Current = new Fight
            {
                Start = time,
                LastActivity = time,
                Zone = context?.Zone ?? string.Empty,
                CharacterName = context?.CharacterName ?? string.Empty,
                World = context?.World ?? string.Empty,
                LocalPlayerId = context?.LocalPlayerId ?? 0,
                AreaKey = context?.AreaKey,
            };
            deaths.Clear();
            shields.Clear();
            lastHitOnAlly.Clear();
            pendingDrops.Clear();
            sawPartyInCombat = false;
        }

        if (time > Current.LastActivity)
            Current.LastActivity = time;
        return true;
    }

    private bool IsAllyOrAllyPet(uint id, uint ownerId) =>
        world.IsAlly(id) || (ownerId != 0 && world.IsAlly(ownerId));

    private bool FightsKnownEnemy(uint sourceId, uint sourceOwnerId, uint targetId, bool sourceIsPlayer)
    {
        if (Current == null)
            return false;
        var enemyId = sourceIsPlayer ? targetId : sourceOwnerId != 0 ? sourceOwnerId : sourceId;
        return Current.Enemies.ContainsKey(enemyId);
    }

    private bool IsPlayerSide(uint id, uint ownerId) =>
        world.IsAlly(id) || world.IsOtherPlayer(id) || (ownerId != 0 && (world.IsAlly(ownerId) || world.IsOtherPlayer(ownerId)));

    private bool EventOutOfRange(uint sourceId, uint sourceOwnerId, uint targetId) =>
        IsPlayerSide(sourceId, sourceOwnerId) ? OutOfRange(sourceId, sourceOwnerId)
        : IsPlayerSide(targetId, 0) && OutOfRange(targetId, 0);

    private bool OutOfRange(uint id, uint ownerId)
    {
        var player = ownerId != 0 ? ownerId : id;
        if (player == 0 || player == world.LocalPlayerId)
            return false;
        return world.DistanceTo(player) is { } distance && distance > MaxRange;
    }

    private bool OnMeter(uint id, uint ownerId) =>
        Current != null && Counts(id, ownerId)
        && (Current.Combatants.ContainsKey(id) || (ownerId != 0 && Current.Combatants.ContainsKey(ownerId)));

    private bool Counts(uint id, uint ownerId) =>
        IsAllyOrAllyPet(id, ownerId)
        || (IncludeOtherPlayers && (world.IsOtherPlayer(id) || (ownerId != 0 && world.IsOtherPlayer(ownerId))));

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

    private void HandleShieldLost(ShieldLostEvent lost)
    {
        if (Current == null || !world.IsAlly(lost.TargetId) || lost.MaxHp == 0)
            return;

        var amount = (long)(lost.ShieldPercentBefore - lost.ShieldPercentAfter) * lost.MaxHp / 100;
        var justHit = lastHitOnAlly.TryGetValue(lost.TargetId, out var hitTime) && lost.Time - hitTime <= AbsorbWindow;
        if (justHit)
            CreditAbsorbed(lost.TargetId, amount);
        else
            pendingDrops.Add((lost.TargetId, lost.Time, amount));
    }

    private void NoteHitOnAlly(uint targetId, DateTime time)
    {
        lastHitOnAlly[targetId] = time;
        for (var i = pendingDrops.Count - 1; i >= 0; i--)
        {
            var drop = pendingDrops[i];
            if (drop.TargetId != targetId || time - drop.Time > AbsorbWindow)
                continue;
            pendingDrops.RemoveAt(i);
            CreditAbsorbed(targetId, drop.Amount);
        }
    }

    private void ExpirePendingDrops(DateTime now)
    {
        for (var i = pendingDrops.Count - 1; i >= 0; i--)
        {
            var drop = pendingDrops[i];
            if (now - drop.Time <= AbsorbWindow)
                continue;
            pendingDrops.RemoveAt(i);
            shields.Expire(drop.TargetId, drop.Amount);
        }
    }

    private void CreditAbsorbed(uint targetId, long amount)
    {
        foreach (var (statusId, sourceId, absorbed) in shields.Absorb(targetId, amount))
        {
            if (sourceId == 0 || !IsAllyOrAllyPet(sourceId, world.Lookup(sourceId)?.OwnerId ?? 0))
                continue;
            var caster = Stats(sourceId);
            caster.Healing += absorbed;
            caster.Shielding += absorbed;
            Ability(caster.HealAbilities, AbilityStats.ShieldKey(statusId)).Add(absorbed);
            Stats(targetId).HealingReceived += absorbed;
        }
    }

    private void RecordTick(Dictionary<uint, AbilityStats> abilities, PeriodicTickEvent tick) =>
        Ability(abilities, Current!.TickKey(tick.StatusIds ?? [], tick.IsHeal)).Add(tick.Amount, overheal: tick.Overheal);

    private static AbilityStats Ability(Dictionary<uint, AbilityStats> abilities, uint key)
    {
        if (!abilities.TryGetValue(key, out var ability))
        {
            ability = new AbilityStats();
            abilities[key] = ability;
        }
        return ability;
    }

    private void AddEnemyDamage(uint enemyId, long amount)
    {
        var fight = Current!;
        if (!fight.Enemies.TryGetValue(enemyId, out var enemy))
        {
            enemy = new EnemyStats();
            fight.Enemies[enemyId] = enemy;
        }
        if (string.IsNullOrEmpty(enemy.Name))
            enemy.Name = world.Lookup(enemyId)?.Name ?? string.Empty;
        enemy.DamageTaken += amount;
    }
}
