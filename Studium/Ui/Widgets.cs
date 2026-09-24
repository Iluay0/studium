using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;

namespace Studium.Ui;

/// <summary>Small drawing helpers shared by the Studium windows.</summary>
public static class Widgets
{
    private const uint ShadowColour = 0xD9000000; // black at 85%, packed ABGR

    /// <summary>
    /// While set, text gets a 1 px dark shadow so it stays readable over a see-through background
    /// (the meter). Windows set it around their own drawing.
    /// </summary>
    public static bool Shadow { get; set; }

    /// <summary>Coloured text at the cursor, with a shadow when <see cref="Shadow"/> is on.</summary>
    public static void Text(Vector4 colour, string text)
    {
        if (Shadow)
            ImGui.GetWindowDrawList().AddText(ImGui.GetCursorScreenPos() + Vector2.One, ShadowColour, text);
        ImGui.TextColored(colour, text);
    }

    /// <summary>Draw-list text, with a shadow when <see cref="Shadow"/> is on.</summary>
    public static void DrawText(ImDrawListPtr drawList, Vector2 position, uint colour, string text)
    {
        if (Shadow)
            drawList.AddText(position + Vector2.One, ShadowColour, text);
        drawList.AddText(position, colour, text);
    }

    /// <summary>Right-aligns text in the current table cell.</summary>
    public static void RightText(string text, Vector4 colour)
    {
        var width = ImGui.CalcTextSize(text).X;
        var available = ImGui.GetContentRegionAvail().X;
        if (available > width)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - width);
        Text(colour, text);
    }

    /// <summary>Uppercase, dim column header; right-aligned unless it's the first column.</summary>
    public static void HeaderText(string text, bool rightAligned)
    {
        if (rightAligned)
            RightText(text.ToUpperInvariant(), Theme.Dim);
        else
            Text(Theme.Dim, text.ToUpperInvariant());
    }

    /// <summary>
    /// A custom header row plus a line under it (instead of ImGui's header background). It's a normal row,
    /// not flagged as ImGui's header row, because ImGui leaves header rows out when auto-sizing columns.
    /// </summary>
    public static void HeaderRow(IReadOnlyList<string> headers, float tableLeft, float tableWidth, bool rightAlignNumbers = true)
    {
        ImGui.TableNextRow();
        for (var i = 0; i < headers.Count; i++)
        {
            ImGui.TableSetColumnIndex(i);
            HeaderText(headers[i], rightAlignNumbers && i > 0);
        }
        var y = ImGui.GetItemRectMax().Y + (ImGui.GetStyle().CellPadding.Y);
        ImGui.GetWindowDrawList().AddLine(new Vector2(tableLeft, y), new Vector2(tableLeft + tableWidth, y), Theme.U32(Theme.Line));
    }

    /// <summary>Flat icon button: no box until hovered, muted icon that brightens on hover.</summary>
    public static bool FlatIconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        var size = new Vector2(ImGui.GetFrameHeight());
        var start = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
            drawList.AddRectFilled(start, start + size, Theme.U32(Theme.Hover), 4f);

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var glyph = icon.ToIconString();
            var glyphSize = ImGui.CalcTextSize(glyph);
            DrawText(drawList, start + ((size - glyphSize) / 2), Theme.U32(hovered ? Theme.Text : Theme.Muted), glyph);
        }

        if (hovered)
            ImGui.SetTooltip(tooltip);
        return clicked;
    }

    /// <summary>A small coloured chip, e.g. Clear / Wipe.</summary>
    public static void Chip(string text, Vector4 colour)
    {
        var padding = new Vector2(5, 0);
        var size = ImGui.CalcTextSize(text) + (padding * 2);
        var start = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRectFilled(start, start + size, Theme.U32(colour with { W = 0.12f }), 3f);
        ImGui.SetCursorScreenPos(start + padding);
        Text(colour, text);
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(size);
    }

    public static void OutcomeChip(Core.Fights.FightOutcome outcome)
    {
        switch (outcome)
        {
            case Core.Fights.FightOutcome.Clear:
                Chip("Clear", Theme.Clear);
                break;
            case Core.Fights.FightOutcome.Wipe:
                Chip("Wipe", Theme.Wipe);
                break;
            default:
                Text(Theme.Dim, "—");
                break;
        }
    }

    /// <summary>A game icon at a square size, or an empty space of that size if there's none.</summary>
    public static void GameIcon(uint iconId, float size)
    {
        if (iconId == 0)
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        ImGui.Image(icon.Handle, new Vector2(size));
    }

    /// <summary>Cuts text to fit a width, ending in "...".</summary>
    public static string Truncate(string text, float maxWidth)
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
}
