using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Studium.Platform;
using Studium.Core;

namespace Studium.Windows;

public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private MeterTab columnsTab = MeterTab.Dps;
    private volatile bool storageStatsDirty = true;
    private string storageStats = string.Empty;
    private Configuration Config => plugin.Configuration;

    public SettingsWindow(Plugin plugin) : base("Studium – Settings##settings")
    {
        this.plugin = plugin;
        plugin.History.Store.Changed += () => storageStatsDirty = true;
        Size = new Vector2(520, 420);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##settingsTabs"))
            return;

        if (ImGui.BeginTabItem("Meter"))
        {
            DrawMeterTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("History"))
        {
            DrawHistoryTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FFLogs"))
        {
            DrawFflogsTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        fileDialog.Draw();
    }

    private void DrawMeterTab()
    {
        var changed = false;

        ImGui.TextUnformatted("Show meter:");
        changed |= EnumRadio("Always", MeterVisibility.Always, () => Config.Visibility, v => Config.Visibility = v);
        ImGui.SameLine();
        changed |= EnumRadio("In combat", MeterVisibility.InCombat, () => Config.Visibility, v => Config.Visibility = v);
        ImGui.SameLine();
        changed |= EnumRadio("In duty", MeterVisibility.InDuty, () => Config.Visibility, v => Config.Visibility = v);
        changed |= Checkbox("Hide in cutscenes", () => Config.HideInCutscenes, v => Config.HideInCutscenes = v);

        ImGui.Spacing();
        changed |= Checkbox("Lock position and size", () => Config.LockMeter, v => Config.LockMeter = v);
        using (Disabled(!Config.LockMeter))
        {
            changed |= Checkbox("Click-through when locked", () => Config.ClickThroughWhenLocked, v => Config.ClickThroughWhenLocked = v);
        }
        if (Config.LockMeter && Config.ClickThroughWhenLocked)
            ImGui.TextDisabled("The meter ignores the mouse. Hold Ctrl to interact with it.");

        var opacity = Configuration.ClampOpacity(Config.BackgroundOpacity) * 100f;
        if (ImGui.SliderFloat("Background opacity", ref opacity, Configuration.MinOpacity * 100f, 100f, "%.0f%%"))
        {
            Config.BackgroundOpacity = Configuration.ClampOpacity(opacity / 100f);
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Names:");
        changed |= EnumRadio("Full", NameDisplay.Full, () => Config.NameDisplay, v => Config.NameDisplay = v);
        ImGui.SameLine();
        changed |= EnumRadio("Short surname (Iluay D.)", NameDisplay.SurnameInitial, () => Config.NameDisplay, v => Config.NameDisplay = v);
        ImGui.SameLine();
        changed |= EnumRadio("Initials (I. D.)", NameDisplay.Initials, () => Config.NameDisplay, v => Config.NameDisplay = v);
        changed |= Checkbox("Show \"YOU\" instead of my name", () => Config.YouForSelf, v => Config.YouForSelf = v);

        ImGui.TextUnformatted("Gauge:");
        changed |= EnumRadio("Thin underline", GaugeStyle.Underline, () => Config.GaugeStyle, v => Config.GaugeStyle = v);
        ImGui.SameLine();
        changed |= EnumRadio("Full-row bar", GaugeStyle.Background, () => Config.GaugeStyle, v => Config.GaugeStyle = v);

        changed |= Checkbox("Merge pets into owner", () => Config.MergePets, v => Config.MergePets = v);

        ImGui.Spacing();
        ImGui.Separator();
        changed |= DrawColumnsEditor();

        if (changed)
            Config.Save();
    }

    /// <summary>Per-tab column picker: show/hide the tab's columns, reorder with arrows, reset to the tab's defaults.</summary>
    private bool DrawColumnsEditor()
    {
        ImGui.TextUnformatted("Columns for the");
        foreach (var (tab, label) in new[] { (MeterTab.Dps, "DPS"), (MeterTab.Tank, "Tank"), (MeterTab.Heal, "Heal") })
        {
            ImGui.SameLine();
            if (ImGui.RadioButton($"{label}##columnsTab", columnsTab == tab))
                columnsTab = tab;
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("tab");
        ImGui.TextDisabled("Name always comes first. The first column after it stays when the meter is narrow;");
        ImGui.TextDisabled("the others drop from the right.");

        var shown = MeterColumns.Resolve(Config.MeterColumns.GetValueOrDefault(columnsTab), columnsTab).Select(c => c.Id).ToList();
        var edited = new List<string>(shown);
        var changed = false;

        if (!ImGui.BeginTable("##columns", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            return false;
        ImGui.TableSetupColumn("Column");
        ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Order");

        for (var i = 0; i < shown.Count; i++)
        {
            var column = MeterColumns.Get(shown[i]);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var visible = true;
            using (Disabled(shown.Count == 1))
            {
                if (ImGui.Checkbox($"{column.Header}##show{column.Id}", ref visible) && !visible)
                {
                    edited.Remove(column.Id);
                    changed = true;
                }
            }
            if (shown.Count == 1 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("A tab needs at least one column besides Name.");
            ImGui.TableNextColumn();
            ImGui.TextDisabled(column.Description);
            ImGui.TableNextColumn();
            using (Disabled(i == 0))
            {
                if (ImGui.ArrowButton($"##up{column.Id}", ImGuiDir.Up))
                {
                    (edited[i - 1], edited[i]) = (edited[i], edited[i - 1]);
                    changed = true;
                }
            }
            ImGui.SameLine();
            using (Disabled(i == shown.Count - 1))
            {
                if (ImGui.ArrowButton($"##down{column.Id}", ImGuiDir.Down))
                {
                    (edited[i + 1], edited[i]) = (edited[i], edited[i + 1]);
                    changed = true;
                }
            }
        }

        foreach (var column in MeterColumns.Available(columnsTab).Where(id => !shown.Contains(id)).Select(MeterColumns.Get))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var visible = false;
            if (ImGui.Checkbox($"{column.Header}##show{column.Id}", ref visible) && visible)
            {
                edited.Add(column.Id); // new columns join at the end
                changed = true;
            }
            ImGui.TableNextColumn();
            ImGui.TextDisabled(column.Description);
            ImGui.TableNextColumn();
        }
        ImGui.EndTable();

        if (ImGui.Button("Reset to default##columns"))
        {
            Config.MeterColumns.Remove(columnsTab);
            return true;
        }

        if (changed)
            Config.MeterColumns[columnsTab] = edited;
        return changed;
    }

    private void DrawHistoryTab()
    {
        var changed = false;

        changed |= Checkbox("Automatically delete saved fights", () => Config.AutoDeleteFights, v => Config.AutoDeleteFights = v);
        using (Disabled(!Config.AutoDeleteFights))
        {
            var unit = Config.RetentionUnit;
            var value = RetentionPeriod.ToDisplay(Config.RetentionHours, unit);
            ImGui.SetNextItemWidth(260);
            if (ImGui.SliderInt("##retention", ref value, 0, RetentionPeriod.DisplayMax(unit)))
            {
                Config.RetentionHours = RetentionPeriod.FromDisplay(value, unit);
                changed = true;
            }
            ImGui.SameLine();
            changed |= EnumRadio("days", RetentionUnit.Days, () => Config.RetentionUnit, v => Config.RetentionUnit = v);
            ImGui.SameLine();
            changed |= EnumRadio("hours", RetentionUnit.Hours, () => Config.RetentionUnit, v => Config.RetentionUnit = v);
            ImGui.TextDisabled($"Keep fights for: {RetentionPeriod.Describe(Config.RetentionHours)}");
        }


        ImGui.Spacing();
        changed |= Checkbox("Skip fights shorter than", () => Config.SkipShortFights, v => Config.SkipShortFights = v);
        ImGui.SameLine();
        using (Disabled(!Config.SkipShortFights))
        {
            changed |= SecondsInput("##skipShort", "s (not saved at all)", () => Config.SkipShortFightsSeconds, v => Config.SkipShortFightsSeconds = v);
        }

        ImGui.TextUnformatted("Hide in lists fights shorter than");
        ImGui.SameLine();
        changed |= SecondsInput("##hideShort", "s", () => Config.HideShortFightsSeconds, v => Config.HideShortFightsSeconds = v);

        ImGui.TextUnformatted("Start a new play session after");
        ImGui.SameLine();
        var gap = Config.SessionGapHours;
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("h idle##sessionGap", ref gap))
        {
            Config.SessionGapHours = Math.Clamp(gap, 1, 24);
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawStorageStats();

        if (changed)
            Config.Save();
    }

    private void DrawStorageStats()
    {
        if (storageStatsDirty)
        {
            storageStatsDirty = false;
            var count = plugin.History.Store.Entries.Count;
            var bytes = new DirectoryInfo(plugin.History.Directory).EnumerateFiles().Sum(f => f.Length);
            storageStats = $"Saved fights: {count} ({bytes / 1024.0 / 1024.0:0.0} MB)";
        }

        ImGui.TextUnformatted(storageStats);
        ImGui.SameLine();
        if (ImGui.SmallButton("Open folder"))
            OpenFolder(plugin.History.Directory);
        ImGui.TextDisabled($"This session: {plugin.History.SessionFights.Count} fight(s)");
        ImGui.TextDisabled(plugin.History.Directory);
    }

    private static void OpenFolder(string path) => RunHostAction($"open {path}", () => HostShell.OpenFolder(path));

    /// <summary>Off the game thread: winepath and process start can take a moment. Failures go to chat.</summary>
    private static void RunHostAction(string what, Action action) =>
        Task.Run(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Failed to {what}");
                Plugin.ChatGui.PrintError($"[Studium] Couldn't {what}: {ex.Message}");
            }
        });

    private void DrawFflogsTab()
    {
        var iinact = Plugin.PluginInterface.InstalledPlugins
            .FirstOrDefault(p => p.InternalName == "IINACT" && p.IsLoaded);

        ImGui.TextUnformatted($"IINACT detected: {(iinact != null ? "yes" : "no")}");
        ImGui.SameLine();
        using (Disabled(iinact == null))
        {
            if (ImGui.Button("Open IINACT settings"))
                Plugin.CommandManager.ProcessCommand("/iinact");
        }
        ImGui.TextDisabled("IINACT is required for writing FFLogs network logs.");

        ImGui.Spacing();
        ImGui.TextUnformatted("FFLogs Uploader:");
        var path = Config.UploaderPath;
        var browseWidth = ImGui.CalcTextSize("Browse...").X + (ImGui.GetStyle().FramePadding.X * 2);
        ImGui.SetNextItemWidth(-browseWidth - ImGui.GetStyle().ItemSpacing.X);
        if (ImGui.InputTextWithHint("##uploaderPath", ".exe, or the Linux AppImage", ref path, 512))
        {
            Config.UploaderPath = path.Trim();
            Config.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            fileDialog.OpenFileDialog("Select the FFLogs Uploader", "Programs{.exe,.AppImage},.*", (ok, selected) =>
            {
                if (!ok)
                    return;
                Config.UploaderPath = selected;
                Config.Save();
            });
        }

        ImGui.Spacing();
        using (Disabled(string.IsNullOrWhiteSpace(Config.UploaderPath)))
        {
            if (ImGui.Button("Launch Uploader"))
                RunHostAction("launch the FFLogs Uploader", () => HostShell.Launch(Config.UploaderPath));
        }
        if (string.IsNullOrWhiteSpace(Config.UploaderPath) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Set the Uploader path first.");

        ImGui.SameLine();
        var url = plugin.Fights.CurrentFflogsUrl;
        using (Disabled(url == null))
        {
            if (ImGui.Button("Open my FFLogs page") && url != null)
                RunHostAction("open FFLogs", () => HostShell.OpenUrl(url));
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(url ?? "Log in to a character first.");
    }

    private static bool Checkbox(string label, Func<bool> get, Action<bool> set)
    {
        var value = get();
        if (!ImGui.Checkbox(label, ref value))
            return false;
        set(value);
        return true;
    }

    private static bool EnumRadio<T>(string label, T option, Func<T> get, Action<T> set) where T : struct, Enum
    {
        if (!ImGui.RadioButton(label, EqualityComparer<T>.Default.Equals(get(), option)))
            return false;
        set(option);
        return true;
    }

    private static bool SecondsInput(string id, string suffix, Func<int> get, Action<int> set)
    {
        var value = get();
        ImGui.SetNextItemWidth(80);
        var changed = ImGui.InputInt(id, ref value);
        if (changed)
            set(Math.Clamp(value, 0, 600));
        ImGui.SameLine();
        ImGui.TextUnformatted(suffix);
        return changed;
    }

    private static DisabledScope Disabled(bool disabled) => new(disabled);

    private readonly struct DisabledScope : IDisposable
    {
        public DisabledScope(bool disabled) => ImGui.BeginDisabled(disabled);

        public void Dispose() => ImGui.EndDisabled();
    }
}
