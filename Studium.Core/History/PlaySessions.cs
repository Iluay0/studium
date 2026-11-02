using Studium.Core.Fights;

namespace Studium.Core.History;

public sealed record PlaySession(DateTime Start, DateTime End, IReadOnlyList<FightIndexEntry> Fights);

public sealed record AreaEntry(string Key, string Zone, DateTime Start, double DurationSeconds, IReadOnlyList<FightIndexEntry> Fights)
{
    public int Pulls => Fights.Count;
}

public static class PlaySessions
{
    public const int DropdownMaximum = 30;

    public static DateTime EndOf(FightIndexEntry entry) => entry.Start.AddSeconds(entry.DurationSeconds);

    public static IReadOnlyList<PlaySession> Group(IEnumerable<FightIndexEntry> entries, TimeSpan gap)
    {
        var sessions = new List<PlaySession>();
        var current = new List<FightIndexEntry>();
        DateTime? previousEnd = null;

        foreach (var entry in entries.OrderBy(e => e.Start))
        {
            if (previousEnd is { } end && entry.Start - end >= gap)
            {
                sessions.Add(Make(current));
                current = new List<FightIndexEntry>();
            }
            current.Add(entry);
            previousEnd = previousEnd is { } p && p > EndOf(entry) ? p : EndOf(entry);
        }
        if (current.Count > 0)
            sessions.Add(Make(current));

        sessions.Reverse();
        return sessions;
    }

    public static IReadOnlyList<FightIndexEntry> DropdownFights(IEnumerable<FightIndexEntry> entries, DateTime now, TimeSpan gap) =>
        CurrentSession(entries, now, gap).Take(DropdownMaximum).ToList();

    public static IReadOnlyList<AreaEntry> DropdownAreas(IEnumerable<FightIndexEntry> entries, DateTime now, TimeSpan gap) =>
        CurrentSession(entries, now, gap)
            .Where(e => !string.IsNullOrEmpty(e.AreaKey))
            .GroupBy(e => e.AreaKey!)
            .Select(g => new AreaEntry(g.Key, g.First().Zone, g.Min(e => e.Start), g.Sum(e => e.DurationSeconds), g.ToList()))
            .Take(DropdownMaximum)
            .ToList();

    private static IReadOnlyList<FightIndexEntry> CurrentSession(IEnumerable<FightIndexEntry> entries, DateTime now, TimeSpan gap)
    {
        var sessions = Group(entries, gap);
        return sessions.Count == 0 || now - sessions[0].End >= gap ? [] : sessions[0].Fights;
    }

    private static PlaySession Make(List<FightIndexEntry> fights) =>
        new(fights.Min(f => f.Start), fights.Max(EndOf), fights.OrderByDescending(f => f.Start).ToList());
}

public sealed record HistoryFilter(string? Zone = null, uint? JobId = null, string? Character = null, bool ClearsOnly = false, double MinSeconds = 0)
{
    public bool Matches(FightIndexEntry entry) =>
        (Zone == null || entry.Zone == Zone)
        && (JobId == null || entry.JobId == JobId)
        && (Character == null || CharacterKey(entry) == Character)
        && (!ClearsOnly || entry.Outcome == FightOutcome.Clear)
        && entry.DurationSeconds >= MinSeconds;

    public static string CharacterKey(FightIndexEntry entry) =>
        string.IsNullOrEmpty(entry.World) ? entry.CharacterName : $"{entry.CharacterName} @ {entry.World}";
}
