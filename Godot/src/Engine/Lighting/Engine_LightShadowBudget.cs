using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// Hands live shadow maps to the placed lights nearest the camera, and only those.
/// </summary>
/// <remarks>
/// Godot serves positional shadows from one fixed atlas; enabling ShadowEnabled on all ~190 of
/// a region's fixtures oversubscribes it and Godot silently drops shadows — worst exactly
/// where lamps are densest, which read as "the shadows in town are not showing". A player can
/// only inspect a handful of pools at once, so a handful of live maps rotating with the camera
/// is indistinguishable from all of them — the same trade every deferred-era engine makes.
///
/// With `placed_light_shadows` off, only fixtures whose DATA states CastShadows compete for
/// the budget, so the authored behaviour survives underneath the remaster switch.
/// </remarks>
public partial class Engine_LightShadowBudget : Node
{
	public const string NodeName = "LightShadowBudget";

	/// <summary>
	/// Shadowed placed lights at once. ZERO: fixtures never cast, which is what the 360 did and
	/// the only policy that reads the same from every camera angle.
	/// </summary>
	/// <remarks>
	/// Measured 2026-09-05 in Bowerstone Market (b175, 190 fixtures, 48 visible): 4 shadowed
	/// fixtures = 20 fps with a 152 M-primitive shadow pass; all 48 shadowed = 10 fps, 1.04 G
	/// shadow primitives, 58 k shadow draw calls. And a handful is WORSE than none: a lamp
	/// blocks correctly while it is among the nearest few and shines through the wall the
	/// moment the camera moves it out of the budget — the inconsistency this replaces.
	/// A non-zero value is a dev lever (`placed_light_shadows`, or `render shadowBudget` on the
	/// debug channel) for looking at one lamp's occlusion, never a shipped setting.
	/// </remarks>
	public static int Budget = 0;

	/// <summary>Placed lights kept VISIBLE at once — the nearest that can still reach the eye.
	/// Measured 2026-09-04: 190 visible lights cost 13 ms of draw with every shadow off.</summary>
	public static int LightBudget = 48;

	/// <summary>Debug lever: false hides every placed light regardless of the budget.</summary>
	public static bool LightsEnabled = true;

	/// <summary>Seconds between reassignments — shadow churn is visible, so not per-frame.</summary>
	private const double Interval = 0.25;

	private readonly List<Light3D> _lights = new();
	private readonly List<(float DistanceSq, Light3D Light)> _ranked = new();
	private readonly List<(float DistanceSq, Light3D Light)> _reach = new();
	private double _since = double.MaxValue;

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("lightshadowbudget");
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

		// With `placed_light_shadows` off nothing competes for shadow maps, whatever Budget
		// says: the authored CastShadows flag is not honoured either, because honouring it for
		// some fixtures and not others is the inconsistency the zero budget exists to end.
		bool forceAll = Engine_LevelLights.PlacedShadowsEnabled;
		if (!forceAll)
		{
			foreach (Light3D light in _lights)
			{
				if (light.ShadowEnabled)
				{
					light.ShadowEnabled = false;
				}
			}
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

			float range = light is OmniLight3D omni ? omni.OmniRange : light is SpotLight3D spot ? spot.SpotRange : 0f;
			float fadeEnd = light.DistanceFadeEnabled ? light.DistanceFadeBegin + light.DistanceFadeLength : float.MaxValue;
			float distance = eye.DistanceTo(light.GlobalPosition);
			bool reaches = LightsEnabled && distance <= fadeEnd + range;
			_reach.Add((reaches ? distance : float.MaxValue, light));
		}

		_reach.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));
		for (int i = 0; i < _reach.Count; i++)
		{
			bool show = _reach[i].DistanceSq != float.MaxValue && i < LightBudget;
			if (_reach[i].Light.Visible != show)
			{
				_reach[i].Light.Visible = show;
			}
		}

		_ranked.Clear();
		foreach (Light3D light in _lights)
		{
			bool eligible = forceAll && light.Visible;
			if (!eligible || !light.IsInsideTree())
			{
				if (light.ShadowEnabled)
				{
					light.ShadowEnabled = false;
				}

				continue;
			}

			_ranked.Add((eye.DistanceSquaredTo(light.GlobalPosition), light));
		}

		_ranked.Sort((a, b) => a.DistanceSq.CompareTo(b.DistanceSq));

		for (int i = 0; i < _ranked.Count; i++)
		{
			bool want = i < Budget && forceAll;
			if (_ranked[i].Light.ShadowEnabled != want)
			{
				_ranked[i].Light.ShadowEnabled = want;
			}
		}
	}
}
