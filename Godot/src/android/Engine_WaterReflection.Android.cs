using Godot;

namespace Refii.Level;

public sealed partial class Engine_WaterReflection
{
    /// <summary>The process frame a mirror's water was last found in view.</summary>
    private static ulong _seenFrame;

    /// <summary>
    /// Whether a water body was in view at the last mirror turns, so a mirror may render: what
    /// the mirrors draw is not what the window's camera sees, and a sky element hidden because the
    /// window cannot see it (fx_no_depth_reads) could still be reflected. Two turns of slack.
    /// </summary>
    public static bool WaterInView =>
        Enabled && _seenFrame != 0 &&
        Engine.GetProcessFrames() - _seenFrame <= (ulong)(2 * System.Math.Max(Cadence, 1) + 1);
}
