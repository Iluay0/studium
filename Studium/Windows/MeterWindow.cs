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
    /// <summary>A past fight picked from the dropdown or history; null shows the live/last fight.</summary>
    private Fight? viewedFight;
    private bool? appliedLock;
    private bool? appliedClickThrough;
    private Configuration Config => plugin.Configuration;

    public MeterWindow(Plugin plugin) : base(Plugin.DisplayName + WindowId, ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        headerFont = Plugin.PluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis18));
        Size = new Vector2(560, 260);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(160, 132) };

        // Dalamud only honours IsPinned/IsClickthrough while these are on. They also add Dalamud's
        // title-bar menu (☰), which is the escape hatch out of click-through; PreDraw syncs it with our config.
        AllowPinning = true;
        AllowClickthrough = true;
    }

    public void Dispose() => headerFont.Dispose();

    /// <summary>Whether the "Show meter" setting currently allows the meter (it stays open, just not drawn).</summary>
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

        // The title bar keeps the player's Dalamud colours but follows the meter's opacity.
        var colours = ImGui.GetStyle().Colors;
        foreach (var col in TitleColours)
            ImGui.PushStyleColor(col, colours[(int)col] with { W = colours[(int)col].W * opacity });

        // ### keeps the window ID (position, size) stable while the visible title changes.
        WindowName = Plugin.DisplayName + (ClickThroughConfigured ? " (Click-through - Ctrl to interact)" : "") + WindowId;
    }

    private static readonly ImGuiCol[] TitleColours = [ImGuiCol.TitleBg, ImGuiCol.TitleBgActive, ImGuiCol.TitleBgCollapsed];

    public override void PostDraw()
    {
        ImGui.PopStyleColor(TitleColours.Length);
        base.PostDraw();
    }

    public override void Draw()
    {
        // Text shadows keep the meter readable over the game when its background is see-through.
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
        // A running fight takes over the meter. While it's on hold (out of combat, may still resume),
        // a past fight can be picked; if combat resumes, the meter goes back to live.
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

    /// <summary>
    /// Keeps our lock/click-through settings and Dalamud's pin/click-through state in step,
    /// in whichever direction changed last (our settings window or Dalamud's ☰ menu).
    /// </summary>
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
        // Holding Ctrl temporarily makes a click-through meter interactive.
        IsClickthrough = ClickThroughConfigured && !ImGui.GetIO().KeyCtrl;
        appliedLock = IsPinned;
        appliedClickThrough = IsClickthrough;
    }

    private bool ClickThroughConfigured => Config.LockMeter && Config.ClickThroughWhenLocked;

    /// <summary>
    /// Timer on the left; fight name (the fight picker, with outcome chip) and zone stacked beside it;
    /// flat icon buttons on the right. As space runs out the zone goes first, then the fight name.
    /// </summary>
    private void DrawHeader(Fight? fight, DateTime now)
    {
        var style = ImGui.GetStyle();
        var rowTop = ImGui.GetCursorPosY();
        var lineHeight = ImGui.GetTextLineHeight();
        var timerText = fight != null ? Format.Duration(fight.Duration(now)) : "00:00";

        Vector2 timerSize;
        using (headerFont.Push())
            timerSize = ImGui.CalcTextSize(timerText);
        var textBlockHeight = lineHeight * 2;
        var rowHeight = Math.Max(timerSize.Y, textBlockHeight);

        ImGui.SetCursorPosY(rowTop + ((rowHeight - timerSize.Y) / 2));
        using (headerFont.Push())
            Widgets.Text(Theme.Bright, timerText);

        ImGui.SameLine(0, 10);
        var textX = ImGui.GetCursorPosX();
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

    /// <summary>Shows a past fight in the meter (from the history browser).</summary>
    public void View(Fight fight)
    {
        viewedFight = fight;
        IsOpen = true;
    }

    /// <summary>Stops showing a fight that was just deleted.</summary>
    public void Forget(Guid fightId)
    {
        if (viewedFight?.Id == fightId)
            viewedFight = null;
    }

    /// <summary>The fight name doubles as the fight dropdown: this play session's fights for this character.</summary>
    private void DrawFightPicker(Fight? shown, float maxWidth)
    {
        // Name, outcome chip and a caret icon, as one clickable area. The caret comes from the icon
        // font: the default font has no reliable arrow glyph.
        var caret = FontAwesomeIcon.CaretDown.ToIconString();
        float caretWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            caretWidth = ImGui.CalcTextSize(caret).X;
        const float spacing = 5f;
        var outcome = shown is { IsActive: false } ? shown.Outcome : FightOutcome.Unknown;
        var chipText = outcome switch
        {
            FightOutcome.Clear => "Clear",
            FightOutcome.Wipe => "Wipe",
            _ => null,
        };
        var chipWidth = chipText != null ? ImGui.CalcTextSize(chipText).X + 10 + spacing : 0;
        var name = Widgets.Truncate(shown?.Name ?? "No fight yet", Math.Max(maxWidth - chipWidth - spacing - caretWidth, 0));
        var nameWidth = ImGui.CalcTextSize(name).X;
        var lineHeight = ImGui.GetTextLineHeight();

        var start = ImGui.GetCursorPos();
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Theme.Hover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Theme.Hover);
        var clicked = ImGui.Selectable("##fightPicker", false, ImGuiSelectableFlags.None, new Vector2(nameWidth + chipWidth + spacing + caretWidth, lineHeight));
        ImGui.PopStyleColor(2);
        if (clicked)
            ImGui.OpenPopup("##fights");
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetTooltip("Switch fight");
        var end = ImGui.GetCursorPos();

        ImGui.SetCursorPos(start);
        Widgets.Text(Theme.Bright, name);
        if (chipText != null)
        {
            ImGui.SameLine(0, spacing);
            Widgets.Chip(chipText, outcome == FightOutcome.Clear ? Theme.Clear : Theme.Wipe);
        }
        ImGui.SameLine(0, spacing);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            Widgets.Text(hovered ? Theme.Text : Theme.Dim, caret);
        ImGui.SetCursorPos(end);

        if (!ImGui.BeginPopup("##fights"))
            return;

        var tracker = plugin.Fights.Tracker;
        if (tracker.Current is { } live && ImGui.Selectable($"LIVE  {live.Name}  {Format.Duration(live.Duration(DateTime.UtcNow))}", shown == live))
            viewedFight = null;

        var character = plugin.Fights.CurrentCharacterKey;
        var candidates = plugin.History.Entries.Where(e =>
            (character == null || HistoryFilter.CharacterKey(e) == character)
            && e.DurationSeconds >= Config.HideShortFightsSeconds);
        var fights = PlaySessions.DropdownFights(candidates, DateTime.UtcNow, TimeSpan.FromHours(Config.SessionGapHours));

        if (fights.Count == 0 && tracker.Current == null)
            ImGui.TextDisabled("No fights yet this session.");

        foreach (var entry in fights)
        {
            var entryOutcome = entry.Outcome switch
            {
                FightOutcome.Clear => "  clear",
                FightOutcome.Wipe => "  wipe",
                _ => string.Empty,
            };
            var text = $"{entry.Start.ToLocalTime():HH:mm}  {entry.Name}  {Format.Duration(TimeSpan.FromSeconds(entry.DurationSeconds))}{entryOutcome}##{entry.Id}";
            if (ImGui.Selectable(text, shown?.Id == entry.Id) && plugin.History.Open(entry.Id) is { } picked)
                viewedFight = picked == tracker.Last ? null : picked;
        }

        ImGui.EndPopup();
    }

    private sealed record Column(string Header, Func<CombatantRow, string> Value, Func<CombatantRow, string?>? Tooltip = null);

    private Column[] ColumnsFor(MeterTab tab) => tab switch
    {
        MeterTab.Tank =>
        [
            new("Name", r => r.Name),
            new("Taken", r => Format.Compact(r.DamageTaken)),
            new("T%", r => Format.Percent(r.DamageTakenShare)),
            new("Parry", r => Format.Percent(r.ParryRate)),
            new("Block", r => Format.Percent(r.BlockRate)),
            new("Healed-on", r => Format.Compact(r.HealingReceived)),
            new("Deaths", r => r.Deaths.ToString()),
        ],
        MeterTab.Heal =>
        [
            new("Name", r => r.Name),
            new("HPS", r => r.Hps.ToString("N0")),
            new("H%", r => Format.Percent(r.HealingShare)),
            new("Total", r => Format.Compact(r.Healing)),
            new("Overheal", r => Format.Percent(r.OverhealRate)),
            new("Crit", r => Format.Percent(r.HealCritRate)),
            new("Deaths", r => r.Deaths.ToString()),
        ],
        _ =>
        [
            new("Name", r => r.Name),
            new("DPS", r => r.Dps.ToString("N0")),
            new("D%", r => Format.Percent(r.DamageShare)),
            new("Total", r => Format.Compact(r.Damage)),
            new("Crit", r => Format.Percent(r.CritRate)),
            new("DH", r => Format.Percent(r.DirectHitRate)),
            new("Max hit", r => r.MaxHit.ToString("N0"), r => plugin.Names.Action(r.MaxHitActionId)),
            new("Deaths", r => r.Deaths.ToString()),
        ],
    };

    /// <summary>The value each tab sorts by and sizes its gauges against.</summary>
    private static long MainMetric(CombatantRow row, MeterTab tab) => tab switch
    {
        MeterTab.Tank => row.DamageTaken,
        MeterTab.Heal => row.Healing,
        _ => row.Damage,
    };

    /// <summary>
    /// Drops columns from the right until the table fits the window. Name and the tab's main
    /// number (DPS / HPS / Taken) always stay.
    /// </summary>
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
                width += ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.X; // job icon
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
        var tableSize = ImGui.GetContentRegionAvail() + size; // size.Y is negative: space kept for the footer
        var available = tableSize.X - ImGui.GetStyle().ScrollbarSize;
        var columns = FittingColumns(ColumnsFor(Config.MeterTab), summary?.Rows ?? [], available);

        // The ID includes the column count so ImGui re-measures widths when a column drops or returns.
        if (!ImGui.BeginTable($"##meter{columns.Length}", columns.Length, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, size))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < columns.Length; i++)
        {
            var flags = i == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed;
            ImGui.TableSetupColumn(columns[i].Header, flags);
        }
        Widgets.HeaderRow(columns.Select(c => c.Header).ToList(), tableTopLeft.X, tableSize.X);

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
                DrawGauge(row, (double)MainMetric(row, tab) / top, tableTopLeft.X, tableSize.X, rowHeight, isSelf);

                // Invisible full-row selectable: a faint job-coloured hover, and a click opens the breakdown.
                var cellStart = ImGui.GetCursorPos();
                ImGui.PushStyleColor(ImGuiCol.HeaderHovered, jobColour with { W = 0.10f });
                ImGui.PushStyleColor(ImGuiCol.HeaderActive, jobColour with { W = 0.16f });
                if (ImGui.Selectable($"##row{row.Id}", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, lineHeight)))
                    plugin.DrillDownWindow.Show(fight, row.Id, tab);
                ImGui.PopStyleColor(2);
                ImGui.SetCursorPos(cellStart);

                DrawNameCell(row, isSelf, lineHeight);

                for (var i = 1; i < columns.Length; i++)
                {
                    ImGui.TableNextColumn();
                    // The tab's main number (first after the name) is bright; the rest are muted.
                    Widgets.RightText(columns[i].Value(row), i == 1 ? Theme.Bright : Theme.Muted);
                    if (columns[i].Tooltip?.Invoke(row) is { Length: > 0 } tooltip && ImGui.IsItemHovered())
                        ImGui.SetTooltip(tooltip);
                }
            }
        }

        ImGui.EndTable();

        // Centred over the whole table, not inside a column.
        if (summary == null)
            DrawCentredText("Waiting for combat…", tableTopLeft, tableSize);
    }

    /// <summary>
    /// Job-coloured bar across the whole row, drawn from the first cell so later columns' text sits on top.
    /// Its length is this row's share of the tab's top value. Your own row also gets an accent edge.
    /// </summary>
    private void DrawGauge(CombatantRow row, double fraction, float tableLeft, float tableWidth, float rowHeight, bool isSelf)
    {
        var rowTop = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().CellPadding.Y;
        var rowBottom = rowTop + rowHeight;
        var right = tableLeft + (float)(tableWidth * Math.Clamp(fraction, 0, 1));
        var rgb = Jobs.Rgb(row.JobId);

        ImGui.PushClipRect(new Vector2(tableLeft, rowTop), new Vector2(tableLeft + tableWidth, rowBottom), false);
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
        Widgets.GameIcon(Jobs.IconId(row.JobId), iconSize); // pets and NPCs get an empty space so names line up
        ImGui.SameLine(0, 6);
        // Only player names (they have a job) get shortened; pet names stay as the game names them.
        var name = row.JobId != 0 ? NameFormatter.Format(row.Name, isSelf, Config.NameDisplay, Config.YouForSelf) : row.Name;
        Widgets.Text(isSelf ? Theme.Bright : Theme.Text, name);
    }

    /// <summary>0xRRGGBB + alpha → ImGui's packed ABGR.</summary>
    private static uint ToImGuiColor(uint rgb, byte alpha) =>
        ((uint)alpha << 24) | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static void DrawCentredText(string text, Vector2 topLeft, Vector2 area)
    {
        var textSize = ImGui.CalcTextSize(text);
        var position = topLeft + ((area - textSize) / 2);
        Widgets.DrawText(ImGui.GetWindowDrawList(), position, Theme.U32(Theme.Dim), text);
    }

    private const float TabGap = 12f;

    /// <summary>Flat text tabs with an accent underline on the active one; raid totals on the right.</summary>
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

        // Raid totals sit on the tabs' line, right-aligned: labels muted, numbers bright.
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

    /// <summary>
    /// Picks the longest raid-total text (and tab labels) that fit beside the tabs:
    /// "Total DPS: X · HPS: Y" → current tab's number only → compact number → short tab labels → no raid text.
    /// </summary>
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
            (false, [new("Total DPS: ", false), new($"{dps:N0}", true), new(" · HPS: ", false), new($"{hps:N0}", true), new(isHeal ? " (excl. shields)" : "", false)]),
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
