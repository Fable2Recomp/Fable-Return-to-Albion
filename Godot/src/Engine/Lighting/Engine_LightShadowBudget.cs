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
/// the 83 shipped spots — so the authored behaviour survives under the remaster switch.
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
	/// Debug lever: dual-paraboloid omni maps (two passes) instead of cube (six). Cheaper, and
	/// wrong on large triangles — the projection bends straight edges — so cube is the default.
	/// </summary>
	public static bool Paraboloid;

	/// <summary>Lights holding or fading in a shadow map after the last pass.</summary>
	public static int Casting;

	/// <summary>Lights that competed for a map on the last pass.</summary>
	public static int Eligible;

	/// <summary>Seconds between reassignments. The fade hides the step; the sort need not be per-frame.</summary>
	private const double Interval = 0.25;

	/// <summary>How long a map takes to arrive or leave.</summary>
	private const float FadeSeconds = 0.4f;

	/// <summary>Metres from the eye to a light's SPHERE beyond which its shadow is not worth a map.</summary>
	private const float MaxShadowDistance = 45.0f;

	/// <summary>Ranks past the budget a fixture may hold before its map is taken away.</summary>
	private const int Slack = 2;

	private readonly List<Light3D> _lights = new();
	private readonly List<(float Score, Light3D Light)> _ranked = new();
	private readonly List<(float DistanceSq, Light3D Light)> _reach = new();
	private readonly Dictionary<Light3D, float> _fading = new();
	private readonly List<Light3D> _settled = new();
	private readonly HashSet<Light3D> _wanted = new();
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
		_reach.Clear();
		foreach (Light3D light in _lights)
		{
			if (!light.IsInsideTree())
			{
				continue;
			}

			float range = RangeOf(light);
			float fadeEnd = light.DistanceFadeEnabled ? light.DistanceFadeBegin + light.DistanceFadeLength : float.MaxValue;
			float distance = eye.DistanceTo(light.GlobalPosition);

			bool reaches = LightsEnabled && distance <= fadeEnd + range;
			_reach.Add((reaches ? distance : float.MaxValue, light));
		}

		_reach.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));
		for (int i = 0; i < _reach.Count; i++)
		{
			bool show = _reach[i].DistanceSq != float.MaxValue && (LightBudget <= 0 || i < LightBudget);
			if (_reach[i].Light.Visible != show)
			{
				_reach[i].Light.Visible = show;
			}
		}

		// Candidates: visible, allowed to cast, with a pool near the eye and inside the view.
		// A light outside the frustum is not rendered at all, and giving it a map would only
		// take one from a light that is.
		bool remaster = Engine_LevelLights.PlacedShadowsEnabled;
		Godot.Collections.Array<Plane> frustum = camera.GetFrustum();
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

			float range = RangeOf(light);
			Vector3 at = light.GlobalPosition;
			float score = Mathf.Max(eye.DistanceTo(at) - range, 0.0f);
			if (score > MaxShadowDistance || !SphereInView(frustum, at, range))
			{
				continue;
			}

			_ranked.Add((score, light));
		}

		Eligible = _ranked.Count;
		_ranked.Sort((a, b) => a.Score.CompareTo(b.Score));

		// STICKY: holders within the slack keep their maps first, then the best of the rest
		// fill what is left, in rank order.
		_wanted.Clear();
		int limit = Mathf.Min(Budget, _ranked.Count);
		for (int i = 0; i < _ranked.Count && _wanted.Count < limit; i++)
		{
			if (i < Budget + Slack && _ranked[i].Light.ShadowEnabled && TargetOf(_ranked[i].Light) > 0.5f)
			{
				_wanted.Add(_ranked[i].Light);
			}
		}

		for (int i = 0; i < _ranked.Count && _wanted.Count < limit; i++)
		{
			_wanted.Add(_ranked[i].Light);
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
		if (light is not OmniLight3D omni)
		{
			return;
		}

		OmniLight3D.ShadowMode mode = Paraboloid ? OmniLight3D.ShadowMode.DualParaboloid : OmniLight3D.ShadowMode.Cube;
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
