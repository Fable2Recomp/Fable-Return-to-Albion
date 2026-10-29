using Godot;
using Refii.Game;

namespace Refii.Titles.Fable2;

using Refii.Launcher.Tools;

/// <summary>
/// Proves the front end actually COMPOSITES over the 3D world, which the screen viewer
/// cannot tell you.
///
/// <see cref="UiScreenViewer"/> renders the presentation into the main viewport with nothing
/// else in it, so it answers "does the screen build and draw" and stops there. The game puts
/// the same screen in a <see cref="SubViewport"/> on a <see cref="CanvasLayer"/> over a live
/// world, and every failure mode that matters lives in that arrangement rather than in the
/// screen: a sub-viewport sized zero, a container that never got laid out, a transparent
/// background that comes out opaque, a camera that is current in the wrong viewport.
///
/// So this stands up the REAL <see cref="GameUi"/> — not a copy of it — over a coloured 3D
/// backdrop, reveals it, and writes a PNG. Anything other than the front end drawn over that
/// backdrop is the composite being wrong, and the backdrop colour says which way: all
/// backdrop means the UI is absent, all grey means the UI viewport is opaque and covering it.
///
/// No guest. <see cref="GameUi._Ready"/> stands the screen up from the banks on its own; only
/// <see cref="GameUi.FollowGuest"/> needs a running guest, and which screen is up is not what
/// this is testing.
/// </summary>
public partial class UiCompositeCheck : Node3D
{
	private GameUi? _ui;
	private int _frames;

	public override void _Ready()
	{
		// A backdrop that cannot be confused with Godot's clear colour. If this survives
		// into the shot, the world is rendering and the UI is composing over it.
		var backdrop = new MeshInstance3D
		{
			Name = "Backdrop",
			Mesh = new QuadMesh { Size = new Vector2(40f, 24f) },
			MaterialOverride = new StandardMaterial3D
			{
				AlbedoColor = new Color(0.15f, 0.30f, 0.55f),
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			},
			Position = new Vector3(0f, 0f, -12f),
		};
		AddChild(backdrop);

		var camera = new Camera3D { Name = "WorldCamera", Near = 0.05f, Far = 1000f };
		AddChild(camera);
		camera.MakeCurrent();

		_ui = new GameUi { Name = "GameUi" };
		AddChild(_ui);
	}

	public override void _Process(double delta)
	{
		// Layout has to run before the sub-viewport has a size, so reveal is deferred a few
		// frames rather than done in _Ready — which is also what the real boot sequence does,
		// for a different reason.
		if (_ui is null || ++_frames < 5)
		{
			return;
		}

		if (!_ui.IsRevealed)
		{
			_ui.Reveal();
			Report();
			return;
		}

		if (_frames >= 12)
		{
			Capture();
		}
	}

	private void Report()
	{
		SubViewport? viewport = _ui!.GetNodeOrNull<SubViewport>("UiLayer/UiView/UiViewport");
		var container = _ui.GetNodeOrNull<SubViewportContainer>("UiLayer/UiView");

		GD.Print($"[ui-composite] built={_ui.IsBuilt} revealed={_ui.IsRevealed}");
		GD.Print(container is null
			? "[ui-composite] NO CONTAINER — GameUi did not build its layer"
			: $"[ui-composite] container visible={container.Visible} size={container.Size} " +
			  $"stretch={container.Stretch}");

		if (viewport is null)
		{
			GD.Print("[ui-composite] NO SUB-VIEWPORT");
			return;
		}

		GD.Print($"[ui-composite] viewport size={viewport.Size} update={viewport.RenderTargetUpdateMode} " +
			$"ownWorld={viewport.OwnWorld3D} transparent={viewport.TransparentBg}");

		// The camera question, asked of the sub-viewport itself rather than of the node tree:
		// a camera can exist, be marked current, and still not be the one this viewport uses.
		Camera3D? current = viewport.GetCamera3D();
		GD.Print(current is null
			? "[ui-composite] NO CURRENT CAMERA in the sub-viewport — this renders nothing"
			: $"[ui-composite] current camera '{current.Name}' proj={current.Projection} " +
			  $"size={current.Size} pos={current.GlobalPosition}");
	}

	private async void Capture()
	{
		string target = System.Environment.GetEnvironmentVariable("REFII_UI_SHOT")
			?? "/tmp/ui_composite.png";

		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		GD.Print(image.SavePng(target) == Error.Ok ? $"wrote {target}" : "screenshot failed");
		GetTree().Quit();
	}
}
