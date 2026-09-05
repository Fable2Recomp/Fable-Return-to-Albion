using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// How far each piece of a region casts a sun shadow, by how big it is.
/// </summary>
/// <remarks>
/// THE FAR CASCADE IS FOR THE LANDSCAPE, NOT FOR CRATES. The sun's shadow reaches 300 m because
/// anything outside the cascades stops existing to the light, and a horizon-low sun then shines
/// straight through the hills. But every barrel, sign and flowerpot in that 300 m is drawn into
/// the cascades too: measured 2026-09-04 in Bowerstone Market, the sun's four cascades were
/// 10,466 draw calls and 105M primitives of a 130M-primitive frame, and turning them off took
/// the frame from 15 to 23 fps. What a player can see of a hand-cart's shadow at 80 m is
/// nothing.
///
/// So the reach is per object and keyed on its own size: small dressing stops casting a long way
/// before the buildings do, and the terrain and the big geometry that motivated the 300 m keep
/// casting all the way out. Nothing is hidden — only its shadow stops being drawn — and the
/// cascades near the camera, which is all anyone inspects, are untouched.
/// </remarks>
public partial class Engine_ShadowReach : Node
{
	public const string NodeName = "ShadowReach";

	/// <summary>Debug lever: false leaves every instance casting, the way Godot built it.</summary>
	public static bool Enabled = true;

	/// <summary>Instances whose shadow is currently switched off.</summary>
	public static int Suppressed;

	/// <summary>Instances considered on the last pass.</summary>
	public static int Considered;

	/// <summary>Longest AABB axis under which a thing is dressing rather than architecture.</summary>
	private const float SmallMetres = 3.0f;

	/// <summary>And over which it is landscape or a building, and always casts.</summary>
	private const float LargeMetres = 12.0f;

	/// <summary>How far dressing casts, and how far mid-sized geometry casts.</summary>
	private const float SmallReach = 60.0f;

	private const float MidReach = 150.0f;

	/// <summary>Seconds between passes. A shadow appearing 60 m away is not a per-frame decision.</summary>
	private const double Interval = 0.5;

	private readonly List<GeometryInstance3D> _instances = new();

	private double _since = double.MaxValue;

	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("shadowreach");
		_since += delta;
		if (_since < Interval)
		{
			return;
		}

		_since = 0;
		Camera3D? camera = GetViewport()?.GetCamera3D();
		if (camera is null || GetParent() is not Node region)
		{
			return;
		}

		Collect(region);
		Vector3 eye = camera.GlobalPosition;
		int suppressed = 0;
		foreach (GeometryInstance3D instance in _instances)
		{
			if (!GodotObject.IsInstanceValid(instance))
			{
				continue;
			}

			Aabb bounds = instance.GlobalTransform * instance.GetAabb();
			Vector3 size = bounds.Size;
			float longest = Mathf.Max(size.X, Mathf.Max(size.Y, size.Z));
			float reach = longest >= LargeMetres
				? float.PositiveInfinity
				: longest >= SmallMetres ? MidReach : SmallReach;

			float distance = eye.Clamp(bounds.Position, bounds.End).DistanceTo(eye);
			GeometryInstance3D.ShadowCastingSetting wanted =
				!Enabled || distance <= reach
					? GeometryInstance3D.ShadowCastingSetting.On
					: GeometryInstance3D.ShadowCastingSetting.Off;

			if (wanted == GeometryInstance3D.ShadowCastingSetting.Off)
			{
				++suppressed;
			}

			// Only on a change: the setter reaches the rendering server, and 3,000 of them a
			// pass is the cost this exists to avoid.
			if (instance.CastShadow != wanted)
			{
				instance.CastShadow = wanted;
			}
		}

		Suppressed = suppressed;
		Considered = _instances.Count;
	}

	/// <summary>
	/// The region's drawn pieces. Re-walked each pass rather than cached: props stream in for
	/// many seconds after a region mounts, and a list taken once is a list of what had arrived
	/// by then. The walk is a few thousand nodes twice a second.
	/// </summary>
	private void Collect(Node region)
	{
		_instances.Clear();
		Walk(region);
	}

	private void Walk(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			// Characters and the entities the guest places are NOT dressing: they are what the
			// player is looking at, they move, and there are few of them. The feed hangs them
			// beside the region, so this walk does not reach them at all — but a rig inside a
			// region (a level's own animated piece) is left alone here as well.
			if (child is Skeleton3D)
			{
				continue;
			}

			if (child is GeometryInstance3D instance)
			{
				_instances.Add(instance);
			}

			Walk(child);
		}
	}
}
