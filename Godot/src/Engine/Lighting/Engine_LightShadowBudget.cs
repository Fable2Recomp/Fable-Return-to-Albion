using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// Which placed lights carry a live shadow map: the nearest few whose pools are on screen,
/// faded in and out rather than switched, so a lamp's occlusion never pops.
/// </summary>
/// <remarks>
/// Godot serves positional shadows from one fixed atlas and re-renders every map every frame, so
/// each shadowed fixture is six (cube) or two (paraboloid) extra passes over everything inside
/// its range. A player inspects a handful of pools at once, so the
/// maps go to the fixtures whose pools are nearest the eye and inside the view.
///
/// The set is STICKY and the change is a FADE: a fixture already casting keeps its map while it
/// still ranks within the budget plus <see cref="Slack"/>, and a map arriving or leaving ramps
/// ShadowOpacity over <see cref="FadeSeconds"/>. The fade reads linearly only because
/// <c>fable_attenuation</c> takes the shadow from the light table's distance rather than from an
/// attenuation the shadow is already multiplied into.
///
/// PANNING NEVER CHURNS THE NEAR SET. A fixture within <see cref="NearEligibleMetres"/> of the
/// eye, or whose pool reaches the eye, is in view whatever the frustum says; one that leaves the
/// frustum is held for <see cref="LightHoldMs"/> (its map for <see cref="ShadowHoldMs"/>) before
/// it fades; and a map is ranked from the PLAYER, not the eye, so a camera orbit around the hero
/// does not re-rank the lamps around them.
///
/// With <c>placed_light_shadows</c> off only fixtures whose data states CastShadows compete —
/// the 83 shipped spots — so the authored behaviour survives under the remaster switch. What
/// a map draws is the light's own mask: <see cref="Engine_LevelLights.CasterMaskOf"/>.
///
/// EACH VIEW HAS THE WHOLE BUDGET. On split screen every view (<see cref="Refii.Core.Engine_Views"/>)
/// ranks visibility and maps from its own eye with its own limits, and the union is applied: a
/// lamp in either player's view is in view, and neither player's share shrinks for the other's.
///
/// ONE PASS A FRAME, WHATEVER THE REGION COUNT. Every streamed region that spawned a light mounts
/// one of these, and the registry it walks is the whole level's, so the state is static and the
/// first node to run a frame does the frame's work. Nothing here allocates per pass: the
/// per-fixture facts a pass needs (reach, fade end, base energy, zone, metadata) are read once
/// into <see cref="Fixture"/> and re-read only when the light set or its stated values change.
/// </remarks>
public partial class Engine_LightShadowBudget : Node
{
	public const string NodeName = "LightShadowBudget";

	/// <summary>Shadowed placed lights at once (legacy compatibility; range-based culling is active).</summary>
	public static int Budget = 0;

	/// <summary>
	/// Placed lights kept VISIBLE at once, the nearest that can reach; 0 keeps every one within
	/// <c>placed_light_range</c>. <c>placed_light_limit</c>.
	/// </summary>
	/// <remarks>
	/// Every visible light is shaded on every pixel of every object it reaches, and a town at
	/// night has dozens in range. Measured at the Bowerstone Market save spot on the S25+ at dusk:
	/// the nearest 24 took 7% off the GPU's frame work, 16 took 12% and 6 17%, but 16 already
	/// left lit shop windows across the square dark. Hidden lights fade out and back in, as on
	/// leaving their range.
	/// </remarks>
	public static int LightBudget = ConfiguredLightBudget();

	private static int ConfiguredLightBudget() => Math.Max(0, Refii.Core.Engine_RefiiConfig.Int(
		Refii.Core.Engine_RefiiConfig.Read(out _), "placed_light_limit", 0));

	/// <summary>Max static world shadow lights at once (legacy compatibility).</summary>
	public static int MaxStaticWorldShadowLights = 0;

	/// <summary>Max dynamic entity shadow lights at once (default 8; 0 uses uncapped).</summary>
	public static int MaxDynamicEntityShadowLights = 8;

	/// <summary>Of the dynamic shadow lights, the nearest N that use Cube shadows (default 4; remainder use DualParaboloid).</summary>
	public static int MaxCubeShadowLights = 4;

	/// <summary>Metres from the eye to a light beyond which its shadow is culled; <c>placed_shadow_range</c>.</summary>
	public static float MaxShadowDistance = Engine_LevelLights.PlacedShadowRange();

	/// <summary>Metres from the eye to a light beyond which it is not kept visible; <c>placed_light_range</c>.</summary>
	public static float MaxLightDistance = Engine_LevelLights.PlacedLightRange();

	/// <summary>Reloads lighting and shadow distance parameters from current config.</summary>
	public static void ReloadConfig()
	{
		MaxShadowDistance = Engine_LevelLights.PlacedShadowRange();
		MaxLightDistance = Engine_LevelLights.PlacedLightRange();
		LightBudget = ConfiguredLightBudget();
	}

	/// <summary>Debug lever: false hides every placed light regardless of the budget.</summary>
	public static bool LightsEnabled = true;

	/// <summary>
	/// Dual-paraboloid omni maps — two passes where a cube map is six — for every fixture whose
	/// map draws ENTITIES ONLY. On by default; the lever turns it off.
	/// </summary>
	/// <remarks>
	/// A positional shadow costs by the PASS, not the draw: each pass is its own cull, render
	/// list and setup on the main thread, and animated casters keep every map dirty. Measured
	/// 2026-09-21 in Fairfax Gardens with eight fixtures casting: cube 14.25 ms / 68 fps,
	/// paraboloid 9.23 ms / 107 fps at night (13.7 -> 10.6 ms by day), the draw count unchanged.
	/// The projection bends the straight edges of LARGE triangles, which is why a fixture whose
	/// map takes the world (`placed_light_shadows_world`) stays a cube; characters and small
	/// props have none, and the user judged it by eye at night and by day.
	/// </remarks>
	public static bool Paraboloid = false;

	/// <summary>Lights holding or fading in a shadow map after the last pass.</summary>
	public static int Casting;

	/// <summary>Lights that competed for a map on the last pass.</summary>
	public static int Eligible;

	/// <summary>Seconds between reassignments. Throttled to keep CPU overhead negligible.</summary>
	private const double Interval = 0.15;

	/// <summary>How long a map takes to arrive or leave.</summary>
	private const float FadeSeconds = 0.25f;

	/// <summary>Milliseconds a light stays in view after leaving the frustum, so a pan never pops it at the edge.</summary>
	public const ulong LightHoldMs = 1500;

	/// <summary>Milliseconds a map stays eligible after its light leaves the frustum, before it is taken.</summary>
	public const ulong ShadowHoldMs = 600;

	/// <summary>Ranks past the shadow budget a fixture already casting may fall before its map is taken.</summary>
	public const int Slack = 4;

	/// <summary>Ranks past the light budget a visible fixture may fall before it is hidden.</summary>
	public const int LightSlack = 12;

	/// <summary>Metres from the eye within which a light is in view whatever the frustum says.</summary>
	public const float NearEligibleMetres = 12.0f;

	/// <summary>A light whose pool reaches this far past the eye is in view whatever the frustum says.</summary>
	public const float NearEligibleBeyondRange = 4.0f;

	/// <summary>A map whose pool reaches this far past the PLAYER stays eligible whatever the frustum says.</summary>
	public const float ShadowNearPlayerBeyondRange = 10.0f;

	/// <summary>A map whose pool reaches this far past the eye stays eligible whatever the frustum says.</summary>
	public const float ShadowNearEyeBeyondRange = 6.0f;

	/// <summary>Metres a light's sphere is grown by for the frustum test, so an edge pool is seen before its rim.</summary>
	private const float LightFrustumMargin = 6.0f;

	/// <summary>Metres a shadowed light's sphere is grown by for the frustum test.</summary>
	private const float ShadowFrustumMargin = 2.0f;

	/// <summary>One placed fixture as the budget sees it: its stated values, read once, and its hold clocks.</summary>
	private struct Fixture
	{
		public Light3D Node;
		public bool Omni;

		/// <summary>Reach, metres — the node's OmniRange/SpotRange when last read.</summary>
		public float Range;

		/// <summary>Where the authored distance fade ends, or MaxValue without one.</summary>
		public float FadeEnd;

		/// <summary>The energy the light is lit at when fully visible (the base_energy meta).</summary>
		public float BaseEnergy;

		/// <summary>Whether the data states CastShadows.</summary>
		public bool Authored;

		/// <summary>The light zone the fixture was resolved into, 0 unresolved.</summary>
		public uint Zone;

		/// <summary>Whether the cull mask marks it an indoor light (bit 0 clear).</summary>
		public bool IndoorBit;

		/// <summary>Whether the cull mask marks it an outdoor-only light (bit 0 set, IndoorMask clear).</summary>
		public bool OutdoorBit;

		/// <summary>The shadow caster mask last stated to the node.</summary>
		public uint ShadowMask;

		/// <summary>Cached linear RGB tint of the light.</summary>
		public Vector3 LinearColor;

		/// <summary>Cached volumetric fog scattering multiplier for this fixture.</summary>
		public float VolumetricFogScale;

		/// <summary>World forward axis (-GlobalBasis.Z) for spotlights; zero for omni lights.</summary>
		public Vector3 SpotDir;

		/// <summary>Cosine of spotlight half-angle; -2.0f for omni lights.</summary>
		public float SpotCosHalfAngle;

		// This pass.
		public bool InTree;
		public Vector3 At;
		public bool Shown;
		public bool Casting;
		public byte VisibleBy;
		public byte WantedBy;
		public byte CubeBy;
		public byte ScoredBy;

		// Per view: when the fixture was last inside the frustum, 0 never.
		public ulong LightSeen0, LightSeen1, ShadowSeen0, ShadowSeen1;
	}

	private struct Candidate
	{
		public int Fixture;
		public float Key;
	}

	private sealed class CandidateOrder : IComparer<Candidate>
	{
		public static readonly CandidateOrder Instance = new();

		public int Compare(Candidate a, Candidate b) => a.Key.CompareTo(b.Key);
	}

	private static readonly List<Light3D> _lights = new();
	private static Fixture[] _fixtures = new Fixture[64];
	private static Fixture[] _spare = new Fixture[64];
	private static int _count;
	private static readonly Dictionary<Light3D, int> _index = new();
	private static Candidate[] _candidates = new Candidate[64];
	private static readonly Candidate[] _shaftCandidates = new Candidate[256];
	private static readonly Engine_VolumetricLightShafts.ShaftLightInput[] _shaftInputs =
		new Engine_VolumetricLightShafts.ShaftLightInput[Engine_VolumetricLightShafts.MaxShaftLights];
	private static readonly Engine_VolumetricLightShafts.MistVolumeSnapshot[] _mistInputs =
		new Engine_VolumetricLightShafts.MistVolumeSnapshot[Engine_VolumetricLightShafts.MaxMistVolumes];
	private static readonly Engine_VolumetricLightShafts.VolumetricCapsule[] _capsuleInputs =
		new Engine_VolumetricLightShafts.VolumetricCapsule[Engine_VolumetricLightShafts.MaxCapsules];
	private static int _metaGeneration = -1;
	private static int _zoneGeneration = -1;
	private static int _shaftMediumGen = -1;
	private static int _shaftAtlasGen = -1;

	private static readonly Dictionary<Light3D, float> _fading = new();
	private static readonly Dictionary<Light3D, float> _visibilityTarget = new();
	private static readonly Dictionary<Light3D, float> VisibilityLevel = new();
	private static readonly List<Light3D> _settled = new();
	private static readonly Camera3D[] _views = new Camera3D[2];
	private static int _viewsLast;
	private static int _wantedCount;
	private static double _since = double.MaxValue;
	private static readonly Vector3[] _lastCameraPos =
	{
		new(float.MaxValue, float.MaxValue, float.MaxValue), new(float.MaxValue, float.MaxValue, float.MaxValue),
	};
	private static readonly Vector3[] _lastCameraDir = new Vector3[2];
	private static readonly Plane[][] _frustumPlanes = { new Plane[6], new Plane[6] };

	private static ulong _tableFrame = ulong.MaxValue;
	private static ulong _passFrame = ulong.MaxValue;

	// Strings, not StringNames: a StringName is a native object, and this type's initializer runs
	// in the test host (ReloadConfig). The metas are read only when the fixture set is rebuilt.
	private const string AuthoredShadowName = Engine_LevelLights.AuthoredShadowMeta;
	private const string BaseEnergyName = Engine_LevelLights.BaseEnergyMeta;
	private const string VolumetricFogScaleName = Engine_LevelLights.VolumetricFogScaleMeta;

	/// <summary>Current smooth visibility fade factor in [0, 1] for <paramref name="light"/>.</summary>
	public static float VisibilityFactorOf(Light3D light) =>
		VisibilityLevel.TryGetValue(light, out float level) ? level : (light.Visible ? 1.0f : 0.0f);

	private static float BaseEnergyOf(Light3D light)
	{
		if (_index.TryGetValue(light, out int i))
		{
			return _fixtures[i].BaseEnergy;
		}

		return ReadBaseEnergy(light);
	}

	private static float ReadBaseEnergy(Light3D light) =>
		light.HasMeta(BaseEnergyName)
			? (float)light.GetMeta(BaseEnergyName).AsDouble()
			: light.LightEnergy;

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("lightshadowbudget");

		// One node does a frame's work; the others mounted by other regions walk the same registry.
		ulong frame = Engine.GetProcessFrames();
		if (_passFrame == frame)
		{
			return;
		}

		_passFrame = frame;

		// The light table refreshes every frame, once across however many regions carry a budget node: a
		// flickering torch's range moves every frame and the shader reads it from here.
		if (_tableFrame != frame)
		{
			_tableFrame = frame;
			using (Refii.Core.Engine_FramePhases.Measure("budget.table"))
			{
				Engine_LightTable.Refresh();
			}
		}

		using (Refii.Core.Engine_FramePhases.Measure("budget.fade"))
		{
			Fade((float)delta);
		}

		// The world's primary camera: the window's under one view, the first split view's under
		// two; player two's is Engine_Views.Second.
		Camera3D? camera = Refii.Core.Engine_Views.Primary(GetViewport()?.GetCamera3D());

		_since += delta;
		if (_since < Interval)
		{
			if (camera is not null)
			{
				UpdateVolumetricLightShafts(camera);
			}
			return;
		}

		_since = 0;

		if (camera is null)
		{
			return;
		}

		int views = 0;
		foreach (Camera3D view in Refii.Core.Engine_Views.Cameras(camera))
		{
			if (views < _views.Length)
			{
				_views[views++] = view;
			}
		}

		for (int v = views; v < _views.Length; v++)
		{
			_lastCameraPos[v] = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		}

		// A view that went away takes its hold clocks with it, so it never keeps another's lights.
		if (views < _viewsLast)
		{
			for (int i = 0; i < _count; i++)
			{
				_fixtures[i].LightSeen1 = 0;
				_fixtures[i].ShadowSeen1 = 0;
			}
		}

		_viewsLast = views;

		_shaftMediumGen = Engine_VolumetricLightShafts.MediumGeneration;
		_shaftAtlasGen = Engine_LevelShadowMaps.AtlasGeneration;

		for (int v = 0; v < views; v++)
		{
			_lastCameraPos[v] = _views[v].GlobalPosition;
			_lastCameraDir[v] = -_views[v].GlobalBasis.Z;
		}

		using (Refii.Core.Engine_FramePhases.Measure("budget.rank"))
		{
			Engine_LevelLights.SnapshotLights(_lights);
			if (_lights.Count == 0)
			{
				_count = 0;
				_index.Clear();
				Engine_VolumetricLightShafts.HideVolume();
				return;
			}

			SyncFixtures();
			Rank(views);
		}

		using (Refii.Core.Engine_FramePhases.Measure("budget.apply"))
		{
			Apply();
			UpdateVolumetricLightShafts(camera);
		}
	}

	private void UpdateVolumetricLightShafts(Camera3D camera)
	{
		bool hasMist = (Refii.Level.Engine_ModernMist.Instance?.Drawn ?? 0) > 0;
		if (!Engine_VolumetricLightShafts.HasActiveMedium && !hasMist)
		{
			Engine_VolumetricLightShafts.HideVolume();
			return;
		}

		bool classic = Refii.Models.Engine_ClassicMaterial.Active;
		if (classic || !LightsEnabled || _count == 0)
		{
			Engine_VolumetricLightShafts.HideVolume();
			return;
		}

		Vector3 eye = camera.GlobalPosition;
		Plane[] frustum = _frustumPlanes[0];
		FrustumOf(camera, frustum);
		uint heroZone = ZoneOfView(0);
		bool interior = Engine_LightingFrame.CurrentInterior;
		bool viewerIndoors = interior || Engine_LightZones.IsIndoors(heroZone);

		int candidateCount = 0;
		for (int i = 0; i < _count; i++)
		{
			ref Fixture f = ref _fixtures[i];
			if (!f.InTree || !f.Shown || f.BaseEnergy <= 0.0001f)
			{
				continue;
			}

			bool isIndoorLight = f.IndoorBit;
			if (ShouldCullForBuildingZone(viewerIndoors, interior, isIndoorLight, f.Zone, heroZone))
			{
				continue;
			}

			float dist = eye.DistanceTo(f.At);
			float maxDist = Engine_VolumetricLightShafts.MaxFogRangeMetres + f.Range;
			bool inFrustum = NearEligible(dist, f.Range) ||
				SphereInView(frustum, frustum.Length, f.At, f.Range + LightFrustumMargin);
			if (dist > maxDist || !inFrustum)
			{
				continue;
			}

			if (candidateCount < _shaftCandidates.Length)
			{
				_shaftCandidates[candidateCount++] = new Candidate { Fixture = i, Key = dist };
			}
		}

		if (candidateCount == 0)
		{
			Engine_VolumetricLightShafts.HideVolume();
			return;
		}

		if (candidateCount > 1)
		{
			Array.Sort(_shaftCandidates, 0, candidateCount, CandidateOrder.Instance);
		}

		int mistCount = Refii.Level.Engine_ModernMist.Instance?.SnapshotVolumes(_mistInputs) ?? 0;
		int capsuleCount = Refii.Game.GameEntities.SnapshotVolumetricCasters(
			eye, Engine_VolumetricLightShafts.MaxFogRangeMetres, _capsuleInputs);
		ReadOnlySpan<Engine_VolumetricLightShafts.VolumetricCapsule> capsuleSpan =
			_capsuleInputs.AsSpan(0, capsuleCount);

		int outCount = 0;
		for (int c = 0; c < candidateCount && outCount < Engine_VolumetricLightShafts.MaxShaftLights; c++)
		{
			ref Fixture f = ref _fixtures[_shaftCandidates[c].Fixture];
			if (!GodotObject.IsInstanceValid(f.Node))
			{
				continue;
			}

			float dist = _shaftCandidates[c].Key;
			float maxDist = Engine_VolumetricLightShafts.MaxFogRangeMetres + f.Range;
			float distFade = Mathf.Clamp((maxDist - dist) / 8.0f, 0.0f, 1.0f);
			float liveEnergy = f.Node.LightEnergy * distFade;
			if (liveEnergy <= 0.0001f)
			{
				continue;
			}

			Engine_LightTable.TryGetLightShaftData(f.Node, f.Range, out float statedRange, out Vector4 atlasRect);
			bool indoorFromOutside = !interior && !viewerIndoors && f.IndoorBit;
			if (indoorFromOutside && atlasRect.Z <= 0.0f)
			{
				continue;
			}

			uint casterMask = capsuleCount > 0
				? Engine_VolumetricLightShafts.ComputeLightCapsuleMask(f.At, statedRange, capsuleSpan)
				: 0u;

			_shaftInputs[outCount++] = new Engine_VolumetricLightShafts.ShaftLightInput(
				Position: f.At,
				StatedRange: statedRange,
				NodeRange: f.Range,
				LinearColor: f.LinearColor,
				Energy: liveEnergy,
				FogEnergyScale: f.VolumetricFogScale,
				AtlasRect: atlasRect,
				IndoorFromOutside: indoorFromOutside,
				SpotDir: f.SpotDir,
				SpotCosHalfAngle: f.SpotCosHalfAngle,
				CasterMask: casterMask);
		}

		if (outCount == 0)
		{
			Engine_VolumetricLightShafts.HideVolume();
			return;
		}

		Engine_VolumetricLightShafts.Update(
			eye,
			_shaftInputs.AsSpan(0, outCount),
			_mistInputs.AsSpan(0, mistCount),
			capsuleSpan);
	}

	/// <summary>
	/// Brings <see cref="_fixtures"/> up to the registry: rebuilt when the set, a stated light
	/// value (<see cref="Engine_LevelLights.LightMetaGeneration"/>) or a zone
	/// (<see cref="Engine_LightZones.Generation"/>) changed; the hold clocks survive a rebuild.
	/// Then reads what moves every pass — in-tree, position, visibility, map — once per fixture.
	/// </summary>
	private static void SyncFixtures()
	{
		int metaGeneration = Engine_LevelLights.LightMetaGeneration;
		int zoneGeneration = Engine_LightZones.Generation;
		bool rebuild = _count != _lights.Count || _metaGeneration != metaGeneration || _zoneGeneration != zoneGeneration;
		if (!rebuild)
		{
			for (int i = 0; i < _count; i++)
			{
				if (!ReferenceEquals(_fixtures[i].Node, _lights[i]))
				{
					rebuild = true;
					break;
				}
			}
		}

		if (rebuild)
		{
			_metaGeneration = metaGeneration;
			_zoneGeneration = zoneGeneration;

			// Built beside the old array, which the carry-over reads: a light streamed in lands
			// at the front of the snapshot and shifts every slot, so building in place would
			// read a slot already overwritten.
			Fixture[] was = _fixtures;
			Fixture[] next = _spare.Length >= _lights.Count
				? _spare
				: new Fixture[Math.Max(_lights.Count, was.Length * 2)];
			if (_candidates.Length < next.Length)
			{
				_candidates = new Candidate[next.Length];
			}

			for (int i = 0; i < _lights.Count; i++)
			{
				Light3D light = _lights[i];
				var fixture = new Fixture { Node = light };
				if (_index.TryGetValue(light, out int slot) && slot < _count && ReferenceEquals(was[slot].Node, light))
				{
					fixture.LightSeen0 = was[slot].LightSeen0;
					fixture.LightSeen1 = was[slot].LightSeen1;
					fixture.ShadowSeen0 = was[slot].ShadowSeen0;
					fixture.ShadowSeen1 = was[slot].ShadowSeen1;
				}

				fixture.Omni = light is OmniLight3D;
				fixture.Range = RangeOf(light);
				fixture.FadeEnd = light.DistanceFadeEnabled
					? light.DistanceFadeBegin + light.DistanceFadeLength
					: float.MaxValue;
				fixture.BaseEnergy = ReadBaseEnergy(light);
				fixture.Authored = light.HasMeta(AuthoredShadowName) && light.GetMeta(AuthoredShadowName).AsBool();
				fixture.Zone = Engine_LightZones.ZoneOf(light);
				uint cullMask = light.LightCullMask;
				fixture.IndoorBit = (cullMask & 1u) == 0u;
				fixture.OutdoorBit = !fixture.IndoorBit && (cullMask & Engine_LightZones.IndoorMask) == 0u;
				fixture.ShadowMask = light.ShadowCasterMask;
				Color lin = Engine_LightColour.Linear(light);
				fixture.LinearColor = new Vector3(lin.R, lin.G, lin.B);
				fixture.VolumetricFogScale = light.HasMeta(VolumetricFogScaleName)
					? (float)light.GetMeta(VolumetricFogScaleName).AsDouble()
					: Engine_LevelLights.DefaultVolumetricFogScale;
				if (light is SpotLight3D spot)
				{
					fixture.SpotCosHalfAngle = MathF.Cos(Mathf.DegToRad(Math.Clamp(spot.SpotAngle, 1.0f, 89.0f)));
					fixture.SpotDir = light.IsInsideTree()
						? (-light.GlobalBasis.Z).Normalized()
						: (-light.Transform.Basis.Z).Normalized();
				}
				else
				{
					fixture.SpotCosHalfAngle = -2.0f;
					fixture.SpotDir = Vector3.Zero;
				}
				next[i] = fixture;
			}

			_fixtures = next;
			_spare = was;
			Array.Clear(_spare, 0, _spare.Length);
			_count = _lights.Count;
			_index.Clear();
			for (int i = 0; i < _count; i++)
			{
				_index[_fixtures[i].Node] = i;
			}
		}

		for (int i = 0; i < _count; i++)
		{
			ref Fixture f = ref _fixtures[i];
			f.InTree = f.Node.IsInsideTree();
			if (f.InTree)
			{
				f.At = f.Node.GlobalPosition;
				f.Shown = f.Node.Visible;
				f.Casting = f.Node.ShadowEnabled;
				if (!f.Omni && f.SpotDir.LengthSquared() < 0.5f)
				{
					f.SpotDir = (-f.Node.GlobalBasis.Z).Normalized();
				}
			}

			f.VisibleBy = 0;
			f.WantedBy = 0;
			f.CubeBy = 0;
			f.ScoredBy = 0;
		}
	}

	/// <summary>Every view's visible set and shadow set, as flags on the fixtures.</summary>
	private static void Rank(int views)
	{
		// VISIBILITY FIRST: a light whose fade has ended before the eye lights nothing on
		// screen and still costs its cull and cluster work. Keep the nearest LightBudget of
		// those that can reach; hide the rest until the eye comes back.
		//
		// NONE UNDER CLASSIC. Every classic material is unshaded and lit from the guest's own
		// light set (Engine_ClassicLitInstances), so a placed Godot light lights nothing there and
		// still costs its cull, cluster and shadow work. Hidden, it never competes for a map.
		bool classic = Refii.Models.Engine_ClassicMaterial.Active;
		ulong nowMs = Time.GetTicksMsec();

		for (int v = 0; v < views; v++)
		{
			FrustumOf(_views[v], _frustumPlanes[v]);
		}

		for (int v = 0; v < views; v++)
		{
			uint zone = ZoneOfView(v);
			bool viewerIndoors = Engine_LightingFrame.CurrentInterior || Engine_LightZones.IsIndoors(zone);
			ChooseVisible(v, _views[v].GlobalPosition, _frustumPlanes[v], viewerIndoors, zone, classic, nowMs);
		}

		// Shadows: range and view-frustum governed. Placed lights cast dynamic entities only (static world is baked).
		// Lights with no characters/entities in their pool cannot cast dynamic shadows and are culled.
		// Each view ranks from its own player — player one's from the hero, player two's from player
		// two — and takes the whole budget; the union casts.
		int maxShadows = Budget > 0 ? Budget : (MaxDynamicEntityShadowLights > 0 ? MaxDynamicEntityShadowLights : int.MaxValue);
		int cubeBudget = Math.Min(MaxCubeShadowLights, maxShadows);
		for (int v = 0; v < views; v++)
		{
			Vector3 eye = _views[v].GlobalPosition;
			Vector3 refPos = (v == 0 ? Refii.Game.GameEntities.HeroPosition : Refii.Game.GameEntities.PlayerTwoPosition) ?? eye;
			uint zone = ZoneOfView(v);
			ChooseShadowed(v, eye, refPos, _frustumPlanes[v], Engine_LightingFrame.CurrentInterior || Engine_LightZones.IsIndoors(zone),
				zone, classic, nowMs, maxShadows, cubeBudget);
		}

		int eligible = 0;
		for (int i = 0; i < _count; i++)
		{
			if (_fixtures[i].ScoredBy != 0)
			{
				eligible++;
			}
		}

		Eligible = eligible;
	}

	/// <summary>Writes the visible set and the shadow set to the nodes, starting each fade.</summary>
	private static void Apply()
	{
		bool classic = Refii.Models.Engine_ClassicMaterial.Active;
		int casting = 0;
		int wanted = 0;
		for (int i = 0; i < _count; i++)
		{
			ref Fixture f = ref _fixtures[i];
			Light3D light = f.Node;
			bool show = f.VisibleBy != 0;
			if (classic)
			{
				if (f.Shown)
				{
					light.Visible = false;
					f.Shown = false;
				}

				_visibilityTarget.Remove(light);
				VisibilityLevel.Remove(light);
			}
			else if (show)
			{
				if (!f.Shown)
				{
					VisibilityLevel[light] = 0.0f;
					light.LightEnergy = 0.0f;
					light.Visible = true;
					f.Shown = true;
					_visibilityTarget[light] = 1.0f;
				}
				else if (VisibilityLevel.TryGetValue(light, out float cur) && cur < 1.0f)
				{
					_visibilityTarget[light] = 1.0f;
				}
			}
			else if (f.Shown)
			{
				if (!VisibilityLevel.ContainsKey(light))
				{
					VisibilityLevel[light] = 1.0f;
				}

				_visibilityTarget[light] = 0.0f;
			}

			if (f.WantedBy != 0)
			{
				wanted++;
				casting++;
				uint desiredMask = Engine_LevelLights.CasterMaskOf(
					includeDynamicEntities: true,
					isIndoorLight: !classic && f.IndoorBit,
					includeStaticWorld: classic || (!f.Omni && f.Authored),
					isOutdoorLight: !classic && f.OutdoorBit);

				if (f.ShadowMask != desiredMask)
				{
					light.ShadowCasterMask = desiredMask;
					f.ShadowMask = desiredMask;
				}

				// Its map is about to render, unless the lite shaders are built without shadow sampling (lite_shadowless).
				if (!f.Casting && Refii.Models.Engine_ShaderSpecialisation.ShadowCast("a placed light"))
				{
					light.ShadowOpacity = 0.0f;
					light.ShadowEnabled = true;
					f.Casting = true;
				}

				ApplyMode(light, f.Omni, f.CubeBy != 0);
				_fading[light] = 1.0f;
			}
			else if (f.Casting)
			{
				_fading[light] = 0.0f;
			}
		}

		Casting = casting;
		_wantedCount = wanted;
	}

	/// <summary>The light zone view <paramref name="view"/>'s player stands in.</summary>
	private static uint ZoneOfView(int view) =>
		view == 0 ? Engine_LightZones.HeroZone : Refii.Game.GameEntities.SecondViewZone;

	/// <summary>
	/// One view's visible lights: those within <see cref="MaxLightDistance"/> of its eye that are
	/// near it (<see cref="NearEligible"/>), in its frustum, or were within <see cref="LightHoldMs"/>.
	/// With a <see cref="LightBudget"/> the nearest fill it, the ones already visible first while
	/// they rank within <see cref="LightSlack"/> of it.
	/// </summary>
	private static void ChooseVisible(int view, Vector3 eye, Plane[] frustum, bool viewerIndoors, uint heroZone, bool classic, ulong nowMs)
	{
		byte bit = (byte)(1 << view);
		bool interior = Engine_LightingFrame.CurrentInterior;
		int n = 0;
		for (int i = 0; i < _count; i++)
		{
			ref Fixture f = ref _fixtures[i];
			if (!f.InTree)
			{
				continue;
			}

			bool isIndoorLight = !classic && f.IndoorBit;
			if (ShouldCullForBuildingZone(viewerIndoors, interior, isIndoorLight, f.Zone, heroZone))
			{
				continue;
			}

			float distance = eye.DistanceTo(f.At);
			float maxDist = f.FadeEnd < float.MaxValue ? f.FadeEnd : MaxLightDistance + f.Range;
			if (distance > maxDist)
			{
				continue;
			}

			bool inFrustum = NearEligible(distance, f.Range) || SphereInView(frustum, frustum.Length, f.At, f.Range + LightFrustumMargin);
			ref ulong seen = ref LightSeenOf(ref f, view);
			if (inFrustum)
			{
				seen = nowMs;
			}

			bool inView = inFrustum || Held(seen, nowMs, LightHoldMs);
			if (LightsEnabled && !classic && f.BaseEnergy > 0.0001f && inView)
			{
				_candidates[n++] = new Candidate { Fixture = i, Key = distance };
			}
		}

		if (LightBudget <= 0 || n <= LightBudget)
		{
			for (int c = 0; c < n; c++)
			{
				_fixtures[_candidates[c].Fixture].VisibleBy |= bit;
			}

			return;
		}

		Array.Sort(_candidates, 0, n, CandidateOrder.Instance);
		int taken = 0;
		int reach = Math.Min(n, LightBudget + LightSlack);
		for (int c = 0; c < reach && taken < LightBudget; c++)
		{
			ref Fixture f = ref _fixtures[_candidates[c].Fixture];
			if (f.Shown && (f.VisibleBy & bit) == 0)
			{
				f.VisibleBy |= bit;
				taken++;
			}
		}

		for (int c = 0; c < n && taken < LightBudget; c++)
		{
			ref Fixture f = ref _fixtures[_candidates[c].Fixture];
			if ((f.VisibleBy & bit) == 0)
			{
				f.VisibleBy |= bit;
				taken++;
			}
		}
	}

	/// <summary>
	/// One view's shadow set: visible, allowed to cast, within <see cref="MaxShadowDistance"/> of
	/// its eye, near its player or eye (<see cref="ShadowNearEligible"/>) or in its frustum or held
	/// within <see cref="ShadowHoldMs"/>, with a dynamic caster in reach, scored from its player's
	/// position. The best fill the budget, a fixture already casting first while it ranks within
	/// <see cref="Slack"/> of it; the best <paramref name="cubeBudget"/> of the chosen take a cube.
	/// A light scored by any view counts as eligible once.
	/// </summary>
	private static void ChooseShadowed(int view, Vector3 eye, Vector3 refPos, Plane[] frustum, bool viewerIndoors,
		uint heroZone, bool classic, ulong nowMs, int maxShadows, int cubeBudget)
	{
		byte bit = (byte)(1 << view);
		bool remaster = Engine_LevelLights.PlacedShadowsEnabled;
		bool interior = Engine_LightingFrame.CurrentInterior;
		int n = 0;
		for (int i = 0; i < _count; i++)
		{
			ref Fixture f = ref _fixtures[i];
			if (!f.InTree || !f.Shown || f.VisibleBy == 0)
			{
				continue;
			}

			if (!remaster && !f.Authored)
			{
				continue;
			}

			bool isIndoorLight = !classic && f.IndoorBit;
			if (ShouldCullForBuildingZone(viewerIndoors, interior, isIndoorLight, f.Zone, heroZone))
			{
				continue;
			}

			float eyeDist = eye.DistanceTo(f.At);
			if (eyeDist > MaxShadowDistance)
			{
				continue;
			}

			// Near the player or the eye the map stays whatever the camera does; otherwise the
			// frustum decides, with a hold so a pan does not take a map at the edge and give it back.
			float refDist = refPos.DistanceTo(f.At);
			bool inFrustum = ShadowNearEligible(refDist, eyeDist, f.Range) ||
				SphereInView(frustum, frustum.Length, f.At, f.Range + ShadowFrustumMargin);
			ref ulong seen = ref ShadowSeenOf(ref f, view);
			if (inFrustum)
			{
				seen = nowMs;
			}
			else if (!Held(seen, nowMs, ShadowHoldMs))
			{
				continue;
			}

			// Dynamic entity presence gating: static world shadows and volumetric shafts for both omni
			// and spot lights are served by `fable_static_shadow_atlas`, so placed lights only need live
			// Godot shadow maps when a dynamic character/entity caster is inside their radius.
			float casterRadius = f.Casting ? f.Range + 1.5f : f.Range;
			if (!classic && !Refii.Game.GameEntities.HasDynamicCasterNear(f.At, casterRadius))
			{
				continue;
			}

			float score = ScoreDynamicEntityShadowLight(
				f.At,
				refPos,
				f.Range,
				f.BaseEnergy,
				viewerIndoors,
				interior,
				isIndoorLight,
				f.Zone,
				heroZone,
				f.Casting);
			if (score >= float.MaxValue)
			{
				continue;
			}

			f.ScoredBy |= bit;
			_candidates[n++] = new Candidate { Fixture = i, Key = score };
		}

		if (n == 0)
		{
			return;
		}

		Array.Sort(_candidates, 0, n, CandidateOrder.Instance);

		// STICKY: a fixture holding a map keeps it while it ranks within the budget plus Slack.
		int taken = 0;
		int reach = Math.Min(n, SaturatingAdd(maxShadows, Slack));
		for (int c = 0; c < reach && taken < maxShadows; c++)
		{
			ref Fixture f = ref _fixtures[_candidates[c].Fixture];
			if (f.Casting && TargetOf(f.Node) > 0.5f && (f.WantedBy & bit) == 0)
			{
				f.WantedBy |= bit;
				taken++;
			}
		}

		for (int c = 0; c < n && taken < maxShadows; c++)
		{
			ref Fixture f = ref _fixtures[_candidates[c].Fixture];
			if ((f.WantedBy & bit) == 0)
			{
				f.WantedBy |= bit;
				taken++;
			}
		}

		int cubes = 0;
		int cubeReach = SaturatingAdd(cubeBudget, 2);
		int chosenRank = 0;
		for (int c = 0; c < n && cubes < cubeBudget && chosenRank < cubeReach; c++)
		{
			ref Fixture f = ref _fixtures[_candidates[c].Fixture];
			if ((f.WantedBy & bit) == 0)
			{
				continue;
			}

			chosenRank++;
			if (f.Casting && f.Node is OmniLight3D { OmniShadowMode: OmniLight3D.ShadowMode.Cube })
			{
				f.CubeBy |= bit;
				cubes++;
			}
		}

		for (int c = 0; c < n && cubes < cubeBudget; c++)
		{
			ref Fixture f = ref _fixtures[_candidates[c].Fixture];
			if ((f.WantedBy & bit) != 0 && (f.CubeBy & bit) == 0)
			{
				f.CubeBy |= bit;
				cubes++;
			}
		}
	}

	/// <summary>Steps every map and light visibility transition toward its target opacity/energy.</summary>
	private static void Fade(float delta)
	{
		float step = delta / FadeSeconds;
		if (_visibilityTarget.Count != 0)
		{
			_settled.Clear();
			foreach (KeyValuePair<Light3D, float> pair in _visibilityTarget)
			{
				Light3D light = pair.Key;
				if (!GodotObject.IsInstanceValid(light))
				{
					_settled.Add(light);
					VisibilityLevel.Remove(light);
					continue;
				}

				float cur = VisibilityLevel.TryGetValue(light, out float v) ? v : (light.Visible ? 1.0f : 0.0f);
				float next = Mathf.MoveToward(cur, pair.Value, step);
				VisibilityLevel[light] = next;
				light.LightEnergy = BaseEnergyOf(light) * next;
				if (next == pair.Value)
				{
					if (pair.Value <= 0.0f)
					{
						light.Visible = false;
					}

					VisibilityLevel.Remove(light);
					_settled.Add(light);
				}
			}

			foreach (Light3D light in _settled)
			{
				_visibilityTarget.Remove(light);
			}
		}

		if (_fading.Count == 0)
		{
			return;
		}

		_settled.Clear();
		foreach (KeyValuePair<Light3D, float> pair in _fading)
		{
			Light3D light = pair.Key;
			if (!GodotObject.IsInstanceValid(light))
			{
				_settled.Add(light);
				continue;
			}

			float next = Mathf.MoveToward(light.ShadowOpacity, pair.Value, step);
			if (next != light.ShadowOpacity)
			{
				light.ShadowOpacity = next;
			}

			if (next == pair.Value)
			{
				if (pair.Value <= 0.0f && light.ShadowEnabled)
				{
					light.ShadowEnabled = false;
				}

				_settled.Add(light);
			}
		}

		foreach (Light3D light in _settled)
		{
			_fading.Remove(light);
		}
	}

	private static float TargetOf(Light3D light) => _fading.TryGetValue(light, out float target) ? target : 1.0f;

	private static void ApplyMode(Light3D light, bool omni, bool useCube)
	{
		if (light.DistanceFadeShadow <= 0f || light.DistanceFadeShadow > MaxShadowDistance)
		{
			light.DistanceFadeShadow = MaxShadowDistance;
		}

		if (!omni)
		{
			return;
		}

		var omniLight = (OmniLight3D)light;
		OmniLight3D.ShadowMode mode = useCube
			? OmniLight3D.ShadowMode.Cube
			: OmniLight3D.ShadowMode.DualParaboloid;
		if (omniLight.OmniShadowMode != mode)
		{
			omniLight.OmniShadowMode = mode;
		}
	}

	private static float RangeOf(Light3D light) =>
		light is OmniLight3D omni ? omni.OmniRange : light is SpotLight3D spot ? spot.SpotRange : 0f;

	private static ref ulong LightSeenOf(ref Fixture f, int view) =>
		ref (view == 0 ? ref f.LightSeen0 : ref f.LightSeen1);

	private static ref ulong ShadowSeenOf(ref Fixture f, int view) =>
		ref (view == 0 ? ref f.ShadowSeen0 : ref f.ShadowSeen1);

	private static int SaturatingAdd(int a, int b) => a > int.MaxValue - b ? int.MaxValue : a + b;

	/// <summary>
	/// The camera's six frustum planes, outward-facing in world space, into <paramref name="planes"/>
	/// — what <c>Camera3D.GetFrustum</c> returns, without the array it returns it in.
	/// </summary>
	private static void FrustumOf(Camera3D camera, Plane[] planes)
	{
		Projection projection = camera.GetCameraProjection();
		Transform3D transform = camera.GetCameraTransform();
		planes[0] = transform * projection.GetProjectionPlane(Projection.Planes.Near);
		planes[1] = transform * projection.GetProjectionPlane(Projection.Planes.Far);
		planes[2] = transform * projection.GetProjectionPlane(Projection.Planes.Left);
		planes[3] = transform * projection.GetProjectionPlane(Projection.Planes.Top);
		planes[4] = transform * projection.GetProjectionPlane(Projection.Planes.Right);
		planes[5] = transform * projection.GetProjectionPlane(Projection.Planes.Bottom);
	}

	/// <summary>Whether a light <paramref name="distance"/> metres from the eye with reach <paramref name="range"/> is in view whatever the frustum says.</summary>
	public static bool NearEligible(float distance, float range) =>
		distance <= NearEligibleMetres || distance <= range + NearEligibleBeyondRange;

	/// <summary>Whether a map stays eligible whatever the frustum says: its pool reaches near the player or the eye.</summary>
	public static bool ShadowNearEligible(float playerDistance, float eyeDistance, float range) =>
		playerDistance <= range + ShadowNearPlayerBeyondRange || eyeDistance <= range + ShadowNearEyeBeyondRange;

	/// <summary>Whether something last seen at <paramref name="seenMs"/> (0 never) is still held at <paramref name="nowMs"/>.</summary>
	public static bool Held(ulong seenMs, ulong nowMs, ulong holdMs) =>
		seenMs != 0 && nowMs - seenMs <= holdMs;

	/// <summary>
	/// The sticky pick over a ranked list, best first: a holder within <paramref name="budget"/> +
	/// <paramref name="slack"/> keeps its place, the rest of the budget fills from the top.
	/// Returns how many were chosen; <paramref name="chosen"/> is written for every rank.
	/// </summary>
	public static int SelectSticky(ReadOnlySpan<bool> holding, int budget, int slack, Span<bool> chosen)
	{
		int n = holding.Length;
		chosen.Slice(0, n).Clear();
		int taken = 0;
		int reach = Math.Min(n, SaturatingAdd(budget, slack));
		for (int i = 0; i < reach && taken < budget; i++)
		{
			if (holding[i])
			{
				chosen[i] = true;
				taken++;
			}
		}

		for (int i = 0; i < n && taken < budget; i++)
		{
			if (!chosen[i])
			{
				chosen[i] = true;
				taken++;
			}
		}

		return taken;
	}

	/// <summary>
	/// Whether a placed light should be culled because of building-zone boundaries:
	/// Only culls indoor lights belonging to a DIFFERENT enclosed building when the viewer is inside
	/// a town building (<c>viewerIndoors &amp;&amp; !isInteriorLevel</c>), because all town building
	/// interiors share <see cref="Engine_LightZones.IndoorMask"/>. Never culls outdoor lights when
	/// indoors (visible through windows/doors) or indoor lights by height/distance hacks.
	/// </summary>
	public static bool ShouldCullForBuildingZone(
		bool viewerIndoors,
		bool isInteriorLevel,
		bool isIndoorLight,
		uint lightZone,
		uint heroZone)
	{
		if (isInteriorLevel || !viewerIndoors || !isIndoorLight)
		{
			return false;
		}

		if (lightZone != 0u && lightZone != Refii.Models.Engine_ClassicZones.Outdoors &&
			heroZone != 0u && heroZone != Refii.Models.Engine_ClassicZones.Outdoors &&
			lightZone != heroZone)
		{
			return true;
		}

		return false;
	}

	/// <summary>
	/// Scores a placed light for the dynamic moving entity shadow budget (<see cref="MaxDynamicEntityShadowLights"/>).
	/// Lower score is better; <see cref="float.MaxValue"/> means the light cannot cast a shadow from the player.
	/// Accounts for indoor/outdoor zone isolation and vertical floor separation so lights on the player's own
	/// floor and room always win over lights on an upper/lower floor or behind building walls.
	/// </summary>
	public static float ScoreDynamicEntityShadowLight(
		Vector3 lightPosition,
		Vector3 referencePosition,
		float range,
		float baseEnergy,
		bool viewerIndoors,
		bool isInteriorLevel,
		bool isIndoorLight,
		uint lightZone,
		uint heroZone,
		bool alreadyDynamic = false)
	{
		if (!isInteriorLevel)
		{
			if (viewerIndoors && !isIndoorLight && lightZone == Refii.Models.Engine_ClassicZones.Outdoors)
			{
				return float.MaxValue;
			}

			if (!viewerIndoors && isIndoorLight)
			{
				return float.MaxValue;
			}

			if (viewerIndoors &&
				lightZone != 0u && lightZone != Refii.Models.Engine_ClassicZones.Outdoors &&
				heroZone != 0u && heroZone != Refii.Models.Engine_ClassicZones.Outdoors &&
				lightZone != heroZone)
			{
				return float.MaxValue;
			}
		}

		float refDist = referencePosition.DistanceTo(lightPosition);
		float effectiveDist = refDist;

		// Lights whose reach does not even reach the player cannot cast a strong player shadow.
		if (refDist > range + 1.5f)
		{
			effectiveDist += (refDist - range) * 6.0f;
		}

		// Floor separation guard: a player's feet sit at referencePosition.Y and lights on the same
		// floor hang between -0.5m and +4.2m relative to the floor. Lights below the floor (dy < -0.5m)
		// or on an upper story (dy > +4.2m) are separated by a ceiling/floor slab and receive a strong
		// penalty so same-floor lights always win the dynamic shadow slots.
		float dy = lightPosition.Y - referencePosition.Y;
		if (dy < -0.5f)
		{
			effectiveDist += (-0.5f - dy) * 25.0f;
		}
		else if (dy > 4.2f)
		{
			effectiveDist += (dy - 4.2f) * 25.0f;
		}

		float clampedEnergy = Mathf.Clamp(baseEnergy, 0.5f, 3.0f);
		float clampedRange = Mathf.Clamp(range, 2.0f, 8.0f);
		float weight = Mathf.Pow(clampedEnergy * clampedRange, 0.25f);
		float score = effectiveDist / weight;
		if (alreadyDynamic)
		{
			score -= 0.75f;
		}

		return score;
	}

	/// <summary>
	/// Whether any part of the sphere is inside the camera's planes.
	/// In Godot 4, Camera3D frustum plane normals face OUTWARD.
	/// If the centre is more than radius in front of any plane (<c>DistanceTo(centre) &gt; radius</c>),
	/// the entire sphere is strictly outside the frustum.
	/// </summary>
	public static bool SphereInView(Plane[] frustum, int planeCount, Vector3 centre, float radius)
	{
		for (int i = 0; i < planeCount; i++)
		{
			if (frustum[i].DistanceTo(centre) > radius)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>Convenience overload for array slice or test suites.</summary>
	public static bool SphereInView(Plane[] frustum, Vector3 centre, float radius) =>
		SphereInView(frustum, frustum.Length, centre, radius);
}
