using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Models;

namespace Refii.Launcher.Tools;

/// <summary>
/// Displays Fable 2 geometry in 3D — the first thing in this project that actually LOOKS
/// at a model rather than counting one.
///
/// The survey proves 4,332 models parse. It cannot prove they are right: mirrored
/// geometry, inside-out winding, materials bound to the wrong surface and UVs off by a
/// flip all parse perfectly and all count as success. Those are visual defects and need
/// a visual instrument.
///
/// Two modes:
/// <list type="bullet">
/// <item>Interactive — orbit with the left mouse button, zoom with the wheel,
/// <c>N</c>/<c>P</c> to step through models, <c>W</c> to toggle wireframe.</item>
/// <item>Screenshot (<c>REFII_MODEL_SHOT=&lt;path&gt;</c>) — frames the model, renders a
/// few frames, writes a PNG and quits. This is what makes the viewer checkable without a
/// human at the keyboard.</item>
/// </list>
///
/// The shot path must live OUTSIDE the repository and any build output: a render of game
/// geometry is derived game data and is not redistributable.
/// </summary>
public partial class ModelViewer : Node3D
{
	/// <summary>Virtual path of the model to show. <c>REFII_MODEL</c> overrides it.</summary>
	[Export] public string ModelPath { get; set; } = string.Empty;

	/// <summary>Frames to render before the screenshot, so materials and textures settle.</summary>
	[Export] public int ShotWarmupFrames { get; set; } = 6;

	private readonly List<string> _catalogue = new();
	private int _index;

	private BnkFileSystem? _filesystem;
	private MeshInstance3D _instance = null!;
	private Camera3D _camera = null!;
	private Label _caption = null!;

	private float _yaw = 0.6f;
	private float _pitch = 0.35f;
	private float _distance = 4.0f;
	private Vector3 _target = Vector3.Zero;
	private bool _orbiting;

	private string _shotPath = string.Empty;
	private int _framesRendered;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PrintErr("[viewer] no game data found; set REFII_GAME_DATA or stage assets/game.");
			GetTree().Quit(1);
			return;
		}

		_shotPath = OS.GetEnvironment("REFII_MODEL_SHOT");

		string mode = OS.GetEnvironment("REFII_MAT_MODE");
		if (mode.Length != 0 && Enum.TryParse(mode, ignoreCase: true, out DisplayMode parsed))
		{
			_mode = parsed;
		}

		_filesystem = BnkFileSystem.Mount(dataRoot);
		GD.Print($"[viewer] mounted {_filesystem.FileCount:N0} files");

		// Materials resolve their textures through these two resolvers, NOT through the
		// filesystem handle. Mounting alone leaves them unset, and the only symptom is that
		// every model renders untextured grey — no warning, no failed load, because a
		// material with no diffuse is a perfectly valid material.
		Refii.Models.Engine_BnkMeshLoader.SetResolver(ReadAsset);
		Refii.Models.Engine_BnkMeshLoader.SetExistsProbe(path => _filesystem.TryGetFile(path, out _));
		Refii.Textures.Engine_BnkTextureLoader.SetResolver(ReadAsset);

		_catalogue.AddRange(_filesystem.EnumerateAllFiles()
			.Where(f => f.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			.Select(f => f.VirtualPath)
			.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

		BuildScene();

		string requested = OS.GetEnvironment("REFII_MODEL");
		if (requested.Length == 0)
		{
			requested = ModelPath;
		}

		if (requested.Length != 0)
		{
			// An exact file-name match wins. Asking for WC_Crucible_Room_Large and getting
			// WC_Crucible_Room_Large_Door_Solid — which sorts earlier and does contain the
			// string — is the obvious trap here, and it silently shows the wrong model.
			int found = _catalogue.FindIndex(path => string.Equals(
				Path.GetFileNameWithoutExtension(path), requested, StringComparison.OrdinalIgnoreCase));

			if (found < 0)
			{
				found = _catalogue.FindIndex(
					path => path.Contains(requested, StringComparison.OrdinalIgnoreCase));
			}
			_index = found >= 0 ? found : 0;
			if (found < 0)
			{
				GD.PrintErr($"[viewer] no model matching '{requested}'; showing the first instead.");
			}
		}

		Show(_index);
	}

	private byte[]? ReadAsset(string virtualPath)
	{
		if (_filesystem is null || !_filesystem.TryGetFile(virtualPath, out BnkFile file))
		{
			return null;
		}

		try
		{
			return file.ReadAllBytes();
		}
		catch (Exception)
		{
			return null;
		}
	}

	private void BuildScene()
	{
		_instance = new MeshInstance3D { Name = "Model" };
		AddChild(_instance);

		_camera = new Camera3D { Name = "Camera", Current = true, Far = 100000.0f };
		AddChild(_camera);

		// Two lights, not one. A single directional light leaves every surface facing away
		// from it pure black, which reads as missing geometry when it is only unlit; the
		// dim fill from behind means nothing is ever fully dark and shape stays readable.
		var key = new DirectionalLight3D { Name = "Key", LightEnergy = 1.2f };
		key.LookAtFromPosition(new Vector3(3.0f, 5.0f, 4.0f), Vector3.Zero, Vector3.Up);
		AddChild(key);

		var fill = new DirectionalLight3D { Name = "Fill", LightEnergy = 0.45f };
		fill.LookAtFromPosition(new Vector3(-4.0f, 2.0f, -3.0f), Vector3.Zero, Vector3.Up);
		AddChild(fill);

		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.11f, 0.12f, 0.15f),
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.45f, 0.47f, 0.52f),
			AmbientLightEnergy = 0.7f,
		};
		AddChild(new WorldEnvironment { Name = "World", Environment = environment });

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
	}

	private void Show(int index)
	{
		if (_filesystem is null || _catalogue.Count == 0)
		{
			GD.PrintErr("[viewer] no models available.");
			GetTree().Quit(1);
			return;
		}

		_index = ((index % _catalogue.Count) + _catalogue.Count) % _catalogue.Count;
		string path = _catalogue[_index];

		if (!_filesystem.TryGetFile(path, out BnkFile file))
		{
			GD.PrintErr($"[viewer] vanished from the index: {path}");
			return;
		}

		ArrayMesh? mesh = null;
		string error = "unreadable";
		try
		{
			mesh = Engine_MdlMesh.TryCreateMesh(file.ReadAllBytes(), path, out error, out _);
		}
		catch (Exception exception)
		{
			error = exception.Message;
		}

		_instance.Mesh = mesh;

		// The flip lives on the transform. See Engine_MdlPlacement for why it must not be
		// baked into the vertices.
		_instance.Transform = Engine_MdlPlacement.Handedness;

		if (mesh is null)
		{
			_caption.Text = $"[{_index + 1}/{_catalogue.Count}] {path}\nFAILED: {error}";
			GD.Print($"[viewer] {path}: {error}");
			return;
		}

		ApplyDisplayMode(mesh);

		Aabb bounds = mesh.GetAabb();
		_target = Engine_MdlPlacement.HandednessBasis * bounds.GetCenter();

		// Frame on the diagonal, not the largest axis: a long thin model framed by its
		// longest side alone still leaves the other two clipped when it is turned.
		float radius = Math.Max(bounds.Size.Length() * 0.5f, 0.001f);
		_distance = radius / MathF.Tan(Mathf.DegToRad(_camera.Fov) * 0.5f) * 1.4f;

		string zoom = OS.GetEnvironment("REFII_MODEL_ZOOM");
		if (zoom.Length != 0 && float.TryParse(zoom, out float factor) && factor > 0.0f)
		{
			_distance *= factor;
		}
		_camera.Near = Math.Max(_distance * 0.001f, 0.01f);

		long vertices = 0;
		for (int s = 0; s < mesh.GetSurfaceCount(); ++s)
		{
			vertices += mesh.SurfaceGetArrayLen(s);
		}

		_caption.Text =
			$"[{_index + 1}/{_catalogue.Count}] {path}   ({_mode})\n" +
			$"{mesh.GetSurfaceCount()} surfaces  {vertices:N0} vertices  " +
			$"extent {bounds.Size.X:F2} x {bounds.Size.Y:F2} x {bounds.Size.Z:F2}";

		GD.Print($"[viewer] {path}: {mesh.GetSurfaceCount()} surfaces, {vertices:N0} vertices, " +
			$"extent {bounds.Size.X:F2}x{bounds.Size.Y:F2}x{bounds.Size.Z:F2}");

		// Name the source .tex each surface uses, with its mip level count. Tracking an
		// artefact back from "the arches look wrong" to a file is otherwise guesswork, and
		// the Godot material has already forgotten where its textures came from.
		byte[] raw = file.ReadAllBytes();
		if (Engine_MdlData.TryParseInfo(raw, path, out MdlInfo info, out _) &&
			Engine_MdlGeometry.TryBuild(raw, info, out List<MdlGeometry> geometry))
		{
			int slot = 0;
			foreach (MdlGeometry geo in Engine_MdlMerge.ByMaterial(geometry))
			{
				GD.Print($"[viewer]   surface {slot}: albedo={DescribeTex(geo.Material?.Diffuse)}");
				GD.Print($"[viewer]              normal={DescribeTex(geo.Material?.Normal)}");
				++slot;
			}
		}

		UpdateCamera();
	}

	/// <summary>
	/// How the model is shaded. Lighting and texture decode fail in ways that look alike —
	/// a speckled surface is equally consistent with a corrupt albedo and with a normal map
	/// whose channels are wrong — so being able to strip the shading back is what separates
	/// them. Set with <c>REFII_MAT_MODE</c> or cycled with the <c>M</c> key.
	/// </summary>
	private enum DisplayMode
	{
		/// <summary>Everything the material declares.</summary>
		Full,

		/// <summary>Lit, but with normal mapping off — isolates the normal map.</summary>
		NoNormal,

		/// <summary>Unshaded albedo — shows the diffuse texture with no lighting at all.</summary>
		Albedo,
	}

	private DisplayMode _mode = DisplayMode.Full;

	private void ApplyDisplayMode(ArrayMesh mesh)
	{
		for (int surface = 0; surface < mesh.GetSurfaceCount(); ++surface)
		{
			if (mesh.SurfaceGetMaterial(surface) is not StandardMaterial3D source)
			{
				continue;
			}

			// Override on the INSTANCE, never on the mesh's own material: the material is
			// shared through Engine_MdlMaterial's cache, so editing it would silently change
			// every other model using the same texture set.
			if (_mode == DisplayMode.Full)
			{
				_instance.SetSurfaceOverrideMaterial(surface, null);
				continue;
			}

			var copy = (StandardMaterial3D)source.Duplicate();
			copy.NormalEnabled = false;

			if (_mode == DisplayMode.Albedo)
			{
				copy.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
			}

			_instance.SetSurfaceOverrideMaterial(surface, copy);
		}
	}

	/// <summary>Names a texture and says how many mip levels it ships, which is what
	/// decides whether it gets a synthesised tail.</summary>
	private string DescribeTex(string? name)
	{
		if (string.IsNullOrWhiteSpace(name) || _filesystem is null)
		{
			return "-";
		}

		string virtualPath = name!.Replace('\\', '/').TrimStart('/');
		byte[]? bytes = ReadAsset(virtualPath);
		if (bytes is null)
		{
			return $"{virtualPath} (missing)";
		}

		if (!Refii.Textures.Engine_TexData.TryDecode(
				bytes, out Refii.Textures.TexSurface probe, out string probeError))
		{
			return $"{virtualPath} ({probeError})";
		}

		int full = Refii.Textures.Engine_TexImage.FullChainLength(probe.Width, probe.Height);
		string tail = probe.MipLevels > 1 && probe.MipLevels < full ? "  <-- TAIL" : string.Empty;
		return $"{virtualPath} {probe.Width}x{probe.Height} {probe.Format} " +
			$"{probe.MipLevels}/{full}{tail}";
	}

	private void UpdateCamera()
	{
		var offset = new Vector3(
			MathF.Cos(_pitch) * MathF.Sin(_yaw),
			MathF.Sin(_pitch),
			MathF.Cos(_pitch) * MathF.Cos(_yaw)) * _distance;

		_camera.Position = _target + offset;
		_camera.LookAt(_target, Vector3.Up);
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton button)
		{
			switch (button.ButtonIndex)
			{
				case MouseButton.Left:
					_orbiting = button.Pressed;
					return;
				case MouseButton.WheelUp when button.Pressed:
					_distance *= 0.9f;
					UpdateCamera();
					return;
				case MouseButton.WheelDown when button.Pressed:
					_distance *= 1.1f;
					UpdateCamera();
					return;
			}
		}

		if (@event is InputEventMouseMotion motion && _orbiting)
		{
			_yaw -= motion.Relative.X * 0.01f;
			_pitch = Math.Clamp(_pitch + (motion.Relative.Y * 0.01f), -1.5f, 1.5f);
			UpdateCamera();
			return;
		}

		if (@event is not InputEventKey { Pressed: true } key)
		{
			return;
		}

		switch (key.Keycode)
		{
			case Key.N:
				Show(_index + 1);
				break;
			case Key.P:
				Show(_index - 1);
				break;
			case Key.M:
				_mode = (DisplayMode)(((int)_mode + 1) % 3);
				GD.Print($"[viewer] display mode: {_mode}");
				Show(_index);
				break;
			case Key.W:
				GetViewport().DebugDraw = GetViewport().DebugDraw == Viewport.DebugDrawEnum.Wireframe
					? Viewport.DebugDrawEnum.Disabled
					: Viewport.DebugDrawEnum.Wireframe;
				break;
			case Key.Escape:
				GetTree().Quit(0);
				break;
		}
	}

	public override void _Process(double delta)
	{
		if (_shotPath.Length == 0)
		{
			return;
		}

		// A screenshot taken on the first frame catches an empty viewport: textures resolve
		// and materials compile over the following frames.
		if (++_framesRendered < ShotWarmupFrames)
		{
			return;
		}

		Image image = GetViewport().GetTexture().GetImage();
		string? parent = Path.GetDirectoryName(_shotPath);
		if (!string.IsNullOrEmpty(parent))
		{
			Directory.CreateDirectory(parent!);
		}

		Error result = image.SavePng(_shotPath);
		GD.Print(result == Error.Ok
			? $"[viewer] wrote {_shotPath}"
			: $"[viewer] could not write {_shotPath}: {result}");

		GetTree().Quit(result == Error.Ok ? 0 : 1);
	}

	public override void _ExitTree()
	{
		_filesystem?.Dispose();
		_filesystem = null;
	}
}
