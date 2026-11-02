using Studium.Core.Combat;
using Studium.Core.Fights;

namespace Studium.Tests;

public class RangeTests
{
    private const uint Me = 1, Healer = 2, Bard = 3, Stranger = 4, StrangerPet = 50, Boss = 100;
    private const uint Stormbite = 1201, Dia = 1871, StormbiteAction = 7407, DiaAction = 16532;
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);

    private sealed class RangeWorld : ICombatWorld
    {
        public Dictionary<uint, float> Distances { get; } = new();

        public bool IsAlly(uint id) => id is Me or Healer or Bard;

        public bool IsOtherPlayer(uint id) => id == Stranger;

        public uint LocalPlayerId => Me;

        public float? DistanceTo(uint id) => Distances.TryGetValue(id, out var d) ? d : null;

        public ActorSnapshot? Lookup(uint id) => new($"#{id}", id < Boss ? 23u : 0, id == StrangerPet ? Stranger : 0);
    }

    private readonly RangeWorld world = new();
    private readonly FightTracker tracker;

    public RangeTests() => tracker = new FightTracker(world);

    private static ActionHitEvent Hit(double seconds, uint source, uint target, long amount,
        HitKind kind = HitKind.Damage, uint owner = 0) =>
        new(T0.AddSeconds(seconds), source, owner, target, 7406, 1, kind, amount, false, false);

    [Fact]
    public void FarPlayerHitIsIgnored()
    {
        world.Distances[Healer] = 200;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, Healer, Boss, 500));

        Assert.False(tracker.Current!.Combatants.ContainsKey(Healer));
    }

    [Fact]
    public void NearPlayerHitCounts()
    {
        world.Distances[Healer] = 50;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, Healer, Boss, 500));

        Assert.Equal(500, tracker.Current!.Combatants[Healer].Damage);
    }

    [Fact]
    public void UnknownDistanceCounts()
    {
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, Healer, Boss, 500));

        Assert.Equal(500, tracker.Current!.Combatants[Healer].Damage);
    }

    [Fact]
    public void LocalPlayerIsNeverDropped()
    {
        world.Distances[Me] = 500;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, Boss, Me, 300));

        Assert.Equal(100, tracker.Current!.Combatants[Me].Damage);
        Assert.Equal(300, tracker.Current.Combatants[Me].DamageTaken);
    }

    [Fact]
    public void DamageTakenByFarPlayerIsIgnored()
    {
        world.Distances[Healer] = 200;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, Boss, Healer, 300));

        Assert.False(tracker.Current!.Combatants.ContainsKey(Healer));
    }

    [Fact]
    public void FarOwnersPetIsIgnored()
    {
        world.Distances[Stranger] = 150;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, StrangerPet, Boss, 400, owner: Stranger));

        Assert.False(tracker.Current!.Combatants.ContainsKey(StrangerPet));
    }

    [Fact]
    public void RangeFollowsMaxRange()
    {
        world.Distances[Healer] = 150;
        tracker.MaxRange = 160;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(Hit(1, Healer, Boss, 500));

        Assert.Equal(500, tracker.Current!.Combatants[Healer].Damage);
    }

    [Fact]
    public void CombinedTickDropsOnlyTheFarShare()
    {
        world.Distances[Healer] = 200;
        tracker.Handle(Hit(0, Me, Boss, 100));
        tracker.Handle(new PeriodicTickEvent(T0.AddSeconds(3), Bard, 0, Boss, false, 1000,
            Dots: [new DotOnTarget(Stormbite, Bard, StormbiteAction, 25), new DotOnTarget(Dia, Healer, DiaAction, 25)]));

        var fight = tracker.Current!;
        Assert.Equal(500, fight.Combatants[Bard].Damage);
        Assert.False(fight.Combatants.ContainsKey(Healer));
        Assert.Equal(1100, fight.Enemies[Boss].DamageTaken);
    }
}
