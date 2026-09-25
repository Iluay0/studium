using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Studium.Combat;
using Studium.Core.Combat;
using Studium.Ui;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;

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
    private readonly GameNames names;
    private readonly PotencyTable potencies;
    private readonly IDataManager dataManager;

    private readonly LinkedList<CombatEvent> events = new();
    private readonly Dictionary<Type, int> eventCounts = new();
    private readonly Dictionary<uint, (int Count, ActorControlRecord Last)> actorControlCategories = new();
    private bool paused;
    private bool partyOnly = true;
    private uint potencyJob;
    private int potencyLevel;
    private bool potencyAllLevels;

    public DebugWindow(GameCombatEventSource source, IObjectTable objectTable, IPartyList partyList, GameNames names,
        PotencyTable potencies, IDataManager dataManager)
        : base("Studium – Combat debug###StudiumDebug")
    {
        this.source = source;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.names = names;
        this.potencies = potencies;
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
            if (ImGui.BeginTabItem("Potency"))
            {
                DrawPotency();
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

    /// <summary>
    /// Potencies read from tooltips, for one job at one level, to check against the game's own tooltips.
    /// Defaults to your current job and level.
    /// </summary>
    private void DrawPotency()
    {
        if (potencyJob == 0 && objectTable.LocalPlayer is { } me)
        {
            potencyJob = me.ClassJob.RowId;
            potencyLevel = me.Level;
        }

        var jobs = dataManager.GetExcelSheet<LuminaClassJob>()
            .Where(j => j.ClassJobCategory.RowId is 30 or 31) // Disciples of War / Magic
            .ToList();
        var current = jobs.FirstOrDefault(j => j.RowId == potencyJob);
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("Job", current.RowId == 0 ? "—" : current.Abbreviation.ExtractText()))
        {
            foreach (var job in jobs)
            {
                if (ImGui.Selectable($"{job.Abbreviation.ExtractText()} ({job.Name.ExtractText()})", job.RowId == potencyJob))
                    potencyJob = job.RowId;
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("Level", ref potencyLevel))
            potencyLevel = Math.Clamp(potencyLevel, 1, 100);
        ImGui.SameLine();
        if (ImGui.Button("Me") && objectTable.LocalPlayer is { } player)
        {
            potencyJob = player.ClassJob.RowId;
            potencyLevel = player.Level;
        }
        ImGui.SameLine();
        ImGui.Checkbox("Include actions above this level", ref potencyAllLevels);

        ImGui.TextUnformatted($"{potencies.Entries.Count} action tooltips with a potency, read at login in {potencies.LoadTime.TotalMilliseconds:0} ms. " +
                              "Fixed = usable for damage (healing) per potency. Hover a row for the tooltip as read.");
        if (current.RowId == 0)
            return;

        var abbreviation = current.Abbreviation.ExtractText();
        var rows = potencies.Entries
            .Where(e => e.Jobs.Split(' ').Contains(abbreviation) && (potencyAllLevels || e.Level <= potencyLevel))
            .OrderBy(e => e.Level)
            .ThenBy(e => e.ActionId);

        if (!ImGui.BeginTable("##potency", 8, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var header in new[] { "Level", "Action", "Potency", "DoT", "Heal", "HoT", "Fixed", "ID" })
            ImGui.TableSetupColumn(header);
        ImGui.TableHeadersRow();

        var iconSize = ImGui.GetTextLineHeight();
        foreach (var entry in rows)
        {
            var potency = potencies.Get(entry.ActionId, potencyJob, potencyLevel);
            ImGui.TableNextRow();
            Cell(entry.Level.ToString());
            ImGui.TableNextColumn();
            Widgets.GameIcon(names.ActionIcon(entry.ActionId), iconSize);
            ImGui.SameLine();
            ImGui.TextUnformatted(names.Action(entry.ActionId));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(TooltipPotency.Evaluate(entry.Macro, potencyJob, potencyLevel));
            Cell(potency.Potency?.ToString() ?? "");
            Cell(potency.DotPotency?.ToString() ?? "");
            Cell(potency.HealPotency?.ToString() ?? "");
            Cell(potency.HotPotency?.ToString() ?? "");
            Cell(potency.IsFixed || potency.IsHealFixed ? "yes" : "");
            Cell(entry.ActionId.ToString());
        }

        ImGui.EndTable();
    }

    private static string ArgText(uint value) => $"{value} (0x{value:X})";

    private (string Type, uint SourceId, uint OwnerId, uint TargetId, string Action, string Amount, string Flags) Describe(CombatEvent e) => e switch
    {
        ActionHitEvent hit => (hit.Kind.ToString(), hit.SourceId, hit.SourceOwnerId, hit.TargetId, ActionName(hit.ActionId),
            hit.Amount.ToString("N0"), $"{(hit.Crit ? "crit " : "")}{(hit.DirectHit ? "DH" : "")}"),
        PeriodicTickEvent tick => (tick.IsHeal ? "HoT" : "DoT", tick.SourceId, tick.SourceOwnerId, tick.TargetId,
            tick.Dots is { Count: > 0 } dots
                ? string.Join(" + ", dots.Select(d => $"{names.Status(d.StatusId).Name} ({source.Actors.NameOf(d.SourceId)}, {d.Potency})"))
                : string.Join(" + ", (tick.StatusIds ?? []).Select(id => $"{names.Status(id).Name} ({id})")),
            tick.Amount.ToString("N0"), ""),
        DeathEvent death => ("Death", death.SourceId, 0, death.TargetId, "", "", ""),
        CastStartEvent cast => ("Cast", cast.SourceId, 0, cast.TargetId, ActionName(cast.ActionId), "", $"{cast.CastTime:0.0}s"),
        _ => (e.GetType().Name, 0, 0, 0, "", "", ""),
    };

    private string Name(uint id, uint ownerId) =>
        id == 0 ? "" : ownerId != 0 ? $"{source.Actors.NameOf(id)} ({source.Actors.NameOf(ownerId)})" : source.Actors.NameOf(id);

    private string ActionName(uint actionId)
    {
        var name = names.Action(actionId);
        return string.IsNullOrEmpty(name) ? actionId.ToString() : $"{name} ({actionId})";
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
