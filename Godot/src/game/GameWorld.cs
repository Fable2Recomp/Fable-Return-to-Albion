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
	/// Builds the region the GUEST NAMED, once it has named one.
	///
	/// `sub_82379628` writes the destination in plain text before it starts the game —
	/// g_StartWorldName / g_StartLevelName / g_StartEntranceName — so Godot is told which
	/// region to build rather than inferring it from anything it observes. That is the
	/// architecture's whole split, and it is why this waits for the guest instead of
	/// guessing from a config value.
	/// </summary>
	private void FollowGuestIntoTheWorld()
	{
		// WAIT FOR IN-GAME. These globals are populated at boot with the startup script's
		// placeholders — SetInitialWorldName/SetInitialLevelName put "TestWorld/TestLevel1"
		// there long before anyone picks New Game, and sub_82379628 only overwrites them
		// with the real destination as the game starts. Latching the first non-empty read
		// captures the placeholder and never looks again, which is exactly what happened.
		// byte_83496A5E is the guest stating the game has begun, so it is the right edge.
		if (_loaded || !HostBridge.IsInGame ||
			HostBridge.GetStartLevel() is not { } start || start.Level.Length == 0)
		{
			return;
		}

		_loaded = true;
		GD.Print($"[game-world] the guest is going to {start.World}/{start.Level} " +
			$"(entrance '{start.Entrance}')");

		// Build it through the editor's own path, then put it under the live camera.
		if (Refii.Assets.Engine_GameAssets.Instance is not { } assets)
		{
			GD.PrintErr("[game-world] no asset mount; the camera is live over an empty scene.");
			return;
		}

		if (!assets.TryBuildLevel(start.World, start.Level, out string scenePath,
				out string error))
		{
			// Named, not silent. An empty world under a live camera renders as flat grey,
			// which is indistinguishable from "rendering is broken" — and was read as
			// exactly that.
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
