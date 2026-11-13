using System;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_canvas_layout_size`: the window's 2D (and the world's tone-mapped image under it) drawn at
/// the game's layout size, about 1560 x 739, and stretched to the screen by the copy that shows the
/// frame, instead of at the screen's own pixels. The world's resolution stays where it was. Off by default.
/// </summary>
/// <remarks>
/// <para>WHY. On a phone the root window lays its 2D out at the project's 1560 x 720 base
/// (`window/stretch/mode="canvas_items"`, aspect expand) but draws it at the screen's pixels: on a
/// Galaxy S10e 2280 x 1080, 2.46 million of them. Every full-screen step after the world's opaque
/// pass is paid at that size: the Mobile renderer's separate tone-map and upscale pass (a separate
/// pass because the world is scaled, render_forward_mobile.cpp: "can't do blit subpass because we're
/// scaling", 0.45-0.55 ms in the market), each canvas layer's render pass (a load and a store of the
/// whole target, RendererCanvasRenderRD::_render_batch_items; "Render CanvasItems" 1.75-2.1 ms) and
/// the final copy's read. The HUD itself is already drawn at the layout size (GameUi's UiViewport,
/// a SubViewport sized by its stretch container) and only upscaled into the screen-sized canvas.</para>
/// <para>HOW. The root window's content scale mode becomes `viewport`: Godot then sizes the root's
/// render target to the layout size and the final copy to the screen (blit_render_targets_to_screen)
/// stretches it. The layout, input mapping and every Control's size are the same in both modes (both
/// lay out at the content size). The world's buffer scale is raised by the stretch, so its pixels
/// stay as many as the configured render_scale_percent gave against the screen
/// (<see cref="WorldScale"/>), and the mesh LOD threshold is fed the root's new pixel size, which is
/// what Godot normalises it by (Engine_MeshLodPolicy).</para>
/// <para>WHAT DIFFERS. Godot's final copy samples NEAREST (the RD blit's default sampler): the
/// 1.46x stretch on the S10e repeats every second or third pixel row and column instead of
/// blending, so text and the touch controls' edges are a little uneven, and the world is
/// upscaled twice (bilinear in the tone-map pass to the layout size, then the copy). The
/// targeting-highlight and displacement layers, sized from the layout size times the world's
/// buffer scale, are larger by the stretch, which draws the outline thinner on screen.
/// A render_scale_percent above 100 / stretch (about 68 on the S10e) is capped at the layout size.</para>
/// <para>Live: the host applies it on its once-a-second settings read, as the render scale.</para>
/// </remarks>
public static class AndroidCanvasResolution
{
    public const string Key = "android_canvas_layout_size";

    /// <summary>Below this stretch the screen is about the layout's size and there is nothing to save.</summary>
    public const float MinStretch = 1.05f;

    /// <summary>
    /// How many screen pixels one layout pixel covers: the smaller axis ratio, the uniform scale the
    /// expand aspect gives. 1 for an unknown size. Pure.
    /// </summary>
    public static float Stretch(Vector2I window, Vector2 layout)
    {
        if (window.X <= 0 || window.Y <= 0 || layout.X <= 0f || layout.Y <= 0f)
        {
            return 1f;
        }

        return MathF.Min(window.X / layout.X, window.Y / layout.Y);
    }

    /// <summary>Whether the root draws at the layout size: the setting on and the screen enough larger. Pure.</summary>
    public static bool Applies(bool enabled, float stretch) => enabled && stretch >= MinStretch;

    /// <summary>
    /// The world's buffer scale against the root's target: <paramref name="scale"/> (of the screen)
    /// raised by the stretch while the root is at the layout size, so the world has as many pixels;
    /// at most 1. Pure.
    /// </summary>
    public static float WorldScale(float scale, float stretch, bool applies) =>
        applies ? MathF.Min(1f, scale * stretch) : scale;

    /// <summary>
    /// The root viewport's size as the RenderingServer sees it: the layout size while
    /// <paramref name="applies"/>, else the window's. Pure.
    /// </summary>
    public static Vector2I RenderedSize(Vector2I window, Vector2 layout, bool applies) =>
        applies ? new Vector2I(Math.Max(1, (int)MathF.Round(layout.X)), Math.Max(1, (int)MathF.Round(layout.Y))) : window;

    private static Window.ContentScaleModeEnum? _original;

    /// <summary>
    /// Main thread: puts <paramref name="root"/>'s content scale mode where the setting asks. Returns
    /// whether the root draws at the layout size, with the stretch and the root's rendered size.
    /// </summary>
    internal static bool Apply(Window root, bool enabled, out float stretch, out Vector2I rendered)
    {
        Vector2 layout = root.GetVisibleRect().Size;
        stretch = Stretch(root.Size, layout);
        bool applies = Applies(enabled, stretch);
        rendered = RenderedSize(root.Size, layout, applies);
        if (root.ContentScaleMode != Window.ContentScaleModeEnum.Viewport)
        {
            _original = root.ContentScaleMode;
        }

        Window.ContentScaleModeEnum want = applies
            ? Window.ContentScaleModeEnum.Viewport
            : _original ?? Window.ContentScaleModeEnum.CanvasItems;
        if (root.ContentScaleMode != want)
        {
            root.ContentScaleMode = want;
            GD.Print(applies
                ? $"[android] 2D at the layout size {rendered.X}x{rendered.Y}, stretched {stretch:0.00}x to the screen {root.Size.X}x{root.Size.Y}"
                : $"[android] 2D at the screen's size {root.Size.X}x{root.Size.Y}");
        }

        return applies;
    }
}
