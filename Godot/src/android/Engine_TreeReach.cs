using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// The far tree models' reach and level bias (`tree_far_reach`, `tree_lod_bias`), on top of the
/// stated ranges <see cref="Engine_PropDrawRange"/> gives every tree.
/// </summary>
public static class Engine_TreeReach
{
	/// <summary>`tree_far_reach`: the farthest any tree model draws, in metres; 0 keeps the stated reach.</summary>
	public const string TreeFarReachKey = "tree_far_reach";

	/// <summary>`tree_far_reach` as last read: at start-up, then on each live change (Engine_PropStreamer
	/// re-applies every streamed node's range when it changes).</summary>
	public static float TreeFarReach { get; private set; } = ReadTreeFarReach();

	/// <summary>
	/// Re-reads the tree settings a cvar-set can change live, when <paramref name="name"/> is one
	/// of them. True when it was, and every streamed node's range wants re-applying.
	/// </summary>
	internal static bool RefreshLive(string name)
	{
		if (!name.Equals(TreeFarReachKey, StringComparison.OrdinalIgnoreCase) &&
			!name.Equals(TreeLodBiasKey, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		TreeFarReach = ReadTreeFarReach();
		TreeLodBias = ReadTreeLodBias();
		return true;
	}

	/// <summary>
	/// Node metadata on a tree node that draws the tree out to its far reach: its `_LOD1` model, or a
	/// main model with none. A main model that hands over to a `_LOD1` at L does not carry it.
	/// </summary>
	public const string TreeFarModelMeta = "tree_far_model";

	/// <summary>Marks a stamped tree node that draws the tree out to its far reach (<see cref="TreeFarModelMeta"/>).</summary>
	internal static void StampFarModel(GeometryInstance3D node, PropRole role, bool hasLod)
	{
		if (role == PropRole.Lod || !hasLod)
		{
			node.SetMeta(TreeFarModelMeta, true);
		}
	}

	/// <summary>`tree_lod_bias`: Godot's lod_bias on the far tree models; 1 keeps the shared mesh_lod_pixels rule.</summary>
	public const string TreeLodBiasKey = "tree_lod_bias";

	/// <summary>
	/// `tree_lod_bias` as last read. Under 1 a far tree model (<see cref="TreeFarModelMeta"/>) takes
	/// its thinned card levels (tree_card_lod) sooner than the props' rule: 0.5 switches each level
	/// at half the distance. The main models within L of the camera keep the shared rule.
	/// </summary>
	/// <remarks>Trees are the one geometry-heavy category on an Adreno 640, and every level a tree
	/// has drops leaf cards only, so a tree can switch sooner than a prop whose silhouette a
	/// simplifier changes. Godot refuses a bias of zero, so it is held to [0.1, 4].</remarks>
	public static float TreeLodBias { get; private set; } = ReadTreeLodBias();

	/// <summary>Gives a far tree model (<see cref="TreeFarModelMeta"/>) the configured <see cref="TreeLodBias"/>.</summary>
	internal static void ApplyLodBias(GeometryInstance3D instance)
	{
		if (instance.HasMeta(TreeFarModelMeta) && instance.LodBias != TreeLodBias)
		{
			// A far tree's levels drop only leaf cards (tree_card_lod): tree_lod_bias lets them switch sooner.
			instance.LodBias = TreeLodBias;
		}
	}

	private static float ReadTreeLodBias()
	{
		try
		{
			float bias = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), TreeLodBiasKey, 1f);
			return float.IsFinite(bias) && bias > 0f ? Mathf.Clamp(bias, 0.1f, 4f) : 1f;
		}
		catch (System.Exception)
		{
			return 1f;
		}
	}

	/// <summary>The configured `tree_far_reach` (a cvar-set override included), or 0.</summary>
	private static float ReadTreeFarReach()
	{
		try
		{
			float metres = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), TreeFarReachKey, 0f);
			return metres > 0f && float.IsFinite(metres) ? metres : 0f;
		}
		catch (System.Exception)
		{
			return 0f;
		}
	}

	/// <summary>A tree node's band and range capped at the configured <see cref="TreeFarReach"/>.</summary>
	internal static (float[] Band, float[] Stated) Cap(float[] band, float[] stated) =>
		CapTreeReach(band, stated, TreeFarReach);

	/// <summary>
	/// A tree node's band and range with its far end pulled in to <paramref name="reach"/> metres,
	/// fading over the last eighth as the stated end does; unchanged when the reach is 0 or past
	/// the stated end.
	/// </summary>
	/// <remarks>
	/// WHY. With no classic renderer there is no impostor, so a tree's `_LOD1` model stands in for
	/// the 360's billboard from 0.875 L out to max(D, F): 1024 m for the shipped trees, where the
	/// 360 drew two triangles a tree. A cap ends every tree there instead, through the same
	/// per-placement fade, so nothing pops. The fade-out never starts before the fade-in has
	/// ended, so a `_LOD1` node keeps its whole hand-over from the main model. The node's own range
	/// keeps the batch spread its stamp added past the band.
	/// </remarks>
	internal static (float[] Band, float[] Stated) CapTreeReach(float[] band, float[] stated, float reach)
	{
		if (!(reach > 0f) || !float.IsFinite(reach) || band.Length < 6 || stated.Length < 3 || !(band[3] > reach))
		{
			return (band, stated);
		}

		float inEnd = band[1] > 0f ? band[0] + (1f / band[1]) : 0f;
		float end = Mathf.Max(reach, inEnd / 0.875f);
		if (!(end < band[3]))
		{
			return (band, stated);
		}

		float pad = Mathf.Max(stated[2] - band[3], 0f);
		var cappedBand = (float[])band.Clone();
		cappedBand[3] = end;
		cappedBand[4] = 1f / (0.125f * end);
		var cappedStated = (float[])stated.Clone();
		cappedStated[2] = end + pad;
		return (cappedBand, cappedStated);
	}
}
