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
	private int _debugView = -1;
	private float _bakeWeight = -1f;
	private int _framesRendered;
	private bool _requested;

	/// <summary>
	/// Frames between sun reports, read from <c>REFII_EXPOSURE_DUMP</c>. Zero is off.
	/// </summary>
	/// <remarks>
	/// THE SAME SWITCH AS THE EXPOSURE DUMP, ON PURPOSE. The two readings only answer anything
	/// TOGETHER: a frame whose pixels sit far below the tone curve's clip point is either lit
	/// weakly or exposed correctly for a weak scene, and the sun's own colour times its energy is
	/// what separates those. Two independent switches would make it possible to read one without
	/// the other, which is the mistake this pairing exists to prevent.
	/// </remarks>
	private int _reportPeriod;

	private long _frames;

	/// <summary>Hold the atmosphere off, from <c>REFII_LEVEL_FOG=off</c>.</summary>
	private bool _fogOff;

	public override void _Ready()
	{
		_shotPath = OS.GetEnvironment("REFII_LEVEL_SHOT");

		if (int.TryParse(OS.GetEnvironment("REFII_EXPOSURE_DUMP"), out int period) && period > 0)
		{
			_reportPeriod = period;
		}

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
		if (_debugView >= 0)
		{
			RenderingServer.GlobalShaderParameterSet("fable_light_debug", _debugView);
		}

		if (_bakeWeight > 0f)
		{
			RenderingServer.GlobalShaderParameterSet("fable_bake_weight", _bakeWeight);
		}

		if (_region is null)
		{
			return;
		}

		if (_fogOff)
		{
			RenderingServer.GlobalShaderParameterSet("atmos_fog_on", false);
		}

		if (_reportPeriod > 0 && ++_frames % _reportPeriod == 0)
		{
			ReportSun();
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
	/// The sun terms the region is actually being lit by, printed beside the exposure dump.
	///
	/// READ OFF THE LIVE NODE, NOT RECOMPUTED. The DirectionalLight3D is what the renderer
	/// consumes, so reporting its own colour and energy cannot disagree with the image the way a
	/// second evaluation of the theme could — and a theme read that quietly stopped reaching the
	/// light is precisely one of the faults this is being used to look for.
	///
	/// The colour and the energy are two halves of ONE authored RGB — <c>Engine_LightingFrame.Split</c>
	/// normalises the triple by its largest channel and carries that magnitude as the energy — so
	/// the number to compare against a theme is the PRODUCT, and either half alone means nothing.
	/// </summary>
	private void ReportSun()
	{
		if (_region?.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>(
				Refii.Level.Engine_SkyEnvironment.NodeName) is not { } sky)
		{
			GD.Print("[sun] the region carries no sky environment; nothing states a sun");
			return;
		}

		if (sky.Lighting is not { } lighting)
		{
			GD.Print("[sun] the sky environment has not evaluated yet");
			return;
		}

		var direction = new Vector3(
			lighting.SunDirection[0], lighting.SunDirection[1], lighting.SunDirection[2]);
		direction = direction.LengthSquared() > 1e-8f ? direction.Normalized() : Vector3.Up;

		float elevation = Mathf.RadToDeg(MathF.Asin(Math.Clamp(direction.Y, -1.0f, 1.0f)));

		var authored = new Vector3(
			lighting.SunColour[0] * lighting.SunEnergy,
			lighting.SunColour[1] * lighting.SunEnergy,
			lighting.SunColour[2] * lighting.SunEnergy);

		// Rec.709, the same weights the histogram bins with, so this number and the histogram's
		// luminances are in one space and can be held against each other directly.
		float sunLuminance = Refii.Lighting.Engine_PostFxModel.Luminance(
			authored.X, authored.Y, authored.Z);

		GD.Print($"[sun] tod={sky.TimeOfDay:0.00}h  rig={direction}  elevation={elevation:0.0}deg  " +
			$"shadow={lighting.SunShadow}");
		GD.Print($"[sun]   main light  colour={lighting.SunColour[0]:0.000}," +
			$"{lighting.SunColour[1]:0.000},{lighting.SunColour[2]:0.000}  " +
			$"energy={lighting.SunEnergy:0.000}  authored={authored}  luminance={sunLuminance:0.0000}");

		string ambient = lighting.HasAmbient
			? $"colour={lighting.AmbientColour[0]:0.000},{lighting.AmbientColour[1]:0.000}," +
			  $"{lighting.AmbientColour[2]:0.000} energy={lighting.AmbientEnergy:0.000}"
			: "NOT STATED by this theme, so Godot's ambient is left alone";

		GD.Print($"[sun]   ambient  {ambient}");

		GD.Print($"[sun]   lightmap={lighting.LightmapColour[0]:0.000}," +
			$"{lighting.LightmapColour[1]:0.000},{lighting.LightmapColour[2]:0.000}  " +
			$"darkening={lighting.DarkeningColour[0]:0.000}," +
			$"{lighting.DarkeningColour[1]:0.000},{lighting.DarkeningColour[2]:0.000}");

		// SunIntensity drives the SKY DOME, not the main light — it is registered against the Sky
		// record rather than the Lighting one. Printed here because a blown horizon over dark
		// surfaces is the two of them disagreeing, and reading only the light cannot see it.
		if (sky.Theme is { } theme)
		{
			GD.Print($"[sun]   sky dome  SunIntensity={theme.SunIntensity:0.000} " +
				$"(stated={theme.HasSunIntensity})");
		}

		if (_region.GetNodeOrNull<DirectionalLight3D>(
				Refii.Lighting.Engine_LevelLighting.SunNodeName) is { } light)
		{
			GD.Print($"[sun]   live node  colour={light.LightColor} energy={light.LightEnergy:0.000} " +
				$"shadow={light.ShadowEnabled}");
		}
		else
		{
			GD.Print("[sun]   live node  MISSING — the region has no DirectionalLight3D named " +
				$"'{Refii.Lighting.Engine_LevelLighting.SunNodeName}'");
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

		// REFII_LEVEL_DEBUG=<n> selects the shaders' lighting debug view (the lighting_debug_view
		// cvar's numbering); REFII_LEVEL_BAKE=<w> overrides the bake weight. Both are globals the
		// sky node publishes from refii.toml, so they are re-stated every frame (see _Process).
		// The fixtures near the camera target, as built: what Godot was actually handed.
		foreach (Node node in Descendants(region))
		{
			if (node is Light3D fixture && fixture is not DirectionalLight3D &&
				fixture.GlobalPosition.DistanceTo(_target) < 6.0f)
			{
				float range = fixture is OmniLight3D omni ? omni.OmniRange : fixture is SpotLight3D spot ? spot.SpotRange : 0f;
				GD.Print($"[level-viewer] fixture '{fixture.Name}' at {fixture.GlobalPosition} {fixture.GlobalPosition.DistanceTo(_target):0.0}m energy={fixture.LightEnergy:0.00} range={range:0.0} colour={fixture.LightColor} visible={fixture.Visible} fade={fixture.DistanceFadeEnabled}/{fixture.DistanceFadeBegin:0}+{fixture.DistanceFadeLength:0}");
			}
		}

		if (int.TryParse(OS.GetEnvironment("REFII_LEVEL_DEBUG"), out int debugView))
		{
			_debugView = debugView;
		}

		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_BAKE"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float bakeWeight))
		{
			_bakeWeight = bakeWeight;
		}
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
		// THE ATMOSPHERE IS A SEPARATE TERM FROM BOTH OF THE LIGHT ONES, and until this switch
		// existed it could not be taken out of the picture at all. That is not a gap in the
		// instrument, it is the gap that let a wrong answer stand: with `direct` and `ambient` the
		// only isolations available, a frame whose luminance comes almost entirely from in-scatter
		// reports the SAME histogram under both, and the obvious reading — "neither light term
		// matters" — says nothing about what does.
		//
		// It clears the published global rather than editing a material, because that is the one
		// switch every fogged surface in the region reads: `fable_atmosphere_terms` returns false
		// on it before computing anything, so nothing is left half-attenuated.
		//
		// RE-ASSERTED EVERY FRAME, not cleared once. `Engine_SkyEnvironment.ApplyAtmosphereGlobals`
		// republishes this global from the theme on every re-evaluation, so a single clear here is
		// overwritten before the first frame is drawn — and the run then reports a fully fogged
		// frame under a flag that says fog is off, which is worse than having no switch.
		if (OS.GetEnvironment("REFII_LEVEL_FOG").Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			_fogOff = true;
			GD.Print("[level-viewer] fog off: atmos_fog_on held clear every frame");
		}

		// REFII_LEVEL_MIST=off: drop the authored mist volumes and the volumetric fog they
		// enable, so a pair of shots differs only in the mist.
		if (OS.GetEnvironment("REFII_LEVEL_MIST").Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			int dropped = 0;
			foreach (Node node in Descendants(region))
			{
				if (node is FogVolume fogVolume)
				{
					fogVolume.Visible = false;
					dropped++;
				}
			}

			foreach (Node node in Descendants(region))
			{
				if (node is Refii.Level.Engine_SkyEnvironmentNode skyNode && skyNode.Environment is { } env)
				{
					env.VolumetricFogEnabled = false;
				}
			}

			GD.Print($"[level-viewer] mist off: {dropped} volume(s) hidden");
		}

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

		// ABOVE THE TERRAIN, NOT A FRACTION OF THE WHOLE REGION'S BOX.
		//
		// A quarter of the way up the merged bounds put the camera UNDER THE GROUND on BWSSlums:
		// the box spans the tallest building in the region, so a fraction of its height is below
		// the surface wherever the terrain does not sit on the box's floor. A run from under the
		// world is not an obviously broken picture — it is a plausible frame of sky dome and
		// undersides, and it meters as one. The exposure dump read 80% sky and reported it as the
		// region's luminance distribution.
		//
		// The terrain's own AABB is the answer, and it is the one node whose top is ground rather
		// than roof. Falling back to the merged box's top keeps a region with no terrain framed
		// from above rather than from inside it.
		float ground = region.GetNodeOrNull<MeshInstance3D>(
				Refii.Level.Engine_LevelScene.TerrainNodeName) is { Mesh: not null } terrain
			? (terrain.GlobalTransform * terrain.Mesh.GetAabb()).End.Y
			: bounds.End.Y;

		_target = new Vector3(centre.X, ground + 2.0f, centre.Z);
		_distance = 45.0f;

		// The sky's share of the frame is a METERING choice, not a cosmetic one — a histogram taken
		// over a frame that is mostly dome describes the dome. Steeper looks further down.
		// REFII_LEVEL_DISTANCE / REFII_LEVEL_YAW / REFII_LEVEL_AT=x,y,z: put the camera INSIDE an
		// interior. The default orbit frames the level's bounds from outside, which for a cave is
		// the back of its shell.
		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_DISTANCE"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float distance) && distance > 0f)
		{
			_distance = distance;
		}

		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_YAW"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float yaw))
		{
			_yaw = Mathf.DegToRad(yaw);
		}

		string at = OS.GetEnvironment("REFII_LEVEL_AT");
		if (at.Length != 0)
		{
			string[] xyz = at.Split(',');
			if (xyz.Length == 3 &&
				float.TryParse(xyz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
				float.TryParse(xyz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
				float.TryParse(xyz[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
			{
				_target = new Vector3(x, y, z);
			}
		}

		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_PITCH"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float pitch))
		{
			_pitch = Math.Clamp(Mathf.DegToRad(pitch), -1.5f, 1.5f);
		}

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
