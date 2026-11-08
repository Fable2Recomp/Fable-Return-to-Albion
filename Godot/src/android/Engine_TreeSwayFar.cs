using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// `prop_sway_far_metres`: past this distance a tree copy skips the title's tree wind
/// (fable_tree_sway.gdshaderinc), settling over the last eighth of it. 0, the default, is off.
/// </summary>
/// <remarks>
/// WHY. The sway is a hash, two divides and six sines per vertex of every tree copy, in the
/// position path, so the binning pass pays it too, and at 150 m on 432 lines it moves a tree
/// about a pixel. A runtime branch on a per-copy distance: it skips executed vertex work, which
/// is what the A/B is for (on the Adreno 640 a skipped FRAGMENT branch measured nothing).
/// Lite builds only; the global is set either way and the full shaders never read it. Live:
/// a `cvar_set` of the key sets the global at once.
/// </remarks>
public static class Engine_TreeSwayFar
{
	public const string Key = "prop_sway_far_metres";

	private static readonly StringName Global = "fable_tree_sway_far";

	private static bool _hooked;

	/// <summary>The reach a value means: off for 0, negatives and non-numbers. Pure.</summary>
	public static float Reach(float metres) => metres > 0f && float.IsFinite(metres) ? metres : 0f;

	/// <summary>Sets the global from the config, and follows live changes from then on. Main thread.</summary>
	public static void Apply()
	{
		float metres = 0f;
		try
		{
			metres = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), Key, 0f);
		}
		catch (Exception)
		{
			metres = 0f;
		}

		RenderingServer.GlobalShaderParameterSet(Global, Reach(metres));
		if (!_hooked)
		{
			_hooked = true;
			Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
			{
				if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase))
				{
					Apply();
				}
			};
		}
	}
}
