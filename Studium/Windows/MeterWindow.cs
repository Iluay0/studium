using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Studium.Core;

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
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(320, 120) };

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
        DrawHeader();
        ImGui.Separator();

        var footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        DrawTable(new Vector2(0, -footerHeight));
        DrawTabBar();
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

    private void DrawHeader()
    {
        var rowTop = ImGui.GetCursorPosY();
        float rowHeight;
        using (headerFont.Push())
        {
            ImGui.TextUnformatted("00:00");
            rowHeight = ImGui.GetItemRectSize().Y;
        }

        // Fight name in the normal font, vertically centred on the larger timer.
        ImGui.SameLine();
        ImGui.SetCursorPosY(rowTop + ((rowHeight - ImGui.GetTextLineHeight()) / 2));
        ImGui.TextUnformatted("No fight yet");

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

    private void DrawTable(Vector2 size)
    {
        var columns = Config.MeterTab switch
        {
            MeterTab.Tank => new[] { "Name", "Taken", "Taken%", "Parry", "Block", "Healed-on", "Deaths" },
            MeterTab.Heal => new[] { "Name", "H%", "HPS", "Total", "Overheal", "Crit", "Deaths" },
            _ => new[] { "Name", "D%", "DPS", "Total", "Crit", "DH", "Max hit", "Deaths" },
        };

        var tableTopLeft = ImGui.GetCursorScreenPos();
        var tableSize = ImGui.GetContentRegionAvail() + size; // size.Y is negative: space kept for the tab bar
        if (!ImGui.BeginTable("##meter", columns.Length, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp, size))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < columns.Length; i++)
        {
            var flags = i == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed;
            ImGui.TableSetupColumn(columns[i], flags);
        }
        ImGui.TableHeadersRow();
        ImGui.EndTable();

        // Placeholder until the combat hooks exist: centred over the whole table, not inside a column.
        DrawCentredText("Waiting for combat…", tableTopLeft, tableSize);
    }

    private static void DrawCentredText(string text, Vector2 topLeft, Vector2 area)
    {
        var textSize = ImGui.CalcTextSize(text);
        var position = topLeft + ((area - textSize) / 2);
        ImGui.GetWindowDrawList().AddText(position, ImGui.GetColorU32(ImGuiCol.TextDisabled), text);
    }

    private void DrawTabBar()
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
        var raidText = "raid 0 dps · 0 hps";
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
