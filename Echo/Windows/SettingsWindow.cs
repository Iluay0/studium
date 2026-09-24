using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Echo.Core;

namespace Echo.Windows;

public sealed class SettingsWindow : Window
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Configuration;

    public SettingsWindow(Plugin plugin) : base("Echo – Settings##settings")
    {
        this.plugin = plugin;
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
            ImGui.TextDisabled("The meter ignores the mouse. To undo: the ☰ button on its title bar, or /echo config.");

        var opacity = Config.BackgroundOpacity * 100f;
        if (ImGui.SliderFloat("Background opacity", ref opacity, 0f, 100f, "%.0f%%"))
        {
            Config.BackgroundOpacity = Math.Clamp(opacity / 100f, 0f, 1f);
            changed = true;
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Names:");
        changed |= EnumRadio("Full", NameDisplay.Full, () => Config.NameDisplay, v => Config.NameDisplay = v);
        ImGui.SameLine();
        changed |= EnumRadio("Initials", NameDisplay.Initials, () => Config.NameDisplay, v => Config.NameDisplay = v);
        ImGui.SameLine();
        changed |= EnumRadio("\"YOU\" for me", NameDisplay.YouForSelf, () => Config.NameDisplay, v => Config.NameDisplay = v);

        ImGui.TextUnformatted("Gauge:");
        changed |= EnumRadio("Thin underline", GaugeStyle.Underline, () => Config.GaugeStyle, v => Config.GaugeStyle = v);
        ImGui.SameLine();
        changed |= EnumRadio("Full-row bar", GaugeStyle.Background, () => Config.GaugeStyle, v => Config.GaugeStyle = v);

        changed |= Checkbox("Merge pets into owner", () => Config.MergePets, v => Config.MergePets = v);

        if (changed)
            Config.Save();
    }

    private void DrawHistoryTab()
    {
        var changed = false;

        using (Disabled(Config.NeverDeleteFights))
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

        changed |= Checkbox("Never delete saved fights", () => Config.NeverDeleteFights, v => Config.NeverDeleteFights = v);

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

        if (changed)
            Config.Save();
    }

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
        var path = Config.UploaderPath;
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##uploaderPath", "FFLogs Uploader path (.exe or AppImage)", ref path, 512))
        {
            Config.UploaderPath = path.Trim();
            Config.Save();
        }

        using (Disabled(true))
        {
            ImGui.Button("Launch Uploader");
            ImGui.SameLine();
            ImGui.Button("Open my FFLogs page");
        }
        ImGui.TextDisabled("These buttons arrive in a later update.");
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
