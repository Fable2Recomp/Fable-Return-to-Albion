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

	/// <summary>Below this magnitude the stick counts as released.</summary>
	public const double ThumbstickReleaseThreshold = 0.65;

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

	public void Add(Item item)
	{
		_roots.Add(item);
		Rebuild();
	}

	/// <summary>
	/// Applies the guest's own availability rules.
	///
	/// <paramref name="saveCount"/> is the caller's, not the guest's: the guest decides "Load
	/// Game" with a live XamContentCreateEnumerator, which is a CALL rather than a memory read,
	/// and this port owns storage anyway. Everything else comes straight off the model.
	/// </summary>
	public void ApplyAvailability(Refii.Host.HostBridge.FrontEndModel model, int saveCount)
	{
		foreach (Item item in _roots)
		{
			item.Available = item.Name switch
			{
				"Load Game" => model.SignedIn && saveCount > 0,
				"Language" => model.HasLanguageEntry,
				_ => item.Available,
			};
		}

		Rebuild();
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
			return null;
		}

		return item.HostOnly ? null : item.Name;
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
