using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Local co-op on split screen: each local player's view of the one world, side by side.
/// </summary>
/// <remarks>
/// <para>THE GUEST STATES BOTH CAMERAS. With the host's <c>coop_split_screen</c> switch on, local
/// player two's Player gets a camera manager of their own, ticked straight after player one's and
/// driven by player two's pad (src/hooks/src/coop_hero_hooks.cpp); the bridge reads each player's
/// live block (<c>RefiiHostGetPlayerCamera</c>). Player one's view follows <see cref="GameWorld"/>'s
/// camera, which stays the window's camera and the one every single-camera system reads; player
/// two's follows their own stated camera on the bodies' clock and rule, as player one's does.</para>
/// <para>TWO SUBVIEWPORTS OVER THE ONE WORLD. Godot renders a viewport through exactly one camera,
/// so each view is a <see cref="SubViewport"/> sharing the window's <see cref="World3D"/>, with its
/// own exposure and tone compositor. While split, the window's own 3D pass is disabled so the world
/// is drawn twice, not three times.</para>
/// <para>EACH HALF KEEPS THE GUEST'S HORIZONTAL FIELD OF VIEW. The guest states a horizontal fov
/// for a 4:3 screen; a half of a widescreen is tall and narrow, so the views keep the stated
/// horizontal angle (<see cref="Camera3D.KeepAspectEnum.Width"/>) rather than the 4:3-derived
/// vertical one the full window uses.</para>
/// <para>THE GUEST IS TOLD ONLY WHAT IS DRAWN. <see cref="CoopSession"/> states split screen to the
/// guest (<c>CoopSplitScreen:1</c>) only when this can draw it — a second guest camera with one
/// drawn view would leave player two walking off the screen. Both renderers draw a view per player:
/// the classic one keeps a register block per view and player two's camera selects its block by
/// <see cref="Refii.Models.Engine_ClassicViews.SecondViewLayer"/> (Engine_ClassicViews keeps the
/// split cameras' cull masks).</para>
/// </remarks>
public partial class GameSplitView : CanvasLayer
{
	/// <summary>The player's choice between split and shared screen for local co-op.</summary>
	public const string SwitchKey = "coop_split_screen";

	/// <summary>Split screen, unless the player has chosen a shared screen.</summary>
	public static bool Wanted => Refii.Core.Engine_Switches.Flag(SwitchKey, true);

	/// <summary>Whether the screen is split right now.</summary>
	public static bool Active { get; private set; }

	private readonly SubViewport?[] _views = new SubViewport?[2];
	private readonly Camera3D?[] _cameras = new Camera3D?[2];
	private Control? _frame;
	private readonly Refii.Core.Engine_TickTimeline _secondLine = new();
	private bool _reported;

	public override void _Ready()
	{
		// Over the window's world and under the UI (its CanvasLayer is 5).
		Layer = 1;
	}

	public override void _ExitTree()
	{
		Collapse();
	}

	/// <summary>
	/// Called by <see cref="GameWorld"/> once its own camera is placed for the frame: splits or
	/// rejoins the screen, and places both views.
	/// </summary>
	public void Follow(Camera3D first)
	{
		HostBridge.GuestCamera? second = CoopSession.LocalCoop && Wanted
			? HostBridge.GetPlayerCamera(2)
			: null;
		if (second is not { IsDefault: false } stated ||
			!GameWorld.TryStatedCamera(stated, out Transform3D secondStated, out _))
		{
			Collapse();
			return;
		}

		if (!Active)
		{
			Open();
		}

		Vector2 size = GetViewport().GetVisibleRect().Size;
		var half = new Vector2I(Mathf.Max(1, (int)size.X / 2), Mathf.Max(1, (int)size.Y));
		foreach (SubViewport? view in _views)
		{
			if (view is not null && view.Size != half)
			{
				view.Size = half;
			}
		}

		// Player one: the window camera's placement, at the stated horizontal angle.
		if (_cameras[0] is { } one)
		{
			one.GlobalTransform = first.GlobalTransform;
			one.Fov = HorizontalFromVertical(first.Fov);
		}

		// Player two: their own stated camera, on the same clock and rule as player one's.
		if (_cameras[1] is { } two)
		{
			float horizontal = Mathf.RadToDeg(stated.HorizontalFov);
			uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
			Transform3D drawn = secondStated;
			float drawnFov = horizontal;
			float teleport = Refii.Core.Engine_TickTimeline.TeleportMetres;
			if (stated.Flag || _secondLine.Empty ||
				_secondLine.Newest.Origin.DistanceSquaredTo(secondStated.Origin) > teleport * teleport)
			{
				_secondLine.Reset(tick, secondStated, horizontal);
			}
			else
			{
				_secondLine.Observe(tick, secondStated, horizontal);
				drawn = _secondLine.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec,
					Refii.Entities.Engine_DrawnFrame.DrawTick);
				drawnFov = _secondLine.DrawnScalar;
			}

			two.GlobalTransform = drawn;
			if (float.IsFinite(drawnFov) && drawnFov >= 1f && drawnFov <= 179f)
			{
				two.Fov = drawnFov;
			}
		}

		if (!_reported)
		{
			_reported = true;
			GD.Print($"[split-view] split: {half.X}x{half.Y} per player");
		}
	}

	/// <summary>
	/// The horizontal angle the guest stated, from the vertical one the window camera carries:
	/// <c>VerticalFovDegrees</c> is <c>2·atan(0.75·tan(h/2))</c>.
	/// </summary>
	private static float HorizontalFromVertical(float verticalDegrees) =>
		Mathf.RadToDeg(2f * Mathf.Atan(Mathf.Tan(Mathf.DegToRad(verticalDegrees) * 0.5f) / 0.75f));

	private void Open()
	{
		Viewport window = GetViewport();
		World3D world = window.World3D;

		_frame = new HBoxContainer
		{
			Name = "SplitViews",
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		_frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		((HBoxContainer)_frame).AddThemeConstantOverride("separation", 0);
		AddChild(_frame);

		for (int player = 0; player < 2; ++player)
		{
			var view = new SubViewport
			{
				Name = player == 0 ? "PlayerOneView" : "PlayerTwoView",
				World3D = world,
				OwnWorld3D = false,
				TransparentBg = false,
				HandleInputLocally = false,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
				Msaa3D = window.Msaa3D,
				ScreenSpaceAA = window.ScreenSpaceAA,
				UseTaa = window.UseTaa,
				UseDebanding = window.UseDebanding,
				Scaling3DMode = window.Scaling3DMode,
				Scaling3DScale = window.Scaling3DScale,
				PositionalShadowAtlasSize = window.PositionalShadowAtlasSize,
				PositionalShadowAtlas16Bits = window.PositionalShadowAtlas16Bits,
				MeshLodThreshold = window.MeshLodThreshold,
			};
			for (int quadrant = 0; quadrant < 4; ++quadrant)
			{
				view.SetPositionalShadowAtlasQuadrantSubdiv(quadrant, window.GetPositionalShadowAtlasQuadrantSubdiv(quadrant));
			}

			var camera = new Camera3D
			{
				Name = player == 0 ? "PlayerOneCamera" : "PlayerTwoCamera",
				Near = 0.05f,
				Far = 20_000f,
				KeepAspect = Camera3D.KeepAspectEnum.Width,
				Attributes = Refii.Lighting.Engine_LevelLighting.BuildCameraAttributes(),
				Compositor = Refii.Lighting.Engine_LevelLighting.BuildCompositor(player),
			};
			camera.Current = true;
			view.AddChild(camera);
			Refii.Lighting.Engine_DepthPrepassMarker.Attach(camera);

			var container = new SubViewportContainer
			{
				Name = player == 0 ? "PlayerOne" : "PlayerTwo",
				Stretch = true,
				SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
				SizeFlagsVertical = Control.SizeFlags.ExpandFill,
				MouseFilter = Control.MouseFilterEnum.Ignore,
			};
			container.AddChild(view);
			_frame.AddChild(container);

			_views[player] = view;
			_cameras[player] = camera;
		}

		window.Disable3D = true;
		Active = true;
		Refii.Core.Engine_Views.SetSplit(_views[0], _views[1], _cameras[1]);
		_reported = false;
		GD.Print("[split-view] local co-op split screen: a view per player");
	}

	private void Collapse()
	{
		if (!Active && _frame is null)
		{
			return;
		}

		if (IsInsideTree())
		{
			GetViewport().Disable3D = false;
		}

		if (Active)
		{
			Refii.Core.Engine_Views.SetSplit(null, null, null);
		}

		_frame?.QueueFree();
		_frame = null;
		for (int player = 0; player < 2; ++player)
		{
			_views[player] = null;
			_cameras[player] = null;
		}

		if (Active)
		{
			GD.Print("[split-view] shared screen again");
		}

		Active = false;
	}
}
