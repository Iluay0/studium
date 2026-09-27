using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaStatus = Lumina.Excel.Sheets.Status;
using LuminaTerritory = Lumina.Excel.Sheets.TerritoryType;

namespace Studium.Combat;

public sealed class GameNames
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, string> actions = new();
    private readonly Dictionary<uint, string> zones = new();
    private readonly Dictionary<uint, uint> actionIcons = new();
    private readonly Dictionary<uint, StatusInfo> statuses = new();

    public readonly record struct StatusInfo(string Name, uint Icon, bool IsDot, bool IsHot, bool IsPartyDebuff, bool IsNoise, byte MaxStacks);

    private const uint WellFed = 48;
    private const uint Medicated = 49;

    public GameNames(IDataManager dataManager) => this.dataManager = dataManager;

    public string Action(uint actionId)
    {
        if (actions.TryGetValue(actionId, out var name))
            return name;
        var row = dataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId);
        name = row is { } r ? r.Name.ExtractText() : string.Empty;
        actions[actionId] = name;
        return name;
    }

    public uint ActionIcon(uint actionId)
    {
        if (actionIcons.TryGetValue(actionId, out var icon))
            return icon;
        icon = dataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId) is { } row ? row.Icon : 0u;
        actionIcons[actionId] = icon;
        return icon;
    }

    public uint StatusIcon(uint statusId, byte stacks)
    {
        var info = Status(statusId);
        return info.MaxStacks > 1 && stacks > 1 ? info.Icon + (uint)Math.Min(stacks, info.MaxStacks) - 1 : info.Icon;
    }

    // The sheet has no DoT flag. PartyListPriority 10 on a debuff means DoT, 5 on a buff means HoT.
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
