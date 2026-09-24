using Studium.Core;
using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Tests;

public class TickAttributionTests
{
    private const uint CausticBite = 1200, Stormbite = 1201;
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);

    private sealed class SoloWorld : ICombatWorld
    {
        public bool IsAlly(uint id) => id == 1;
        public ActorSnapshot? Lookup(uint id) => new(id == 1 ? "Iluay Dory" : "Striking Dummy", id == 1 ? 23u : 0, 0);
    }

    private static FightTracker StartFight()
    {
        var tracker = new FightTracker(new SoloWorld());
        tracker.Handle(new ActionHitEvent(T0, 1, 0, 100, 7406, 1, HitKind.Damage, 1000, false, false));
        return tracker;
    }

    private static void Tick(FightTracker tracker, double seconds, long amount, params uint[] statuses) =>
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(seconds), 1, 0, 100, false, amount, StatusIds: statuses));

    [Fact]
    public void SingleDotIsNamedAfterItsStatus()
    {
        var tracker = StartFight();
        Tick(tracker, 3, 1668, CausticBite);

        var row = FightView.Abilities(tracker.Current!, 1, MeterTab.Dps, mergePets: true).Single(r => r.IsTick);
        Assert.Equal(AbilityStats.StatusKey(CausticBite), row.ActionId);
        Assert.Equal([CausticBite], row.StatusIds);
    }

    [Fact]
    public void SeveralDotsShareOneUnsplitRowPerCombination()
    {
        var tracker = StartFight();
        Tick(tracker, 3, 3300, CausticBite, Stormbite);
        Tick(tracker, 6, 3400, Stormbite, CausticBite); // same combination, other order

        var ticks = FightView.Abilities(tracker.Current!, 1, MeterTab.Dps, mergePets: true).Where(r => r.IsTick).ToList();
        var combo = Assert.Single(ticks);
        Assert.Equal(6700, combo.Total);
        Assert.Equal(2, combo.Hits);
        Assert.Equal([CausticBite, Stormbite], combo.StatusIds);
    }

    [Fact]
    public void OpenerTicksLandWhereTheyBelong()
    {
        // Caustic Bite ticks once before Stormbite is on the target, then both tick together.
        var tracker = StartFight();
        Tick(tracker, 2, 1668, CausticBite);
        Tick(tracker, 5, 3342, CausticBite, Stormbite);
        Tick(tracker, 8, 3390, CausticBite, Stormbite);

        var ticks = FightView.Abilities(tracker.Current!, 1, MeterTab.Dps, mergePets: true).Where(r => r.IsTick).ToList();
        Assert.Equal(2, ticks.Count);
        Assert.Equal(1668, ticks.Single(r => r.StatusIds.Count == 1).Total);
        Assert.Equal(6732, ticks.Single(r => r.StatusIds.Count == 2).Total);
    }

    [Fact]
    public void UnknownTicksUseGenericRows()
    {
        var tracker = StartFight();
        Tick(tracker, 3, 500);
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(4), 1, 0, 1, true, 300));

        var fight = tracker.Current!;
        Assert.Equal(500, fight.Combatants[1].DamageAbilities[AbilityStats.DotKey].Total);
        Assert.Equal(300, fight.Combatants[1].HealAbilities[AbilityStats.HotKey].Total);
    }

    [Fact]
    public void KeysDontCollide()
    {
        Assert.True(AbilityStats.IsStatusKey(AbilityStats.StatusKey(1871)));
        Assert.False(AbilityStats.IsComboKey(AbilityStats.StatusKey(1871)));
        Assert.True(AbilityStats.IsComboKey(AbilityStats.ComboKeyFlag | 3));
        Assert.False(AbilityStats.IsStatusKey(AbilityStats.DotKey));
        Assert.False(AbilityStats.IsComboKey(AbilityStats.DotKey));
        Assert.False(AbilityStats.IsTick(7406));
    }
}
