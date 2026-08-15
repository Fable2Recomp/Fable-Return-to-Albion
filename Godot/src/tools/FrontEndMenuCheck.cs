using Godot;

using Refii.Host;
using Refii.Ui;

namespace Refii.Launcher.Tools;

/// <summary>
/// Drives <see cref="Engine_FrontEndMenuView"/> from the keyboard, with no guest.
///
/// The layout and the navigation FEEL are the two things this exists to expose, and neither
/// is checkable from a static screenshot of the running game. The slot table bows the column
/// toward the highlight and fades the two end slots to nothing; the auto-repeat starts at a
/// quarter second and accelerates to 0.14. Both are read out of shipped Lua rather than
/// chosen, so both are things we can get subtly wrong and not notice — a fixed repeat rate
/// and a straight column both look plausible in a still.
///
/// It subclasses <see cref="Engine_UiPresentationHost"/> for the banks, the fonts and the
/// texture cache. That is the whole reason: mounting the game data, finding
/// <c>language/en-uk/fonts</c> and registering the <c>game://</c> texture resolver are the
/// same work whether a screen or a menu is being stood up, and doing it again here would be
/// the second implementation the engine rules forbid.
///
/// Keys:
/// <list type="bullet">
/// <item>Up / Down — navigate, with the guest's own repeat curve</item>
/// <item>Enter or A — activate; on "Subtitles" that expands the submenu, on "New Game" it
/// opens the gender card</item>
/// <item>Left / Right — while the gender card is up, choose the boy or the girl</item>
/// <item>Escape or B — collapse an expanded submenu, or cancel the gender card</item>
/// <item>1..4 — toggle a root entry's availability, so the list can be watched changing
/// under the highlight</item>
/// </list>
///
/// <c>REFII_MENU_SCREEN</c> names a presentation to stand up behind the menu — set it to
/// <c>frontendmainmenu</c> to see the rows against the real front end and to have the row
/// frame art AND the boy/girl cards cloned out of it. Unset, the view draws labels only and
/// the card draws stand-ins, which is the honest picture of what they can source on their own.
///
/// <c>REFII_MENU_OPEN=card</c> opens the gender card straight away and
/// <c>REFII_MENU_SHOT=&lt;path&gt;</c> writes the viewport and quits, so the panel's layout can be
/// looked at without a guest and without a human at the keyboard. The path must be outside
/// the repository — a render of the game's own card art is derived data.
/// </summary>
public partial class FrontEndMenuCheck : Engine_UiPresentationHost
{
	private Engine_FrontEndMenu? _menu;
	private Engine_FrontEndMenuView? _view;
	private Engine_FrontEndCardChoice? _cards;
	private Engine_FrontEndSaveCards? _saveCards;
	private bool _started;

	public override void _Ready()
	{
		// The base mounts the banks and calls OnLoaded on success. On failure it reports and
		// returns, and the menu still has something to show — the layout does not need game
		// data, only the labels do — so the harness starts either way.
		base._Ready();
		Start();
	}

	protected override void OnLoaded() => Start();

	private void Start()
	{
		if (_started)
		{
			return;
		}
		_started = true;

		string backdrop = System.Environment.GetEnvironmentVariable("REFII_MENU_SCREEN") ?? string.Empty;
		Node3D? template = null;
		if (backdrop.Length > 0 && ShowByName(backdrop) && Built is not null)
		{
			template = Engine_FrontEndMenuView.FindRowTemplate(Built);
			GD.Print(template is null
				? $"[menu-check] '{backdrop}' is up but carries no 'Buttons' component group — rows will be text only"
				: $"[menu-check] cloning row art from '{template.Name}' under {backdrop}");

			// THE SHIPPED ROWS GO AWAY, as they do in the game — GameUi.AdoptMenu hides this
			// same group after every rebuild. Without it the harness draws two menus: ours, and
			// the twelve components the presentation ships with their authored placeholder
			// labels ("DEFAULT_TEXT_ID") sitting across ours. That is a harness that disagrees
			// with the thing it exists to check.
			if (Engine_UiTheme.FindByName(Built, Engine_FrontEndMenuView.RowGroupName)
				is Node3D shipped)
			{
				shipped.Visible = false;
			}

			// WHERE THE FILE PUTS THE MENU, in depth. The view anchors itself at z 0, which is
			// a number nobody read — and it decides whether the rows are drawn over or under
			// the gender card, whose own poses reach z -134.
			for (Node? n = template; n is Node3D s; n = n.GetParent())
			{
				GD.Print($"[menu-check]   menu chain {s.Name} z={s.Position.Z:F1} worldZ={s.GlobalPosition.Z:F1}");
			}
		}
		else
		{
			// No backdrop means no screen was built, and the camera comes with the screen.
			Engine_UiSceneBuilder.ActivateCamera(this);
		}

		_menu = BuildMenu();
		_view = new Engine_FrontEndMenuView
		{
			Name = "FrontEndMenuView",
			Theme = Theme,
			RowTemplate = template,
		};
		AddChild(_view);
		_view.Bind(_menu);

		BuildCardChoice();
		BuildSaveCards();

		GD.Print(Theme?.Describe() ?? "[menu-check] no theme — the host never loaded game data");
		GD.Print(_view.Describe());

		// REFII_MENU_OPEN=card puts the gender card up immediately, so a screenshot run does
		// not have to synthesise a keypress to reach it. `card-boy` and `card-girl` then move
		// the choice THROUGH THE VIEW'S OWN NAVIGATION rather than through a setter — the three
		// poses are the thing being checked, and a capture that placed them directly would pass
		// whether or not left and right reach them.
		string open = System.Environment.GetEnvironmentVariable("REFII_MENU_OPEN") ?? string.Empty;
		if (open.StartsWith("card", System.StringComparison.Ordinal))
		{
			OpenCards();

			if (open == "card-boy")
			{
				_cards?.Navigate(-1);
			}
			else if (open == "card-girl")
			{
				_cards?.Navigate(1);
			}

			if (_cards is not null && open != "card")
			{
				GD.Print($"[menu-check] {_cards.Describe()}");
			}
		}
		else if (open == "saves")
		{
			// The fan, straight away. `REFII_MENU_SAVES=<n>` decides how many cards are on it —
			// see BuildSaveCards for why a count is the whole of what a stand-in save needs.
			OpenSaveCards();

			// STEPPED THROUGH THE VIEW'S OWN NAVIGATION rather than set, for the reason the
			// gender card's capture path gives: the raise table is the thing being checked, and a
			// capture that placed the cursor directly would pass whether or not left and right
			// reach it.
			if (int.TryParse(System.Environment.GetEnvironmentVariable("REFII_MENU_SAVE_NAV"),
					out int right))
			{
				for (int i = 0; i < right; ++i)
				{
					// A whole second apart, so the auto-repeat interval has always elapsed and
					// each call is one move rather than a swallowed repeat.
					_saveCards?.Navigate(1, 1.0);
					_saveCards?.Navigate(0, 1.0);
				}

				GD.Print($"[menu-check] stepped right {right}x — {_saveCards?.Describe()}");
			}
		}

		// REFII_MENU_NAV=<n> steps the highlight down n times before the capture, so a still can
		// catch the column MID-SLIDE. The capture waits two frames, which is ~33ms into a 150ms
		// tween — far enough in to see rows between slots, and the only way a screenshot can say
		// anything about an animation at all.
		// REFII_MENU_BACKDROP=0 keeps the presentation mounted as the ART SOURCE and stops it
		// being the picture. With no guest, nothing hides the six pages the screen is not on, so
		// the backdrop draws the load-game cards, the friends page and a full-screen dialog over
		// whatever is being looked at.
		if (System.Environment.GetEnvironmentVariable("REFII_MENU_BACKDROP") == "0" &&
			Built is not null)
		{
			Built.Visible = false;
		}

		if (int.TryParse(System.Environment.GetEnvironmentVariable("REFII_MENU_NAV"), out int steps))
		{
			for (int i = 0; i < steps; ++i)
			{
				// A whole second per step, so the model's auto-repeat interval has always
				// elapsed and each call is one move rather than a swallowed repeat.
				_view.Navigate(1, 1.0);
			}

			GD.Print($"[menu-check] stepped down {steps}x — {_view.Describe()}");
		}

		// REFII_MENU_ACTIVATE=1 presses A after the steps, so a capture can catch an EXPANDED
		// submenu. The child rows are the only place the red tab face and the child indent are
		// visible at all, and neither can be checked from a screenshot of the root list.
		if (System.Environment.GetEnvironmentVariable("REFII_MENU_ACTIVATE") == "1")
		{
			string? dispatched = _view.Activate();
			GD.Print($"[menu-check] activated — {(dispatched ?? "handled here")}\n{_view.Describe()}");
		}

		CaptureIfRequested();
	}

	/// <summary>
	/// Stands the gender card up beside the menu, from whatever the backdrop carries.
	///
	/// Sourced the same way the game sources it — clones out of the built `choosecard` group —
	/// so a stand-in here means the same thing it means in the game: this presentation does not
	/// carry the art, rather than the panel being broken.
	/// </summary>
	private void BuildCardChoice()
	{
		Node3D? group = Built is null ? null : Engine_FrontEndCardChoice.FindGroup(Built);

		// The same factory the game uses, deliberately — see its remarks. A harness that built
		// the panel its own way would be evidence about the harness.
		_cards = Engine_FrontEndCardChoice.FromPresentation(group);
		AddChild(_cards);
		GD.Print($"[menu-check] {_cards.Describe()}" +
			$"{(group is null ? " (no 'choosecard' group in the backdrop)" : string.Empty)}");

		// EACH FACE AND ITS DEPTH. A card is two quads at one place — a portrait and a
		// `cardback` — and which of them the player sees is decided by z, not by anything this
		// view sets. Reading that off the screenshot is guessing at a 0.1-unit difference; the
		// numbers say it outright.
		DumpFaces(_cards.BoyTemplate, "boy");
		DumpFaces(_cards.GirlTemplate, "girl");
	}

	private static void DumpFaces(Node? card, string which)
	{
		if (card is null)
		{
			return;
		}

		void Walk(Node node)
		{
			if (node is Node3D spatial)
			{
				// LOCAL z says how the artist stacked the two faces; GLOBAL z says which one
				// the camera actually sees. They disagree the moment anything up the chain
				// turns the card over, and a card flipped 180 degrees about Y puts its front
				// face behind its back — which looks exactly like a depth-sort fault and is
				// not one.
				string material = spatial is MeshInstance3D m &&
					m.MaterialOverride is StandardMaterial3D s
					? $"alpha={s.AlbedoColor.A:F2} tex={(s.AlbedoTexture is null ? "NONE" : "set")} " +
					  $"prio={s.RenderPriority} vis={m.Visible}"
					: string.Empty;

				GD.Print($"[menu-check]   {which}/{spatial.Name} z={spatial.Position.Z:F3} " +
					$"worldZ={spatial.GlobalPosition.Z:F3} " +
					$"rotY={Mathf.RadToDeg(spatial.Rotation.Y):F1}deg {material} " +
					$"{(spatial is MeshInstance3D mesh && mesh.HasMeta("texture_path") ? (string)mesh.GetMeta("texture_path") : string.Empty)}");
			}

			foreach (Node child in node.GetChildren())
			{
				Walk(child);
			}
		}

		Walk(card);
	}

	/// <summary>
	/// Stands the Load Game fan up beside the menu, from whatever the backdrop carries.
	///
	/// Same factory the game uses, for the same reason the gender card's is shared: a harness
	/// that built the panel its own way would be evidence about the harness.
	/// </summary>
	private void BuildSaveCards()
	{
		Node3D? page = Built is null ? null : Engine_FrontEndSaveCards.FindPage(Built);

		_saveCards = Engine_FrontEndSaveCards.FromPresentation(page);
		_saveCards.Theme = Theme;
		AddChild(_saveCards);

		GD.Print($"[menu-check] {_saveCards.Describe()}" +
			$"{(page is null ? " (no 'loadgame' page in the backdrop)" : string.Empty)}");
	}

	/// <summary>
	/// Puts the fan up with <c>REFII_MENU_SAVES</c> stand-in saves, three by default.
	///
	/// THE SAVES ARE STAND-INS AND SAY SO. With no guest there is nothing to enumerate — the
	/// count comes from a live `XamContentCreateEnumerator` on the guest's side, which is a call
	/// rather than a read — so the descriptions are numbered placeholders. What the harness is
	/// for is the LAYOUT: the fan, the raise, and whether the description text lands under it,
	/// and none of those needs a real save behind it.
	///
	/// THE GAME DOES NOT OPEN IT THIS WAY, and that difference is the harness's, not a shortcut.
	/// There, "Load Game" dispatches to the guest, the guest enumerates and enters substate 5,
	/// and only then does the fan go up — because the enumeration is the guest's to do. Here
	/// there is no substate 5 to wait for, so the keypress stands in for it.
	/// </summary>
	private void OpenSaveCards()
	{
		if (_saveCards is null)
		{
			return;
		}

		int count = int.TryParse(System.Environment.GetEnvironmentVariable("REFII_MENU_SAVES"),
			out int wanted) ? wanted : 3;

		var saves = new System.Collections.Generic.List<Engine_FrontEndSaveCards.Save>();
		for (int i = 0; i < count && i < Engine_FrontEndSaveCards.MaxCards; ++i)
		{
			saves.Add(new Engine_FrontEndSaveCards.Save($"Save {i + 1} — stand-in, no guest"));
		}

		_saveCards.SetSaves(saves);
		_saveCards.Reset();
		_saveCards.Visible = true;

		if (_view is not null)
		{
			_view.Visible = false;
		}

		// THE BACKDROP GOES AWAY WHILE THE FAN IS UP, for exactly the reason OpenCards gives:
		// with no guest nothing hides the pages the presentation is not on, and the cards — which
		// the file parks at z -10 through -100 — end up behind the dialog box and the friends
		// page. The presentation stays mounted as the ART SOURCE and stops being the picture.
		if (Built is not null)
		{
			Built.Visible = false;
		}

		GD.Print($"[menu-check] opened the load fan — {_saveCards.Describe()}");
	}

	/// <summary>Takes the fan down and gives the rows back, as the game does.</summary>
	private void CloseSaveCards()
	{
		if (_saveCards is not null)
		{
			_saveCards.Visible = false;
		}

		if (_view is not null)
		{
			_view.Visible = true;
		}

		if (Built is not null)
		{
			Built.Visible = true;
		}
	}

	private void OpenCards()
	{
		if (_cards is null)
		{
			return;
		}

		_cards.Reset();
		_cards.Visible = true;

		// The rows down while the card is up, as the game does it — see GameUi's remarks for
		// why that is ours rather than the original's.
		if (_view is not null)
		{
			_view.Visible = false;
		}

		// THE BACKDROP GOES AWAY WHILE THE CARD IS UP, and that is not cheating.
		//
		// With no guest, nothing hides the six pages the presentation is NOT on, so the
		// harness draws the load-game cards, the friends page and a full-screen dialog box all
		// at once — and the gender card, which the file parks at z -4 and -134, ends up behind
		// every one of them. In the game that never happens: the guest's globalactive hides the
		// other pages, so the panel is the only thing there.
		//
		// So the presentation stays mounted as the ART SOURCE and stops being the picture. That
		// is the same relationship the game has with it once the card is up, and it is the only
		// way this harness can answer the question it exists for — where the cards sit.
		if (Built is not null)
		{
			Built.Visible = false;
		}

		GD.Print($"[menu-check] opened the gender card — {_cards.Describe()}");
	}

	/// <summary>Takes the card down and gives the rows back, as the game does.</summary>
	private void CloseCards()
	{
		if (_cards is not null)
		{
			_cards.Visible = false;
		}

		if (_view is not null)
		{
			_view.Visible = true;
		}

		if (Built is not null)
		{
			Built.Visible = true;
		}
	}

	/// <summary>
	/// Writes the viewport to <c>REFII_MENU_SHOT</c> and quits.
	///
	/// The layout is the whole point of this harness and it cannot be asserted on: the slot
	/// bow, the card poses and the tint are all things that read correctly in a log and wrongly
	/// on screen. A PNG outside the repository is the only check that catches those.
	/// </summary>
	private async void CaptureIfRequested()
	{
		string? target = System.Environment.GetEnvironmentVariable("REFII_MENU_SHOT");
		if (string.IsNullOrEmpty(target))
		{
			return;
		}

		// Two frames: one to flush the scene change, one to draw it.
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
		await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

		Image image = GetViewport().GetTexture().GetImage();
		Error err = image.SavePng(target!);
		GD.Print(err == Error.Ok ? $"[menu-check] wrote {target}" : $"[menu-check] screenshot failed: {err}");
		GetTree().Quit();
	}

	/// <summary>Stands in for the guest's sign-in state. Toggled with 2.</summary>
	private bool _signedIn;

	/// <summary>Stands in for storage. The model takes the save count from the HOST, not the guest.</summary>
	private int _saveCount;

	/// <summary>Installed languages. "Language" spawns only above one — toggled with 4.</summary>
	private uint _languageCount = 1;

	/// <summary>
	/// The root menu, then availability applied BY HAND through the model's own API.
	///
	/// The starting state is the interesting one rather than the default: signed out with no
	/// saves, so "Load Game" is gone, and one language installed, so "Language" is gone too.
	/// That is what a first boot looks like, and it exercises the part of the model that keeps
	/// the highlight on the ENTRY rather than on the index — press 2 to sign in while the
	/// highlight is below the gap and the selection must not shift.
	/// </summary>
	private Engine_FrontEndMenu BuildMenu()
	{
		Engine_FrontEndMenu menu = Engine_FrontEndMenu.BuildRoot();
		ApplyAvailability(menu);
		menu.Add(BuildCustomScreen());
		return menu;
	}

	/// <summary>
	/// A screen the original never had, to prove the custom-screen path actually works.
	///
	/// This is the thing the theme exists for, and it is here rather than in the game because
	/// a demonstration entry does not belong in the shipped menu. What it demonstrates is
	/// three claims that are otherwise just API:
	///
	/// <list type="bullet">
	/// <item><b>A host-only entry never reaches the guest.</b> <c>HostOnly</c> makes
	/// <see cref="Engine_FrontEndMenu.Activate"/> return null, so the strcmp chain in the
	/// guest's dispatcher — which has never heard of "Extras" — is never given the chance to
	/// fall through it.</item>
	/// <item><b>A submenu REPLACES the list</b> rather than expanding under it, and B returns
	/// to exactly where the player was. Compare with "Subtitles", two rows up, which expands
	/// in place: both behaviours on one screen, which is the only way to see that they differ.
	/// </item>
	/// <item><b>Literal labels work.</b> These strings are not in <c>book.babel</c> and never
	/// will be. The tag falls through the lookup verbatim, which is the engine's own
	/// behaviour, so a custom entry needs no localisation to render — and gets the game's
	/// fonts, colour and slot layout regardless, because those come from the theme.</item>
	/// </list>
	/// </summary>
	private static Engine_FrontEndMenu.Item BuildCustomScreen()
	{
		var extras = new Engine_FrontEndMenu.Item("Extras", "Extras") { HostOnly = true };

		var submenu = new Engine_FrontEndMenu();
		submenu.Add(new Engine_FrontEndMenu.Item("Renderer", "Renderer"));
		submenu.Add(new Engine_FrontEndMenu.Item("Mods", "Mods"));
		submenu.Add(new Engine_FrontEndMenu.Item("Multiplayer", "Multiplayer"));
		extras.Submenu = submenu;

		return extras;
	}

	/// <summary>
	/// Feeds the model a synthetic <see cref="HostBridge.FrontEndModel"/>.
	///
	/// Constructing the guest's own record rather than poking <c>Item.Available</c> is what
	/// makes this a test of the real path: <c>ApplyAvailability</c> is the only public entry
	/// that both rules on the guest's spawn conditions AND rebuilds the visible list, so
	/// setting the flags directly would exercise neither.
	/// </summary>
	private void ApplyAvailability(Engine_FrontEndMenu menu) =>
		menu.ApplyAvailability(
			new HostBridge.FrontEndModel(
				SignedIn: _signedIn,
				UserIndex: 0,
				LanguageCount: _languageCount,
				LanguageIndex: 0,
				HasNewDownloadableContent: false,
				MenuIsUp: true,
				MenuState: 0,
				TitleState: 0,
				CanPressA: true,
				LoadCardCursor: 0,
				EnumeratedSaves: (uint)_saveCount),
			// Limit 6, which is what `sub_82378618` stores over `dword_83319620` before the menu
			// can dispatch — not the 150 that sits in the image.
			new HostBridge.SaveState(_signedIn, _saveCount, Engine_FrontEndMenu.DefaultSaveLimit));

	public override void _Process(double delta)
	{
		if (_menu is null || _view is null)
		{
			return;
		}

		// The card owns the input while it is up, exactly as it does in the game — the harness
		// and GameUi must not disagree about that, or the thing being eyeballed here is not
		// the thing that ships.
		if (_cards is { Visible: true })
		{
			int sideways = 0;
			if (Input.IsKeyPressed(Key.Left))
			{
				sideways = -1;
			}
			else if (Input.IsKeyPressed(Key.Right))
			{
				sideways = 1;
			}

			if (_cards.Navigate(sideways))
			{
				GD.Print(_cards.Describe());
			}

			return;
		}

		// The fan owns the input while it is up, for the same reason the gender card does. It
		// takes a DELTA as well as a direction, unlike the card: its left and right are relative
		// — the cursor walks up to six saves — so it auto-repeats on the guest's own curve, where
		// the card's two sides are absolute and edge-triggered.
		if (_saveCards is { Visible: true })
		{
			int sideways = 0;
			if (Input.IsKeyPressed(Key.Left))
			{
				sideways = -1;
			}
			else if (Input.IsKeyPressed(Key.Right))
			{
				sideways = 1;
			}

			if (_saveCards.Navigate(sideways, delta))
			{
				GD.Print(_saveCards.Describe());
			}

			return;
		}

		// The direction is sampled every frame and handed to the model, which owns the whole
		// repeat curve. Reading it here as discrete key events instead would put the timing in
		// the harness, which is exactly the value we are trying to look at.
		int direction = 0;
		if (Input.IsKeyPressed(Key.Down))
		{
			direction = 1;
		}
		else if (Input.IsKeyPressed(Key.Up))
		{
			direction = -1;
		}

		if (_view.Navigate(direction, delta))
		{
			GD.Print(_view.Describe());
		}
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (_menu is null || _view is null || @event is not InputEventKey { Pressed: true, Echo: false } key)
		{
			return;
		}

		// The card first, for the same reason _Process routes to it first.
		if (_cards is { Visible: true })
		{
			switch (key.Keycode)
			{
				case Key.Enter or Key.KpEnter or Key.A:
					if (_cards.Activate() is { } chosen)
					{
						CloseCards();
						GD.Print($"[menu-check] would dispatch '{chosen}' to the guest");
					}
					else
					{
						GD.Print("[menu-check] A with neither card up does nothing — the guest's " +
							"own A arm has no case for substate 2");
					}
					break;

				case Key.Escape or Key.B:
					CloseCards();
					GD.Print("[menu-check] cancelled the gender card — back to the menu");
					break;
			}

			return;
		}

		if (_saveCards is { Visible: true })
		{
			switch (key.Keycode)
			{
				case Key.Enter or Key.KpEnter or Key.A:
					int chosen = _saveCards.Activate();
					if (chosen < 0)
					{
						GD.Print("[menu-check] A on an empty fan does nothing — there is no save " +
							"to commit, and the guest would never have opened the carousel");
						break;
					}

					CloseSaveCards();
					GD.Print($"[menu-check] would post 'LoadSave:{chosen}' to the guest");
					break;

				case Key.Escape or Key.B:
					CloseSaveCards();
					GD.Print("[menu-check] cancelled the load fan — back to the menu");
					break;
			}

			return;
		}

		switch (key.Keycode)
		{
			case Key.Enter or Key.KpEnter or Key.A:
			{
				string? dispatched = _view.Activate();

				if (_view.TakeOpenedScreen() is { } screen)
				{
					GD.Print($"[menu-check] the entry opens screen '{screen}'");
					if (screen == Engine_FrontEndMenu.HeroCardScreen)
					{
						OpenCards();
					}

					break;
				}

				GD.Print(dispatched is null
					? $"[menu-check] handled here (expand, submenu or host-only) depth={_view.Depth}\n{_view.Describe()}"
					: $"[menu-check] would dispatch '{dispatched}' to the guest");
				break;
			}

			case Key.Escape or Key.B:
				GD.Print(_view.Back()
					? $"[menu-check] backed out to depth {_view.Depth}"
					: "[menu-check] nothing to back out of — the front end would leave the screen here");
				GD.Print(_view.Describe());
				break;

			case >= Key.Key1 and <= Key.Key4:
				Toggle((int)(key.Keycode - Key.Key1));
				break;
		}
	}

	/// <summary>
	/// Flips one root entry's availability, in the order <c>BuildRoot</c> spawns them.
	///
	/// Two of the four go through the guest's CONDITION rather than through the entry —
	/// "Load Game" needs a sign-in and a save, "Language" needs a second language — because
	/// that is how the model decides them and toggling the flag underneath would not test it.
	/// </summary>
	private void Toggle(int index)
	{
		if (_menu is null || _view is null)
		{
			return;
		}

		string name;
		switch (index)
		{
			case 0:
			case 2:
				name = index == 0 ? "New Game" : "Downloadable Content";
				if (_menu.Find(name) is not { } entry)
				{
					return;
				}
				entry.Available = !entry.Available;
				break;

			case 1:
				name = "Load Game";
				_signedIn = !_signedIn;
				_saveCount = _signedIn ? 1 : 0;
				break;

			case 3:
				name = "Language";
				_languageCount = _languageCount > 1 ? 1u : 2u;
				break;

			default:
				return;
		}

		ApplyAvailability(_menu);
		_view.Refresh();
		GD.Print($"[menu-check] toggled '{name}'\n{_view.Describe()}");
	}
}
