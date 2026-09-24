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
    private bool showDeaths;
    private Fight? fight;
    private uint rowId;
    private MeterTab tab;

    public DrillDownWindow(Plugin plugin) : base("Breakdown###StudiumDrillDown")
    {
        this.plugin = plugin;
        Size = new Vector2(620, 340);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    /// <summary>Opens the breakdown for a meter row; <paramref name="deaths"/> opens it on the Deaths tab.</summary>
    public void Show(Fight fight, uint rowId, MeterTab tab, bool deaths = false)
    {
        this.fight = fight;
        this.rowId = rowId;
        this.tab = tab;
        showDeaths = deaths;
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

        var deathCount = fight.Deaths.Count(d => d.VictimId == rowId);
        var selected = Widgets.FlatTabs("##breakdownTabs", ["Abilities", deathCount > 0 ? $"Deaths ({deathCount})" : "Deaths"], showDeaths ? 1 : 0);
        showDeaths = selected == 1;
        ImGui.Spacing();

        if (showDeaths)
            DrawDeaths();
        else
            DrawAbilities(row);
    }

    private void DrawAbilities(CombatantRow row)
    {
        var abilities = FightView.Abilities(fight!, rowId, tab, plugin.Configuration.MergePets);
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

    /// <summary>Each death of this player: when, what killed them, and the last 30 s leading up to it.</summary>
    private void DrawDeaths()
    {
        var deaths = fight!.Deaths.Where(d => d.VictimId == rowId).ToList();
        if (deaths.Count == 0)
        {
            ImGui.TextColored(Theme.Dim, "No deaths in this fight.");
            return;
        }

        if (!ImGui.BeginChild("##deaths"))
        {
            ImGui.EndChild();
            return;
        }

        var iconSize = MathF.Round(ImGui.GetTextLineHeight() * 1.25f);
        for (var i = 0; i < deaths.Count; i++)
        {
            var death = deaths[i];
            var killer = death.KillingBlow is { } blow ? $"killed by {KeyName(blow.AbilityKey, false)} ({blow.SourceName})" : "died";
            var label = $"{Format.Duration(TimeSpan.FromSeconds(death.SecondsIntoFight))}  ·  {killer}###death{i}";
            if (!ImGui.CollapsingHeader(label, i == deaths.Count - 1 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
                continue;
            DrawDeath(death, i, iconSize);
            ImGui.Spacing();
        }

        ImGui.EndChild();
    }

    private void DrawDeath(DeathRecord death, int index, float iconSize)
    {
        string[] headers = ["Time", "Event", "Source", "Amount", "HP after"];
        var tableLeft = ImGui.GetCursorScreenPos().X;
        var tableWidth = ImGui.GetContentRegionAvail().X;
        if (!ImGui.BeginTable($"##death{index}", headers.Length, ImGuiTableFlags.SizingStretchProp))
            return;
        for (var i = 0; i < headers.Length; i++)
            ImGui.TableSetupColumn(headers[i], i == 1 ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed);
        Widgets.HeaderRow(headers, tableLeft, tableWidth, rightAlignNumbers: false);

        var textOffset = (iconSize - ImGui.GetTextLineHeight()) / 2;
        foreach (var e in death.Events)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, iconSize + (ImGui.GetStyle().CellPadding.Y * 2));

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
            ImGui.TextColored(Theme.Muted, $"−{e.SecondsBeforeDeath:0.0}s");

            ImGui.TableNextColumn();
            DrawKeyIcon(e.AbilityKey, iconSize);
            ImGui.SameLine(0, 6);
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
            ImGui.TextColored(Theme.Text, KeyName(e.AbilityKey, e.Kind == RecapKind.Heal));
            KeyTooltip(e.AbilityKey);

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
            ImGui.TextColored(Theme.Muted, e.SourceName);

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
            var (amount, colour) = e.Kind switch
            {
                RecapKind.Heal => ($"+{e.Amount:N0}", Theme.Clear),
                RecapKind.Miss => ("Miss", Theme.Dim),
                _ => ($"−{e.Amount:N0}", Theme.Wipe),
            };
            var marks = string.Concat(e.Crit ? " crit" : "", e.DirectHit ? " DH" : "", e.Parried ? " parry" : "", e.Blocked ? " block" : "");
            ImGui.TextColored(colour, amount);
            if (marks.Length > 0)
            {
                ImGui.SameLine(0, 4);
                ImGui.TextColored(Theme.Muted, marks.Trim());
            }

            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
            DrawHpBar(e.HpAfter, e.MaxHp);
        }

        ImGui.EndTable();
    }

    /// <summary>A small HP bar with the percentage; red under 25%. "—" when HP wasn't known.</summary>
    private static void DrawHpBar(uint? hp, uint max)
    {
        if (hp is not { } current || max == 0)
        {
            ImGui.TextColored(Theme.Dim, "—");
            return;
        }

        var fraction = Math.Clamp((float)current / max, 0f, 1f);
        var lineHeight = ImGui.GetTextLineHeight();
        var barSize = new Vector2(70, 5);
        var start = ImGui.GetCursorScreenPos() + new Vector2(0, (lineHeight - barSize.Y) / 2);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + barSize, Theme.U32(Theme.Surface), 2f);
        if (fraction > 0)
            drawList.AddRectFilled(start, start + new Vector2(barSize.X * fraction, barSize.Y), Theme.U32(fraction < 0.25f ? Theme.Wipe : Theme.Clear), 2f);
        ImGui.Dummy(new Vector2(barSize.X, lineHeight));
        ImGui.SameLine(0, 6);
        ImGui.TextColored(Theme.Muted, $"{fraction * 100:0}%");
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

        DrawKeyIcon(ability.ActionId, iconSize);
        ImGui.SameLine(0, 7);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((iconSize - ImGui.GetTextLineHeight()) / 2));
        var name = KeyName(ability.ActionId, isHeal);
        ImGui.TextColored(Theme.Text, ability.PetName != null ? $"{name} ({ability.PetName})" : name);
        KeyTooltip(ability.ActionId);

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

    /// <summary>The statuses behind a tick key: one for a single DoT / HoT, several for a combination.</summary>
    private IReadOnlyList<uint> StatusesFor(uint key) =>
        AbilityStats.IsStatusKey(key) ? [AbilityStats.StatusIdOf(key)]
        : AbilityStats.IsComboKey(key) && fight != null && fight.StatusCombos.TryGetValue(key, out var ids) ? ids
        : [];

    /// <summary>
    /// Skill icon for actions, status icon for one DoT / HoT. Combined ticks show each status icon, smaller and
    /// overlapping diagonally, inside the same square so names stay aligned. Unknown ticks get an empty square.
    /// </summary>
    private void DrawKeyIcon(uint key, float size)
    {
        if (!AbilityStats.IsTick(key))
        {
            Widgets.GameIcon(plugin.Names.ActionIcon(key), size);
            return;
        }
        var statuses = StatusesFor(key);
        if (statuses.Count <= 1)
        {
            Widgets.GameIcon(statuses.Count == 1 ? plugin.Names.Status(statuses[0]).Icon : 0, size);
            return;
        }

        var start = ImGui.GetCursorPos();
        var small = size * 0.72f;
        var step = (size - small) / (statuses.Count - 1);
        for (var i = 0; i < statuses.Count; i++)
        {
            ImGui.SetCursorPos(start + new Vector2(step * i, step * i));
            Widgets.GameIcon(plugin.Names.Status(statuses[i]).Icon, small);
        }
        ImGui.SetCursorPos(start);
        ImGui.Dummy(new Vector2(size));
    }

    /// <summary>
    /// "Glare III"; "Dia (DoT)" for one DoT's ticks; "DoT ticks (Caustic Bite + Stormbite)" for combined ticks;
    /// "DoT ticks" when no status could be identified.
    /// </summary>
    private string KeyName(uint key, bool isHeal)
    {
        if (!AbilityStats.IsTick(key))
        {
            var action = plugin.Names.Action(key);
            return string.IsNullOrEmpty(action) ? $"Unknown ({key})" : action;
        }
        var kind = isHeal ? "HoT" : "DoT";
        var statuses = StatusesFor(key);
        return statuses.Count switch
        {
            0 => $"{kind} ticks",
            1 => $"{plugin.Names.Status(statuses[0]).Name} ({kind})",
            _ => $"{kind} ticks ({string.Join(" + ", statuses.Select(id => plugin.Names.Status(id).Name))})",
        };
    }

    private void KeyTooltip(uint key)
    {
        var statuses = StatusesFor(key);
        if (statuses.Count > 1 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", statuses.Select(id => plugin.Names.Status(id).Name)));
    }

    private CombatantRow? CurrentRow() =>
        fight == null
            ? null
            : FightView.Summarize(fight, DateTime.UtcNow, plugin.Configuration.MergePets).Rows.FirstOrDefault(r => r.Id == rowId);
}
