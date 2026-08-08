using System;
using Godot;

namespace Refii.Launcher.Tools;

/// <summary>
/// Loads a CONVERTED <c>.tscn</c> and renders it, to prove the exporter's output stands
/// on its own — no bank streaming, no format readers, nothing but the scene file.
///
/// A headless load proves only that the file parses. Whether it draws is a separate
/// question, so this captures pixels: set <c>REFII_UI_SHOT</c> to a PNG path.
/// </summary>
public partial class UiSceneVerify : Node3D
{
	/// <summary>Converted scene to open. <c>REFII_VERIFY_SCENE</c> overrides.</summary>
	[Export] public string ScenePath { get; set; } = "res://assets/gui/screens/menubox.tscn";

	public override void _Ready()
	{
		string path = System.Environment.GetEnvironmentVariable("REFII_VERIFY_SCENE") ?? ScenePath;

		var packed = ResourceLoader.Load<PackedScene>(path);
		if (packed is null)
		{
			GD.PrintErr($"verify: could not load {path}");
			GetTree().Quit(1);
			return;
		}

		Node instance = packed.Instantiate();
		AddChild(instance);

		int meshes = 0, cameras = 0, current = 0;
		void Count(Node n)
		{
			if (n is MeshInstance3D) meshes++;
			if (n is Camera3D cam) { cameras++; if (cam.Current) current++; }
			foreach (Node c in n.GetChildren()) Count(c);
		}
		Count(instance);
		GD.Print($"verify: {path}\n  {meshes} meshes, {cameras} cameras ({current} current)");

		Capture();
	}

	private async void Capture()
	{
		string? target = System.Environment.GetEnvironmentVariable("REFII_UI_SHOT");
		if (string.IsNullOrEmpty(target))
			return;

		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		GD.Print(image.SavePng(target!) == Error.Ok ? $"wrote {target}" : "screenshot failed");
		GetTree().Quit();
	}
}
