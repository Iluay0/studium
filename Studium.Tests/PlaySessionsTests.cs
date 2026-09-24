using Studium.Core.Fights;
using Studium.Core.History;

namespace Studium.Tests;

public class PlaySessionsTests
{
    private static readonly DateTime Night = new(2026, 9, 23, 21, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromHours(4);

    private static FightIndexEntry Entry(DateTime start, double seconds = 300, string zone = "M4S", uint job = 42,
        string character = "Iluay Dory", FightOutcome outcome = FightOutcome.Wipe) =>
        new(Guid.NewGuid(), start, seconds, "Boss", zone, outcome, character, "Moogle", job, 1000, 8000, false);

    [Fact]
    public void RaidNightAcrossMidnightIsOneSession()
    {
        var entries = Enumerable.Range(0, 10).Select(i => Entry(Night.AddMinutes(i * 25))).ToList(); // 21:00 → 00:45
        var sessions = PlaySessions.Group(entries, Gap);
        var session = Assert.Single(sessions);
        Assert.Equal(10, session.Fights.Count);
        Assert.True(session.Fights[0].Start > session.Fights[^1].Start); // newest first
    }

    [Fact]
    public void GapOfFourHoursStartsNewSession()
    {
        var yesterday = Entry(Night.AddDays(-1));
        var today = Entry(Night);
        var sessions = PlaySessions.Group([yesterday, today], Gap);
        Assert.Equal(2, sessions.Count);
        Assert.Same(today, sessions[0].Fights[0]);
    }

    [Fact]
    public void DropdownShowsWholeCurrentSessionUpToCap()
    {
        var older = Enumerable.Range(0, 20).Select(i => Entry(Night.AddDays(-1).AddMinutes(i * 10)));
        var tonight = Enumerable.Range(0, 40).Select(i => Entry(Night.AddMinutes(i * 6))).ToList();
        var dropdown = PlaySessions.DropdownFights(older.Concat(tonight), Night.AddHours(4.5), Gap);
        Assert.Equal(PlaySessions.DropdownMaximum, dropdown.Count);
        Assert.All(dropdown, e => Assert.Contains(e, tonight));
    }

    [Fact]
    public void ShortSessionIsToppedUpWithEarlierFights()
    {
        var older = Enumerable.Range(0, 20).Select(i => Entry(Night.AddDays(-1).AddMinutes(i * 10))).ToList();
        var tonight = new[] { Entry(Night), Entry(Night.AddMinutes(10)) };
        var dropdown = PlaySessions.DropdownFights(older.Concat(tonight), Night.AddMinutes(20), Gap);
        Assert.Equal(PlaySessions.DropdownMinimum, dropdown.Count);
        Assert.Equal(tonight[1], dropdown[0]);
    }

    [Fact]
    public void AfterLongBreakDropdownShowsRecentFights()
    {
        var older = Enumerable.Range(0, 20).Select(i => Entry(Night.AddMinutes(i * 10))).ToList();
        var dropdown = PlaySessions.DropdownFights(older, Night.AddDays(2), Gap);
        Assert.Equal(PlaySessions.DropdownMinimum, dropdown.Count);
    }

    [Fact]
    public void Filters()
    {
        var entry = Entry(Night, seconds: 20, zone: "M4S", job: 42, outcome: FightOutcome.Clear);
        Assert.True(new HistoryFilter().Matches(entry));
        Assert.True(new HistoryFilter(Zone: "M4S", JobId: 42, Character: "Iluay Dory @ Moogle", ClearsOnly: true, MinSeconds: 15).Matches(entry));
        Assert.False(new HistoryFilter(Zone: "M3S").Matches(entry));
        Assert.False(new HistoryFilter(JobId: 23).Matches(entry));
        Assert.False(new HistoryFilter(Character: "Alt Char @ Moogle").Matches(entry));
        Assert.False(new HistoryFilter(MinSeconds: 30).Matches(entry));
        Assert.False(new HistoryFilter(ClearsOnly: true).Matches(entry with { Outcome = FightOutcome.Wipe }));
    }
}
