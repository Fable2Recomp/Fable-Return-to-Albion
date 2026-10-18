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
		float FlickerRadiusRange);

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

		light = new LightPlacement(
			colour,
			ReadFloat(views, component, Intensity, DefaultIntensity),
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
			ReadFloat(views, component, FlickeringRadiusRange, 0.0f));

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

	/// <summary>The fixture's reach as BUILT, before any live scaling, kept so a sweep can be
	/// relative to the authored value rather than to whatever the last sweep left.</summary>
	public const string AuthoredRangeMeta = "refii_authored_range";

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

	/// <summary>Sum of nearby placed lights at a WORLD position, linear falloff, into `lit`.</summary>
	public static void AddLightsAt(Vector3 world, float[] lit)
	{
		lock (FxRegistry)
		{
			for (int i = FxRegistry.Count - 1; i >= 0; i--)
			{
				Light3D node = FxRegistry[i];
				if (!GodotObject.IsInstanceValid(node) || !node.IsInsideTree())
				{
					FxRegistry.RemoveAt(i);
					continue;
				}

				float range = node is SpotLight3D spot ? spot.SpotRange
					: node is OmniLight3D omni ? omni.OmniRange : 0f;
				if (range <= 0f)
				{
					continue;
				}

				// A flickering light's node holds its envelope; its falloff is the stated radius.
				range = Engine_LightTable.StatedRangeOf(node, range);
				float d = node.GlobalPosition.DistanceTo(world);
				float atten = 1f - (d / range);
				if (atten <= 0f)
				{
					continue;
				}

				Color c = node.LightColor;
				float e = node.LightEnergy * atten;
				lit[0] += c.R * e;
				lit[1] += c.G * e;
				lit[2] += c.B * e;
			}
		}
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
			if (TryRead(placement.Hash, views, out LightPlacement light) && light.Active)
			{
				bearing.Add(placement.Hash);
			}
		}

		return bearing;
	}

	public static int Spawn(
		Node3D container,
		GdbView levelView,
		List<GdbView> supplementalViews,
		List<GdbPlacement> placements,
		Dictionary<uint, string>? modelPaths = null)
	{
		var views = new List<GdbView>(supplementalViews.Count + 1) { levelView };
		views.AddRange(supplementalViews);

		int spawned = 0;
		var flicker = new Engine_LightFlicker { Name = Engine_LightFlicker.NodeName };
		var hosts = new HashSet<uint>();

		// ONE LIGHT PER (ENTITY, ATTACHMENT), not one per placement.
		// `Engine_LevelProps.AugmentPlacementModelHashes` splits a multi-model placement into one
		// entry PER MODEL PART, each with the same record hash and transform, so without this key
		// one fixture is built once per part. `Engine_LevelFx` guards the same list the same way.
		// The dummy index is in the key because a twin-headed post states two lights on two
		// dummies — a second fixture, not a repeat.
		var emitted = new Dictionary<(uint Entity, int Index), Vector3>();
		int duplicate = 0;
		int duplicateElsewhere = 0;

		bool Emit(uint entity, in LightPlacement placement, Transform3D at, string? name, int index)
		{
			var key = (entity, index);
			if (!emitted.TryAdd(key, at.Origin))
			{
				duplicate++;

				// A repeat at a different origin is counted apart: it would mean the key is
				// collapsing two real fixtures into one.
				if (emitted[key].DistanceSquaredTo(at.Origin) > 0.01f)
				{
					duplicateElsewhere++;
				}

				return false;
			}

			Light3D node = Build(placement, at, name, index);
			container.AddChild(node);
			Engine_LightZones.AddLight(node, entity);
			flicker.Add(node, placement);
			lock (FxRegistry)
			{
				FxRegistry.Add(node);
			}

			return true;
		}

		foreach (GdbPlacement placement in placements)
		{
			if (!TryRead(placement.Hash, views, out LightPlacement light) || !light.Active)
			{
				continue;
			}

			Transform3D placed = Engine_LevelProps.BuildTransform(placement);

			string? model = null;
			modelPaths?.TryGetValue(placement.Hash, out model);
			if (model is not null && model.Length != 0)
			{
				hosts.Add(placement.Hash);
			}

			// The entity's own origin, which is what AttachToEntityPosition asks for and also the
			// only thing left when the placement resolved to no model at all.
			if (light.Dummy.Length == 0 || model is null || model.Length == 0)
			{
				if (Emit(placement.Hash, light, placed, placement.EntityName, 0))
				{
					spawned++;
				}
				continue;
			}

			// EVERY dummy of that name. The numeric spelling is the one this tree already
			// established for the same attachment table — OB_MusicBox carries
			// FX_Particle_DummyObject beside FX_Particle_DummyObject1 — so a second light is
			// Layout_Light_DummyObject1. The scan stops at the first gap rather than probing a
			// fixed count, because the count is a property of the model.
			string bone = Engine_LevelFx.DummyBoneName(LightDummy);
			int index = 0;
			for (; index < DummyScanLimit; index++)
			{
				string named = index == 0 ? bone : bone + index;
				if (!Engine_LevelFx.TryDummyTransform(model, named, out Transform3D at))
				{
					break;
				}

				if (Emit(placement.Hash, light, placed * at, placement.EntityName, index))
				{
					spawned++;
				}
			}

			// A model that names no such dummy still carries the light — the component states it,
			// and dropping it because the rig is missing an attachment point loses the fixture
			// entirely rather than misplacing it by a metre.
			if (index == 0)
			{
				if (Emit(placement.Hash, light, placed, placement.EntityName, 0))
				{
					spawned++;
				}
			}
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
	public static uint EntityLayersOf(uint record) =>
		IsLampHost(record) ? LampHostLayerMask : 1u | EntityCasterLayerMask;

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
					_placedRange = Math.Clamp(
						Refii.Core.Engine_RefiiConfig.Float(
							Refii.Core.Engine_RefiiConfig.Read(out _), "placed_light_range", 1.5f),
						0.25f, 8.0f);
				}
				catch (Exception)
				{
					_placedRange = 1.5f;
				}
			}

			return _placedRange.Value;
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
							"placed_light_scale", 1.5f),
						0.0f, 4.0f);
				}
				catch (Exception)
				{
					_placedScale = 1.5f;
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

	/// <summary>`placed_light_budget`: fixtures kept visible at once; 64 default, 0 is every fixture.</summary>
	public static int PlacedLightBudget()
	{
		try
		{
			return Refii.Core.Engine_RefiiConfig.Read(out _).TryGetValue("placed_light_budget", out string? v) &&
				int.TryParse(v.Trim(), out int budget)
				? budget
				: 42;
		}
		catch (Exception)
		{
			return 42;
		}
	}

	/// <summary>`placed_light_shadow_budget`: fixtures casting at once; 0 is none.</summary>
	public static int PlacedShadowBudget()
	{
		try
		{
			return Math.Max(Refii.Core.Engine_RefiiConfig.Int(
				Refii.Core.Engine_RefiiConfig.Read(out _), "placed_light_shadow_budget", 8), 0);
		}
		catch (Exception)
		{
			return 8;
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

	/// <summary>What a placed light draws into its shadow map, by the data's own CastShadows flag.</summary>
	public static uint CasterMaskOf(bool authored) =>
		authored || PlacedShadowsWorld ? ~LampHostLayerMask : EntityCasterLayerMask;

	private static bool? _placedShadowsWorld;

	private static bool PlacedShadowsWorld
	{
		get
		{
			if (_placedShadowsWorld is null)
			{
				try
				{
					_placedShadowsWorld = Refii.Core.Engine_RefiiConfig.Bool(
						Refii.Core.Engine_RefiiConfig.Read(out _),
						"placed_light_shadows_world", false);
				}
				catch (Exception)
				{
					_placedShadowsWorld = false;
				}
			}

			return _placedShadowsWorld.Value;
		}
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

	public static float PlacedShadowNormalBias = 2.0f;

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
		LightPlacement light, Transform3D at, string? entityName, int index)
	{
		// THE COLOUR AND THE INTENSITY STAY SEPARATE, which is what lets this reach our shader
		// unchanged. The guest premultiplies them — `825c2180 fmuls f0, f0, f13` — but Godot
		// multiplies LightColor by LightEnergy on its way into LIGHT_COLOR, so handing the tint to
		// one and the scalar to the other reproduces the same product. `fable_direct` then divides
		// LIGHT_COLOR by PI to undo Godot's own multiply, exactly as it already does for the sun,
		// so a placed light and the main light travel identical arithmetic from here.
		var colour = new Color(light.ColourLinear.X, light.ColourLinear.Y, light.ColourLinear.Z);

		// THE TWO ATTENUATION EXPONENTS BELOW ARE LOAD-BEARING, not tidying. `fable_attenuation`
		// recovers the guest's linear ramp by inverting Godot's own falloff in closed form, and
		// that inversion is only valid for the ZERO-DECAY form `A = (1 - (d/r)^4)^2`. Godot's
		// default is 1.0, which multiplies in a `pow(d, -1)` term and makes the recovered distance
		// wrong everywhere — brightest, and most wrongly, close to the fixture.
		Light3D node = light.Type == LightTypeSpot
			? new SpotLight3D
			{
				// Godot's SpotAngle IS the half angle, in degrees — the same quantity the field
				// names, which is why this converts back out of the radians the component stores.
				SpotAngle = Mathf.RadToDeg(light.HalfAngleRadians),
				SpotRange = light.Range * PlacedRange,
				SpotAttenuation = 0.0f,

				// ONE, because at exactly one Godot's cone ramp IS the guest's. Godot computes
				// `1 - ((1 - cos)/(1 - K))^p`, which for p = 1 rearranges to `(cos - K)/(1 - K)`
				// — the guest's `saturate((cos - K) * M)` with the M its spot fill computes as
				// `1/(1 - cos(HalfAngle))` at 0x82A6CFB0. Any other exponent bends a curve that
				// currently matches exactly.
				SpotAngleAttenuation = 1.0f,
			}
			: new OmniLight3D
			{
				OmniRange = light.Range * PlacedRange,
				OmniAttenuation = 0.0f,
			};

		node.Name = $"Light_{entityName ?? "?"}_{index}";
		node.SetMeta(AuthoredRangeMeta, light.Range * PlacedRange);
		// The component as authored, for the classic light list where no guest states one.
		Refii.Models.Engine_ClassicPlacedLights.Stamp(node, light.ColourLinear, light.Intensity, light.Range,
			light.Type == LightTypeSpot, light.HalfAngleRadians, light.FadeDistance, light.FadeRange,
			light.FlickerBrightnessRange, light.FlickerRadiusRange, light.FlickerRate, light.CastShadows);
		node.LightColor = colour;

		// The remaster's `placed_light_scale`: the authored fixtures throw modest pools — a
		// lamp on a tall post barely reaches the ground it stands on. 1.0 is the shipped data.
		node.LightEnergy = light.Intensity * PlacedScale;

		// SHADOWS ARE A BUDGET, NOT A FLAG. Enabling every fixture's shadow blew past Godot's
		// positional shadow atlas — with ~190 shadow-enabled omnis in a town the atlas serves a
		// few dozen and silently drops the rest, exactly where the lamps are densest. The
		// authored flag is recorded here; Engine_LightShadowBudget hands live shadow maps to
		// the nearest few fixtures each frame instead.
		node.ShadowEnabled = false;
		node.ShadowOpacity = 0.0f;
		node.SetMeta(AuthoredShadowMeta, light.CastShadows);
		node.ShadowCasterMask = Refii.Level.Engine_ReflectionLayer.CasterMask(CasterMaskOf(light.CastShadows));
		ApplyShadowTuning(node);

		// A fixture never moves: the GI bakes it into a cascade when the cascade refreshes, where a
		// dynamic light is re-injected on every GI update. See ConfigureGlobalIllumination.
		node.LightBakeMode = Light3D.BakeMode.Static;
		node.Transform = at;

		// Registered in the light table, so the shader measures the distance and takes the shadow
		// on its own instead of inverting an attenuation the shadow is multiplied into.
		Engine_LightTable.Register(node);

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
		if (!Engine_LightingFrame.ModernModel || PlacedFadeEnabled)
		{
			bool authored = light.FadeDistance > 0.0f;
			if (authored || (Engine_LightingFrame.ModernModel && PlacedFadeEnabled))
			{
				float cutoff = authored ? MathF.Min(light.FadeDistance, 1000.0f) : 40.0f;
				float ramp = authored ? MathF.Max(light.FadeRange, 0.01f) : 15.0f;
				if (Engine_LightingFrame.ModernModel)
				{
					cutoff *= 2.0f;
					ramp = MathF.Max(ramp * 2.0f, cutoff * 0.4f);
				}

				node.DistanceFadeEnabled = true;
				node.DistanceFadeBegin = MathF.Max(cutoff - ramp, 0.0f);
				node.DistanceFadeLength = ramp;
				node.DistanceFadeShadow = cutoff;
			}
		}

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
