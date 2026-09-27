using Studium.Core.Fights;

namespace Studium.Core.History;

public sealed record PlaySession(DateTime Start, DateTime End, IReadOnlyList<FightIndexEntry> Fights);

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

    public static IReadOnlyList<FightIndexEntry> DropdownFights(IEnumerable<FightIndexEntry> entries, DateTime now, TimeSpan gap)
    {
        var sessions = Group(entries, gap);
        if (sessions.Count == 0 || now - sessions[0].End >= gap)
            return [];
        return sessions[0].Fights.Take(DropdownMaximum).ToList();
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
