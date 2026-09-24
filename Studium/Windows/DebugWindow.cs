using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Studium.Combat;
using Studium.Core.Combat;
using LuminaAction = Lumina.Excel.Sheets.Action;

namespace Studium.Windows;

/// <summary>
/// Developer view of raw combat events, used to verify the hooks in-game before the meter consumes them.
/// </summary>
public sealed class DebugWindow : Window, IDisposable
{
    private const int MaxEvents = 1000;

    private readonly GameCombatEventSource source;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IDataManager dataManager;

    private readonly LinkedList<CombatEvent> events = new();
    private readonly Dictionary<Type, int> eventCounts = new();
    private readonly Dictionary<uint, (int Count, ActorControlRecord Last)> actorControlCategories = new();
    private readonly Dictionary<uint, string> actionNames = new();
    private bool paused;
    private bool partyOnly = true;

    public DebugWindow(GameCombatEventSource source, IObjectTable objectTable, IPartyList partyList, IDataManager dataManager)
        : base("Studium – Combat debug###StudiumDebug")
    {
        this.source = source;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.dataManager = dataManager;
        Size = new Vector2(820, 480);
        SizeCondition = ImGuiCond.FirstUseEver;

        source.EventReceived += OnEvent;
        source.ActorControlReceived += OnActorControl;
    }

    public void Dispose()
    {
        source.EventReceived -= OnEvent;
        source.ActorControlReceived -= OnActorControl;
    }

    private void OnEvent(CombatEvent e)
    {
        eventCounts[e.GetType()] = eventCounts.GetValueOrDefault(e.GetType()) + 1;
        if (paused)
            return;
        events.AddFirst(e);
        if (events.Count > MaxEvents)
            events.RemoveLast();
    }

    private void OnActorControl(ActorControlRecord record)
    {
        if (paused)
            return;
        var count = actorControlCategories.TryGetValue(record.Category, out var existing) ? existing.Count : 0;
        actorControlCategories[record.Category] = (count + 1, record);
    }

    public override void Draw()
    {
        DrawStatus();
        ImGui.Separator();

        if (ImGui.BeginTabBar("##debugTabs"))
        {
            if (ImGui.BeginTabItem("Events"))
            {
                DrawEvents();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Raw ActorControl"))
            {
                DrawActorControl();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    private void DrawStatus()
    {
        foreach (var hook in new[] { "ActionEffect", "ActorControl", "ActorCast" })
        {
            var failed = source.HookErrors.TryGetValue(hook, out var error);
            ImGui.TextColored(failed ? new Vector4(1, 0.4f, 0.4f, 1) : new Vector4(0.4f, 1, 0.4f, 1), $"{hook}: {(failed ? "FAILED" : "ok")}");
            if (failed && ImGui.IsItemHovered())
                ImGui.SetTooltip(error);
            ImGui.SameLine();
        }
        ImGui.NewLine();

        ImGui.TextUnformatted(
            $"Seen — hits: {Count<ActionHitEvent>()}  ticks: {Count<PeriodicTickEvent>()}  " +
            $"deaths: {Count<DeathEvent>()}  casts: {Count<CastStartEvent>()}");

        ImGui.Checkbox("Pause", ref paused);
        ImGui.SameLine();
        ImGui.Checkbox("Only me, my party and our pets", ref partyOnly);
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            events.Clear();
            eventCounts.Clear();
            actorControlCategories.Clear();
        }
    }

    private int Count<T>() => eventCounts.GetValueOrDefault(typeof(T));

    private void DrawEvents()
    {
        var party = partyOnly ? PartyIds() : null;

        if (!ImGui.BeginTable("##events", 7, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time");
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Source");
        ImGui.TableSetupColumn("Target");
        ImGui.TableSetupColumn("Action");
        ImGui.TableSetupColumn("Amount");
        ImGui.TableSetupColumn("Flags");
        ImGui.TableHeadersRow();

        foreach (var e in events)
        {
            var row = Describe(e);
            if (party != null && !party.Contains(row.SourceId) && !party.Contains(row.OwnerId) && !party.Contains(row.TargetId))
                continue;

            ImGui.TableNextRow();
            Cell(e.Time.ToLocalTime().ToString("HH:mm:ss.ff"));
            Cell(row.Type);
            Cell(Name(row.SourceId, row.OwnerId));
            Cell(Name(row.TargetId, 0));
            Cell(row.Action);
            Cell(row.Amount);
            Cell(row.Flags);
        }

        ImGui.EndTable();
    }

    private void DrawActorControl()
    {
        ImGui.TextWrapped("Every ActorControl category seen, with the last packet's arguments. " +
                          "0x06 = death, 0x604 = HoT tick, 0x605 = DoT tick (arg2 = amount, arg3 = source).");

        if (!ImGui.BeginTable("##ac", 7, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var header in new[] { "Category", "Count", "Entity", "Arg1", "Arg2", "Arg3", "Arg4" })
            ImGui.TableSetupColumn(header);
        ImGui.TableHeadersRow();

        foreach (var (category, (count, last)) in actorControlCategories.OrderBy(kv => kv.Key))
        {
            ImGui.TableNextRow();
            Cell($"0x{category:X2}");
            Cell(count.ToString());
            Cell(source.Actors.NameOf(last.EntityId));
            Cell(ArgText(last.Arg1));
            Cell(ArgText(last.Arg2));
            Cell(ArgText(last.Arg3));
            Cell(ArgText(last.Arg4));
        }

        ImGui.EndTable();
    }

    private static string ArgText(uint value) => $"{value} (0x{value:X})";

    private (string Type, uint SourceId, uint OwnerId, uint TargetId, string Action, string Amount, string Flags) Describe(CombatEvent e) => e switch
    {
        ActionHitEvent hit => (hit.Kind.ToString(), hit.SourceId, hit.SourceOwnerId, hit.TargetId, ActionName(hit.ActionId),
            hit.Amount.ToString("N0"), $"{(hit.Crit ? "crit " : "")}{(hit.DirectHit ? "DH" : "")}"),
        PeriodicTickEvent tick => (tick.IsHeal ? "HoT" : "DoT", tick.SourceId, tick.SourceOwnerId, tick.TargetId, "", tick.Amount.ToString("N0"), ""),
        DeathEvent death => ("Death", death.SourceId, 0, death.TargetId, "", "", ""),
        CastStartEvent cast => ("Cast", cast.SourceId, 0, cast.TargetId, ActionName(cast.ActionId), "", $"{cast.CastTime:0.0}s"),
        _ => (e.GetType().Name, 0, 0, 0, "", "", ""),
    };

    private string Name(uint id, uint ownerId) =>
        id == 0 ? "" : ownerId != 0 ? $"{source.Actors.NameOf(id)} ({source.Actors.NameOf(ownerId)})" : source.Actors.NameOf(id);

    private string ActionName(uint actionId)
    {
        if (actionNames.TryGetValue(actionId, out var name))
            return name;
        var row = dataManager.GetExcelSheet<LuminaAction>().GetRowOrDefault(actionId);
        name = row is { } r && !r.Name.IsEmpty ? $"{r.Name.ExtractText()} ({actionId})" : actionId.ToString();
        actionNames[actionId] = name;
        return name;
    }

    private HashSet<uint> PartyIds()
    {
        var ids = new HashSet<uint>();
        if (objectTable.LocalPlayer is { } me)
            ids.Add(me.EntityId);
        foreach (var member in partyList)
            ids.Add(member.EntityId);
        return ids;
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }
}
