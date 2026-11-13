namespace Refii.Models;

public partial class Engine_ClassicParticles
{
    /// <summary>
    /// The modern stages' define for a draw without a depth fade: under `fx_no_depth_reads` the
    /// build has no depth texture (fx_soft's FX_HARD), whose declaration alone splits Mobile's
    /// render pass. Read when the material is composed; a live change reaches groups composed after it.
    /// </summary>
    internal static string HardDefines(bool soft) =>
        Refii.Fx.Engine_FxDepthReads.ShouldBeHard(Refii.Fx.Engine_FxDepthReads.Enabled, Refii.Fx.Engine_FxDepthReads.HardAll, soft)
            ? "#define " + Refii.Fx.Engine_FxDepthReads.HardDefine + "\n"
            : string.Empty;
}
