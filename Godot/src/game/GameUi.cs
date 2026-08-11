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

		_presentation.LiveOpacity = opacity;
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
		AdoptWarning(theme);

		// The guest's own rows, out of the way. Hidden rather than freed: the tree is rebuilt
		// from the file on the next state change anyway, and freeing a node the theme's
		// catalogue still points at would leave a dangling entry behind.
		if (group is not null)
		{
			group.Visible = false;
		}

		// Not while the card is up. This runs after EVERY rebuild, and the guest keeps changing
		// state under a front end it no longer drives, so an unconditional show here would put
		// the rows back across the raised card mid-choice.
		_menuView.Visible = _cardView is not { Visible: true };
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

		if (!_menuView.Visible)
		{
			return;
		}

		// THE LOAD CAROUSEL OWNS THE PRESS ONCE THE GUEST HAS OPENED IT.
		//
		// Choosing "Load Game" puts the guest's screen into substate 5 — its own save carousel —
		// and it keeps receiving input there, because the mask only covers the root list. The
		// run shows the consequence exactly: its cursor walked 0..5 and back while every A press
		// re-dispatched "Load Game", because this class still believed the root list was up. The
		// load could never start; the entry just re-opened the screen it was already on.
		//
		// THE SELECTED INDEX IS THE GUEST'S, and taking it is the same rule the rest of the front
		// end follows: it publishes the carousel's position at `screen+72` and we read it rather
		// than keeping a second cursor that would have to be kept in step with the one actually
		// moving the cards.
		if (DriveLoadCarousel())
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
	/// One frame of the guest's load carousel. True when it consumed the input.
	///
	/// SUBSTATE 5 IS THE CAROUSEL — `screen+24`, set at 0x826E07E0 when the Load Game branch
	/// finds more than one save. While it is up, A must commit the SELECTED save rather than
	/// re-dispatch the menu entry, and that is the whole bug this fixes.
	///
	/// B is left to the guest. Its own handler maps B in this substate to UNFREEZE, which puts
	/// `screen+24` back to 0 — and it is already being fed the press, since the mask covers only
	/// the root list. Posting a second back-out here would drive the same transition twice.
	/// </summary>
	private bool DriveLoadCarousel()
	{
		if (HostBridge.GetFrontEndModel() is not { } model || model.MenuState != LoadCarouselState)
		{
			return false;
		}

		if (GameInput.MenuAcceptPressed)
		{
			int selected = (int)model.LoadCardCursor;
			uint queued = HostBridge.LoadSave(selected);
			GD.Print($"[game-ui] load carousel: save {selected} chosen — posted " +
				$"(queue depth {queued})");
		}

		// Consumed either way. A press that is not A still belongs to the carousel, and letting
		// it fall through would move the root list's highlight underneath the cards.
		return true;
	}

	/// <summary>
	/// `screen+24` while the guest's save carousel is up — set at 0x826E07E0 for two or more
	/// saves. The one-save case never reaches it; that path commits directly at 0x826E0470.
	/// </summary>
	private const uint LoadCarouselState = 5;

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
	};

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
		/// The builder that produced <see cref="Node"/>, kept for its ordinal map.
		///
		/// A transform is the one piece of live state that must NOT trigger a rebuild — the
		/// guest moves these every frame — so it is written straight onto the built nodes
		/// instead. See <see cref="Engine_UiSceneBuilder.ApplyLiveTransform"/>.
		/// </summary>
		public Engine_UiSceneBuilder? Builder { get; set; }

		/// <summary>How many elements this presentation carries, for the per-frame read.</summary>
		public int ElementCount { get; set; }
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

		foreach ((uint address, Drawn drawn) in _drawn)
		{
			if (drawn.Builder is null || drawn.ElementCount == 0)
			{
				continue;
			}

			if (ReadLiveTransform(address, drawn.ElementCount) is not { } transform)
			{
				continue;
			}

			int moved = drawn.Builder.ApplyLiveTransform(transform);
			if (moved > 0 && _animated.Add(address))
			{
				GD.Print($"[game-ui] '{drawn.Name}' is animating — {moved} element(s) moved " +
					"on the first frame it did");
			}

			ReportScroll(drawn, moved);
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

			var material = (mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)) as BaseMaterial3D;
			if (material is null || material.AlbedoColor.A <= 0.01f)
			{
				return;
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			// Only what crosses the canvas centreline, which is where the stripe was measured.
			if (box.Position.Y > 0f || box.End.Y < 0f)
			{
				return;
			}

			// AN UNTEXTURED QUAD IS NOT SKIPPED, it is NAMED. A StandardMaterial3D with no
			// albedo texture paints its albedo COLOUR across the whole quad — which for the
			// default white is a solid white plate. That is the single most likely cause of a
			// screen going white, so a scanline that filtered it out would have dutifully
			// reported whatever was behind the very thing being looked for.
			string texture = material.AlbedoTexture is { } albedo
				? albedo.ResourcePath.GetFile()
				: "NO-TEXTURE";
			Color tint = material.AlbedoColor;

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

			var material = (mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)) as BaseMaterial3D;
			Aabb box = mesh.GlobalTransform * mesh.GetAabb();

			found[name] =
				$"x[{box.Position.X:F0}..{box.End.X:F0}] y[{box.Position.Y:F0}..{box.End.Y:F0}] " +
				$"a{material?.AlbedoColor.A ?? 0f:F2}";
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
		if (moved == 0 || drawn.Builder is null || ++_scrollTick % ScrollSampleFrames != 0)
		{
			return;
		}

		var report = new System.Text.StringBuilder($"[game-ui] '{drawn.Name}' moving:");
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

			// The builder puts the quad's material on the override slot; the surface slot is the
			// fallback for anything cloned or re-materialised since.
			var material = (mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)) as BaseMaterial3D;
			if (material is null)
			{
				++untextured;
				continue;
			}

			if (material.AlbedoColor.A <= 0.01f)
			{
				++transparent;
				continue;
			}

			// COUNTED AS PAINTING, and flagged. A quad with no albedo texture is not silent —
			// the material paints its albedo colour over the whole quad, so it covers its span
			// with a flat plate. Filing it under "not drawing" was backwards, and that is the
			// same mistake in miniature as counting geometry instead of pixels.
			if (material.AlbedoTexture is null)
			{
				++untextured;
			}

			Aabb box = mesh.GlobalTransform * mesh.GetAabb();
			spans.Add((box.Position.X, box.End.X));
		}

		string silent = $" ({hidden} hidden, {transparent} transparent, " +
			$"{untextured} PAINTING UNTEXTURED)";

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

	/// <summary>Presentations reported as still being built, so that line fires once each too.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _mismatched = new();

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
		_mismatched.Remove(address);
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
			if (p.IsShowing)
			{
				wanted.Add(p);
			}
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

		if (wanted.Count > 0 && _menuView is not null && _menuView.Visible)
		{
			// The front end's menu has no business over the running game — nor does the card,
			// which by this point has already been answered.
			_menuView.Visible = false;
			if (_cardView is not null)
			{
				_cardView.Visible = false;
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
		HostBridge.LiveAttribute[,]? attributes =
			HostBridge.GetUiAttributes(OpacityIds, presentation.Address);
		if (attributes is not null)
		{
			opacity = new float?[System.Math.Min(attributes.GetLength(0), elements.Count)];
			for (int i = 0; i < opacity.Length; ++i)
			{
				opacity[i] = attributes[i, 0].Present ? attributes[i, 0].AsFloat : null;
			}
		}

		bool unchanged = _drawn.TryGetValue(presentation.Address, out Drawn? existing) &&
			existing.Slides is not null && existing.Visible is not null &&
			slides.SequenceEqual(existing.Slides) && visible.SequenceEqual(existing.Visible) &&
			((existing.Opacity is null && opacity is null) ||
			 (existing.Opacity is not null && opacity is not null &&
			  opacity.SequenceEqual(existing.Opacity)));
		if (unchanged)
		{
			return;
		}

		_presentation!.LiveSlides = slides;
		_presentation.LiveText = texts;
		_presentation.LiveVisible = visible;
		_presentation.LiveOpacity = opacity;
		_presentation.LiveTransform = ReadLiveTransform(presentation.Address, elements.Count);
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

			GD.Print($"[game-ui] drawing the guest's '{name}' " +
				$"({elements.Count} live elements, " +
				$"{System.Array.FindAll(visible, v => v).Length} of them active{art})");

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
		_drawn[presentation.Address] = new Drawn
		{
			Name = name,
			Node = built,
			Slides = slides,
			Visible = visible,
			Opacity = opacity,
			Builder = stats,
			ElementCount = elements.Count,
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
