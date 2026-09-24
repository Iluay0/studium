using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Studium.Ui;

/// <summary>
/// Studium's own fixed palette (approved mockup, 2026-09-24). Windows push it around their contents so the
/// user's Dalamud theme doesn't leak in; the Dalamud title bar is left alone.
/// </summary>
public static class Theme
{
    public static readonly Vector4 Window = Rgb(0x121419);
    public static readonly Vector4 Surface = Rgb(0x1A1D23);
    public static readonly Vector4 SurfaceHover = Rgb(0x22262D);
    public static readonly Vector4 SurfaceActive = Rgb(0x2A2F37);
    public static readonly Vector4 Line = Rgb(0x262A31);
    public static readonly Vector4 Border = Rgb(0x2B2F37);
    public static readonly Vector4 Text = Rgb(0xD9DDE4);
    public static readonly Vector4 Bright = Rgb(0xEEF1F5);
    public static readonly Vector4 Muted = Rgb(0x8B929D);
    public static readonly Vector4 Dim = Rgb(0x5B616B);
    public static readonly Vector4 Accent = Rgb(0x7FB8C2);
    public static readonly Vector4 Clear = Rgb(0x74C07A);
    public static readonly Vector4 Wipe = Rgb(0xD8736A);
    public static readonly Vector4 Hover = new(1, 1, 1, 0.045f);
    public static readonly Vector4 Transparent = Vector4.Zero;

    /// <summary>0xRRGGBB → ImGui colour.</summary>
    public static Vector4 Rgb(uint rgb, float alpha = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    public static uint U32(Vector4 colour) => ImGui.GetColorU32(colour);

    private static readonly (ImGuiCol Col, Vector4 Value)[] Colours =
    [
        (ImGuiCol.WindowBg, Window with { W = 0.9f }),
        (ImGuiCol.ChildBg, Transparent),
        (ImGuiCol.PopupBg, Window with { W = 0.98f }),
        (ImGuiCol.Border, Border),
        (ImGuiCol.Text, Text),
        (ImGuiCol.TextDisabled, Muted),
        (ImGuiCol.FrameBg, Surface),
        (ImGuiCol.FrameBgHovered, SurfaceHover),
        (ImGuiCol.FrameBgActive, SurfaceActive),
        (ImGuiCol.Button, Surface),
        (ImGuiCol.ButtonHovered, SurfaceHover),
        (ImGuiCol.ButtonActive, SurfaceActive),
        (ImGuiCol.Header, Surface),
        (ImGuiCol.HeaderHovered, SurfaceHover),
        (ImGuiCol.HeaderActive, SurfaceActive),
        (ImGuiCol.Separator, Line),
        (ImGuiCol.SeparatorHovered, Line),
        (ImGuiCol.SeparatorActive, Accent),
        (ImGuiCol.CheckMark, Accent),
        (ImGuiCol.SliderGrab, Accent with { W = 0.8f }),
        (ImGuiCol.SliderGrabActive, Accent),
        (ImGuiCol.ScrollbarBg, Transparent),
        (ImGuiCol.ScrollbarGrab, SurfaceActive),
        (ImGuiCol.ScrollbarGrabHovered, Rgb(0x353A43)),
        (ImGuiCol.ScrollbarGrabActive, Rgb(0x3F4550)),
        (ImGuiCol.ResizeGrip, Transparent),
        (ImGuiCol.ResizeGripHovered, Accent with { W = 0.4f }),
        (ImGuiCol.ResizeGripActive, Accent with { W = 0.7f }),
        (ImGuiCol.Tab, Surface),
        (ImGuiCol.TabHovered, SurfaceHover),
        (ImGuiCol.TabActive, SurfaceActive),
        (ImGuiCol.TabUnfocused, Surface),
        (ImGuiCol.TabUnfocusedActive, SurfaceActive),
        (ImGuiCol.TableHeaderBg, Transparent),
        (ImGuiCol.TableBorderStrong, Line),
        (ImGuiCol.TableBorderLight, Line),
        (ImGuiCol.TableRowBg, Transparent),
        (ImGuiCol.TableRowBgAlt, new Vector4(1, 1, 1, 0.02f)),
        (ImGuiCol.TextSelectedBg, Accent with { W = 0.3f }),
        (ImGuiCol.NavHighlight, Accent),
    ];

    /// <summary>Pushes the palette and shape settings; dispose (after the window ends) to pop them.</summary>
    public static Scope Push()
    {
        foreach (var (col, value) in Colours)
            ImGui.PushStyleColor(col, value);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 10f);
        return new Scope(Colours.Length, 6);
    }

    public readonly struct Scope(int colours, int vars) : IDisposable
    {
        public void Dispose()
        {
            ImGui.PopStyleVar(vars);
            ImGui.PopStyleColor(colours);
        }
    }

    /// <summary>A window wrapped in the Studium theme: pushes before Begin, pops after End.</summary>
    public abstract class ThemedWindow(string name, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        : Dalamud.Interface.Windowing.Window(name, flags)
    {
        private Scope? scope;

        public override void PreDraw()
        {
            base.PreDraw();
            scope = Push();
        }

        public override void PostDraw()
        {
            scope?.Dispose();
            scope = null;
            base.PostDraw();
        }
    }
}
