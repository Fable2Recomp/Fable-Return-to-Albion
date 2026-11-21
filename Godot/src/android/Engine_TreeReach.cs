using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// The far tree models' reach and level bias (`tree_far_reach`, `tree_lod_bias`), on top of the
/// stated ranges <see cref="Engine_PropDrawRange"/> gives every tree.
/// </summary>
public static class Engine_TreeReach
{
	/// <summary>
	/// Whether distant trees are drawn as cards (the classic renderer's billboards, or
	/// `tree_billboards` on a phone): the tree's impostor model then ends at its stated F and
	/// carries {F, cull} for the cards. Any thread.
	/// </summary>
	public static bool BillboardsDrawn =>
		Refii.Models.Engine_ClassicBillboards.Available || Refii.AndroidRuntime.AndroidTreeCards.On;

	/// <summary>
	/// Whether a placed tree whose definition names no LOD model borrows the `_LOD1` (and `_shadow`) model
	/// of the level's own tree block for the same model (Engine_LevelProps.StatePlacedTree): the main model
	/// to F, the `_LOD1` beyond. Any thread.
	/// </summary>
	public static bool PlacedTreeBorrowsFarModel => PlacedTreeBorrows(
		Refii.Models.Engine_ClassicBillboards.Available, Refii.AndroidRuntime.AndroidTreeCards.On,
		Refii.AndroidRuntime.AndroidTreeCards.StartScaleChosen);

	/// <summary>
	/// <see cref="PlacedTreeBorrowsFarModel"/> for these settings. Pure.
	/// </summary>
	/// <remarks>
	/// Under classic the impostor follows the full model, as the title's does: no stand-in. The phone's cards
	/// (`tree_billboards`) do the same at start scale 1, where the full model ends at F and the card takes
	/// over there, as it always has. Past 1 the cards start at scale x F, and a tree that kept its full
	/// model as the impostor drew the full model all the way out to the card: in Bowerstone's graveyard at
	/// Ultra (scale 3, so 192 m) 2.83 M triangles against 1.68 M with no cards at all (Galaxy S25+,
	/// 2026-11-16). Borrowing, the tree draws exactly what it draws without cards (the `_LOD1` from the
	/// hand-over) up to the card start, and the card from there.
	/// </remarks>
	public static bool PlacedTreeBorrows(bool classic, bool cards, float startScale) =>
		!classic && (!cards || Refii.AndroidRuntime.AndroidTreeCardMaths.StartScale(startScale) > 1f);

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
			!name.Equals(TreeLodBiasKey, StringComparison.OrdinalIgnoreCase) &&
			!name.Equals(TreeHandoverKey, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		TreeFarReach = ReadTreeFarReach();
		TreeLodBias = ReadTreeLodBias();
		TreeHandover = ReadTreeHandover();
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
			float bias = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), TreeLodBiasKey, 0.65f);
			return float.IsFinite(bias) && bias > 0f ? Mathf.Clamp(bias, 0.1f, 4f) : 0.65f;
		}
		catch (System.Exception)
		{
			return 0.65f;
		}
	}

	/// <summary>The configured `tree_far_reach` (a cvar-set override included), defaulting to 400 m.</summary>
	private static float ReadTreeFarReach()
	{
		try
		{
			float metres = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), TreeFarReachKey, 400f);
			return metres > 0f && float.IsFinite(metres) ? metres : 0f;
		}
		catch (System.Exception)
		{
			return 400f;
		}
	}

	/// <summary>A tree node's band and range capped at the configured <see cref="TreeFarReach"/>.</summary>
	internal static (float[] Band, float[] Stated) Cap(float[] band, float[] stated) =>
		Cap(band, stated, TreeHandover, TreeFarReach);

	/// <summary>A tree node's band and range with its hand-over scaled, then capped at <paramref name="reach"/>:
	/// in that order, so a capped `_LOD1` node's fade-out follows its scaled fade-in. Pure.</summary>
	internal static (float[] Band, float[] Stated) Cap(float[] band, float[] stated, float scale, float reach)
	{
		(band, stated) = ScaleHandover(band, stated, scale);
		return CapTreeReach(band, stated, reach);
	}

	/// <summary>`tree_handover_scale`: where a tree's main model hands over to its `_LOD1` model, as a
	/// fraction of the stated LOD distance; 1 keeps the stated distance.</summary>
	public const string TreeHandoverKey = "tree_handover_scale";

	/// <summary>The nearest a scaled hand-over may come, in metres.</summary>
	/// <remarks>A tree that already hands over within this distance keeps it: closer in, a full model covers
	/// enough of the screen that its simpler far model would show.</remarks>
	public const float HandoverFloorMetres = 40f;

	/// <summary>`tree_handover_scale` as last read, held to [0.25, 1].</summary>
	public static float TreeHandover { get; private set; } = ReadTreeHandover();

	private static float ReadTreeHandover()
	{
		try
		{
			float scale = Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), TreeHandoverKey, 1f);
			return float.IsFinite(scale) ? Mathf.Clamp(scale, 0.25f, 1f) : 1f;
		}
		catch (System.Exception)
		{
			return 1f;
		}
	}

	/// <summary>
	/// A tree node's band and range with the hand-over between its main and `_LOD1` models moved in
	/// to <paramref name="scale"/> times the stated LOD distance; unchanged at 1 and for a tree
	/// with no `_LOD1` model.
	/// </summary>
	/// <remarks>
	/// WHY. A main tree model is the game's full mesh (Oakfield's pine: 9,500 triangles) and its
	/// `_LOD1` the game's own far mesh (316), swapped at the stated L (about 80 m for the pine). On a
	/// phone's screen the far mesh holds up sooner. Both nodes move together, so the two models
	/// still trade places across one stretch, through the same per-placement fade.
	/// </remarks>
	internal static (float[] Band, float[] Stated) ScaleHandover(float[] band, float[] stated, float scale)
	{
		if (!(scale > 0f) || !(scale < 1f) || band.Length < 6 || stated.Length < 3)
		{
			return (band, stated);
		}

		// The stated L, from whichever node this is, and the scale held so the hand-over never comes
		// nearer than HandoverFloorMetres: a tree that already hands over close by keeps its distance.
		float stat = band[1] > 0f ? band[0] / 0.875f : band[5] == 2f ? band[3] : 0f;
		if (!(stat > 0f))
		{
			return (band, stated);
		}

		scale = Mathf.Max(scale, Mathf.Min(1f, HandoverFloorMetres / stat));
		if (!(scale < 1f))
		{
			return (band, stated);
		}

		var scaledBand = (float[])band.Clone();
		var scaledStated = (float[])stated.Clone();
		if (band[1] > 0f)
		{
			// The `_LOD1` node: its fade-in starts at 0.875 L and runs over 0.125 L.
			float pad = Mathf.Max(stated[2] - band[3], 0f);
			scaledBand[0] = band[0] * scale;
			scaledBand[1] = band[1] / scale;
			scaledStated[0] = Mathf.Max(0f, scaledBand[0] - pad);
			return (scaledBand, scaledStated);
		}

		if (band[5] == 2f && band[3] > 0f)
		{
			// The main node of a tree with a `_LOD1`: its fade-out ends at L.
			float pad = Mathf.Max(stated[2] - band[3], 0f);
			scaledBand[3] = band[3] * scale;
			scaledBand[4] = band[4] / scale;
			scaledStated[2] = scaledBand[3] + pad;
			return (scaledBand, scaledStated);
		}

		return (band, stated);
	}

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
