using System.Numerics;
using Dalamud.Bindings.ImGui;
using Studium.Core;
using Studium.Core.Fights;
using Studium.Ui;

namespace Studium.Windows;

/// <summary>Per-ability breakdown for one meter row, opened by clicking it. Follows a live fight as it updates.</summary>
public sealed class DrillDownWindow : Theme.ThemedWindow
{
    private const float IconScale = 1.75f;

    private readonly Plugin plugin;
    private Fight? fight;
    private uint rowId;
    private MeterTab tab;

    public DrillDownWindow(Plugin plugin) : base("Breakdown###StudiumDrillDown")
    {
        this.plugin = plugin;
        Size = new Vector2(620, 340);
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
        base.PreDraw();
        var row = CurrentRow();
        var what = tab switch
        {
            MeterTab.Heal => "Healing",
            MeterTab.Tank => "Damage Taken",
            _ => "Damage",
        };
        WindowName = fight == null || row == null
            ? "Breakdown###StudiumDrillDown"
            : $"{row.Name} · {what} · {fight.Name} ({Format.Duration(fight.Duration(DateTime.UtcNow))})###StudiumDrillDown";
    }

    public override void Draw()
    {
        if (fight == null || CurrentRow() is not { } row)
        {
            ImGui.TextColored(Theme.Dim, "Click a player in the meter to see their breakdown.");
            return;
        }

        DrawSummary(row);

        var abilities = FightView.Abilities(fight, rowId, tab, plugin.Configuration.MergePets);
        var isHeal = tab == MeterTab.Heal;
        string[] headers = isHeal
            ? ["Ability", "Total", "%", "Hits", "Crit", "Overheal", "Avg", "Max"]
            : ["Ability", "Total", "%", "Hits", "Crit", "DH", "Avg", "Max"];

        var tableLeft = ImGui.GetCursorScreenPos().X;
        var tableWidth = ImGui.GetContentRegionAvail().X;
        if (!ImGui.BeginTable("##abilities", headers.Length, ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        for (var i = 0; i < headers.Length; i++)
            ImGui.TableSetupColumn(headers[i], i == 0 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed);
        var body = Widgets.TableBodyRect(Widgets.HeaderRow(headers, tableLeft, tableWidth));

        // Tab colour for the gauges: the player's job colour (grey for NPCs / unknown).
        var colour = Theme.Rgb(Jobs.Rgb(row.JobId));
        var top = abilities.Count > 0 ? Math.Max(abilities.Max(a => a.Total), 1) : 1;
        var lineHeight = ImGui.GetTextLineHeight();
        // Icons at 1.5× the text height: bigger icons without a bigger font; rows grow taller, not wider.
        var iconSize = MathF.Round(lineHeight * IconScale);

        // Ticks sit in the same list, sorted by total like everything else.
        foreach (var ability in abilities)
            DrawAbility(ability, isHeal, colour, top, tableLeft, tableWidth, iconSize, body);

        ImGui.EndTable();
    }

    private void DrawSummary(CombatantRow row)
    {
        (string Label, string Value)[] stats = tab switch
        {
            MeterTab.Heal => [("HPS", row.Hps.ToString("N0")), ("Total", Format.Compact(row.Healing)), ("Overheal", Format.Percent(row.OverhealRate)), ("Crit", Format.Percent(row.HealCritRate)), ("Deaths", row.Deaths.ToString())],
            MeterTab.Tank => [("Taken", Format.Compact(row.DamageTaken)), ("Parry", Format.Percent(row.ParryRate)), ("Block", Format.Percent(row.BlockRate)), ("Healed-on", Format.Compact(row.HealingReceived)), ("Deaths", row.Deaths.ToString())],
            _ => [("DPS", row.Dps.ToString("N0")), ("Total", Format.Compact(row.Damage)), ("Crit", Format.Percent(row.CritRate)), ("Direct hit", Format.Percent(row.DirectHitRate)), ("Deaths", row.Deaths.ToString())],
        };

        var top = ImGui.GetCursorPosY();
        var x = ImGui.GetCursorPosX();
        foreach (var (label, value) in stats)
        {
            var width = Math.Max(ImGui.CalcTextSize(label.ToUpperInvariant()).X, ImGui.CalcTextSize(value).X) + 18;
            ImGui.SetCursorPos(new Vector2(x, top));
            ImGui.TextColored(Theme.Dim, label.ToUpperInvariant());
            ImGui.SetCursorPos(new Vector2(x, top + ImGui.GetTextLineHeight()));
            ImGui.TextColored(Theme.Bright, value);
            x += width;
        }

        var y = ImGui.GetCursorScreenPos().Y + 3;
        var left = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X;
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        ImGui.GetWindowDrawList().AddLine(new Vector2(left, y), new Vector2(right, y), Theme.U32(Theme.Line));
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 8);
    }

    private void DrawAbility(AbilityRow ability, bool isHeal, Vector4 colour, long top, float tableLeft, float tableWidth, float iconSize, (Vector2 Min, Vector2 Max) body)
    {
        var rowHeight = iconSize + (ImGui.GetStyle().CellPadding.Y * 2);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.TableNextColumn();

        // Thin gauge along the bottom of the row, sized against the top ability.
        var rowBottom = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().CellPadding.Y + rowHeight;
        var fraction = (float)ability.Total / top;
        Widgets.PushClip(new Vector2(tableLeft, rowBottom - 2), new Vector2(tableLeft + tableWidth, rowBottom), body);
        ImGui.GetWindowDrawList().AddRectFilled(
            new Vector2(tableLeft, rowBottom - 1.5f), new Vector2(tableLeft + (tableWidth * fraction), rowBottom),
            Theme.U32(colour with { W = 0.8f }));
        ImGui.PopClipRect();

        DrawAbilityIcon(ability, iconSize);
        ImGui.SameLine(0, 7);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((iconSize - ImGui.GetTextLineHeight()) / 2));
        ImGui.TextColored(Theme.Text, AbilityName(ability, isHeal));
        if (ability.StatusIds.Count > 1 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", ability.StatusIds.Select(id => plugin.Names.Status(id).Name)));

        Number(Format.Compact(ability.Total), Theme.Bright);
        Number(Format.Percent(ability.Share), Theme.Muted);
        Number(ability.Hits.ToString("N0"), Theme.Muted);
        Number(ability.IsTick ? "—" : Format.Percent(ability.CritRate), Theme.Muted);
        Number(isHeal ? Format.Percent(ability.OverhealRate) : ability.IsTick ? "—" : Format.Percent(ability.DirectHitRate), Theme.Muted);
        Number(ability.Average.ToString("N0"), Theme.Muted);
        Number(ability.Max.ToString("N0"), Theme.Muted);

        void Number(string text, Vector4 textColour)
        {
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((iconSize - ImGui.GetTextLineHeight()) / 2));
            Widgets.RightText(text, textColour);
        }
    }

    /// <summary>
    /// Skill icon for actions, status icon for one DoT / HoT. Combined ticks show each status icon, smaller and
    /// overlapping diagonally, inside the same square so names stay aligned. Unknown ticks get an empty square.
    /// </summary>
    private void DrawAbilityIcon(AbilityRow ability, float size)
    {
        if (!ability.IsTick)
        {
            Widgets.GameIcon(plugin.Names.ActionIcon(ability.ActionId), size);
            return;
        }
        if (ability.StatusIds.Count <= 1)
        {
            Widgets.GameIcon(ability.StatusIds.Count == 1 ? plugin.Names.Status(ability.StatusIds[0]).Icon : 0, size);
            return;
        }

        var start = ImGui.GetCursorPos();
        var count = ability.StatusIds.Count;
        var small = size * 0.72f;
        var step = (size - small) / (count - 1);
        for (var i = 0; i < count; i++)
        {
            ImGui.SetCursorPos(start + new Vector2(step * i, step * i));
            Widgets.GameIcon(plugin.Names.Status(ability.StatusIds[i]).Icon, small);
        }
        ImGui.SetCursorPos(start);
        ImGui.Dummy(new Vector2(size));
    }

    private CombatantRow? CurrentRow() =>
        fight == null
            ? null
            : FightView.Summarize(fight, DateTime.UtcNow, plugin.Configuration.MergePets).Rows.FirstOrDefault(r => r.Id == rowId);

    /// <summary>
    /// "Glare III"; "Dia (DoT)" for one DoT's ticks; "DoT ticks (Caustic Bite + Stormbite)" for combined ticks;
    /// "DoT ticks" when no status could be identified. Pet abilities get the pet's name.
    /// </summary>
    private string AbilityName(AbilityRow ability, bool isHeal)
    {
        var kind = isHeal ? "HoT" : "DoT";
        var name = ability.IsTick
            ? ability.StatusIds.Count switch
            {
                0 => $"{kind} ticks",
                1 => $"{plugin.Names.Status(ability.StatusIds[0]).Name} ({kind})",
                _ => $"{kind} ticks ({string.Join(" + ", ability.StatusIds.Select(id => plugin.Names.Status(id).Name))})",
            }
            : plugin.Names.Action(ability.ActionId);
        if (string.IsNullOrEmpty(name))
            name = $"Unknown ({ability.ActionId})";
        return ability.PetName != null ? $"{name} ({ability.PetName})" : name;
    }
}
