using System.Linq;
using Godot;
using Refii.Core;
using Refii.Ui;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Renders Fable 2's own front end, in Godot, over the world.
///
/// The guest owns WHICH screen is up and what state every widget is in; Godot owns the
/// pixels. The screens are the title's real <c>.bgf</c> presentation data, stood up through
/// <see cref="Engine_UiPresentationHost"/> — the same code the viewer uses, so what is
/// checked against the original game is what ships.
///
/// IT RENDERS INTO ITS OWN VIEWPORT, and that is the whole point of this class rather than
/// an implementation detail. A Gameface screen is a 3D scene: it carries cameras, lights and
/// quads authored around the origin on a ~720-unit canvas. Built into the game's world it
/// shares one viewport with the world the guest is walking through, and Godot renders a
/// viewport through exactly ONE current camera — so the last camera to declare itself won
/// and the other half of the game vanished. The front end won, because it re-declares its
/// camera every time a widget changes state, and it kept winning through
/// <see cref="Conceal"/>: hiding the meshes leaves the camera current, so the world stayed
/// unrendered even with the menu off screen.
///
/// A SubViewport with its own World3D is the fix. The front end gets a camera it cannot
/// lose, the world keeps the window's camera, and the two compose the way the game composes
/// them — UI over scene, on a transparent background.
///
/// It is built at boot but stays HIDDEN until the startup movies are done. Building it
/// early is deliberate: standing up the slide graph costs a visible fraction of a second
/// and would hitch exactly at the logo-to-title cut if it were deferred to that moment.
/// Building it behind the logos spends that cost where nothing is watching.
/// </summary>
public partial class GameUi : Node
{
	/// <summary>
	/// Screen to show before the guest says otherwise.
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

	/// <summary>
	/// True when a bank name is one of the front end's own screens.
	///
	/// Asked of <see cref="PresentationFor"/> rather than of a list written out again, so the
	/// two cannot disagree about what the front end consists of.
	/// </summary>
	private static bool IsFrontEndScreen(string bank)
	{
		foreach (HostBridge.FrontEndScreen screen in
			System.Enum.GetValues<HostBridge.FrontEndScreen>())
		{
			if (PresentationFor(screen) is { } name &&
				string.Equals(bank, name, System.StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	private Engine_UiPresentationHost? _presentation;
	private SubViewport? _viewport;
	private SubViewportContainer? _container;

	/// <summary>
	/// The main menu's model and its view — GODOT's, not the guest's.
	///
	/// This is the half of the front end that stopped being a mirror. The guest's menu has no
	/// C++ item list and no C++ highlight to copy: items, order, nesting and the selection all
	/// live in shipped Lua, so there was never anything on the guest side to read. What we
	/// mirrored instead was the RESULT — the per-element slides the Lua had already written —
	/// which meant Godot could draw the menu and could not move it.
	///
	/// Owning the model inverts that. The list is ours, the highlight is ours, the navigation
	/// timing is ours, and the guest is told the outcome by name. The art stays the game's.
	/// </summary>
	private Engine_FrontEndMenu? _menu;

	private Engine_FrontEndMenuView? _menuView;

	/// <summary>
	/// The gender card "New Game" opens.
	///
	/// The guest models this as a SUBSTATE of the same screen object rather than as a different
	/// screen — `screen+24` goes to 2 and the menu's button handler stops answering the list's
	/// codes — so the front end does not change hands here, only the input does.
	///
	/// THE ROWS COME DOWN ANYWAY, and that part is OURS. The file puts every element of
	/// `ExpandableMenu` at z 0 and the card's active pose at z -134, so the rows cut straight
	/// across the raised card; the six root slides all START the menu off-canvas at x -800 and
	/// tween it in, so none of them is a parking spot the original uses for this. Whatever the
	/// shipped Lua does to get the list out of the way, it is not in the file and has not been
	/// read — so this hides the rows and says so, rather than reproducing a mechanism it does
	/// not have. The panel's own <c>Background</c> tint still dims what is left behind it.
	/// </summary>
	private Engine_FrontEndCardChoice? _cardView;

	/// <summary>
	/// The Load Game card fan.
	///
	/// The last piece of the front end that was still a mirror. The guest enumerates the saves
	/// and builds each card's text — both of those are calls only it can make — but the CARDS
	/// were still the guest's own six components moving under its own cursor, with Godot merely
	/// proxying an A press back at it. Now the enumeration is read and Godot draws and moves the
	/// fan, the same trade the row menu and the gender card already made.
	/// </summary>
	private Engine_FrontEndSaveCards? _saveCardView;

	/// <summary>
	/// The loading screen, on its own layer above this one.
	///
	/// Polled every frame rather than driven from the level-change contract, because the guest
	/// owns both whether one is up and how far it has faded — see <see cref="GameLoadingScreen"/>.
	/// </summary>
	private GameLoadingScreen? _loading;

	/// <summary>
	/// Whether the display takes the game's WIDE layout.
	///
	/// Sampled from the viewport rather than assumed, because the answer is the display's and
	/// the game genuinely ships both. It is re-read as the window changes, so resizing from
	/// 4:3 to 16:9 moves the HUD the way the original does.
	/// </summary>
	private bool _isWidescreen = true;

	/// <summary>
	/// True while anything modal is over the menu — the gender card, the load fan, or the
	/// warning box.
	///
	/// One place, because "is the row menu allowed on screen" is asked from more than one and
	/// the answers were drifting apart: the rebuild path knew only about the gender card, so
	/// every panel added after it shared the screen with the rows for a frame and then kept
	/// them.
	/// </summary>
	private bool ModalPanelUp =>
		_cardView is { Visible: true } ||
		_saveCardView is { Visible: true } ||
		_warningView is { Visible: true };

	/// <summary>
	/// The refusal box, cloned from the shipped `dialog` page.
	///
	/// Modal in the guest's sense: it parks the screen in substate 1 and only an A press
	/// clears it. Ours is modal the same way — while it is up the menu takes no input.
	/// </summary>
	private Engine_FrontEndWarning? _warningView;

	/// <summary>The name a menu entry asked us to dispatch, latched until something takes it.</summary>
	public string? PendingAction { get; private set; }

	/// <summary>True once <see cref="Reveal"/> has put the UI on screen.</summary>
	public bool IsRevealed { get; private set; }

	/// <summary>Whether the screen stood up at all.</summary>
	public bool IsBuilt => _presentation is not null;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PushWarning("[game-ui] no game data; UI not shown.");
			return;
		}

		// Layer 5: over the world, under the videos on layer 10. A movie covers the front
		// end, which is what the boot sequence and every later cutscene want.
		var layer = new CanvasLayer { Name = "UiLayer", Layer = 5 };
		AddChild(layer);

		_container = new SubViewportContainer
		{
			Name = "UiView",
			// The container drives the SubViewport's size, so the front end is rendered at
			// the window's real resolution rather than upscaled from a fixed buffer.
			Stretch = true,
			// The front end is driven by the GUEST's input, read through GameInput. Letting
			// the sub-viewport take Godot's GUI input as well would give a second, host-side
			// route into it that the guest knows nothing about.
			MouseFilter = Control.MouseFilterEnum.Ignore,
			Visible = false,
		};
		_container.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(_container);

		_viewport = new SubViewport
		{
			Name = "UiViewport",
			// The two that make this an overlay rather than a second game: its own world, so
			// the front end's cameras and lights cannot reach the world the guest is in, and
			// a transparent background so the world shows through everywhere the screen does
			// not draw.
			OwnWorld3D = true,
			TransparentBg = true,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
			GuiDisableInput = true,
		};
		_container.AddChild(_viewport);

		_presentation = new Engine_UiPresentationHost
		{
			Name = "Presentation",
			GameDataRoot = dataRoot,
			StartScreen = Screen,

			// Framing camera OFF: the title's own camera aiming is what we are testing.
			// A framing camera would fit the geometry to the viewport and hide exactly the
			// placement errors this exists to surface.
			UseFramingCamera = false,
		};

		// The host's own _Ready runs on AddChild, so the screen is already standing up by the
		// time this returns and the theme has a presentation to catalogue.
		_viewport.AddChild(_presentation);
		_showing = Screen;

		// If Godot is STARTING on the menu, own it immediately rather than waiting to be told.
		//
		// FollowGuest is the other way in, and it is the one that runs during a real boot. This
		// one matters because the guest is not the only thing that decides which screen is up
		// any more — a harness with no guest at all, and eventually a pause or options screen
		// Godot opens on its own, both arrive here. Gating menu ownership on a guest report
		// would mean the menu only exists when something else says so, which is exactly the
		// mirroring being taken out.
		if (string.Equals(Screen, PresentationFor(HostBridge.FrontEndScreen.MainMenu),
				System.StringComparison.OrdinalIgnoreCase))
		{
			AdoptMenu();
		}

		// THE LOADING SCREEN, above this layer and below the videos — see GameLoadingScreen for
		// the whole stack. It is owned here rather than by GameWindow because this class already
		// owns the layer ordering and the texture loader it needs; GameWindow only ever LOGGED
		// the art the guest picked, which is why nothing has been drawing it.
		_loading = new GameLoadingScreen
		{
			Name = "LoadingScreen",
			Resolver = path => _presentation?.LoadTexture(path),
			// THE GAME'S OWN FONT AND STRINGS, from the same library every Gameface screen
			// uses. The loading screen is not a presentation, but its tip and its banner are
			// the title's text and must be drawn in the title's letters — and its tip arrives
			// as a Babel KEY, so it needs the string database to say anything at all.
			Fonts = _presentation?.Fonts,
			Strings = _presentation?.Strings,
		};

		AddChild(_loading);

		GD.Print($"[game-ui] built '{Screen}' from {dataRoot} " +
			"in its own viewport (hidden until the logos end)");
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
		if (_presentation is null)
		{
			return;
		}

		string? presentation = PresentationFor(screen);
		if (presentation is null)
		{
			// THE FRONT END IS GONE — take it down rather than leaving it mounted.
			//
			// This used to return here and nothing else ever freed the built screen, so the
			// last front end stayed in the viewport for the rest of the session. It did not
			// stay hidden: GameWindow conceals the layer only while the guest has NO live UI,
			// so the first in-game presentation to stand up revealed the layer again with a
			// dead `frontendmainmenu` still in it — its frame, its dialog box and its cursor
			// over the running game.
			//
			// Gated on the VTABLE, not on `screen`. Once the game has taken over,
			// `0x83496B08` holds a base-only object whose +180/+184/+188 are not screen
			// pointers at all, so `screen` reads None from fields that mean nothing — and
			// None also occurs for a beat while the front-end GUI is up between screens.
			// FrontEndGuiIsUp is the test that tells those two apart.
			if (!HostBridge.FrontEndGuiIsUp)
			{
				LeaveFrontEnd();
			}

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
		bool[]? visible = null;
		if (live is { Count: > 0 })
		{
			slides = new int[live.Count];
			texts = new string?[live.Count];
			visible = new bool[live.Count];
			for (int i = 0; i < live.Count; ++i)
			{
				slides[i] = live[i].Slide;
				texts[i] = live[i].Text;
				visible[i] = live[i].IsGloballyActive;
			}
		}

		// VISIBILITY counts as a change too. The guest can hide or reveal a widget without any
		// slide moving — that is most of what a screen's Lua does — and comparing slides alone
		// meant those never triggered a rebuild, so the screen kept whatever it was last built
		// with.
		bool same = presentation == _showing && _showingSlides is not null &&
			slides is not null && slides.Length == _showingSlides.Length &&
			slides.SequenceEqual(_showingSlides) &&
			_showingVisible is not null && visible is not null &&
			visible.Length == _showingVisible.Length &&
			visible.SequenceEqual(_showingVisible);
		if (same)
		{
			return;
		}

		// Live opacity, keyed the same way.
		//
		// NOT a visibility lever, which is what it was assumed to be. sub_8229E358 uses opacity
		// only to choose the transparent bucket — a fully transparent element is still
		// traversed and still submitted. Hiding is globalactive's job, above. This is here so
		// a faded widget looks faded, nothing more.
		float?[]? opacity = null;
		Engine_UiScreen.LiveTint?[]? tint = null;
		if (slides is not null)
		{
			HostBridge.LiveAttribute[,]? attributes = HostBridge.GetUiAttributes(OpacityIds);
			if (attributes is not null)
			{
				int count = System.Math.Min(attributes.GetLength(0), slides.Length);
				opacity = new float?[count];
				tint = new Engine_UiScreen.LiveTint?[count];
				for (int i = 0; i < count; ++i)
				{
					opacity[i] = attributes[i, 0].Present ? attributes[i, 0].AsFloat : null;
					tint[i] = ReadTint(attributes, i);
				}
			}
		}

		_presentation.LiveOpacity = opacity;
		_presentation.LiveTint = tint;
		_presentation.LiveVisible = visible;
		_presentation.LiveSlides = slides;
		_presentation.LiveText = texts;
		// The front end needs this as much as the HUD: its own screens lay elements out by
		// writing position attributes, so without it they stack where the file put them.
		_presentation.LiveTransform =
			slides is null ? null : ReadLiveTransform(0u, slides.Length);
		if (!_presentation.ShowByName(presentation, sceneSlide))
		{
			GD.PushWarning($"[game-ui] the guest is on '{screen}' but '{presentation}' " +
				"is not among the shipped presentations");
			return;
		}

		// GODOT OWNS THE MENU from here on.
		//
		// This runs after EVERY ShowByName, not once, because ShowByName rebuilds the whole
		// node tree — the guest's twelve row components come back with it each time, and a
		// screen carrying both their menu and ours is the failure this prevents.
		if (screen == HostBridge.FrontEndScreen.MainMenu)
		{
			AdoptMenu();
		}
		else if (_menuView is not null)
		{
			// Off the main menu — the title, or a screen we do not draw. The view is kept
			// rather than freed so the highlight survives a trip out to the title and back,
			// which is what the original does: leaving the menu does not reset the selection.
			_menuView.Visible = false;

			// The card goes with it. Unlike the highlight it does NOT survive: it is a question
			// that was asked on a screen that is no longer up, and leaving it armed would put
			// it back over whatever comes next.
			if (_cardView is not null)
			{
				_cardView.Visible = false;
			}
		}

		if (presentation != _showing)
		{
			GD.Print($"[game-ui] following the guest onto '{presentation}' " +
				$"({slides?.Length ?? 0} live elements, " +
				$"{(_presentation.CurrentScreen?.IsLive == true ? "applied" : "NOT applied")})");
		}

		// THE ORDINAL JOIN, checked rather than believed.
		//
		// Slide, text and opacity are all applied by position — the guest's depth-first walk
		// is taken to be the same sequence as the .bgf's element order. That was verified
		// 77/77 on the TITLE screen and never on this one, which has 3812 elements. If the two
		// walks diverge anywhere, every element after the divergence receives another
		// element's state, and the result is not an obviously broken screen: it is a mostly
		// correct one with stray widgets on it.
		//
		// Element type is the cheap invariant to compare — the guest holds a kind byte per
		// element with the same numbering as BgfElementType, and it carries no live state, so
		// any mismatch is a JOIN failure and nothing else.
		if (presentation != _showing && live is { Count: > 0 } &&
			_presentation.CurrentScreen is { } built)
		{
			int compared = System.Math.Min(built.Nodes.Length, live.Count);
			int diverged = -1;
			int mismatches = 0;
			for (int i = 0; i < compared; ++i)
			{
				if ((int)built.Nodes[i].Type == live[i].Type)
				{
					continue;
				}

				++mismatches;
				if (diverged < 0)
				{
					diverged = i;
				}
			}

			GD.Print(diverged < 0
				? $"[game-ui] ordinal join OK: {compared} elements, types agree end to end"
				: $"[game-ui] ORDINAL JOIN BROKEN: {mismatches} of {compared} element types " +
				  $"disagree, first at index {diverged} " +
				  $"(file says {built.Nodes[diverged].Type}, guest says {live[diverged].Type}) " +
				  "— every element past that point is wearing another element's state");

			if (built.Nodes.Length != live.Count)
			{
				GD.Print($"[game-ui] element COUNT differs: file {built.Nodes.Length}, " +
					$"guest {live.Count} — the join cannot be positional at all");
			}
		}

		// How much of the screen the guest actually TOLD us about, reported once per screen.
		//
		// Opacity is the lever the screens' own Lua uses to hide what it does not want drawn,
		// so an element the guest never reported keeps whatever the file authored — and the
		// shipped .bgf is full of placeholder labels ("fdg", "argarg arg arg") that only stay
		// off screen because something set them transparent. "The screen is applied" does not
		// distinguish "the guest hid nothing" from "we read nothing", and those need different
		// fixes.
		if (presentation != _showing && opacity is not null)
		{
			int reported = 0;
			int hidden = 0;
			foreach (float? value in opacity)
			{
				if (value is not { } alpha)
				{
					continue;
				}

				++reported;
				if (alpha <= 0.001f)
				{
					++hidden;
				}
			}

			GD.Print($"[game-ui] opacity: {reported} of {slides?.Length ?? 0} elements " +
				$"reported one, {hidden} of those are fully transparent");

			// Did following globalactive actually change anything? The file-derived visibility
			// and the guest's own answer should DISAGREE — that disagreement is the entire
			// reason for reading it. If they match element for element, then the runtime never
			// moved from what the .bgf authored and the stray widgets have some other cause,
			// which is worth knowing before hunting further.
			if (visible is not null && _presentation?.CurrentScreen is { } current)
			{
				int guestHides = 0;
				int fileHides = 0;
				int disagree = 0;
				int compared = System.Math.Min(current.Nodes.Length, visible.Length);
				for (int i = 0; i < compared; ++i)
				{
					// Nodes[i].Visible has already had the live value applied, so the file's
					// answer is recovered from the document's own slide state instead.
					bool fromFile = current.FileVisible(i);
					if (!visible[i])
					{
						++guestHides;
					}

					if (!fromFile)
					{
						++fileHides;
					}

					if (visible[i] != fromFile)
					{
						++disagree;
					}
				}

				GD.Print($"[game-ui] visibility: guest hides {guestHides} of {compared}, " +
					$"the file would hide {fileHides}, they disagree on {disagree}");
			}

			ReportPages(slides, visible);
		}

		// DOES THE GUEST AGREE WE SHOULD BE DRAWING THIS?
		//
		// The front end still picks its screen BY NAME, from the guest's reported active
		// screen, while the in-game path asks the guest's own submission gate. Those should be
		// one mechanism — the guest parks front-end presentations on exactly the same bytes,
		// and `scrollingbackground` is one it parks — but switching this path over blind is how
		// the last two regressions happened, so the disagreement gets MEASURED first.
		//
		// A steady "agrees" across a boot is the evidence needed to delete the by-name path. A
		// disagreement names the moment the two models diverge, which is the thing that would
		// otherwise blank the front end.
		if (presentation != _showing)
		{
			foreach (HostBridge.Presentation p in HostBridge.GetPresentations())
			{
				if (!p.Is(presentation + ".bgf"))
				{
					continue;
				}

				GD.Print(p.IsShowing
					? $"[game-ui] gate agrees: '{presentation}' is SHOWING"
					: $"[game-ui] GATE DISAGREES: drawing '{presentation}' by name, but the " +
					  $"guest has it hidden (active={p.ActiveElements}" +
					  $"{(p.IsFrozen ? " FROZEN" : string.Empty)}" +
					  $"{(p.IsSuppressed ? " PARKED" : string.Empty)}) — the by-name path " +
					  "cannot be replaced by the gate until this stops happening");
			}
		}

		_showing = presentation;
		_showingSlides = slides;
		_showingVisible = visible;
	}

	/// <summary>
	/// Reports the state of each PAGE of the front-end screen, once per screen change.
	///
	/// `frontendmainmenu` is not one screen — it is seven pages plus a dialog, all in one file
	/// under a single `Layer` element, and the shipped Lua names them exactly that way:
	/// <c>SelectTitleItem</c> reaches them as `Scene.Layer.newgame`, `Scene.Layer.loadgame`,
	/// `Scene.Layer.options`, `Scene.Layer.multiplayer` and `Scene.Layer.debug`. Only one of
	/// them should be on screen at a time, and the load-game page has been drawing across the
	/// menu.
	///
	/// Three things could put it there and they need opposite fixes, so this prints all three
	/// side by side rather than any one of them:
	///
	/// <list type="bullet">
	/// <item>the guest's <c>globalactive</c> for the page — if it is off and we drew it anyway,
	/// the fault is that something overrides visibility downstream;</item>
	/// <item>the guest's live SLIDE for the page — `loadgame` slide 1 parks its cards at
	/// y -598 and slide 2 fans them out, so a page reporting 0 (never set) falls back to the
	/// artist's authored layout, which is the fanned one;</item>
	/// <item>whether the guest stated a live POSITION for it at all — if it did and we still
	/// drew the page in the wrong place, the transform read is what is broken.</item>
	/// </list>
	///
	/// Direct children of `Layer` rather than a hardcoded page list, so a page nobody thought of
	/// still shows up.
	/// </summary>
	private void ReportPages(int[]? slides, bool[]? visible)
	{
		if (_presentation?.CurrentScreen is not { } screen || slides is null || visible is null)
		{
			return;
		}

		float?[]?[]? transform = ReadLiveTransform(0u, slides.Length);

		// Node to ordinal, once. A page's subtree is walked by following Children, and looking
		// each one up by scanning Nodes would be quadratic over 3812 elements.
		var ordinals = new System.Collections.Generic.Dictionary<UiNode, int>(screen.Nodes.Length);
		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			ordinals[screen.Nodes[i]] = i;
		}

		// How much of a page the guest has switched on. A page that is off should be off all the
		// way down — the engine skips an inactive element AND its whole subtree.
		(int Live, int Total) Count(UiNode node)
		{
			int live = 0;
			int total = 0;

			if (ordinals.TryGetValue(node, out int ordinal) && ordinal < visible.Length)
			{
				++total;
				if (visible[ordinal])
				{
					++live;
				}
			}

			foreach (UiNode child in node.Children)
			{
				(int childLive, int childTotal) = Count(child);
				live += childLive;
				total += childTotal;
			}

			return (live, total);
		}

		var report = new System.Text.StringBuilder("[game-ui] pages under 'Layer':");
		int pages = 0;

		for (int i = 0; i < screen.Nodes.Length && i < slides.Length; ++i)
		{
			if (screen.Nodes[i].Parent is not { } parent ||
				!string.Equals(parent.Name, "Layer", System.StringComparison.Ordinal))
			{
				continue;
			}

			++pages;
			(int live, int total) = Count(screen.Nodes[i]);

			string moved = transform is not null && i < transform.Length && transform[i] is not null
				? "stated"
				: "-";

			report.Append($"\n    {screen.Nodes[i].Name,-20} slide={slides[i],-3} " +
				$"active={(visible[i] ? "on " : "OFF")} live-transform={moved,-6} " +
				$"subtree {live}/{total} on");
		}

		if (pages > 0)
		{
			GD.Print(report.ToString());
		}
	}

	/// <summary>
	/// Takes the whole front end down, because the guest no longer has one.
	///
	/// Idempotent, and it has to be: this is reached from a per-frame path, and the guest sits
	/// with no front end for the entire game.
	///
	/// The MENU MODEL survives. Only the view goes — the highlight is the player's place in a
	/// list, and the original does not reset it either. The card is different and is reset on
	/// the way in rather than kept; see <see cref="OpenScreen"/>.
	/// </summary>
	private void LeaveFrontEnd()
	{
		if (_showing is null)
		{
			return;
		}

		GD.Print($"[game-ui] the guest has left the front end — taking '{_showing}' down");

		// FREED, not hidden — and that is the point rather than tidiness. Both views hold
		// references INTO the tree about to go away: the row view keeps the shipped
		// `MenuItemButton` it duplicates its slots from, and both were sourced through the
		// theme's catalogue. Hiding them would leave those pointing at freed nodes.
		//
		// Nothing is lost by it. AdoptMenu re-sources both from the next build, and the model
		// they draw — which is where the highlight lives — is not touched here.
		if (_menuView is not null)
		{
			_menuView.QueueFree();
			_menuView = null;
		}

		if (_cardView is not null)
		{
			_cardView.QueueFree();
			_cardView = null;
		}

		// THE LOAD FAN GOES WITH THEM, and forgetting it here is what left six save cards
		// hanging over the running game.
		//
		// Choosing a save takes the guest out of the front end IMMEDIATELY — the log goes
		// "load fan: save 0 chosen" straight into "the guest has no live UI; hiding the layer" —
		// so the fan's own tidy-up in `DriveLoadCarousel` never gets another frame to run: that
		// path is behind an `IsRevealed` gate which is now false. The layer being hidden masked
		// it until the HUD came up and revealed the layer again, with the stale fan still
		// visible on top of it.
		//
		// Freed rather than hidden for the reason the two above are: it holds clones sourced
		// from the tree that is about to go away. `AdoptSaveCards` rebuilds it from the next
		// front end.
		if (_saveCardView is not null)
		{
			_saveCardView.QueueFree();
			_saveCardView = null;
		}

		if (_warningView is not null)
		{
			_warningView.QueueFree();
			_warningView = null;
		}

		// The extras go too — `scrollingbackground` is a front-end presentation we draw behind
		// the by-name screen, and it is only ever retired by the IN-GAME sampler noticing the
		// guest stopped showing it, which is a sample up to half a second later. Ownership
		// changes hands here, so it is released here.
		RetireDrawn("the guest left the front end");

		_presentation?.ClearScreen();

		_showing = null;
		_showingSlides = null;
		_showingVisible = null;
	}

	/// <summary>
	/// Takes the menu over from the presentation: the guest's rows go away, ours go up.
	///
	/// TWO STEPS, and the order matters. The shipped row components are cloned for their ART
	/// first, then the group they live in is hidden — reversed, we would be duplicating a
	/// node that had just been made invisible, and every row would inherit that.
	///
	/// The group may be ABSENT rather than merely hidden. Engine_UiSceneBuilder drops an
	/// inactive element's whole subtree instead of hiding it, reproducing the guest's own
	/// walk, so on a slide where the menu is not up there is no "Buttons" node at all. That
	/// is not a failure: the view falls back to text-only rows, which is honest about having
	/// no art to source rather than inventing a tab.
	/// </summary>
	private void AdoptMenu()
	{
		if (_presentation?.Theme is not { } theme)
		{
			return;
		}

		// TWO TREES, TWO JOBS. The group to HIDE has to be the live one — that is the copy on
		// screen. The row to CLONE has to be the file's, because the guest keeps its own menu
		// components faded out and a template taken from the live tree comes out invisible.
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? template = pattern is null
			? null
			: Engine_FrontEndMenuView.RowTemplateIn(pattern);

		if (_menuView is null)
		{
			// The MODEL outlives the view. The view is freed whenever the guest leaves the
			// front end, because it holds nodes belonging to the tree that goes with it; the
			// model holds the list and the highlight, which are the player's place in a menu
			// and survive a trip out to the title and back — as they do in the original.
			_menu ??= Engine_FrontEndMenu.BuildRoot();
			_menuView = new Engine_FrontEndMenuView
			{
				Name = "FrontEndMenu",
				Theme = theme,
				RowTemplate = template,
				// The shipped highlight art, if this slide carries it. UpdateCursorColor only
				// ever changes its opacity — the scripts never move it — so cloning it and
				// parking it on the pinned slot is what the original does. From the pattern
				// book for the same reason the row is: the guest's own copy carries whatever
				// opacity the guest last left it at.
				CursorTemplate = (_presentation?.Templates ?? theme).Art("MenuHighlight"),
			};

			// Parented to the HOST rather than to the built screen, because the built screen is
			// destroyed and recreated on every guest state change and the menu must outlive
			// that. The host's transform is identity and the builder gives the screen root an
			// identity transform too, so both sit in the same authored canvas space.
			// Non-null: the theme test at the top of this method only passes through a live
			// presentation. The `?.` reads above are for the TEMPLATES, which a host that never
			// finished loading may genuinely not have.
			_presentation!.AddChild(_menuView);
			_menuView.Bind(_menu);

			GD.Print($"[game-ui] Godot owns the main menu now — " +
				$"{_menu.Visible.Count} entries, " +
				$"row art {(template is null ? "NOT sourced (text-only rows)" : "cloned from the presentation")}, " +
				$"cursor {(_menuView.CursorTemplate is null ? "stand-in" : "shipped")}");
		}

		AdoptCardChoice(theme);
		AdoptSaveCards(theme);
		AdoptWarning(theme);

		// The guest's own rows, out of the way. Hidden rather than freed: the tree is rebuilt
		// from the file on the next state change anyway, and freeing a node the theme's
		// catalogue still points at would leave a dangling entry behind.
		if (group is not null)
		{
			group.Visible = false;
		}

		// NOT WHILE ANY MODAL PANEL IS UP, and the list of them has to be complete here.
		//
		// This runs after EVERY rebuild, and the guest keeps changing state under a front end it
		// no longer drives — so an unconditional show puts the rows back over whatever is in
		// front of them, a fraction of a second after they were hidden. It knew about the gender
		// card and not about the other two, which is why a screenshot of the load fan has a menu
		// row's label and tab art floating across it: the fan hid the rows, the next rebuild
		// brought them straight back.
		//
		// Kept as one test rather than three scattered assignments so a panel added later fails
		// visibly here rather than silently sharing the screen.
		_menuView.Visible = !ModalPanelUp;
		ApplyMenuAvailability();
	}

	/// <summary>
	/// Takes the gender card over from the presentation, the same way <see cref="AdoptMenu"/>
	/// takes the rows.
	///
	/// Built HERE rather than when the player first presses New Game, and that is not
	/// eagerness: the art can only be cloned while `frontendmainmenu` is standing up, and by
	/// the time the panel is wanted the screen may have been rebuilt out from under the
	/// catalogue. Building it behind the menu also spends the cost where nothing is watching,
	/// which is the same argument that puts the whole UI up behind the logos.
	///
	/// The shipped group is hidden on every rebuild for the same reason the rows are — with
	/// Godot drawing the cards, leaving the guest's two components in the tree is two panels on
	/// one screen. It is not merely tidiness that they are currently off screen: the `Intro`
	/// slide parks them at x -831 and 883, and a live slide from the guest would bring them
	/// straight back into the canvas.
	/// </summary>
	private void AdoptCardChoice(Engine_UiTheme theme)
	{
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndCardChoice.GroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndCardChoice.GroupName);

		if (_cardView is null)
		{
			// Cloned from the file, placed by the LIVE group's transform — the pattern book has
			// the art the artist drew, and the live tree is the authority on where on the canvas
			// that group actually sits.
			_cardView = Engine_FrontEndCardChoice.FromPresentation(pattern ?? group);
			if (group is not null)
			{
				_cardView.Transform = group.GlobalTransform;
			}

			_presentation!.AddChild(_cardView);

			GD.Print($"[game-ui] gender card ready — art " +
				$"{(_cardView.HasShippedArt ? "cloned from 'choosecard'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
		}

		// The LIVE group, re-hidden on every rebuild — including the first, which the factory
		// no longer does for us now that it is handed the pattern tree instead. The tree it
		// lives in is rebuilt from the file on every guest state change and comes back visible.
		if (group is not null)
		{
			group.Visible = false;
		}
	}

	/// <summary>
	/// Takes the Load Game fan over from the presentation, the same way
	/// <see cref="AdoptCardChoice"/> takes the gender card.
	///
	/// Built HERE rather than when the guest opens its carousel, and for a sharper reason than
	/// the gender card's: the card art can only be cloned while `frontendmainmenu` is standing
	/// up, and by the time substate 5 arrives the screen has been rebuilt several times over.
	/// Building it behind the menu also spends the cost where nothing is watching.
	///
	/// The shipped `loadgame` page is hidden on every rebuild for the reason the rows and the
	/// gender card are — with Godot drawing the fan, leaving the guest's six components in the
	/// tree is two fans on one screen. That they are currently parked at y -598 is not
	/// protection: that is the page's own closed slide, and a live slide brings them back.
	/// </summary>
	private void AdoptSaveCards(Engine_UiTheme theme)
	{
		Node3D? page = theme.ArtSourceNode(Engine_FrontEndSaveCards.PageName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndSaveCards.PageName);

		if (_saveCardView is null)
		{
			// Cloned from the FILE, placed by the LIVE page's transform — the pattern book has
			// the art the artist drew, and the live tree is the authority on where on the canvas
			// that page actually sits. Same split as the gender card, and for the same reason:
			// the guest leaves its own copy in whatever state its last slide put it.
			_saveCardView = Engine_FrontEndSaveCards.FromPresentation(pattern ?? page);
			_saveCardView.Theme = theme;
			if (page is not null)
			{
				_saveCardView.Transform = page.GlobalTransform;
			}

			_presentation!.AddChild(_saveCardView);

			GD.Print("[game-ui] load fan ready — art " +
				$"{(_saveCardView.HasShippedArt ? "cloned from 'loadgame'" : "NOT sourced (stand-ins)")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(page is null ? ", page ABSENT from this build" : string.Empty)}");
		}

		if (page is not null)
		{
			page.Visible = false;
		}
	}

	/// <summary>
	/// Adopts the shipped `dialog` group — the box every refusing guard puts up.
	///
	/// Same shape as the gender card above, and hidden the same way: with Godot owning the
	/// front end, leaving the guest's own group live in the tree would draw a second box under
	/// ours. It is a PAGE of `frontendmainmenu` rather than a presentation of its own, which is
	/// why it is adopted here rather than loaded.
	/// </summary>
	private void AdoptWarning(Engine_UiTheme theme)
	{
		Node3D? group = theme.ArtSourceNode(Engine_FrontEndWarning.GroupName);
		Node3D? pattern = _presentation?.Templates?.ArtSourceNode(Engine_FrontEndWarning.GroupName);

		if (_warningView is null)
		{
			_warningView = Engine_FrontEndWarning.FromPresentation(pattern ?? group);
			if (group is not null)
			{
				_warningView.Transform = group.GlobalTransform;
			}

			_presentation!.AddChild(_warningView);

			GD.Print("[game-ui] warning box ready — art " +
				$"{(_warningView.HasShippedArt ? "cloned from 'dialog'" : "NOT sourced")}" +
				$" ({(pattern is null ? "live tree" : "pattern book")})" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
		}

		if (group is not null)
		{
			group.Visible = false;
		}
	}

	/// <summary>
	/// Applies the guest's OWN spawn conditions to our list.
	///
	/// The one thing a host-authored menu genuinely cannot invent. "Load Game" exists only when
	/// signed in with at least one save and "Language" only above one installed language — the
	/// guest decides both, and an entry we offer that it will not honour is worse than a
	/// missing one, because it looks like the game is broken rather than like the option is
	/// unavailable.
	/// </summary>
	private void ApplyMenuAvailability()
	{
		if (_menu is null || HostBridge.GetFrontEndModel() is not { } model)
		{
			return;
		}

		// THE GUEST'S OWN COUNT, and the second attempt at getting it.
		//
		// The first passed a hardcoded 0; the second passed `model.EnumeratedSaves`, the vector
		// at the front-end screen's +84/+88. Both kept "Load Game" off the menu unconditionally,
		// and the second only looked right — that vector is written in exactly one place,
		// `sub_82443048` at 0x826E03CC, INSIDE the dispatcher's own "Load Game" branch. It is
		// empty until the entry has already been chosen, so it can never be the thing that
		// decides whether to offer it. The guest's spawn guard calls `sub_82443308` instead, and
		// so now do we, through the tick pump.
		//
		// The languages come from HERE, not from the model, because reading them reads guest
		// memory — and this is the one place that already knows a guest is up, having just got a
		// front-end model out of it.
		_menu.ApplyAvailability(model, HostBridge.GetSaveState(),
			languages: HostBridge.GetLanguages());
		_menuView?.Refresh();
	}

	/// <summary>
	/// Drives the Godot-owned menu from the Godot-owned input.
	///
	/// Per frame rather than event-driven because the auto-repeat curve is a TIMING model —
	/// the model wants the held direction and a delta and owns the rest of it. Reading discrete
	/// key events here instead would move the repeat timing into this class, which is the one
	/// piece of the feel that was read out of shipped Lua rather than chosen.
	/// </summary>
	public override void _Process(double delta)
	{
		// Visible as well as revealed. IsRevealed is the whole UI layer being on screen — it
		// is false through the boot movies — and Visible is this menu being the screen that is
		// up. Without the second test the highlight would keep moving under the title screen,
		// so a player holding down on the title would arrive at the menu with the selection
		// already somewhere else.
		// Before the menu gate, because this has to keep reporting once the front end is gone
		// — that is exactly the window the in-game UI lives in.
		// THE LOADING SCREEN FIRST, and outside every gate below it. The others return early on
		// `IsRevealed` or on there being no menu, and a loading screen has to work in exactly
		// those windows — it is up while the front end is coming down and before the HUD exists.
		_loading?.Poll();

		// WHICH LAYOUT THE DISPLAY WANTS, re-read as the window changes. Reported on the change
		// rather than every frame: a HUD that jumped between layouts and a HUD in the wrong one
		// look the same in a still, and only the transition says which.
		if (_viewport is not null)
		{
			bool wide = Engine_UiWidescreen.IsWidescreen(_viewport.Size);
			if (wide != _isWidescreen)
			{
				_isWidescreen = wide;
				GD.Print($"[game-ui] the display is now {(wide ? "WIDESCREEN" : "4:3")} " +
					$"({_viewport.Size.X}x{_viewport.Size.Y}) — the shipped " +
					$"{(wide ? "wide" : "standard")} layout applies");
			}
		}

		TracePresentations();

		// THE MOVING PARTS, every frame — see Engine_UiSceneBuilder.ApplyLiveTransform. This is
		// deliberately NOT inside TracePresentations, which samples a few times a second: the
		// guest animates these per frame, and a backdrop that scrolled at the sampling rate
		// would judder rather than move.
		AnimateDrawn();

		ScanWhenSettled();
		DriveTitle(delta);

		if (_menu is null || _menuView is null || !IsRevealed)
		{
			return;
		}

		// THE WARNING IS MODAL and therefore first of all, ahead of even the card. The guest's
		// box parks its screen in substate 1 and the only way out is the accept button — every
		// other input is dead while it stands. B clears it too, which is ours: the one-button
		// form has nothing else to press, and a box that could only be dismissed by a key we
		// might have mismapped is a deadlock.
		if (_warningView is { Visible: true })
		{
			// ACCEPT may PROCEED rather than merely dismiss. The two-button form is an offer,
			// not a dead end — "Continue without saving" goes on to the gender card — so the
			// box carries where accepting leads and null means it leads nowhere.
			if (GameInput.MenuAcceptPressed)
			{
				string? proceed = _warningView.AcceptOpens;
				_warningView.Visible = false;

				if (proceed is not null)
				{
					OpenScreen(proceed);
				}
				else
				{
					_menuView.Visible = true;
				}
			}
			else if (GameInput.MenuBackPressed && _warningView.AllowCancel)
			{
				// Cancel exists only on the two-button form, and the guest's B arm checks that
				// flag before doing anything at all — see AllowCancel. It returns the screen to
				// substate 0 without touching the presentation: back to the list, no card.
				_warningView.Visible = false;
				_menuView.Visible = true;
			}

			return;
		}

		// THE CARD OWNS THE INPUT WHILE IT IS UP, and it is tested BEFORE the menu's own
		// visibility. The card takes the rows off screen, so a gate that required a visible
		// menu first would hand the card no input at all and leave it stuck on the screen it
		// had just put up.
		if (_cardView is { Visible: true })
		{
			DriveCardChoice();
			return;
		}

		// THE LOAD FAN OWNS THE PRESS ONCE THE GUEST HAS OPENED IT, and — like the gender card
		// above — it is tested BEFORE the menu's own visibility rather than after.
		//
		// That ordering is the whole of a bug worth naming, because it is the same one twice. The
		// fan takes the rows off screen, so a gate requiring a visible menu first would hand it no
		// input at all and leave it stuck on the screen it had just put up. The earlier version of
		// this sat below that gate and was reachable only while the rows were still drawn.
		//
		// Choosing "Load Game" puts the guest's screen into substate 5 and it keeps receiving
		// input there, because our input mask covers only the root list. A run showed the
		// consequence exactly: its cursor walked 0..5 and back while every A press re-dispatched
		// "Load Game", because this class still believed the root list was up.
		if (DriveLoadCarousel(delta))
		{
			return;
		}

		if (!_menuView.Visible)
		{
			return;
		}

		// THE GAME'S OWN MOVEMENT SOUND, asked for the way its own menu asks.
		//
		// Godot owns the list now, so the guest never fires the slide sound it would have fired
		// for itself. Rather than reach into the audio system, the outcome is named: the
		// dispatcher's command ids 140 and 141 exist for exactly this and do nothing else but
		// fire "SE_GUI_SLIDE_MENU_UP" / "..._DOWN" on the front end's emitter. Only on a move
		// that actually happened — Navigate returns false while the auto-repeat is still
		// waiting, and a sound per frame held would be a buzz.
		if (_menuView.Navigate(GameInput.MenuDirection, delta))
		{
			HostBridge.PostFrontEndAction(
				GameInput.MenuDirection < 0 ? MenuSlideUpAction : MenuSlideDownAction);
		}

		if (GameInput.MenuBackPressed)
		{
			// A false return is "nothing left to back out of". On the front end's root menu
			// that is the guest's business, not ours — B there leaves for the title screen,
			// and the guest is already being fed the same B press through XInput.
			_menuView.Back();
		}

		if (!GameInput.MenuAcceptPressed)
		{
			return;
		}

		string? action = _menuView.Activate();

		// A REFUSAL, before either of the two outcomes below. The guest's New Game branch runs
		// its storage guards ahead of the gender card, so an entry can decline to open the very
		// screen it names — and the reason is a Babel tag, which is what the guest puts through
		// `dialog.Group.yousure`.
		if (_menu?.TakeWarning() is { } warning)
		{
			// The save-limit refusal is the guest's ONE two-button warning on this path
			// (`r6 = 1` at 0x826E01A4) and it labels its accept button GUI_DEVICE_REMOVED_CONTINUE
			// — a generic "Continue", reused. Every other storage refusal passes `r6 = 0` with
			// GUI_OK. Carried explicitly rather than derived, because it is the guest's flag.
			bool limit = warning == Engine_FrontEndMenu.SaveLimitWarning;
			string button = limit ? "GUI_DEVICE_REMOVED_CONTINUE" : "GUI_OK";

			if (_warningView is not null)
			{
				_warningView.Show(_presentation?.Theme, warning, button, twoButton: limit);

				// BOTH go on to the card, not just the two-button one. Every New Game refusal
				// leaves `+116 = 0, +120 = 2` on the screen, and the accept arm routes that pair
				// to "choosecard" whatever the button count — see AcceptOpens. The one-button
				// form differs only in having no cancel.
				_warningView.AcceptOpens = Engine_FrontEndMenu.HeroCardScreen;
				_menuView.Visible = false;
			}

			GD.Print($"[game-ui] New Game refused — {warning} " +
				$"(saves {_menu.Saves.Count}/{_menu.Saves.Limit}, " +
				$"signed in {_menu.Saves.SignedIn}) — " +
				$"{(_warningView is null ? "NO BOX to show it in" : _warningView.LastReport)}");
			return;
		}

		// A SCREEN the entry opens, before the null test below — an entry that opens one also
		// returns null, and treating the two the same is what made "New Game" do nothing
		// visible while quietly starting a game.
		if (_menuView.TakeOpenedScreen() is { } screen)
		{
			OpenScreen(screen);
			return;
		}

		if (action is null)
		{
			// Expanded a submenu, collapsed one, or a host-only entry the guest knows nothing
			// about. All three are complete here — there is nothing to tell the guest.
			return;
		}

		Post(action, "menu");
	}

	/// <summary>
	/// One frame of the Load Game card fan. True when it consumed the input.
	///
	/// THE GUEST OPENS IT; GODOT DRAWS IT. That split is not a compromise — it is the only
	/// division the enumeration allows. Listing a profile's saves is `sub_82443308`, a live
	/// `XamContentCreateEnumerator`, and building each card's text is `sub_826E14F8`, which
	/// mounts every container and parses its `ChapterSave.bin`. Both are guest CALLS, both run
	/// inside the dispatcher's own "Load Game" branch, and neither is something a host can do
	/// for itself. So "Load Game" still dispatches by name, the guest still does the work, and
	/// what Godot takes over is the part that was never the guest's to begin with once the front
	/// end changed hands: the cards on screen and the cursor moving between them.
	///
	/// SUBSTATE 5 IS THE SIGNAL — `screen+24`, set at 0x826E07E0 once the branch has enumerated
	/// more than one save. A single save never reaches it: that path commits directly at
	/// 0x826E0470, which is why this returning false during a one-save load is correct rather
	/// than a gap.
	///
	/// B IS LEFT TO THE GUEST, as it was. Its own handler maps B in this substate to UNFREEZE,
	/// which puts `screen+24` back to 0 — and it is already being fed the press, since the input
	/// mask covers only the root list. Posting a second back-out would drive one transition
	/// twice; the fan comes down on the next frame when the substate is no longer 5.
	/// </summary>
	private bool DriveLoadCarousel(double delta)
	{
		if (HostBridge.GetFrontEndModel() is not { } model || model.MenuState != LoadCarouselState)
		{
			// The guest has left the carousel — by its own B, or because a save was committed.
			// Taking the fan down here rather than on our own B press is what keeps the two in
			// step whichever of them ended it.
			CloseSaveCards();
			return false;
		}

		if (_saveCardView is null)
		{
			return false;
		}

		// POPULATED ONCE PER OPENING, not per frame. The containers are filled before the
		// substate is set — `sub_826E14F8` runs at 0x826E0448 and `screen+24 = 5` at 0x826E07E0
		// — so by the time this first sees state 5 the list is complete and re-reading it every
		// frame would rebuild the glyph mesh for a string that cannot change.
		if (!_saveCardView.Visible)
		{
			System.Collections.Generic.List<HostBridge.SaveCard> cards =
				HostBridge.GetSaveCards(out int guestCursor);
			if (cards.Count == 0)
			{
				// STATE 5 WITH NOTHING TO DRAW IS A CONTRADICTION, and saying so is the point.
				// The guest only reaches this substate having enumerated two or more saves —
				// zero warns and one commits directly — so an empty list means OUR read is
				// wrong, never that there are no saves.
				//
				// It reports the model's own count beside ours because that is the comparison
				// that localises the fault. `EnumeratedSaves` is read from the same
				// `screen+84/+88` vector by a different function: if it is non-zero while this
				// list is empty, the defect is in `GetSaveCards`; if both are zero, the vector
				// really is empty and the substate is being reached some way the dispatcher
				// does not explain. Reporting neither is what let the first version of this say
				// "empty" once a frame for a whole run without narrowing anything — the fault
				// was a host-side liveness gate returning early, and the log could not have
				// distinguished that from a bad offset.
				//
				// Rate-limited to once per opening: it fired every frame before, which buried
				// the build lines above it.
				if (_loadFanEmpty.Add(model.MenuState))
				{
					GD.PushWarning("[game-ui] the guest is in its save carousel but the " +
						$"enumerated list read back empty — model says {model.EnumeratedSaves} " +
						$"save(s) at screen+84/+88, cursor {model.LoadCardCursor}. The fan " +
						"cannot be drawn.");
				}

				return true;
			}

			_loadFanEmpty.Clear();

			// THE PORTRAITS, taken from the guest rather than from the file.
			//
			// Every card in `frontendmainmenu.bgf` is authored with the same placeholder back,
			// and the guest stamps the real screenshot's texture HASH into each card's
			// `screenshot` element as it builds the list. Without this the fan draws six
			// identical blank cards, which is what a run showed.
			System.Collections.Generic.List<Texture2D?> portraits = ReadCardPortraits();

			var saves = new System.Collections.Generic.List<Engine_FrontEndSaveCards.Save>();
			for (int i = 0; i < cards.Count; ++i)
			{
				saves.Add(new Engine_FrontEndSaveCards.Save(
					cards[i].Description, i < portraits.Count ? portraits[i] : null));
			}

			OpenSaveCards(saves);
			GD.Print($"[game-ui] the guest opened the load fan with {cards.Count} save(s), " +
				$"its cursor at {guestCursor} — {_saveCardView.Describe()}");
		}

		if (_saveCardView.Navigate(GameInput.MenuHorizontal, delta))
		{
			GD.Print($"[game-ui] load fan moved — {_saveCardView.Describe()}");
		}

		if (GameInput.MenuAcceptPressed && _saveCardView.Activate() is var chosen && chosen >= 0)
		{
			// THE INDEX IS THE GUEST'S JOIN. It identifies a save by the address of its handle
			// slot — `Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i` and
			// `*(screen+148) + 40*i + 12` — so the index is what both sides already agree on,
			// and our fan is populated in that same enumeration order.
			uint queued = HostBridge.LoadSave(chosen);
			GD.Print($"[game-ui] load fan: save {chosen} chosen — posted (queue depth {queued})");
		}

		// Consumed either way. A press that is not A still belongs to the fan, and letting it
		// fall through would move the root list's highlight underneath the cards.
		return true;
	}

	/// <summary>
	/// Each save card's portrait, in card order, as the guest currently has it.
	///
	/// THREE STEPS, and each is a place this can honestly come back empty. The guest publishes
	/// a texture HASH per element; `Engine_TextureNameTable` inverts it to a path; the texture
	/// cache loads that path. A card whose quest has no screenshot, a hash the table does not
	/// know, or a texture the banks do not carry all end with a null — and a null keeps the
	/// artist's placeholder, which is the right picture for "no screenshot" and an honest one
	/// for the other two.
	///
	/// Read from the LIVE front-end presentation by ordinal, so the join is the same one slides
	/// and text use. It declines wholesale if the guest's element count disagrees with ours.
	/// </summary>
	private System.Collections.Generic.List<Texture2D?> ReadCardPortraits()
	{
		var portraits = new System.Collections.Generic.List<Texture2D?>();

		if (_presentation?.TextureNames is not { } names ||
			HostBridge.GetUiElements() is not { } elements || elements.Count == 0)
		{
			return portraits;
		}

		if (HostBridge.GetUiTextureHashes(0u, elements.Count)
			is not { } hashes)
		{
			return portraits;
		}

		// THE SIX PORTRAIT ELEMENTS, FOUND BY NAME HASH IN WALK ORDER.
		//
		// `frontendmainmenu` carries exactly six elements named `screenshot` and all six are the
		// load cards' — checked against the file rather than assumed, because matching a leaf
		// name is only safe when the name is unique to the thing being matched. Walk order is
		// card order, which is the same order the guest fills the save list in.
		uint screenshot = Engine_GameHash.Sdbm(Engine_FrontEndSaveCards.PortraitName);

		for (int i = 0; i < elements.Count && portraits.Count < Engine_FrontEndSaveCards.MaxCards; ++i)
		{
			if (elements[i].NameHash != screenshot)
			{
				continue;
			}

			Texture2D? texture = null;
			if (i < hashes.Length && hashes[i] != 0 &&
				names.TryResolve(hashes[i], out string path))
			{
				texture = _presentation.LoadTexture(path);
			}

			portraits.Add(texture);
		}

		return portraits;
	}

	/// <summary>
	/// Puts the fan up with the guest's own enumerated list, and takes the rows down.
	///
	/// The rows go for the reason the gender card's do — see <see cref="_cardView"/>: the file
	/// puts every menu element at z 0 and the cards reach z -100, so the list would cut straight
	/// across them, and no shipped slide parks the menu anywhere this could reuse.
	/// </summary>
	private void OpenSaveCards(
		System.Collections.Generic.List<Engine_FrontEndSaveCards.Save> saves)
	{
		if (_saveCardView is null)
		{
			return;
		}

		_saveCardView.SetSaves(saves);
		_saveCardView.Visible = true;

		if (_menuView is not null)
		{
			_menuView.Visible = false;
		}
	}

	/// <summary>
	/// Takes the fan down and gives the rows back. Idempotent — it runs on every frame the
	/// guest is not in substate 5, which is nearly all of them.
	/// </summary>
	private void CloseSaveCards()
	{
		if (_saveCardView is not { Visible: true })
		{
			return;
		}

		_saveCardView.Visible = false;

		// Not over the gender card, and not over the running game. The menu comes back only if
		// it is what the player would be looking at.
		if (_menuView is not null && _cardView is not { Visible: true } && !HasInGameUi)
		{
			_menuView.Visible = true;
		}

		GD.Print("[game-ui] the guest left its save carousel — the load fan is down");
	}

	/// <summary>
	/// `screen+24` while the guest's save carousel is up — set at 0x826E07E0 for two or more
	/// saves. The one-save case never reaches it; that path commits directly at 0x826E0470.
	/// </summary>
	private const uint LoadCarouselState = 5;

	/// <summary>
	/// Menu states already reported as "carousel up, list empty", so that warning fires once
	/// per opening rather than once per frame.
	///
	/// Cleared as soon as a non-empty list arrives, so a later opening that fails still speaks.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _loadFanEmpty = new();

	/// <summary>
	/// Sends one named action to the guest.
	///
	/// POSTED, not called. The name goes onto the guest's tick-thread queue and the guest
	/// performs it inside its own tick. Nothing here touches guest state, which is what keeps
	/// this side as safe as it has always been — the bridge still only reads and writes memory
	/// from Godot's thread, and the one place that makes a guest CALL is the guest's own thread.
	///
	/// A name the guest has no action for is dropped by the pump with a log line rather than
	/// silently, so an entry that does nothing says so.
	/// </summary>
	private void Post(string action, string from)
	{
		PendingAction = action;
		uint queued = HostBridge.PostFrontEndAction(action);
		GD.Print($"[game-ui] {from} activated '{action}' — posted to the guest tick pump " +
			$"(queue depth {queued})");
	}

	/// <summary>
	/// Opens a front-end screen a menu entry named.
	///
	/// An unknown name is reported rather than ignored: an entry that opens nothing looks
	/// exactly like an entry whose press was dropped, and those need different fixes.
	/// </summary>
	private void OpenScreen(string screen)
	{
		if (screen == Engine_FrontEndMenu.HeroCardScreen && _cardView is not null)
		{
			// Reset on the way IN, not on the way out. The guest re-enters through the same
			// `Intro` slide and substate 2 every time, whatever the player did last, so a
			// cancelled choice must not be waiting when the card is opened again.
			_cardView.Reset();
			_cardView.Visible = true;

			// The rows out of the way while the card is up — see the field's remarks for why
			// that is ours rather than the original's.
			if (_menuView is not null)
			{
				_menuView.Visible = false;
			}

			GD.Print($"[game-ui] the gender card is up — {_cardView.Describe()}");
			return;
		}

		GD.PushWarning($"[game-ui] menu entry asked for screen '{screen}', which nothing here " +
			"draws — the entry will appear to do nothing");
	}

	/// <summary>
	/// One frame of the gender card.
	///
	/// B closes it, which is the guest's own answer too: its B arm sets the `Outro` slide from
	/// any of the three card substates, and `Outro` switches on the group's `BehaviorUnFreeze`
	/// element, whose event drives the screen back to substate 0 — the menu list.
	///
	/// A with NEITHER card up does nothing, and that is read rather than invented: the guest's
	/// A arm has no case for substate 2 and falls through to `default: return` at
	/// `0x826DF3E8`. So the panel opens with a real question and will not answer it for you.
	/// </summary>
	/// <summary>
	/// The pump's names for the menu's two movement sounds. Host-only: on the guest's side
	/// these are command ids rather than menu entries, so it has no such strings.
	/// </summary>
	private const string MenuSlideUpAction = "MenuSlideUp";

	private const string MenuSlideDownAction = "MenuSlideDown";

	/// <summary>Last direction reported to the card, so the trace fires on change only.</summary>
	private int _lastCardDirection;

	private void DriveCardChoice()
	{
		if (_cardView is null)
		{
			return;
		}

		// WHAT THE CARD IS BEING TOLD, reported when it changes.
		//
		// The panel draws but has been seen not responding to left and right, and the two
		// explanations need opposite fixes: either the direction never arrives here — the
		// masking, the pad mapping, the axis fold — or it arrives and the pose does not follow.
		// Only the input side is invisible from a screenshot, so it is the side that says so.
		// Keyed on change, because this runs at the display rate.
		int sideways = GameInput.MenuHorizontal;
		if (sideways != _lastCardDirection)
		{
			_lastCardDirection = sideways;
			GD.Print($"[game-ui] gender card input {sideways:+#;-#;0} — {_cardView.Describe()}");
		}

		if (_cardView.Navigate(sideways))
		{
			GD.Print($"[game-ui] gender card moved — {_cardView.Describe()}");
		}

		if (GameInput.MenuBackPressed)
		{
			// The guest sees this press too, through XInput, and it is sitting in substate 0
			// with the menu list live — so its own B handler may leave for the title screen at
			// the same moment we cancel back to the rows. That is inherited rather than
			// introduced: the row menu has always shared its A and B with the guest (which is
			// how B on the root menu reaches the title at all), and closing it needs a way to
			// suppress guest input for a press, which the bridge does not have.
			CloseCardChoice();
			GD.Print("[game-ui] the gender card was cancelled — back to the menu");
			return;
		}

		if (!GameInput.MenuAcceptPressed)
		{
			return;
		}

		if (_cardView.Activate() is not { } chosen)
		{
			// Neither card is up. Nothing to confirm, and nothing to say — pressing A on an
			// unanswered question is not a fault.
			return;
		}

		CloseCardChoice();
		Post(chosen, "gender card");
	}

	/// <summary>
	/// Takes the card down and gives the rows back.
	///
	/// One place, because a card closed without restoring the menu leaves the front end with
	/// nothing on it and no way back — and there are two ways out of the panel.
	/// </summary>
	private void CloseCardChoice()
	{
		if (_cardView is not null)
		{
			_cardView.Visible = false;
		}

		if (_menuView is not null)
		{
			_menuView.Visible = true;
		}
	}

	/// <summary>
	/// The attributes we read live. sdbm of the name, which is the id the runtime stores —
	/// computed once rather than per frame.
	/// </summary>
	private static readonly uint[] OpacityIds =
	{
		Engine_GameHash.Sdbm("opacity") & Engine_GameHash.AttributeIdMask,

		// The tint's WEIGHT, read in the same pass rather than a second walk over 3,444
		// elements. Without it every quad draws at the authored weight, which is usually none.
		Engine_GameHash.Sdbm("TintingAmount") & Engine_GameHash.AttributeIdMask,

		// THE TINT COLOUR, useless without the weight and vice versa: `rgb -= (1 - tint) *
		// amount` subtracts nothing against white. `SetTintingAmountAndDiffuseColour` writes
		// both together. These are 0..255 (divided on the way in at 0x8286EE18); the weight is
		// stored verbatim.
		Engine_GameHash.Sdbm("diffuse.r") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("diffuse.g") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("diffuse.b") & Engine_GameHash.AttributeIdMask,
	};

	/// <summary>Indices within <see cref="OpacityIds"/>.</summary>
	private const int TintingAmountSlot = 1;

	private const int DiffuseRSlot = 2;
	private const int DiffuseGSlot = 3;
	private const int DiffuseBSlot = 4;

	/// <summary>The guest stores diffuse attributes 0..255 and divides by this on the way in.</summary>
	private const float DiffuseScale = 1f / 255f;

	/// <summary>
	/// The attributes that decide WHERE an element is, in the order
	/// <see cref="Engine_UiPresentationHost.LiveTransform"/> expects them.
	///
	/// These are read for the same reason opacity is: they are values the guest STATES, and
	/// the file's copy is only a starting point. A slide moves an element by writing them, and
	/// the shipped Lua writes them directly for anything it lays out itself — which is why a
	/// menu authored with all twelve rows in one place needs them to look like a menu at all.
	/// </summary>
	private static readonly uint[] TransformIds =
	{
		Engine_GameHash.Sdbm("position.x") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("position.y") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("position.z") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("scale.x") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("scale.y") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("scale.z") & Engine_GameHash.AttributeIdMask,
	};

	/// <summary>
	/// Reads the guest's live transform for one presentation, as
	/// <see cref="Engine_UiPresentationHost.LiveTransform"/> wants it.
	///
	/// A row is null when the guest states none of the six on that element, which is the
	/// common case — most elements never move, and a null row leaves the file's transform
	/// alone rather than flattening it to zero.
	/// </summary>
	private static float?[]?[]? ReadLiveTransform(uint presentation, int elementCount)
	{
		HostBridge.LiveAttribute[,]? attributes =
			HostBridge.GetUiAttributes(TransformIds, presentation);
		if (attributes is null)
		{
			return null;
		}

		int rows = System.Math.Min(attributes.GetLength(0), elementCount);
		var transform = new float?[]?[rows];
		for (int i = 0; i < rows; ++i)
		{
			float?[]? values = null;
			for (int a = 0; a < TransformIds.Length; ++a)
			{
				if (!attributes[i, a].Present)
				{
					continue;
				}

				values ??= new float?[TransformIds.Length];
				values[a] = attributes[i, a].AsFloat;
			}

			transform[i] = values;
		}

		return transform;
	}

	private string? _showing;
	private int[]? _showingSlides;
	private bool[]? _showingVisible;

	/// <summary>
	/// Reports which presentations the guest has live, whenever that set CHANGES.
	///
	/// This exists because "the in-game UI is missing" is two different problems wearing one
	/// face, and they need opposite fixes. Either the guest has a HUD standing up and Godot is
	/// not drawing it, or the guest never built one under a null device — and until the
	/// registry could be read at all, there was no way to tell which.
	///
	/// Keyed on the set rather than polled, because it changes rarely (a menu opening) and
	/// printing 30 screens every frame would bury the transition that matters.
	/// </summary>
	private void TracePresentations()
	{
		// Sampled, not polled. The registry walk allocates and reads guest memory, the set it
		// reports changes on the scale of a menu opening, and this runs at the display rate —
		// so a few times a second is the honest sampling rate for it.
		if (++_presentationTick % PresentationSampleFrames != 0 ||
			HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		System.Collections.Generic.List<HostBridge.Presentation> live =
			HostBridge.GetPresentations();

		// Drawing comes first and happens EVERY sample, because the guest keeps mutating the
		// element tree of a presentation that is already up — the HUD's numbers change without
		// the registry changing at all. The report below is the part that only fires on a
		// change, so a stable screen does not fill the log.
		if (!HostBridge.FrontEndGuiIsUp)
		{
			FollowInGameUi(live);
		}
		else
		{
			HasInGameUi = false;
			FollowFrontEndExtras(live);
		}

		// WHICH SCREEN THE GUEST SAYS IT IS ON, reported on change and separately from the set
		// of live presentations — because for the in-game screens those two are not the same
		// claim. The pause menu's presentation is created once at game entry and never freed,
		// so it is in the list below from the first frame of play; only this says whether the
		// player is actually in it.
		HostBridge.InGameScreens? manager = HostBridge.GetInGameScreens();
		HostBridge.InGameScreen? screen = manager?.Current;
		if (screen != _inGameScreen)
		{
			_inGameScreen = screen;

			// THE SLOTS TOO, because "which screen is current" and "which presentation is in
			// which slot" are separate claims and the draw gate needs both to be right. A gate
			// that silently never fires looks exactly like a gate that fires and finds nothing,
			// and only the addresses tell them apart — they can be read straight against the
			// `[guest-ui] N live presentation(s)` list printed below, which gives each
			// presentation's address.
			string slots = manager is { } m && m.Owned.Count > 0
				? string.Join(", ", System.Linq.Enumerable.Select(
					System.Linq.Enumerable.OrderBy(m.Owned, e => e.Value),
					e => $"[{e.Value}]={e.Key:X8}"))
				: "none built yet";

			GD.Print(screen is null
				? "[guest-ui] no screen manager yet — the guest is not in game"
				: $"[guest-ui] the guest's current in-game screen is {screen} " +
				  $"({(int)screen.Value}); manager holds {slots}");
		}

		string signature = string.Join('|', live.ConvertAll(p => $"{p.Address:X8}:{p.Name}"));
		if (signature == _presentationSignature)
		{
			return;
		}

		_presentationSignature = signature;

		if (live.Count == 0)
		{
			GD.Print("[guest-ui] no live presentations at all — the guest has built no UI");
			return;
		}

		GD.Print($"[guest-ui] {live.Count} live presentation(s):");
		foreach (HostBridge.Presentation p in live)
		{
			// The ROOT is the half that decides whether it can be drawn. A presentation with a
			// null root is constructed but not yet deserialised, which looks identical from the
			// outside to one we simply are not rendering.
			GD.Print($"    {p.Address:X8} root={p.Root:X8} " +
				$"{(p.IsShowing ? "SHOWING" : "hidden")} " +
				$"active={p.ActiveElements}{(p.IsFrozen ? " FROZEN" : string.Empty)} " +
				$"{(p.Root == 0 ? "(NO ROOT — built but not populated)" : string.Empty)} " +
				$"'{p.Name}'");
		}
	}

	// No list of in-game screens any more, deliberately. Which presentations are drawn is the
	// guest's statement — see HostBridge.Presentation.IsShowing — so there is nothing here to
	// keep in step with the thirty in-game screens.

	/// <summary>
	/// True when the guest has an in-game presentation standing up.
	///
	/// The window uses this to decide whether the UI layer belongs on screen at all. It is
	/// deliberately about what the GUEST has live rather than about whether we happen to be
	/// drawing it, so a presentation we cannot yet render still keeps the layer up instead of
	/// silently taking the whole UI away.
	/// </summary>
	public bool HasInGameUi { get; private set; }

	/// <summary>One presentation being drawn, and the state it was last built from.</summary>
	private sealed class Drawn
	{
		public required string Name { get; init; }
		public required Node3D Node { get; init; }
		public int[]? Slides { get; set; }
		public bool[]? Visible { get; set; }

		/// <summary>
		/// Compared too, and it has to be: the HUD's own hide is an opacity write with no slide
		/// or visibility change anywhere, so a screen that only fades would never be rebuilt.
		/// </summary>
		public float?[]? Opacity { get; set; }

		/// <summary>
		/// The tint WEIGHT per element the screen was last built from.
		///
		/// In the test for the same reason opacity is. A wheel item highlighting raises its
		/// `TintingAmount` and changes nothing else — no slide, no visibility, no opacity — so
		/// a screen whose only change is a tint would keep drawing the previous weight.
		/// </summary>
		public Engine_UiScreen.LiveTint?[]? Tint { get; set; }

		/// <summary>
		/// The live texture hash per element the screen was last built from.
		///
		/// Part of the rebuild test, because a texture swap changes nothing else about an
		/// element — equipping an item repaints a dpad slot without touching its slide.
		/// </summary>
		public uint[]? Textures { get; set; }

		/// <summary>
		/// The builder that produced <see cref="Node"/>, kept for its ordinal map.
		///
		/// A transform is the one piece of live state that must NOT trigger a rebuild — the
		/// guest moves these every frame — so it is written straight onto the built nodes
		/// instead. See <see cref="Engine_UiSceneBuilder.ApplyLiveTransform"/>.
		/// </summary>
		public Engine_UiSceneBuilder? Builder { get; set; }

		/// <summary>How many elements this presentation carries, for the per-frame read.</summary>
		public int ElementCount { get; set; }

		/// <summary>
		/// The live-value bindings this presentation carries, and the view that applies them.
		///
		/// Non-null only for a screen that HAS bindings, which in practice is the HUD — the
		/// front-end screens carry none. Rebuilt with the node tree, because the view holds
		/// nodes belonging to it: a screen the guest changes the state of is rebuilt from the
		/// file, and a view still pointing at the old tree would drive nodes nobody can see.
		/// </summary>
		public Engine_UiLiveValues? Values { get; set; }

		/// <summary>
		/// The screen's WIDE layout, applied after the guest's transforms every frame.
		///
		/// Per screen because it is file data, and re-read with the tree for the same reason the
		/// bindings are: both are keyed by element ordinal into a tree that is rebuilt whenever
		/// the guest changes state.
		/// </summary>
		public Engine_UiWidescreen? Widescreen { get; set; }
	}

	private readonly System.Collections.Generic.Dictionary<uint, Drawn> _drawn = new();

	/// <summary>
	/// Follows the guest's per-frame movement on every drawn presentation.
	///
	/// The transforms are the guest's own statement and they change constantly — the front end's
	/// backdrop scrolls, the HUD's needles swing — so they are written onto the existing nodes
	/// rather than being allowed to trigger a rebuild. Slides, visibility and opacity still go
	/// through <see cref="Follow"/>, because those DO change the shape of the tree.
	///
	/// Reported the first time each presentation actually moves, so "the guest never moved it"
	/// and "we never applied it" stay distinguishable — they were not, for two turns.
	/// </summary>
	private void AnimateDrawn()
	{
		if (_drawn.Count == 0 || HostBridge.TryGetAbiVersion() is null)
		{
			return;
		}

		// THE LIVE VALUES, once per frame for every drawn screen that has bindings.
		//
		// ONE READ FOR ALL OF THEM, not one per bar. The guest keeps every meter in a single map
		// keyed by name hash, so the whole HUD's state is one walk — and a per-bar lookup would
		// mean re-walking a red-black tree fifty-two times a frame for values that were all
		// written together.
		//
		// Read outside the loop for the same reason: two drawn screens can bind the same name,
		// and sampling twice in one frame could hand them different values from a map the guest
		// is still writing.
		++_valueTick;

		System.Collections.Generic.Dictionary<uint, float>? meters = null;
		System.Collections.Generic.Dictionary<uint, string>? texts = null;
		foreach (Drawn candidate in _drawn.Values)
		{
			if (candidate.Values is not null)
			{
				meters = HostBridge.GetHudMeters();
				texts = HostBridge.GetHudTexts();
				break;
			}
		}

		foreach ((uint address, Drawn drawn) in _drawn)
		{
			if (meters is not null && drawn.Values is { } values)
			{
				values.Apply(meters, texts);

				// Reported when the picture CHANGES, so "the guest never published" and "we
				// never applied" stay distinguishable — the same distinction the transform
				// report below exists to keep, and the one that cost two turns. EITHER half
				// counts. A HUD whose bars move but whose text never arrives, and one whose text
				// arrives but whose bars sit at zero, are different faults — and a report that
				// fired only on meters would stay silent through the second.
				//
				// ON CHANGE RATHER THAN ONCE, because a single sample cannot tell a value that
				// IS zero from one we looked at too early. This used to fire the first time
				// anything landed, which on the HUD is while the world is still loading — and it
				// duly reported PlayerHealth=0% for a hero that had not spawned yet, with no
				// second line to correct it. Sampled rather than polled: the string is rebuilt
				// once a second per screen, not once a frame.
				// NO "SOMETHING LANDED" PRECONDITION, and removing it is the fix for a report
				// that went completely silent. Requiring a non-zero applied count AND the
				// sample tick meant both had to be true in the SAME frame — one frame in
				// sixty — so a run where the guest published nothing on those exact frames
				// produced no line at all, and "the HUD has no values" became
				// indistinguishable from "the report never ran". A description reading
				// `meters=0/0` is itself the answer, so it is worth printing.
				if (_valueTick % ValueSampleFrames == 0)
				{
					string described = values.Describe(meters, texts);
					if (!_valued.TryGetValue(address, out string? wasValued) ||
						!string.Equals(wasValued, described, System.StringComparison.Ordinal))
					{
						_valued[address] = described;
						GD.Print($"[game-ui] '{drawn.Name}' live values — {described}");
					}
				}
			}

			if (drawn.Builder is null || drawn.ElementCount == 0)
			{
				continue;
			}

			if (ReadLiveTransform(address, drawn.ElementCount) is not { } transform)
			{
				continue;
			}

			int moved = drawn.Builder.ApplyLiveTransform(transform);

			// THE WIDE LAYOUT LAST, and every frame. The guest still believes it is in 4:3 —
			// nothing calls `GUI:SetToDisplayInWidescreen()` here, because that is a guest CALL
			// and this bridge only reads and writes memory — so it keeps restating the
			// standard-definition position on anything it animates. Applying the override after
			// the transform is the same final word the shipped event handler has.
			if (drawn.Widescreen is { } wide && _isWidescreen)
			{
				wide.Apply(drawn.Builder);
			}

			// LAST, because a clip has to follow whatever moved the mask — the live transform
			// above, or the widescreen override after it. A mask is an ordinary scene node and
			// the pause menu animates its own in lockstep with the page slide.
			drawn.Builder.RefreshClips();
			if (moved > 0 && _animated.Add(address))
			{
				GD.Print($"[game-ui] '{drawn.Name}' is animating — {moved} element(s) moved " +
					"on the first frame it did");
			}

			_lastAddress = address;
			ReportScroll(drawn, moved);
			FollowMovie(drawn, address);
		}
	}

	/// <summary>
	/// WHAT IS ACTUALLY ON SCREEN AT A GIVEN X — a scanline across the canvas, once, late.
	///
	/// The backdrop measurement closed its own question and opened this one: all 28 of its quads
	/// paint, continuously, right across the visible band, with nothing hidden, untextured or
	/// transparent. So the black stripe is not a hole in the backdrop — something is drawn OVER
	/// it. Which means the useful question is no longer "does the backdrop reach here" but "what
	/// is painting here", and no amount of reading the backdrop can answer that.
	///
	/// So this walks EVERY node under the presentation host — the guest's screens, our cloned
	/// rows, the card, the warning — and at each sample x reports the painting quads that cover
	/// it, nearest first. Three of them, because the answer to "why is this black" is usually the
	/// second one.
	///
	/// SAMPLED LATE. It runs once, well after the menu is up, because a scanline taken while the
	/// tree is still being assembled describes a screen the player never saw — the failure this
	/// project has hit repeatedly with once-only diagnostics.
	/// </summary>
	private void Scanline()
	{
		if (_presentation is null)
		{
			return;
		}

		var quads = new System.Collections.Generic.List<(float Min, float Max, float Z, string What)>();
		Collect(_presentation);

		void Collect(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Collect(child);
			}

			if (node is not MeshInstance3D mesh || !mesh.IsVisibleInTree())
			{
				return;
			}

			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			if (!paint.Painted)
			{
				return;
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			// Only what crosses the canvas centreline, which is where the stripe was measured.
			if (box.Position.Y > 0f || box.End.Y < 0f)
			{
				return;
			}

			// AN UNTEXTURED QUAD IS NOT SKIPPED, it is NAMED. A quad with no texture paints its
			// tint flat across its whole area — which for the default white is a solid white
			// plate. That is the single most likely cause of a screen going white, so a
			// scanline that filtered it out would have dutifully reported whatever was behind
			// the very thing being looked for.
			string texture = paint.Texture is { } albedo
				? albedo.ResourcePath.GetFile()
				: "NO-TEXTURE";
			Color tint = paint.Tint;

			quads.Add((box.Position.X, box.End.X, box.Position.Z,
				$"{mesh.Name}<{(texture.Length > 0 ? texture : "runtime")} " +
				$"{tint.R:F2},{tint.G:F2},{tint.B:F2}@a{tint.A:F2}>"));
		}

		var line = new System.Text.StringBuilder(
			$"[game-ui] scanline on '{_scanning}' at y=0 ({quads.Count} painting quad(s), nearest first)");
		foreach (float x in new[] { -600f, -480f, -400f, -340f, -280f, -160f, 0f, 200f, 400f, 600f })
		{
			var over = new System.Collections.Generic.List<(float Z, string What)>();
			foreach ((float min, float max, float z, string what) in quads)
			{
				if (x >= min && x <= max)
				{
					over.Add((z, what));
				}
			}

			over.Sort((a, b) => b.Z.CompareTo(a.Z));

			line.Append($"\n    x={x,5:F0}: ");
			line.Append(over.Count == 0
				? "NOTHING PAINTS HERE"
				: string.Join("  ", over.ConvertAll(o => $"{o.What}@z{o.Z:F0}").GetRange(
					0, System.Math.Min(3, over.Count))) + $"  (+{System.Math.Max(0, over.Count - 3)} more)");
		}

		GD.Print(line.ToString());
		Inventory();
	}

	/// <summary>
	/// Every named element on a small screen, painting or not — the "what is missing" report.
	///
	/// THE SCANLINE CANNOT ANSWER THIS. It samples the centreline, so anything that lives above
	/// or below y=0 — "Press A To Start", the legal text, the logo's particle group — is invisible
	/// to it whether it is drawn correctly or not there at all. Asking "what is painting at this
	/// x" and "does this element exist" are different questions and the first one silently
	/// answers "no" to both.
	///
	/// So this lists the built tree: name, world bounds, and why it is not on screen if it is
	/// not. An element absent from this list was never built; one present with `HIDDEN` or
	/// `a0.00` was built and suppressed; one with sane bounds and full alpha is on screen and the
	/// complaint is about placement. Those are three different fixes.
	///
	/// SMALL SCREENS ONLY. `frontendmainmenu` carries 3812 elements and would bury the log; the
	/// title screen carries about twenty. The cap is stated rather than silent — a report that
	/// quietly covers some screens and not others is how "it printed nothing" gets read as
	/// "nothing is wrong".
	/// </summary>
	private void Inventory()
	{
		// 128, not 64. The title screen has 77 elements and the first cap skipped it — the one
		// screen the inventory existed to explain. Sized to clear it with room rather than to
		// sit just above it, while still excluding `frontendmainmenu`'s 3812.
		const int cap = 128;

		if (_presentation?.CurrentScreen is not { } screen)
		{
			return;
		}

		if (screen.Nodes.Length > cap)
		{
			GD.Print($"[game-ui] inventory of '{_scanning}' SKIPPED — {screen.Nodes.Length} " +
				$"elements, over the {cap} cap");
			return;
		}

		// The built tree, gathered by NAME. Names are not guaranteed unique, but on a screen
		// small enough to pass the cap a collision is visible in the output rather than hidden
		// by it — and the alternative, the builder's ordinal map, is not reachable for a screen
		// the host mounted itself.
		var found = new System.Collections.Generic.Dictionary<string, string>();
		Gather(_presentation);

		void Gather(Node node)
		{
			foreach (Node child in node.GetChildren())
			{
				Gather(child);
			}

			string name = node.Name;
			if (found.ContainsKey(name))
			{
				return;
			}

			if (node is not MeshInstance3D mesh)
			{
				found[name] = node is Node3D { Visible: false } ? "group HIDDEN" : "group";
				return;
			}

			if (!mesh.IsVisibleInTree())
			{
				found[name] = "HIDDEN";
				return;
			}

			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			found[name] =
				$"x[{box.Position.X:F0}..{box.End.X:F0}] y[{box.Position.Y:F0}..{box.End.Y:F0}] " +
				$"a{paint.Opacity:F2}";
		}

		var line = new System.Text.StringBuilder(
			$"[game-ui] inventory of '{_scanning}' ({screen.Nodes.Length} authored element(s))");

		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			UiNode authored = screen.Nodes[i];
			string name = authored.HasAuthoredName ? authored.Name : $"#{i}";
			string kind = authored.Quad is not null
				? "quad"
				: authored.Text is not null ? $"text \"{authored.Text.Display}\"" : "group";

			line.Append($"\n    {name} [{kind}] " +
				(found.TryGetValue(name, out string? state) ? state : "NOT IN THE BUILT TREE"));
		}

		GD.Print(line.ToString());
	}

	/// <summary>
	/// One scanline per presentation, taken once it has settled.
	///
	/// KEYED ON WHICH SCREEN IS UP, not on a single once-ever latch. The first version counted
	/// frames only after the main-menu guard, so it could never fire for the title screen — the
	/// exact screen that turned out to be drawing a white plate. A diagnostic that cannot run on
	/// the broken screen is not a diagnostic, and this is the second time on this feature that
	/// the gate, not the code, was the defect.
	/// </summary>
	private void ScanWhenSettled()
	{
		if (!IsRevealed)
		{
			_scanning = null;
			return;
		}

		string current = _showing ?? Screen;
		if (current != _scanning)
		{
			_scanning = current;
			_scanFrames = 0;
			return;
		}

		// Two seconds in. Long enough for a fade to finish, so an element caught mid-transition
		// is not reported as a permanent one.
		if (++_scanFrames == 120)
		{
			Scanline();
		}
	}

	private string? _scanning;

	private int _scanFrames;

	/// <summary>
	/// Runs the title screen's logo flash — see <see cref="Engine_FrontEndTitle"/>.
	///
	/// GODOT DRIVES IT, which is the point rather than an implementation detail. The alternative
	/// was to follow the guest's own `frontendstartupscreen` presentation and take the opacity it
	/// states, and that would have worked for this one element while handing the screen's
	/// animation back to a presentation the guest reports as `hidden` throughout. The front end
	/// is ours: the menu, the card and the warning are all driven here, and the title screen is
	/// the last piece that was not.
	/// </summary>
	private void DriveTitle(double delta)
	{
		// NO SCREEN-NAME TEST. The first version only ran while `_showing` matched
		// "frontendstartupscreen", and produced no output at all on a run where the scanline —
		// same method, same frames, one line above — reported that very screen twice. Rather
		// than keep reasoning about why a string comparison did not hold, the comparison is
		// gone: the flash is driven wherever the current screen actually has one, and a screen
		// without it says so.
		string current = _showing ?? Screen;
		if (current != _titleScreen)
		{
			_titleScreen = current;
			_titleFrames = 0;
			_title.Reset();
		}

		string before = _title.LastReport;
		_title.Advance(_presentation?.Theme, delta);
		++_titleFrames;

		// REPORTED ON CHANGE, not on a frame count. The frame-count version fired once, sixty
		// frames after process start — which is before the title screen is revealed and before
		// its catalogue exists — and so reported "not in this screen's catalogue" on a run where
		// the fade then worked perfectly. A one-shot sample of a state that has not happened yet
		// is worse than no sample: it reads as a failure.
		if (_title.LastReport != before)
		{
			GD.Print($"[game-ui] logo flash on '{current}' — {_title.LastReport}");
		}
	}

	private int _titleFrames;

	private readonly Engine_FrontEndTitle _title = new();

	private string? _titleScreen;

	/// <summary>
	/// One element's live tint, as weight plus colour, or null when the guest states no weight.
	///
	/// Both halves or neither: a weight with no colour subtracts nothing, and a colour with no
	/// weight is inert. The guest writes them together and so does this.
	/// </summary>
	private static Engine_UiScreen.LiveTint? ReadTint(HostBridge.LiveAttribute[,] attributes, int i)
	{
		if (!attributes[i, TintingAmountSlot].Present)
		{
			return null;
		}

		return new Engine_UiScreen.LiveTint(
			attributes[i, TintingAmountSlot].AsFloat,
			attributes[i, DiffuseRSlot].Present
				? attributes[i, DiffuseRSlot].AsFloat * DiffuseScale : null,
			attributes[i, DiffuseGSlot].Present
				? attributes[i, DiffuseGSlot].AsFloat * DiffuseScale : null,
			attributes[i, DiffuseBSlot].Present
				? attributes[i, DiffuseBSlot].AsFloat * DiffuseScale : null);
	}

	/// <summary>The video player, so an embedded movie can be drawn into the element hosting it.</summary>
	public GameVideo? Video { get; set; }

	/// <summary>Element ordinals currently hosting a movie, per presentation address.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, int> _movieSlots = new();

	/// <summary>
	/// True while any drawn presentation hosts an embedded movie — so the window's
	/// "hide the UI" handler does not conceal the screen a preview is playing inside.
	/// </summary>
	public bool HasEmbeddedMovie => _movieSlots.Count > 0;

	/// <summary>
	/// Draws the guest's embedded movie into the element hosting it. A sprite whose `Movie`
	/// string is non-empty draws a movie into its OWN rect instead of a texture;
	/// `RefiiHostGetUiMovie` reads it off the same sprite base as the texture hash, so its
	/// ordinal joins like every other live array. The frame is STRETCHED — the guest's draw
	/// never reads the movie's own size.
	/// </summary>
	private void FollowMovie(Drawn drawn, uint address)
	{
		if (Video is null || drawn.Builder is null)
		{
			return;
		}

		if (HostBridge.GetUiMovie(address) is not { } movie)
		{
			if (_movieSlots.Remove(address) && !HasEmbeddedMovie)
			{
				Video.EmbedOrdinal = -1;
			}

			return;
		}

		if (!_movieSlots.TryGetValue(address, out int was) || was != movie.Ordinal)
		{
			_movieSlots[address] = movie.Ordinal;
			GD.Print($"[game-ui] '{drawn.Name}' element {movie.Ordinal} is hosting a movie: " +
				$"{movie.Path}");
		}

		Video.EmbedOrdinal = movie.Ordinal;

		// The element the ATTRIBUTE sits on is a Node whose quad is the card; the guest also
		// authors `Movie` on its Material child, which has no quad of its own. Painting is
		// therefore skipped rather than forced when the ordinal has no quad.
		if (movie.Ordinal < drawn.Builder.ByOrdinal.Length &&
			drawn.Builder.ByOrdinal[movie.Ordinal] is MeshInstance3D quad &&
			Video.FrameTexture is { } frame)
		{
			Engine_UiSceneBuilder.SetQuadTexture(quad, frame);
		}
	}

	/// <summary>
	/// Reports WHERE the moving parts have got to, about once a second.
	///
	/// The backdrop is a strip of 512-wide panels drawn twice, and the copy is nested inside the
	/// scrolling group — so whether the two copies abut, overlap or leave a hole is decided by a
	/// couple of live positions and nothing else. A still frame cannot tell a hole in the wrap
	/// from a panel that failed to draw, and the file's own numbers cannot either: it parks the
	/// second copy 131 units short of the first copy's end, which the guest then moves.
	///
	/// Generic rather than keyed on element names — it reports whatever moved, so it is worth
	/// the same on the HUD.
	/// </summary>
	private void ReportScroll(Drawn drawn, int moved)
	{
		if (drawn.Builder is null)
		{
			return;
		}

		// A SETTLED SCREEN IS REPORTED TOO, once. Gating on `moved > 0` only ever described a
		// screen mid-animation, and the state worth measuring is the one after it stops.
		bool settled = moved == 0 && _animated.Contains(_lastAddress) && _stilled.Add(_lastAddress);

		if (!settled && (moved == 0 || ++_scrollTick % ScrollSampleFrames != 0))
		{
			return;
		}

		var report = new System.Text.StringBuilder(
			$"[game-ui] '{drawn.Name}' {(settled ? "settled" : "moving")}:");
		int shown = 0;

		Node3D?[] byOrdinal = drawn.Builder.ByOrdinal;
		for (int i = 0; i < byOrdinal.Length && shown < ScrollReportLimit; ++i)
		{
			// Only the groups are interesting — a panel inherits its parent's motion and would
			// fill the line with numbers that say the same thing.
			if (byOrdinal[i] is not { } node || node.GetChildCount() == 0)
			{
				continue;
			}

			// Scale only when it is not 1. The panels are laid out edge to edge in their own
			// space, so the ONE thing that can open a hole between the two copies of a strip is
			// a scale on a group — the positions alone say they always overlap by 131.
			string scale = node.Scale.IsEqualApprox(Vector3.One)
				? string.Empty
				: $"*{node.Scale.X:F3},{node.Scale.Y:F3}";

			report.Append($"  {node.Name}=({node.Position.X:F0},{node.Position.Y:F0}){scale}");
			++shown;
		}

		report.Append(Coverage(drawn));

		if (shown > 0)
		{
			GD.Print(report.ToString());
		}
	}

	/// <summary>
	/// Where a drawn presentation's quads actually PAINT, and the largest hole in them.
	///
	/// THIS MEASURED THE WRONG THING FIRST TIME and the correction is the whole point. It took
	/// the span of every BUILT quad and reported "no holes" on every frame, across spans as wide
	/// as x=[-1173..5864] — while a black band sat on screen. Both statements were true: the
	/// geometry is continuous and the pixels are not. A quad that is hidden, has no albedo
	/// texture, or is fully transparent occupies its span and paints none of it, so counting it
	/// answers a question nobody asked.
	///
	/// So a span is only counted when the quad actually paints, and the three reasons one might
	/// not are reported SEPARATELY — hidden, untextured and transparent are three different
	/// defects with three different fixes, and a single "not drawing" total cannot tell them
	/// apart. If the hole is real the edges are exact; if the painted band is contiguous and the
	/// black is still there, it belongs to a different presentation.
	/// </summary>
	private static string Coverage(Drawn drawn)
	{
		if (drawn.Builder is null)
		{
			return string.Empty;
		}

		var spans = new System.Collections.Generic.List<(float Min, float Max)>();
		int hidden = 0;
		int untextured = 0;
		int transparent = 0;

		// NAMED, not just counted. "2 PAINTING UNTEXTURED" says a flat plate is being painted
		// over the game somewhere and gives no way to find it; a screenshot of a magenta
		// rectangle on the HUD and a count of two are the same fact reported twice, neither
		// actionable. The name is the join to the file, where the element's texture and colour
		// can be read directly.
		var untexturedNames = new System.Collections.Generic.List<string>();

		// Every painting quad as (front Z, area, node), for the covering report below.
		var frontMost = new System.Collections.Generic.List<(float Z, float Area, MeshInstance3D Mesh)>();

		MeshInstance3D? leftMost = null;
		MeshInstance3D? rightMost = null;
		float leftX = 0f;
		float rightX = 0f;
		MeshInstance3D? widest = null;
		float widestSpan = 0f;

		foreach (Node3D? node in drawn.Builder.ByOrdinal)
		{
			if (node is not MeshInstance3D mesh)
			{
				continue;
			}

			if (!mesh.IsVisibleInTree())
			{
				++hidden;
				continue;
			}

			// A MeshInstance3D WITH NO MESH DRAWS NOTHING — skip it before it can be counted
			// or measured. The builder returns exactly that for a text element whose glyph
			// build produced no geometry: an empty label keeps its node so the metadata still
			// hangs off it, but gets neither a mesh nor a material.
			//
			// Counting those cost two wrong readings at once. They have no material, so they
			// were filed under PAINTING UNTEXTURED and named as suspects — `Subtitles/Group/Text`
			// and `LookAt/LookAt/Text`, neither of which paints a pixel. And `GetAabb()` on a
			// meshless instance is an empty box at the node's ORIGIN, which still joins the
			// span union: that is what put a HUD whose real content spans x=[-466..575] on
			// record as reaching x=-1283.
			if (mesh.Mesh is null)
			{
				continue;
			}

			// The builder puts the quad's material on the override slot; the surface slot is the
			// fallback for anything cloned or re-materialised since. Read through the builder
			// so a quad and a text mesh both answer — see DescribeQuad.
			Engine_UiSceneBuilder.QuadPaint paint = Engine_UiSceneBuilder.DescribeQuad(mesh);
			if (paint.Opacity <= 0f && paint.Texture is null && !paint.DetailMapped)
			{
				++untextured;
				Name(untexturedNames, mesh, "no material");
				continue;
			}

			if (!paint.Painted)
			{
				++transparent;
				continue;
			}

			// COUNTED AS PAINTING, and flagged. A quad with no albedo texture is not silent —
			// the material paints its albedo colour over the whole quad, so it covers its span
			// with a flat plate. Filing it under "not drawing" was backwards, and that is the
			// same mistake in miniature as counting geometry instead of pixels.
			// A DETAIL-MAPPED QUAD IS TEXTURED even with no primary: its RGB comes from the
			// detail map, and the primary would only have supplied alpha. Counting those as
			// untextured would report the entire frame widget as painting flat colour.
			if (paint.Texture is null && !paint.DetailMapped)
			{
				++untextured;

				// The COLOUR it is painting, because that is what identifies it on screen. A
				// quad with no albedo texture paints the .fac record's vertex colour flat, so
				// the magenta plate in a screenshot and the entry in this list are matched by
				// eye without another run.
				Color c = paint.Tint;
				Name(untexturedNames, mesh,
					$"rgba({c.R:0.##},{c.G:0.##},{c.B:0.##},{c.A:0.##})" +
					(mesh.HasMeta("texture_path")
						? $" wanted {(string)mesh.GetMeta("texture_path")}"
						: " no texture named"));
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();
			spans.Add((box.Position.X, box.End.X));

			// WHAT IS IN FRONT. The span says how wide the painting is, not what the player
			// sees. These presentations share one orthographic camera and write no depth, so
			// covering is decided purely by the Z sort.
			frontMost.Add((box.End.Z, box.Size.X * box.Size.Y, mesh));

			// THE OUTLIERS BY NAME — the span's union cannot say which element reaches past it.
			if (leftMost is null || box.Position.X < leftX)
			{
				leftX = box.Position.X;
				leftMost = mesh;
			}

			if (rightMost is null || box.End.X > rightX)
			{
				rightX = box.End.X;
				rightMost = mesh;
			}

			// THE WIDEST PAINTING QUAD, BY NAME. The span line reports the union of every
			// painting quad, so a single element reaching past the canvas edge shows up only as
			// an odd number — `x=[-1283..642]` on a 1280-wide canvas — with nothing to say
			// whether that is one deliberately over-wide backdrop or something the transform
			// pass has stretched. The name settles it without another run.
			if (box.Size.X > widestSpan)
			{
				widestSpan = box.Size.X;
				widest = mesh;
			}
		}

		string widestNote = string.Empty;
		if (widest is not null)
		{
			var named = new System.Collections.Generic.List<string>();
			Name(named, widest, $"{widestSpan:F0} wide");
			widestNote = $"  widest={named[0]}";
		}

		// The three front-most quads big enough to matter, nearest first. Area-gated at a
		// twentieth of the canvas so the report names backdrops and panels rather than the
		// topmost pixel of a cursor.
		string covering = string.Empty;
		if (frontMost.Count > 0)
		{
			frontMost.Sort((a, b) => b.Z.CompareTo(a.Z));
			var names = new System.Collections.Generic.List<string>();
			foreach ((float z, float area, MeshInstance3D mesh) in frontMost)
			{
				if (area < 960f * 720f / 20f)
				{
					continue;
				}

				var one = new System.Collections.Generic.List<string>();
				Name(one, mesh, $"z={z:F0} {Mathf.Sqrt(area):F0}²");
				names.Add(one[0]);
				if (names.Count == 3)
				{
					break;
				}
			}

			if (names.Count > 0)
			{
				covering = "  infront=" + string.Join(" > ", names);
			}
		}

		string edges = string.Empty;
		if (leftMost is not null && rightMost is not null)
		{
			var l = new System.Collections.Generic.List<string>();
			Name(l, leftMost, $"x={leftX:F0}");
			var r = new System.Collections.Generic.List<string>();
			Name(r, rightMost, $"x={rightX:F0}");
			edges = $"  leftmost={l[0]}  rightmost={r[0]}";
		}

		string silent = widestNote + edges + covering +
			$" ({hidden} hidden, {transparent} transparent, " +
			$"{untextured} PAINTING UNTEXTURED" +
			(untexturedNames.Count == 0
				? string.Empty
				: ": " + string.Join("; ", untexturedNames)) + ")";

		if (spans.Count == 0)
		{
			return $"  paints=none{silent}";
		}

		spans.Sort((a, b) => a.Min.CompareTo(b.Min));

		// The canvas half-width the camera shows at 16:9. Only a hole INSIDE what the player can
		// see is a defect; the strip runs far off both sides by design.
		const float visible = 640f;
		float reach = spans[0].Max;
		float worstGap = 0f;
		float gapAt = 0f;

		foreach ((float min, float max) in spans)
		{
			if (min > reach && min < visible && reach > -visible)
			{
				float gap = min - reach;
				if (gap > worstGap)
				{
					worstGap = gap;
					gapAt = reach;
				}
			}

			reach = System.Math.Max(reach, max);
		}

		return $"  paints x=[{spans[0].Min:F0}..{reach:F0}] in {spans.Count} quad(s)" +
			(worstGap > 0.5f ? $"  HOLE {worstGap:F0} wide at x={gapAt:F0}" : "  no holes") +
			silent;
	}

	/// <summary>
	/// Canvas units between one presentation's slab and the next.
	///
	/// Larger than the deepest screen we have measured (176 units, `frontendmainmenu`) by enough
	/// that a deeper one still orders correctly, and small enough that the whole stack stays
	/// well inside the canvas camera's far plane.
	/// </summary>
	private const float PresentationDepthStep = 1000f;

	private int _scrollTick;

	/// <summary>Frames between scroll reports — about once a second at any display rate.</summary>
	private const int ScrollSampleFrames = 60;

	/// <summary>Groups per report. Enough to see a wrap, few enough to stay one line.</summary>
	private const int ScrollReportLimit = 6;

	/// <summary>Presentations already reported as animating, so the line fires once each.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _animated = new();

	/// <summary>
	/// Presentations already reported in their SETTLED state, so that census fires once each
	/// rather than every frame the screen sits still — which is most of them.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _stilled = new();

	/// <summary>The address whose <see cref="Drawn"/> is being reported, for the settled test.</summary>
	private uint _lastAddress;

	/// <summary>Presentations reported as still being built, so that line fires once each too.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _mismatched = new();

	/// <summary>
	/// Presentations withheld because the guest says no screen is up, so that line fires once
	/// each rather than several times a second for as long as the player is walking around.
	/// </summary>
	private readonly System.Collections.Generic.HashSet<uint> _suppressed = new();

	/// <summary>
	/// Presentations whose live VALUES have landed at least once, so that line fires once each.
	///
	/// Separate from <see cref="_animated"/> because they are different claims about the same
	/// screen: a HUD can be animating — its needles swinging, its groups fading — while every
	/// bar it owns sits at the artist's authored zero because no meter ever resolved.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<uint, string> _valued = new();

	/// <summary>Frames between live-value samples — about once a second at any display rate.</summary>
	private const int ValueSampleFrames = 60;

	private int _valueTick;

	/// <summary>
	/// What each presentation's <c>ExpandableMenu</c> last contained, so a change is reported.
	///
	/// REPORTED ON CHANGE RATHER THAN ONCE, and that is the whole reason this is a dictionary
	/// and not another <c>HashSet</c>. The guest spawns a menu's entries AFTER the presentation
	/// appears — the pause menu shows up with an empty 26-button pool and fills it over the
	/// following frames — so a once-per-build line samples the one moment the menu is
	/// guaranteed to be empty and reports that as the answer. Keyed by address like the rest,
	/// and cleared in <see cref="Retire"/>.
	/// </summary>
	private readonly System.Collections.Generic.Dictionary<uint, string> _menuContents = new();

	/// <summary>
	/// Says what the guest has put in this presentation's menu, whenever that changes.
	///
	/// The pause menu is the case this exists for. Its entries are not in the file and not in
	/// the shipped Lua's population table — they are spawned at runtime — so whether the guest
	/// has filled the button pool is a question no static read can answer, and it is the
	/// question that decides whether Godot can own the list the way it owns the front end's.
	/// Silent for every screen without an <c>ExpandableMenu</c>, which is most of them.
	/// </summary>
	/// <summary>
	/// Adds a node to a diagnostic list, by the path that locates it in the file.
	///
	/// The leaf name alone is useless on a HUD: slice names repeat across every frame group, so
	/// a dozen unrelated elements are all called <c>bar</c>. Two ancestors are enough to place
	/// one without making the line unreadable. Capped, because a fault that hits every quad
	/// would otherwise print three thousand names.
	/// </summary>
	private static void Name(System.Collections.Generic.List<string> into, Node node, string why)
	{
		const int limit = 8;
		if (into.Count >= limit)
		{
			return;
		}

		if (into.Count == limit - 1)
		{
			into.Add("…more");
			return;
		}

		string path = node.Name;
		Node? parent = node.GetParent();
		for (int up = 0; up < 2 && parent is not null; ++up, parent = parent.GetParent())
		{
			path = parent.Name + "/" + path;
		}

		into.Add($"{path} [{why}]");
	}

	private void ReportMenuContents(uint address, string name, Engine_UiScreen? screen,
		string?[] texts, bool[] visible, int[] slides, float?[]?[]? transform)
	{
		System.Collections.Generic.IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons =
			Engine_ExpandableMenuItems.Read(screen, texts, visible, slides);
		if (buttons.Count == 0)
		{
			return;
		}

		// WHETHER THE GUEST'S OWN FORMATTER RAN, which is the other half of the question and
		// not answerable from the labels. `expandablemenuformatting.lua` lays the pool out by
		// writing `position.x` and `position.y` on each button component — attributes we
		// already read — so buttons that all still share one position are buttons the
		// formatter never touched. The file authors all 26 on top of each other, so "stacked"
		// is the untouched state and reads as a single bar on screen.
		string laidOut = Engine_ExpandableMenuItems.DescribeLayout(buttons, transform);

		string described = $"{Engine_ExpandableMenuItems.Describe(buttons)}; {laidOut}";
		if (_menuContents.TryGetValue(address, out string? was) &&
			string.Equals(was, described, System.StringComparison.Ordinal))
		{
			return;
		}

		_menuContents[address] = described;
		GD.Print($"[game-ui] '{name}' menu: {described}");
	}

	/// <summary>Takes one drawn presentation off screen and forgets it.</summary>
	private void Retire(uint address, string why)
	{
		if (!_drawn.TryGetValue(address, out Drawn? drawn))
		{
			return;
		}

		GD.Print($"[game-ui] retired '{drawn.Name}' — {why}");
		drawn.Node.QueueFree();
		_drawn.Remove(address);

		// So a presentation that comes back reports its movement again rather than staying
		// silent because a previous incarnation at the same address already had.
		_animated.Remove(address);
		_stilled.Remove(address);
		_movieSlots.Remove(address);
		_mismatched.Remove(address);
		_valued.Remove(address);
		_menuContents.Remove(address);
		_suppressed.Remove(address);
	}

	/// <summary>
	/// Takes every drawn presentation down.
	///
	/// Needed wherever ownership of the screen changes hands, not just where the guest drops a
	/// presentation — otherwise a screen we were drawing outlives the thing that wanted it.
	/// </summary>
	private void RetireDrawn(string why)
	{
		if (_drawn.Count == 0)
		{
			return;
		}

		foreach (uint address in new System.Collections.Generic.List<uint>(_drawn.Keys))
		{
			Retire(address, why);
		}
	}

	/// <summary>
	/// Draws the front-end presentations the by-name path does not cover.
	///
	/// The by-name path knows two screens — the title and the main menu — because those are the
	/// two the guest names in its own constructor. It has never known about
	/// <c>scrollingbackground</c>, which the guest reports as SHOWING while the menu is up, and
	/// that is the grey void behind the menu: not a missing texture, a whole presentation
	/// nobody was drawing.
	///
	/// This is a step toward the by-name path going away entirely. It is NOT that step: the
	/// gate reports the title screen hidden for stretches while it is plainly on screen (the
	/// attract movie is up), so switching the active screen over to the gate wholesale would
	/// blank the front end. Adding what the gate says is showing, on top of what we already
	/// draw, is the half of that change which cannot regress.
	///
	/// Drawn BEHIND, at negative depth. These are backgrounds by definition — the by-name
	/// screen is the one the player is interacting with — and the front-end screen supplies the
	/// camera, so nothing here activates one.
	/// </summary>
	private void FollowFrontEndExtras(System.Collections.Generic.List<HostBridge.Presentation> live)
	{
		if (_presentation is null)
		{
			return;
		}

		var wanted = new System.Collections.Generic.List<HostBridge.Presentation>();
		foreach (HostBridge.Presentation p in live)
		{
			if (!p.IsShowing)
			{
				continue;
			}

			// Skip whatever the by-name path is already drawing, or it would be built twice.
			string bank = System.IO.Path.GetFileNameWithoutExtension(p.Name.Replace('\\', '/'));
			if (string.Equals(bank, _showing, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			// AND SKIP THE OTHER FRONT-END SCREEN, which is not an extra at all.
			//
			// There are exactly two — `frontendstartupscreen` and `frontendmainmenu` — and the
			// guest tells us which is ACTIVE; the by-name path draws that one and this path must
			// not draw the other. Its root keeps its active bit set after the hand-over, so
			// "the guest is showing it" is true of both, and drawing both puts the title
			// screen's "Press A" prompt over the menu. That is the stray A glyph floating in the
			// corner of every front-end screenshot: not a widget of ours, the whole title screen
			// underneath one.
			//
			// Derived from our own contract rather than a name list: `PresentationFor` is the
			// one place that enumerates the front end's screens, so a third would be covered
			// here the day it is added.
			if (IsFrontEndScreen(bank))
			{
				continue;
			}

			wanted.Add(p);
		}

		var gone = new System.Collections.Generic.List<uint>();
		foreach (uint address in _drawn.Keys)
		{
			if (!wanted.Exists(p => p.Address == address))
			{
				gone.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			Retire(address, "the guest stopped showing it");
		}

		foreach (HostBridge.Presentation p in wanted)
		{
			Follow(p, -1);
		}
	}

	/// <summary>
	/// Draws the in-game UI the guest currently has live.
	///
	/// SAME PATH AS THE FRONT END, which is the point. The HUD is a Gameface presentation like
	/// every front-end screen — same factory, same element tree, same root at +500 — so it goes
	/// through the presentation host and the scene builder that already exist rather than
	/// through a second renderer. What the HUD needed was not drawing but FINDING: a walk that
	/// does not go through a field only the front-end GUI object has.
	/// </summary>
	private void FollowInGameUi(System.Collections.Generic.List<HostBridge.Presentation> live)
	{
		if (_presentation is null)
		{
			return;
		}

		// WHAT THE GUEST SAYS IS SHOWING, not a list we keep.
		//
		// A hardcoded set of file names is the wrong shape twice over: it draws closed menus
		// (every live screen looks drawable) and it can never show a screen nobody thought to
		// add. The guest states this — a presentation is drawn
		// exactly when its ROOT element's `active` bit is set, which is the test its own update
		// makes before walking the tree at all — so following that needs no host-side list and
		// covers all thirty in-game screens for free.
		var wanted = new System.Collections.Generic.List<HostBridge.Presentation>();
		foreach (HostBridge.Presentation p in live)
		{
			if (!p.IsShowing)
			{
				continue;
			}

			// A SCREEN CONTRIBUTING NO ELEMENTS IS NOT ON SCREEN, and the guest counts them
			// itself — so this is its statement, not our inference.
			//
			// The pause menu is why. Its presentation is created at game entry (`sub_823DC888`,
			// once, behind the one-shot byte at GUI+102) and never freed, so from the first
			// frame of play it sits in the registry with its root active. The registry read
			// from the run this was written against says exactly what it is:
			//
			//   SHOWING active=2616  hud.bgf           <- really drawing
			//   SHOWING active=52    intermissionbox   <- really drawing
			//   SHOWING active=0     pausemainmenu     <- root active, drawing NOTHING
			//   hidden  active=1878  pausemainmenu FROZEN
			//
			// Drawing that third one is what put a full-screen purple backdrop and a stack of
			// 26 blank tabs over the running game: the guest contributes nothing, but we
			// rebuild the screen from the FILE, where those elements are active and stacked.
			//
			// It costs nothing elsewhere. The one other screen seen at zero is the HUD on the
			// first frame after it stands up, where our own build produced `quads 0` anyway —
			// so this skips an empty tree we were building for no reason and picks the HUD up
			// on the next sample.
			//
			// This replaced a gate on the guest's current-screen ID. That one was sound in
			// principle and never fired: the screen manager's slot pointers did not match the
			// addresses the presentation registry reports, so the test could not match. The
			// element count needs no pointer identity at all.
			if (p.ActiveElements == 0)
			{
				if (_suppressed.Add(p.Address))
				{
					GD.Print($"[game-ui] not drawing '{p.Name}' — its root is active but the " +
						"guest has NO globally-active elements in it, so it is contributing " +
						"nothing to the frame");
				}

				continue;
			}

			_suppressed.Remove(p.Address);
			wanted.Add(p);
		}

		// The HUD underneath, everything else over it. Depth is not something the guest states
		// in a form we have read, so this is ours — and it is the one ordering that cannot put
		// a menu behind the health bar.
		wanted.Sort((a, b) =>
			(a.Is("hud.bgf") ? 0 : 1).CompareTo(b.Is("hud.bgf") ? 0 : 1));

		HasInGameUi = wanted.Count > 0;

		// Retire anything the guest has taken away.
		var gone = new System.Collections.Generic.List<uint>();
		foreach (uint address in _drawn.Keys)
		{
			if (!wanted.Exists(p => p.Address == address))
			{
				gone.Add(address);
			}
		}

		foreach (uint address in gone)
		{
			Retire(address, "the guest stopped showing it");
		}

		if (wanted.Count > 0)
		{
			// NOTHING FROM THE FRONT END BELONGS OVER THE RUNNING GAME. `LeaveFrontEnd` frees
			// all three when the guest leaves properly; this is the belt to that braces, for the
			// case where in-game UI appears without that handover having been seen — which is
			// how the load fan ended up drawn over the world in the first place.
			if (_menuView is not null)
			{
				_menuView.Visible = false;
			}

			if (_cardView is not null)
			{
				_cardView.Visible = false;
			}

			if (_saveCardView is not null)
			{
				_saveCardView.Visible = false;
			}
		}

		for (int depth = 0; depth < wanted.Count; ++depth)
		{
			Follow(wanted[depth], depth);
		}
	}

	/// <summary>Builds or refreshes one live presentation at a given depth.</summary>
	private void Follow(HostBridge.Presentation presentation, int depth)
	{
		// The bank entry name: the guest's string is a full guest path, e.g.
		// `GAME:\data\art\gui\gameface\hud.bgf`, with backslashes that mean nothing here.
		string name = System.IO.Path.GetFileNameWithoutExtension(
			presentation.Name.Replace('\\', '/'));

		// This presentation's OWN element state, keyed by its address — the whole point of the
		// registry. Reading the front end's would put one screen's slides on another's widgets.
		System.Collections.Generic.List<HostBridge.UiElement>? elements =
			HostBridge.GetUiElements(presentation.Address);
		if (elements is not { Count: > 0 })
		{
			return;
		}

		var slides = new int[elements.Count];
		var texts = new string?[elements.Count];
		var visible = new bool[elements.Count];
		for (int i = 0; i < elements.Count; ++i)
		{
			slides[i] = elements[i].Slide;
			texts[i] = elements[i].Text;
			visible[i] = elements[i].IsGloballyActive;
		}

		// LIVE OPACITY, passed rather than dropped.
		//
		// It matters more in game than in the front end: `hud.lua` binds Enable/Disable as a
		// single `setAttribute(Scene.Layer, 'opacity', 100 | 0)`, so the game's own way of
		// taking the HUD off screen — a cutscene, a loading screen — is one group going to
		// zero. Ignoring it leaves the HUD painted over those. The builder multiplies it down
		// the tree, which is what makes one group's zero reach every element beneath it.
		//
		// Read BEFORE the unchanged test below, because it is part of that test: a HUD fading
		// out changes no slide and no visibility, so comparing only those would never notice.
		float?[]? opacity = null;
		Engine_UiScreen.LiveTint?[]? tint = null;
		HostBridge.LiveAttribute[,]? attributes =
			HostBridge.GetUiAttributes(OpacityIds, presentation.Address);
		if (attributes is not null)
		{
			int count = System.Math.Min(attributes.GetLength(0), elements.Count);
			opacity = new float?[count];
			tint = new Engine_UiScreen.LiveTint?[count];
			for (int i = 0; i < count; ++i)
			{
				opacity[i] = attributes[i, 0].Present ? attributes[i, 0].AsFloat : null;
				tint[i] = ReadTint(attributes, i);
			}
		}

		// THE GUEST'S OWN TEXTURE PER ELEMENT, read here rather than after the test below
		// BECAUSE IT IS PART OF THAT TEST. Anywhere the game chooses a picture at runtime the
		// file carries only a placeholder — every dpad quick-slot points at the same grey
		// `empty.tex`, all twenty-four HUD world-icon slots at the same icon — so following the
		// file alone draws one wrong image in every slot of a set. Equipping an item changes
		// the slot's texture and NOTHING else: no slide, no visibility, no opacity. Left out of
		// the comparison the swap would be read once and then never again.
		uint[]? textures = HostBridge.GetUiTextureHashes(presentation.Address, elements.Count);

        bool unchanged = _drawn.TryGetValue(presentation.Address, out Drawn? existing) &&
			existing.Slides is not null && existing.Visible is not null &&
			slides.SequenceEqual(existing.Slides) && visible.SequenceEqual(existing.Visible) &&
			((existing.Opacity is null && opacity is null) ||
			 (existing.Opacity is not null && opacity is not null &&
			  opacity.SequenceEqual(existing.Opacity))) &&
			((existing.Textures is null && textures is null) ||
			 (existing.Textures is not null && textures is not null &&
			  textures.SequenceEqual(existing.Textures))) &&
			((existing.Tint is null && tint is null) ||
			 (existing.Tint is not null && tint is not null &&
			  tint.SequenceEqual(existing.Tint)));
		if (unchanged)
		{
			return;
		}

		_presentation!.LiveSlides = slides;
		_presentation.LiveText = texts;
		_presentation.LiveVisible = visible;
		_presentation.LiveOpacity = opacity;
		_presentation.LiveTint = tint;
		float?[]?[]? transform = ReadLiveTransform(presentation.Address, elements.Count);
		_presentation.LiveTransform = transform;

		_presentation.LiveTextures = textures;
		_presentation.SceneSlide = 0;

		Node3D? built = _presentation.BuildDetached(
			name, out Engine_UiScreen? screen, out Engine_UiSceneBuilder? stats);
		if (built is null)
		{
			GD.PushWarning($"[game-ui] the guest has '{presentation.Name}' live but '{name}' " +
				"is not among the shipped presentations");
			return;
		}

		// A HALF-BUILT PRESENTATION IS NOT DRAWN.
		//
		// The guest populates an element tree over several frames, and while it is doing so the
		// registry already reports the presentation — `frontendmainmenu` shows up with 1023,
		// then 1402, against the file's 3812. Every piece of live state we apply is keyed by
		// ORDINAL, so a tree of the wrong length does not give a partial screen: it gives every
		// element after the first divergence someone else's slide, text and transform. Waiting
		// costs a frame or two of a screen the guest is not finished with anyway.
		if (screen is not null && screen.Nodes.Length != elements.Count)
		{
			if (_mismatched.Add(presentation.Address))
			{
				GD.Print($"[game-ui] '{name}' is still being built — file {screen.Nodes.Length} " +
					$"elements, guest {elements.Count}; not drawing it until they agree");
			}

			built.QueueFree();
			return;
		}

		if (existing is not null)
		{
			existing.Node.QueueFree();
			_drawn.Remove(presentation.Address);
		}
		else
		{
			// The build counts, because an unresolved TEXTURE still builds a quad — it just
			// draws with no albedo, which is a white rectangle. A screen that covers the title
			// in white and a screen that is correct look the same from "it built".
			string art = stats is null
				? string.Empty
				: $", quads {stats.QuadsBuilt} textures {stats.TexturesResolved}" +
				  $"{(stats.TexturesResolved < stats.QuadsBuilt ? " — TEXTURES MISSING, those quads draw untextured" : string.Empty)}" +
				  $", text {stats.TextsBuilt} ({stats.TextGlyphsBuilt} glyphs)";

			// The texture-swap breakdown is here rather than in its own line because it is only
			// meaningful beside the quad count. It reports STATED as well as swapped: a plain
			// "0 swapped" cannot tell "the guest names no textures" from "it names exactly what
			// the file already has" from "it names textures we cannot resolve", and those need
			// different work. Empty when the guest states none, which is most screens.
			string swaps = screen?.DescribeLiveTextures() ?? string.Empty;

			// THE TINT WEIGHT, reported separately from the swap counts because it answers a
			// different question. Every quad now draws its tint SUBTRACTIVELY at this weight,
			// and the weight is 0 on most authored records — so "the frames look pale" and
			// "the guest never stated a TintingAmount" are the same observation, and a screen
			// that states none needs to be distinguishable from one whose read is broken.
			string tints = screen is null || screen.LiveTintsApplied == 0
				? string.Empty
				: $", {screen.LiveTintsApplied} tint weight(s) stated";

			// AND WHY, when the answer is "none". The chain the host walks is the guest's own,
			// verified against `sub_82425C10` instruction for instruction — element+4 to the
			// scene node, node+0 to the render item, +0x200 for the hash — so an all-zero
			// result is not evidence that the walk is broken. It is equally consistent with the
			// guest building no render items at all in this configuration, which is a question
			// about the GUEST and not about us. This prints the breakdown once per screen so
			// the two stop looking identical.
			if (screen is { LiveTexturesStated: 0 } &&
				HostBridge.GetUiTextureStats(presentation.Address) is { } chain)
			{
				GD.Print($"[game-ui] '{name}' texture chain: {chain.Describe()}");
			}

			GD.Print($"[game-ui] drawing the guest's '{name}' " +
				$"({elements.Count} live elements, " +
				$"{System.Array.FindAll(visible, v => v).Length} of them active{art}{swaps}{tints})");

			// THE ORDINAL JOIN, checked rather than believed — the same check the front end
			// makes, because this path applies slide, text and opacity BY POSITION too.
			//
			// It was verified 77/77 on the title screen and never on anything else. The HUD is
			// 3431 elements; if the guest's depth-first walk diverges from the .bgf's order
			// anywhere, every element past that point wears another element's state. That does
			// not look like a join fault — it looks like a HUD with the wrong numbers on it.
			//
			// Element TYPE is the cheap invariant: the guest holds a kind byte per element with
			// the same numbering as BgfElementType, and it carries no live state, so a mismatch
			// is a join failure and nothing else.
			if (screen is not null)
			{
				int compared = System.Math.Min(screen.Nodes.Length, elements.Count);
				int diverged = -1;
				int mismatches = 0;
				for (int i = 0; i < compared; ++i)
				{
					if ((int)screen.Nodes[i].Type == elements[i].Type)
					{
						continue;
					}

					++mismatches;
					if (diverged < 0)
					{
						diverged = i;
					}
				}

				GD.Print(diverged < 0
					? $"[game-ui] '{name}' ordinal join OK: {compared} elements, types agree end to end"
					: $"[game-ui] '{name}' ORDINAL JOIN BROKEN: {mismatches} of {compared} " +
					  $"element types disagree, first at index {diverged} " +
					  $"(file says {screen.Nodes[diverged].Type}, guest says {elements[diverged].Type})");

				if (screen.Nodes.Length != elements.Count)
				{
					GD.Print($"[game-ui] '{name}' element COUNT differs: file " +
						$"{screen.Nodes.Length}, guest {elements.Count} — the join cannot be " +
						"positional at all");
				}
			}
		}

		ReportMenuContents(presentation.Address, name, screen, texts, visible, slides, transform);

		// Depth by Z rather than by child order: these share one orthographic canvas camera
		// looking down -Z, so tree order does not decide what covers what.
		//
		// A WHOLE SLAB PER PRESENTATION, not half a unit. A screen is not flat — `frontendmainmenu`
		// spans z -5..171, a hundred and seventy-six units deep — so stacking screens 0.5 apart
		// does not order them at all: the one nominally behind sits half a unit back while the
		// one in front reaches a hundred and seventy units forward, and the two interleave
		// element by element. Everything here is alpha-blended and writes no depth, so what
		// covers what is decided purely by that sort. The step has to exceed the deepest screen
		// by a comfortable margin, and 1000 does with the whole set well inside the camera's
		// 100000 far plane.
		built.Position = new Vector3(0f, 0f, depth * PresentationDepthStep);
		_presentation.AddChild(built);

		// AFTER the attach, which is the contract `ApplyCameraAiming` states and the one
		// `BuildDetached` cannot keep: the aim rides through the camera's parent GLOBAL
		// transform, and a detached tree has none. Doing it here is what makes an in-game
		// presentation's cameras aim at the same points the viewer's do.
		Engine_UiSceneBuilder.ApplyCameraAiming(built);

		// THE LIVE-VALUE BINDINGS, resolved once per build rather than per frame.
		//
		// This is what makes the HUD more than a picture. Its numbers are not in the element
		// tree and not in any script: `hud.bgf` binds an element with `MeterName = "PlayerHealth"`
		// and the guest publishes a percentage against a hash of that string every frame. Reading
		// the binding here — while the document and the ordinal map are both in hand — is what
		// lets the per-frame path be a dictionary lookup and a transform write.
		//
		// Only for a screen that HAS bindings, which is the HUD; the front-end screens carry
		// none and get a null, which costs nothing per frame.
		Engine_UiLiveValues? values = null;
		if (screen is not null && stats is not null)
		{
			Engine_UiBindings bindings = Engine_UiBindings.Read(screen.Document);
			if (bindings.All.Count > 0)
			{
				values = new Engine_UiLiveValues
				{
					// The same theme the front end draws with — the HUD's live text has to be
					// laid out in the game's own fonts, and without it a bound field simply
					// keeps whatever the file left in it.
					Theme = _presentation.Theme,
				};

				values.Bind(screen.Document, bindings, stats.ByOrdinal);
				GD.Print($"[game-ui] '{name}' {bindings.Describe()} — {values.BarsBound} bar(s) " +
					$"and {values.FieldsBound} text field(s) bound to nodes, " +
					$"{values.BarsUnresolved} not in the built tree");
			}
		}

		// THE WIDE LAYOUT, read once per build. The game ships two layouts per element and the
		// authored one is 4:3; without this the HUD draws in its standard-definition arrangement
		// on a 16:9 canvas, which is why the ABXY prompts sat in the middle of the screen.
		Engine_UiWidescreen? widescreen = null;
		if (screen is not null)
		{
			widescreen = Engine_UiWidescreen.Read(screen.Document);
			if (widescreen.Count > 0)
			{
				GD.Print($"[game-ui] '{name}' {widescreen.Describe()}");
			}
			else
			{
				widescreen = null;
			}

			// THE NAMED FIXUPS, ONCE PER BUILD AND NEVER PER FRAME. One of the three divides the
			// vignette's existing scale by 0.75 rather than assigning, so repeating it grows the
			// border by a third every frame. A rebuild re-reads the artist's value from the file,
			// which is what makes once-per-build the same thing as the original's once-per-init.
			if (_isWidescreen)
			{
				Engine_UiWidescreen.ApplyNamedFixups(built, out string fixups);
				if (fixups.Length > 0)
				{
					GD.Print($"[game-ui] '{name}' {fixups}");
				}
			}
		}

		_drawn[presentation.Address] = new Drawn
		{
			Name = name,
			Node = built,
			Slides = slides,
			Visible = visible,
			Opacity = opacity,
			Textures = textures,
			Tint = tint,
			Builder = stats,
			ElementCount = elements.Count,
			Values = values,
			Widescreen = widescreen,
		};

		// A camera has to be current or the sub-viewport renders through Godot's default one
		// and the whole layer is empty. The front end's own screen supplies it while the front
		// end is up; in game these are the only screens there are, so the first one does.
		if (depth == 0 && screen is not null)
		{
			Engine_UiSceneBuilder.ActivateCamera(built);
		}
	}

	private string? _presentationSignature;

	/// <summary>
	/// The guest's last stated in-game screen, so the line fires on a change rather than
	/// every sample. Null is a distinct value here — "no screen manager" is not
	/// <see cref="HostBridge.InGameScreen.None"/>.
	/// </summary>
	private HostBridge.InGameScreen? _inGameScreen;

	/// <summary>Frames between registry samples — a few times a second at any display rate.</summary>
	private const int PresentationSampleFrames = 30;

	private int _presentationTick;

	/// <summary>Puts the UI on screen. Idempotent.</summary>
	public void Reveal()
	{
		if (IsRevealed || _container is null || _viewport is null)
		{
			return;
		}

		IsRevealed = true;
		_container.Visible = true;

		// Always while it is up, not Once-per-rebuild. A menu is static between guest state
		// changes, so redrawing on demand is tempting — but a window resize reallocates the
		// render target and an on-demand viewport has nothing to put back in it, so the front
		// end blanks until the guest happens to change something. 77 unshaded quads a frame is
		// not worth that.
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

		// _showing, not Screen: the export is the STARTING presentation and never changes, so
		// naming it here reports the title screen long after the guest has moved on.
		GD.Print($"[game-ui] revealed '{_showing ?? Screen}'");
	}

	/// <summary>Takes the UI back off screen — a movie playing over the front end.</summary>
	public void Conceal()
	{
		if (!IsRevealed || _container is null || _viewport is null)
		{
			return;
		}

		IsRevealed = false;
		_container.Visible = false;

		// The render target goes idle too. Hiding the container alone leaves the front end
		// being drawn into a texture nothing samples.
		_viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
	}
}
