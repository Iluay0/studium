using System.IO.Compression;
using System.Text.Json;
using Studium.Core.Fights;

namespace Studium.Core.History;

/// <summary>What the history lists need about a saved fight, without loading the fight itself.</summary>
public sealed record FightIndexEntry(
    Guid Id,
    DateTime Start,
    double DurationSeconds,
    string Name,
    string Zone,
    FightOutcome Outcome,
    string CharacterName,
    string World,
    uint JobId,
    double LocalDps,
    double RaidDps,
    bool Pinned)
{
    public static FightIndexEntry From(Fight fight, bool pinned = false)
    {
        var end = fight.EndTime ?? fight.LastActivity;
        var summary = FightView.Summarize(fight, end, mergePets: true);
        var me = summary.Rows.FirstOrDefault(r => r.Id == fight.LocalPlayerId);
        return new FightIndexEntry(
            fight.Id, fight.Start, fight.FinalDuration.TotalSeconds, fight.Name, fight.Zone, fight.Outcome,
            fight.CharacterName, fight.World, me?.JobId ?? 0, me?.Dps ?? 0, summary.RaidDps, pinned);
    }
}

/// <summary>
/// Saved fights on disk: one gzipped JSON file per fight, plus an index loaded at startup so lists
/// are instant; a fight's full data is only read when opened. Thread-safe (saves run off the game thread).
/// </summary>
public sealed class FightStore
{
    private const string IndexFileName = "index.json";
    private const string FightFileSuffix = ".json.gz";

    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = false };

    private sealed record FightFile(FightIndexEntry Entry, Fight Fight);

    private readonly string directory;
    private readonly object sync = new();
    private readonly Dictionary<Guid, FightIndexEntry> entries = new();

    public FightStore(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
        LoadIndex();
    }

    /// <summary>Raised after any change (save, delete, pin, retention), possibly off the game thread.</summary>
    public event Action? Changed;

    /// <summary>All saved fights, newest first.</summary>
    public IReadOnlyList<FightIndexEntry> Entries
    {
        get
        {
            lock (sync)
                return entries.Values.OrderByDescending(e => e.Start).ToList();
        }
    }

    public void Save(Fight fight)
    {
        lock (sync)
        {
            var pinned = entries.TryGetValue(fight.Id, out var existing) && existing.Pinned;
            var entry = FightIndexEntry.From(fight, pinned);
            WriteFightFile(new FightFile(entry, fight));
            entries[fight.Id] = entry;
            WriteIndex();
        }
        Changed?.Invoke();
    }

    public Fight? Load(Guid id)
    {
        lock (sync)
            return ReadFightFile(PathFor(id))?.Fight;
    }

    public bool Delete(Guid id)
    {
        lock (sync)
        {
            if (!entries.Remove(id))
                return false;
            File.Delete(PathFor(id));
            WriteIndex();
        }
        Changed?.Invoke();
        return true;
    }

    public void SetPinned(Guid id, bool pinned)
    {
        lock (sync)
        {
            if (!entries.TryGetValue(id, out var entry) || entry.Pinned == pinned)
                return;
            entry = entry with { Pinned = pinned };
            entries[id] = entry;
            // Keep the pin in the fight file too, so an index rebuild doesn't lose it.
            if (ReadFightFile(PathFor(id)) is { } file)
                WriteFightFile(file with { Entry = entry });
            WriteIndex();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes unpinned fights that started before <paramref name="now"/> minus <paramref name="keep"/>. Returns how many.</summary>
    public int ApplyRetention(DateTime now, TimeSpan keep)
    {
        var cutoff = now - keep;
        int removed;
        lock (sync)
        {
            var expired = entries.Values.Where(e => !e.Pinned && e.Start < cutoff).Select(e => e.Id).ToList();
            foreach (var id in expired)
            {
                entries.Remove(id);
                File.Delete(PathFor(id));
            }
            removed = expired.Count;
            if (removed > 0)
                WriteIndex();
        }
        if (removed > 0)
            Changed?.Invoke();
        return removed;
    }

    private string PathFor(Guid id) => Path.Combine(directory, id.ToString("N") + FightFileSuffix);

    private void LoadIndex()
    {
        var indexPath = Path.Combine(directory, IndexFileName);
        try
        {
            if (File.Exists(indexPath))
            {
                var loaded = JsonSerializer.Deserialize<List<FightIndexEntry>>(File.ReadAllText(indexPath), JsonOptions);
                if (loaded != null)
                {
                    foreach (var entry in loaded.Where(e => File.Exists(PathFor(e.Id))))
                        entries[entry.Id] = entry;
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt index: fall through and rebuild it from the fight files.
        }

        RebuildIndex();
    }

    private void RebuildIndex()
    {
        entries.Clear();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + FightFileSuffix))
        {
            if (ReadFightFile(path) is { } file)
                entries[file.Entry.Id] = file.Entry;
        }
        WriteIndex();
    }

    private void WriteIndex()
    {
        var indexPath = Path.Combine(directory, IndexFileName);
        var tempPath = indexPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(entries.Values.ToList(), JsonOptions));
        File.Move(tempPath, indexPath, overwrite: true);
    }

    private void WriteFightFile(FightFile file)
    {
        var path = PathFor(file.Entry.Id);
        var tempPath = path + ".tmp";
        using (var stream = File.Create(tempPath))
        using (var gzip = new GZipStream(stream, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gzip, file, JsonOptions);
        File.Move(tempPath, path, overwrite: true);
    }

    private static FightFile? ReadFightFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<FightFile>(gzip, JsonOptions);
        }
        catch (Exception)
        {
            return null; // missing or corrupt: treat as not there
        }
    }
}
