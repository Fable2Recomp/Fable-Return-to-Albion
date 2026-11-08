using Godot;
using Refii.Ui;

namespace Refii.AndroidRuntime;

/// <summary>
/// The move stick on canvas items of its own, above the rest of the touch controls: the base and
/// its rings on this one, the knob on a child. A thumb moves the knob every frame, and moving an
/// item builds nothing, where redrawing the controls builds every circle and plate of them again.
/// </summary>
/// <remarks>
/// A Node2D, not a Control: nothing may snap the knob to a whole pixel, as nothing did when it was
/// drawn in one with the rest.
/// </remarks>
public partial class AndroidTouchStick : Node2D
{
    private readonly Node2D _knob = new() { Name = "Knob", Visible = false };
    private TouchStickLook _look, _baseDrawn, _knobDrawn;
    private Vector2 _knobAt;

    public override void _Ready()
    {
        _knob.Draw += DrawKnobItem;
        AddChild(_knob);
    }

    /// <summary>Follows the stick as it stands this frame, redrawing only what changed.</summary>
    /// <param name="look">How it is drawn; no origin hides it.</param>
    /// <param name="knob">The knob's offset from the origin.</param>
    public void Follow(in TouchStickLook look, Vector2 knob)
    {
        _look = look;
        _knob.Visible = look.Origin is not null;
        if (look.Origin is { } origin && origin + knob != _knobAt)
        {
            _knobAt = origin + knob;
            _knob.Position = _knobAt;
        }

        // Hidden nothing is drawn, and showing an item draws it again by itself.
        if (IsVisibleInTree() && look != _baseDrawn)
        {
            QueueRedraw();
        }

        if (_knob.IsVisibleInTree() && look.KnobOnly != _knobDrawn)
        {
            _knob.QueueRedraw();
        }
    }

    public override void _Draw()
    {
        _baseDrawn = _look;
        if (_look.Origin is { } origin)
        {
            DrawBase(this, origin, _look);
        }
    }

    private void DrawKnobItem()
    {
        _knobDrawn = _look.KnobOnly;
        DrawKnob(_knob, Vector2.Zero, _look);
    }

    /// <summary>The stick's base, its ring and the sprint ring, lit once the thumb is past it.</summary>
    public static void DrawBase(CanvasItem on, Vector2 origin, in TouchStickLook look)
    {
        Color ring = Engine_UiKeyGlyphs.TrimColour with { A = look.Alpha * 0.9f };
        on.DrawCircle(origin, look.Radius, Engine_UiKeyGlyphs.PlateColour with { A = look.Alpha * 0.55f });
        on.DrawArc(origin, look.Radius, 0f, Mathf.Tau, 64, ring, 2.5f * look.Unit, true);
        on.DrawArc(origin, look.Radius * AndroidTouchPad.SprintReach, 0f, Mathf.Tau, 64,
            ring with { A = ring.A * (look.Sprinting ? 1f : 0.3f) }, (look.Sprinting ? 4f : 2f) * look.Unit, true);
    }

    /// <summary>The stick's knob.</summary>
    public static void DrawKnob(CanvasItem on, Vector2 at, in TouchStickLook look)
    {
        on.DrawCircle(at, look.Radius * 0.45f, Engine_UiKeyGlyphs.PlateColour with { A = look.Alpha * 0.9f });
        on.DrawArc(at, look.Radius * 0.45f, 0f, Mathf.Tau, 48, Engine_UiKeyGlyphs.TrimColour with { A = look.Alpha }, 2.5f * look.Unit, true);
    }
}
