using System.Diagnostics;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Studium.Core.Combat;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaActionTransient = Lumina.Excel.Sheets.ActionTransient;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace Studium.Combat;

// Tooltips are read in English whatever the client language, so one parser does.
public sealed class PotencyTable : IDisposable
{
    public sealed record Entry(uint ActionId, string Name, byte Level, string Jobs, string Macro);

    private const uint Spell = 2;
    private const uint Weaponskill = 3;
    private const uint Ability = 4;

    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, Entry> entries = new();
    private readonly Dictionary<(uint Action, uint Job, int Level), ActionPotency> evaluated = new();
    private readonly Dictionary<uint, uint[]> statusActions = new();

    public IReadOnlyCollection<Entry> Entries => entries.Values;
    public TimeSpan LoadTime { get; private set; }

    public PotencyTable(IDataManager dataManager, IClientState clientState, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.log = log;
        clientState.Login += Load;
        if (clientState.IsLoggedIn)
            Load();
    }

    public void Dispose() => clientState.Login -= Load;

    public ActionPotency Get(uint actionId, uint jobId, int level)
    {
        if (!entries.TryGetValue(actionId, out var entry))
            return default;
        if (!evaluated.TryGetValue((actionId, jobId, level), out var potency))
            evaluated[(actionId, jobId, level)] = potency = TooltipPotency.Read(entry.Macro, jobId, level);
        return potency;
    }

    public int? FixedPotency(uint actionId, uint jobId, int level, bool heal)
    {
        var potency = Get(actionId, jobId, level);
        return heal
            ? potency is { IsHealFixed: true, HealPotency: > 0 } ? potency.HealPotency : null
            : potency is { IsFixed: true, Potency: > 0 } ? potency.Potency : null;
    }

    public (uint ActionId, int Potency) OverTime(uint statusId, uint jobId, int level, bool heal)
    {
        if (!statusActions.TryGetValue(statusId, out var candidates))
        {
            var name = dataManager.GetExcelSheet<LuminaStatus>(ClientLanguage.English).GetRowOrDefault(statusId)?.Name.ExtractText();
            candidates = string.IsNullOrEmpty(name) ? [] : entries.Values.Where(e => e.Name == name).Select(e => e.ActionId).ToArray();
            statusActions[statusId] = candidates;
        }

        foreach (var actionId in candidates)
        {
            var potency = Get(actionId, jobId, level);
            if ((heal ? potency.HotPotency : potency.DotPotency) is { } perTick)
                return (actionId, perTick);
        }
        return (candidates.FirstOrDefault(), 0);
    }

    private void Load()
    {
        var watch = Stopwatch.StartNew();
        entries.Clear();
        evaluated.Clear();
        statusActions.Clear();

        var transient = dataManager.GetExcelSheet<LuminaActionTransient>(ClientLanguage.English);
        foreach (var action in dataManager.GetExcelSheet<LuminaAction>(ClientLanguage.English))
        {
            // Replacement actions (Higanbana, Eukrasian Dosis) aren't flagged as player actions; use the category.
            if (action.IsPvP || action.ClassJobLevel == 0 || action.ClassJobCategory.RowId == 0
                || action.ActionCategory.RowId is not (Spell or Weaponskill or Ability))
                continue;
            if (transient.GetRowOrDefault(action.RowId) is not { } row)
                continue;
            var macro = row.Description.ToMacroString();
            if (!macro.Contains("otency", StringComparison.Ordinal))
                continue;
            var jobs = action.ClassJobCategory.ValueNullable?.Name.ExtractText() ?? string.Empty;
            entries[action.RowId] = new Entry(action.RowId, action.Name.ExtractText(), action.ClassJobLevel, jobs, macro);
        }

        LoadTime = watch.Elapsed;
        log.Debug($"Read {entries.Count} action tooltips with a potency in {LoadTime.TotalMilliseconds:0} ms");
    }
}
