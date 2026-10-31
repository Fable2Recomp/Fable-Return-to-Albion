using System.Collections.Concurrent;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>The render class the guest gives a placed model, from its `.mdl` header.</summary>
public enum PropModelClass
{
	/// <summary>StaticMesh: one model, drawn to its stated distance with a fade.</summary>
	Static,

	/// <summary>TreeMesh (`.mdl` byte 77 == 2): main, `_LOD1` and `_shadow` models.</summary>
	Tree,

	/// <summary>RepeatedMesh (`.mdl` byte 78 != 0): instanced geometry, one model.</summary>
	Repeated,
}

/// <summary>Which of a placement's models a node draws.</summary>
public enum PropRole
{
	Main,
	Lod,
	Shadow,
}

/// <summary>
/// The distances a level file states for one placement. Type-2 instances carry them per
/// instance (<see cref="FromStatic"/>); a type-21 foliage block carries one for the block.
/// </summary>
public readonly record struct StatedRange(
	float Draw, float Fade, float NearDraw, float NearFade, float Shadow, float Lod)
{
	/// <summary>
	/// A type-2 instance's floats (`sub_82AB9CF8`): [13] draw distance, [14] far-fade fraction,
	/// [16] S (msg+0x48), [17] L (msg+0x4C), [18] near-fade distance, [19] its fraction.
	/// </summary>
	public static StatedRange FromStatic(float[] values) =>
		new(values[13], values[14], values[18], values[19], values[16], values[17]);

	/// <summary>A foliage block: its draw distance and the fraction it fades over.</summary>
	public static StatedRange FromFoliage(float draw, float fade) => new(draw, fade, 0f, 0f, 0f, 0f);
}

/// <summary>
/// The guest's draw-distance and model-switch contract for placed props, and its Godot form.
/// </summary>
/// <remarks>
/// Every static placement in a level file carries its own draw distance
/// and fade; a TreeMesh carries a LOD distance L, a shadow distance S, and names a separate
/// `_LOD1` model and a cast-only `_shadow` model in its block header. The guest's rule:
///
/// - StaticMesh / RepeatedMesh (`sub_8221E5F0`): drawn to D, fading out linearly from
///   D·(1 − fade) to D; an optional near fade the same way from NearDraw; the shadow pass cuts
///   hard at D.
/// - TreeMesh (`sub_82208310`, `sub_822DAB20`): with F = max(D, S), the main model below L, the
///   `_LOD1` model from 0.75·L to max(L, F), both in the band between (no alpha on either); the
///   shadow pass draws ONLY the `_shadow` model, while the camera is within S. With no LOD model
///   the main model covers the whole range; with no shadow model the tree casts nothing.
/// - A foliage block (`sub_822261C8`): a hard cut at the block's D.
///
/// Godot's visibility range is measured to the NODE's box centre, so a batch switches as one;
/// the guest switches per placement. A batch spans one 32 m cell, which bounds the difference.
/// Godot's fade runs across end ± margin, so a guest fade from D·(1 − f) to D is end D − f·D/2
/// with margin f·D/2. The range also culls the directional shadow cascades
/// (`renderer_scene_cull.cpp` VIS_CHECK in the cascade loop), as the guest's pass-0 cut does.
/// </remarks>
public static class Engine_PropDrawRange
{
	/// <summary>Node metadata: {begin, begin margin, end, end margin, fade} the build stated.</summary>
	public const string Meta = "stated_range";

	/// <summary>
	/// Node metadata on a classic tree's main or LOD node: the guest's per-placement fade as
	/// {in start, 1/in length, in power, out end, 1/out length, out power}, a zero length
	/// being no fade on that side. The node's own visibility range is then a hard bound.
	/// </summary>
	public const string TreeBandMeta = "tree_fade_band";

	/// <summary>
	/// Whether a TreeMesh's shadow stops at its stated S (`tree_shadow_reach = "stated"`, the
	/// 360's rule) or its `_shadow` model casts at any distance the cascades reach ("full").
	/// </summary>
	public static readonly bool TreeShadowStated = ReadTreeShadowStated();

	/// <summary>`prop_draw_distance_scale`: every stated distance, times this. 1 is the 360.</summary>
	public static readonly float DistanceScale = ReadFloat("prop_draw_distance_scale", 1f, 0.25f, 8f);

	/// <summary>`prop_fade_fraction`: the least share of a range that fades rather than pops.</summary>
	public static readonly float FadeFloor = ReadFloat("prop_fade_fraction", 0.15f, 0f, 0.9f);

	/// <summary>Placements whose stated distances were unusable and kept the screen-size rule. Run total.</summary>
	public static int Unstated;

	private static readonly ConcurrentDictionary<string, PropModelClass> Classes = new();

	private static float ReadFloat(string key, float fallback, float min, float max)
	{
		try
		{
			IReadOnlyDictionary<string, string> config = Refii.Core.Engine_RefiiConfig.Read(out _);
			return float.TryParse(
				Refii.Core.Engine_RefiiConfig.String(config, key, string.Empty),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float value) && float.IsFinite(value)
				? Mathf.Clamp(value, min, max)
				: fallback;
		}
		catch (System.Exception)
		{
			return fallback;
		}
	}

	private static bool ReadTreeShadowStated()
	{
		try
		{
			IReadOnlyDictionary<string, string> config = Refii.Core.Engine_RefiiConfig.Read(out _);
			return Refii.Core.Engine_RefiiConfig.String(config, "tree_shadow_reach", "full")
				.Equals("stated", System.StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// A model's class from its header (`MeshHeader_ReadFlags` 0x82B8F568): byte 77 is the
	/// dword at resource+0x68 the create slot compares with 2 (0x82a443d8), byte 78 != 0 the
	/// flag at resource+0x6C. Cached per path.
	/// </summary>
	public static PropModelClass ClassOf(string modelPath) =>
		Classes.GetOrAdd(modelPath, static path =>
		{
			byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);
			if (bytes is null || bytes.Length <= 78)
			{
				return PropModelClass.Static;
			}

			return bytes[77] == 2 ? PropModelClass.Tree
				: bytes[78] != 0 ? PropModelClass.Repeated
				: PropModelClass.Static;
		});

	/// <summary>
	/// Records on <paramref name="node"/> the range it is drawn over, for the streamer to apply
	/// once its mesh stands. False when the stated values cannot describe a range, and the node
	/// keeps the streamer's own rule.
	/// </summary>
	/// <param name="spread">
	/// How far the node's placements sit from its box centre. Godot measures the range to that
	/// centre; the guest measures each placement (a foliage block, to its sphere's surface). The
	/// range is widened by the spread so no placement is dropped before its stated distance.
	/// </param>
	public static bool Stamp(
		GeometryInstance3D node, StatedRange range, PropModelClass modelClass, PropRole role, bool hasLod,
		float spread)
	{
		if (!Describe(Scaled(range), modelClass, role, hasLod, out float[] stated))
		{
			++Unstated;
			return false;
		}

		if (modelClass == PropModelClass.Tree && role != PropRole.Shadow &&
			TreeBand(Scaled(range), role, hasLod) is { } band)
		{
			StampBillboard(node, Scaled(range), role, hasLod);
			// Both tree shaders fade each placement themselves (the classic TreeDefines, the
			// modern body's v_tree_fade), so the node's range only bounds where any copy can be
			// drawn: from the fade-in's start to the fade-out's end, widened both ways by the
			// batch's spread.
			float pad = spread > 0f && float.IsFinite(spread) ? spread : 0f;
			float begin = band[1] > 0f ? Mathf.Max(0f, band[0] - pad) : 0f;
			stated = new[] { begin, 0f, band[3] + pad, 0f, 0f };
			node.SetMeta(TreeBandMeta, band);
			node.SetMeta(Meta, stated);
			if (role == PropRole.Lod || !hasLod)
			{
				node.SetMeta(TreeFarModelMeta, true);
			}

			return true;
		}

		if (spread > 0f && float.IsFinite(spread))
		{
			// Not a tree's LOD start: pulled in by the spread it would put both models over the
			// trees nearest the camera. Only the far edges widen.
			if (stated[0] > 0f && modelClass != PropModelClass.Tree)
			{
				stated[0] = Mathf.Max(0f, stated[0] - spread);
			}

			if (stated[2] > 0f)
			{
				stated[2] += spread;
			}
		}

		node.SetMeta(Meta, stated);
		return true;
	}

	/// <summary>
	/// `entity_draw_distance_scale`: an entity's stated distance, times this. 1 is the 360; the
	/// shipped 2 keeps characters twice as far as the original drew them.
	/// </summary>
	public static readonly float EntityDistanceScale = ReadFloat("entity_draw_distance_scale", 2f, 0.25f, 8f);

	/// <summary>
	/// The range an ENTITY states: its renderable's D (GDB `MaxDrawDistance` times the guest's
	/// class multiplier) and `DrawDistanceFadeRangeFactor`, read live through
	/// <see cref="Refii.Host.HostBridge.GetEntityDrawRange"/>. The animated gather
	/// (`sub_82219AB8`) and the rigid one (`sub_8221E5F0`) apply the same rule as a static
	/// placement: a fade to D and a hard shadow cut at D. <paramref name="spread"/> widens it as
	/// <see cref="Stamp"/> does, for a batch measured to its centre. Null when nothing usable is
	/// stated — a zero D is "never drawn" to the guest, and the node keeps the screen-size rule.
	/// </summary>
	public static float[]? EntityRange(float distance, float fadeFactor, float spread = 0f)
	{
		var range = new StatedRange(distance * EntityDistanceScale, Mathf.Max(fadeFactor, FadeFloor), 0f, 0f, 0f, 0f);
		if (!Describe(range, PropModelClass.Static, PropRole.Main, false, out float[] stated))
		{
			return null;
		}

		if (spread > 0f && float.IsFinite(spread))
		{
			stated[2] += spread;
		}

		return stated;
	}

	/// <summary>Records an entity's range on <paramref name="node"/>; <see cref="Apply"/> applies it.</summary>
	public static void StampEntity(GeometryInstance3D node, float[] stated) => node.SetMeta(Meta, stated);

	/// <summary>The farthest the node is drawn at: its range end plus the fade margin, or infinity.</summary>
	public static float Reach(float[] stated) =>
		stated.Length >= 4 && stated[2] > 0f ? stated[2] + stated[3] : float.PositiveInfinity;

	/// <summary>
	/// Applies a stamped range; false when the node carries none. A classic tree's fade band
	/// goes to its instance uniforms with <paramref name="mesh"/>'s box centre, the point the
	/// guest measures from.
	/// </summary>
	public static bool Apply(GeometryInstance3D node, Mesh? mesh = null)
	{
		if (!node.HasMeta(Meta))
		{
			return false;
		}

		float[] stated = node.GetMeta(Meta).AsFloat32Array();
		if (mesh is not null && node.HasMeta(TreeBandMeta) &&
			node.GetMeta(TreeBandMeta).AsFloat32Array() is { Length: >= 6 } band)
		{
			(band, stated) = CapTreeReach(band, stated, TreeFarReach);
			Engine_PropFade.StateBand(node, new Vector4(band[0], band[1], band[2], 0f),
				new Vector4(band[3], band[4], band[5], 0f), mesh.GetAabb().GetCenter());
		}

		return ApplyStated(node, stated);
	}

	/// <summary>`tree_far_reach`: the farthest any tree model draws, in metres; 0 keeps the stated reach.</summary>
	public const string TreeFarReachKey = "tree_far_reach";

	/// <summary>`tree_far_reach` as last read: at start-up, then on each live change (Engine_PropStreamer
	/// re-applies every streamed node's range when it changes).</summary>
	public static float TreeFarReach { get; private set; } = ReadTreeFarReach();

	/// <summary>Re-reads the tree settings a cvar-set can change live.</summary>
	internal static void RefreshLive()
	{
		TreeFarReach = ReadTreeFarReach();
		TreeLodBias = ReadTreeLodBias();
	}

	/// <summary>
	/// Node metadata on a tree node that draws the tree out to its far reach: its `_LOD1` model, or a
	/// main model with none. A main model that hands over to a `_LOD1` at L does not carry it.
	/// </summary>
	public const string TreeFarModelMeta = "tree_far_model";

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

	/// <summary>Applies <paramref name="stated"/> ({begin, begin margin, end, end margin, fade}).</summary>
	public static bool ApplyStated(GeometryInstance3D node, float[] stated)
	{
		if (stated.Length < 5)
		{
			return false;
		}

		node.VisibilityRangeBegin = stated[0];
		node.VisibilityRangeBeginMargin = stated[1];
		node.VisibilityRangeEnd = stated[2];
		node.VisibilityRangeEndMargin = stated[3];
		node.VisibilityRangeFadeMode = stated[4] > 0f
			? GeometryInstance3D.VisibilityRangeFadeModeEnum.Self
			: GeometryInstance3D.VisibilityRangeFadeModeEnum.Disabled;
		return true;
	}

	/// <summary>The stated distances times the scale; the fade no shorter than the floor.</summary>
	private static StatedRange Scaled(StatedRange r) => r with
	{
		Draw = r.Draw * DistanceScale,
		Lod = r.Lod * DistanceScale,
		Shadow = r.Shadow * DistanceScale,
		NearDraw = r.NearDraw * DistanceScale,
		Fade = Mathf.Max(r.Fade, FadeFloor),
	};

	/// <summary>
	/// Under classic, the node whose model is the placement's impostor — the LOD model, else the
	/// main one (`sub_822754F8` forces tier 3) — carries {F, cull} for Engine_ClassicBillboards:
	/// F = obj+0x258, the type-2 block's S or the definition's BillboardDistance (`sub_82A708E0`
	/// 0x82A7091C stores msg+0x48), and the cull obj+0x124 = max(D, F) (0x82A70928).
	/// </summary>
	private static void StampBillboard(GeometryInstance3D node, StatedRange r, PropRole role, bool hasLod)
	{
		if (!Refii.Models.Engine_ClassicBillboards.Available || role != (hasLod ? PropRole.Lod : PropRole.Main) ||
			!float.IsFinite(r.Shadow) || r.Shadow <= 0f)
		{
			return;
		}

		node.SetMeta(Refii.Models.Engine_ClassicBillboards.Meta, new[] { r.Shadow, Mathf.Max(r.Draw, r.Shadow) });
	}

	/// <summary>
	/// A tree's per-placement fade (`sub_8220AF90`, verified 0x8220B054..0x8220B2D4), R1 the
	/// LOD distance L and R0 the LOD model's reach, d the eye's distance to the model's world box
	/// centre (`sub_822109B0`):
	///   main  alpha = sat((R1 - d) / 0.125 R1)^2                       — over [0.875, 1] R1
	///   LOD   alpha = sat((d - 0.75 R1) / 0.125 R1)^2 · sat((R0 - d) / 0.125 R0)
	/// (the LOD's fade-in here deviates: see below).
	/// A main model with no LOD model is, to the guest, drawn unfaded to its cull; here it takes
	/// the LOD's linear fade-out at its reach instead of popping (no impostor follows it).
	/// </summary>
	private static float[]? TreeBand(StatedRange r, PropRole role, bool hasLod)
	{
		if (!float.IsFinite(r.Draw) || !float.IsFinite(r.Lod) || !float.IsFinite(r.Shadow))
		{
			return null;
		}

		float lod = Mathf.Max(r.Lod, 0f);
		float reach = Mathf.Max(lod, Mathf.Max(r.Draw, r.Shadow));
		// Under classic the impostor's model stops at F, fading out over [0.875, 1] F (tier 4,
		// `sub_8220AF90` 0x8220B054..: fadeOut = sat(1 - (d - 0.875 F) / 0.125 F)); the billboard
		// carries the placement from 0.75 F to the cull.
		bool impostor = role == (hasLod ? PropRole.Lod : PropRole.Main);
		if (Refii.Models.Engine_ClassicBillboards.Available && impostor && r.Shadow > 0f)
		{
			reach = r.Shadow;
		}
		if (role == PropRole.Main)
		{
			return hasLod && lod > 0f
				? new[] { 0f, 0f, 1f, lod, 1f / (0.125f * lod), 2f }
				: reach > 0f ? new[] { 0f, 0f, 1f, reach, 1f / (0.125f * reach), 1f } : null;
		}

		// The LOD's fade-in is moved out to the main model's own band [0.875, 1] R1 and made
		// linear, so the two models trade places across one stretch: on the title's
		// [0.75, 0.875] R1 the LOD dithered in over a main model still drawn whole, and the
		// overlap read as a pale ghost on the trunk (user's call, 2026-10-13).
		return lod > 0f && reach > 0.875f * lod
			? new[] { 0.875f * lod, 1f / (0.125f * lod), 1f, reach, 1f / (0.125f * reach), 1f }
			: null;
	}

	private static bool Describe(
		StatedRange r, PropModelClass modelClass, PropRole role, bool hasLod, out float[] stated)
	{
		stated = System.Array.Empty<float>();
		if (!float.IsFinite(r.Draw) || !float.IsFinite(r.Lod) || !float.IsFinite(r.Shadow))
		{
			return false;
		}

		if (modelClass != PropModelClass.Tree)
		{
			// A zero distance would be "never drawn" to the guest; from a file we parse
			// ourselves it is far likelier a misread, so it keeps the screen-size rule and is counted.
			if (r.Draw <= 0f)
			{
				return false;
			}

			float fade = Mathf.Clamp(r.Fade, 0f, 1f);
			float endMargin = fade * r.Draw * 0.5f;
			float begin = 0f;
			float beginMargin = 0f;
			if (r.NearDraw > 0f && float.IsFinite(r.NearDraw))
			{
				float nearFade = Mathf.Clamp(r.NearFade, 0f, 1f);
				beginMargin = nearFade * r.NearDraw * 0.5f;
				begin = r.NearDraw - beginMargin;
			}

			stated = new[] { begin, beginMargin, r.Draw - endMargin, endMargin,
				endMargin > 0f || beginMargin > 0f ? 1f : 0f };
			return true;
		}

		float far = Mathf.Max(r.Draw, r.Shadow);
		float lod = Mathf.Max(r.Lod, 0f);
		if (far <= 0f && lod <= 0f)
		{
			return false;
		}

		// THE CROSS-FADE (`sub_8220AF90`, modes from `sub_82208310`): with R1 = L the main
		// model fades out over [0.875 L, L] while the LOD model fades in over [0.75 L, 0.875 L]; here
		// the LOD fades in across the main's band instead (TreeBand), and out over the last eighth of its reach. Godot fades
		// across end +- margin, so a band [a, b] is end (a + b) / 2, margin (b - a) / 2.
		float reach = Mathf.Max(lod, far);
		switch (role)
		{
			case PropRole.Main:
				// With a LOD model the main one stops at L; without, modes 2 and 3 become 1 and
				// it covers the LOD's range as well.
				stated = hasLod
					? new[] { 0f, 0f, 0.9375f * lod, 0.0625f * lod, 1f }
					: new[] { 0f, 0f, 0.9375f * reach, 0.0625f * reach, 1f };
				return stated[2] > 0f;

			case PropRole.Lod:
				// Fades in across the main model's own band, as the classic shader's does (TreeBand).
				stated = new[] { 0.9375f * lod, 0.0625f * lod, 0.9375f * reach, 0.0625f * reach, 1f };
				return stated[2] > stated[0];

			default:
				// Cast-only: the guest's cut is S. "full" leaves the cascades to decide.
				stated = new[] { 0f, 0f, TreeShadowStated ? r.Shadow : 0f, 0f, 0f };
				return !TreeShadowStated || r.Shadow > 0f;
		}
	}
}
