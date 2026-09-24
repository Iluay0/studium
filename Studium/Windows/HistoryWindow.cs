using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Studium.Core;
using Studium.Core.Fights;
using Studium.Core.History;
using Studium.Ui;

namespace Studium.Windows;

/// <summary>Every kept fight, grouped by play session, with filters, pinning and deletion.</summary>
public sealed class HistoryWindow : Theme.ThemedWindow
{
    private const string DeletePopup = "Delete fight?##confirmDelete";

    private readonly Plugin plugin;
    private Guid? selectedId;
    private string? zoneFilter;
    private uint? jobFilter;
    private string? characterFilter;
    private bool clearsOnly;
    private int? minSeconds;

    private Configuration Config => plugin.Configuration;

    public HistoryWindow(Plugin plugin) : base("Studium – Fight history###StudiumHistory")
    {
        this.plugin = plugin;
        Size = new Vector2(780, 460);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var all = plugin.History.Entries;
        minSeconds ??= Config.HideShortFightsSeconds;

        DrawFilters(all);
        ImGui.Separator();

        var filter = new HistoryFilter(zoneFilter, jobFilter, characterFilter, clearsOnly, minSeconds.Value);
        var sessions = PlaySessions.Group(all.Where(filter.Matches), TimeSpan.FromHours(Config.SessionGapHours));

        // Footer: action buttons, then a line of help text.
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        DrawTable(sessions, new Vector2(0, -footerHeight));
        DrawActions(all);
    }

    /// <summary>Three rows: zone + clears, character + job, shortest fight.</summary>
    private void DrawFilters(IReadOnlyList<FightIndexEntry> all)
    {
        FilterCombo("Zone", 220, zoneFilter, all.Select(e => e.Zone).Where(z => z.Length > 0).Distinct().Order(), z => z, v => zoneFilter = v);
        ImGui.SameLine();
        ImGui.Checkbox("Clears only", ref clearsOnly);

        FilterCombo("Character", 220, characterFilter, all.Select(HistoryFilter.CharacterKey).Where(c => c.Length > 0).Distinct().Order(), c => c, v => characterFilter = v);
        ImGui.SameLine();
        FilterCombo<uint?>("Job", 100, jobFilter,
            all.Select(e => e.JobId).Where(j => j != 0).Distinct().OrderBy(Jobs.Abbreviation).Select(j => (uint?)j),
            j => Jobs.Abbreviation(j!.Value), v => jobFilter = v);

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Shortest fight:");
        ImGui.SameLine();
        var min = minSeconds!.Value;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("##minSeconds", ref min))
            minSeconds = Math.Clamp(min, 0, 3600);
        ImGui.SameLine();
        ImGui.TextUnformatted("s");
    }

    private static void FilterCombo<T>(string label, float width, T? current, IEnumerable<T> options, Func<T, string> text, Action<T?> set)
    {
        ImGui.SetNextItemWidth(width);
        if (!ImGui.BeginCombo($"##{label}", current is null ? $"{label}: all" : $"{label}: {text(current)}"))
            return;
        if (ImGui.Selectable("All", current is null))
            set(default);
        foreach (var option in options)
        {
            if (ImGui.Selectable(text(option), EqualityComparer<T?>.Default.Equals(current, option)))
                set(option);
        }
        ImGui.EndCombo();
    }

    private static readonly string[] Headers = ["", "Time", "Fight", "Zone", "Duration", "Result", "Job", "Character", "DPS"];

    /// <summary>
    /// One collapsible header per play session, each with its own table. The tables share an ID,
    /// so ImGui keeps their column widths in sync and the columns line up across sessions.
    /// </summary>
    private void DrawTable(IReadOnlyList<PlaySession> sessions, Vector2 size)
    {
        if (!ImGui.BeginChild("##sessions", size))
        {
            ImGui.EndChild();
            return;
        }

        if (sessions.Count == 0)
            ImGui.TextDisabled("No fights match.");

        for (var i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var start = session.Start.ToLocalTime();
            var end = session.End.ToLocalTime();
            var label = $"{start:ddd d MMM, HH:mm} → {end:HH:mm}   ·  {session.Fights.Count} fight{(session.Fights.Count == 1 ? "" : "s")}###{start.Ticks}";
            if (!ImGui.CollapsingHeader(label, i == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                continue;

            var tableLeft = ImGui.GetCursorScreenPos().X;
            var tableWidth = ImGui.GetContentRegionAvail().X;
            // Fixed-fit, not resizable: every column fits its header and content. No saved settings, so
            // widths remembered from older layouts can't clip headers.
            if (!ImGui.BeginTable("##sessionFights", Headers.Length, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
                continue;
            foreach (var header in Headers)
                ImGui.TableSetupColumn(header, header == "Fight" ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.None);
            Widgets.HeaderRow(Headers, tableLeft, tableWidth, rightAlignNumbers: false);

            foreach (var entry in session.Fights)
                DrawRow(entry);
            ImGui.EndTable();
        }

        ImGui.EndChild();
    }

    private void DrawRow(FightIndexEntry entry)
    {
        var lineHeight = ImGui.GetTextLineHeight();
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        // Full-row selectable; double-click opens the fight in the meter.
        var cellStart = ImGui.GetCursorPos();
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.Hover);
        ImGui.PushStyleColor(ImGuiCol.Header, Theme.Accent with { W = 0.14f });
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Theme.Accent with { W = 0.2f });
        if (ImGui.Selectable($"##{entry.Id}", selectedId == entry.Id,
                ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap | ImGuiSelectableFlags.AllowDoubleClick))
        {
            selectedId = entry.Id;
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                OpenInMeter(entry.Id);
        }
        ImGui.PopStyleColor(3);
        ImGui.SetCursorPos(cellStart);
        if (entry.Pinned)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                ImGui.TextColored(Theme.Accent, FontAwesomeIcon.Thumbtack.ToIconString());
        }

        Cell(entry.Start.ToLocalTime().ToString("HH:mm"), Theme.Muted);
        Cell(entry.Name, Theme.Text);
        Cell(entry.Zone, Theme.Muted);
        ImGui.TableNextColumn();
        Widgets.RightText(Format.Duration(TimeSpan.FromSeconds(entry.DurationSeconds)), Theme.Muted);
        ImGui.TableNextColumn();
        Widgets.OutcomeChip(entry.Outcome);
        ImGui.TableNextColumn();
        Widgets.GameIcon(Jobs.IconId(entry.JobId), lineHeight);
        ImGui.SameLine(0, 5);
        ImGui.TextColored(Theme.Muted, Jobs.Abbreviation(entry.JobId));
        Cell(entry.CharacterName, Theme.Muted);
        ImGui.TableNextColumn();
        Widgets.RightText(entry.LocalDps.ToString("N0"), Theme.Bright);
    }

    private void DrawActions(IReadOnlyList<FightIndexEntry> all)
    {
        var selected = selectedId is { } id ? all.FirstOrDefault(e => e.Id == id) : null;

        using (Disabled(selected == null))
        {
            if (ImGui.Button("Open in meter") && selected != null)
                OpenInMeter(selected.Id);

            ImGui.SameLine();
            var saved = selected != null && plugin.History.IsSaved(selected.Id);
            using (Disabled(!saved))
            {
                if (ImGui.Button(selected?.Pinned == true ? "Unpin" : "Pin") && selected != null)
                    plugin.History.SetPinned(selected.Id, !selected.Pinned);
            }
            if (!saved && selected != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Only saved fights can be pinned (retention is set to this session only).");

            ImGui.SameLine();
            if (ImGui.Button("Delete") && selected != null)
                ImGui.OpenPopup(DeletePopup);
        }

        ImGui.TextColored(Theme.Dim, "Pinned fights are never deleted by retention. Double-click a fight to open it.");

        var popupOpen = true;
        if (ImGui.BeginPopupModal(DeletePopup, ref popupOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(selected != null
                ? $"Delete {selected.Name} ({selected.Start.ToLocalTime():ddd d MMM, HH:mm})? This can't be undone."
                : "Delete this fight?");
            if (ImGui.Button("Delete") && selected != null)
            {
                plugin.History.Delete(selected.Id);
                plugin.MeterWindow.Forget(selected.Id);
                selectedId = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void OpenInMeter(Guid id)
    {
        if (plugin.History.Open(id) is { } fight)
            plugin.MeterWindow.View(fight);
    }

    private static void Cell(string text, System.Numerics.Vector4 colour)
    {
        ImGui.TableNextColumn();
        ImGui.TextColored(colour, text);
    }

    private static DisabledScope Disabled(bool disabled) => new(disabled);

    private readonly struct DisabledScope : IDisposable
    {
        public DisabledScope(bool disabled) => ImGui.BeginDisabled(disabled);

        public void Dispose() => ImGui.EndDisabled();
    }
}
