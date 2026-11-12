using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// Re-voxelises Godot's GI when static geometry has STREAMED IN around the camera.
/// </summary>
/// <remarks>
/// SDFGI voxelises a cascade when it is created and, after that, only the slab the camera
/// scrolls into: `GI::SDFGI::update` (gi.cpp) clears every cascade's dirty region each frame
/// and sets it from camera movement alone. Nothing marks a cascade dirty because a mesh was
/// added inside it. This world arrives over many frames and a save loads INTO a room, so the
/// cascades round the player were built empty and stayed empty — no bounce, no fill, a character
/// lit by one fixture and nothing else, while the same scene built up front lights correctly.
///
/// The only invalidation the renderer offers is its own: a frame drawn with SDFGI disabled
/// deletes it, and the next enabled frame creates it with every cascade dirty
/// (render_forward_clustered.cpp, `needs_sdfgi`). A rebuild costs a re-voxelise and a fade-in
/// of the fill, so it is spent only where it shows: arrivals NEAR the camera, after they have
/// gone quiet, and no more often than <see cref="MinSecondsBetween"/>. The far streaming
/// frontier lands in coarse cascades and is picked up as they scroll.
///
/// NEAR ANY VIEW. SDFGI is per viewport, centred on that viewport's camera, but the switch is the
/// one shared Environment: on split screen an arrival near either player's eye rebuilds both
/// views' cascades, which is the only invalidation there is.
/// </remarks>
public static class Engine_GiRefresh
{
	/// <summary>An arrival whose bounds come this close to the camera is worth a rebuild: cascades 0 and 1.</summary>
	private const float NearMetres = 50.0f;

	/// <summary>How long arrivals must stay quiet, so a load-in is one rebuild and not one per prop.</summary>
	private const double SettleSeconds = 0.6;

	/// <summary>A room that never stops streaming still gets its GI after this long.</summary>
	private const double MaxWaitSeconds = 5.0;

	private const double MinSecondsBetween = 2.5;

	/// <summary>Frames drawn with SDFGI off before it is handed back; one is what deletes it.</summary>
	private const int OffFrames = 2;

	/// <summary>
	/// Frames drawn after <see cref="HoldForWorldLoad"/> releases before SDFGI is enabled, so the
	/// main scene's opaque/shadow MultiUmaBuffers and D3D12 staging buffer fences settle first.
	/// </summary>
	public const int PostHoldSettleFrameCount = 2;

	/// <summary>Debug lever: true holds the GI off, so its cost can be read as a difference.</summary>
	public static bool ForcedOff;

	private static bool _holdForWorldLoad;
	private static int _postHoldSettleFrames;

	/// <summary>
	/// True while a world load is still streaming props/entities/uploads behind the cover,
	/// deferring SDFGI allocation until settle frames.
	/// </summary>
	public static bool HoldForWorldLoad
	{
		get => _holdForWorldLoad;
		set
		{
			if (_holdForWorldLoad && !value)
			{
				_postHoldSettleFrames = PostHoldSettleFrameCount;
			}
			else if (value)
			{
				_postHoldSettleFrames = PostHoldSettleFrameCount;
			}

			_holdForWorldLoad = value;
		}
	}

	/// <summary>True during the post-hold settle window before SDFGI activates.</summary>
	public static bool SettlingAfterWorldLoad => _postHoldSettleFrames > 0;

	/// <summary>Resets the world-load hold and post-hold settle state for isolated unit tests.</summary>
	internal static void ResetWorldLoadGateForTest()
	{
		_holdForWorldLoad = false;
		_postHoldSettleFrames = 0;
	}

	/// <summary>
	/// Advances the world-load hold and post-hold settle state machine by one frame and returns
	/// whether SDFGI is permitted to be enabled on this frame.
	/// </summary>
	public static bool AdvanceWorldLoadGate()
	{
		if (ForcedOff || HoldForWorldLoad)
		{
			if (HoldForWorldLoad)
			{
				lock (_arrived)
				{
					_arrived.Clear();
				}

				_pending = false;
				_quiet = 0;
				_waiting = 0;
			}

			return false;
		}

		if (_postHoldSettleFrames > 0)
		{
			lock (_arrived)
			{
				_arrived.Clear();
			}

			_pending = false;
			_quiet = 0;
			_waiting = 0;
			--_postHoldSettleFrames;
			return false;
		}

		return true;
	}

	/// <summary>Rebuilds since boot.</summary>
	public static int Rebuilds;

	/// <summary>True while this holds SDFGI off for the rebuild; ConfigureGlobalIllumination honours it.</summary>
	public static bool HoldingOff { get; private set; }

	private static readonly List<Aabb> _arrived = new();
	private static bool _pending;
	private static double _quiet;
	private static double _waiting;
	private static double _sinceRebuild = double.MaxValue;
	private static int _offFor;

	/// <summary>A static instance has landed in the tree. Any thread.</summary>
	public static void StaticGeometryArrived(VisualInstance3D instance)
	{
		if (!GodotObject.IsInstanceValid(instance) || !instance.IsInsideTree())
		{
			return;
		}

		Aabb local = instance switch
		{
			MultiMeshInstance3D { Multimesh: { } mm } =>
				mm.CustomAabb.Size != Vector3.Zero ? mm.CustomAabb : (mm.Mesh?.GetAabb() ?? default),
			MeshInstance3D mi =>
				mi.CustomAabb.Size != Vector3.Zero ? mi.CustomAabb : (mi.Mesh?.GetAabb() ?? default),
			GeometryInstance3D gi => gi.CustomAabb,
			_ => default,
		};
		Aabb bounds = instance.GlobalTransform * local;
		lock (_arrived)
		{
			if (_arrived.Count < 512)
			{
				_arrived.Add(bounds);
			}
		}
	}

	/// <summary>
	/// Once per frame from the environment's owner. Main thread. <paramref name="camera"/> is the
	/// window's; the split view's second camera is added through <see cref="Refii.Core.Engine_Views"/>.
	/// </summary>
	public static void Tick(Godot.Environment? environment, Camera3D? camera, double delta)
	{
		if (environment is null)
		{
			return;
		}

		// Respect the configured `sdfgi_enabled` setting (e.g. false on Low/Balanced presets):
		// never turn SDFGI back on when the user or preset disabled it.
		if (!Engine_LevelLighting.SdfgiEnabled || !Engine_LightingFrame.ModernModel)
		{
			HoldingOff = false;
			_pending = false;
			_waiting = 0;
			_quiet = 0;
			if (environment.SdfgiEnabled)
			{
				environment.SdfgiEnabled = false;
			}
			lock (_arrived)
			{
				if (_arrived.Count != 0)
				{
					_arrived.Clear();
				}
			}
			return;
		}

		if (!AdvanceWorldLoadGate())
		{
			if (environment.SdfgiEnabled)
			{
				environment.SdfgiEnabled = false;
			}
			return;
		}

		if (!HoldingOff && !environment.SdfgiEnabled)
		{
			environment.SdfgiEnabled = true;
			_sinceRebuild = 0;
		}

		if (HoldingOff)
		{
			if (++_offFor >= OffFrames)
			{
				HoldingOff = false;
				environment.SdfgiEnabled = true;
				Rebuilds++;
				GD.Print($"[gi] rebuilt after streamed geometry settled near the camera (rebuild {Rebuilds})");
			}

			return;
		}

		_sinceRebuild += delta;
		bool near = false;
		lock (_arrived)
		{
			if (_arrived.Count != 0)
			{
				foreach (Camera3D view in Refii.Core.Engine_Views.Cameras(camera))
				{
					Vector3 eye = view.GlobalPosition;
					foreach (Aabb bounds in _arrived)
					{
						Vector3 closest = eye.Clamp(bounds.Position, bounds.End);
						if (closest.DistanceSquaredTo(eye) <= NearMetres * NearMetres)
						{
							near = true;
							break;
						}
					}

					if (near)
					{
						break;
					}
				}

				_arrived.Clear();
			}
		}

		if (near)
		{
			_pending = true;
			_quiet = 0;
		}
		else
		{
			_quiet += delta;
		}

		if (!_pending)
		{
			return;
		}

		_waiting += delta;
		if (!environment.SdfgiEnabled || _sinceRebuild < MinSecondsBetween ||
			(_quiet < SettleSeconds && _waiting < MaxWaitSeconds))
		{
			return;
		}

		_pending = false;
		_waiting = 0;
		_sinceRebuild = 0;
		_offFor = 0;
		HoldingOff = true;
		environment.SdfgiEnabled = false;
	}
}
