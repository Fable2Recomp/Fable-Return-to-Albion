using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The front end's root menu, owned by GODOT.
///
/// The guest's menu has no C++ item list and no C++ highlight — items, order, nesting and the
/// highlight all live in the shipped Lua `g_Menu`, and the C++ side only spawns into it and
/// receives an activation back. So there is nothing to mirror: a native menu has to own its
/// own model, and this is it.
///
/// WHAT STILL COMES FROM THE GUEST is the part a host list cannot invent — whether an entry
/// exists at all. "Load Game" appears only when signed in with at least one save; "Language"
/// only when more than one language is installed. Those are the guest's own spawn conditions,
/// and a list that disagrees offers an entry the guest will not honour.
///
/// ACTIVATION IS BY NAME. The guest dispatches on the item's name string — a strcmp chain over
/// "New Game", "Load Game", "Downloadable Content", "SubtitlesOn", "SubtitlesOff" — not on an
/// index and not through a table. That is what makes this two-way: a host-authored entry can
/// trigger a guest action by naming it, an entry we relabel keeps working, and an entry the
/// guest never had simply never dispatches.
/// </summary>
public sealed class Engine_FrontEndMenu
{
	/// <summary>One entry. <see cref="Name"/> is the identity the guest dispatches on.</summary>
	public sealed class Item
	{
		public Item(string name, string textTag, string? parent = null)
		{
			Name = name;
			TextTag = textTag;
			Parent = parent;
		}

		/// <summary>The guest's identity for this entry — never shown, never localised.</summary>
		public string Name { get; }

		/// <summary>The localisation tag for the label. Overridable by the host.</summary>
		public string TextTag { get; set; }

		/// <summary>Parent entry name, or null for a root entry.</summary>
		public string? Parent { get; }

		/// <summary>False when the guest's own spawn condition excludes it.</summary>
		public bool Available { get; set; } = true;

		/// <summary>
		/// Host-only entries are never dispatched to the guest. This is how "Multiplayer" can
		/// sit in the menu without the guest knowing it exists.
		/// </summary>
		public bool HostOnly { get; set; }

		public List<Item> Children { get; } = new();

		/// <summary>
		/// A front-end SCREEN this entry opens instead of dispatching, or null.
		///
		/// This is the guest's own shape, not an abstraction over it. Its "New Game" entry does
		/// not start a game: `GUI_FrontendRoot_MenuActionDispatch`'s branch at `0x826E0200` sets
		/// slide 1 on the `choosecard` element and writes `screen+24 = 2`, which is the gender
		/// card, and the game is committed later by an event the presentation fires. So an entry
		/// that opens a screen and an entry that dispatches an action are two different things on
		/// the guest side too, and collapsing them is exactly the mistake that made every new
		/// game a boy.
		///
		/// Unlike <see cref="Submenu"/>, the screen is not a list and this class knows nothing
		/// about it — the view hands the name to whoever owns that screen.
		/// </summary>
		public string? Opens { get; set; }

		/// <summary>
		/// The action this entry performs when the guest dispatches on a COMMAND rather than on
		/// a name, or null when the name is the identity.
		///
		/// The language entries are the case that forces this to exist. The guest spawns one per
		/// installed language with command 302 and the table row index, and its dispatcher takes
		/// that pair from `a2+4`/`a2+8` before the name chain is ever reached — so the entries
		/// have no name the guest would recognise, and naming "Language" reached nothing at all.
		/// </summary>
		public string? Command { get; set; }

		/// <summary>
		/// A whole other menu this entry OPENS, as opposed to <see cref="Children"/>, which
		/// expand underneath it in place.
		///
		/// The distinction is the shipped menu's own. "Subtitles" expands — its two options
		/// appear indented below it and the rest of the list stays put — because that is what
		/// <c>expandablemenu.lua</c> does with a nested item. A screen like an options page or
		/// a mod list is not that: it REPLACES the list, and coming back restores what was
		/// there. Modelling both as children would put an options page's forty entries inline
		/// under the root menu.
		///
		/// This is also the seam for screens the original never had. The guest dispatches on
		/// the entry NAME and knows nothing it was not shipped with, so a host-only entry with
		/// a submenu is a complete custom screen that the guest never sees and never has to.
		/// </summary>
		public Engine_FrontEndMenu? Submenu { get; set; }
	}

	/// <summary>
	/// Fable 2's own navigation feel, read out of the shipped `guistateandconstants.lua`
	/// rather than chosen.
	///
	/// The first repeat waits a quarter second, then repeats accelerate toward 0.14s. A menu
	/// that repeats at one fixed rate does not feel like this one, and the difference is
	/// immediately obvious to anyone who has played it — which is exactly the sort of thing
	/// that is invisible in the binary and plain in the scripts.
	/// </summary>
	public const double FirstNavigateInterval = 0.25;

	public const double SubsequentNavigateInterval = 0.14;

	public const double MinimumNavigateInterval = 0.20;

	/// <summary>How many accelerating steps before the interval stops shrinking.</summary>
	public const int InputIntervalClamp = 10;

	/// <summary>
	/// Below this magnitude the stick counts as released — `THUMBSTICK_RELEASE_THRESHOLD`.
	///
	/// It is HALF of a hysteresis pair, which is easy to miss because only one of the two ever
	/// gets quoted. See <see cref="ThumbstickPushThreshold"/>.
	/// </summary>
	public const double ThumbstickReleaseThreshold = 0.65;

	/// <summary>
	/// Above this magnitude the stick counts as pushed — `THUMBSTICK_PUSH_THRESHOLD`, 0.70.
	///
	/// The gap between the two is deliberate and is why `guistateandconstants.lua` also keeps
	/// four `Pushing*Held` booleans: a direction ENGAGES at 0.70 and does not release until the
	/// stick falls back under 0.65. Using one threshold for both — which this did, at 0.65 —
	/// makes a stick resting in that band read as a push the original would have ignored, and a
	/// stick wobbling across the line chatter between held and released.
	/// </summary>
	public const double ThumbstickPushThreshold = 0.70;

	private readonly List<Item> _roots = new();

	private double _timeSinceLastNavigate;

	private double _navigateInterval = FirstNavigateInterval;

	private int _navigationCount;

	private int _held;

	/// <summary>The entries currently on screen, in order — roots plus any expanded children.</summary>
	public List<Item> Visible { get; } = new();

	/// <summary>Index into <see cref="Visible"/>.</summary>
	public int HighlightIndex { get; private set; }

	/// <summary>The highlighted entry, or null when the menu is empty.</summary>
	public Item? Highlighted =>
		HighlightIndex >= 0 && HighlightIndex < Visible.Count ? Visible[HighlightIndex] : null;

	/// <summary>The entry whose children are expanded, or null.</summary>
	public Item? Expanded { get; private set; }

	/// <summary>
	/// Builds the root menu the way the guest builds it — same entries, same order.
	///
	/// The order is the guest's spawn order in `sub_826E2220` and is not incidental: the
	/// activation the guest sends back is keyed by name, but a player reads the list top to
	/// bottom and every screenshot of the original has this sequence.
	/// </summary>
	public static Engine_FrontEndMenu BuildRoot()
	{
		var menu = new Engine_FrontEndMenu();

		// NEW GAME OPENS THE GENDER CARD; it does not start a game.
		//
		// The guest's own entry does not either. Its dispatcher sets slide 1 on `choosecard`
		// and puts the screen in substate 2, and the game is committed much later, by
		// `sub_826E0D60`, when the presentation's own Behavior element fires BOY_SELECTED or
		// GIRL_SELECTED. The host route into a new game takes that EVENT directly, so posting
		// a bare "New Game" consumes the answer to a question nobody asked and starts a male
		// hero every time, silently.
		//
		// So the entry names the screen and stops there. Godot asks, Godot names the answer,
		// and the guest gets the event it was always going to get — see
		// <see cref="Engine_FrontEndCardChoice"/>.
		var newGame = new Item("New Game", "GUI_SCREEN_FRONT_END_NEW_GAME") { Opens = HeroCardScreen };
		menu.Add(newGame);
		menu.Add(new Item("Load Game", "GUI_SCREEN_FRONT_END_LOAD_GAME"));
		menu.Add(new Item("Downloadable Content", "GUI_DOWNLOADABLE_CONTENT"));
		menu.Add(new Item("Language", "GUI_SCREEN_FRONT_END_LANGUAGE"));

		var subtitles = new Item("Subtitles", "GUI_SCREEN_OPTIONS_SUBTITLES");
		subtitles.Children.Add(new Item("SubtitlesOn", "GUI_CONTROLS_ON", "Subtitles"));
		subtitles.Children.Add(new Item("SubtitlesOff", "GUI_CONTROLS_OFF", "Subtitles"));
		menu.Add(subtitles);

		return menu;
	}

	/// <summary>
	/// The gender card <c>New Game</c> opens — <see cref="Engine_FrontEndCardChoice"/>.
	///
	/// A name rather than a reference because this class holds the MODEL and knows nothing
	/// about node trees; whoever owns the front end recognises it and puts the panel up.
	/// </summary>
	public const string HeroCardScreen = "HeroCard";

	/// <summary>
	/// The saved-game slot cap, for a caller with no guest to read it from.
	///
	/// SIX, not the 150 that sits in the image at `dword_83319620`. `sub_82378618` — the
	/// front-end enter, vtable slot 0x820A9260 — stores 6 over it unconditionally, and that
	/// runs before the menu can dispatch, so 150 is a value the game never decides with.
	/// </summary>
	public const int DefaultSaveLimit = 6;

	/// <summary>The guest's own text tag for a New Game with no profile signed in.</summary>
	public const string NoProfileWarning = "GUI_NO_PROFILE_SELECTED_WARNING";

	/// <summary>The guest's own text tag for a New Game with every save slot used.</summary>
	public const string SaveLimitWarning = "GUI_REACHED_SAVE_GAME_LIMIT_WARNING";

	/// <summary>What the storage layer last said, as <see cref="ApplyAvailability"/> was told.</summary>
	public Refii.Host.HostBridge.SaveState Saves { get; private set; } = new(false, -1, 0);

	/// <summary>
	/// A refusal the last <see cref="Activate"/> produced, as a text tag, or null.
	///
	/// Latched rather than thrown or returned, because a refusal is not a dispatch and not a
	/// screen — the two things <see cref="Activate"/> already distinguishes — and a caller that
	/// ignores it gets an entry that does nothing rather than one that acts wrongly.
	/// </summary>
	public string? Warning { get; private set; }

	/// <summary>The pending refusal, cleared by reading it.</summary>
	public string? TakeWarning()
	{
		string? warning = Warning;
		Warning = null;
		return warning;
	}

	/// <summary>
	/// The guest's own New Game guards, in its own order — `sub_826DFD58` from 0x826E0040.
	///
	/// TWO OF THE THREE. The guest checks, in order:
	///
	///   0x826E0078  `(1 &lt;&lt; *0x83496AF8) &amp; *0x83496A70` — a profile is signed in.
	///   0x826E0094  `sub_82441CB8(saveMgr, user, 1)` — a storage device is selected.
	///   0x826E0148  `sub_824431E8(...) &lt; *0x83319620` — a slot is free.
	///
	/// THE MIDDLE ONE IS DELIBERATELY ABSENT and this is the place to say so. It is not a test:
	/// `sub_82441CB8` calls `j_XamShowDeviceSelectorUI` and then blocks on
	/// `XGetOverlappedResult(..., bWait=1)` — it PUTS UP the console's device picker and waits
	/// for an answer. There is one device here and no picker to show, so reproducing it would
	/// mean reproducing a console affordance that has nothing to choose between. Its failure
	/// text, GUI_NO_DEVICE_SELECTED_WARNING, is for a state this port cannot enter.
	///
	/// THE COUNT IS A SUBSTITUTION and this is the place to admit it. The guest's third guard
	/// calls `sub_824431E8`, which enumerates the SELECTED device; our sample comes from
	/// `sub_82443308`, which enumerates ALL of them. They are different queries and only agree
	/// where there is one device — which is the case here, and is a consequence of skipping the
	/// device picker above rather than an independent choice. `sub_824431E8` is not available
	/// to us: it reads a DeviceID out of a map that only the blocking picker ever writes, so it
	/// would return a confident zero and let a full profile start a game that cannot be saved.
	///
	/// The third guard is why the limit matters: the guest compares against 6, and the WRITE
	/// side (`sub_82445050` at 0x82445108) makes the same comparison and returns 0 silently.
	/// This dialog is the only thing that ever tells the player why nothing happened.
	/// </summary>
	private string? NewGameRefusal()
	{
		if (!Saves.SignedIn)
		{
			return NoProfileWarning;
		}

		return Saves.AtSaveLimit ? SaveLimitWarning : null;
	}

	public void Add(Item item)
	{
		_roots.Add(item);
		Rebuild();
	}

	/// <summary>
	/// Applies the guest's own availability rules.
	///
	/// LOAD GAME IS THE ONE THAT NEEDED A CALL, and getting that wrong is what kept it off the
	/// menu. The guest's spawn guard at 0x826E2264 is `signedIn &amp;&amp; sub_82443308() != 0`
	/// — a live XamContentCreateEnumerator over content type 1, DeviceID 0. The vector of save
	/// names at the front-end screen's +84/+88 is NOT that answer: it is filled only inside the
	/// dispatcher's own "Load Game" branch (`sub_82443048` at 0x826E03CC), so at menu-build time
	/// it is always empty and gating on it suppressed the entry unconditionally.
	///
	/// <paramref name="saves"/> therefore carries the pump's sample, and carries "not asked yet"
	/// distinctly from "none" — see <see cref="Refii.Host.HostBridge.SaveState"/>. Everything
	/// else comes straight off the model.
	/// </summary>
	public void ApplyAvailability(
		Refii.Host.HostBridge.FrontEndModel model, Refii.Host.HostBridge.SaveState saves,
		System.Collections.Generic.IReadOnlyList<Refii.Host.HostBridge.Language>? languages = null)
	{
		foreach (Item item in _roots)
		{
			item.Available = item.Name switch
			{
				// Held back until the pump has answered. Drawing it absent and adding it a frame
				// later is the lesser evil against asserting a profile has no saves.
				"Load Game" => saves.CanLoad,
				"Language" => model.HasLanguageEntry,
				_ => item.Available,
			};
		}

		Saves = saves;

		PopulateLanguages(languages);
		Rebuild();
	}

	/// <summary>
	/// Fills "Language" with one child per installed language, as the guest fills it.
	///
	/// `sub_826E1C98` walks the guest's own 96-byte-stride table and spawns an entry per row —
	/// display NAME as the item name, group "Language", command 302, index = the row. So the
	/// list is the guest's, and the only thing invented here is the order of the two calls.
	///
	/// NOTE THE ASYMMETRY, which is the guest's and not a mistake: the children are spawned
	/// whenever the table has entries, but the "Language" PARENT only appears above one
	/// installed language. A single-language install therefore has a child list nobody can
	/// reach, which is exactly what <see cref="ApplyAvailability"/> reproduces by leaving the
	/// parent unavailable.
	///
	/// THE LIST IS THE CALLER'S, for the same reason the save state is: reading it
	/// means reading GUEST memory, and this class must work without a guest. It called
	/// `HostBridge.GetLanguages()` itself and segfaulted the menu harness, which builds the very
	/// same model with no guest behind it — a model that can only be constructed next to a
	/// running game is not a model.
	/// </summary>
	private void PopulateLanguages(
		System.Collections.Generic.IReadOnlyList<Refii.Host.HostBridge.Language>? languages)
	{
		if (languages is null || Find("Language") is not { } language)
		{
			return;
		}

		language.Children.Clear();

		foreach (Refii.Host.HostBridge.Language installed in languages)
		{
			// The NAME is the guest's display string and doubles as the text tag: the front end
			// puts it through Babel, and an unresolved tag renders verbatim, which is the same
			// fallback the engine takes.
			language.Children.Add(new Item(installed.Name, installed.Name, "Language")
			{
				Command = $"Language:{installed.Index}",
			});
		}
	}

	/// <summary>Recomputes the visible list and keeps the highlight on something real.</summary>
	private void Rebuild()
	{
		Item? was = Highlighted;
		Visible.Clear();

		foreach (Item root in _roots)
		{
			if (!root.Available)
			{
				continue;
			}

			Visible.Add(root);
			if (!ReferenceEquals(root, Expanded))
			{
				continue;
			}

			foreach (Item child in root.Children)
			{
				if (child.Available)
				{
					Visible.Add(child);
				}
			}
		}

		// Follow the entry the player was on rather than the index it happened to occupy.
		// Availability can change under the menu — signing in adds "Load Game" above the
		// highlight — and holding the index would silently move the selection.
		int index = was is null ? 0 : Visible.IndexOf(was);
		HighlightIndex = index >= 0 ? index : 0;
	}

	/// <summary>
	/// Advances the auto-repeat and returns how many steps to move this frame.
	///
	/// <paramref name="direction"/> is -1 up, +1 down, 0 released. The curve is the guest's:
	/// act immediately on a fresh press, then wait <see cref="FirstNavigateInterval"/>, then
	/// accelerate toward <see cref="SubsequentNavigateInterval"/> over
	/// <see cref="InputIntervalClamp"/> steps.
	/// </summary>
	public int Navigate(int direction, double delta)
	{
		if (direction == 0)
		{
			// Released: back to a standing start, so the next press is instant rather than
			// inheriting the accelerated rate from the last hold.
			_held = 0;
			_navigationCount = 0;
			_navigateInterval = FirstNavigateInterval;
			_timeSinceLastNavigate = 0.0;
			return 0;
		}

		if (_held != direction)
		{
			_held = direction;
			_navigationCount = 0;
			_navigateInterval = FirstNavigateInterval;
			_timeSinceLastNavigate = 0.0;
			Move(direction);
			return 1;
		}

		_timeSinceLastNavigate += delta;
		if (_timeSinceLastNavigate < _navigateInterval)
		{
			return 0;
		}

		_timeSinceLastNavigate = 0.0;
		if (_navigationCount < InputIntervalClamp)
		{
			++_navigationCount;
			double t = (double)_navigationCount / InputIntervalClamp;
			_navigateInterval = FirstNavigateInterval +
				((SubsequentNavigateInterval - FirstNavigateInterval) * t);
		}

		Move(direction);
		return 1;
	}

	/// <summary>
	/// Moves the highlight. It CLAMPS at both ends — it does not wrap.
	///
	/// The shipped `expandablemenuinput.lua` guards both directions —
	/// `ScrollUp` acts only while `1 &lt; CurrentHighlightIdx` and `ScrollDown` only while
	/// `CurrentHighlightIdx &lt; #VisibleElements` — so the top and bottom entries are dead ends.
	///
	/// It matters more than it looks. With the highlight pinned to a fixed slot and the LIST
	/// sliding underneath it, wrapping would throw the window from one end of the menu to the
	/// other in a single press, which is a completely different feel from stopping.
	/// </summary>
	private void Move(int direction)
	{
		int next = HighlightIndex + direction;
		if (next >= 0 && next < Visible.Count)
		{
			HighlightIndex = next;
		}
	}

	/// <summary>
	/// Activates the highlighted entry.
	///
	/// Returns the NAME to dispatch to the guest, or null when the press is NOT a dispatch —
	/// expanding a submenu, opening one, opening a screen, or a host-only entry the guest knows
	/// nothing about. The caller distinguishes those by looking at the entry it activated.
	///
	/// AN ENTRY THAT OPENS SOMETHING MUST NOT ALSO DISPATCH, and it used to. Only
	/// <see cref="Item.Children"/> returned null here, so an entry carrying a
	/// <see cref="Item.Submenu"/> or an <see cref="Item.Opens"/> fell through to the last line
	/// and was sent to the guest — which meant the view's submenu branch, guarded on a null
	/// return, could never run. "New Game" went straight to the pump and started a male hero
	/// without the gender card ever appearing.
	/// </summary>
	public string? Activate()
	{
		if (Highlighted is not { } item)
		{
			return null;
		}

		if (item.Children.Count > 0)
		{
			Expanded = ReferenceEquals(Expanded, item) ? null : item;
			Rebuild();
			return null;
		}

		if (item.Opens is not null || item.Submenu is not null)
		{
			// The guest runs its storage guards BEFORE the card appears, not after the choice —
			// 0x826E01FC, the switch to the `choosecard` element, is the SUCCESS arm of the
			// third guard. Refusing here rather than at the commit is following that order.
			if (item.Opens == HeroCardScreen)
			{
				Warning = NewGameRefusal();
			}

			return null;
		}

		if (item.HostOnly)
		{
			return null;
		}

		// A command entry dispatches on its COMMAND, not its name — see Item.Command.
		return item.Command ?? item.Name;
	}

	/// <summary>Collapses an expanded submenu. True when it consumed the press.</summary>
	public bool Back()
	{
		if (Expanded is null)
		{
			return false;
		}

		Expanded = null;
		Rebuild();
		return true;
	}

	/// <summary>Finds a root entry by the guest's name, for host overrides.</summary>
	public Item? Find(string name)
	{
		foreach (Item item in _roots)
		{
			if (item.Name == name)
			{
				return item;
			}
		}

		return null;
	}
}
