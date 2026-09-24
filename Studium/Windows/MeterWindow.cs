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

namespace Studium.Windows;

public sealed class MeterWindow : Window, IDisposable
{
    private const string WindowId = "###StudiumMeter";

    private static readonly (MeterTab Tab, string Label, string ShortLabel)[] Tabs =
        [(MeterTab.Dps, "DPS", "D"), (MeterTab.Tank, "Tank", "T"), (MeterTab.Heal, "Heal", "H")];

    private readonly Plugin plugin;
    private readonly IFontHandle headerFont;
    /// <summary>A past fight picked from the dropdown or history; null shows the live/last fight.</summary>
    private Fight? viewedFight;
    private bool restoreSavedTab = true;
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
        SyncLockState();
        BgAlpha = Config.BackgroundOpacity;

        // ### keeps the window ID (position, size) stable while the visible title changes.
        WindowName = Plugin.DisplayName + (ClickThroughConfigured ? " (Click-through - Ctrl to interact)" : "") + WindowId;
    }

    public override void Draw()
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
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
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
    /// Timer on the left; fight name (the fight picker) and zone stacked beside it, cut to fit
    /// before the buttons on the right.
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
            ImGui.TextUnformatted(timerText);

        ImGui.SameLine();
        var textX = ImGui.GetCursorPosX();
        var buttons = new[] { FontAwesomeIcon.History, FontAwesomeIcon.Cog };
        var buttonsWidth = buttons.Sum(IconButtonWidth) + (style.ItemSpacing.X * (buttons.Length - 1));
        var buttonsX = ImGui.GetContentRegionMax().X - buttonsWidth;
        var textWidth = buttonsX - textX - style.ItemSpacing.X;

        var blockTop = rowTop + ((rowHeight - textBlockHeight) / 2);
        var name = fight?.Name ?? "No fight yet";
        if (fight is { IsActive: false, Outcome: not FightOutcome.Unknown })
            name += fight.Outcome == FightOutcome.Clear ? " · Clear" : " · Wipe";

        ImGui.SetCursorPos(new Vector2(textX, blockTop));
        DrawFightPicker(fight, name, textWidth);
        if (!string.IsNullOrEmpty(fight?.Zone))
        {
            ImGui.SetCursorPos(new Vector2(textX, blockTop + lineHeight));
            ImGui.TextDisabled(Truncate(fight.Zone, textWidth));
        }

        ImGui.SetCursorPos(new Vector2(buttonsX, rowTop + ((rowHeight - ImGui.GetFrameHeight()) / 2)));
        if (ImGuiComponents.IconButton("##history", FontAwesomeIcon.History))
            plugin.OpenHistory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fight history");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##settings", FontAwesomeIcon.Cog))
            plugin.OpenSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");

        ImGui.SetCursorPosY(rowTop + rowHeight + style.ItemSpacing.Y);
    }

    /// <summary>Cuts text to fit a width, ending in "...".</summary>
    private static string Truncate(string text, float maxWidth)
    {
        if (ImGui.CalcTextSize(text).X <= maxWidth)
            return text;
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + "...";
            if (ImGui.CalcTextSize(candidate).X <= maxWidth)
                return candidate;
        }
        return string.Empty;
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
    private void DrawFightPicker(Fight? shown, string label, float maxWidth)
    {
        // Name plus a caret icon, as one clickable area. The icon comes from the icon font: the
        // default font has no reliable arrow glyph.
        var caret = FontAwesomeIcon.CaretDown.ToIconString();
        float caretWidth;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            caretWidth = ImGui.CalcTextSize(caret).X;
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        var name = Truncate(label, Math.Max(maxWidth - spacing - caretWidth, 0));
        var nameSize = new Vector2(ImGui.CalcTextSize(name).X, ImGui.GetTextLineHeight());

        var start = ImGui.GetCursorPos();
        if (ImGui.Selectable("##fightPicker", false, ImGuiSelectableFlags.None, new Vector2(nameSize.X + spacing + caretWidth, nameSize.Y)))
            ImGui.OpenPopup("##fights");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Switch fight");
        var end = ImGui.GetCursorPos();

        ImGui.SetCursorPos(start);
        ImGui.TextUnformatted(name);
        ImGui.SameLine(0, spacing);
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            ImGui.TextDisabled(caret);
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
            var outcome = entry.Outcome switch
            {
                FightOutcome.Clear => "  clear",
                FightOutcome.Wipe => "  wipe",
                _ => string.Empty,
            };
            var text = $"{entry.Start.ToLocalTime():HH:mm}  {entry.Name}  {Format.Duration(TimeSpan.FromSeconds(entry.DurationSeconds))}{outcome}##{entry.Id}";
            if (ImGui.Selectable(text, shown?.Id == entry.Id) && plugin.History.Open(entry.Id) is { } picked)
                viewedFight = picked == tracker.Last ? null : picked;
        }

        ImGui.EndPopup();
    }

    private static float IconButtonWidth(FontAwesomeIcon icon)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            return ImGui.CalcTextSize(icon.ToIconString()).X + (ImGui.GetStyle().FramePadding.X * 2);
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
        var tableSize = ImGui.GetContentRegionAvail() + size; // size.Y is negative: space kept for the tab bar
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
        ImGui.TableHeadersRow();

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
                ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);

                ImGui.TableNextColumn();
                DrawGauge(row, (double)MainMetric(row, tab) / top, tableTopLeft.X, tableSize.X, rowHeight);

                // Invisible full-row selectable: hover feedback, and a click opens the breakdown.
                var cellStart = ImGui.GetCursorPos();
                if (ImGui.Selectable($"##row{row.Id}", false, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap, new Vector2(0, lineHeight)))
                    plugin.DrillDownWindow.Show(fight, row.Id, tab);
                ImGui.SetCursorPos(cellStart);

                DrawNameCell(row, isSelf, lineHeight);

                foreach (var column in columns.Skip(1))
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(column.Value(row));
                    if (column.Tooltip?.Invoke(row) is { Length: > 0 } tooltip && ImGui.IsItemHovered())
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
    /// Its length is this row's share of the tab's top value.
    /// </summary>
    private void DrawGauge(CombatantRow row, double fraction, float tableLeft, float tableWidth, float rowHeight)
    {
        if (fraction <= 0)
            return;

        var rowTop = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().CellPadding.Y;
        var rowBottom = rowTop + rowHeight;
        var right = tableLeft + (float)(tableWidth * Math.Min(fraction, 1));
        var rgb = Jobs.Rgb(row.JobId);

        ImGui.PushClipRect(new Vector2(tableLeft, rowTop), new Vector2(tableLeft + tableWidth, rowBottom), false);
        var drawList = ImGui.GetWindowDrawList();
        if (Config.GaugeStyle == GaugeStyle.Background)
            drawList.AddRectFilled(new Vector2(tableLeft, rowTop), new Vector2(right, rowBottom), ToImGuiColor(rgb, 0x59));
        else
            drawList.AddRectFilled(new Vector2(tableLeft, rowBottom - 2), new Vector2(right, rowBottom), ToImGuiColor(rgb, 0xFF));
        ImGui.PopClipRect();
    }

    private void DrawNameCell(CombatantRow row, bool isSelf, float iconSize)
    {
        var iconId = Jobs.IconId(row.JobId);
        if (iconId != 0)
        {
            var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new Vector2(iconSize));
        }
        else
        {
            ImGui.Dummy(new Vector2(iconSize)); // pets and NPCs: keep names aligned
        }

        ImGui.SameLine();
        // Only player names (they have a job) get shortened; pet names stay as the game names them.
        var name = row.JobId != 0 ? NameFormatter.Format(row.Name, isSelf, Config.NameDisplay, Config.YouForSelf) : row.Name;
        ImGui.TextUnformatted(name);
    }

    /// <summary>0xRRGGBB + alpha → ImGui's packed ABGR.</summary>
    private static uint ToImGuiColor(uint rgb, byte alpha) =>
        ((uint)alpha << 24) | ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static void DrawCentredText(string text, Vector2 topLeft, Vector2 area)
    {
        var textSize = ImGui.CalcTextSize(text);
        var position = topLeft + ((area - textSize) / 2);
        ImGui.GetWindowDrawList().AddText(position, ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
    }

    private void DrawTabBar(FightSummary? summary)
    {
        var lineStart = ImGui.GetCursorPos();
        var (shortLabels, raidText) = FitFooter(summary);

        if (ImGui.BeginTabBar("##meterTabs"))
        {
            foreach (var (tab, label, shortLabel) in Tabs)
            {
                var flags = restoreSavedTab && Config.MeterTab == tab
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;
                // ### keeps the tab's identity when its label shortens.
                if (!ImGui.BeginTabItem($"{(shortLabels ? shortLabel : label)}###{label}", flags))
                    continue;

                if (!restoreSavedTab && Config.MeterTab != tab)
                {
                    Config.MeterTab = tab;
                    Config.Save();
                }
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
            restoreSavedTab = false;
        }

        // Raid totals sit on the tab bar's line, right-aligned.
        if (raidText.Length == 0)
            return;
        var textWidth = ImGui.CalcTextSize(raidText).X;
        ImGui.SetCursorPos(new Vector2(ImGui.GetContentRegionMax().X - textWidth, lineStart.Y + ImGui.GetStyle().FramePadding.Y));
        ImGui.TextUnformatted(raidText);
    }

    /// <summary>
    /// Picks the longest raid-total text (and tab labels) that fit beside the tabs:
    /// full → current tab's number only → compact number → short tab labels → no raid text.
    /// </summary>
    private (bool ShortLabels, string RaidText) FitFooter(FightSummary? summary)
    {
        var dps = summary?.RaidDps ?? 0;
        var hps = summary?.RaidHps ?? 0;
        var isHeal = Config.MeterTab == MeterTab.Heal;
        var tabNumber = isHeal ? $"{hps:N0} hps" : $"{dps:N0} dps";
        var compactNumber = isHeal ? $"{Format.Compact((long)hps)} hps" : $"{Format.Compact((long)dps)} dps";

        (bool, string)[] candidates =
        [
            (false, $"raid {dps:N0} dps · {hps:N0} hps" + (isHeal ? " (excl. shields)" : "")),
            (false, $"raid {tabNumber}"),
            (false, compactNumber),
            (true, compactNumber),
            (true, string.Empty),
        ];

        var available = ImGui.GetContentRegionAvail().X;
        var gap = ImGui.GetStyle().ItemSpacing.X * 2;
        foreach (var (shortLabels, text) in candidates)
        {
            var textWidth = text.Length == 0 ? 0 : ImGui.CalcTextSize(text).X + gap;
            if (TabsWidth(shortLabels) + textWidth <= available)
                return (shortLabels, text);
        }
        return (true, string.Empty);
    }

    private static float TabsWidth(bool shortLabels)
    {
        var style = ImGui.GetStyle();
        return Tabs.Sum(t => ImGui.CalcTextSize(shortLabels ? t.ShortLabel : t.Label).X + (style.FramePadding.X * 2) + style.ItemInnerSpacing.X);
    }

    private void SetOpenState(bool open)
    {
        if (Config.MeterOpen == open)
            return;
        Config.MeterOpen = open;
        Config.Save();
    }
}
