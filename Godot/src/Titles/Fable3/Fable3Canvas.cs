namespace Refii.Titles.Fable3;

using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// A 720-high UI canvas for Fable 3's owned surfaces: a SubViewport with its own world and an
/// orthographic camera fitted by <see cref="Engine_UiSceneBuilder.FitCanvasCamera"/>, so the shared
/// Node3D widgets (the menu view, the world sprites, the achievement toast) lay out in the
/// console's units whatever the window is. The theme is the title's fonts, strings and GUI art,
/// rebuilt when the guest's culture changes.
/// </summary>
public abstract partial class Fable3Canvas : SubViewportContainer
{
	public const float CanvasHeight = Engine_UiSceneBuilder.CanvasHeight;
	public const float PixelsPerUnit = Engine_UiSceneBuilder.PixelsPerUnit;

	protected SubViewport CanvasViewport { get; private set; } = null!;
	protected Node3D Root { get; private set; } = null!;
	protected Camera3D CanvasCamera { get; private set; } = null!;

	/// <summary>The title's theme for the current culture; null until the strings and fonts are up.</summary>
	public new Engine_UiTheme? Theme { get; private set; }

	private Vector2 _fittedTo;
	private int _cameraReports;
	private string _culture = string.Empty;

	public override void _Ready()
	{
		// Anchors AND offsets, as Fable3Splash: anchors alone left the container 0×0 under the
		// CanvasLayer, its stretched viewport 2×2, and every 3D widget rendering into four
		// transparent pixels (r9 canvas probe: "container size (0, 0) … readback 2x2: NOTHING
		// RENDERED") — the 2D text drawn on the container from the window rect still showed.
		SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Stretch = true;

		CanvasViewport = new SubViewport
		{
			Name = "Canvas",
			TransparentBg = true,
			OwnWorld3D = true,
			HandleInputLocally = false,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			Msaa3D = Viewport.Msaa.Disabled,
		};
		AddChild(CanvasViewport);

		Root = new Node3D { Name = "Root" };
		CanvasViewport.AddChild(Root);

		CanvasCamera = new Camera3D
		{
			Name = "CanvasCamera",
			Projection = Camera3D.ProjectionType.Orthogonal,
			Position = new Vector3(0f, 0f, 100f),
			Near = 0.1f,
			Far = 500f,
			Current = true,
		};
		Root.AddChild(CanvasCamera);
		Build();
	}

	/// <summary>Stands the owned widgets under <see cref="Root"/>; called once from <c>_Ready</c>.</summary>
	protected abstract void Build();

	/// <summary>One frame, after the camera and theme are current.</summary>
	protected abstract void Frame(double delta);

	/// <summary>The theme changed (first load or a culture switch).</summary>
	protected new virtual void ThemeChanged()
	{
	}

	/// <summary>Canvas units per window pixel at the current window height.</summary>
	protected float UnitsPerPixel
	{
		get
		{
			Vector2 size = GetViewportRect().Size;
			return size.Y > 0f ? CanvasHeight / PixelsPerUnit / size.Y : 1f;
		}
	}

	public override void _Process(double delta)
	{
		// The camera stays CURRENT and is fitted to the render target's own size, as Fable 2's UI
		// camera is (GameUi.DriveUiCamera): the aspect that matters is the viewport's, and a lost
		// current camera renders nothing. Either event is said once.
		if (CanvasViewport.GetCamera3D() != CanvasCamera)
		{
			if (_cameraReports++ < 3)
			{
				GD.Print($"[fable3-canvas] {Name}: the canvas camera was '{CanvasViewport.GetCamera3D()?.Name ?? "none"}' — made current");
			}

			CanvasCamera.MakeCurrent();
		}

		// The container covers the window: its parent is a CanvasLayer, not a Control, so nothing
		// else resizes it when the window does.
		Vector2 window = GetViewportRect().Size;
		if (Size != window && window.X > 0f && window.Y > 0f)
		{
			Position = Vector2.Zero;
			Size = window;
		}

		Vector2 size = CanvasViewport.Size;
		if (size != _fittedTo && size.X > 0f && size.Y > 0f)
		{
			_fittedTo = size;
			Engine_UiSceneBuilder.FitCanvasCamera(CanvasCamera, size);
		}

		// Rebuilt when the culture switches and when the strings arrive after the first frame.
		if (Theme is null || _culture != Engine_GameCulture.Current || !ReferenceEquals(Theme.Strings, Fable3Strings.Current))
		{
			_culture = Engine_GameCulture.Current;
			Theme = new Engine_UiTheme
			{
				Fonts = Fable3Fonts.For(_culture),
				Strings = Fable3Strings.Current,
				Textures = Fable3Art.Load,
			};
			ThemeChanged();
		}

		Frame(delta);
	}
}
