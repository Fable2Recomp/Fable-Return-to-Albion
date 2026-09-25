using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// Steps every in-tree <see cref="FableFx"/> from ONE <c>_Process</c>, under one "fablefx"
/// phase, instead of one callback and one stopwatch per system.
///
/// Registration is static and main-thread: a system registers on <c>_EnterTree</c> and leaves
/// on <c>_ExitTree</c>. While a driver is in the tree the systems' own <c>_Process</c> is off
/// and this node calls <see cref="FableFx.Step"/> in registration order; with no driver in the
/// tree (the editor, the viewer tools) every system processes itself, unchanged.
///
/// Frame position: default priority, mounted as a child of <c>Refii.Game.GameFx</c>. Godot
/// runs equal-priority nodes in tree order, parent before child, so the step comes after
/// GameWorld has placed the camera and after GameFx has placed the live nodes — both are
/// inputs to the step — and before the root-level halo feed and the priority-1000
/// displacement pass, which read what the step wrote. A raised priority would move the step
/// behind the halo feed and lag every glow sprite by a frame; this position does not.
/// </summary>
public sealed partial class Engine_FxDriver : Node
{
    private static readonly List<FableFx> Systems = new();

    private static Engine_FxDriver? _current;

    /// <summary>Whether a driver is in the tree and owns the stepping.</summary>
    public static bool Present =>
        _current is not null && GodotObject.IsInstanceValid(_current) && _current.IsInsideTree();

    /// <summary>
    /// Adds a system and sets its self-processing to match the driver's presence. Idempotent;
    /// safe to call again from <c>_Ready</c>, where Godot has just re-enabled processing.
    /// </summary>
    internal static void Register(FableFx fx)
    {
        if (!Systems.Contains(fx))
        {
            Systems.Add(fx);
        }

        fx.SetProcess(!Present);
    }

    internal static void Unregister(FableFx fx) => Systems.Remove(fx);

    /// <summary>
    /// Debug lever, A/B only: shows or hides every live system. A hidden node's meshes leave
    /// Godot's spatial index, which is what the lantern shadow passes search; its light goes too.
    /// </summary>
    public static void SetAllVisible(bool visible)
    {
        foreach (FableFx fx in Systems)
        {
            if (GodotObject.IsInstanceValid(fx))
            {
                fx.Visible = visible;
            }
        }
    }

    public override void _EnterTree()
    {
        _current = this;
        SetSelfStepping(false);
    }

    public override void _ExitTree()
    {
        if (_current != this)
        {
            return;
        }

        _current = null;
        SetSelfStepping(true);
    }

    private static void SetSelfStepping(bool on)
    {
        for (int i = Systems.Count - 1; i >= 0; i--)
        {
            FableFx fx = Systems[i];
            if (!GodotObject.IsInstanceValid(fx))
            {
                Systems.RemoveAt(i);
                continue;
            }

            fx.SetProcess(on);
        }
    }

    /// <summary>Effects actually stepped last frame, against FableFx.FrozenThisFrame.</summary>
    public static int Stepped;

    public override void _Process(double delta)
    {
        using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fablefx");

        // Resolved once for the whole list; every system shares the same camera.
        Camera3D? camera = GetViewport()?.GetCamera3D();
        Stepped = 0;

        for (int i = 0; i < Systems.Count; i++)
        {
            FableFx fx = Systems[i];
            if (!GodotObject.IsInstanceValid(fx))
            {
                Systems.RemoveAt(i--);
                continue;
            }

            if (!fx.IsInsideTree())
            {
                continue;
            }

            fx.Step(delta, camera);
            ++Stepped;
        }
    }
}
