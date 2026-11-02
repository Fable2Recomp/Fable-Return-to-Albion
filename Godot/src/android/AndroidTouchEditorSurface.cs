using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The touch editor's full-screen layer under its toolbar: outlines every movable control, the picked one
/// in gold, and keeps the pointer from the game's UI beneath. Its own file and class, not nested in
/// <see cref="AndroidTouchEditor"/>: Godot calls a C# node's _Draw and _GuiInput only on a class it has
/// registered as a script, and a nested class is not one.
/// </summary>
public partial class AndroidTouchEditorSurface : Control
{
    /// <summary>The editor whose controls and pick this layer shows.</summary>
    public AndroidTouchEditor? Editor { get; set; }

    /// <summary>Takes the pointer's clicks and scrolls, so nothing beneath acts on them.</summary>
    public override void _GuiInput(InputEvent inputEvent) => AcceptEvent();

    public override void _Draw()
    {
        if (Editor is null) return;
        foreach (TouchControl control in Editor.Editable())
        {
            // Every movable control tinted and ringed, so the faint controls (a low opacity) show
            // which ones move; the picked one in gold.
            bool picked = control.Id == Editor.Picked;
            Color edge = picked ? new Color(1f, 0.82f, 0.4f) : new Color(1f, 1f, 1f, 0.9f);
            Color fill = picked ? new Color(1f, 0.82f, 0.4f, 0.25f) : new Color(1f, 1f, 1f, 0.12f);
            if (control.Round)
            {
                float radius = control.Bounds.Size.X * 0.5f;
                DrawCircle(control.Bounds.GetCenter(), radius, fill);
                DrawArc(control.Bounds.GetCenter(), radius, 0f, Mathf.Tau, 48, edge, picked ? 5f : 3f);
            }
            else
            {
                DrawRect(control.Bounds, fill);
                DrawRect(control.Bounds, edge, false, picked ? 5f : 3f);
            }
        }
    }
}
