using Studium.Core;
using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Tests;

public class DotSplitTests
{
    private const uint Bard = 1, WhiteMage = 2, BlackMage = 3, Stranger = 4, Boss = 100;
    private const uint Stormbite = 1201, CausticBite = 1200, Dia = 1871, ThunderIII = 163;
    private const uint StormbiteAction = 7407, CausticBiteAction = 7406, DiaAction = 16532, HeavyShot = 97, Glare = 25859;
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<uint> Everyone = [Bard, WhiteMage, BlackMage];

    private sealed class PartyWorld : ICombatWorld
    {
        public bool IsAlly(uint id) => id is Bard or WhiteMage or BlackMage;
        public bool IsOtherPlayer(uint id) => id == Stranger;
        public ActorSnapshot? Lookup(uint id) => new($"#{id}", id < Boss ? 23u : 0, 0);
    }

    private static DotTick Tick(long amount, params DotOnTarget[] dots) => new(T0, Boss, amount, dots, Everyone);

    [Fact]
    public void OneDotTakesTheWholeTick()
    {
        var share = Assert.Single(DotSplit.Split(Tick(900, new DotOnTarget(Stormbite, Bard, StormbiteAction, 25)), new Dictionary<uint, double>()));
        Assert.Equal(new DotShare(Bard, 0, Stormbite, StormbiteAction, 900), share);
    }

    [Fact]
    public void WeightsByPotencyTimesDamagePerPotency()
    {
        var rates = new Dictionary<uint, double> { [Bard] = 10, [WhiteMage] = 20 };
        var shares = DotSplit.Split(Tick(1750,
            new DotOnTarget(Stormbite, Bard, StormbiteAction, 25),
            new DotOnTarget(Dia, WhiteMage, DiaAction, 75)), rates).ToList();

        Assert.Equal(250, shares.Single(s => s.SourceId == Bard).Amount);
        Assert.Equal(1500, shares.Single(s => s.SourceId == WhiteMage).Amount);
    }

    [Fact]
    public void PlayersWithoutHitsCountAsTheMedianPlayer()
    {
        var rates = new Dictionary<uint, double> { [Bard] = 10 };
        var shares = DotSplit.Split(Tick(1000,
            new DotOnTarget(Stormbite, Bard, StormbiteAction, 25),
            new DotOnTarget(Dia, WhiteMage, DiaAction, 75)), rates).ToList();

        Assert.Equal(250, shares.Single(s => s.SourceId == Bard).Amount);
        Assert.Equal(750, shares.Single(s => s.SourceId == WhiteMage).Amount);
    }

    [Fact]
    public void UnknownPotencyCountsAsTheAverageDot()
    {
        var shares = DotSplit.Split(Tick(900,
            new DotOnTarget(Stormbite, Bard, StormbiteAction, 20),
            new DotOnTarget(CausticBite, Bard, CausticBiteAction, 40),
            new DotOnTarget(ThunderIII, BlackMage, 0, 0)), new Dictionary<uint, double>()).ToList();

        Assert.Equal(300, shares.Single(s => s.StatusId == ThunderIII).Amount);
    }

    [Fact]
    public void SharesAlwaysAddUpToTheTick()
    {
        var shares = DotSplit.Split(Tick(1001,
            new DotOnTarget(Stormbite, Bard, StormbiteAction, 1),
            new DotOnTarget(CausticBite, Bard, CausticBiteAction, 1),
            new DotOnTarget(Dia, WhiteMage, DiaAction, 1)), new Dictionary<uint, double>()).ToList();

        Assert.Equal(1001, shares.Sum(s => s.Amount));
    }

    [Fact]
    public void MedianIgnoresBuffWindows()
    {
        Assert.Equal(10, DotSplit.Median([10, 12, 10, 12, 10]));
        Assert.Equal(11, DotSplit.Median([10, 12]));
    }

    [Fact]
    public void TrackerCreditsEachPlayerTheirShare()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        tracker.Handle(new ActionHitEvent(T0, WhiteMage, 0, Boss, Glare, 1, HitKind.Damage, 6200, false, false, Potency: 310));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Bard, 0, Boss, false, 1750, Dots:
        [
            new DotOnTarget(Stormbite, Bard, StormbiteAction, 25),
            new DotOnTarget(Dia, WhiteMage, DiaAction, 75),
        ]));

        var fight = tracker.Current!;
        Assert.Equal(1600 + 250, fight.Combatants[Bard].Damage);
        Assert.Equal(6200 + 1500, fight.Combatants[WhiteMage].Damage);
        Assert.Equal(1500, fight.Combatants[WhiteMage].DamageAbilities[AbilityStats.SplitTickKey(Dia)].Total);
        Assert.Equal(DiaAction, fight.DotActions[Dia]);
        Assert.Equal(6200 + 1500 + 1600 + 250, fight.Enemies[Boss].DamageTaken);
    }

    [Fact]
    public void EarlierTicksAreResplitAsRatesSettle()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        DotOnTarget[] dots = [new(Stormbite, Bard, StormbiteAction, 25), new(Dia, WhiteMage, DiaAction, 75)];
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Bard, 0, Boss, false, 1000, Dots: dots));
        Assert.Equal(750, tracker.Current!.Combatants[WhiteMage].DotDamage);

        tracker.Handle(new ActionHitEvent(T0.AddSeconds(4), WhiteMage, 0, Boss, Glare, 1, HitKind.Damage, 6200, false, false, Potency: 310));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(6), Bard, 0, Boss, false, 1750, Dots: dots));

        var whm = tracker.Current!.Combatants[WhiteMage];
        Assert.Equal(857 + 1500, whm.DotDamage);
        Assert.Equal(6200 + whm.DotDamage, whm.Damage);
        Assert.Equal(2, whm.DamageAbilities[AbilityStats.SplitTickKey(Dia)].Hits);
    }

    [Fact]
    public void CritsAndDirectHitsDontSetTheRate()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 3000, true, false, Potency: 160));
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 2000, false, true, Potency: 160));
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, 7, 1, HitKind.Damage, 900, false, false));

        Assert.Equal([10.0], tracker.Current!.Combatants[Bard].PotencySamples);
    }

    [Fact]
    public void SharesOfUncountedPlayersAreDropped()
    {
        var tracker = new FightTracker(new PartyWorld()) { IncludeOtherPlayers = false };
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Stranger, 0, Boss, false, 1000, Dots:
        [
            new DotOnTarget(Stormbite, Bard, StormbiteAction, 25),
            new DotOnTarget(Dia, Stranger, DiaAction, 75),
        ]));

        var fight = tracker.Current!;
        Assert.Equal(250, fight.Combatants[Bard].DotDamage);
        Assert.False(fight.Combatants.ContainsKey(Stranger));
    }

    [Fact]
    public void DotsOfPlayersOnTheMeterCountOnEnemiesTheFightDoesntKnow()
    {
        const uint Add = 101;
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        tracker.Handle(new ActionHitEvent(T0, Stranger, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Stranger, 0, Add, false, 900, Dots:
            [new DotOnTarget(Stormbite, Stranger, StormbiteAction, 25)]));

        var fight = tracker.Current!;
        Assert.Equal(900, fight.Combatants[Stranger].DotDamage);
        Assert.False(fight.Enemies.ContainsKey(Add));
    }

    private const uint ThunderAction = 153, FireIV = 3577;

    private static ActionHitEvent Applying(double seconds, uint source, uint action, long amount, bool crit = false, bool dh = false) =>
        new(T0.AddSeconds(seconds), source, 0, Boss, action, 1, HitKind.Damage, amount, crit, dh, Potency: 100);

    private static readonly DotOnTarget[] BardAndThunder =
        [new(Stormbite, Bard, StormbiteAction, 50), new(ThunderIII, BlackMage, ThunderAction, 50)];

    private static PeriodicTickEvent DotTickAt(double seconds, long amount) =>
        new(T0.AddSeconds(seconds), Bard, 0, Boss, false, amount, Dots: BardAndThunder);

    [Fact]
    public void DotsUseTheRateOfTheHitThatAppliedThem()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(Applying(0, BlackMage, FireIV, 1800));
        tracker.Handle(Applying(0, BlackMage, FireIV, 1800));
        tracker.Handle(Applying(1, BlackMage, ThunderAction, 1000));
        tracker.Handle(Applying(1, Bard, StormbiteAction, 1000));
        tracker.Handle(DotTickAt(3, 1000));

        Assert.Equal(500, tracker.Current!.Combatants[BlackMage].DotDamage);
    }

    [Fact]
    public void ADirectHitApplyingHitCountsWithoutItsBonus()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(Applying(0, BlackMage, ThunderAction, 1250, dh: true));
        tracker.Handle(Applying(0, Bard, StormbiteAction, 1000));
        tracker.Handle(DotTickAt(3, 1000));

        Assert.Equal(500, tracker.Current!.Combatants[BlackMage].DotDamage);
    }

    [Fact]
    public void ACritApplyingHitFallsBackToThePlayersCleanApplyingHits()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(Applying(0, BlackMage, FireIV, 1800));
        tracker.Handle(Applying(0, BlackMage, ThunderAction, 1000));
        tracker.Handle(Applying(0, Bard, StormbiteAction, 1000));
        tracker.Handle(Applying(30, BlackMage, ThunderAction, 3000, crit: true));
        tracker.Handle(DotTickAt(33, 1000));

        Assert.Equal(500, tracker.Current!.Combatants[BlackMage].DotDamage);
    }

    [Fact]
    public void IronJawsReappliesTheBardsDots()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(Applying(0, BlackMage, ThunderAction, 1000));
        tracker.Handle(Applying(0, Bard, StormbiteAction, 1000));
        tracker.Handle(Applying(10, Bard, FightTracker.IronJaws, 2000));
        tracker.Handle(DotTickAt(12, 1500));

        Assert.Equal(1000, tracker.Current!.Combatants[Bard].DotDamage);
        Assert.Equal(500, tracker.Current.Combatants[BlackMage].DotDamage);
    }

    [Fact]
    public void AbilityRowsShowSplitDotsAsTicks()
    {
        var tracker = new FightTracker(new PartyWorld());
        tracker.Handle(new ActionHitEvent(T0, Bard, 0, Boss, HeavyShot, 1, HitKind.Damage, 1600, false, false, Potency: 160));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Bard, 0, Boss, false, 500, Dots: [new DotOnTarget(Stormbite, Bard, StormbiteAction, 25)]));

        var row = FightView.Abilities(tracker.Current!, Bard, MeterTab.Dps, mergePets: true).Single(r => r.IsTick);
        Assert.Equal([Stormbite], row.StatusIds);
        Assert.Equal(StormbiteAction, row.LinkedActionId);
    }

    private const uint Scholar = 5, Faerie = 50, Tank = 6;
    private const uint Regen = 158, WhisperingDawn = 315, RegenAction = 137, CureII = 135, WhisperingDawnAction = 16537;

    private sealed class HealWorld : ICombatWorld
    {
        public bool IsAlly(uint id) => id is WhiteMage or Scholar or Tank;
        public ActorSnapshot? Lookup(uint id) => id == Faerie ? new("Eos", 0, Scholar) : new($"#{id}", 24, 0);
    }

    [Fact]
    public void HotTicksAreSplitByHealingPerPotency()
    {
        var tracker = new FightTracker(new HealWorld());
        tracker.Handle(new ActionHitEvent(T0, Tank, 0, Boss, HeavyShot, 1, HitKind.Damage, 1000, false, false));
        tracker.Handle(new ActionHitEvent(T0, WhiteMage, 0, Tank, CureII, 1, HitKind.Heal, 8000, false, false, Potency: 800));
        tracker.Handle(new ActionHitEvent(T0, Scholar, 0, Boss, HeavyShot, 1, HitKind.Damage, 500, false, false));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), WhiteMage, 0, Tank, true, 3300, Overheal: 330, Dots:
        [
            new DotOnTarget(Regen, WhiteMage, RegenAction, 250),
            new DotOnTarget(WhisperingDawn, Faerie, WhisperingDawnAction, 80, Scholar),
        ]));

        var fight = tracker.Current!;
        var whm = fight.Combatants[WhiteMage];
        Assert.Equal(8000 + 2500, whm.Healing);
        Assert.Equal(250, whm.HotOverheal);
        Assert.Equal(2500, whm.HealAbilities[AbilityStats.SplitTickKey(Regen)].Total);
        Assert.Equal(800, fight.Combatants[Faerie].HotHealing);
        Assert.Equal(Scholar, fight.Combatants[Faerie].OwnerId);
        Assert.Equal(8000 + 3300, fight.Combatants[Tank].HealingReceived);

        var merged = FightView.Summarize(fight, T0.AddSeconds(3), mergePets: true).Rows.Single(r => r.Id == Scholar);
        Assert.Equal(800, merged.Healing);
    }

    [Fact]
    public void CritHealsDontSetTheRate()
    {
        var tracker = new FightTracker(new HealWorld());
        tracker.Handle(new ActionHitEvent(T0, Tank, 0, Boss, HeavyShot, 1, HitKind.Damage, 1000, false, false));
        tracker.Handle(new ActionHitEvent(T0, WhiteMage, 0, Tank, CureII, 1, HitKind.Heal, 8000, false, false, Potency: 800));
        tracker.Handle(new ActionHitEvent(T0, WhiteMage, 0, Tank, CureII, 1, HitKind.Heal, 12000, true, false, Potency: 800));

        Assert.Equal([10.0], tracker.Current!.Combatants[WhiteMage].HealPotencySamples);
    }
}
