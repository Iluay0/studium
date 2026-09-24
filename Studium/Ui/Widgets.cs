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
    /// <returns>Screen Y of the line under the header: where the table's scrolling body starts.</returns>
    public static float HeaderRow(IReadOnlyList<string> headers, float tableLeft, float tableWidth, bool rightAlignNumbers = true)
    {
        ImGui.TableNextRow();
        for (var i = 0; i < headers.Count; i++)
        {
            ImGui.TableSetColumnIndex(i);
            HeaderText(headers[i], rightAlignNumbers && i > 0);
        }
        var y = ImGui.GetItemRectMax().Y + (ImGui.GetStyle().CellPadding.Y);
        ImGui.GetWindowDrawList().AddLine(new Vector2(tableLeft, y), new Vector2(tableLeft + tableWidth, y), Theme.U32(Theme.Line));
        return y;
    }

    /// <summary>
    /// The visible part of a scrolling table's body: below its header, left of its scrollbar, inside its
    /// scroll area. Call between BeginTable and EndTable (ImGui then reports the table's own scroll window).
    /// Drawing that spans columns has to be clipped to this by hand, or it spills out when scrolled.
    /// </summary>
    public static (Vector2 Min, Vector2 Max) TableBodyRect(float headerBottom)
    {
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var scrollbar = ImGui.GetScrollMaxY() > 0 ? ImGui.GetStyle().ScrollbarSize : 0;
        return (new Vector2(position.X, headerBottom), new Vector2(position.X + size.X - scrollbar, position.Y + size.Y));
    }

    /// <summary>Pushes a clip rect for <paramref name="min"/>..<paramref name="max"/>, limited to <paramref name="bounds"/>.</summary>
    public static void PushClip(Vector2 min, Vector2 max, (Vector2 Min, Vector2 Max) bounds) =>
        ImGui.PushClipRect(Vector2.Max(min, bounds.Min), Vector2.Max(Vector2.Max(min, bounds.Min), Vector2.Min(max, bounds.Max)), false);

    /// <summary>Flat text tabs with an accent underline on the selected one. Returns the (possibly new) selection.</summary>
    public static int FlatTabs(string id, IReadOnlyList<string> labels, int selected)
    {
        var drawList = ImGui.GetWindowDrawList();
        var lineHeight = ImGui.GetTextLineHeight();
        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 14);
            var size = new Vector2(ImGui.CalcTextSize(labels[i]).X, lineHeight + 4);
            var start = ImGui.GetCursorScreenPos();
            if (ImGui.InvisibleButton($"{id}{i}", size))
                selected = i;
            var colour = i == selected || ImGui.IsItemHovered() ? Theme.Text : Theme.Dim;
            DrawText(drawList, start, U32(colour), labels[i]);
            if (i == selected)
                drawList.AddRectFilled(new Vector2(start.X, start.Y + size.Y - 2), new Vector2(start.X + size.X, start.Y + size.Y), U32(Theme.Accent));
        }
        return selected;
    }

    private static uint U32(Vector4 colour) => Theme.U32(colour);

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

    /// <summary>
    /// A game icon inside a square of the given size, keeping its aspect ratio (status icons are taller
    /// than wide). An empty square when there's no icon, so neighbouring text stays aligned.
    /// </summary>
    public static void GameIcon(uint iconId, float size)
    {
        var start = ImGui.GetCursorPos();
        if (iconId != 0)
        {
            var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
            var scale = icon.Width > 0 && icon.Height > 0 ? size / Math.Max(icon.Width, icon.Height) : 1f;
            var drawn = icon.Width > 0 && icon.Height > 0 ? new Vector2(icon.Width * scale, icon.Height * scale) : new Vector2(size);
            ImGui.SetCursorPos(start + ((new Vector2(size) - drawn) / 2));
            ImGui.Image(icon.Handle, drawn);
        }
        ImGui.SetCursorPos(start);
        ImGui.Dummy(new Vector2(size));
    }

    /// <summary>
    /// A game icon drawn at a given height, as wide as its own proportions make it (status icons are 3:4).
    /// Returns whether it's hovered, for tooltips.
    /// </summary>
    public static bool IconAtHeight(uint iconId, float height)
    {
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).GetWrapOrEmpty();
        var width = icon.Height > 0 ? height * icon.Width / icon.Height : height * 0.75f;
        ImGui.Image(icon.Handle, new Vector2(width, height));
        return ImGui.IsItemHovered();
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
