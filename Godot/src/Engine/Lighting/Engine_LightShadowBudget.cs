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

	/// <summary>Live shadow maps at once. Comfortably inside the default 4096 atlas.</summary>
	public const int Budget = 8;

	/// <summary>Seconds between reassignments — shadow churn is visible, so not per-frame.</summary>
	private const double Interval = 0.25;

	private readonly List<Light3D> _lights = new();
	private readonly List<(float DistanceSq, Light3D Light)> _ranked = new();
	private double _since = double.MaxValue;

	public override void _Process(double delta)
	{
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

		bool forceAll = Engine_LevelLights.PlacedShadowsEnabled;
		Vector3 eye = camera.GlobalPosition;

		_ranked.Clear();
		foreach (Light3D light in _lights)
		{
			bool eligible = forceAll ||
				(light.HasMeta(Engine_LevelLights.AuthoredShadowMeta) &&
					light.GetMeta(Engine_LevelLights.AuthoredShadowMeta).AsBool());
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
			bool want = i < Budget;
			if (_ranked[i].Light.ShadowEnabled != want)
			{
				_ranked[i].Light.ShadowEnabled = want;
			}
		}
	}
}
