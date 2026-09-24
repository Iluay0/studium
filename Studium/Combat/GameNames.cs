using Dalamud.Plugin.Services;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaTerritory = Lumina.Excel.Sheets.TerritoryType;

namespace Studium.Combat;

/// <summary>Cached lookups of game sheet names (actions, zones).</summary>
public sealed class GameNames
{
    private readonly IDataManager dataManager;
    private readonly Dictionary<uint, string> actions = new();
    private readonly Dictionary<uint, string> zones = new();

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
