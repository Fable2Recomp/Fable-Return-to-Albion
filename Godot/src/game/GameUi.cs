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
		string? presentation = PresentationFor(screen);
		if (presentation is null || _presentation is null)
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

		Node3D? group = theme.ArtSourceNode(Engine_FrontEndMenuView.RowGroupName);
		Node3D? template = group is null ? null : Engine_FrontEndMenuView.RowTemplateIn(group);

		if (_menuView is null)
		{
			_menu = Engine_FrontEndMenu.BuildRoot();
			_menuView = new Engine_FrontEndMenuView
			{
				Name = "FrontEndMenu",
				Theme = theme,
				RowTemplate = template,
				// The shipped highlight art, if this slide carries it. UpdateCursorColor only
				// ever changes its opacity — the scripts never move it — so cloning it and
				// parking it on the pinned slot is what the original does.
				CursorTemplate = theme.Art("MenuHighlight"),
			};

			// Parented to the HOST rather than to the built screen, because the built screen is
			// destroyed and recreated on every guest state change and the menu must outlive
			// that. The host's transform is identity and the builder gives the screen root an
			// identity transform too, so both sit in the same authored canvas space.
			_presentation.AddChild(_menuView);
			_menuView.Bind(_menu);

			GD.Print($"[game-ui] Godot owns the main menu now — " +
				$"{_menu.Visible.Count} entries, " +
				$"row art {(template is null ? "NOT sourced (text-only rows)" : "cloned from the presentation")}, " +
				$"cursor {(_menuView.CursorTemplate is null ? "stand-in" : "shipped")}");
		}

		AdoptCardChoice(theme);

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

		if (_cardView is null)
		{
			_cardView = Engine_FrontEndCardChoice.FromPresentation(group);
			_presentation!.AddChild(_cardView);

			GD.Print($"[game-ui] gender card ready — art " +
				$"{(_cardView.HasShippedArt ? "cloned from 'choosecard'" : "NOT sourced (stand-ins)")}" +
				$"{(group is null ? ", group ABSENT from this build" : string.Empty)}");
			return;
		}

		// Re-hidden on every rebuild, because the tree it lives in is rebuilt from the file on
		// every guest state change and comes back visible.
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

		// THE GUEST'S OWN COUNT, not a hardcoded zero.
		//
		// This passed 0 with a comment about the host owning storage, and the effect was that
		// "Load Game" could never appear no matter how many saves existed. The guest states
		// this: it keeps the enumerated saves in a vector and its own Load Game branch tests
		// exactly that span before offering the entry, so reading it is following the same
		// rule rather than inventing one.
		_menu.ApplyAvailability(model, saveCount: (int)model.EnumeratedSaves);
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

		if (_menu is null || _menuView is null || !IsRevealed)
		{
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

		_menuView.Navigate(GameInput.MenuDirection, delta);

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
	private void DriveCardChoice()
	{
		if (_cardView is null)
		{
			return;
		}

		_cardView.Navigate(GameInput.MenuHorizontal);

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
	}

	private readonly System.Collections.Generic.Dictionary<uint, Drawn> _drawn = new();

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
		built.Position = new Vector3(0f, 0f, depth * 0.5f);
		_presentation.AddChild(built);
		_drawn[presentation.Address] = new Drawn
		{
			Name = name,
			Node = built,
			Slides = slides,
			Visible = visible,
			Opacity = opacity,
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
