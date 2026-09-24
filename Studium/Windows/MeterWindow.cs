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

namespace Studium.Windows;

public sealed class MeterWindow : Window, IDisposable
{
    private const string WindowId = "###StudiumMeter";

    private static readonly (MeterTab Tab, string Label)[] Tabs =
        [(MeterTab.Dps, "DPS"), (MeterTab.Tank, "Tank"), (MeterTab.Heal, "Heal")];

    private readonly Plugin plugin;
    private readonly IFontHandle headerFont;
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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(320, 80) };

        // Dalamud only honours IsPinned/IsClickthrough while these are on. They also add Dalamud's
        // title-bar menu (☰), which is the escape hatch out of click-through; PreDraw syncs it with our config.
        AllowPinning = true;
        AllowClickthrough = true;
    }

    public void Dispose() => headerFont.Dispose();

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
        var fight = plugin.Fights.Tracker.Displayed;
        var now = DateTime.UtcNow;
        var summary = fight != null ? FightView.Summarize(fight, now, Config.MergePets) : null;

        DrawHeader(fight, now);
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        DrawTable(new Vector2(0, -footerHeight), summary);
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

    private void DrawHeader(Fight? fight, DateTime now)
    {
        var rowTop = ImGui.GetCursorPosY();
        float rowHeight;
        using (headerFont.Push())
        {
            ImGui.TextUnformatted(fight != null ? FormatDuration(fight.Duration(now)) : "00:00");
            rowHeight = ImGui.GetItemRectSize().Y;
        }

        // Fight name in the normal font, vertically centred on the larger timer.
        var textY = rowTop + ((rowHeight - ImGui.GetTextLineHeight()) / 2);
        ImGui.SameLine();
        ImGui.SetCursorPosY(textY);
        ImGui.TextUnformatted(fight?.Name ?? "No fight yet");
        if (fight is { IsActive: false, Outcome: not FightOutcome.Unknown })
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textY);
            ImGui.TextUnformatted(fight.Outcome == FightOutcome.Clear ? "· Clear" : "· Wipe");
        }
        if (!string.IsNullOrEmpty(fight?.Zone))
        {
            ImGui.SameLine();
            ImGui.SetCursorPosY(textY);
            ImGui.TextDisabled(fight.Zone);
        }

        var buttons = new[] { FontAwesomeIcon.History, FontAwesomeIcon.Cog };
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var buttonsWidth = buttons.Sum(IconButtonWidth) + (spacing * (buttons.Length - 1));
        ImGui.SameLine(ImGui.GetContentRegionMax().X - buttonsWidth);
        ImGui.SetCursorPosY(rowTop + ((rowHeight - ImGui.GetFrameHeight()) / 2));

        if (ImGuiComponents.IconButton("##history", FontAwesomeIcon.History))
            plugin.OpenHistory();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fight history");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##settings", FontAwesomeIcon.Cog))
            plugin.OpenSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Settings");

        ImGui.SetCursorPosY(rowTop + rowHeight + ImGui.GetStyle().ItemSpacing.Y);
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
            new("Taken", r => Compact(r.DamageTaken)),
            new("Taken%", r => Percent(r.DamageTakenShare)),
            new("Parry", r => Percent(r.ParryRate)),
            new("Block", r => Percent(r.BlockRate)),
            new("Healed-on", r => Compact(r.HealingReceived)),
            new("Deaths", r => r.Deaths.ToString()),
        ],
        MeterTab.Heal =>
        [
            new("Name", r => r.Name),
            new("H%", r => Percent(r.HealingShare)),
            new("HPS", r => r.Hps.ToString("N0")),
            new("Total", r => Compact(r.Healing)),
            new("Overheal", r => Percent(r.OverhealRate)),
            new("Crit", r => Percent(r.HealCritRate)),
            new("Deaths", r => r.Deaths.ToString()),
        ],
        _ =>
        [
            new("Name", r => r.Name),
            new("D%", r => Percent(r.DamageShare)),
            new("DPS", r => r.Dps.ToString("N0")),
            new("Total", r => Compact(r.Damage)),
            new("Crit", r => Percent(r.CritRate)),
            new("DH", r => Percent(r.DirectHitRate)),
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

    private void DrawTable(Vector2 size, FightSummary? summary)
    {
        var columns = ColumnsFor(Config.MeterTab);

        var tableTopLeft = ImGui.GetCursorScreenPos();
        var tableSize = ImGui.GetContentRegionAvail() + size; // size.Y is negative: space kept for the tab bar
        if (!ImGui.BeginTable("##meter", columns.Length, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, size))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < columns.Length; i++)
        {
            var flags = i == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed;
            ImGui.TableSetupColumn(columns[i].Header, flags);
        }
        ImGui.TableHeadersRow();

        var localId = plugin.Fights.LocalPlayerId;
        if (summary != null)
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

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"mm\:ss");

    private static string Percent(double share) => $"{share * 100:0}%";

    /// <summary>1,234 · 12.3k · 1.23M</summary>
    private static string Compact(long value) => value switch
    {
        < 10_000 => value.ToString("N0"),
        < 1_000_000 => $"{value / 1000.0:0.0}k",
        _ => $"{value / 1_000_000.0:0.00}M",
    };

    private static void DrawCentredText(string text, Vector2 topLeft, Vector2 area)
    {
        var textSize = ImGui.CalcTextSize(text);
        var position = topLeft + ((area - textSize) / 2);
        ImGui.GetWindowDrawList().AddText(position, ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
    }

    private void DrawTabBar(FightSummary? summary)
    {
        var lineStart = ImGui.GetCursorPos();

        if (ImGui.BeginTabBar("##meterTabs"))
        {
            foreach (var (tab, label) in Tabs)
            {
                var flags = restoreSavedTab && Config.MeterTab == tab
                    ? ImGuiTabItemFlags.SetSelected
                    : ImGuiTabItemFlags.None;
                if (!ImGui.BeginTabItem(label, flags))
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
        var raidText = $"raid {summary?.RaidDps ?? 0:N0} dps · {summary?.RaidHps ?? 0:N0} hps";
        if (Config.MeterTab == MeterTab.Heal)
            raidText += " (excl. shields)";
        var textWidth = ImGui.CalcTextSize(raidText).X;
        ImGui.SetCursorPos(new Vector2(ImGui.GetContentRegionMax().X - textWidth, lineStart.Y + ImGui.GetStyle().FramePadding.Y));
        ImGui.TextUnformatted(raidText);
    }

    private void SetOpenState(bool open)
    {
        if (Config.MeterOpen == open)
            return;
        Config.MeterOpen = open;
        Config.Save();
    }
}
