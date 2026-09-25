using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Studium.Core;
using Studium.Core.Combat;
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

    /// <summary>
    /// Opens the breakdown for a meter row on the tab it was clicked from (DPS, Tank or Heal);
    /// <paramref name="deaths"/> opens it on Deaths.
    /// </summary>
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
        var what = showDeaths ? "Deaths" : tab switch
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

        // One tab per meter tab plus Deaths, so one player's damage, tanking and healing are a click apart.
        var deathCount = fight.Deaths.Count(d => d.VictimId == rowId);
        var current = showDeaths ? 3 : tab switch { MeterTab.Tank => 1, MeterTab.Heal => 2, _ => 0 };
        var selected = Widgets.FlatTabs("##breakdownTabs", ["DPS", "Tank", "Heal", deathCount > 0 ? $"Deaths ({deathCount})" : "Deaths"], current);
        showDeaths = selected == 3;
        if (!showDeaths)
            tab = selected switch { 1 => MeterTab.Tank, 2 => MeterTab.Heal, _ => MeterTab.Dps };
        ImGui.Spacing();

        if (showDeaths)
        {
            DrawDeaths();
            return;
        }
        DrawSummary(row);
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
        var groups = GroupDots(abilities);
        var top = groups.Count > 0 ? Math.Max(groups.Max(g => g.Total), 1) : 1;
        var lineHeight = ImGui.GetTextLineHeight();
        // Icons at 1.5× the text height: bigger icons without a bigger font; rows grow taller, not wider.
        var iconSize = MathF.Round(lineHeight * IconScale);

        // A skill and its DoT share one gauge, under the DoT row; everything is sorted by its (combined) total.
        foreach (var (main, dot, total) in groups)
        {
            var fraction = (float)total / top;
            DrawAbility(main, isHeal, colour, dot == null ? fraction : null, false, tableLeft, tableWidth, iconSize, body);
            if (dot != null)
                DrawAbility(dot, isHeal, colour, fraction, true, tableLeft, tableWidth, iconSize, body);
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Pairs each DoT (HoT) with the skill that applies it (Stormbite with Stormbite's DoT), when both are in the list.
    /// One whose skill isn't there (applied before the fight) stays on its own as "Stormbite (DoT)".
    /// </summary>
    private static List<(AbilityRow Main, AbilityRow? Dot, long Total)> GroupDots(IReadOnlyList<AbilityRow> abilities)
    {
        var skills = abilities.Where(a => !a.IsTick && a.PetName == null).ToDictionary(a => a.ActionId);
        var dotOf = new Dictionary<uint, AbilityRow>();
        foreach (var dot in abilities.Where(a => a is { IsEstimated: true, LinkedActionId: not 0, PetName: null }))
        {
            if (skills.ContainsKey(dot.LinkedActionId))
                dotOf.TryAdd(dot.LinkedActionId, dot);
        }

        var paired = dotOf.Values.ToHashSet();
        return abilities
            .Where(a => !paired.Contains(a))
            .Select(a => a.IsTick || a.PetName != null || !dotOf.TryGetValue(a.ActionId, out var dot)
                ? (a, (AbilityRow?)null, a.Total)
                : (a, dot, a.Total + dot.Total))
            .OrderByDescending(g => g.Item3)
            .ToList();
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

    private enum DeathColumn
    {
        Time,
        Event,
        Source,
        Amount,
        Hp,
        OnEnemy,
        Buffs,
    }

    private static readonly (DeathColumn Column, string Header)[] DeathColumns =
    [
        (DeathColumn.Time, "Time"), (DeathColumn.Event, "Event"), (DeathColumn.Source, "Source"), (DeathColumn.Amount, "Amount"),
        (DeathColumn.Hp, "HP after"), (DeathColumn.OnEnemy, "On enemy"), (DeathColumn.Buffs, "Buffs"),
    ];

    /// <summary>
    /// One death's events. Like the meter, it fits the window: Event and Source take the spare width and cut
    /// their text; the other columns keep their content width; when even that doesn't fit, columns drop from
    /// the right (Buffs first). Time and Event always stay.
    /// </summary>
    private void DrawDeath(DeathRecord death, int index, float iconSize)
    {
        var tableLeft = ImGui.GetCursorScreenPos().X;
        var tableWidth = ImGui.GetContentRegionAvail().X;
        var columns = FittingDeathColumns(death, iconSize, tableWidth);

        if (!ImGui.BeginTable($"##death{index}_{columns.Count}", columns.Count, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings))
            return;
        foreach (var (column, header, width) in columns)
        {
            if (column is DeathColumn.Event or DeathColumn.Source)
                ImGui.TableSetupColumn(header, ImGuiTableColumnFlags.WidthStretch, column == DeathColumn.Event ? 1.4f : 1f);
            else
                ImGui.TableSetupColumn(header, ImGuiTableColumnFlags.WidthFixed, width);
        }
        Widgets.HeaderRow(columns.Select(c => c.Header).ToList(), tableLeft, tableWidth, rightAlignNumbers: false);

        var textOffset = (iconSize - ImGui.GetTextLineHeight()) / 2;
        foreach (var e in death.Events)
        {
            ImGui.TableNextRow(ImGuiTableRowFlags.None, iconSize + (ImGui.GetStyle().CellPadding.Y * 2));
            foreach (var (column, _, _) in columns)
            {
                ImGui.TableNextColumn();
                if (column is not (DeathColumn.Event or DeathColumn.OnEnemy or DeathColumn.Buffs))
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
                DrawDeathCell(column, e, iconSize, textOffset);
            }
        }

        ImGui.EndTable();
    }

    private void DrawDeathCell(DeathColumn column, RecapEvent e, float iconSize, float textOffset)
    {
        switch (column)
        {
            case DeathColumn.Time:
                ImGui.TextColored(Theme.Muted, TimeText(e));
                break;
            case DeathColumn.Event when e.Kind == RecapKind.Shield:
                // Shield lines are named after the status that brought the shield.
                var statusId = AbilityStats.IsStatusKey(e.AbilityKey) ? AbilityStats.StatusIdOf(e.AbilityKey) : 0;
                if (statusId != 0)
                    Widgets.GameIcon(plugin.Names.Status(statusId).Icon, iconSize);
                else
                    Widgets.GameIcon(0, iconSize);
                ImGui.SameLine(0, 6);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
                FittedText(statusId != 0 ? plugin.Names.Status(statusId).Name : "Shield", Theme.Text);
                break;
            case DeathColumn.Event:
                DrawKeyIcon(e.AbilityKey, iconSize);
                ImGui.SameLine(0, 6);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() + textOffset);
                FittedText(KeyName(e.AbilityKey, e.Kind == RecapKind.Heal), Theme.Text);
                KeyTooltip(e.AbilityKey);
                break;
            case DeathColumn.Source:
                FittedText(e.SourceName, Theme.Muted);
                break;
            case DeathColumn.Amount:
                var (amount, colour) = AmountText(e);
                ImGui.TextColored(colour, amount);
                if (MarksText(e) is { Length: > 0 } marks)
                {
                    ImGui.SameLine(0, 4);
                    ImGui.TextColored(Theme.Muted, marks);
                }
                break;
            case DeathColumn.Hp:
                DrawHpBar(e.HpAfter, e.MaxHp, e.ShieldAfterPercent);
                break;
            case DeathColumn.OnEnemy:
                DrawStatuses(e.Defense?.OnAttacker, iconSize);
                break;
            case DeathColumn.Buffs:
                DrawStatuses(e.Defense?.Target, iconSize);
                break;
        }
    }

    /// <summary>Text cut to the cell's width, with the full text on hover when it was cut.</summary>
    private static void FittedText(string text, Vector4 colour)
    {
        var fitted = Widgets.Truncate(text, ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(colour, fitted);
        if (fitted != text && ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static string TimeText(RecapEvent e) => $"-{e.SecondsBeforeDeath:0.0}s";

    private static (string Text, Vector4 Colour) AmountText(RecapEvent e) => e.Kind switch
    {
        RecapKind.Heal => ($"+{e.Amount:N0}", Theme.Clear),
        RecapKind.Shield => ($"+{e.Amount:N0}", Theme.Accent),
        RecapKind.Miss => ("Miss", Theme.Dim),
        _ => ($"-{e.Amount:N0}", Theme.Wipe),
    };

    private static string MarksText(RecapEvent e) =>
        string.Join(" ", new[] { e.Crit ? "crit" : null, e.DirectHit ? "DH" : null, e.Parried ? "parry" : null, e.Blocked ? "block" : null }.OfType<string>());

    /// <summary>
    /// Which columns fit, with the width each fixed column needs. Event and Source need at least a minimum;
    /// columns drop from the right until everything fits.
    /// </summary>
    private static List<(DeathColumn Column, string Header, float Width)> FittingDeathColumns(DeathRecord death, float iconSize, float available)
    {
        var padding = ImGui.GetStyle().CellPadding.X * 2;
        var statusWidth = (iconSize * 0.75f) + 1; // status icons are 3:4
        float Header(string text) => ImGui.CalcTextSize(text.ToUpperInvariant()).X;
        float Max(Func<RecapEvent, float> measure) => death.Events.Count == 0 ? 0 : death.Events.Max(measure);

        var columns = DeathColumns.Select(c => (c.Column, c.Header, Width: Math.Max(Header(c.Header), c.Column switch
        {
            DeathColumn.Time => Max(e => ImGui.CalcTextSize(TimeText(e)).X),
            DeathColumn.Event => iconSize + 6 + 60, // minimum; it stretches
            DeathColumn.Source => 50,               // minimum; it stretches
            DeathColumn.Amount => Max(e => ImGui.CalcTextSize(AmountText(e).Text).X + (MarksText(e) is { Length: > 0 } m ? 4 + ImGui.CalcTextSize(m).X : 0)),
            DeathColumn.Hp => 70 + 6 + ImGui.CalcTextSize("100%").X,
            DeathColumn.OnEnemy => Math.Max(Max(e => (e.Defense?.OnAttacker.Count ?? 0) * statusWidth), ImGui.CalcTextSize("—").X),
            DeathColumn.Buffs => Math.Max(Max(e => (e.Defense?.Target.Count ?? 0) * statusWidth), ImGui.CalcTextSize("—").X),
            _ => 0,
        }))).ToList();

        while (columns.Count > 2 && columns.Sum(c => c.Width + padding) > available)
            columns.RemoveAt(columns.Count - 1);
        return columns;
    }

    /// <summary>A row of status icons (hover for name and who applied it), or a dash when there are none.</summary>
    private void DrawStatuses(IReadOnlyList<StatusSnapshot>? statuses, float rowHeight)
    {
        if (statuses is not { Count: > 0 })
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((rowHeight - ImGui.GetTextLineHeight()) / 2));
            ImGui.TextColored(Theme.Dim, "—");
            return;
        }

        for (var i = 0; i < statuses.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 1);
            StatusIcon(statuses[i], rowHeight);
        }
    }

    private void StatusIcon(StatusSnapshot status, float height)
    {
        var info = plugin.Names.Status(status.StatusId);
        if (!Widgets.IconAtHeight(plugin.Names.StatusIcon(status.StatusId, status.Stacks), height))
            return;
        var name = status.Stacks > 1 ? $"{info.Name} ×{status.Stacks}" : info.Name;
        ImGui.SetTooltip(string.IsNullOrEmpty(status.SourceName) ? name : $"{name}\nfrom {status.SourceName}");
    }

    /// <summary>
    /// A small HP bar with the percentage (red under 25%), and the shield as a teal segment after the HP,
    /// like the game's party list. "—" when HP wasn't known.
    /// </summary>
    private static void DrawHpBar(uint? hp, uint max, byte shieldPercent)
    {
        if (hp is not { } current || max == 0)
        {
            ImGui.TextColored(Theme.Dim, "—");
            return;
        }

        var fraction = Math.Clamp((float)current / max, 0f, 1f);
        var shield = Math.Clamp(shieldPercent / 100f, 0f, 1f - fraction);
        var lineHeight = ImGui.GetTextLineHeight();
        var barSize = new Vector2(70, 5);
        var start = ImGui.GetCursorScreenPos() + new Vector2(0, (lineHeight - barSize.Y) / 2);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(start, start + barSize, Theme.U32(Theme.Surface), 2f);
        if (fraction > 0)
            drawList.AddRectFilled(start, start + new Vector2(barSize.X * fraction, barSize.Y), Theme.U32(fraction < 0.25f ? Theme.Wipe : Theme.Clear), 2f);
        if (shield > 0)
        {
            var shieldStart = start + new Vector2(barSize.X * fraction, 0);
            drawList.AddRectFilled(shieldStart, shieldStart + new Vector2(barSize.X * shield, barSize.Y), Theme.U32(Theme.Accent), 2f);
        }
        ImGui.Dummy(new Vector2(barSize.X, lineHeight));
        var barHovered = ImGui.IsItemHovered();
        ImGui.SameLine(0, 6);
        ImGui.TextColored(Theme.Muted, $"{fraction * 100:0}%");
        if (shieldPercent > 0 && (barHovered || ImGui.IsItemHovered()))
            ImGui.SetTooltip($"Shield: {shieldPercent}% of max HP");
    }

    private void DrawSummary(CombatantRow row)
    {
        (string Label, string Value)[] stats = tab switch
        {
            MeterTab.Heal => [("HPS", row.Hps.ToString("N0")), ("Total", Format.Compact(row.Healing)), ("Heal", Format.Compact(row.Healing - row.Shielding)),
                ("Shield", Format.Compact(row.Shielding)), ("Overheal", Format.Percent(row.OverhealRate)), ("Crit", Format.Percent(row.HealCritRate))],
            MeterTab.Tank => [("Taken", Format.Compact(row.DamageTaken)), ("Parry", Format.Percent(row.ParryRate)), ("Block", Format.Percent(row.BlockRate)), ("Healed-on", Format.Compact(row.HealingReceived))],
            _ => [("DPS", row.Dps.ToString("N0")), ("Total", Format.Compact(row.Damage)), ("Crit", Format.Percent(row.CritRate)), ("Direct hit", Format.Percent(row.DirectHitRate))],
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

    /// <param name="gauge">Fraction of the top ability to draw along the bottom of the row; null draws none.</param>
    /// <param name="child">A DoT (HoT) row under its skill: indented, smaller icon, named just "DoT" ("HoT").</param>
    private void DrawAbility(AbilityRow ability, bool isHeal, Vector4 colour, float? gauge, bool child, float tableLeft, float tableWidth, float iconSize, (Vector2 Min, Vector2 Max) body)
    {
        var rowIconSize = child ? MathF.Round(iconSize * 0.7f) : iconSize;
        var rowHeight = rowIconSize + (ImGui.GetStyle().CellPadding.Y * 2);
        ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
        ImGui.TableNextColumn();

        // Thin gauge along the bottom of the row, sized against the top ability.
        if (gauge is { } fraction)
        {
            var rowBottom = ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().CellPadding.Y + rowHeight;
            Widgets.PushClip(new Vector2(tableLeft, rowBottom - 2), new Vector2(tableLeft + tableWidth, rowBottom), body);
            ImGui.GetWindowDrawList().AddRectFilled(
                new Vector2(tableLeft, rowBottom - 1.5f), new Vector2(tableLeft + (tableWidth * fraction), rowBottom),
                Theme.U32(colour with { W = 0.8f }));
            ImGui.PopClipRect();
        }

        if (child)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + iconSize - rowIconSize); // icon ends where the skill's does
        DrawKeyIcon(ability.ActionId, rowIconSize);
        ImGui.SameLine(0, 7);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((rowIconSize - ImGui.GetTextLineHeight()) / 2));
        var name = child ? isHeal ? "HoT" : "DoT" : KeyName(ability.ActionId, isHeal);
        ImGui.TextColored(child ? Theme.Muted : Theme.Text, ability.PetName != null ? $"{name} ({ability.PetName})" : name);
        KeyTooltip(ability.ActionId);
        if (ability.IsEstimated && ImGui.IsItemHovered())
            ImGui.SetTooltip(isHeal
                ? "Estimated. The game sends one tick for every HoT on the player, from every healer;\n" +
                  "Studium splits it by each HoT's potency × its owner's healing per potency."
                : "Estimated. The game sends one tick for every DoT on the target, from every player;\n" +
                  "Studium splits it by each DoT's potency × its owner's damage per potency.");

        Number(ability.IsEstimated ? $"~{Format.Compact(ability.Total)}" : Format.Compact(ability.Total), Theme.Bright);
        Number(Format.Percent(ability.Share), Theme.Muted);
        Number(ability.Hits.ToString("N0"), Theme.Muted);
        // Ticks carry no crit / DH data, and absorbed shields can't crit or overheal.
        var isShield = AbilityStats.IsShieldKey(ability.ActionId);
        Number(ability.IsTick || isShield ? "—" : Format.Percent(ability.CritRate), Theme.Muted);
        Number(isShield ? "—" : isHeal ? Format.Percent(ability.OverhealRate) : ability.IsTick ? "—" : Format.Percent(ability.DirectHitRate), Theme.Muted);
        Number(ability.Average.ToString("N0"), Theme.Muted);
        Number(ability.Max.ToString("N0"), Theme.Muted);

        void Number(string text, Vector4 textColour)
        {
            ImGui.TableNextColumn();
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + ((rowIconSize - ImGui.GetTextLineHeight()) / 2));
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
        if (AbilityStats.IsShieldKey(key))
        {
            var statusId = AbilityStats.ShieldStatusOf(key);
            Widgets.GameIcon(statusId == 0 ? 0 : plugin.Names.Status(statusId).Icon, size);
            return;
        }
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
        if (AbilityStats.IsShieldKey(key))
        {
            var statusId = AbilityStats.ShieldStatusOf(key);
            return statusId == 0 ? "Shield (unknown)" : $"{plugin.Names.Status(statusId).Name} (shield)";
        }
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
