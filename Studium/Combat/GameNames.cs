using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;
using LuminaTerritory = Lumina.Excel.Sheets.TerritoryType;

namespace Studium.Combat;

/// <summary>Cached lookups of game sheet names (actions, zones).</summary>
public sealed class GameNames
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, string> actions = new();
    private readonly Dictionary<uint, string> zones = new();
    private readonly Dictionary<uint, uint> actionIcons = new();
    private readonly Dictionary<uint, StatusInfo> statuses = new();

    /// <param name="IsPartyDebuff">A harmful status players put on enemies that isn't a DoT (Reprisal, Addle, Feint...).</param>
    /// <param name="IsNoise">Never worth showing in a death recap: permanent (stances), FC buffs, food, potions.</param>
    public readonly record struct StatusInfo(string Name, uint Icon, bool IsDot, bool IsHot, bool IsPartyDebuff, bool IsNoise, byte MaxStacks);

    private const uint WellFed = 48;
    private const uint Medicated = 49;

    public GameNames(IDataManager dataManager) => this.dataManager = dataManager;

    /// <summary>Action name, or empty when unknown.</summary>
    public string Action(uint actionId)
    {
        if (actions.TryGetValue(actionId, out var name))
            return name;
        var row = dataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId);
        name = row is { } r ? r.Name.ExtractText() : string.Empty;
        actions[actionId] = name;
        return name;
    }

    /// <summary>The action's icon ID, or 0 when it has none.</summary>
    public uint ActionIcon(uint actionId)
    {
        if (actionIcons.TryGetValue(actionId, out var icon))
            return icon;
        icon = dataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId) is { } row ? row.Icon : 0u;
        actionIcons[actionId] = icon;
        return icon;
    }

    /// <summary>The icon for a status at a stack count: stacked statuses have one icon per stack, in sequence.</summary>
    public uint StatusIcon(uint statusId, byte stacks)
    {
        var info = Status(statusId);
        return info.MaxStacks > 1 && stacks > 1 ? info.Icon + (uint)Math.Min(stacks, info.MaxStacks) - 1 : info.Icon;
    }

    /// <summary>
    /// Status name, icon and kind. The sheet has no DoT flag; its PartyListPriority groups them instead:
    /// 10 on harmful statuses marks damage over time (Dia, Biolysis, bleeds; debuffs like Chain Stratagem are 50),
    /// 5 on helpful ones marks healing over time (Regen, Medica III, Physis II).
    /// </summary>
    public StatusInfo Status(uint statusId)
    {
        if (statuses.TryGetValue(statusId, out var info))
            return info;
        info = dataManager.GetExcelSheet<LuminaStatus>().GetRowOrDefault(statusId) is { } row
            ? new StatusInfo(row.Name.ExtractText(), row.Icon,
                IsDot: row.StatusCategory == 2 && row.PartyListPriority == 10,
                IsHot: row.StatusCategory == 1 && row.PartyListPriority == 5,
                IsPartyDebuff: row.StatusCategory == 2 && row.PartyListPriority == 50,
                IsNoise: row.IsPermanent || row.IsFcBuff || statusId is WellFed or Medicated || row.Name.IsEmpty,
                row.MaxStacks)
            : default;
        statuses[statusId] = info;
        return info;
    }

    public string Zone(uint territoryId)
    {
        if (zones.TryGetValue(territoryId, out var name))
            return name;
        var row = dataManager.GetExcelSheet<LuminaTerritory>().GetRowOrDefault(territoryId);
        name = row is { } r && r.PlaceName.ValueNullable is { } place ? place.Name.ExtractText() : string.Empty;
        zones[territoryId] = name;
        return name;
    }
}
