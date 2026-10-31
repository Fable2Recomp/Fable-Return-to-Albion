namespace Refii.Fx;

/// <summary>
/// `fx_cpu_fastpaths`: the effect system's main-thread shortcuts that leave the picture as it was.
/// Off draws through the original loops; the debug channel's `render` op flips it live
/// (`fxFastPaths`), so the two are A/B'd in one process.
/// </summary>
/// <remarks>
/// Main-thread owned, like the emitters it serves. Each shortcut states what it keeps: the emit
/// loop's early rejection, of a particle or of a whole emitter, and the cheaper sort give the same
/// vertex buffers bit for bit; skipping the sort of an order-independent batch gives the same quads
/// in emission order, the same sum; the bounds are the exact box of the same points; the
/// step-alpha and mesh-particle writes leave every drawn value as before.
///
/// The switch and the shortcuts only the fast paths take are in
/// src/android/Engine_FxFastPaths.cs; <see cref="OrderIndependent"/> stays here because both
/// emit paths ask it.
/// </remarks>
internal static partial class Engine_FxFastPaths
{
	/// <summary>
	/// Whether a batch's blend gives the same sum in any order, so back-to-front buys nothing.
	/// `add`, `addone` (and the additive layer it goes to) and `disp` are Godot's blend_add, `sub`
	/// is blend_sub: dst ± src·a with src independent of dst, and none of them writes depth or reads
	/// the screen. A saturating target clamps the same either way for non-negative terms. `mix`,
	/// `mixalpha` and `darken` are order-dependent; `revsub` draws a darken pass first.
	/// </summary>
	/// <remarks>Rounding is per blend, so the sum can differ from back-to-front in its last bit.</remarks>
	internal static bool OrderIndependent(string mode) => mode is "add" or "addone" or "disp" or "sub";
}
