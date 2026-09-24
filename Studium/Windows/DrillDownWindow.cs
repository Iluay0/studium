using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Studium.Core;
using Studium.Core.Fights;

namespace Studium.Windows;

/// <summary>Per-ability breakdown for one meter row, opened by clicking it. Follows a live fight as it updates.</summary>
public sealed class DrillDownWindow : Window
{
    private readonly Plugin plugin;
    private Fight? fight;
    private uint rowId;
    private MeterTab tab;

    public DrillDownWindow(Plugin plugin) : base("Breakdown###StudiumDrillDown")
    {
        this.plugin = plugin;
        Size = new Vector2(620, 320);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Show(Fight fight, uint rowId, MeterTab tab)
    {
        this.fight = fight;
        this.rowId = rowId;
        this.tab = tab;
        IsOpen = true;
        BringToFront();
    }

    public override void PreDraw()
    {
        var row = CurrentRow();
        var name = row?.Name ?? "Breakdown";
        var what = tab switch
        {
            MeterTab.Heal => "healing",
            MeterTab.Tank => "damage taken",
            _ => "damage",
        };
        WindowName = fight == null
            ? "Breakdown###StudiumDrillDown"
            : $"{name} · {what} · {fight.Name} ({Format.Duration(fight.Duration(DateTime.UtcNow))})###StudiumDrillDown";
    }

    public override void Draw()
    {
        if (fight == null || CurrentRow() is not { } row)
        {
            ImGui.TextDisabled("Click a player in the meter to see their breakdown.");
            return;
        }

        ImGui.TextUnformatted(tab switch
        {
            MeterTab.Heal => $"{row.Hps:N0} hps · {Format.Compact(row.Healing)} total · {Format.Percent(row.OverhealRate)} overheal · {Format.Percent(row.HealCritRate)} crit",
            MeterTab.Tank => $"{Format.Compact(row.DamageTaken)} taken · {Format.Percent(row.ParryRate)} parry · {Format.Percent(row.BlockRate)} block · {row.Deaths} deaths",
            _ => $"{row.Dps:N0} dps · {Format.Compact(row.Damage)} total · {Format.Percent(row.CritRate)} crit · {Format.Percent(row.DirectHitRate)} DH · {row.Deaths} deaths",
        });
        ImGui.Separator();

        var abilities = FightView.Abilities(fight, rowId, tab, plugin.Configuration.MergePets);
        var isHeal = tab == MeterTab.Heal;
        string[] headers = isHeal
            ? ["Ability", "Total", "%", "Hits", "Crit", "Overheal", "Avg", "Max"]
            : ["Ability", "Total", "%", "Hits", "Crit", "DH", "Avg", "Max"];

        if (!ImGui.BeginTable("##abilities", headers.Length, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < headers.Length; i++)
            ImGui.TableSetupColumn(headers[i], i == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableHeadersRow();

        foreach (var ability in abilities)
        {
            ImGui.TableNextRow();
            Cell(AbilityName(ability));
            Cell(Format.Compact(ability.Total));
            Cell(Format.Percent(ability.Share));
            Cell(ability.Hits.ToString("N0"));
            Cell(ability.IsTick ? "—" : Format.Percent(ability.CritRate));
            Cell(isHeal ? Format.Percent(ability.OverhealRate) : ability.IsTick ? "—" : Format.Percent(ability.DirectHitRate));
            Cell(ability.Average.ToString("N0"));
            Cell(ability.Max.ToString("N0"));
        }

        ImGui.EndTable();
    }

    private CombatantRow? CurrentRow() =>
        fight == null
            ? null
            : FightView.Summarize(fight, DateTime.UtcNow, plugin.Configuration.MergePets).Rows.FirstOrDefault(r => r.Id == rowId);

    private string AbilityName(AbilityRow ability)
    {
        var name = ability.ActionId switch
        {
            AbilityStats.DotKey => "DoT ticks",
            AbilityStats.HotKey => "HoT ticks",
            _ => plugin.Names.Action(ability.ActionId),
        };
        if (string.IsNullOrEmpty(name))
            name = $"Unknown ({ability.ActionId})";
        return ability.PetName != null ? $"{name} ({ability.PetName})" : name;
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

}
