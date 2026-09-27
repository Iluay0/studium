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
            viewedFight = null;
        var fight = viewedFight ?? tracker.Current ?? tracker.Last;
        var now = DateTime.UtcNow;
        var summary = fight != null ? FightView.Summarize(fight, now, Config.MergePets) : null;

        DrawHeader(fight, now);

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

    private void DrawHeader(Fight? fight, DateTime now)
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
        var chipHeight = 0f;
        if (chipText != null)
        {
            using (chipFont.Push())
                chipHeight = ImGui.GetTextLineHeight() + 2;
        }

        var textBlockHeight = lineHeight * 2;
        var leftHeight = timerSize.Y + chipHeight;
        var rowHeight = Math.Max(leftHeight, textBlockHeight);
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
        var buttonSize = ImGui.GetFrameHeight();
        var buttonsX = ImGui.GetContentRegionMax().X - (buttonSize * 2) - 2;
        var textWidth = buttonsX - textX - style.ItemSpacing.X;

        var showName = textWidth >= 40;
        var showZone = showName && !string.IsNullOrEmpty(fight?.Zone);
        var blockHeight = showZone ? textBlockHeight : lineHeight;
        var blockTop = rowTop + ((rowHeight - blockHeight) / 2);

        if (showName)
        {
            ImGui.SetCursorPos(new Vector2(textX, blockTop));
            DrawFightPicker(fight, textWidth);
        }
        if (showZone)
        {
            ImGui.SetCursorPos(new Vector2(textX, blockTop + lineHeight));
            Widgets.Text(Theme.Muted, Widgets.Truncate(fight!.Zone, textWidth));
        }

        ImGui.SetCursorPos(new Vector2(buttonsX, rowTop + ((rowHeight - buttonSize) / 2)));
        if (Widgets.FlatIconButton("##history", FontAwesomeIcon.History, "Fight history"))
            plugin.OpenHistory();
        ImGui.SameLine(0, 2);
        if (Widgets.FlatIconButton("##settings", FontAwesomeIcon.Cog, "Settings"))
            plugin.OpenSettings();

        ImGui.SetCursorPosY(rowTop + rowHeight + style.ItemSpacing.Y);
    }

    public void View(Fight fight)
    {
        viewedFight = fight;
        IsOpen = true;
    }

    public void Forget(Guid fightId)
    {
        if (viewedFight?.Id == fightId)
            viewedFight = null;
    }

    private void DrawFightPicker(Fight? shown, float maxWidth)
    {
        // Caret from the icon font; the default font has no arrow glyph.
        var caret = FontAwesomeIcon.CaretDown.ToIconString();
        float caretWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            caretWidth = ImGui.CalcTextSize(caret).X;
        const float spacing = 5f;
        var name = Widgets.Truncate(shown?.Name ?? "No fight yet", Math.Max(maxWidth - spacing - caretWidth, 0));
        var nameWidth = ImGui.CalcTextSize(name).X;
        var lineHeight = ImGui.GetTextLineHeight();

        var start = ImGui.GetCursorPos();
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.Hover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Theme.Hover);
        var clicked = ImGui.Selectable("##fightPicker", false, ImGuiSelectableFlags.None, new Vector2(nameWidth + spacing + caretWidth, lineHeight));
        ImGui.PopStyleColor(2);
        if (clicked)
            ImGui.OpenPopup("##fights");
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip("Switch fight");
        var end = ImGui.GetCursorPos();

        ImGui.SetCursorPos(start);
        Widgets.Text(Theme.Bright, name);
        ImGui.SameLine(0, spacing);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            Widgets.Text(hovered ? Theme.Text : Theme.Dim, caret);
        ImGui.SetCursorPos(end);

        if (!ImGui.BeginPopup("##fights"))
            return;

        var tracker = plugin.Fights.Tracker;
        var character = plugin.Fights.CurrentCharacterKey;
        var candidates = plugin.History.Entries.Where(e =>
            (character == null || HistoryFilter.CharacterKey(e) == character)
            && e.DurationSeconds >= Config.HideShortFightsSeconds);
        var fights = PlaySessions.DropdownFights(candidates, DateTime.UtcNow, TimeSpan.FromHours(Config.SessionGapHours));

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

        ImGui.EndPopup();
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
