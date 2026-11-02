using Dalamud.Plugin.Services;
using Studium.Core.Fights;
using Studium.Core.History;

namespace Studium.Combat;

public sealed class FightHistory : IDisposable
{
    private static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);

    private readonly Configuration config;
    private readonly FightTracker tracker;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly List<Fight> sessionFights = new();
    private readonly Dictionary<Guid, FightIndexEntry> sessionEntries = new();
    private DateTime nextRetention = DateTime.MinValue;
    private volatile bool entriesDirty = true;
    private IReadOnlyList<FightIndexEntry> entries = [];

    public FightStore Store { get; }
    public string Directory { get; }

    public IReadOnlyList<Fight> SessionFights => sessionFights;

    public FightHistory(Configuration config, FightTracker tracker, IFramework framework, IPluginLog log, string directory)
    {
        this.config = config;
        this.tracker = tracker;
        this.framework = framework;
        this.log = log;
        Directory = directory;
        Store = new FightStore(directory);

        tracker.FightEnded += OnFightEnded;
        framework.Update += OnFrameworkUpdate;
        Store.Changed += () => entriesDirty = true;
    }

    public void Dispose()
    {
        tracker.FightEnded -= OnFightEnded;
        framework.Update -= OnFrameworkUpdate;
    }

    private bool SaveToDisk => !config.AutoDeleteFights || config.RetentionHours > 0;

    private void OnFightEnded(Fight fight)
    {
        sessionFights.Add(fight);
        sessionEntries[fight.Id] = FightIndexEntry.From(fight);
        entriesDirty = true;
        if (SaveToDisk)
            RunInBackground("save fight", () => Store.Save(fight));
    }

    public IReadOnlyList<FightIndexEntry> Entries
    {
        get
        {
            if (!entriesDirty)
                return entries;
            entriesDirty = false;
            var merged = Store.Entries.ToDictionary(e => e.Id);
            foreach (var (id, entry) in sessionEntries)
                merged.TryAdd(id, entry);
            entries = merged.Values.OrderByDescending(e => e.Start).ToList();
            return entries;
        }
    }

    public Fight? Open(Guid id) => sessionFights.FirstOrDefault(f => f.Id == id) ?? Store.Load(id);

    public bool IsSaved(Guid id) => Store.Entries.Any(e => e.Id == id);

    public void Delete(Guid id)
    {
        sessionFights.RemoveAll(f => f.Id == id);
        sessionEntries.Remove(id);
        entriesDirty = true;
        RunInBackground("delete fight", () => Store.Delete(id));
    }

    public void SetPinned(Guid id, bool pinned) =>
        RunInBackground("pin fight", () => Store.SetPinned(id, pinned));

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextRetention)
            return;
        nextRetention = now + RetentionInterval;

        if (!config.AutoDeleteFights || config.RetentionHours <= 0)
            return;
        var keep = TimeSpan.FromHours(config.RetentionHours);
        RunInBackground("apply retention", () =>
        {
            var removed = Store.ApplyRetention(DateTime.UtcNow, keep);
            if (removed > 0)
                log.Information($"Retention removed {removed} fight(s) older than {keep.TotalHours:0} h");
        });
    }

    private void RunInBackground(string what, Action action) =>
        Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to {what}");
            }
        });
}
