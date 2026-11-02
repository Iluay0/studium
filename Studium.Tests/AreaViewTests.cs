using Studium.Core.Fights;
using Studium.Core.History;

namespace Studium.Tests;

public class AreaViewTests
{
    private static readonly DateTime T0 = new(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromHours(4);
    private const string Key = "1122:2026-11-02T19:59:00.0000000Z";

    private static Fight MakeFight(DateTime start, double seconds, long damage, string? areaKey = Key, bool active = false)
    {
        var fight = new Fight
        {
            Start = start,
            LastActivity = start.AddSeconds(seconds),
            EndTime = active ? null : start.AddSeconds(seconds),
            IsActive = active,
            Zone = "Middle La Noscea",
            AreaKey = areaKey,
        };
        var stats = new CombatantStats { Id = 1, Name = "Alma Sylphe", JobId = 23 };
        stats.Damage = damage;
        stats.Hits = 2;
        stats.Crits = 1;
        stats.MaxHit = damage / 2;
        stats.MaxHitActionId = 7406;
        stats.Healing = 100;
        stats.DamageTaken = 50;
        stats.DamageAbilities[7406] = new AbilityStats { Total = damage, Hits = 2, Crits = 1, Max = damage / 2 };
        var combo = fight.TickKey([1200, 1201], false);
        stats.DamageAbilities[combo] = new AbilityStats { Total = 10, Hits = 1, Max = 10 };
        fight.Combatants[1] = stats;
        fight.Enemies[100] = new EnemyStats { Name = "Striking Dummy", DamageTaken = damage };
        fight.Deaths.Add(new DeathRecord(1, "Alma Sylphe", 23, 5, [new RecapEvent(0, RecapKind.Damage, "Boss", combo, 999, 0, false, false, false, false, 0, 1000)]));
        return fight;
    }

    [Fact]
    public void MergingSumsStatsAbilitiesDeathsAndDurations()
    {
        var first = MakeFight(T0, 60, 1000);
        var second = MakeFight(T0.AddMinutes(5), 30, 3000);
        second.StatusCombos.Clear();
        second.Combatants[1].DamageAbilities.Clear();
        var otherCombo = second.TickKey([1300, 1301], false);
        var sameCombo = second.TickKey([1200, 1201], false);
        second.Combatants[1].DamageAbilities[7406] = new AbilityStats { Total = 3000, Hits = 2, Crits = 1, Max = 2500 };
        second.Combatants[1].DamageAbilities[otherCombo] = new AbilityStats { Total = 7, Hits = 1, Max = 7 };
        second.Combatants[1].DamageAbilities[sameCombo] = new AbilityStats { Total = 5, Hits = 1, Max = 5 };
        second.Deaths[0] = second.Deaths[0] with { Events = [second.Deaths[0].Events[0] with { AbilityKey = sameCombo }] };
        Assert.NotEqual(otherCombo, sameCombo);

        var merged = AreaView.Merge([second, first], Key, "Middle La Noscea", T0.AddHours(1));

        Assert.Equal("Middle La Noscea", merged.Name);
        Assert.Equal(FightOutcome.Unknown, merged.Outcome);
        Assert.Equal(T0, merged.Start);
        Assert.Equal(TimeSpan.FromSeconds(90), merged.Duration(T0.AddHours(1)));

        var stats = merged.Combatants[1];
        Assert.Equal(4000, stats.Damage);
        Assert.Equal(4, stats.Hits);
        Assert.Equal(2, stats.Crits);
        Assert.Equal(200, stats.Healing);
        Assert.Equal(100, stats.DamageTaken);
        Assert.Equal(1500, stats.MaxHit);

        var hit = stats.DamageAbilities[7406];
        Assert.Equal(4000, hit.Total);
        Assert.Equal(4, hit.Hits);
        Assert.Equal(2500, hit.Max);

        var combo = merged.TickKey([1200, 1201], false);
        Assert.Equal(15, stats.DamageAbilities[combo].Total);
        Assert.Equal(7, stats.DamageAbilities[merged.TickKey([1300, 1301], false)].Total);

        Assert.Equal(2, merged.Deaths.Count);
        Assert.Equal([5, 65], merged.Deaths.Select(d => d.SecondsIntoFight));
        Assert.All(merged.Deaths, d => Assert.Equal(combo, d.Events[0].AbilityKey));

        var row = Assert.Single(FightView.Summarize(merged, T0.AddHours(1), mergePets: true).Rows);
        Assert.Equal(4000 / 90.0, row.Dps, 6);
    }

    [Fact]
    public void FightsWithoutTheAreaKeyAreExcluded()
    {
        var merged = AreaView.Merge(
            [MakeFight(T0, 60, 1000), MakeFight(T0.AddMinutes(2), 60, 5000, areaKey: null), MakeFight(T0.AddMinutes(4), 60, 7000, areaKey: "other")],
            Key, "Middle La Noscea", T0.AddHours(1));
        Assert.Equal(1000, merged.Combatants[1].Damage);
        Assert.Equal(TimeSpan.FromSeconds(60), merged.Duration(T0.AddHours(1)));

        Assert.Empty(AreaView.Merge([MakeFight(T0, 60, 1000, areaKey: null)], null, "Zone", T0).Combatants);
    }

    [Fact]
    public void LiveFightContributesItsRunningDuration()
    {
        var past = MakeFight(T0, 60, 1000);
        var live = MakeFight(T0.AddMinutes(5), 0, 500, active: true);
        var now = T0.AddMinutes(5).AddSeconds(20);

        var merged = AreaView.Merge([past, live], Key, "Middle La Noscea", now);

        Assert.Equal(TimeSpan.FromSeconds(80), merged.Duration(now));
        Assert.Equal(1500, merged.Combatants[1].Damage);
        Assert.Equal(1500 / 80.0, FightView.Summarize(merged, now, mergePets: true).Rows[0].Dps, 6);
    }

    private static FightIndexEntry Entry(DateTime start, string? key, double seconds = 60, string zone = "Middle La Noscea") =>
        new(Guid.NewGuid(), start, seconds, "Boss", zone, FightOutcome.Unknown, "Alma Sylphe", "Moogle", 23, 1000, 2000, false, key);

    [Fact]
    public void AreaDropdownGroupsByKeyWithinTheCurrentSessionAndCountsPulls()
    {
        var yesterday = Entry(T0.AddDays(-1), "a");
        var first = Entry(T0, "b", zone: "Eastern La Noscea");
        var second = Entry(T0.AddMinutes(5), "b", seconds: 30, zone: "Eastern La Noscea");
        var noKey = Entry(T0.AddMinutes(10), null);
        var third = Entry(T0.AddMinutes(20), "c", zone: "Western La Noscea");

        var areas = PlaySessions.DropdownAreas([yesterday, first, second, noKey, third], T0.AddMinutes(30), Gap);

        Assert.Equal(["c", "b"], areas.Select(a => a.Key));
        Assert.Equal(1, areas[0].Pulls);
        Assert.Equal("Western La Noscea", areas[0].Zone);
        Assert.Equal(2, areas[1].Pulls);
        Assert.Equal(90, areas[1].DurationSeconds);
        Assert.Equal(T0, areas[1].Start);
        Assert.Equal("Eastern La Noscea", areas[1].Zone);
    }

    [Fact]
    public void AreaIncludesShortPullsTheFightDropdownHides()
    {
        const int hideSeconds = 15;
        var longPull = MakeFight(T0, 60, 1000);
        var shortPull = MakeFight(T0.AddMinutes(2), 2, 300);
        var entries = new[] { FightIndexEntry.From(longPull), FightIndexEntry.From(shortPull) };
        var now = T0.AddMinutes(5);

        var fights = PlaySessions.DropdownFights(entries.Where(e => e.DurationSeconds >= hideSeconds), now, Gap);
        var area = Assert.Single(PlaySessions.DropdownAreas(entries, now, Gap));
        var merged = AreaView.Merge([longPull, shortPull], Key, "Middle La Noscea", now);

        Assert.Equal([longPull.Id], fights.Select(e => e.Id));
        Assert.Equal(2, area.Pulls);
        Assert.Contains(area.Fights, e => e.Id == shortPull.Id);
        Assert.Equal(62, area.DurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(62), merged.Duration(now));
        Assert.Equal(1300, merged.Combatants[1].Damage);
    }

    [Fact]
    public void AreaDropdownIsEmptyAfterALongBreak()
    {
        Assert.Empty(PlaySessions.DropdownAreas([Entry(T0, "a")], T0.AddDays(1), Gap));
    }
}
