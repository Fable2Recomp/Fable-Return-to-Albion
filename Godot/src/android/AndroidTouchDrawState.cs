using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// How the move stick is drawn, on its own canvas item (<see cref="AndroidTouchStick"/>). Where the
/// knob stands is not here: it moves the item, it draws nothing.
/// </summary>
/// <param name="Origin">Where the base stands, or null with no stick to draw.</param>
public readonly record struct TouchStickLook(Vector2? Origin, float Radius, bool Sprinting, float Unit, float Alpha)
{
    /// <summary>What the knob's own drawing depends on: not where the base stands, nor the sprint ring.</summary>
    public TouchStickLook KnobOnly => this with { Origin = null, Sprinting = false };
}

/// <summary>
/// Everything the touch controls' <c>_Draw</c> reads that can change from one frame to the next,
/// compared whole to the last picture's. A control's look comes from its fields, which a new
/// layout replaces (<paramref name="Controls"/>, a new list each time).
/// </summary>
/// <param name="Controls">The layout: bounds, labels, captions, colours and order.</param>
/// <param name="Context">Fainter in anything but the world.</param>
/// <param name="Size">The canvas size, and with <paramref name="Scale"/> the unit everything is sized by.</param>
/// <param name="Opacity">`touch_opacity`.</param>
/// <param name="Density">The window's pixels per canvas unit: the size text is rendered at.</param>
/// <param name="Slots">The HUD's item slots, which the rings stand on.</param>
/// <param name="Spell">The will on the first power level, or -1: the Spells icon and the picker's ring.</param>
/// <param name="Atlas">The controller's button art (Engine_UiKeyGlyphs.PadAtlas), null until it loads.</param>
/// <param name="Picker">Whether the spell picker is open. It can open a frame before its layout.</param>
/// <param name="DPadBits">The arms held, lit on the D-pad and the HUD's rings.</param>
/// <param name="Active">Controls drawn pressed, one bit per index in <paramref name="Controls"/>.</param>
/// <param name="Latched">Controls latched on (the heavier rim), likewise.</param>
/// <param name="Hold">The ring filling round Spells: it changes each frame while it fills.</param>
/// <param name="InlineStick">The stick when this picture draws it: under the open picker. Default otherwise.</param>
/// <param name="InlineKnob">The knob's offset when this picture draws the stick.</param>
public readonly record struct TouchDrawnOverlay(
    IReadOnlyList<TouchControl>? Controls, TouchContext Context, Vector2 Size, float Scale, float Opacity, float Density,
    (Vector2 Centre, float Reach)? Slots, int Spell, object? Atlas, bool Picker, uint DPadBits, ulong Active, ulong Latched,
    float Hold, TouchStickLook InlineStick, Vector2 InlineKnob);

/// <summary>
/// When the touch controls need drawing again. Each circle, arc and rounded plate drawn is a vertex
/// and index buffer made on the render thread and freed at the next redraw, so the picture is kept
/// until something in it has changed. Pure: the node supplies what it holds.
/// </summary>
public static class AndroidTouchDrawState
{
    /// <summary>
    /// The longest the picture stands unredrawn, in seconds: a limit on how long anything this
    /// comparison does not know of could stand stale, not how anything is meant to update.
    /// </summary>
    public const double LongestKept = 1.0;

    /// <summary>A control's resting alpha: a menu's controls are a little lighter than the world's.</summary>
    public static float IdleAlpha(float opacity, TouchContext context) => opacity * (context == TouchContext.World ? 1f : 0.9f);

    /// <summary>What the main canvas item would draw now.</summary>
    public static TouchDrawnOverlay Overlay(AndroidTouchPad pad, Vector2 size, float scale, float opacity, float density,
        (Vector2 Centre, float Reach)? slots, int spell, object? atlas, bool picker, double now)
    {
        ulong active = 0ul, latched = 0ul;
        IReadOnlyList<TouchControl> controls = pad.Controls;
        for (int i = 0; i < controls.Count; ++i)
        {
            TouchControl control = controls[i];
            // A thumb on the stick or a drag on the camera presses nothing drawn, and the D-pad draws from its bits.
            if (control.Kind is TouchControlKind.Stick or TouchControlKind.Look or TouchControlKind.DPad)
            {
                continue;
            }

            if (pad.IsActive(control.Id))
            {
                active |= 1ul << i;
            }

            if (pad.IsLatched(control.Id))
            {
                latched |= 1ul << i;
            }
        }

        TouchStickLook inline = picker ? Look(pad, size, scale, opacity) : default;
        return new TouchDrawnOverlay(controls, pad.Context, size, scale, opacity, density, slots, spell, atlas, picker,
            pad.DPadBits, active, latched, pad.HoldProgress("RT", now), inline, inline.Origin is null ? Vector2.Zero : pad.StickKnob);
    }

    /// <summary>
    /// What the stick's own canvas item would draw now. Nothing while the spell picker is open: its
    /// backdrop darkens the stick, so the main item draws the stick then, under it.
    /// </summary>
    public static TouchStickLook Stick(AndroidTouchPad pad, Vector2 size, float scale, float opacity, bool picker) =>
        picker ? default : Look(pad, size, scale, opacity);

    private static TouchStickLook Look(AndroidTouchPad pad, Vector2 size, float scale, float opacity)
    {
        // A layout with no stick draws none, whatever the pad still holds from before it.
        Vector2? origin = HasStick(pad.Controls) ? pad.StickOrigin : null;
        return new TouchStickLook(origin, pad.StickRadius, origin is not null && pad.Sprinting,
            AndroidTouchLayout.Unit(size, scale), IdleAlpha(opacity, pad.Context));
    }

    private static bool HasStick(IReadOnlyList<TouchControl> controls)
    {
        for (int i = 0; i < controls.Count; ++i)
        {
            if (controls[i].Kind == TouchControlKind.Stick)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the main canvas item must be drawn again this frame.</summary>
    /// <param name="artMissing">The last picture went without art that had not loaded (a spell icon, the game's font): try again.</param>
    /// <param name="sinceDrawn">Seconds since the last picture.</param>
    public static bool NeedsRedraw(in TouchDrawnOverlay now, in TouchDrawnOverlay drawn, bool artMissing, double sinceDrawn) =>
        artMissing || sinceDrawn >= LongestKept || !now.Equals(drawn);
}
