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
/// With <c>placed_light_shadows</c> off only fixtures whose data states CastShadows compete —
/// the 83 shipped spots — so the authored behaviour survives under the remaster switch. What
/// a map draws is the light's own mask: <see cref="Engine_LevelLights.CasterMaskOf"/>.
/// </remarks>
public partial class Engine_LightShadowBudget : Node
{
	public const string NodeName = "LightShadowBudget";

	/// <summary>Shadowed placed lights at once (legacy compatibility; range-based culling is active).</summary>
	public static int Budget = 0;

	/// <summary>Placed lights kept VISIBLE at once (legacy compatibility; range-based culling is active).</summary>
	public static int LightBudget = 0;

	/// <summary>Max static world shadow lights at once (legacy compatibility).</summary>
	public static int MaxStaticWorldShadowLights = 0;

	/// <summary>Max dynamic entity shadow lights at once (default 6; 0 uses uncapped).</summary>
	public static int MaxDynamicEntityShadowLights = 6;

	/// <summary>Of the dynamic shadow lights, the nearest N that use Cube shadows (default 2; remainder use DualParaboloid).</summary>
	public static int MaxCubeShadowLights = 2;

	/// <summary>Metres from the eye to a light beyond which its shadow is culled; <c>placed_shadow_range</c>.</summary>
	public static float MaxShadowDistance = Engine_LevelLights.PlacedShadowRange();

	/// <summary>Metres from the eye to a light beyond which it is not kept visible; <c>placed_light_range</c>.</summary>
	public static float MaxLightDistance = Engine_LevelLights.PlacedLightRange();

	/// <summary>Reloads lighting and shadow distance parameters from current config.</summary>
	public static void ReloadConfig()
	{
		MaxShadowDistance = Engine_LevelLights.PlacedShadowRange();
		MaxLightDistance = Engine_LevelLights.PlacedLightRange();
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

	/// <summary>Milliseconds to hold a light in view after leaving the frustum to prevent edge pop during camera rotation.</summary>
	private const ulong LightHoldMs = 1500;

	private readonly List<Light3D> _lights = new();
	private readonly Dictionary<Light3D, float> _fading = new();
	private readonly Dictionary<Light3D, float> _visibilityTarget = new();
	private static readonly Dictionary<Light3D, float> VisibilityLevel = new();
	private readonly List<Light3D> _settled = new();
	private readonly HashSet<Light3D> _wanted = new();
	private readonly HashSet<Light3D> _visibleWanted = new();
	private readonly Dictionary<Light3D, ulong> _lightSeenMs = new();
	private double _since = double.MaxValue;
	private Vector3 _lastCameraPos = new(float.MaxValue, float.MaxValue, float.MaxValue);
	private Vector3 _lastCameraDir = Vector3.Zero;
	private Plane[] _frustumPlanes = new Plane[6];
	private int _frustumCount;

	private static ulong _tableFrame = ulong.MaxValue;

	private const string AuthoredShadowName = Engine_LevelLights.AuthoredShadowMeta;

	/// <summary>Current smooth visibility fade factor in [0, 1] for <paramref name="light"/>.</summary>
	public static float VisibilityFactorOf(Light3D light) =>
		VisibilityLevel.TryGetValue(light, out float level) ? level : (light.Visible ? 1.0f : 0.0f);

	private static float BaseEnergyOf(Light3D light) =>
		light.HasMeta(Engine_LevelLights.BaseEnergyMeta)
			? (float)light.GetMeta(Engine_LevelLights.BaseEnergyMeta).AsDouble()
			: light.LightEnergy;

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("lightshadowbudget");

		// The light table refreshes every frame, once across however many regions carry a budget node: a
		// flickering torch's range moves every frame and the shader reads it from here.
		ulong frame = Engine.GetProcessFrames();
		if (_tableFrame != frame)
		{
			_tableFrame = frame;
			Engine_LightTable.Refresh();
		}

		Fade((float)delta);

		_since += delta;
		if (_since < Interval)
		{
			return;
		}

		_since = 0;

		Camera3D? camera = GetViewport()?.GetCamera3D();
		if (camera is null)
		{
			return;
		}

		Vector3 eye = camera.GlobalPosition;
		Vector3 cameraDir = -camera.GlobalBasis.Z;
		bool cameraMoved = eye.DistanceSquaredTo(_lastCameraPos) > 0.04f ||
			cameraDir.DistanceSquaredTo(_lastCameraDir) > 0.005f;

		if (!cameraMoved && _lights.Count > 0 && _wanted.Count > 0)
		{
			return;
		}

		_lastCameraPos = eye;
		_lastCameraDir = cameraDir;

		Engine_LevelLights.SnapshotLights(_lights);
		if (_lights.Count == 0)
		{
			return;
		}

		// VISIBILITY FIRST: a light whose fade has ended before the eye lights nothing on
		// screen and still costs its cull and cluster work. Keep the nearest LightBudget of
		// those that can reach; hide the rest until the eye comes back.
		//
		// NONE UNDER CLASSIC. Every classic material is unshaded and lit from the guest's own
		// light set (Engine_ClassicLitInstances), so a placed Godot light lights nothing there and
		// still costs its cull, cluster and shadow work. Hidden, it never competes for a map.
		bool classic = Refii.Models.Engine_ClassicMaterial.Active;
		bool viewerIndoors = Engine_LightZones.IsViewerIndoors;
		uint heroZone = Engine_LightZones.HeroZone;
		ulong nowMs = Time.GetTicksMsec();

		Godot.Collections.Array<Plane> frustum = camera.GetFrustum();
		if (_frustumPlanes.Length < frustum.Count)
		{
			_frustumPlanes = new Plane[frustum.Count];
		}
		_frustumCount = frustum.Count;
		for (int p = 0; p < frustum.Count; p++)
		{
			_frustumPlanes[p] = frustum[p];
		}
		((Godot.Collections.Array)frustum).Dispose();

		_visibleWanted.Clear();
		foreach (Light3D light in _lights)
		{
			if (!light.IsInsideTree())
			{
				continue;
			}

			float range = RangeOf(light);
			float fadeEnd = light.DistanceFadeEnabled ? light.DistanceFadeBegin + light.DistanceFadeLength : float.MaxValue;
			Vector3 at = light.GlobalPosition;
			float distance = eye.DistanceTo(at);

			if (distance > MaxLightDistance + range || distance > fadeEnd)
			{
				continue;
			}

			bool inFrustum = SphereInView(_frustumPlanes, _frustumCount, at, range + 1.5f);
			if (inFrustum)
			{
				_lightSeenMs[light] = nowMs;
			}
			bool seenRecently = _lightSeenMs.TryGetValue(light, out ulong seenMs) && (nowMs - seenMs <= LightHoldMs);
			bool inView = inFrustum || seenRecently;

			float baseEnergy = BaseEnergyOf(light);
			if (LightsEnabled && !classic && baseEnergy > 0.0001f && inView)
			{
				_visibleWanted.Add(light);
			}
		}

		for (int i = 0; i < _lights.Count; i++)
		{
			Light3D light = _lights[i];
			bool show = _visibleWanted.Contains(light);
			if (classic)
			{
				if (light.Visible)
				{
					light.Visible = false;
				}
				_visibilityTarget.Remove(light);
				VisibilityLevel.Remove(light);
				continue;
			}

			if (show)
			{
				if (!light.Visible)
				{
					VisibilityLevel[light] = 0.0f;
					light.LightEnergy = 0.0f;
					light.Visible = true;
					_visibilityTarget[light] = 1.0f;
				}
				else if (VisibilityLevel.TryGetValue(light, out float cur) && cur < 1.0f)
				{
					_visibilityTarget[light] = 1.0f;
				}
			}
			else if (light.Visible)
			{
				if (!VisibilityLevel.ContainsKey(light))
				{
					VisibilityLevel[light] = 1.0f;
				}
				_visibilityTarget[light] = 0.0f;
			}
		}

		// Shadows: range and view-frustum governed. Placed lights cast dynamic entities only (static world is baked).
		// Lights with no characters/entities in their pool cannot cast dynamic shadows and are culled.
		bool remaster = Engine_LevelLights.PlacedShadowsEnabled;
		_wanted.Clear();
		int eligible = 0;
		Vector3 refPos = Refii.Game.GameEntities.HeroPosition ?? eye;
		var candidates = new List<(Light3D Light, float Score)>();

		foreach (Light3D light in _lights)
		{
			if (!light.IsInsideTree() || !light.Visible || !_visibleWanted.Contains(light))
			{
				continue;
			}

			bool authored = light.HasMeta(AuthoredShadowName) &&
				light.GetMeta(AuthoredShadowName).AsBool();
			if (!remaster && !authored)
			{
				continue;
			}

			uint lightZone = Engine_LightZones.ZoneOf(light);
			bool isIndoorLight = Engine_LightZones.IsIndoorLight(light);
			if (ShouldCullForBuildingZone(
				viewerIndoors,
				Engine_LightingFrame.CurrentInterior,
				isIndoorLight,
				lightZone,
				heroZone))
			{
				continue;
			}

			float range = RangeOf(light);
			Vector3 at = light.GlobalPosition;
			float eyeDist = eye.DistanceTo(at);
			if (eyeDist > MaxShadowDistance)
			{
				continue;
			}

			// Shadows are strictly culled to the camera view frustum:
			if (!SphereInView(_frustumPlanes, _frustumCount, at, range + 1.5f))
			{
				continue;
			}

			// Dynamic entity presence gating: if static world shadows are baked, placed lights only cast dynamic entities.
			// A light with no characters/entities within its reach cannot cast dynamic shadows and should not allocate shadow maps.
			if (!Refii.Models.Engine_ClassicMaterial.Active && !Refii.Game.GameEntities.HasDynamicCasterNear(at, range))
			{
				continue;
			}

			eligible++;
			float score = ScoreDynamicEntityShadowLight(
				at,
				refPos,
				range,
				BaseEnergyOf(light),
				viewerIndoors,
				Engine_LightingFrame.CurrentInterior,
				isIndoorLight,
				lightZone,
				heroZone,
				light.ShadowEnabled);

			candidates.Add((light, score));
		}

		Eligible = eligible;

		var cubeLights = new HashSet<Light3D>();
		int maxShadows = Budget > 0 ? Budget : (MaxDynamicEntityShadowLights > 0 ? MaxDynamicEntityShadowLights : int.MaxValue);
		int cubeBudget = System.Math.Min(MaxCubeShadowLights, maxShadows);

		candidates.Sort((a, b) => a.Score.CompareTo(b.Score));
		int count = System.Math.Min(candidates.Count, maxShadows);
		for (int i = 0; i < count; i++)
		{
			Light3D l = candidates[i].Light;
			_wanted.Add(l);
			if (i < cubeBudget)
			{
				cubeLights.Add(l);
			}
		}

		int casting = 0;
		foreach (Light3D light in _lights)
		{
			if (_wanted.Contains(light))
			{
				casting++;
				uint desiredMask = Engine_LevelLights.CasterMaskOf(
					includeDynamicEntities: true,
					isIndoorLight: Engine_LightZones.IsIndoorLight(light),
					includeStaticWorld: Refii.Models.Engine_ClassicMaterial.Active);

				if (light.ShadowCasterMask != desiredMask)
				{
					light.ShadowCasterMask = desiredMask;
				}

				if (!light.ShadowEnabled)
				{
					light.ShadowOpacity = 0.0f;
					light.ShadowEnabled = true;
				}

				ApplyMode(light, cubeLights.Contains(light));
				_fading[light] = 1.0f;
			}
			else if (light.ShadowEnabled)
			{
				_fading[light] = 0.0f;
			}
		}

		Casting = casting;
	}

	/// <summary>Steps every map and light visibility transition toward its target opacity/energy.</summary>
	private void Fade(float delta)
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

	private float TargetOf(Light3D light) => _fading.TryGetValue(light, out float target) ? target : 1.0f;

	private static void ApplyMode(Light3D light, bool useCube = false)
	{
		if (light.DistanceFadeShadow <= 0f || light.DistanceFadeShadow > MaxShadowDistance)
		{
			light.DistanceFadeShadow = MaxShadowDistance;
		}

		if (light is not OmniLight3D omni)
		{
			return;
		}

		OmniLight3D.ShadowMode mode = useCube
			? OmniLight3D.ShadowMode.Cube
			: (Paraboloid ? OmniLight3D.ShadowMode.DualParaboloid : OmniLight3D.ShadowMode.DualParaboloid);
		if (omni.OmniShadowMode != mode)
		{
			omni.OmniShadowMode = mode;
		}
	}

	private static float RangeOf(Light3D light) =>
		light is OmniLight3D omni ? omni.OmniRange : light is SpotLight3D spot ? spot.SpotRange : 0f;

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

		float strength = Mathf.Max(baseEnergy, 0.25f) * Mathf.Max(range, 1.0f);
		float score = effectiveDist / Mathf.Sqrt(strength);
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
