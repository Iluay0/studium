using Studium.Core.Combat;
using Studium.Core.Fights;
using Studium.Core.History;

namespace Studium.Tests;

public sealed class FightStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 9, 24, 20, 0, 0, DateTimeKind.Utc);
    private readonly string directory = Path.Combine(Path.GetTempPath(), "studium-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class World : ICombatWorld
    {
        public bool IsAlly(uint id) => id == 1;
        public ActorSnapshot? Lookup(uint id) => id == 1 ? new("Iluay Dory", 23, 0) : new("Striking Dummy", 0, 0);
    }

    private static Fight MakeFight(DateTime start, long damage = 1000)
    {
        var tracker = new FightTracker(new World())
        {
            ContextProvider = () => new FightContext("The Lavender Beds", "Iluay Dory", "Moogle", 1),
        };
        tracker.Handle(new ActionHitEvent(start, 1, 0, 100, 7406, 1, HitKind.Damage, damage, true, false));
        tracker.Handle(new PeriodicTickEvent(start.AddSeconds(3), 1, 0, 100, false, 50));
        tracker.End(FightOutcome.Clear, start.AddSeconds(10));
        return tracker.Last!;
    }

    [Fact]
    public void SavedFightRoundTrips()
    {
        var fight = MakeFight(T0);
        new FightStore(directory).Save(fight);

        var reopened = new FightStore(directory);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(fight.Id, entry.Id);
        Assert.Equal("Striking Dummy", entry.Name);
        Assert.Equal("The Lavender Beds", entry.Zone);
        Assert.Equal("Iluay Dory", entry.CharacterName);
        Assert.Equal(23u, entry.JobId);
        Assert.Equal(10, entry.DurationSeconds);
        Assert.Equal(105, entry.LocalDps);
        Assert.Equal(FightOutcome.Clear, entry.Outcome);

        var loaded = reopened.Load(fight.Id)!;
        Assert.Equal(fight.Start, loaded.Start);
        Assert.False(loaded.IsActive);
        Assert.Equal(TimeSpan.FromSeconds(10), loaded.FinalDuration);
        var stats = loaded.Combatants[1];
        Assert.Equal(1050, stats.Damage);
        Assert.Equal(1, stats.Crits);
        Assert.Equal(1000, stats.DamageAbilities[7406].Total);
        Assert.Equal(50, stats.DamageAbilities[AbilityStats.DotKey].Total);
        Assert.Equal("Striking Dummy", loaded.Name);
    }

    [Fact]
    public void EntriesAreNewestFirst()
    {
        var store = new FightStore(directory);
        store.Save(MakeFight(T0));
        store.Save(MakeFight(T0.AddHours(1)));
        Assert.Equal([T0.AddHours(1), T0], store.Entries.Select(e => e.Start));
    }

    [Fact]
    public void RetentionDeletesOldUnpinnedFights()
    {
        var store = new FightStore(directory);
        var old = MakeFight(T0);
        var oldPinned = MakeFight(T0.AddMinutes(1));
        var recent = MakeFight(T0.AddDays(6));
        store.Save(old);
        store.Save(oldPinned);
        store.Save(recent);
        store.SetPinned(oldPinned.Id, true);

        var removed = store.ApplyRetention(T0.AddDays(7).AddHours(1), TimeSpan.FromDays(7));

        Assert.Equal(1, removed);
        Assert.Equal([recent.Id, oldPinned.Id], store.Entries.Select(e => e.Id));
        Assert.Null(store.Load(old.Id));
    }

    [Fact]
    public void DeleteRemovesFileAndEntry()
    {
        var store = new FightStore(directory);
        var fight = MakeFight(T0);
        store.Save(fight);
        Assert.True(store.Delete(fight.Id));
        Assert.Empty(store.Entries);
        Assert.Empty(new FightStore(directory).Entries);
    }

    [Fact]
    public void MissingIndexIsRebuiltFromFilesKeepingPins()
    {
        var store = new FightStore(directory);
        var fight = MakeFight(T0);
        store.Save(fight);
        store.SetPinned(fight.Id, true);

        File.Delete(Path.Combine(directory, "index.json"));
        var entry = Assert.Single(new FightStore(directory).Entries);
        Assert.True(entry.Pinned);
    }

    [Fact]
    public void CorruptIndexIsRebuilt()
    {
        new FightStore(directory).Save(MakeFight(T0));
        File.WriteAllText(Path.Combine(directory, "index.json"), "{ not json");
        Assert.Single(new FightStore(directory).Entries);
    }

    [Fact]
    public void CorruptFightFileIsSkipped()
    {
        var store = new FightStore(directory);
        var fight = MakeFight(T0);
        store.Save(fight);
        File.WriteAllText(Path.Combine(directory, fight.Id.ToString("N") + ".json.gz"), "garbage");
        Assert.Null(store.Load(fight.Id));
    }
}
