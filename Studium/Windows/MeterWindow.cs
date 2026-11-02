using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Studium.Core;
using Studium.Core.Fights;
using Studium.Core.History;
using Studium.Ui;

namespace Studium.Windows;

public sealed class MeterWindow : Theme.ThemedWindow, IDisposable
{
    private const string WindowId = "###StudiumMeter";

    private static readonly (MeterTab Tab, string Label, string ShortLabel)[] Tabs =
        [(MeterTab.Dps, "DPS", "D"), (MeterTab.Tank, "Tank", "T"), (MeterTab.Heal, "Heal", "H")];

    private readonly Plugin plugin;
    private readonly IFontHandle headerFont;
    private readonly IFontHandle chipFont;
    private Fight? viewedFight;
    private (string Key, string Zone)? viewedArea;
    private Fight? areaFight;
    private (string Key, List<Guid> Ids, Fight Fight)? pastArea;
    private int areaPulls;
    private bool? appliedLock;
    private bool? appliedClickThrough;
    private Configuration Config => plugin.Configuration;

    public MeterWindow(Plugin plugin) : base(Plugin.DisplayName + WindowId, ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        headerFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
        chipFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis12));
        Size = new Vector2(560, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(160, 132) };

        // Without these Dalamud ignores IsPinned and IsClickthrough. They also add Dalamud's title-bar menu, which is
        // the way out of click-through.
        AllowPinning = true;
        AllowClickthrough = true;
    }

    public void Dispose()
    {
        headerFont.Dispose();
        chipFont.Dispose();
    }

    public bool AllowedByVisibility =>
        MeterVisibilityRules.ShouldShow(Config.Visibility, plugin.Fights.PartyInCombat, plugin.Fights.InDuty, plugin.Fights.Tracker.Current != null);

    public override bool DrawConditions() => AllowedByVisibility;

    public override void OnOpen() => SetOpenState(true);

    public override void OnClose() => SetOpenState(false);

    public override void PreDraw()
    {
        base.PreDraw();
        SyncLockState();
        var opacity = Configuration.ClampOpacity(Config.BackgroundOpacity);
        BgAlpha = opacity;

        var colours = ImGui.GetStyle().Colors;
        foreach (var col in TitleColours)
            ImGui.PushStyleColor(col, colours[(int)col] with { W = colours[(int)col].W * opacity });

        // ### keeps the window ID stable while the title changes.
        WindowName = Title() + WindowId;
    }

    private float lastWidth;

    private string Title()
    {
        if (!ClickThroughConfigured)
            return Plugin.DisplayName;
        var buttons = TitleBarButtons.Count + 3;
        var available = lastWidth - (buttons * ImGui.GetFrameHeight()) - (ImGui.GetStyle().FramePadding.X * 4);
        string[] titles =
        [
            $"{Plugin.DisplayName} (Click-through - Ctrl to interact)",
            $"{Plugin.DisplayName} (Ctrl to interact)",
            Plugin.DisplayName,
        ];
        return lastWidth <= 0 ? titles[^1] : titles.FirstOrDefault(t => ImGui.CalcTextSize(t).X <= available) ?? titles[^1];
    }

    private static readonly ImGuiCol[] TitleColours = [ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.TitleBgCollapsed];

    public override void PostDraw()
    {
        ImGui.PopStyleColor(TitleColours.Length);
        base.PostDraw();
    }

    public override void Draw()
    {
        lastWidth = ImGui.GetWindowWidth();
        Widgets.Shadow = true;
        try
        {
            DrawMeter();
        }
        finally
        {
            Widgets.Shadow = false;
        }
    }

    private void DrawMeter()
    {
        var tracker = plugin.Fights.Tracker;
        if (tracker.Current is { HeldAt: null })
        {
            viewedFight = null;
            viewedArea = null;
        }
        var now = DateTime.UtcNow;
        var areaMode = Config.MeterScope == MeterScope.Area;
        var fight = areaMode ? AreaFight(now) : viewedFight ?? tracker.Current ?? tracker.Last;
        var summary = fight != null ? FightView.Summarize(fight, now, Config.MergePets) : null;

        var title = areaMode ? AreaTitle(viewedArea?.Zone ?? plugin.Fights.CurrentZone) : null;
        var detail = areaMode
            ? areaPulls == 0 ? "No pulls" : PullsText(areaPulls)
            : fight != null ? fight.Zone : AreaTitle(plugin.Fights.CurrentZone);
        DrawHeader(fight, now, title, detail);

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y + 1;
        DrawTable(new Vector2(0, -footerHeight), fight, summary);
        DrawTabBar(summary);
    }

    private void SyncLockState()
    {
        var changedInMenu = false;
        if (appliedLock is { } lockApplied && IsPinned != lockApplied)
        {
            Config.LockMeter = IsPinned;
            changedInMenu = true;
        }
        if (appliedClickThrough is { } clickApplied && IsClickthrough != clickApplied)
        {
            Config.ClickThroughWhenLocked = IsClickthrough;
            if (IsClickthrough)
                Config.LockMeter = true;
            changedInMenu = true;
        }
        if (changedInMenu)
            Config.Save();

        IsPinned = Config.LockMeter;
        IsClickthrough = ClickThroughConfigured && !ImGui.GetIO().KeyCtrl;
        appliedLock = IsPinned;
        appliedClickThrough = IsClickthrough;
    }

    private bool ClickThroughConfigured => Config.LockMeter && Config.ClickThroughWhenLocked;

    private Fight? AreaFight(DateTime now)
    {
        var tracker = plugin.Fights.Tracker;
        var key = viewedArea?.Key ?? plugin.Fights.CurrentAreaKey;
        var zone = AreaTitle(viewedArea?.Zone ?? plugin.Fights.CurrentZone);
        var saved = CharacterEntries().Where(e => e.AreaKey == key).Select(e => e.Id).ToList();
        var live = viewedArea == null && tracker.Current is { } current && current.AreaKey == key && !saved.Contains(current.Id)
            ? current
            : null;
        areaPulls = saved.Count + (live != null ? 1 : 0);
        var past = PastArea(key, zone, saved, now);
        var merged = live == null ? past : AreaView.Merge(past == null ? [live] : [past, live], key, zone, now);
        if (areaFight != null && merged != null && merged != areaFight && areaFight.AreaKey == merged.AreaKey)
            plugin.DrillDownWindow.Replace(areaFight, merged);
        areaFight = merged;
        return merged;
    }

    private Fight? PastArea(string key, string zone, List<Guid> ids, DateTime now)
    {
        if (ids.Count == 0)
            return null;
        if (pastArea is { } cached && cached.Key == key && cached.Ids.SequenceEqual(ids))
            return cached.Fight;
        var fights = ids.Select(plugin.History.Open).OfType<Fight>().ToList();
        var merged = AreaView.Merge(fights, key, zone, now);
        pastArea = (key, ids, merged);
        return merged;
    }

    private static string AreaTitle(string zone) => string.IsNullOrEmpty(zone) ? "Area" : zone;

    private IEnumerable<FightIndexEntry> CharacterEntries()
    {
        var character = plugin.Fights.CurrentCharacterKey;
        return plugin.History.Entries.Where(e => character == null || HistoryFilter.CharacterKey(e) == character);
    }

    private IEnumerable<FightIndexEntry> DropdownCandidates() =>
        CharacterEntries().Where(e => e.DurationSeconds >= Config.HideShortFightsSeconds);

    private void DrawHeader(Fight? fight, DateTime now, string? title, string detail)
    {
        var style = ImGui.GetStyle();
        var rowTop = ImGui.GetCursorPosY();
        var lineHeight = ImGui.GetTextLineHeight();
        var timerText = fight != null ? Format.Duration(fight.Duration(now)) : "00:00";

        Vector2 timerSize;
        using (headerFont.Push())
            timerSize = ImGui.CalcTextSize(timerText);

        var outcome = fight is { IsActive: false } ? fight.Outcome : FightOutcome.Unknown;
        var chipText = outcome switch
        {
            FightOutcome.Clear => "Clear",
            FightOutcome.Wipe => "Wipe",
            _ => null,
        };
        float smallLine;
        using (chipFont.Push())
            smallLine = ImGui.GetTextLineHeight();
        var chipSlot = smallLine + 2;
        var chipHeight = chipText != null ? chipSlot : 0f;

        var buttonSize = ImGui.GetFrameHeight();
        var blockHeight = buttonSize + smallLine;
        var rowHeight = Math.Max(timerSize.Y + chipSlot, blockHeight);
        var leftHeight = timerSize.Y + chipHeight;
        var leftTop = rowTop + ((rowHeight - leftHeight) / 2);
        var timerX = ImGui.GetCursorPosX();

        ImGui.SetCursorPosY(leftTop);
        using (headerFont.Push())
            Widgets.Text(Theme.Bright, timerText);
        if (chipText != null)
        {
            ImGui.SetCursorPos(new Vector2(timerX, leftTop + timerSize.Y + 1));
            using (chipFont.Push())
                Widgets.Chip(chipText, outcome == FightOutcome.Clear ? Theme.Clear : Theme.Wipe);
        }

        // Placed by hand: SameLine would attach to the chip, not the timer.
        var textX = timerX + timerSize.X + 10;
        var blockTop = rowTop + ((rowHeight - blockHeight) / 2);
        var titleTop = blockTop + ((buttonSize - lineHeight) / 2);
        var historyX = ImGui.GetContentRegionMax().X - buttonSize;

        ImGui.SetCursorPos(new Vector2(textX, titleTop));
        var badgeWidth = DrawScopeBadge();
        ImGui.SetCursorPos(new Vector2(textX + badgeWidth, titleTop));
        DrawFightPicker(fight, Math.Max(historyX - 2 - textX - badgeWidth, 0), title);

        ImGui.SetCursorPos(new Vector2(historyX, blockTop));
        if (Widgets.FlatIconButton("##history", FontAwesomeIcon.History, "Fight history"))
            plugin.OpenHistory();

        var detailTop = blockTop + buttonSize;
        var settingsX = historyX + ((buttonSize - smallLine) / 2);
        using (chipFont.Push())
        {
            ImGui.SetCursorPos(new Vector2(textX, detailTop));
            Widgets.Text(Theme.Muted, Widgets.Truncate(detail, Math.Max(settingsX - style.ItemSpacing.X - textX, 0)));
        }
        ImGui.SetCursorPos(new Vector2(settingsX, detailTop));
        if (Widgets.FlatIconButton("##settings", FontAwesomeIcon.Cog, "Settings", smallLine))
            plugin.OpenSettings();

        ImGui.SetCursorPosY(rowTop + rowHeight + style.ItemSpacing.Y);
    }

    private float DrawScopeBadge()
    {
        const float gap = 6f;
        var area = Config.MeterScope == MeterScope.Area;
        var text = area ? "A" : "F";
        var padding = new Vector2(4, 0);
        var size = ImGui.CalcTextSize(text) + (padding * 2);
        var start = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##scope", size))
        {
            Config.MeterScope = area ? MeterScope.Fight : MeterScope.Area;
            Config.Save();
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(area ? "Area: every pull in this zone visit. Click for single fights." : "Fight: one pull. Click for the whole area.");
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + size, Theme.U32(Theme.Accent with { W = hovered ? 0.22f : 0.12f }), 3f);
        Widgets.DrawText(drawList, start + padding, Theme.U32(hovered ? Theme.Bright : Theme.Accent), text);
        return size.X + gap;
    }

    public void View(Fight fight)
    {
        viewedFight = fight;
        if (Config.MeterScope != MeterScope.Fight)
        {
            Config.MeterScope = MeterScope.Fight;
            Config.Save();
        }
        IsOpen = true;
    }

    public void Forget(Guid fightId)
    {
        if (viewedFight?.Id == fightId)
            viewedFight = null;
    }

    private void DrawFightPicker(Fight? shown, float maxWidth, string? title)
    {
        // Caret from the icon font; the default font has no arrow glyph.
        var caret = FontAwesomeIcon.CaretDown.ToIconString();
        float caretWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            caretWidth = ImGui.CalcTextSize(caret).X;
        const float spacing = 5f;
        var fullName = title ?? shown?.Name ?? "No fight yet";
        var showCaret = ImGui.CalcTextSize(fullName).X + spacing + caretWidth <= maxWidth;
        var name = Widgets.Truncate(fullName, showCaret ? maxWidth - spacing - caretWidth : maxWidth);
        var lineHeight = ImGui.GetTextLineHeight();

        var start = ImGui.GetCursorPos();
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.Hover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Theme.Hover);
        var clicked = ImGui.Selectable("##fightPicker", false, ImGuiSelectableFlags.None, new Vector2(Math.Max(maxWidth, 1), lineHeight));
        ImGui.PopStyleColor(2);
        if (clicked)
            ImGui.OpenPopup("##fights");
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip(title != null ? "Switch area" : "Switch fight");
        var end = ImGui.GetCursorPos();

        ImGui.SetCursorPos(start);
        Widgets.Text(Theme.Bright, name);
        if (showCaret)
        {
            ImGui.SetCursorPos(start with { X = start.X + maxWidth - caretWidth });
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
                Widgets.Text(hovered ? Theme.Text : Theme.Dim, caret);
        }
        ImGui.SetCursorPos(end);

        if (!ImGui.BeginPopup("##fights"))
            return;

        if (title != null)
            DrawAreaList();
        else
            DrawFightList(shown);
        ImGui.EndPopup();
    }

    private void DrawAreaList()
    {
        var now = DateTime.UtcNow;
        var tracker = plugin.Fights.Tracker;
        var currentKey = plugin.Fights.CurrentAreaKey;
        var areas = PlaySessions.DropdownAreas(CharacterEntries(), now, TimeSpan.FromHours(Config.SessionGapHours));
        var current = areas.FirstOrDefault(a => a.Key == currentKey);
        var live = tracker.Current is { } running && running.AreaKey == currentKey && current?.Fights.All(e => e.Id != running.Id) != false
            ? running
            : null;

        var lines = new List<(string? Key, string Zone, TimeSpan Duration, int Pulls)>
        {
            (null, AreaTitle(plugin.Fights.CurrentZone),
                TimeSpan.FromSeconds(current?.DurationSeconds ?? 0) + (live?.Duration(now) ?? TimeSpan.Zero),
                (current?.Pulls ?? 0) + (live != null ? 1 : 0)),
        };
        lines.AddRange(areas.Where(a => a.Key != currentKey)
            .Select(a => ((string?)a.Key, AreaTitle(a.Zone), TimeSpan.FromSeconds(a.DurationSeconds), a.Pulls)));

        var durationSlot = lines.Max(l => ImGui.CalcTextSize(Format.Duration(l.Duration)).X);
        var pullsSlot = lines.Max(l => ImGui.CalcTextSize(PullsText(l.Pulls)).X);
        foreach (var line in lines)
        {
            if (AreaItem(line.Key ?? "live", line.Duration, PullsText(line.Pulls), line.Zone, viewedArea?.Key == line.Key, durationSlot, pullsSlot))
                viewedArea = line.Key is { } key ? (key, line.Zone) : null;
        }
    }

    private static string PullsText(int pulls) => pulls == 1 ? "1 pull" : $"{pulls} pulls";

    private static bool AreaItem(string id, TimeSpan duration, string pulls, string zone, bool selected, float durationSlot, float pullsSlot)
    {
        const float gap = 8;
        var lineHeight = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable($"##area{id}", selected, ImGuiSelectableFlags.None, new Vector2(0, lineHeight));
        var after = ImGui.GetCursorPos();

        var durationText = Format.Duration(duration);
        ImGui.SetCursorPos(origin with { X = origin.X + durationSlot - ImGui.CalcTextSize(durationText).X });
        Widgets.Text(Theme.Muted, durationText);

        var pullsX = origin.X + durationSlot + gap;
        ImGui.SetCursorPos(origin with { X = pullsX + pullsSlot - ImGui.CalcTextSize(pulls).X });
        Widgets.Text(Theme.Dim, pulls);

        ImGui.SetCursorPos(origin with { X = pullsX + pullsSlot + gap });
        Widgets.Text(Theme.Text, zone);
        ImGui.SetCursorPos(after);
        return clicked;
    }

    private void DrawFightList(Fight? shown)
    {
        var tracker = plugin.Fights.Tracker;
        var fights = PlaySessions.DropdownFights(DropdownCandidates(), DateTime.UtcNow, TimeSpan.FromHours(Config.SessionGapHours));

        if (fights.Count == 0 && tracker.Current == null)
            ImGui.TextDisabled("No fights yet this session.");

        var liveDuration = tracker.Current?.Duration(DateTime.UtcNow);
        var durationSlot = fights.Select(e => Format.Duration(TimeSpan.FromSeconds(e.DurationSeconds)))
            .Append(liveDuration is { } d ? Format.Duration(d) : "00:00")
            .Max(t => ImGui.CalcTextSize(t).X);
        var chipSlot = new[] { "Live", "Clear", "Wipe" }.Max(t => ImGui.CalcTextSize(t).X) + 10;
        if (tracker.Current is { } live && FightItem("live", "Live", Theme.Accent, liveDuration!.Value, live.Name, shown == live, durationSlot, chipSlot))
            viewedFight = null;

        foreach (var entry in fights)
        {
            var (chip, colour) = entry.Outcome switch
            {
                FightOutcome.Clear => ("Clear", Theme.Clear),
                FightOutcome.Wipe => ("Wipe", Theme.Wipe),
                _ => ((string?)null, Theme.Dim),
            };
            if (FightItem(entry.Id.ToString(), chip, colour, TimeSpan.FromSeconds(entry.DurationSeconds), entry.Name, shown?.Id == entry.Id, durationSlot, chipSlot)
                && plugin.History.Open(entry.Id) is { } picked)
                viewedFight = picked == tracker.Last ? null : picked;
        }
    }

    private static bool FightItem(string id, string? chip, Vector4 chipColour, TimeSpan duration, string name, bool selected,
        float durationSlot, float chipSlot)
    {
        const float gap = 8;
        var lineHeight = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable($"##fight{id}", selected, ImGuiSelectableFlags.None, new Vector2(0, lineHeight));
        var after = ImGui.GetCursorPos();

        var durationText = Format.Duration(duration);
        ImGui.SetCursorPos(origin with { X = origin.X + durationSlot - ImGui.CalcTextSize(durationText).X });
        Widgets.Text(Theme.Muted, durationText);

        var chipX = origin.X + durationSlot + gap;
        ImGui.SetCursorPos(origin with { X = chip != null ? chipX : chipX + ((chipSlot - ImGui.CalcTextSize("—").X) / 2) });
        if (chip != null)
            Widgets.Chip(chip, chipColour);
        else
            Widgets.Text(Theme.Dim, "—");

        ImGui.SetCursorPos(origin with { X = chipX + chipSlot + gap });
        Widgets.Text(Theme.Text, name);
        ImGui.SetCursorPos(after);
        return clicked;
    }

    private sealed record Column(string Id, string Header, Func<CombatantRow, string> Value, Func<CombatantRow, string?>? Tooltip = null);

    private Column[] ColumnsFor(MeterTab tab) =>
    [
        new("name", "Name", r => r.Name),
        .. MeterColumns.Resolve(Config.MeterColumns.GetValueOrDefault(tab), tab).Select(c => new Column(c.Id, c.Header, c.Value,
            c.Id == "maxHit" ? r => plugin.Names.Action(r.MaxHitActionId) : null)),
    ];

    private static long MainMetric(CombatantRow row, MeterTab tab) => tab switch
    {
        MeterTab.Tank => row.DamageTaken,
        MeterTab.Heal => row.Healing,
        _ => row.Damage,
    };

    private Column[] FittingColumns(Column[] columns, IReadOnlyList<CombatantRow> rows, float available)
    {
        var padding = ImGui.GetStyle().CellPadding.X * 2;
        var widths = columns.Select((column, i) =>
        {
            var width = ImGui.CalcTextSize(column.Header).X;
            foreach (var row in rows)
            {
                var text = i == 0 ? NameFormatter.Format(row.Name, false, Config.NameDisplay, false) : column.Value(row);
                width = Math.Max(width, ImGui.CalcTextSize(text).X);
            }
            if (i == 0)
                width += ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.X;
            return width + padding;
        }).ToArray();

        var count = columns.Length;
        var total = widths.Sum();
        while (count > 2 && total > available)
        {
            count--;
            total -= widths[count];
        }
        return columns[..count];
    }

    private void DrawTable(Vector2 size, Fight? fight, FightSummary? summary)
    {
        var tableTopLeft = ImGui.GetCursorScreenPos();
        var tableSize = ImGui.GetContentRegionAvail() + size;
        var available = tableSize.X - ImGui.GetStyle().ScrollbarSize;
        var columns = FittingColumns(ColumnsFor(Config.MeterTab), summary?.Rows ?? [], available);

        // Column count in the ID makes ImGui re-measure when columns come and go.
        if (!ImGui.BeginTable($"##meter{columns.Length}", columns.Length, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, size))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < columns.Length; i++)
        {
            var flags = i == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed;
            ImGui.TableSetupColumn(columns[i].Header, flags);
        }
        var body = Widgets.TableBodyRect(Widgets.HeaderRow(columns.Select(c => c.Header).ToList(), tableTopLeft.X, tableSize.X));

        var localId = plugin.Fights.LocalPlayerId;
        if (fight != null && summary != null)
        {
            var tab = Config.MeterTab;
            var rows = summary.Rows.OrderByDescending(r => MainMetric(r, tab)).ToList();
            var top = rows.Count > 0 ? Math.Max(MainMetric(rows[0], tab), 1) : 1;
            var lineHeight = ImGui.GetTextLineHeight();
            var rowHeight = lineHeight + (ImGui.GetStyle().CellPadding.Y * 2);

            foreach (var row in rows)
            {
                var isSelf = row.Id == localId;
                var jobColour = Theme.Rgb(Jobs.Rgb(row.JobId));
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

                ImGui.TableNextColumn();
                DrawGauge(row, (double)MainMetric(row, tab) / top, tableTopLeft.X, tableSize.X, rowHeight, isSelf, body);

                var cellStart = ImGui.GetCursorPos();
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, jobColour with { W = 0.10f });
                ImGui.PushStyleColor(ImGuiCol.HeaderActive, jobColour with { W = 0.16f });
                var clicked = ImGui.Selectable($"##row{row.Id}", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, lineHeight));
                ImGui.PopStyleColor(2);
                (float Min, float Max)? deathsCell = null;
                ImGui.SetCursorPos(cellStart);

                DrawNameCell(row, isSelf, lineHeight);

                for (var i = 1; i < columns.Length; i++)
                {
                    ImGui.TableNextColumn();
                    if (columns[i].Id == "deaths")
                    {
                        var x = ImGui.GetCursorScreenPos().X;
                        deathsCell = (x, x + ImGui.GetContentRegionAvail().X);
                    }
                    Widgets.RightText(columns[i].Value(row), i == 1 ? Theme.Bright : Theme.Muted);
                    if (columns[i].Tooltip?.Invoke(row) is { Length: > 0 } tooltip && ImGui.IsItemHovered())
                        ImGui.SetTooltip(tooltip);
                    if (columns[i].Id == "deaths" && row.Deaths > 0 && ImGui.IsItemHovered())
                        ImGui.SetTooltip("Death recap");
                }

                if (clicked)
                {
                    var mouseX = ImGui.GetMousePos().X;
                    var onDeaths = row.Deaths > 0 && deathsCell is { } cell && mouseX >= cell.Min && mouseX <= cell.Max;
                    plugin.DrillDownWindow.Show(fight, row.Id, tab, deaths: onDeaths);
                }
            }
        }

        ImGui.EndTable();

        if (summary == null)
            DrawCentredText("Waiting for combat…", tableTopLeft, tableSize);
    }

    private void DrawGauge(CombatantRow row, double fraction, float tableLeft, float tableWidth, float rowHeight, bool isSelf, (Vector2 Min, Vector2 Max) body)
    {
        var rowTop = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().CellPadding.Y;
        var rowBottom = rowTop + rowHeight;
        var right = tableLeft + (float)(tableWidth * Math.Clamp(fraction, 0, 1));
        var rgb = Jobs.Rgb(row.JobId);

        Widgets.PushClip(new Vector2(tableLeft, rowTop), new Vector2(tableLeft + tableWidth, rowBottom), body);
        var drawList = ImGui.GetWindowDrawList();
        if (fraction > 0)
        {
            if (Config.GaugeStyle == GaugeStyle.Background)
                drawList.AddRectFilled(new Vector2(tableLeft, rowTop + 1), new Vector2(right, rowBottom - 1), ToImGuiColor(rgb, 0x38), 2f);
            else
                drawList.AddRectFilled(new Vector2(tableLeft, rowBottom - 2), new Vector2(right, rowBottom), ToImGuiColor(rgb, 0xFF), 1f);
        }
        if (isSelf)
            drawList.AddRectFilled(new Vector2(tableLeft, rowTop), new Vector2(tableLeft + 2, rowBottom), Theme.U32(Theme.Accent));
        ImGui.PopClipRect();
    }

    private void DrawNameCell(CombatantRow row, bool isSelf, float iconSize)
    {
        Widgets.GameIcon(Jobs.IconId(row.JobId), iconSize);
        ImGui.SameLine(0, 6);
        var name = row.JobId != 0 ? NameFormatter.Format(row.Name, isSelf, Config.NameDisplay, Config.YouForSelf) : row.Name;
        var colour = isSelf ? Theme.Bright : plugin.Fights.IsPartyMember(row.Id) ? Theme.Text : Theme.Muted;
        Widgets.Text(colour, name);
    }

    private static uint ToImGuiColor(uint rgb, byte alpha) =>
        ((uint)alpha << 24) | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static void DrawCentredText(string text, Vector2 topLeft, Vector2 area)
    {
        var textSize = ImGui.CalcTextSize(text);
        var position = topLeft + ((area - textSize) / 2);
        Widgets.DrawText(ImGui.GetWindowDrawList(), position, Theme.U32(Theme.Dim), text);
    }

    private const float TabGap = 12f;

    private void DrawTabBar(FightSummary? summary)
    {
        var drawList = ImGui.GetWindowDrawList();
        var lineY = ImGui.GetCursorScreenPos().Y;
        var left = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X;
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        drawList.AddLine(new Vector2(left, lineY), new Vector2(right, lineY), Theme.U32(Theme.Line));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5);

        var (shortLabels, raidParts) = FitFooter(summary);
        var lineStart = ImGui.GetCursorPos();
        var lineHeight = ImGui.GetTextLineHeight();

        foreach (var (tab, label, shortLabel) in Tabs)
        {
            var text = shortLabels ? shortLabel : label;
            var size = new Vector2(ImGui.CalcTextSize(text).X, lineHeight + 4);
            var start = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"##tab{label}", size) && Config.MeterTab != tab)
            {
                Config.MeterTab = tab;
                Config.Save();
            }
            var selected = Config.MeterTab == tab;
            var colour = selected || ImGui.IsItemHovered() ? Theme.Text : Theme.Dim;
            Widgets.DrawText(drawList, start, Theme.U32(colour), text);
            if (selected)
                drawList.AddRectFilled(new Vector2(start.X, start.Y + size.Y - 2), new Vector2(start.X + size.X, start.Y + size.Y), Theme.U32(Theme.Accent));
            ImGui.SameLine(0, TabGap);
        }

        if (raidParts.Count == 0)
        {
            ImGui.NewLine();
            return;
        }
        var width = raidParts.Sum(p => ImGui.CalcTextSize(p.Text).X);
        ImGui.SetCursorPos(new Vector2(ImGui.GetContentRegionMax().X - width, lineStart.Y));
        for (var i = 0; i < raidParts.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 0);
            Widgets.Text(raidParts[i].Bright ? Theme.Text : Theme.Muted, raidParts[i].Text);
        }
    }

    private readonly record struct TextPart(string Text, bool Bright);

    private (bool ShortLabels, IReadOnlyList<TextPart> Raid) FitFooter(FightSummary? summary)
    {
        var dps = summary?.RaidDps ?? 0;
        var hps = summary?.RaidHps ?? 0;
        var isHeal = Config.MeterTab == MeterTab.Heal;
        TextPart[] tabNumber = isHeal ? [new($"{hps:N0}", true)] : [new($"{dps:N0}", true)];
        TextPart[] compact = isHeal
            ? [new(Format.Compact((long)hps), true), new(" HPS", false)]
            : [new(Format.Compact((long)dps), true), new(" DPS", false)];

        (bool, TextPart[])[] candidates =
        [
            (false, [new("Total DPS: ", false), new($"{dps:N0}", true), new(" · HPS: ", false), new($"{hps:N0}", true)]),
            (false, [new(isHeal ? "Total HPS: " : "Total DPS: ", false), tabNumber[0]]),
            (false, compact),
            (true, compact),
            (true, []),
        ];

        var available = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X * 2;
        foreach (var (shortLabels, parts) in candidates)
        {
            var textWidth = parts.Length == 0 ? 0 : parts.Sum(p => ImGui.CalcTextSize(p.Text).X) + gap;
            if (TabsWidth(shortLabels) + textWidth <= available)
                return (shortLabels, parts);
        }
        return (true, []);
    }

    private static float TabsWidth(bool shortLabels) =>
        Tabs.Sum(t => ImGui.CalcTextSize(shortLabels ? t.ShortLabel : t.Label).X + TabGap);

    private void SetOpenState(bool open)
    {
        if (Config.MeterOpen == open)
            return;
        Config.MeterOpen = open;
        Config.Save();
    }
}
