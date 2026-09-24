using Studium.Core;
using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Tests;

public class ShieldCreditTests
{
    private const uint Tank = 1, Scholar = 2, Sage = 3, Boss = 100;
    private const uint Galvanize = 297, Haima = 2612;
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);

    private sealed class World : ICombatWorld
    {
        public bool IsAlly(uint id) => id is Tank or Scholar or Sage;
        public ActorSnapshot? Lookup(uint id) => new(id switch { Tank => "Taro", Scholar => "Aria", Sage => "Luna", _ => "Boss" }, 0, 0);
    }

    [Fact]
    public void LedgerAbsorbsOldestFirst()
    {
        var ledger = new ShieldLedger();
        ledger.Gained(Tank, Galvanize, Scholar, 10_000);
        ledger.Gained(Tank, Haima, Sage, 8_000);
        Assert.Equal([(Galvanize, Scholar, 10_000L), (Haima, Sage, 5_000L)], ledger.Absorb(Tank, 15_000));
    }

    [Fact]
    public void ExpiredShieldsAreDroppedWithoutCredit()
    {
        var ledger = new ShieldLedger();
        ledger.Gained(Tank, Galvanize, Scholar, 10_000);
        ledger.Gained(Tank, Haima, Sage, 8_000);
        ledger.Expire(Tank, 10_000); // Galvanize ran out
        Assert.Equal([(Haima, Sage, 5_000L)], ledger.Absorb(Tank, 5_000));
    }

    private static FightTracker ShieldedTank()
    {
        var tracker = new FightTracker(new World());
        tracker.Handle(new ActionHitEvent(T0, Tank, 0, Boss, 7, 1, HitKind.Damage, 1000, false, false));
        tracker.Handle(new ShieldGainedEvent(T0.AddSeconds(1), Tank, Scholar, Galvanize, 0, 10, new TargetHp(100_000, 100_000)));
        return tracker;
    }

    [Fact]
    public void GaugeDropAfterAHitIsCreditedToTheCaster()
    {
        var tracker = ShieldedTank();
        // The gauge can move before or after the hit's packet: both orders credit the absorbed part.
        tracker.Handle(new ShieldLostEvent(T0.AddSeconds(2), Tank, 10, 4, 100_000));
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(2.1), Boss, 0, Tank, 500, 1, HitKind.Damage, 6_000, false, false));
        tracker.Handle(new ShieldLostEvent(T0.AddSeconds(3), Tank, 4, 0, 100_000));

        var fight = tracker.Current!;
        var scholar = fight.Combatants[Scholar];
        Assert.Equal(10_000, scholar.Shielding);
        Assert.Equal(10_000, scholar.Healing);
        Assert.Equal(10_000, fight.Combatants[Tank].HealingReceived);

        var shieldRow = Assert.Single(FightView.Abilities(fight, Scholar, MeterTab.Heal, mergePets: true));
        Assert.Equal(AbilityStats.ShieldKey(Galvanize), shieldRow.ActionId);
        Assert.Equal(10_000, shieldRow.Total);
    }

    [Fact]
    public void GaugeDropWithoutAHitIsAnExpiry()
    {
        var tracker = ShieldedTank();
        tracker.Handle(new ShieldLostEvent(T0.AddSeconds(30), Tank, 10, 0, 100_000));
        tracker.Update(T0.AddSeconds(33), partyInCombat: true); // no hit came: it expired
        tracker.Handle(new ActionHitEvent(T0.AddSeconds(34), Boss, 0, Tank, 500, 1, HitKind.Damage, 6_000, false, false));
        Assert.Equal(0, tracker.Current!.Combatants.GetValueOrDefault(Scholar)?.Shielding ?? 0);
    }

    [Fact]
    public void ShieldKeysDontCollide()
    {
        var key = AbilityStats.ShieldKey(Galvanize);
        Assert.True(AbilityStats.IsShieldKey(key));
        Assert.False(AbilityStats.IsStatusKey(key));
        Assert.False(AbilityStats.IsComboKey(key));
        Assert.False(AbilityStats.IsTick(key));
        Assert.False(AbilityStats.IsShieldKey(AbilityStats.StatusKey(Galvanize)));
        Assert.False(AbilityStats.IsShieldKey(AbilityStats.DotKey));
        Assert.Equal(Galvanize, AbilityStats.ShieldStatusOf(key));
    }
}
