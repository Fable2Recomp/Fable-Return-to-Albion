using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The world, rendered by GODOT through the guest's camera.
///
/// This is the plan's phase 1 in its smallest form: the guest simulates and tells us where
/// it is looking; Godot draws. Proving the camera first is deliberate — it is nearly free,
/// it needs no entity sync, and nothing else in the world feed is visible until something
/// is looking at it.
/// </summary>
public partial class GameWorld : Node3D
{
	private Camera3D _camera = null!;
	private bool _reported;

	public override void _Ready()
	{
		_camera = new Camera3D
		{
			Name = "GuestCamera",
			Current = true,
			Near = 0.05f,
			Far = 20_000f,
		};

		AddChild(_camera);
	}

	public override void _Process(double delta)
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		FollowGuestIntoTheWorld();

		if (HostBridge.GetCamera() is not { } camera || camera.IsDefault)
		{
			return;
		}

		// Fable's world is right-handed with X right, Y FORWARD, Z up — proven from
		// sub_821B0068 building [cross(fwd,up), fwd, up] as a proper rotation, and from the
		// engine's own defaults (forward +Y, up +Z). Godot is right-handed with Y up and
		// the camera looking down -Z, so the change of basis is (x, y, z) -> (x, z, -y),
		// which has determinant +1 and preserves handedness.
		Vector3 position = ToGodot(camera.Position);
		Vector3 forward = ToGodot(camera.Forward).Normalized();
		Vector3 up = ToGodot(camera.Up);

		if (forward.IsZeroApprox())
		{
			return;
		}

		// Re-orthonormalise up against forward. This matches the guest rather than fighting
		// it — it does the same Gram-Schmidt itself in sub_822B52F8, correcting UP and
		// holding FORWARD fixed.
		Vector3 basisZ = -forward;
		Vector3 basisY = (up - basisZ * up.Dot(basisZ)).Normalized();
		if (basisY.IsZeroApprox())
		{
			return;
		}

		_camera.GlobalTransform = new Transform3D(
			new Basis(basisY.Cross(basisZ), basisY, basisZ), position);

		// The guest stores a HORIZONTAL field of view; Godot wants the vertical one.
		_camera.Fov = camera.VerticalFovDegrees;
		_camera.KeepAspect = Camera3D.KeepAspectEnum.Height;

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[game-world] driving Godot's camera from the guest: " +
				$"pos={position} fov={_camera.Fov:F1}deg");
		}
	}

	/// <summary>
	/// Builds the region the guest named. `sub_82379628` writes the destination before it
	/// starts the game, so Godot is told which region to build rather than inferring one.
	/// </summary>
	private void FollowGuestIntoTheWorld()
	{
		// Wait for in-game: the startup script leaves "TestWorld/TestLevel1" in these globals
		// until sub_82379628 overwrites them, so an earlier read latches the placeholder.
		if (_loaded || !HostBridge.IsInGame ||
			HostBridge.GetStartLevel() is not { } start || start.Level.Length == 0)
		{
			return;
		}

		_loaded = true;
		GD.Print($"[game-world] the guest is going to {start.World}/{start.Level} " +
			$"(entrance '{start.Entrance}')");

		// The editor's own construction path, then mount it under the live camera.
		if (Refii.Assets.Engine_GameAssets.Instance is not { } assets)
		{
			GD.PrintErr("[game-world] no asset mount; the camera is live over an empty scene.");
			return;
		}

		if (!assets.TryBuildLevel(start.World, start.Level, out string scenePath,
				out string error))
		{
			// Named, not silent: an empty world under a live camera is flat grey, which reads
			// as "rendering is broken".
			GD.PrintErr($"[game-world] could not build {start.World}/{start.Level}: {error}");
			return;
		}

		if (ResourceLoader.Load<PackedScene>(scenePath) is not { } packed ||
			packed.Instantiate() is not Node3D region)
		{
			GD.PrintErr($"[game-world] built {scenePath} but it did not load as a scene.");
			return;
		}

		AddChild(region);
		GD.Print($"[game-world] built and mounted {start.World}/{start.Level} from {scenePath}");
	}

	private bool _loaded;

	/// <summary>Fable world space to Godot world space.</summary>
	private static Vector3 ToGodot(Vector3 fable) =>
		new(fable.X, fable.Z, -fable.Y);
}
