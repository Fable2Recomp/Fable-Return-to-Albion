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

	/// <summary>A foliage block: a hard cut at its draw distance, nothing else.</summary>
	public static StatedRange FromFoliage(float draw) => new(draw, 0f, 0f, 0f, 0f, 0f);
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

	/// <summary>Applies a stamped range; false when the node carries none.</summary>
	public static bool Apply(GeometryInstance3D node)
	{
		if (!node.HasMeta(Meta))
		{
			return false;
		}

		return ApplyStated(node, node.GetMeta(Meta).AsFloat32Array());
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

		switch (role)
		{
			case PropRole.Main:
				// With a LOD model the main one stops at L; without, modes 2 and 3 become 1 and
				// it covers the LOD's range as well.
				stated = new[] { 0f, 0f, hasLod ? lod : Mathf.Max(lod, far), 0f, 0f };
				return stated[2] > 0f;

			case PropRole.Lod:
				stated = new[] { 0.75f * lod, 0f, Mathf.Max(lod, far), 0f, 0f };
				return stated[2] > stated[0];

			default:
				// Cast-only: the guest's cut is S. "full" leaves the cascades to decide.
				stated = new[] { 0f, 0f, TreeShadowStated ? r.Shadow : 0f, 0f, 0f };
				return !TreeShadowStated || r.Shadow > 0f;
		}
	}
}
