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

	/// <summary>Shadowed placed lights at once; <c>placed_light_shadow_budget</c>. 0 is none.</summary>
	public static int Budget = Engine_LevelLights.PlacedShadowBudget();

	/// <summary>Placed lights kept VISIBLE at once — the nearest that can still reach the eye.
	/// Measured 2026-09-04: 190 visible lights cost 13 ms of draw with every shadow off.</summary>
	public static int LightBudget = Engine_LevelLights.PlacedLightBudget();

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
	public static bool Paraboloid = true;

	/// <summary>Lights holding or fading in a shadow map after the last pass.</summary>
	public static int Casting;

	/// <summary>Lights that competed for a map on the last pass.</summary>
	public static int Eligible;

	/// <summary>Seconds between reassignments. The fade hides the step; the sort need not be per-frame.</summary>
	private const double Interval = 0.25;

	/// <summary>How long a map takes to arrive or leave.</summary>
	private const float FadeSeconds = 0.4f;

	/// <summary>Metres from the eye to a light's SPHERE beyond which its shadow is not worth a map.</summary>
	private const float MaxShadowDistance = 35.0f;

	/// <summary>Milliseconds to hold a light's shadow in view after leaving the frustum to prevent edge pop during camera rotation.</summary>
	private const ulong ShadowHoldMs = 600;

	/// <summary>Ranks past the budget a fixture may hold before its map is taken away.</summary>
	private const int Slack = 4;

	/// <summary>Ranks past the light visibility budget a fixture may hold before being hidden (prevents edge thrashing).</summary>
	private const int LightSlack = 12;

	/// <summary>Milliseconds to hold a light in view after leaving the frustum to prevent edge pop during camera rotation.</summary>
	private const ulong LightHoldMs = 1500;

	private readonly List<Light3D> _lights = new();
	private readonly List<(float Score, Light3D Light)> _ranked = new();
	private readonly List<(float DistanceSq, Light3D Light)> _reach = new();
	private readonly Dictionary<Light3D, float> _fading = new();
	private readonly List<Light3D> _settled = new();
	private readonly HashSet<Light3D> _wanted = new();
	private readonly HashSet<Light3D> _visibleWanted = new();
	private readonly Dictionary<Light3D, ulong> _lightSeenMs = new();
	private readonly Dictionary<Light3D, ulong> _shadowSeenMs = new();
	private double _since = double.MaxValue;

	private static ulong _tableFrame = ulong.MaxValue;

	private static readonly StringName AuthoredShadowName = Engine_LevelLights.AuthoredShadowMeta;

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

		Engine_LevelLights.SnapshotLights(_lights);
		if (_lights.Count == 0)
		{
			return;
		}

		Vector3 eye = camera.GlobalPosition;

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
		_reach.Clear();
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

			// Lights within 45m of the camera stay eligible regardless of camera frustum rotation so
			// panning the camera never pops nearby building lights on/off at the budget boundary.
			bool inFrustum = distance <= 45.0f || distance <= range + 4.0f || SphereInView(frustum, at, range + 6.0f);
			if (inFrustum)
			{
				_lightSeenMs[light] = nowMs;
			}
			bool seenRecently = _lightSeenMs.TryGetValue(light, out ulong seenMs) && (nowMs - seenMs <= LightHoldMs);
			bool inView = inFrustum || seenRecently;

			uint lightZone = Engine_LightZones.ZoneOf(light);
			bool otherBuilding = false;
			if (!viewerIndoors && Engine_LightZones.IsIndoorLight(light) && distance > range + 8.0f)
			{
				otherBuilding = true;
			}
			else if (viewerIndoors && lightZone != 0u && lightZone != Refii.Models.Engine_ClassicZones.Outdoors &&
				lightZone != heroZone && distance > range + 4.0f)
			{
				otherBuilding = true;
			}

			bool reaches = LightsEnabled && !classic && !otherBuilding && inView && distance <= fadeEnd + range;
			_reach.Add((reaches ? distance : float.MaxValue, light));
		}

		_reach.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));
		_visibleWanted.Clear();
		int budgetLimit = LightBudget > 0 ? Mathf.Min(LightBudget, _reach.Count) : _reach.Count;

		// STICKY: lights that are currently visible within LightBudget + LightSlack keep visibility first
		if (LightBudget > 0)
		{
			for (int i = 0; i < _reach.Count && _visibleWanted.Count < budgetLimit; i++)
			{
				if (_reach[i].DistanceSq != float.MaxValue && i < LightBudget + LightSlack && _reach[i].Light.Visible)
				{
					_visibleWanted.Add(_reach[i].Light);
				}
			}
		}

		// Fill remaining budget
		for (int i = 0; i < _reach.Count && _visibleWanted.Count < budgetLimit; i++)
		{
			if (_reach[i].DistanceSq != float.MaxValue)
			{
				_visibleWanted.Add(_reach[i].Light);
			}
		}

		for (int i = 0; i < _reach.Count; i++)
		{
			bool show = _visibleWanted.Contains(_reach[i].Light);
			if (_reach[i].Light.Visible != show)
			{
				_reach[i].Light.Visible = show;
			}
		}

		// Candidates: visible, allowed to cast, with a pool near the eye.
		// Static objects cast at most 1 shadow (the strongest authored/world light, or the directional sun);
		// moving objects (NPCs/Hero) cast at most 2 dynamic shadows (the 2 strongest entity-caster lights).
		const int MaxStaticWorldShadowLights = 1;
		const int MaxDynamicEntityShadowLights = 2;
		Vector3 referenceAt = Refii.Game.GameEntities.HeroPosition ?? eye;
		bool remaster = Engine_LevelLights.PlacedShadowsEnabled;
		_ranked.Clear();
		foreach (Light3D light in _lights)
		{
			if (!light.IsInsideTree() || !light.Visible)
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
			if (!viewerIndoors)
			{
				// When outdoors, do not allocate shadow maps to indoor building lights.
				if (Engine_LightZones.IsIndoorLight(light))
				{
					continue;
				}
			}
			else
			{
				// When inside a building, allow lights in the SAME building zone (or unzoned) to cast shadows;
				// skip lights belonging to other buildings and skip outdoor entity-only lights.
				if (lightZone != 0u && lightZone != Refii.Models.Engine_ClassicZones.Outdoors && lightZone != heroZone)
				{
					continue;
				}
				if (Engine_LightZones.IsOutdoorLight(light) &&
					light.ShadowCasterMask == Engine_LevelLights.EntityCasterLayerMask)
				{
					continue;
				}
			}

			float range = RangeOf(light);
			Vector3 at = light.GlobalPosition;
			float eyeDist = eye.DistanceTo(at);
			float edgeDist = Mathf.Max(eyeDist - range, 0.0f);
			if (edgeDist > MaxShadowDistance)
			{
				continue;
			}

			// Lights near the hero/eye stay eligible regardless of camera rotation so shadows on nearby
			// buildings and characters never flicker when panning the camera.
			float refDist = referenceAt.DistanceTo(at);
			bool inFrustumShadow = refDist <= range + 10.0f || eyeDist <= range + 6.0f || SphereInView(frustum, at, range + 2.0f);
			if (inFrustumShadow)
			{
				_shadowSeenMs[light] = nowMs;
			}
			bool seenRecentlyShadow = _shadowSeenMs.TryGetValue(light, out ulong shadowSeen) && (nowMs - shadowSeen <= ShadowHoldMs);
			if (!inFrustumShadow && !seenRecentlyShadow)
			{
				continue;
			}

			// Rank by distance from the reference position weighted by light strength so the strongest
			// lights win. Hysteresis slightly favours lights already casting a settled shadow map.
			float strength = Mathf.Max(light.LightEnergy, 0.25f) * Mathf.Max(range, 1.0f);
			float score = refDist / Mathf.Sqrt(strength);
			if (light.ShadowEnabled && TargetOf(light) > 0.5f)
			{
				score -= 0.75f;
			}
			_ranked.Add((score, light));
		}

		Eligible = _ranked.Count;
		_ranked.Sort((a, b) => a.Score.CompareTo(b.Score));

		_wanted.Clear();
		int totalLimit = Mathf.Min(Budget, _ranked.Count);
		int staticLimit = Mathf.Min(MaxStaticWorldShadowLights, totalLimit);
		int entityLimit = Mathf.Min(MaxDynamicEntityShadowLights, totalLimit);
		int staticCount = 0;
		int entityCount = 0;

		for (int i = 0; i < _ranked.Count && _wanted.Count < totalLimit; i++)
		{
			Light3D candidate = _ranked[i].Light;
			bool entitiesOnly = candidate.ShadowCasterMask == Engine_LevelLights.EntityCasterLayerMask;
			if (entitiesOnly)
			{
				if (entityCount < entityLimit)
				{
					_wanted.Add(candidate);
					entityCount++;
				}
			}
			else
			{
				if (staticCount < staticLimit)
				{
					_wanted.Add(candidate);
					staticCount++;
				}
			}
		}

		int casting = 0;
		foreach (Light3D light in _lights)
		{
			if (_wanted.Contains(light))
			{
				casting++;
				if (!light.ShadowEnabled)
				{
					light.ShadowOpacity = 0.0f;
					light.ShadowEnabled = true;
				}

				ApplyMode(light);
				_fading[light] = 1.0f;
			}
			else if (light.ShadowEnabled)
			{
				_fading[light] = 0.0f;
			}
		}

		Casting = casting;
	}

	/// <summary>Steps every map in transition toward its target opacity; a map that reaches zero is released.</summary>
	private void Fade(float delta)
	{
		if (_fading.Count == 0)
		{
			return;
		}

		float step = delta / FadeSeconds;
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

	private static void ApplyMode(Light3D light)
	{
		if (light.DistanceFadeShadow <= 0f || light.DistanceFadeShadow > MaxShadowDistance)
		{
			light.DistanceFadeShadow = MaxShadowDistance;
		}

		if (light is not OmniLight3D omni)
		{
			return;
		}

		bool entitiesOnly = omni.ShadowCasterMask == Engine_LevelLights.EntityCasterLayerMask;
		OmniLight3D.ShadowMode mode = Paraboloid && entitiesOnly
			? OmniLight3D.ShadowMode.DualParaboloid : OmniLight3D.ShadowMode.Cube;
		if (omni.OmniShadowMode != mode)
		{
			omni.OmniShadowMode = mode;
		}
	}

	private static float RangeOf(Light3D light) =>
		light is OmniLight3D omni ? omni.OmniRange : light is SpotLight3D spot ? spot.SpotRange : 0f;

	/// <summary>Whether any of the sphere is inside the camera's planes, whose normals face outward.</summary>
	private static bool SphereInView(Godot.Collections.Array<Plane> frustum, Vector3 centre, float radius)
	{
		foreach (Plane plane in frustum)
		{
			if (plane.DistanceTo(centre) > radius)
			{
				return false;
			}
		}

		return true;
	}
}
