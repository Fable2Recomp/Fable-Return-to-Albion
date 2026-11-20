using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Lighting;

/// <summary>
/// The region's PLACED lights — every torch, brazier, lantern and fixture the level states.
///
/// Until this landed a region was lit by exactly two things: the theme's one directional sun and
/// the offline bake. That is correct at noon in an open street and wrong everywhere else — a lit
/// torch threw no light on the wall behind it, and an interior with no sun reaching it was lit
/// only by whatever the bake happened to hold. The guest places 772 light components across the
/// shipped levels; we placed none.
///
/// A placed light is an ENTITY COMPONENT, not a level-owned array. It arrives through the same
/// entity/layer/placement chain as props and particle effects, which is why this reads it the way
/// <see cref="Engine_LevelFx"/> reads emitters — through the cross-file parent walk — rather than
/// looking for a light table that does not exist.
/// </summary>
public static class Engine_LevelLights
{
	/// <summary>
	/// FNV-1 of "GraphicAppearanceLightComponent" — the property an entity states to carry a light.
	///
	/// The guest never spells this hash out; it builds the name at runtime. <c>sub_8263C388</c>
	/// registers the type from the string "CECGraphicAppearanceLight" (0x820C57B0), and
	/// <c>sub_826302C0</c> turns a type name into a property name by stripping the leading "CEC"
	/// (<c>sub_8222CED0(&amp;v5, (_BYTE *)(a2 + 3))</c>) and appending "Component"
	/// (<c>sub_821DA9C0(v6, "Component")</c>). The intern it goes through, <c>sub_822078A0</c>,
	/// only allocates and copies — there is no case folding — so the property name is exactly
	/// "GraphicAppearanceLightComponent".
	///
	/// The hash is therefore COMPUTED rather than quoted, and it was checked the only way a
	/// computed hash can be: against a value this project already had proven. Our FNV-1 reproduces
	/// ParticleEmitter = 0x4EDC9083 from its own name, and the same function gives 0xBB61B654 here.
	/// The shipped data agrees — 772 occurrences of this word across levels.bnk, alongside 436
	/// HalfAngleDegrees, 606 FadeRange and 408 FlickeringRate.
	/// </summary>
	public const uint GraphicAppearanceLightComponent = 0xBB61B654;

	// The component's own fields, read by `sub_825C0CC0` (0x825C0CC0). Every offset below is
	// measured from the COMPONENT BASE — r28 in that function, which is its first argument — and
	// was read out of the disassembly rather than taken from a summary.
	public const uint Colour = 0xB76325F5;
	public const uint Red = 0x3A232172;
	public const uint Green = 0x608C9792;
	public const uint Blue = 0xB1911CC9;
	public const uint Intensity = 0x8F89F9D4;
	public const uint Range = 0xF21E235C;
	public const uint LightType = 0x141A09CB;
	public const uint HalfAngleDegrees = 0x25ECC30E;
	public const uint FadeDistance = 0x38B010EA;
	public const uint FadeRange = 0x7505C7FA;
	public const uint CastShadows = 0x63200437;
	public const uint InitialActive = 0xFA11F9CD;
	public const uint AttachToEntityPosition = 0x84886409;
	public const uint IgnoreZoning = 0x59732763;
	public const uint FlickeringBrightnessRange = 0xB6EE59FF;
	public const uint FlickeringRadiusRange = 0x553EE81A;
	public const uint FlickeringRate = 0x5CD3FCE3;

	/// <summary>
	/// The dummy a light hangs on, and the reason a lamp lights its own head rather than its foot.
	///
	/// <c>sub_821E44F0</c> resolves a light's position in a strict order, and the middle case is
	/// the ordinary one: unless <c>AttachToEntityPosition</c> is set, it interns
	/// "Prop.Layout.Light" (the string is at 0x820D2470, interned into <c>dword_8349B808</c> by
	/// <c>sub_83266628</c>), asks the model for how many such dummies it has via
	/// <c>sub_825727B0</c>, and emits ONE PRIMITIVE PER DUMMY —
	/// <c>821E4A24 sub_825C2108(v5 - 12, v46, a2, a3)</c> inside the loop.
	///
	/// So a model with two of these carries two lights, and this is a count rather than a lookup.
	/// <see cref="Engine_LevelFx.DummyBoneName"/> already maps this spelling onto the rig's own —
	/// "Layout_Light_DummyObject" — which the lamp models demonstrably carry beside their
	/// FX_Particle_DummyObject.
	/// </summary>
	public const string LightDummy = "Prop.Layout.Light";

	/// <summary>
	/// Maximum horizontal distance (in meters) within which compatible point lights of the same type
	/// and chromaticity (candle stands, multi-candle tables, candelabras, chandeliers) are clustered
	/// into a single point light placed at the cluster centroid, preventing multi-candle groups from
	/// emitting duplicate overlapping lights while keeping distinct fixtures separate.
	/// </summary>
	public const float FlameClusterRadius = 1.35f;
	public const float FlameClusterRadiusSq = FlameClusterRadius * FlameClusterRadius;

	/// <summary>
	/// Maximum vertical distance (in meters) for same-type point light clustering, preventing lights on
	/// different building floors or vertical tiers from being merged.
	/// </summary>
	public const float FlameClusterMaxVertical = 0.75f;

	/// <summary>The GDB null hash — an empty reference, not a missing one.</summary>
	public const uint NullHash = 0x811C9DC5;

	/// <summary>
	/// Intensity when the entity does not state one: <b>16.0</b>, from the constructor
	/// <c>sub_826310D0</c>.
	///
	/// This is not a neutral default and it cannot be treated as one. <c>Intensity</c> is one of
	/// the GATED reads in <c>sub_825C0CC0</c> — it happens only when
	/// <c>GDBEntry_GetPropTyped(entry, hash, 8)</c> reports the property exists — so an entity
	/// that states no Intensity keeps whatever the constructor put there, and the constructor put
	/// 16. Defaulting to 1.0 instead would under-light every such fixture by a factor of sixteen.
	/// </summary>
	public const float DefaultIntensity = 16.0f;

	/// <summary>
	/// Fallback intensity in Modern lighting mode when a light component omits an explicit
	/// <c>Intensity</c> property, calibrated to match authored street lamps (<c>1.05</c>).
	/// </summary>
	public const float DefaultModernIntensity = 1.05f;

	/// <summary>
	/// Base intensity multiplier applied to all placed lights in Modern mode while keeping the
	/// user-facing <c>placed_light_scale</c> CVar default at <c>1.0</c>. Leaves light range untouched.
	/// </summary>
	public const float ModernBaseEnergyScale = 2.0f;

	/// <summary>
	/// Base reach multiplier (+50%) applied to all placed lights in Modern mode while keeping the
	/// user-facing <c>placed_light_reach_scale</c> CVar default at <c>1.0</c>.
	/// </summary>
	public const float ModernBaseReachScale = 1.5f;

	/// <summary>
	/// Default volumetric scattering flux multiplier for placed and synthetic lights in Modern mode.
	/// </summary>
	public const float DefaultVolumetricFogScale = 5.0f;

	/// <summary>The guest's <c>LightType</c> values. NOT the render-primitive enum.</summary>
	/// <remarks>
	/// There are two light enumerations in this engine and they disagree. <c>sub_825C2108</c>
	/// branches on the GDB field at component+28 — <c>lwz r11, 0x1C(r31)</c> then
	/// <c>cmpwi cr6, r11, 2</c>, taking the POINT path when it equals 2 and falling through to the
	/// spot path otherwise — so the authored values are 1 = spot, 2 = point.
	///
	/// The render primitives are numbered differently (PointLight = 8, SpotLight = 9,
	/// DirLight = 10, in the table at 0x8331AE50), and reading an authored LightType against that
	/// table would silently produce the wrong kind of light for every fixture in the game.
	///
	/// <c>LightType == 0</c> is the constructor default and builds NO primitive. That it might
	/// name a directional light is not established — there is no Directional or Omni string
	/// anywhere in this path — so it is treated as "no light", which is what the guest does with it.
	/// </remarks>
	public const int LightTypeSpot = 1;

	public const int LightTypePoint = 2;

	/// <summary>One light an entity states, with where on the entity it hangs.</summary>
	/// <param name="ColourLinear">
	/// The light's colour, already divided by 255 and NOT gamma-decoded.
	///
	/// <c>sub_825C2108</c> loads the three bytes at component+18/+17/+16, converts each with
	/// <c>fcfid</c>/<c>frsp</c> and multiplies by <c>flt_833216C8</c> = 0x3B808081 = 1/255
	/// (<c>825c222c fmuls f6, f8, f13</c>). There is no <c>pow</c> and no squaring on this path —
	/// unlike the colour TEXTURE decode, which squares. Treating a light colour as if it needed
	/// the texture transfer would darken every warm fixture toward red.
	/// </param>
	/// <param name="Intensity">
	/// The scalar the guest premultiplies into that colour, together with the fade multiplier:
	/// <c>825c2180 fmuls f0, f0, f13</c> with f0 = component+60 (the fade) and f13 = component+20
	/// (Intensity). It is separate from the colour in the data and combined before the renderer
	/// ever sees it, so it belongs on the light's ENERGY rather than in its tint.
	/// </param>
	/// <param name="Range">
	/// The light's reach in raw guest world units — <c>stfs f1, 0x18(r28)</c>, with no scaling
	/// applied anywhere between the GDB read and the render primitive (where it lands at +144).
	/// </param>
	/// <param name="HalfAngleRadians">
	/// The spot cone's half angle, ALREADY IN RADIANS. The field is authored in DEGREES and
	/// converted on the way in: <c>825c0d8c fmuls f0, f1, f0</c> with f0 = flt_82100ED4 =
	/// 0x3C8EFA35 = 0.017453292, then <c>825c0d90 stfs f0, 0x20(r28)</c>. The save-game serializer
	/// names the same offset "HalfAngleRadians", which confirms the stored unit.
	/// </param>
	/// <param name="Dummy">
	/// The rig attachment this light hangs on, empty when the entity states
	/// <c>AttachToEntityPosition</c> and the light belongs at the entity's own origin.
	/// </param>
	/// <param name="FlickerRate">
	/// How fast the flicker walk moves, and the GATE on the whole effect — at zero the walk never
	/// leaves its starting value and neither range does anything. See <see cref="Engine_LightFlicker"/>.
	/// </param>
	public readonly record struct LightPlacement(
		Vector3 ColourLinear,
		float Intensity,
		float Range,
		int Type,
		float HalfAngleRadians,
		float FadeDistance,
		float FadeRange,
		bool CastShadows,
		bool Active,
		string Dummy,
		float FlickerRate,
		float FlickerBrightnessRange,
		float FlickerRadiusRange,
		bool IgnoreZoning = false,
		bool IsSyntheticFlame = false);

	/// <summary>
	/// The light an entity states, or none.
	/// </summary>
	/// <remarks>
	/// Read through the cross-file parent walk for the same reason the FX walk uses it: a level's
	/// placements inherit from shared templates that live in other .gdb files, and a component
	/// def states only what it overrides. A light that states nothing but a Colour inherits its
	/// Range, Intensity and type from the template it descends from.
	/// </remarks>
	public static bool TryRead(uint entity, List<GdbView> views, out LightPlacement light)
	{
		light = default;

		if (Engine_LevelFx.Inherited(views, entity, GraphicAppearanceLightComponent,
				GdbFieldType.Reference) is not { Raw: not 0 and not Engine_LevelFx.Null } hop)
		{
			return false;
		}

		uint component = hop.Raw;

		// LIGHTTYPE FIRST, because it is the gate. `sub_825C2108` builds nothing for type 0, and
		// type 0 is the constructor default — so an entity carrying a light component that never
		// states a type carries no light, and emitting one anyway would put a stray point light
		// at the origin of every such entity.
		int type = ReadInt(views, component, LightType, 0);
		if (type != LightTypePoint && type != LightTypeSpot)
		{
			return false;
		}

		float range = ReadFloat(views, component, Range, 0.0f);
		if (!float.IsFinite(range) || range <= 0.0f)
		{
			return false;
		}

		// The three channels are INTS here, not the floats every theme colour uses. The guest
		// reads them with `sub_823BF820` — the same getter it uses for LightType — at
		// 825c0d08/0d1c/0d30, and the shipped records agree: a light colour reads
		// `Red int 255, Green int 199, Blue int 63`. Asking for a float would find nothing and
		// leave every fixture white.
		var colour = new Vector3(1.0f, 1.0f, 1.0f);
		if (Engine_LevelFx.Inherited(views, component, Colour, GdbFieldType.Reference) is
			{ Raw: not 0 and not Engine_LevelFx.Null } colourRef)
		{
			colour = new Vector3(
				ReadInt(views, colourRef.Raw, Red, 255) * (1.0f / 255.0f),
				ReadInt(views, colourRef.Raw, Green, 255) * (1.0f / 255.0f),
				ReadInt(views, colourRef.Raw, Blue, 255) * (1.0f / 255.0f));
		}

		// A light that names no dummy still has a home: the entity's own origin. That is the
		// AttachToEntityPosition branch of `sub_821E44F0` — `if (*(_BYTE *)(result + 120))`,
		// which is component+132 measured from the component base, since that function's argument
		// is comp+12.
		bool atEntity = ReadBool(views, component, AttachToEntityPosition, false);

		float fallbackIntensity = Engine_LightingFrame.ModernModel ? DefaultModernIntensity : DefaultIntensity;
		light = new LightPlacement(
			colour,
			ReadFloat(views, component, Intensity, fallbackIntensity),
			range,
			type,
			ReadFloat(views, component, HalfAngleDegrees, 0.0f) * (MathF.PI / 180.0f),
			ReadFloat(views, component, FadeDistance, 0.0f),
			ReadFloat(views, component, FadeRange, 0.0f),

			// CastShadows is read ONLY for a spot in `sub_825C0CC0` — the `825C1168` branch gates
			// it, SpotLightTexture and UseScreenSpaceShadowmap together on LightType == 1 — so a
			// point light's stated CastShadows is a field the guest never looks at.
			type == LightTypeSpot && ReadBool(views, component, CastShadows, false),

			// InitialActive defaults TRUE (`sub_826310D0` stores 1), and `sub_825C2108` builds
			// nothing while it is clear: `if (!*(_BYTE *)(result + 124))` in the driver.
			ReadBool(views, component, InitialActive, true),
			atEntity ? string.Empty : LightDummy,

			// All three default to 0.0 in the component constructor (`sub_826310D0` at
			// 0x8263117C/80/84), so a light that states none of them simply does not flicker.
			ReadFloat(views, component, FlickeringRate, 0.0f),
			ReadFloat(views, component, FlickeringBrightnessRange, 0.0f),
			ReadFloat(views, component, FlickeringRadiusRange, 0.0f),
			ReadBool(views, component, IgnoreZoning, false));

		return true;
	}

	/// <summary>
	/// Builds the region's placed lights under <paramref name="container"/>, and returns how many.
	/// </summary>
	/// <remarks>
	/// ONE LIGHT PER DUMMY, not one per placement. <c>sub_821E44F0</c> asks the model how many
	/// <c>Prop.Layout.Light</c> attachment points it carries and loops over them, indexing the
	/// component's own light vector by the dummy's index — so a twin-headed lamp post is two
	/// lights, and collapsing it to one loses half of them.
	/// </remarks>
	/// <summary>
	/// Every placed light, for the FX system's lit term. The guest's particle vertex shader
	/// lights a particle as `ambient + Σ point lights` (blob 0038, slots 1..N of
	/// `g_GlobalLightParamsVS`) — smoke beside a torch is torch-coloured. Registered at spawn,
	/// validated at query, because a level change frees the nodes without telling anyone.
	/// </summary>
	private static readonly List<Light3D> FxRegistry = new();

	static Engine_LevelLights()
	{
		Refii.Core.Engine_RefiiConfig.OnConfigChanged += (_, _) => ReloadConfig();
	}

	/// <summary>The fixture's reach as BUILT, before any live scaling, kept so a sweep can be
	/// relative to the authored value rather than to whatever the last sweep left.</summary>
	public const string AuthoredRangeMeta = "refii_authored_range";

	public const string RawIntensityMeta = "raw_intensity";

	public const string RawRangeMeta = "raw_range";

	public const string RawFadeDistanceMeta = "raw_fade_distance";

	public const string RawFadeRangeMeta = "raw_fade_range";

	/// <summary>
	/// A live multiplier on every placed fixture's reach, for measuring what `placed_light_range`
	/// costs WITHOUT a level reload. 1.0 is whatever the level was built with.
	/// </summary>
	/// <remarks>
	/// Godot re-pairs a light against every geometry instance its AABB touches, so pairing cost
	/// tracks the volume each fixture covers, the cube of its range. Scaling live costs one
	/// re-pair pass; `placed_light_range` itself is read at light CREATION and needs a reload.
	/// </remarks>
	public static float RangeScale = 1.0f;

	/// <summary>Puts every placed fixture at its authored reach times <see cref="RangeScale"/>.</summary>
	public static void ApplyRangeScale()
	{
		LightMetaGeneration++;
		lock (FxRegistry)
		{
			foreach (Light3D node in FxRegistry)
			{
				if (!GodotObject.IsInstanceValid(node) || !node.HasMeta(AuthoredRangeMeta))
				{
					continue;
				}

				float authored = (float)node.GetMeta(AuthoredRangeMeta);
				if (node is OmniLight3D omni)
				{
					omni.OmniRange = authored * RangeScale;
				}
				else if (node is SpotLight3D spot)
				{
					spot.SpotRange = authored * RangeScale;
				}
			}
		}
	}

	public static void ComputeEffectiveLightValues(
		float rawIntensity,
		float rawRange,
		bool isIndoorLight,
		out float baseEnergy,
		out float authoredRange)
	{
		bool isCaveLevel = Engine_LightingFrame.CurrentInterior;
		bool isBuildingInterior = !isCaveLevel && isIndoorLight;

		float modernBaseEnergy = Engine_LightingFrame.ModernModel ? ModernBaseEnergyScale : 1.0f;
		float modernBaseReach = Engine_LightingFrame.ModernModel ? Refii.AndroidRuntime.AndroidLeanModern.LampReach(ModernBaseReachScale) : 1.0f;
		float energyScale = modernBaseEnergy * PlacedScale * (isBuildingInterior ? BuildingInteriorLightScale : 1.0f);
		float reachScale = modernBaseReach * PlacedRange * (isBuildingInterior ? BuildingInteriorReachScale : 1.0f);

		baseEnergy = rawIntensity * energyScale;
		authoredRange = rawRange * reachScale;
	}

	[Obsolete("Unattached light scaling has been removed. Use the overload without isUnattached.")]
	public static void ComputeEffectiveLightValues(
		float rawIntensity,
		float rawRange,
		bool isIndoorLight,
		bool isUnattached,
		out float baseEnergy,
		out float authoredRange)
	{
		_ = isUnattached;
		ComputeEffectiveLightValues(rawIntensity, rawRange, isIndoorLight, out baseEnergy, out authoredRange);
	}

	/// <summary>
	/// Computes the maximum world-space ray distance for baking static shadow maps for <paramref name="item"/>,
	/// honouring the <c>placed_light_reach_scale</c> (<see cref="PlacedRange"/>),
	/// <c>building_interior_reach_scale</c> (<see cref="BuildingInteriorReachScale"/>), and
	/// <see cref="ModernBaseReachScale"/> via <see cref="ComputeEffectiveLightValues(float, float, bool, out float, out float)"/>,
	/// plus the light's flicker radius envelope and <see cref="RangeScale"/>.
	/// </summary>
	public static float ComputeMaxBakeRange(ResolvedLightInstance item)
	{
		ComputeEffectiveLightValues(
			item.Light.Intensity,
			item.Light.Range,
			isIndoorLight: false,
			out _,
			out float outdoorReach);
		ComputeEffectiveLightValues(
			item.Light.Intensity,
			item.Light.Range,
			isIndoorLight: true,
			out _,
			out float indoorReach);

		float authoredReach = MathF.Max(outdoorReach, indoorReach);
		if (!Engine_LightingFrame.ModernModel)
		{
			authoredReach *= ModernBaseReachScale;
		}
		authoredReach *= Refii.AndroidRuntime.AndroidLeanModern.BakeReachLift(Engine_LightingFrame.ModernModel, ModernBaseReachScale);

		float flickerEnvelope = 1.0f + MathF.Max(0.0f, item.Light.FlickerRadiusRange);
		return authoredReach * flickerEnvelope * MathF.Max(1.0f, RangeScale);
	}

	public static void ComputeEffectiveLightValues(Light3D node, out float baseEnergy, out float authoredRange)
	{
		float rawIntensity = node.HasMeta(RawIntensityMeta)
			? (float)node.GetMeta(RawIntensityMeta).AsDouble()
			: DefaultIntensity;
		float rawRange = node.HasMeta(RawRangeMeta)
			? (float)node.GetMeta(RawRangeMeta).AsDouble()
			: 8.0f;
		ComputeEffectiveLightValues(
			rawIntensity,
			rawRange,
			Engine_LightZones.IsIndoorLight(node),
			out baseEnergy,
			out authoredRange);
	}

	public static void RefreshLightScales(Light3D node)
	{
		if (!GodotObject.IsInstanceValid(node) || !node.HasMeta(RawIntensityMeta))
		{
			return;
		}

		ComputeEffectiveLightValues(node, out float baseEnergy, out float authoredRange);
		node.SetMeta(BaseEnergyMeta, baseEnergy);
		node.SetMeta(AuthoredRangeMeta, authoredRange);
		LightMetaGeneration++;

		float fade = Engine_LightShadowBudget.VisibilityFactorOf(node);
		node.LightEnergy = baseEnergy * fade;

		float scaledRange = authoredRange * RangeScale;
		if (node is OmniLight3D omni)
		{
			if (MathF.Abs(omni.OmniRange - scaledRange) > 1e-4f)
			{
				omni.OmniRange = scaledRange;
			}
		}
		else if (node is SpotLight3D spot)
		{
			if (MathF.Abs(spot.SpotRange - scaledRange) > 1e-4f)
			{
				spot.SpotRange = scaledRange;
			}
		}

		float rawFadeDist = node.HasMeta(RawFadeDistanceMeta)
			? (float)node.GetMeta(RawFadeDistanceMeta).AsDouble()
			: 0.0f;
		float rawFadeRange = node.HasMeta(RawFadeRangeMeta)
			? (float)node.GetMeta(RawFadeRangeMeta).AsDouble()
			: 0.0f;
		ApplyDistanceFade(node, rawFadeDist, rawFadeRange);
	}

	private static void ApplyDistanceFade(Light3D node, float rawFadeDistance, float rawFadeRange)
	{
		if (!Engine_LightingFrame.ModernModel || PlacedFadeEnabled)
		{
			bool authored = rawFadeDistance > 0.0f;
			if (authored || (Engine_LightingFrame.ModernModel && PlacedFadeEnabled))
			{
				float rangePresetScale = PlacedLightRange() / 60.0f;
				float cutoff = !authored
					? PlacedLightRange()
					: (Engine_LightingFrame.ModernModel
						? rawFadeDistance * 2.0f * PlacedRange * rangePresetScale
						: rawFadeDistance);
				cutoff = Refii.AndroidRuntime.AndroidLeanModern.LampFadeCutoff(cutoff, authored);
				float ramp = authored ? MathF.Max(rawFadeRange, 0.01f) : 15.0f;
				if (Engine_LightingFrame.ModernModel)
				{
					ramp = MathF.Max(ramp * 2.0f, cutoff * 0.5f);
				}

				ramp = Refii.AndroidRuntime.AndroidLeanModern.LampFadeRamp(cutoff, ramp);

				node.DistanceFadeEnabled = true;
				node.DistanceFadeBegin = MathF.Max(cutoff - ramp, 0.0f);
				node.DistanceFadeLength = ramp;
				node.DistanceFadeShadow = Engine_LightingFrame.ModernModel
					? MathF.Min(cutoff, PlacedShadowRange())
					: cutoff;
				return;
			}
		}

		node.DistanceFadeEnabled = false;
	}

	public static void RefreshAllLightScales()
	{
		var snapshot = new List<Light3D>();
		SnapshotLights(snapshot);
		foreach (Light3D node in snapshot)
		{
			RefreshLightScales(node);
		}

		lock (FxRegistry)
		{
			_fxLightCacheFrame = ulong.MaxValue;
		}

		Engine_LightFlicker.RefreshAllBaseValues();
		Engine_LightTable.Refresh();
	}

	public static void ReloadConfig()
	{
		_placedScale = null;
		_placedRange = null;
		_buildingInteriorLightScale = null;
		_buildingInteriorReachScale = null;
		_placedShadows = null;
		_placedFade = null;

		Engine_LightShadowBudget.ReloadConfig();
		Engine_LevelBakedLighting.ReloadConfig();
		Engine_LevelLighting.ReloadConfig();
		Engine_LevelShadowMaps.ReloadConfig();
		Engine_LevelShadowPrebuild.OnLightingConfigReloaded();
		RefreshAllLightScales();
		Refii.Models.Engine_ShaderSpecialisation.SettingsChanged();
	}

	/// <summary>Copies the live placed lights into <paramref name="into"/>, pruning dead ones.</summary>
	public static void SnapshotLights(List<Light3D> into)
	{
		into.Clear();
		lock (FxRegistry)
		{
			for (int i = FxRegistry.Count - 1; i >= 0; i--)
			{
				if (!GodotObject.IsInstanceValid(FxRegistry[i]))
				{
					FxRegistry.RemoveAt(i);
					continue;
				}

				into.Add(FxRegistry[i]);
			}
		}
	}

	private readonly struct FxLightSample
	{
		public readonly Vector3 Position;
		public readonly float Range;
		public readonly float RangeSq;
		public readonly Vector3 ColorTimesEnergy;

		public FxLightSample(Vector3 position, float range, float rangeSq, Vector3 colorTimesEnergy)
		{
			Position = position;
			Range = range;
			RangeSq = rangeSq;
			ColorTimesEnergy = colorTimesEnergy;
		}
	}

	private static FxLightSample[] _fxLightSamples = new FxLightSample[256];
	private static int _fxLightCacheCount;
	private static ulong _fxLightCacheFrame = ulong.MaxValue;

	/// <summary>Sum of nearby placed lights at a WORLD position, linear falloff, into `lit`.</summary>
	public static void AddLightsAt(Vector3 world, float[] lit)
	{
		ulong frame = Refii.Core.Engine_AppRoot.IsTestHost ? ulong.MaxValue : Engine.GetProcessFrames();
		lock (FxRegistry)
		{
			if (frame == ulong.MaxValue || _fxLightCacheFrame == ulong.MaxValue || frame - _fxLightCacheFrame >= 4)
			{
				if (FxRegistry.Count > _fxLightSamples.Length)
				{
					_fxLightSamples = new FxLightSample[Math.Max(FxRegistry.Count, _fxLightSamples.Length * 2)];
				}

				int count = 0;
				for (int i = FxRegistry.Count - 1; i >= 0; i--)
				{
					Light3D node = FxRegistry[i];
					if (!GodotObject.IsInstanceValid(node))
					{
						FxRegistry.RemoveAt(i);
						continue;
					}

					if (!Engine_LightTable.TryGetCachedLightSample(node, out Vector3 pos, out float range))
					{
						if (!node.IsInsideTree())
						{
							FxRegistry.RemoveAt(i);
							continue;
						}

						range = node is SpotLight3D spot ? spot.SpotRange
							: node is OmniLight3D omni ? omni.OmniRange : 0f;
						if (range <= 0f)
						{
							continue;
						}

						// A flickering light's node holds its envelope; its falloff is the stated radius.
						range = Engine_LightTable.StatedRangeOf(node, range);
						pos = node.GlobalPosition;
					}

					float energy = node.LightEnergy;
					if (range <= 0f || energy <= 0f)
					{
						continue;
					}

					Color c = Engine_LightColour.Linear(node);
					_fxLightSamples[count++] = new FxLightSample(
						pos,
						range,
						range * range,
						new Vector3(c.R * energy, c.G * energy, c.B * energy));
				}

				_fxLightCacheCount = count;
				_fxLightCacheFrame = frame;
			}

			for (int i = 0; i < _fxLightCacheCount; i++)
			{
				ref readonly FxLightSample s = ref _fxLightSamples[i];
				float dx = s.Position.X - world.X;
				float dy = s.Position.Y - world.Y;
				float dz = s.Position.Z - world.Z;
				float d2 = dx * dx + dy * dy + dz * dz;
				if (d2 >= s.RangeSq)
				{
					continue;
				}

				float d = MathF.Sqrt(d2);
				float ratio = Math.Clamp(d / s.Range, 0f, 1f);
				float r2 = ratio * ratio;
				float r4 = r2 * r2;
				float oneMinusR4 = 1f - r4;
				float window = oneMinusR4 * oneMinusR4;
				float atten = Math.Min(window / MathF.Max(d, 1f), 1f);
				if (atten <= 0f)
				{
					continue;
				}

				lit[0] += s.ColorTimesEnergy.X * atten;
				lit[1] += s.ColorTimesEnergy.Y * atten;
				lit[2] += s.ColorTimesEnergy.Z * atten;
			}
		}
	}

	public static bool IsFlameDummy(string dummy) =>
		dummy.Length != 0 &&
		(dummy.Contains("Flame", StringComparison.OrdinalIgnoreCase) ||
		 dummy.Contains("Fire", StringComparison.OrdinalIgnoreCase) ||
		 dummy.Contains("Torch", StringComparison.OrdinalIgnoreCase) ||
		 dummy.Contains("Lamp", StringComparison.OrdinalIgnoreCase) ||
		 dummy.Contains("Lantern", StringComparison.OrdinalIgnoreCase) ||
		 dummy.Contains("Brazier", StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Aligns a fixture's <c>Prop.Layout.Light</c> dummy height to its flame emitter height when the
	/// light dummy was authored above the suspended lamp/flame (e.g. up on the chandelier chain in
	/// <c>BS_Light_Ceiling.mdl</c> at Y=0.698 vs. the lamp ring at Y=0.394, or inside the top cap of
	/// <c>BS_Lights_Town.mdl</c> at Y=3.397 vs. the globe flame at Y=3.214). Leaves floor grates and
	/// campfires (<c>flameY &lt;= 0.35f</c>) untouched.
	/// </summary>
	public static Transform3D AlignLightDummyToFlame(Transform3D lightDummy, IReadOnlyList<Vector3> flameOrigins)
	{
		float bestFlameY = float.NegativeInfinity;
		for (int i = 0; i < flameOrigins.Count; i++)
		{
			float fy = flameOrigins[i].Y;
			if (fy > 0.35f && fy < lightDummy.Origin.Y - 0.02f && fy > bestFlameY)
			{
				bestFlameY = fy;
			}
		}

		if (float.IsFinite(bestFlameY))
		{
			lightDummy.Origin = new Vector3(lightDummy.Origin.X, bestFlameY, lightDummy.Origin.Z);
		}

		return lightDummy;
	}

	private static bool HasFlameFxAttachment(uint entity, List<GdbView> views)
	{
		List<Engine_LevelFx.FxAttachment> fxList = Engine_LevelFx.HuntFxAttachments(entity, views);
		for (int i = 0; i < fxList.Count; i++)
		{
			if (IsFlameDummy(fxList[i].Dummy))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The placements carrying an active light — kept as nodes of their own by the prop builder.</summary>
	public static HashSet<uint> LightBearing(
		GdbView levelView, List<GdbView> supplementalViews, List<GdbPlacement> placements)
	{
		var views = new List<GdbView>(supplementalViews.Count + 1) { levelView };
		views.AddRange(supplementalViews);
		var bearing = new HashSet<uint>();
		foreach (GdbPlacement placement in placements)
		{
			if ((TryRead(placement.Hash, views, out LightPlacement light) && light.Active) ||
			    HasFlameFxAttachment(placement.Hash, views))
			{
				bearing.Add(placement.Hash);
			}
		}

		return bearing;
	}

	/// <summary>One resolved static light instance in region-local space, shared by runtime spawning and offline shadow baking.</summary>
	public readonly record struct ResolvedLightInstance(
		uint Entity,
		int Index,
		ulong LightKey,
		LightPlacement Light,
		Transform3D Transform,
		string? EntityName);

	private readonly record struct SyntheticFlameCandidate(
		uint PlacementHash,
		string? EntityName,
		LightPlacement Light,
		Transform3D Transform);

	/// <summary>Composes the deterministic 64-bit key identifying a placed light by its placement record and attachment dummy index.</summary>
	public static ulong ComposeLightKey(uint entity, int index) =>
		((ulong)entity << 32) | (uint)index;

	/// <summary>
	/// Resolves all placed light instances and lamp-host placement hashes for a level scenario using the
	/// exact 5-step dummy fallback chain and synthetic flame-FX light synthesis.
	/// </summary>
	public static List<ResolvedLightInstance> ResolvePlacements(
		GdbView levelView,
		List<GdbView> supplementalViews,
		List<GdbPlacement> placements,
		Dictionary<uint, string>? modelPaths,
		HashSet<uint> hosts,
		out int duplicate,
		out int duplicateElsewhere)
	{
		var views = new List<GdbView>(supplementalViews.Count + 1) { levelView };
		views.AddRange(supplementalViews);

		var resolved = new List<ResolvedLightInstance>();
		var emitted = new Dictionary<(uint Entity, int Index), Vector3>();
		var synthIndices = new Dictionary<uint, int>();
		List<SyntheticFlameCandidate>? flameCandidates = null;
		int dup = 0;
		int dupElsewhere = 0;

		bool Emit(uint entity, in LightPlacement placement, Transform3D at, string? name, int index)
		{
			var key = (entity, index);
			if (!emitted.TryAdd(key, at.Origin))
			{
				dup++;
				if (emitted[key].DistanceSquaredTo(at.Origin) > 0.01f)
				{
					dupElsewhere++;
				}

				return false;
			}

			resolved.Add(new ResolvedLightInstance(
				entity,
				index,
				ComposeLightKey(entity, index),
				placement,
				at,
				name));
			return true;
		}

		static bool TryResolveDummy(string model, string dummy, out Transform3D bone)
		{
			if (Engine_LevelFx.TryDummyTransform(model, dummy, out bone))
			{
				return true;
			}

			if (Engine_LevelProps.CompanionPath(model) is { Length: > 0 } companion &&
			    Engine_LevelFx.TryDummyTransform(companion, dummy, out bone))
			{
				return true;
			}

			bone = Transform3D.Identity;
			return false;
		}

		foreach (GdbPlacement placement in placements)
		{
			bool hasAuthored = TryRead(placement.Hash, views, out LightPlacement light) && light.Active;
			if (!hasAuthored)
			{
				// Placements that lack GraphicAppearanceLightComponent in GDB (e.g. market stall lanterns,
				// hanging lanterns, wall braziers) but carry visual flame particle attachments.
				List<Engine_LevelFx.FxAttachment> fxList = Engine_LevelFx.HuntFxAttachments(placement.Hash, views);
				if (fxList.Count == 0)
				{
					continue;
				}

				string? propModel = null;
				modelPaths?.TryGetValue(placement.Hash, out propModel);
				if (propModel is null || propModel.Length == 0)
				{
					continue;
				}

				Transform3D placedXform = Engine_LevelProps.BuildTransform(placement);
				var usedBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

				for (int f = 0; f < fxList.Count; f++)
				{
					Engine_LevelFx.FxAttachment fx = fxList[f];
					if (!IsFlameDummy(fx.Dummy))
					{
						continue;
					}

					string fxBone = Engine_LevelFx.DummyBoneName(fx.Dummy);
					string trackKey = fxBone.Length != 0 ? fxBone : fx.Dummy;
					if (!usedBones.Add(trackKey))
					{
						continue;
					}

					if (!TryResolveDummy(propModel, fx.Dummy, out Transform3D at) &&
					    (fxBone.Length == 0 || !TryResolveDummy(propModel, fxBone, out at)))
					{
						continue;
					}

					Transform3D xform = placedXform * at;
					if (fx.Offset != Vector3.Zero)
					{
						xform *= new Transform3D(Basis.Identity, -fx.Offset);
					}

					bool isLantern = fx.Dummy.Contains("Lamp", StringComparison.OrdinalIgnoreCase) ||
					                 fx.Dummy.Contains("Lantern", StringComparison.OrdinalIgnoreCase);
					Vector3 col = isLantern
						? new Vector3(1.0f, 0.36f, 0.11f)
						: new Vector3(1.0f, 0.34f, 0.06f);
					float intensity = isLantern ? 1.00f : 0.85f;
					float range = isLantern ? 8.0f : 7.0f;

					var synthetic = new LightPlacement(
						ColourLinear: col,
						Intensity: intensity,
						Range: range,
						Type: LightTypePoint,
						HalfAngleRadians: 0f,
						FadeDistance: 0.0f,
						FadeRange: 0.0f,
						CastShadows: false,
						Active: true,
						Dummy: fx.Dummy,
						FlickerRate: 0.1f,
						FlickerBrightnessRange: 0.23f,
						FlickerRadiusRange: 0.21f,
						IgnoreZoning: false,
						IsSyntheticFlame: true
					);

					flameCandidates ??= new List<SyntheticFlameCandidate>();
					flameCandidates.Add(new SyntheticFlameCandidate(placement.Hash, placement.EntityName, synthetic, xform));
				}

				continue;
			}

			Transform3D placed = Engine_LevelProps.BuildTransform(placement);

			string? model = null;
			modelPaths?.TryGetValue(placement.Hash, out model);
			bool isBuildingRoomModel = model is not null && (
				model.Contains("building", StringComparison.OrdinalIgnoreCase) ||
				model.Contains("interior", StringComparison.OrdinalIgnoreCase) ||
				model.Contains("chamber", StringComparison.OrdinalIgnoreCase));

			if (model is not null && model.Length != 0 && !isBuildingRoomModel)
			{
				hosts.Add(placement.Hash);
			}

			// The entity's own origin, which is what AttachToEntityPosition asks for and also the
			// only thing left when the placement resolved to no model at all.
			if (light.Dummy.Length == 0 || model is null || model.Length == 0)
			{
				Emit(placement.Hash, light, placed, placement.EntityName, 0);
				continue;
			}

			// EVERY dummy of that name.
			var flameOrigins = new List<Vector3>();
			List<Engine_LevelFx.FxAttachment> modelFxList = Engine_LevelFx.HuntFxAttachments(placement.Hash, views);
			for (int f = 0; f < modelFxList.Count; f++)
			{
				Engine_LevelFx.FxAttachment fx = modelFxList[f];
				if (!IsFlameDummy(fx.Dummy))
				{
					continue;
				}

				if (TryResolveDummy(model, fx.Dummy, out Transform3D fxAt))
				{
					flameOrigins.Add(fxAt.Origin - fx.Offset);
				}

				string fxBone = Engine_LevelFx.DummyBoneName(fx.Dummy);
				if (fxBone.Length != 0 && TryResolveDummy(model, fxBone, out Transform3D boneAt))
				{
					flameOrigins.Add(boneAt.Origin - fx.Offset);
				}
			}

			for (int pIdx = 0; pIdx < DummyScanLimit; pIdx++)
			{
				string pNamed = pIdx == 0 ? "FX_Particle_DummyObject" : "FX_Particle_DummyObject" + pIdx;
				if (TryResolveDummy(model, pNamed, out Transform3D pAt))
				{
					flameOrigins.Add(pAt.Origin);
				}
			}

			string bone = Engine_LevelFx.DummyBoneName(LightDummy);
			int index = 0;
			for (; index < DummyScanLimit; index++)
			{
				string dotted = index == 0 ? LightDummy : LightDummy + index;
				string named = index == 0 ? bone : bone + index;
				if (!TryResolveDummy(model, dotted, out Transform3D at) &&
				    !TryResolveDummy(model, named, out at))
				{
					break;
				}

				if (flameOrigins.Count != 0)
				{
					at = AlignLightDummyToFlame(at, flameOrigins);
				}

				Emit(placement.Hash, light, placed * at, placement.EntityName, index);
			}

			// Fallback 1: Authored FX attachments.
			if (index == 0)
			{
				List<Engine_LevelFx.FxAttachment> fxList =
					Engine_LevelFx.HuntFxAttachments(placement.Hash, views);
				var usedBones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (int f = 0; f < fxList.Count; f++)
				{
					Engine_LevelFx.FxAttachment fx = fxList[f];
					if (fx.Dummy.Length == 0)
					{
						continue;
					}

					string fxBone = Engine_LevelFx.DummyBoneName(fx.Dummy);
					string trackKey = fxBone.Length != 0 ? fxBone : fx.Dummy;
					if (!usedBones.Add(trackKey))
					{
						continue;
					}

					if (!TryResolveDummy(model, fx.Dummy, out Transform3D at) &&
					    (fxBone.Length == 0 || !TryResolveDummy(model, fxBone, out at)))
					{
						continue;
					}

					Transform3D xform = placed * at;
					if (fx.Offset != Vector3.Zero)
					{
						xform *= new Transform3D(Basis.Identity, -fx.Offset);
					}

					Emit(placement.Hash, light with { Dummy = fx.Dummy }, xform, placement.EntityName, index);
					index++;
				}
			}

			// Fallback 2: Generic particle dummy objects on the model rig or sidecar
			if (index == 0)
			{
				const string particleDotted = "Prop.FX.Particle";
				const string particleDummy = "FX_Particle_DummyObject";
				for (int pIdx = 0; pIdx < DummyScanLimit; pIdx++)
				{
					string dotted = pIdx == 0 ? particleDotted : particleDotted + pIdx;
					string named = pIdx == 0 ? particleDummy : particleDummy + pIdx;
					if (!TryResolveDummy(model, dotted, out Transform3D at) &&
					    !TryResolveDummy(model, named, out at))
					{
						break;
					}

					Emit(placement.Hash, light with { Dummy = dotted }, placed * at, placement.EntityName, index);
					index++;
				}
			}

			// Fallback 3: Prop.Layout.Position
			if (index == 0)
			{
				const string posDotted = "Prop.Layout.Position";
				string posBone = Engine_LevelFx.DummyBoneName(posDotted);
				if (TryResolveDummy(model, posDotted, out Transform3D at) ||
				    (posBone.Length != 0 && TryResolveDummy(model, posBone, out at)))
				{
					Emit(placement.Hash, light, placed * at, placement.EntityName, index);
					index++;
				}
			}

			// Fallback 4: Entity origin
			if (index == 0)
			{
				Emit(placement.Hash, light, placed, placement.EntityName, 0);
			}
		}

		if (flameCandidates is { Count: > 0 })
		{
			for (int i = 0; i < flameCandidates.Count; i++)
			{
				SyntheticFlameCandidate cand = flameCandidates[i];
				hosts.Add(cand.PlacementHash);
				int synthIndex = synthIndices.TryGetValue(cand.PlacementHash, out int existing) ? existing : 0;
				Emit(cand.PlacementHash, cand.Light, cand.Transform, cand.EntityName, synthIndex);
				synthIndices[cand.PlacementHash] = synthIndex + 1;
			}
		}

		ClusterCompatiblePointLights(resolved, modelPaths);

		duplicate = dup;
		duplicateElsewhere = dupElsewhere;
		return resolved;
	}

	/// <summary>
	/// Determines whether two point lights have compatible type, normalized linear color (chromaticity),
	/// fixture category, shadow state, and range to be collapsed when placed within the cluster envelope.
	/// </summary>
	public static bool AreLightsClusterCompatible(
		in LightPlacement a,
		string? entityNameA,
		string? modelPathA,
		in LightPlacement b,
		string? entityNameB,
		string? modelPathB)
	{
		if (a.Type != LightTypePoint || b.Type != LightTypePoint)
		{
			return false;
		}

		if (a.CastShadows != b.CastShadows || a.IgnoreZoning != b.IgnoreZoning)
		{
			return false;
		}

		// Keep lanterns/streetlamps distinct from open candle/flame clusters.
		if (IsLanternFixture(a, entityNameA, modelPathA) != IsLanternFixture(b, entityNameB, modelPathB))
		{
			return false;
		}

		// Range compatibility: prevent small candle lights from merging with large room fill lights.
		float minRange = MathF.Min(a.Range, b.Range);
		float maxRange = MathF.Max(a.Range, b.Range);
		if (minRange <= 0.0f || maxRange > minRange * 1.35f)
		{
			return false;
		}

		// Normalized chromaticity check: warm candles (e.g. (1.0, 0.34, 0.06)) must NEVER merge with
		// differently colored lights such as the blue ritual flame ((0.2, 0.5, 1.0)) in the Hall of Light.
		float lenA = a.ColourLinear.Length();
		float lenB = b.ColourLinear.Length();
		if (lenA <= 1e-5f || lenB <= 1e-5f)
		{
			return false;
		}

		Vector3 normA = a.ColourLinear / lenA;
		Vector3 normB = b.ColourLinear / lenB;
		if (normA.Dot(normB) < 0.985f)
		{
			return false;
		}

		float maxChannelDelta = MathF.Max(
			MathF.Abs(normA.X - normB.X),
			MathF.Max(MathF.Abs(normA.Y - normB.Y), MathF.Abs(normA.Z - normB.Z)));
		return maxChannelDelta <= 0.12f;
	}

	private static bool IsLanternFixture(in LightPlacement light, string? entityName, string? modelPath)
	{
		if (light.Dummy.Length != 0 &&
		    (light.Dummy.Contains("Lamp", StringComparison.OrdinalIgnoreCase) ||
		     light.Dummy.Contains("Lantern", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		if (entityName is { Length: > 0 } &&
		    (entityName.Contains("Lantern", StringComparison.OrdinalIgnoreCase) ||
		     entityName.Contains("Streetlight", StringComparison.OrdinalIgnoreCase) ||
		     entityName.Contains("LampPost", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		if (modelPath is { Length: > 0 } &&
		    (modelPath.Contains("Lantern", StringComparison.OrdinalIgnoreCase) ||
		     modelPath.Contains("Lights_Town", StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		return false;
	}

	private static void ClusterCompatiblePointLights(
		List<ResolvedLightInstance> resolved,
		Dictionary<uint, string>? modelPaths)
	{
		if (resolved.Count <= 1)
		{
			return;
		}

		var used = new bool[resolved.Count];
		var compacted = new List<ResolvedLightInstance>(resolved.Count);
		var clusterIndices = new List<int>(8);

		for (int i = 0; i < resolved.Count; i++)
		{
			if (used[i])
			{
				continue;
			}

			used[i] = true;
			ResolvedLightInstance seedInst = resolved[i];
			if (seedInst.Light.Type != LightTypePoint)
			{
				compacted.Add(seedInst);
				continue;
			}

			clusterIndices.Clear();
			clusterIndices.Add(i);

			Vector3 seedPos = seedInst.Transform.Origin;
			string? seedModel = null;
			modelPaths?.TryGetValue(seedInst.Entity, out seedModel);

			for (int j = i + 1; j < resolved.Count; j++)
			{
				if (used[j])
				{
					continue;
				}

				ResolvedLightInstance otherInst = resolved[j];
				string? otherModel = null;
				modelPaths?.TryGetValue(otherInst.Entity, out otherModel);

				if (!AreLightsClusterCompatible(
					seedInst.Light, seedInst.EntityName, seedModel,
					otherInst.Light, otherInst.EntityName, otherModel))
				{
					continue;
				}

				bool sameEntityFlame = otherInst.Entity == seedInst.Entity &&
				                       (seedInst.Light.IsSyntheticFlame || otherInst.Light.IsSyntheticFlame ||
				                        !seedInst.Light.Dummy.Equals(LightDummy, StringComparison.OrdinalIgnoreCase));

				Vector3 otherPos = otherInst.Transform.Origin;
				float dx = otherPos.X - seedPos.X;
				float dz = otherPos.Z - seedPos.Z;
				float dy = MathF.Abs(otherPos.Y - seedPos.Y);

				if (sameEntityFlame || ((dx * dx + dz * dz) <= FlameClusterRadiusSq && dy <= FlameClusterMaxVertical))
				{
					used[j] = true;
					clusterIndices.Add(j);
				}
			}

			if (clusterIndices.Count == 1)
			{
				compacted.Add(seedInst);
				continue;
			}

			Vector3 centroid = Vector3.Zero;
			for (int k = 0; k < clusterIndices.Count; k++)
			{
				centroid += resolved[clusterIndices[k]].Transform.Origin;
			}
			centroid /= clusterIndices.Count;

			Transform3D clusterXform = new Transform3D(seedInst.Transform.Basis, centroid);
			compacted.Add(seedInst with { Transform = clusterXform });
		}

		resolved.Clear();
		resolved.AddRange(compacted);
	}

	/// <summary>
	/// Resolves and attaches placed region lights to <paramref name="container"/>, returning the spawned count.
	/// </summary>
	public static int Spawn(
		Node3D container,
		GdbView levelView,
		List<GdbView> supplementalViews,
		List<GdbPlacement> placements,
		Dictionary<uint, string>? modelPaths = null) =>
		Spawn(container, levelView, supplementalViews, placements, modelPaths, out _, out _);

	/// <summary>
	/// Resolves and attaches placed region lights to <paramref name="container"/>, also returning the resolved
	/// <paramref name="resolvedLights"/> and <paramref name="resolvedLightHosts"/> so during-load static lighting
	/// bakes can reuse them without re-parsing GDB placements.
	/// </summary>
	public static int Spawn(
		Node3D container,
		GdbView levelView,
		List<GdbView> supplementalViews,
		List<GdbPlacement> placements,
		Dictionary<uint, string>? modelPaths,
		out List<ResolvedLightInstance> resolvedLights,
		out HashSet<uint> resolvedLightHosts)
	{
		var flicker = new Engine_LightFlicker { Name = Engine_LightFlicker.NodeName };
		var hosts = new HashSet<uint>();
		List<ResolvedLightInstance> resolved = ResolvePlacements(
			levelView,
			supplementalViews,
			placements,
			modelPaths,
			hosts,
			out int duplicate,
			out int duplicateElsewhere);
		resolvedLights = resolved;
		resolvedLightHosts = hosts;

		int spawned = 0;
		foreach (ResolvedLightInstance item in resolved)
		{
			Light3D node = Build(item.Light, item.Transform, item.EntityName, item.Index, item.Entity);
			container.AddChild(node);
			Engine_LightZones.AddLight(node, item.Entity, item.Light.IgnoreZoning);
			flicker.Add(node, item.Light);
			lock (FxRegistry)
			{
				FxRegistry.Add(node);
				_fxLightCacheFrame = ulong.MaxValue;
			}

			spawned++;
		}

		// The updater only earns its place if something actually flickers. Mounting an empty one
		// costs a `_Process` call per frame for an empty list.
		if (flicker.Count != 0)
		{
			container.AddChild(flicker);
		}
		else
		{
			flicker.QueueFree();
		}

		// The fixtures' own models leave every placed light's casters: see LampHostLayer.
		if (hosts.Count != 0)
		{
			MarkHosts(container, hosts);
			lock (HostRecords)
			{
				HostRecords.UnionWith(hosts);
			}
		}

		// The shadow budget rides along whenever any light spawned — it is what turns
		// ShadowEnabled on, so without it no fixture would ever cast.
		if (spawned != 0)
		{
			container.AddChild(new Engine_LightShadowBudget { Name = Engine_LightShadowBudget.NodeName });
		}

		// `duplicateElsewhere` nonzero means the key collapsed fixtures that stood apart.
		if (duplicate != 0)
		{
			GD.Print(
				$"[lights] {spawned} placed, {duplicate} duplicate placement(s) rejected " +
				$"({duplicateElsewhere} of them somewhere else)");
		}

		return spawned;
	}

	/// <summary>Placement records that carry a placed light on their own model, across regions.</summary>
	private static readonly HashSet<uint> HostRecords = new();

	/// <summary>
	/// Whether a placement record hosts a placed light. A guest-driven entity carries the same
	/// record as its static twin, and most town fixtures are drawn as the entity, so the feed
	/// asks this to keep a lantern out of its own lamp's shadow map.
	/// </summary>
	public static bool IsLampHost(uint record)
	{
		lock (HostRecords)
		{
			return HostRecords.Contains(record);
		}
	}

	/// <summary>What a guest-driven entity draws on: a lamp host follows <see cref="MarkHosts"/>.</summary>
	public static uint EntityLayersOf(uint record, bool includeDynamicCaster = true) =>
		IsLampHost(record)
			? LampHostLayerMask
			: (includeDynamicCaster ? (1u | EntityCasterLayerMask) : (1u | Refii.Fx.Engine_ModernDecals.StaticDecalReceiverLayerMask));

	/// <summary>Moves the prop nodes of <paramref name="hosts"/> onto <see cref="LampHostLayer"/>.</summary>
	private static void MarkHosts(Node container, HashSet<uint> hosts)
	{
		foreach (Node child in container.GetChildren())
		{
			if (child is GeometryInstance3D instance &&
				instance.HasMeta(Engine_PropStreamer.RecordMeta) &&
				hosts.Contains((uint)instance.GetMeta(Engine_PropStreamer.RecordMeta).AsInt64()))
			{
				// The reflected bit stays: a lamp is part of the world the water shows.
				instance.Layers = LampHostLayerMask | Refii.Level.Engine_ReflectionLayer.Mask;
			}
		}
	}

	/// <summary>How many same-named attachment points to look for before giving up.</summary>
	private const int DummyScanLimit = 8;

	/// <summary>Meta key on a placed light holding the data's own CastShadows flag.</summary>
	public const string AuthoredShadowMeta = "authored_shadow";

	/// <summary>Meta key on a placed omni light holding its volumetric fog scattering scale.</summary>
	public const string VolumetricFogScaleMeta = "volumetric_fog_scale";

	/// <summary>
	/// The render layer a light-bearing prop is moved to, and the one bit every placed light's
	/// ShadowCasterMask leaves out. A fixture must not shadow itself: its light hangs on the
	/// model's own Prop.Layout.Light dummy, inside the housing. The prop is still drawn and lit;
	/// only its shadow into placed lights is gone, and the sun's mask keeps it. Layers 20 and
	/// 21 are the FX passes', 18 the highlight's, 17 the entity casters' and 2 the water's.
	/// </summary>
	public const int LampHostLayer = 19;

	public const uint LampHostLayerMask = 1u << (LampHostLayer - 1);

	private static float? _placedScale;

	private static float? _placedRange;

	private static float? _buildingInteriorLightScale;

	private static float? _buildingInteriorReachScale;

	/// <summary>`building_interior_light_scale`: multiplier on placed light intensity inside town buildings in outdoor levels; 1.0 default.</summary>
	public static float BuildingInteriorLightScale
	{
		get
		{
			if (_buildingInteriorLightScale is null)
			{
				try
				{
					_buildingInteriorLightScale = Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _),
							"building_interior_light_scale", 1.0f),
						0.0f, 4.0f);
				}
				catch (Exception)
				{
					_buildingInteriorLightScale = 1.0f;
				}
			}

			return _buildingInteriorLightScale.Value;
		}
	}

	/// <summary>`building_interior_reach_scale`: multiplier on placed light reach inside town buildings in outdoor levels; 1.0 default.</summary>
	public static float BuildingInteriorReachScale
	{
		get
		{
			if (_buildingInteriorReachScale is null)
			{
				try
				{
					_buildingInteriorReachScale = Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _),
							"building_interior_reach_scale", 1.0f),
						0.25f, 4.0f);
				}
				catch (Exception)
				{
					_buildingInteriorReachScale = 1.0f;
				}
			}

			return _buildingInteriorReachScale.Value;
		}
	}

	/// <summary>
	/// The remaster's `placed_light_range`: a multiplier on every fixture's authored reach. The
	/// shipped torches light 5–6 m with a linear falloff, balanced for a 360 whose vertex bakes
	/// carried the glow between them; those bakes decode to ~0.016 here, so a cave floor between
	/// two braziers was black. 1.0 is the shipped data; the falloff shape is untouched.
	/// </summary>
	public static float PlacedRange
	{
		get
		{
			if (_placedRange is null)
			{
				try
				{
					float configured = Refii.Core.Engine_RefiiConfig.Float(
						Refii.Core.Engine_RefiiConfig.Read(out _), "placed_light_reach_scale", 1.0f);
					_placedRange = Math.Clamp(configured, 0.25f, 4.0f);
				}
				catch (Exception)
				{
					_placedRange = 1.0f;
				}
			}

			return _placedRange.Value;
		}
	}

	/// <summary>`placed_light_range`: metres from eye to fixture beyond which fixtures fade and cull; 60.0 default.</summary>
	public static float PlacedLightRange()
	{
		try
		{
			return Math.Clamp(Refii.Core.Engine_RefiiConfig.Float(
				Refii.Core.Engine_RefiiConfig.Read(out _), "placed_light_range", 60.0f), 10.0f, 300.0f);
		}
		catch (Exception)
		{
			return 60.0f;
		}
	}

	public static float PlacedScale
	{
		get
		{
			if (_placedScale is null)
			{
				try
				{
					_placedScale = Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _),
							"placed_light_scale", 1.0f),
						0.0f, 4.0f);
				}
				catch (Exception)
				{
					_placedScale = 1.0f;
				}
			}

			return _placedScale.Value;
		}
	}

	private static bool? _placedShadows;

	private static bool? _placedFade;

	/// <summary>`placed_light_fade`: whether fixtures fade and cull by distance in the modern model.</summary>
	public static bool PlacedFadeEnabled
	{
		get
		{
			if (_placedFade is null)
			{
				try
				{
					_placedFade = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _), "placed_light_fade", true);
				}
				catch (Exception)
				{
					_placedFade = true;
				}
			}

			return _placedFade.Value;
		}
	}

	/// <summary>`placed_shadow_range`: metres from eye to light sphere beyond which shadows fade; 50.0 default.</summary>
	public static float PlacedShadowRange()
	{
		try
		{
			return Math.Clamp(Refii.Core.Engine_RefiiConfig.Float(
				Refii.Core.Engine_RefiiConfig.Read(out _), "placed_shadow_range", 50.0f), 5.0f, 200.0f);
		}
		catch (Exception)
		{
			return 50.0f;
		}
	}

	/// <summary>
	/// `placed_light_shadows`, ON by default: every fixture competes for one of the
	/// <see cref="Engine_LightShadowBudget.Budget"/> maps. OFF leaves only a spot whose
	/// component states CastShadows, the 360's own set of 83.
	/// </summary>
	public static bool PlacedShadowsEnabled => PlacedShadows;

	/// <summary>
	/// The render layer every guest-driven entity carries beside layer 1, and the only one a
	/// fixture WITHOUT the authored CastShadows flag draws into its map: a character's shadow
	/// under every torch for a handful of draws, where the static world is six passes over
	/// everything in reach. `placed_light_shadows_world` widens the mask to the world.
	/// </summary>
	public const int EntityCasterLayer = 17;

	public const uint EntityCasterLayerMask = 1u << (EntityCasterLayer - 1);

	/// <summary>
	/// Render layer 11: indoor dynamic character shadow-caster stand-in for <see cref="EntityCasterLayerMask"/>.
	/// Paired with <see cref="Engine_LightZones.IndoorMask"/> on indoor character shadow proxies and excluded from
	/// outdoor light cull masks so <c>RendererSceneCull</c> pairs indoor movers with indoor lights (dirtying their
	/// shadow maps on every step) without leaking character shadows through building walls in either direction.
	/// </summary>
	public const int IndoorEntityCasterLayer = 11;

	public const uint IndoorEntityCasterMask = 1u << (IndoorEntityCasterLayer - 1);

	public const string BaseEnergyMeta = "base_energy";

	/// <summary>
	/// Bumped whenever a placed light's stated values change in place — its base energy, its
	/// reach, a scale reload — so a reader caching them per light (the shadow budget) re-reads
	/// once instead of asking every light every pass.
	/// </summary>
	public static int LightMetaGeneration { get; private set; }

	public static void BumpLightMetaGeneration() => LightMetaGeneration++;

	/// <summary>
	/// Static architecture and rigid props (outdoor Layer 1 + indoor Layer 14), excluding lamp-host
	/// fixtures (<see cref="LampHostLayerMask"/> and <see cref="Engine_LightZones.IndoorLampHostMask"/>)
	/// and dynamic moving character proxies (<see cref="EntityCasterLayerMask"/>).
	/// </summary>
	public const uint StaticCasterMask = 1u | Engine_LightZones.IndoorMask;

	/// <summary>
	/// What a placed light draws into its shadow map: all static walls, floors, ceilings and rigid
	/// props (<see cref="StaticCasterMask"/>, or <see cref="Engine_LightZones.IndoorMask"/> alone
	/// for indoor building lights in outdoor levels so they never cull/draw outdoor street geometry)
	/// when <paramref name="includeStaticWorld"/> is true, plus dynamic moving character proxies
	/// (<see cref="EntityCasterLayerMask"/>) when <paramref name="includeDynamicEntities"/> is true.
	/// Never includes lamp-host layers so lights never cast shadows on themselves.
	/// </summary>
	public static uint CasterMaskOf(
		bool includeDynamicEntities = true,
		bool isIndoorLight = false,
		bool includeStaticWorld = true,
		bool isOutdoorLight = false)
	{
		uint mask = 0u;
		if (includeStaticWorld)
		{
			mask |= (isIndoorLight && !Engine_LightingFrame.CurrentInterior)
				? Engine_LightZones.IndoorMask
				: StaticCasterMask;
		}

		if (includeDynamicEntities)
		{
			if (isIndoorLight && !Engine_LightingFrame.CurrentInterior)
			{
				mask |= IndoorEntityCasterMask;
			}
			else if (isOutdoorLight && !Engine_LightingFrame.CurrentInterior)
			{
				mask |= EntityCasterLayerMask;
			}
			else
			{
				mask |= EntityCasterLayerMask | IndoorEntityCasterMask;
			}
		}

		return Refii.Level.Engine_ReflectionLayer.CasterMask(mask);
	}

	private static bool PlacedShadows
	{
		get
		{
			if (_placedShadows is null)
			{
				try
				{
					_placedShadows = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"placed_light_shadows", true);
				}
				catch (Exception)
				{
					_placedShadows = true;
				}
			}

			return _placedShadows.Value;
		}
	}

	/// <summary>
	/// The shadow filter of every placed light: one source for <see cref="Build"/> and the live
	/// `render` lever. The defaults are Godot 4.7.1's own (light_3d.cpp), so untouched they
	/// change nothing. A NaN bias is the per-type default, omni 0.1 and spot 0.03.
	/// </summary>
	public static float PlacedShadowBlur = 1.0f;

	public static float PlacedShadowBias = float.NaN;

	public static float PlacedShadowNormalBias = 0.8f;

	/// <summary>
	/// Placed lights MUST use <see cref="Light3D.BakeMode.Dynamic"/>, never <see cref="Light3D.BakeMode.Static"/>.
	/// In Godot 4's SDFGI, scrolling a cascade shifts surviving voxels' <c>process_voxels.data[i].light</c>
	/// via <c>PRE_PROCESS_SCROLL</c> and then dispatches <c>DIRECT_LIGHT_MODE_STATIC</c> across ALL solid
	/// voxels in the cascade whenever any static light intersects the newly exposed scroll slab, adding
	/// static light energy into <c>process_voxels.data[i].light</c> repeatedly on every scroll and causing
	/// walls to glow progressively brighter as the player moves back and forth. Dynamic mode keeps
	/// <c>process_voxels</c> read-only and evaluates live light energy (including flicker and zone fades)
	/// directly into <c>light_tex</c> without accumulation.
	/// </summary>
	public const Light3D.BakeMode PlacedLightBakeMode = Light3D.BakeMode.Dynamic;

	private static void ApplyShadowTuning(Light3D node)
	{
		node.ShadowBlur = PlacedShadowBlur;
		node.ShadowNormalBias = PlacedShadowNormalBias;
		node.ShadowBias = float.IsNaN(PlacedShadowBias)
			? (node is SpotLight3D ? 0.03f : 0.05f)
			: PlacedShadowBias;
	}

	/// <summary>Writes the tuning to every live placed light. Main thread only.</summary>
	public static void RetuneShadows()
	{
		var lights = new List<Light3D>();
		SnapshotLights(lights);
		foreach (Light3D light in lights)
		{
			ApplyShadowTuning(light);
		}
	}

	private static Light3D Build(
		LightPlacement light, Transform3D at, string? entityName, int index, uint entity = 0u)
	{
		// THE COLOUR AND THE INTENSITY STAY SEPARATE, which is what lets this reach our shader
		// unchanged. The guest premultiplies them — `825c2180 fmuls f0, f0, f13` — but Godot
		// multiplies LightColor by LightEnergy on its way into LIGHT_COLOR, so handing the tint to
		// one and the scalar to the other reproduces the same product. `fable_direct` then divides
		// LIGHT_COLOR by PI to undo Godot's own multiply, exactly as it already does for the sun,
		// so a placed light and the main light travel identical arithmetic from here.
		// THE TINT GOES IN ENCODED (Engine_LightColour): LightColor is nonlinear sRGB to Godot and
		// the renderer decodes it. Assigned raw, a warm lamp (1, 0.47, 0.06) shaded as
		// (1, 0.19, 0.005) — every candle-lit wall deep red, and bright enough to bloom.
		var colour = new Color(light.ColourLinear.X, light.ColourLinear.Y, light.ColourLinear.Z);

		// Godot's native falloff exponent (1.0: `((1 - (d/R)^4)^2) / d`, clamped at <= 1.0 for d < 1m
		// in `fable_attenuation`) gives natural physical distance decay while reaching smoothly to
		// the authored range R.
		Light3D node = light.Type == LightTypeSpot
			? new SpotLight3D
			{
				// Godot's SpotAngle IS the half angle, in degrees — the same quantity the field
				// names, which is why this converts back out of the radians the component stores.
				SpotAngle = Mathf.RadToDeg(light.HalfAngleRadians),
				SpotRange = light.Range * PlacedRange,
				SpotAttenuation = 1.0f,

				// ONE, because at exactly one Godot's cone ramp IS the guest's. Godot computes
				// `1 - ((1 - cos)/(1 - K))^p`, which for p = 1 rearranges to `(cos - K)/(1 - K)`
				// — the guest's `saturate((cos - K) * M)` with the M its spot fill computes as
				// `1/(1 - cos(HalfAngle))` at 0x82A6CFB0. Any other exponent bends a curve that
				// currently matches exactly.
				SpotAngleAttenuation = 1.0f,
				// Zero on the Godot node: `Engine_PostFxEffect` evaluates volumetric light shafts
				// against `fable_static_shadow_atlas` and dynamic character capsules so cones never
				// bleed through solid walls or pillars.
				LightVolumetricFogEnergy = 0.0f,
			}
			: new OmniLight3D
			{
				OmniRange = light.Range * PlacedRange,
				OmniAttenuation = 1.0f,
				OmniShadowMode = OmniLight3D.ShadowMode.Cube,
				// Zero on the Godot node: Godot's built-in omni volumetric loop does not sample
				// `fable_static_shadow_atlas` and would bleed a 360-degree sphere through solid walls.
				// `Engine_PostFxEffect` evaluates omni volumetric scattering and static-shadow
				// light shafts directly against `fable_static_shadow_atlas`.
				LightVolumetricFogEnergy = 0.0f,
			};

		string label = string.IsNullOrEmpty(entityName)
			? (entity != 0u ? $"{entity:X8}" : "?")
			: (entity != 0u ? $"{entityName}_{entity:X8}" : entityName);
		node.Name = $"Light_{label}_{index}";
		node.SetMeta(RawIntensityMeta, light.Intensity);
		node.SetMeta(RawRangeMeta, light.Range);
		node.SetMeta(RawFadeDistanceMeta, light.FadeDistance);
		node.SetMeta(RawFadeRangeMeta, light.FadeRange);
		node.SetMeta(VolumetricFogScaleMeta, DefaultVolumetricFogScale);
		ComputeEffectiveLightValues(node, out float initialEnergy, out float initialRange);
		node.SetMeta(AuthoredRangeMeta, initialRange);
		if (node is OmniLight3D omniInit)
		{
			omniInit.OmniRange = initialRange * RangeScale;
		}
		else if (node is SpotLight3D spotInit)
		{
			spotInit.SpotRange = initialRange * RangeScale;
		}
		// The component as authored, for the classic light list where no guest states one.
		Refii.Models.Engine_ClassicPlacedLights.Stamp(node, light.ColourLinear, light.Intensity, light.Range,
			light.Type == LightTypeSpot, light.HalfAngleRadians, light.FadeDistance, light.FadeRange,
			light.FlickerBrightnessRange, light.FlickerRadiusRange, light.FlickerRate, light.CastShadows);
		Engine_LightColour.Apply(node, colour);

		// The remaster's `placed_light_scale` (and `building_interior_light_scale`).
		node.LightEnergy = initialEnergy;
		node.SetMeta(BaseEnergyMeta, initialEnergy);

		// SHADOWS ARE A BUDGET, NOT A FLAG. Enabling every fixture's shadow blew past Godot's
		// positional shadow atlas — with ~190 shadow-enabled omnis in a town the atlas serves a
		// few dozen and silently drops the rest, exactly where the lamps are densest. The
		// authored flag is recorded here; Engine_LightShadowBudget hands live shadow maps to
		// the nearest few fixtures each frame instead.
		node.ShadowEnabled = false;
		node.ShadowOpacity = 0.0f;
		node.SetMeta(AuthoredShadowMeta, light.CastShadows);
		node.ShadowCasterMask = CasterMaskOf(
			includeDynamicEntities: !Refii.Models.Engine_ClassicMaterial.Active,
			includeStaticWorld: Refii.Models.Engine_ClassicMaterial.Active);
		ApplyShadowTuning(node);

		// Dynamic GI injection avoids Godot 4's SDFGI static-light scroll accumulation bug
		// (where MODE_PROCESS_STATIC additively overwrites process_voxels[].light on every cascade
		// scroll) and ensures flicker, time-of-day, and zone fades update GI live.
		node.LightBakeMode = PlacedLightBakeMode;
		node.Transform = at;

		// Registered in the light table, so the shader measures the distance and takes the shadow
		// on its own instead of inverting an attenuation the shadow is multiplied into.
		Engine_LightTable.Register(node, ComposeLightKey(entity, index), fixedPlacement: true);

		// FADEDISTANCE IS THE CAMERA'S, NOT THE LIGHT'S, and reading it as a second radius is the
		// mistake the numbers themselves rule out: the shipped fixtures state Range 8 with
		// FadeDistance 40 and FadeRange 20, so a "falloff" starting at 40 would never begin inside
		// a light that ends at 8. It is the distance at which the light stops being drawn at all —
		// `sub_82A49D08` takes Range, FadeDistance and FadeRange as three separate arguments — and
		// Godot's distance fade is the same control.
		// THE GUEST REACHES ZERO AT FadeDistance, NOT AT FadeDistance + FadeRange. Its live
		// update computes clamp((min(FadeDistance, 1000) − dist) / FadeRange, 0, 1) × intensity
		// (`sub_821F0A50`, fdivs at 0x821f0d38), so the ramp STARTS at FadeDistance − FadeRange
		// and the draw-skip (`sub_821FFB30`) cuts at the same FadeDistance. Godot's begin+length
		// therefore maps as begin = FadeDistance − FadeRange, length = FadeRange. Godot also
		// CULLS entirely past begin+length, so a zero FadeRange is a hard pop — authentic in
		// fable mode.
		//
		// The modern model doubles the cutoff and ramps over at least half of it: the authored
		// reach was balanced for the 360, where the BAKES carried lamplight at distance; we run
		// the bakes at a residual weight and hand that job to the live light.
		// ALL LIGHTS ON, ALL THE TIME, in the modern model unless `placed_light_fade` asks for
		// the cutoff: a fixture switching off as the camera walks away reads as a fault on a
		// machine that can afford every fixture in the region.
		ApplyDistanceFade(node, light.FadeDistance, light.FadeRange);

		return node;
	}

	private static float ReadFloat(List<GdbView> views, uint record, uint field, float fallback) =>
		Engine_LevelFx.Inherited(views, record, field, GdbFieldType.Float) is { } found &&
		float.IsFinite(found.AsFloat)
			? found.AsFloat
			: fallback;

	/// <summary>
	/// A whole-number field in any of the three spellings the shipped data uses for one.
	/// </summary>
	/// <remarks>
	/// <b>ENUM IS NOT OPTIONAL HERE, and leaving it out silently deletes every light in the game.</b>
	/// The guest reads <c>LightType</c> and the three colour channels through the SAME getter
	/// (<c>sub_823BF820</c>, called at 825c0d08/0d1c/0d30 for Red/Green/Blue and at 825c0d58 for
	/// LightType), so as far as the engine is concerned they are one kind of field. The data does
	/// not agree about how to write them: the colour channels are stored as GDB ints, and
	/// <c>LightType</c> is stored as an <b>enum</b> — the shipped template
	/// <c>GraphicAppearanceLightComponent</c> (0xF6F2F75F) states <c>LightType[Enum]=2</c>.
	///
	/// Asking only for Int found nothing, every light read back as type 0, and type 0 is the value
	/// that means "build no primitive" — so a walk that was reaching 125 of BWSSlums' 2500
	/// placements correctly reported zero lights. The count was right and the answer was empty,
	/// which is exactly the failure a survey exists to catch before it reaches the screen.
	///
	/// The float spelling is kept as a third try for the colour channels, so a value authored as
	/// 255.0 does not read as absent and quietly default to white.
	/// </remarks>
	private static int ReadInt(List<GdbView> views, uint record, uint field, int fallback)
	{
		foreach (GdbFieldType spelling in
			new[] { GdbFieldType.Int, GdbFieldType.Enum, GdbFieldType.Float })
		{
			if (Engine_LevelFx.Inherited(views, record, field, spelling) is not { } found)
			{
				continue;
			}

			return spelling == GdbFieldType.Float
				? float.IsFinite(found.AsFloat) ? (int)found.AsFloat : fallback
				: (int)found.Raw;
		}

		return fallback;
	}

	private static bool ReadBool(List<GdbView> views, uint record, uint field, bool fallback) =>
		Engine_LevelFx.Inherited(views, record, field, GdbFieldType.Bool) is { } found
			? found.Raw != 0
			: fallback;
}
