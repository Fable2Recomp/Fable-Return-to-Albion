using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// <c>android_cloth_land_predraw</c>: the frame's cloth solves are collected and uploaded at
/// RenderingServer.FramePreDraw instead of at the end of the frame's processing.
/// </summary>
/// <remarks>
/// The solve runs on a worker from each body's _Process; Engine_FrameEndNode then waits for it
/// (<c>cloth_land</c>). FramePreDraw comes later on the same thread, after the deferred calls (Godot's
/// skeleton updates), the transform flush and RenderingServer.sync(), and before draw() queues the
/// frame, so the upload still lands in this frame's draw and the solve overlaps that work too. A frame
/// that does not draw lands the solve in the body's next _Process, as before. The same picture.
/// </remarks>
public static class AndroidClothLand
{
    public const string Key = "android_cloth_land_predraw";

    /// <summary>The phase the wait is measured under when it lands at FramePreDraw.</summary>
    public const string Phase = "cloth_land_predraw";

    /// <summary>Whether the cloth lands at FramePreDraw; Engine_FrameEndNode then leaves it.</summary>
    public static bool AtPreDraw { get; private set; }

    /// <summary>Whether refii.toml asks for it; off when unset.</summary>
    public static bool Configured(IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Moves the land to FramePreDraw when configured. Main thread, once at start.</summary>
    public static void Apply(IReadOnlyDictionary<string, string> values)
    {
        if (AtPreDraw || !Configured(values))
            return;
        RenderingServer.FramePreDraw += Land;
        AtPreDraw = true;
    }

    private static void Land()
    {
        using (Engine_FramePhases.Measure(Phase))
        {
            Refii.Physics.Engine_ClothBody.LandAll();
        }
    }
}
