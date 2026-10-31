using System;
using Godot;

namespace Refii.Titles.Fable2;

/// <summary>The mirror under the touch controls: the drag that turns the hero, the back gesture, the panel's place.</summary>
public partial class GameMorphMirror
{
    /// <summary>The turn a touchscreen drag has asked for since the last frame, as mouse pixels.</summary>
    private static float _touchTurn;

    /// <summary>
    /// A drag on the touchscreen, as a fraction of the screen's height, turning the hero as the
    /// mouse's right-drag does: a swipe the screen's height is about half a turn.
    /// </summary>
    public static void PostTouchTurn(float screens) => _touchTurn += screens * TouchTurnPerScreen;

    private static bool _backRequested;

    /// <summary>Presses the mirror's B once, from outside the pad: the phone's back gesture.</summary>
    public static void RequestBack() => _backRequested = true;

    /// <summary>Mouse pixels a screen-height touch drag stands for (Engine_MorphMirrorSettings turns 0.008 rad each).</summary>
    public const float TouchTurnPerScreen = 400f;

    /// <summary>
    /// The open mirror panel's rectangle in the root viewport's units, for a touch layer to leave a
    /// finger there to the panel; null while the mirror is closed or closing.
    /// </summary>
    public static Rect2? PanelRect => _current is { _settings: not null, _closing: false } mirror
        ? PanelRectFor(mirror.GetViewport().GetVisibleRect().Size)
        : null;

    /// <summary>Where the panel stands in a viewport of <paramref name="size"/>, matching <see cref="Resize"/>: at the left edge.</summary>
    public static Rect2 PanelRectFor(Vector2 size)
    {
        float width = Math.Max(Engine_UiMorphMirror.Width + 40, 720 * size.X / Math.Max(1, size.Y));
        float unit = width == Engine_UiMorphMirror.Width + 40 ? size.X / width : size.Y / 720f;
        return new Rect2(20f * unit, (size.Y - Engine_UiMorphMirror.Height * unit) / 2f,
            Engine_UiMorphMirror.Width * unit, Engine_UiMorphMirror.Height * unit);
    }
}
