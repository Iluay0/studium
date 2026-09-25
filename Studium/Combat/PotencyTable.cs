using System.Diagnostics;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Studium.Core.Combat;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaActionTransient = Lumina.Excel.Sheets.ActionTransient;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace Studium.Combat;

/// <summary>
/// Potencies read from action tooltips. The tooltips are read once per session at login (English, whatever the
/// client language, so one parser covers every client); each job / level is then evaluated on demand and cached.
/// </summary>
public sealed class PotencyTable : IDisposable
{
    /// <param name="Name">English name, to match DoT statuses to their actions (Stormbite's DoT is "Stormbite").</param>
    /// <param name="Jobs">The action's ClassJobCategory name, e.g. "CNJ WHM" or "SGE": who can use it.</param>
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

    /// <summary>The action's potencies for a player of that job and level; default when its tooltip names none.</summary>
    public ActionPotency Get(uint actionId, uint jobId, int level)
    {
        if (!entries.TryGetValue(actionId, out var entry))
            return default;
        if (!evaluated.TryGetValue((actionId, jobId, level), out var potency))
            evaluated[(actionId, jobId, level)] = potency = TooltipPotency.Read(entry.Macro, jobId, level);
        return potency;
    }

    /// <summary>
    /// The potency every hit (or heal) of this action lands at for that job and level, or null when it depends on
    /// the situation.
    /// </summary>
    public int? FixedPotency(uint actionId, uint jobId, int level, bool heal)
    {
        var potency = Get(actionId, jobId, level);
        return heal
            ? potency is { IsHealFixed: true, HealPotency: > 0 } ? potency.HealPotency : null
            : potency is { IsFixed: true, Potency: > 0 } ? potency.Potency : null;
    }

    /// <summary>
    /// The action behind a DoT (or HoT) status and its per-tick potency for that job and level. These statuses are
    /// named after their action; (0, 0) when none matches, (action, 0) when its tooltip names no such potency.
    /// </summary>
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
            // Replacement actions (Higanbana, Eukrasian Dosis) aren't flagged as player actions, so go by category.
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
