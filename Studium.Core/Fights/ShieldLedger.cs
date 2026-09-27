namespace Studium.Core.Fights;

// When shields overlap, the oldest is assumed to go first.
public sealed class ShieldLedger
{
    private sealed class Entry(uint statusId, uint sourceId, long remaining)
    {
        public uint StatusId { get; } = statusId;
        public uint SourceId { get; } = sourceId;
        public long Remaining { get; set; } = remaining;
    }

    private readonly Dictionary<uint, List<Entry>> shields = new();

    public void Clear() => shields.Clear();

    public void Gained(uint targetId, uint statusId, uint sourceId, long amount)
    {
        if (amount <= 0)
            return;
        if (!shields.TryGetValue(targetId, out var list))
        {
            list = new List<Entry>();
            shields[targetId] = list;
        }
        list.Add(new Entry(statusId, sourceId, amount));
    }

    public IReadOnlyList<(uint StatusId, uint SourceId, long Absorbed)> Absorb(uint targetId, long amount)
    {
        if (!shields.TryGetValue(targetId, out var list))
            return [];

        var credited = new List<(uint, uint, long)>();
        while (amount > 0 && list.Count > 0)
        {
            var oldest = list[0];
            var part = Math.Min(oldest.Remaining, amount);
            credited.Add((oldest.StatusId, oldest.SourceId, part));
            oldest.Remaining -= part;
            amount -= part;
            if (oldest.Remaining <= 0)
                list.RemoveAt(0);
        }
        return credited;
    }

    public void Expire(uint targetId, long amount)
    {
        if (shields.TryGetValue(targetId, out var list))
            Trim(list, list.Sum(e => e.Remaining) - amount);
    }

    private static void Trim(List<Entry> list, long actual)
    {
        var excess = list.Sum(e => e.Remaining) - Math.Max(actual, 0);
        while (excess > 0 && list.Count > 0)
        {
            var oldest = list[0];
            var drop = Math.Min(oldest.Remaining, excess);
            oldest.Remaining -= drop;
            excess -= drop;
            if (oldest.Remaining <= 0)
                list.RemoveAt(0);
        }
    }
}
