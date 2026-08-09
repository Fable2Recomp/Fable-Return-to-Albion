using System.Linq;
using Godot;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Host;
using Refii.Launcher.Tools;

namespace Refii.Game;

/// <summary>
/// Renders Fable 2's own UI, in Godot, inside the game window.
///
/// This is the feasibility probe for the whole plan. The UI is the first thing the game
/// puts on screen and the first thing a player touches, and it is built from the title's
/// real <c>.bgf</c> presentation data — so if Godot can stand it up from the shipped
/// assets while the recompiled guest runs beside it, the "Godot renders, the guest
/// simulates" split is credible. If it cannot, that is worth knowing before any more is
/// built on top.
///
/// It deliberately reuses <see cref="UiScreenViewer"/> rather than reimplementing the
/// loader. That class already does the real work — resolve game data, load the bitmap
/// fonts and the Babel string database, build the slide graph through Engine_UiSceneBuilder,
/// aim the camera — and it is the same code the 52-screen export is verified against.
/// A parallel implementation here would drift from the thing we actually test.
///
/// It is built at boot but stays HIDDEN until the startup movies are done. Building it
/// early is deliberate: standing up the slide graph costs a visible fraction of a second
/// and would hitch exactly at the logo-to-title cut if it were deferred to that moment.
/// Building it behind the logos spends that cost where nothing is watching.
///
/// WHAT THIS DOES NOT DO: the guest does not drive it. Which screen appears is chosen by
/// the host, not by the title's UI state, so this proves the RENDERING half only. Wiring
/// the guest's slide state to it is the next step and a much larger one.
/// </summary>
public partial class GameUi : Node
{
	/// <summary>
	/// Screen to show before the guest says otherwise. <c>REFII_UI_SCREEN</c> overrides,
	/// as in the viewer.
	///
	/// The title screen, because that is what the guest puts up first — once it reports a
	/// front end, <see cref="FollowGuest"/> takes over and this is only the starting
	/// point.
	/// </summary>
	[Export] public string Screen { get; set; } = "frontendstartupscreen";

	/// <summary>
	/// The presentation each of the guest's front-end screens is authored as.
	///
	/// Not a guess: the guest's own ctor hands these exact file names to its two screen
	/// objects — `"FrontEndStartupScreen.bgf"` to GUI+180 and `"FrontEndMainMenu.bgf"` to
	/// GUI+184 (static init at 0x8324A2B8 / 0x8324A2F8).
	/// </summary>
	private static string? PresentationFor(HostBridge.FrontEndScreen screen) => screen switch
	{
		HostBridge.FrontEndScreen.Title => "frontendstartupscreen",
		HostBridge.FrontEndScreen.MainMenu => "frontendmainmenu",
		_ => null,
	};

	private UiScreenViewer? _viewer;

	/// <summary>True once <see cref="Reveal"/> has put the UI on screen.</summary>
	public bool IsRevealed { get; private set; }

	/// <summary>Whether the screen stood up at all.</summary>
	public bool IsBuilt => _viewer is not null;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PushWarning("[game-ui] no game data; UI not shown.");
			return;
		}

		_viewer = new UiScreenViewer
		{
			Name = "UiScreenViewer",
			GameDataRoot = dataRoot,
			StartScreen = Screen,

			// Framing camera OFF: the title's own camera aiming is what we are testing.
			// A framing camera would fit the geometry to the viewport and hide exactly the
			// placement errors this probe exists to surface.
			UseFramingCamera = false,
		};

		// Hidden, not absent: the node runs so the slides, fonts and strings load, but
		// nothing is drawn until the boot sequence hands over.
		_viewer.Visible = false;

		AddChild(_viewer);
		_showing = Screen;
		GD.Print($"[game-ui] built '{Screen}' from {dataRoot} (hidden until the logos end)");
	}

	/// <summary>
	/// Shows whichever presentation the guest currently has active.
	///
	/// This is the join the whole plan turns on: the guest owns which screen is up, Godot
	/// owns the pixels. Idempotent — rebuilding a screen graph is not free, so a repeated
	/// report of the same screen does nothing.
	/// </summary>
	public void FollowGuest(HostBridge.FrontEndScreen screen, int sceneSlide)
	{
		string? presentation = PresentationFor(screen);
		if (presentation is null || _viewer is null)
		{
			return;
		}

		// The guest's own per-element slides — a button on GetFocus, a card on Selected.
		// Compared as a whole rather than element by element: one widget changing state is
		// a different screen and has to be rebuilt either way.
		// Ordinal-keyed, because the guest's depth-first walk is byte-for-byte the order
		// the elements appear in the .bgf — verified 77/77 on the title screen. The name
		// hash is ambiguous (77 elements share 47 names there) and using it forced every
		// widget with a repeated name onto one state.
		System.Collections.Generic.List<HostBridge.UiElement>? live = HostBridge.GetUiElements();
		int[]? slides = null;
		string?[]? texts = null;
		if (live is { Count: > 0 })
		{
			slides = new int[live.Count];
			texts = new string?[live.Count];
			for (int i = 0; i < live.Count; ++i)
			{
				slides[i] = live[i].Slide;
				texts[i] = live[i].Text;
			}
		}

		bool same = presentation == _showing && _showingSlides is not null &&
			slides is not null && slides.Length == _showingSlides.Length &&
			slides.SequenceEqual(_showingSlides);
		if (same)
		{
			return;
		}

		// Live opacity, keyed the same way. This is the attribute the screen's Lua uses to
		// hide things it does not want drawn.
		float?[]? opacity = null;
		if (slides is not null)
		{
			HostBridge.LiveAttribute[,]? attributes = HostBridge.GetUiAttributes(OpacityIds);
			if (attributes is not null)
			{
				opacity = new float?[System.Math.Min(attributes.GetLength(0), slides.Length)];
				for (int i = 0; i < opacity.Length; ++i)
				{
					opacity[i] = attributes[i, 0].Present ? attributes[i, 0].AsFloat : null;
				}
			}
		}

		_viewer.LiveOpacity = opacity;
		_viewer.LiveSlides = slides;
		_viewer.LiveText = texts;
		if (!_viewer.ShowByName(presentation, sceneSlide))
		{
			GD.PushWarning($"[game-ui] the guest is on '{screen}' but '{presentation}' " +
				"is not among the shipped presentations");
			return;
		}

		if (presentation != _showing)
		{
			GD.Print($"[game-ui] following the guest onto '{presentation}' " +
				$"({slides?.Length ?? 0} live elements, " +
				$"{(_viewer.CurrentScreen?.IsLive == true ? "applied" : "NOT applied")})");
		}

		_showing = presentation;
		_showingSlide = sceneSlide;
		_showingSlides = slides;
	}

	/// <summary>
	/// The attributes we read live. sdbm of the name, which is the id the runtime stores —
	/// computed once rather than per frame.
	/// </summary>
	private static readonly uint[] OpacityIds =
	{
		Engine_GameHash.Sdbm("opacity") & Engine_GameHash.AttributeIdMask,
	};

	private string? _showing;
	private int _showingSlide;
	private int[]? _showingSlides;
	private bool _countsLogged;
	private bool _opacityLogged;

	/// <summary>Puts the UI on screen. Idempotent.</summary>
	public void Reveal()
	{
		if (IsRevealed || _viewer is null)
		{
			return;
		}

		IsRevealed = true;
		_viewer.Visible = true;
		// _showing, not Screen: the export is the STARTING presentation and never changes,
		// so naming it here reported the title screen long after the guest had moved on and
		// read as the front end bouncing back.
		GD.Print($"[game-ui] revealed '{_showing ?? Screen}'");
	}

	/// <summary>Takes the UI back off screen — a movie playing over the front end.</summary>
	public void Conceal()
	{
		if (!IsRevealed || _viewer is null)
		{
			return;
		}

		IsRevealed = false;
		_viewer.Visible = false;
	}
}
