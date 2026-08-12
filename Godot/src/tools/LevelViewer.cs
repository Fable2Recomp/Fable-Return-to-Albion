using System;
using Godot;
using Refii.Assets;
using Refii.Core;

namespace Refii.Launcher.Tools;

/// <summary>
/// Displays a built REGION, lit exactly as the editor and the running game light it.
///
/// Lighting is the one thing neither of the existing instruments can answer. `model_viewer`
/// lights a single model with its own key/fill rig, so it says nothing about the level's sun;
/// `mdl_survey` counts. A region's lighting is only visible with the region's own sun,
/// environment and baked data standing together — which is what this loads.
///
/// It goes through <see cref="Engine_GameAssets.TryBuildLevel"/>, the SAME construction path
/// the editor's Game:// tree and <c>GameWorld</c> both use, so a fault seen here is a fault in
/// all three rather than in a fourth copy of the scene builder.
///
/// Orbit with the left mouse button, zoom with the wheel, WASD/QE to fly the pivot,
/// <c>[</c>/<c>]</c> to step time of day.
///
/// Screenshot mode (<c>REFII_LEVEL_SHOT=&lt;path&gt;</c>) frames the region, renders a few
/// frames and quits. The shot path must live OUTSIDE the repository and any build output: a
/// render of game geometry is derived game data and is not redistributable.
/// </summary>
public partial class LevelViewer : Node3D
{
	/// <summary>Level name to show. <c>REFII_LEVEL</c> overrides it.</summary>
	[Export] public string LevelName { get; set; } = "BWSSlums";

	/// <summary>Frames to render before the screenshot, so streaming and materials settle.</summary>
	[Export] public int ShotWarmupFrames { get; set; } = 90;

	private Camera3D _camera = null!;
	private Label _caption = null!;
	private Node3D? _region;

	private float _yaw = 0.6f;
	private float _pitch = 0.35f;
	private float _distance = 40.0f;
	private Vector3 _target = Vector3.Zero;
	private bool _orbiting;

	private string _shotPath = string.Empty;
	private int _framesRendered;
	private bool _requested;

	public override void _Ready()
	{
		_shotPath = OS.GetEnvironment("REFII_LEVEL_SHOT");

		// Streaming is time-boxed per frame, so a big region is still paging in long after the
		// default warmup — and a shot taken then shows a half-built world and reads as missing
		// geometry. REFII_LEVEL_WARMUP buys the frames when the question needs the whole region.
		if (int.TryParse(OS.GetEnvironment("REFII_LEVEL_WARMUP"), out int warmup) && warmup > 0)
		{
			ShotWarmupFrames = warmup;
		}

		string requested = OS.GetEnvironment("REFII_LEVEL");
		if (requested.Length != 0)
		{
			LevelName = requested;
		}

		// The same auto-exposure the game runs, so the editor and the game agree on exposure.
		_camera = new Camera3D
		{
			Name = "Camera",
			Current = true,
			Near = 0.05f,
			Far = 20_000f,
			Attributes = Refii.Lighting.Engine_LevelLighting.BuildCameraAttributes(),

			// The guest's tone curve, measured per frame. Godot's tonemapper is Linear
			// wherever this runs — see Engine_LevelLighting.ConfigureEnvironment.
			Compositor = Refii.Lighting.Engine_LevelLighting.BuildCompositor(),
		};
		AddChild(_camera);

		var layer = new CanvasLayer { Name = "Hud" };
		AddChild(layer);
		_caption = new Label
		{
			Name = "Caption",
			Position = new Vector2(12.0f, 8.0f),
			AutowrapMode = TextServer.AutowrapMode.Off,
		};
		_caption.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.96f));
		_caption.AddThemeColorOverride("font_outline_color", new Color(0.0f, 0.0f, 0.0f));
		_caption.AddThemeConstantOverride("outline_size", 4);
		layer.AddChild(_caption);
		_caption.Text = "mounting banks...";

		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PrintErr("[level-viewer] no game data found; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		if (Engine_GameAssets.Instance is not { } assets)
		{
			GD.PrintErr("[level-viewer] Engine_GameAssets autoload is not present.");
			GetTree().Quit(1);
			return;
		}

		// Subscribe BEFORE mounting: the mount can complete between these two statements.
		assets.Mounted += OnMounted;
		assets.MountFailed += reason =>
		{
			GD.PrintErr($"[level-viewer] could not mount {dataRoot}: {reason}");
			GetTree().Quit(1);
		};

		if (assets.IsMounted)
		{
			OnMounted(assets.FileCount);
		}
		else if (!assets.IsMounting)
		{
			assets.Mount(dataRoot);
		}
	}

	public override void _Process(double delta)
	{
		if (_region is null)
		{
			return;
		}

		if (_shotPath.Length == 0)
		{
			Fly((float)delta);
			return;
		}

		if (++_framesRendered >= ShotWarmupFrames)
		{
			WriteShot();
		}
	}

	/// <summary>
	/// Builds the region. Driven by the MOUNTED SIGNAL, never by polling <c>IsMounted</c>.
	///
	/// Those are not the same moment, which is the trap. <c>Engine_GameAssets.Mount</c> publishes
	/// the filesystem from its worker thread — so <c>IsMounted</c> flips immediately — and only
	/// then defers <c>ReportMounted</c>, which is what installs the terrain, mesh and texture
	/// ResourceFormatLoaders and emits this signal. Polling the flag builds in the window between
	/// the two and loads nothing, failing on `terrain mesh did not load from game://…`
	/// intermittently, depending on which side of the deferred call the frame landed.
	/// </summary>
	private void OnMounted(int fileCount)
	{
		if (_requested || Engine_GameAssets.Instance is not { } assets)
		{
			return;
		}

		_requested = true;
		GD.Print($"[level-viewer] mounted {fileCount:N0} files");

		if (!assets.TryBuildLevel(string.Empty, LevelName, out string scenePath, out string error))
		{
			GD.PrintErr($"[level-viewer] could not build {LevelName}: {error}");
			GetTree().Quit(1);
			return;
		}

		if (ResourceLoader.Load<PackedScene>(scenePath) is not { } packed ||
			packed.Instantiate() is not Node3D region)
		{
			GD.PrintErr($"[level-viewer] built {scenePath} but it did not load as a scene.");
			GetTree().Quit(1);
			return;
		}

		_region = region;
		AddChild(region);

		// REFII_LEVEL_TOD drives the same setter the editor's inspector does, AFTER the region
		// is standing. That is a different path from REFII_FORCE_TOD, which changes the time
		// the scene is BUILT at — and only this one proves the slider relights what is already
		// there rather than just re-aiming the sun over a region frozen at build time.
		string tod = OS.GetEnvironment("REFII_LEVEL_TOD");
		if (tod.Length != 0 &&
			float.TryParse(tod, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float hours) &&
			region.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>(
				Refii.Level.Engine_SkyEnvironment.NodeName) is { } sky)
		{
			sky.TimeOfDay = hours;
			GD.Print($"[level-viewer] time of day moved to {hours:0.0}h after load");
		}

		Isolate(region);
		Frame(region);
		GD.Print($"[level-viewer] showing {scenePath}");
	}

	/// <summary>
	/// Splits the lit image into the terms that make it, because they fail differently and
	/// look alike once added together.
	///
	/// A region that comes out flat and dim has either a sun that is not reaching the surface
	/// or an ambient term that is drowning one that is, and the composite cannot tell those
	/// apart — both read as "the lighting needs work". <c>REFII_LEVEL_LIGHT=direct</c> zeroes
	/// the baked and ambient instance uniforms so only the sun remains; <c>=ambient</c> takes
	/// the sun's energy to zero so only the baked term remains.
	/// </summary>
	private void Isolate(Node3D region)
	{
		string mode = OS.GetEnvironment("REFII_LEVEL_LIGHT");
		if (mode.Length == 0 || mode.Equals("full", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		if (mode.Equals("ambient", StringComparison.OrdinalIgnoreCase))
		{
			if (region.GetNodeOrNull<DirectionalLight3D>(
					Refii.Lighting.Engine_LevelLighting.SunNodeName) is { } off)
			{
				off.LightEnergy = 0.0f;
			}

			GD.Print("[level-viewer] ambient only: the sun is off");
			return;
		}

		int zeroed = 0;
		foreach (Node node in Descendants(region))
		{
			if (node is not GeometryInstance3D instance)
			{
				continue;
			}

			instance.SetInstanceShaderParameter("ambient_colour", new Vector4(0, 0, 0, 1));
			instance.SetInstanceShaderParameter("lightmap_colour", new Vector4(0, 0, 0, 0));
			++zeroed;
		}

		GD.Print($"[level-viewer] direct only: baked and ambient zeroed on {zeroed} instance(s)");
	}

	private static System.Collections.Generic.IEnumerable<Node> Descendants(Node root)
	{
		foreach (Node child in root.GetChildren())
		{
			yield return child;
			foreach (Node grandchild in Descendants(child))
			{
				yield return grandchild;
			}
		}
	}

	/// <summary>
	/// Puts the camera at eye height over the middle of the terrain, looking along it.
	///
	/// Not an orbit around the whole region's bounding sphere: a level is hundreds of metres
	/// across, so framing all of it puts the camera far enough away that nothing about the
	/// lighting is legible — the shadow cascades are out of range and every surface is a
	/// pixel. Standing IN the region is what the game shows.
	/// </summary>
	private void Frame(Node3D region)
	{
		Aabb bounds = default;
		bool any = false;

		foreach (Node node in region.GetChildren())
		{
			if (node is not MeshInstance3D { Mesh: not null } mesh)
			{
				continue;
			}

			Aabb box = mesh.GlobalTransform * mesh.Mesh.GetAabb();
			bounds = any ? bounds.Merge(box) : box;
			any = true;
		}

		if (!any)
		{
			_target = Vector3.Zero;
			_distance = 40.0f;
			return;
		}

		Vector3 centre = bounds.GetCenter();
		_target = new Vector3(centre.X, bounds.Position.Y + (bounds.Size.Y * 0.25f), centre.Z);
		_distance = 45.0f;
		Place();
	}

	private void Fly(float delta)
	{
		float speed = Input.IsKeyPressed(Key.Shift) ? 120.0f : 30.0f;
		var move = Vector3.Zero;

		if (Input.IsKeyPressed(Key.W)) move.Z -= 1.0f;
		if (Input.IsKeyPressed(Key.S)) move.Z += 1.0f;
		if (Input.IsKeyPressed(Key.A)) move.X -= 1.0f;
		if (Input.IsKeyPressed(Key.D)) move.X += 1.0f;
		if (Input.IsKeyPressed(Key.E)) move.Y += 1.0f;
		if (Input.IsKeyPressed(Key.Q)) move.Y -= 1.0f;

		if (move == Vector3.Zero)
		{
			return;
		}

		_target += _camera.GlobalTransform.Basis * move.Normalized() * speed * delta;
		Place();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton button)
		{
			if (button.ButtonIndex == MouseButton.Left)
			{
				_orbiting = button.Pressed;
			}
			else if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp)
			{
				_distance = MathF.Max(_distance * 0.85f, 0.5f);
				Place();
			}
			else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
			{
				_distance = MathF.Min(_distance * 1.18f, 8000.0f);
				Place();
			}
		}
		else if (@event is InputEventMouseMotion motion && _orbiting)
		{
			_yaw -= motion.Relative.X * 0.006f;
			_pitch = Math.Clamp(_pitch - (motion.Relative.Y * 0.006f), -1.5f, 1.5f);
			Place();
		}
		else if (@event is InputEventKey { Pressed: true } key &&
			key.Keycode is Key.Bracketleft or Key.Bracketright)
		{
			StepTimeOfDay(key.Keycode == Key.Bracketright ? 0.5f : -0.5f);
		}
	}

	/// <summary>
	/// Moves the region's own time of day, through the scene's <c>Engine_SkyEnvironmentNode</c>.
	///
	/// This is the same setter the editor's inspector drives, so what this shows at 14:00 is
	/// what the editor shows at 14:00 — the point of a lighting instrument is that it cannot
	/// disagree with the thing it is checking.
	/// </summary>
	private void StepTimeOfDay(float hours)
	{
		if (_region?.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>("SkyEnvironment")
			is not { } sky)
		{
			return;
		}

		sky.TimeOfDay = Mathf.PosMod(sky.TimeOfDay + hours, 24.0f);
		Place();
	}

	private void Place()
	{
		var offset = new Vector3(
			MathF.Cos(_pitch) * MathF.Sin(_yaw),
			MathF.Sin(_pitch),
			MathF.Cos(_pitch) * MathF.Cos(_yaw));

		_camera.GlobalPosition = _target + (offset * _distance);
		_camera.LookAt(_target, Vector3.Up);

		float tod = _region?.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>("SkyEnvironment")
			?.TimeOfDay ?? -1.0f;

		_caption.Text = $"{LevelName}   tod={tod:0.0}h   pos={_camera.GlobalPosition}   " +
			"LMB orbit · wheel zoom · WASDQE fly · [ ] time of day";
	}

	/// <summary>
	/// Writes the terrain's baked lightmap beside the shot, when <c>REFII_LEVEL_LIGHTMAP=1</c>.
	///
	/// What the texture CONTAINS decides how it may be combined, and the two candidates look
	/// identical in a log line: full baked radiance has to replace the runtime ambient, while
	/// a shadow or occlusion mask has to multiply it. One is chromatic, the other is grey, so
	/// the picture settles it and a count never will.
	/// </summary>
	private void WriteLightmap()
	{
		if (OS.GetEnvironment("REFII_LEVEL_LIGHTMAP") != "1" || _region is null)
		{
			return;
		}

		if (_region.GetNodeOrNull<MeshInstance3D>(Refii.Level.Engine_LevelScene.TerrainNodeName)
				is not { MaterialOverride: ShaderMaterial splat } ||
			splat.GetShaderParameter("lightmap").As<Texture2D>() is not { } lightmap)
		{
			GD.Print("[level-viewer] the terrain carries no lightmap");
			return;
		}

		string path = _shotPath + ".lightmap.png";
		Image image = lightmap.GetImage();
		GD.Print(image.SavePng(path) == Error.Ok
			? $"[level-viewer] wrote {path} ({image.GetWidth()}x{image.GetHeight()} " +
			  $"{image.GetFormat()})"
			: $"[level-viewer] could not write {path}");
	}

	private void WriteShot()
	{
		WriteLightmap();

		Image image = GetViewport().GetTexture().GetImage();
		Error saved = image.SavePng(_shotPath);
		GD.Print(saved == Error.Ok
			? $"[level-viewer] wrote {_shotPath}"
			: $"[level-viewer] could not write {_shotPath}: {saved}");

		GetTree().Quit(saved == Error.Ok ? 0 : 1);
	}
}
