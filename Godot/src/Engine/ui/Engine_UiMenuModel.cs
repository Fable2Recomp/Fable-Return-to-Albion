using System.Collections.Generic;

using Godot;

namespace Refii.Ui;

/// <summary>
/// The menu model the Gameface Lua would have held, built from the C++'s own statements.
/// </summary>
/// <remarks>
/// The C++ mutates menus by staging attributes onto the `Parameters` element and running a
/// one-line Lua chunk that reads them back. There are exactly 28 `g_Menu:` chunk literals in the
/// image and every C++ emitter reaches one runner, so that set is the whole vocabulary. See
/// docs/ui_native_cut.md.
///
/// State is keyed per PRESENTATION, not per chunk name: `menu/menuemphasisandghosting` defines a
/// second `SetItemEmphasis` / `SetItemGhosting` for the non-expandable menu that treats `Float1`
/// as a boolean rather than the 0..3 enum and writes `Ghosting` rather than `Ghosted`.
///
/// No layout, folding or cursor here — `Engine_UiExpandableMenu` and `Engine_UiMenuFormatter`
/// consume this.
/// </remarks>
public sealed class Engine_UiMenuModel
{
	/// <summary>
	/// The attribute the runner adds to the statements that act on "the selected row"
	/// (`SetSelectedItemGhosting`, `SetSelectedItemEmphasis`, `RefreshSelectedButtonText`,
	/// `RenameSelectedButton`) and to `PushMenuState`: the name of the row Godot states as
	/// highlighted when the guest made the statement, which is the row the shipped script's
	/// `VisibleElements[CurrentHighlightIdx]` would have been. Absent when no row was stated — the
	/// script's `nil` arm, which changes nothing.
	/// </summary>
	public const string HighlightedAttribute = "@Highlighted";

	/// <summary>
	/// The first chunk the guest runs on a presentation it initialises (`sub_823DCC70`, disc
	/// `sub_823FE988`, at 0x823dcca8 / 0x823fe9c0): its Lua state, and so its menu, starts here.
	/// </summary>
	public const string PresentationInitChunk = "GUI:SetAsNotRunningInAnarkStudio()";

	/// <summary>One row, as the C++ stated it.</summary>
	public sealed class Row
	{
		/// <summary>`String1`. Settable: `RenameSelectedButton` renames the row in place.</summary>
		public required string Name { get; set; }

		public string Parent { get; set; } = string.Empty;

		public string Label { get; set; } = string.Empty;

		public string Icon { get; set; } = string.Empty;

		public uint Id { get; set; }

		public int EventType { get; set; }

		public int Rating { get; set; }

		/// <summary>`SetItemGhosting`'s flag; false at spawn (`expandablemenu.dis:402`).</summary>
		public bool Ghosted { get; set; }

		/// <summary>0 none · 1 primary quest · 2 active quest · 3 suggested.</summary>
		public Engine_UiMenuConstants.Emphasis Emphasis { get; set; }

		/// <summary>Spawn order, which is display order among siblings.</summary>
		public int Ordinal { get; init; }

		/// <summary>
		/// A top-level entry — `sub_82430468` passes the literal "Root" in the String4 position.
		/// An empty parent counts too.
		/// </summary>
		public bool IsRoot =>
			Parent.Length == 0 ||
			string.Equals(Parent, "Root", System.StringComparison.Ordinal);
	}

	/// <summary>One menu's state.</summary>
	public sealed class Menu
	{
		public readonly List<Row> Rows = new();

		/// <summary>`SetInitiallySelectedItem(N)` — 1-based, 0 when unstated.</summary>
		public int InitiallySelected;

		/// <summary>
		/// The population script this menu was last switched to, from
		/// `RepopulateMenuFromPopulationScript`'s own argument. Empty until one is stated.
		/// Several screens never put a `MenuPopulationScript` attribute on their widget, so this
		/// is the only statement of which list they are.
		/// </summary>
		public string PopulationScript = string.Empty;

		/// <summary>
		/// `TurnOffAutomaticGhostingOnSubmenus` clears this; default on. Second term of
		/// `UpdateAllGhosting`'s `ghost = item.Ghosted or (EventType == Submenu and
		/// #ChildItems == 0 and AutomaticGhostingOnSubmenus)`.
		/// </summary>
		public bool AutomaticGhostingOnSubmenus =
			Engine_UiMenuConstants.AutomaticGhostingOnSubmenusDefault;

		/// <summary>True once `UpdateAfterSpawning` has run. The in-game lists never send it, so
		/// its absence means nothing.</summary>
		public bool Settled;

		/// <summary>Bumped on every change, so a view can skip untouched frames.</summary>
		public ulong Generation;

		/// <summary>
		/// `SetItemGhosting` by row NAME, kept whether or not this model holds the row — see the
		/// case. A list Godot builds from the template reads its ghosting here.
		/// </summary>
		public readonly Dictionary<string, bool> Ghosting =
			new(System.StringComparer.Ordinal);

		/// <summary>
		/// `SetSelectedItemEmphasis` by the highlighted row's NAME, kept whether or not this model
		/// holds the row, like <see cref="Ghosting"/>.
		/// </summary>
		public readonly Dictionary<string, Engine_UiMenuConstants.Emphasis> Emphases =
			new(System.StringComparer.Ordinal);

		/// <summary>`RefreshSelectedButtonText`'s `String2`, by the highlighted row's name.</summary>
		public readonly Dictionary<string, string> Labels = new(System.StringComparer.Ordinal);

		/// <summary>
		/// `RenameSelectedButton`: the highlighted row's name when renamed, to the name it took
		/// (`String1`). The shop names a row it sold out `SOLD` (`sub_826B0B90`, 0x826b0ca4) and its
		/// handler then reads that name back through `GetHighlightedMenuItemData`.
		/// </summary>
		public readonly Dictionary<string, string> Renames = new(System.StringComparer.Ordinal);

		/// <summary>
		/// `SetExpandableMenuDisabled(true|false)` — `g_Menu.UpDownInputDisabled`, which the
		/// script's `onUpdate` checks before reading the stick. Lives with the presentation's
		/// script state: no list boundary resets it, only the script's own `onInitialize`.
		/// </summary>
		public bool InputDisabled;

		/// <summary>How many `g_MenuFormatter:UpdateAllGhosting()` the guest has asked for — a
		/// view re-derives every row's drawn ghosting when this moves.</summary>
		public int GhostRefreshes;

		/// <summary>True while a `PushMenuState` stands unpopped.</summary>
		public bool Pushed => _saved is not null;

		private Saved? _saved;

		private string? _restoredHighlight;

		/// <summary>
		/// The row `PopMenuState` put the cursor back on (the script's
		/// `forceReset(Backup.CurrentHighlightIdx)`), named; taken once by the view.
		/// </summary>
		public string? TakeRestoredHighlight()
		{
			string? name = _restoredHighlight;
			_restoredHighlight = null;
			return name;
		}

		/// <summary>One saved menu, as `PushMenuState` keeps it — one level, not a stack.</summary>
		private sealed record Saved(
			List<Row> Rows,
			Dictionary<string, bool> Ghosting,
			Dictionary<string, Engine_UiMenuConstants.Emphasis> Emphases,
			Dictionary<string, string> Labels,
			Dictionary<string, string> Renames,
			int InitiallySelected,
			bool Settled,
			string PopulationScript,
			string Highlighted);

		/// <summary>
		/// `g_Menu:PushMenuState()` — the whole menu (`MenuRoot`, `SpawnedItems`, the highlight) is
		/// saved and an empty one starts, for a screen opening a sub-list over its own.
		/// </summary>
		public void Push(string highlighted)
		{
			_saved = new Saved(
				new List<Row>(Rows),
				new Dictionary<string, bool>(Ghosting, System.StringComparer.Ordinal),
				new Dictionary<string, Engine_UiMenuConstants.Emphasis>(Emphases, System.StringComparer.Ordinal),
				new Dictionary<string, string>(Labels, System.StringComparer.Ordinal),
				new Dictionary<string, string>(Renames, System.StringComparer.Ordinal),
				InitiallySelected,
				Settled,
				PopulationScript,
				highlighted);
			ClearRows();
		}

		/// <summary>
		/// `g_Menu:PopMenuState()` — the saved menu comes back with its cursor; nothing pushed does
		/// nothing. The population script is restored with the rows it made (the script itself
		/// leaves its `MenuPopulationScript` attribute on the sub-list's name).
		/// </summary>
		public bool Pop()
		{
			if (_saved is not { } saved)
			{
				return false;
			}

			ClearRows();
			Rows.AddRange(saved.Rows);
			Copy(saved.Ghosting, Ghosting);
			Copy(saved.Emphases, Emphases);
			Copy(saved.Labels, Labels);
			Copy(saved.Renames, Renames);
			InitiallySelected = saved.InitiallySelected;
			Settled = saved.Settled;
			PopulationScript = saved.PopulationScript;
			_restoredHighlight = saved.Highlighted.Length != 0 ? saved.Highlighted : null;
			_saved = null;
			return true;
		}

		/// <summary>A list boundary: every row and every by-name statement about one goes.</summary>
		public void ClearRows()
		{
			Rows.Clear();
			Ghosting.Clear();
			Emphases.Clear();
			Labels.Clear();
			Renames.Clear();
			Settled = false;
			InitiallySelected = 0;
		}

		private static void Copy<T>(Dictionary<string, T> from, Dictionary<string, T> to)
		{
			to.Clear();
			foreach ((string key, T value) in from)
			{
				to[key] = value;
			}
		}

		public Row? Find(string name) =>
			Rows.Find(row => string.Equals(row.Name, name, System.StringComparison.Ordinal));
	}

	private readonly Dictionary<uint, Menu> _menus = new();

	private readonly Dictionary<string, int> _unhandled = new(System.StringComparer.Ordinal);

	private int _applied;

	/// <summary>One menu's state, created on first statement.</summary>
	public Menu For(uint presentation)
	{
		if (!_menus.TryGetValue(presentation, out Menu? menu))
		{
			menu = new Menu();
			_menus[presentation] = menu;
		}

		return menu;
	}

	/// <summary>Every menu the game has stated anything about.</summary>
	public IReadOnlyDictionary<uint, Menu> Menus => _menus;

	/// <summary>
	/// Forgets every menu. For the guest leaving the game: its shutdown deletes the in-game GUI
	/// and every presentation with it, and the next game's presentations land at reused
	/// addresses, so a menu kept across the boundary joins the old game's rows to the new one's
	/// screen until its Lua happens to repopulate it. The applied count is a run total and stays.
	/// </summary>
	public void Reset()
	{
		_menus.Clear();
	}

	/// <summary>
	/// Forgets one presentation's menu. The model is keyed by presentation ADDRESS and the guest
	/// recycles addresses: a screen freed on close and rebuilt on the next open lands where the
	/// last one stood, and its fresh Gameface VM holds only the rows it spawns itself, so it
	/// never states a clear; the earlier instance's rows must not carry into the new list.
	/// Called when the registry stops listing the address.
	/// </summary>
	public void Forget(uint presentation) => _menus.Remove(presentation);

	/// <summary>How many statements have been applied, for the report.</summary>
	public int Applied => _applied;

	/// <summary>
	/// Applies one drained batch in order. Never sort, batch or coalesce — order is the semantics
	/// (`DeleteAllSpawnedMenuItems` then nine `SpawnButton`s is a repopulation).
	/// </summary>
	public void Apply(IReadOnlyList<Engine_UiModel.MenuChunk> chunks)
	{
		foreach (Engine_UiModel.MenuChunk chunk in chunks)
		{
			Apply(chunk);
		}
	}

	/// <summary>Applies one statement.</summary>
	/// <summary>
	/// The first single-quoted argument in a chunk, or empty. The guest builds these with
	/// `sprintf("... '%s' ...")`, so the quotes are the delimiter and there is no escaping to
	/// handle.
	/// </summary>
	private static string QuotedArgument(string chunk)
	{
		int open = chunk.IndexOf('\'');
		if (open < 0)
		{
			return string.Empty;
		}

		int close = chunk.IndexOf('\'', open + 1);
		return close < 0 ? string.Empty : chunk[(open + 1)..close].Trim();
	}

	public void Apply(Engine_UiModel.MenuChunk chunk)
	{
		++_applied;

		// A presentation's script state is born: whatever this address held is another instance's.
		if (string.Equals(chunk.Chunk.Trim(), PresentationInitChunk, System.StringComparison.Ordinal))
		{
			_menus.Remove(chunk.Presentation);
			return;
		}

		Menu menu = For(chunk.Presentation);

		switch (chunk.Method)
		{
			case "SpawnButton":
				Spawn(menu, chunk);
				break;

			case "DeleteAllSpawnedMenuItems":
				// The list boundary: rows, settle marker and initial selection all go, because
				// the next population states its own. The shipped Lua drains
				// `g_Menu.SpawnedItems` one at a time (`expandablemenuspawning`), so its scope
				// is exactly the rows this model holds.
				menu.ClearRows();
				break;

			case "RepopulateMenuFromPopulationScript":
				// A SECOND, WIDER LIST BOUNDARY — and the one that was missing. The shipped Lua
				// is three statements:
				//
				//   setAttribute(parent, "MenuPopulationScript", scriptName)
				//   g_Menu:RemoveAllChildren()
				//   g_Menu:RepopulateFromPopulationTable()
				//
				// `RemoveAllChildren` clears EVERYTHING (not just `SpawnedItems`, which is all
				// `DeleteAllSpawnedMenuItems` touches) and it happens INSIDE Lua, so it never
				// arrives as a chunk of its own. Without this the model keeps every previous
				// population on the presentation: the gift screen accumulated 98 rows and drew
				// 32 clothing items beside its nine categories, orphans of a list that had been
				// replaced.
				//
				// The rows it then creates come from `CreateMenuItem`/`AddChild` in Lua, NOT
				// `SpawnButton`, so they never reach this model at all — which is exactly why
				// the fixed menus have to be built from our own transcription of the population
				// table rather than waited for here.
				menu.ClearRows();

				// The script it is switching to. The name is the chunk's own quoted argument —
				// `sub_829B2A20` builds the text with
				// `"g_Menu:RepopulateMenuFromPopulationScript( '%s' )"` — and it is the only
				// place some screens ever state it: the gift family's widget carries no
				// `MenuPopulationScript` attribute for us to read.
				if (QuotedArgument(chunk.Chunk) is { Length: > 0 } script)
				{
					menu.PopulationScript = script;
				}

				break;

			case "SetItemGhosting":
			{
				// RECORDED BY NAME WHETHER OR NOT THE MODEL HOLDS THE ROW. It usually does not:
				// the fixed menus are built by `CreateMenuItem` inside Lua and never reach this
				// model, so a ghosting statement about one of their rows had nowhere to land and
				// was dropped — the guest's only channel for "this option is unavailable right
				// now" was being discarded for exactly the lists Godot builds itself.
				string named = chunk.Attribute("String1");

				// The guest's own test: `Float1 == 0` is false, anything else true.
				bool ghost = chunk.Number("Float1") != 0f;
				if (named.Length != 0)
				{
					menu.Ghosting[named] = ghost;
				}

				if (menu.Find(named) is { } ghosting)
				{
					ghosting.Ghosted = ghost;
				}

				break;
			}

			case "SetItemEmphasis":
			{
				// RECORDED BY NAME, as SetItemGhosting is: the world map's enter emphasises its
				// category by name (`sub_823DFA98(23, name, 3)` at 0x826c5df8), and its categories
				// are template rows the model never spawned.
				string named = chunk.Attribute("String1");
				Engine_UiMenuConstants.Emphasis emphasis = EmphasisFrom(chunk.Number("Float1"));
				if (named.Length != 0)
				{
					menu.Emphases[named] = emphasis;
				}

				if (menu.Find(named) is { } emphasised)
				{
					emphasised.Emphasis = emphasis;
				}

				break;
			}

			case "GetLastItemIndex":
				// A read, answered natively at the runner (resource_manager_open.cpp) for the
				// spawn helper's `SetItemEmphasisByIndex`; it states nothing.
				break;

			case "SetItemEmphasisByIndex":
				// By spawn index, not name: `Float2` is `MenuItemIdx`, matched by a depth-first
				// walk from `MenuRoot` (`expandablemenuemphasisandghosting.dis` main.2.0 pc4..7).
				// `Float1` is written raw onto `Emphasis` (main.2 pc24) — the same 0..3 enum
				// `SetItemEmphasis` carries, not a boolean.
				{
					int index = (int)chunk.Number("Float2");
					if (index >= 1 && index <= menu.Rows.Count)
					{
						menu.Rows[index - 1].Emphasis = EmphasisFrom(chunk.Number("Float1"));
					}
				}

				break;

			case "RefreshButton":
				// Text AND both packed data fields, without respawning.
				if (menu.Find(chunk.Attribute("String1")) is { } refreshed)
				{
					refreshed.Label = chunk.Attribute("String2");
					refreshed.Id = PackedId(chunk);
					refreshed.Rating = (int)chunk.Number("Float4");
				}

				break;

			case "RefreshButtonText":
			case "RefreshChildsButtonText":
				// Caption only. `RefreshChildsButtonText` also states `String4` (the parent key)
				// but re-captions rather than reparents, so the parent is not written here.
				if (menu.Find(chunk.Attribute("String1")) is { } recaptioned)
				{
					recaptioned.Label = chunk.Attribute("String2");
				}

				break;

			case "SetInitiallySelectedItem":
				// The argument is in the chunk text, not staged — built by concatenation.
				menu.InitiallySelected =
					int.TryParse(chunk.Inline, out int initial) ? initial : 0;
				break;

			case "TurnOffAutomaticGhostingOnSubmenus":
				menu.AutomaticGhostingOnSubmenus = false;
				break;

			case "UpdateAfterSpawning":
				// Only the front-end lists send it; the in-game ones return, so its absence is
				// not evidence the list is incomplete.
				menu.Settled = true;
				break;

			case "SetExpandableMenuDisabled":
				// Two literals, the flag in the text: `(true)` from the abilities blade
				// (`sub_826C2480`, 0x826c24d4), the shop's confirm, the options page, the gift
				// adjuster; `(false)` from their closes (`sub_826C25C0`, 0x826c261c, …).
				menu.InputDisabled = string.Equals(chunk.Inline, "true", System.StringComparison.Ordinal);
				break;

			case "PushMenuState":
				menu.Push(chunk.Attribute(HighlightedAttribute));
				break;

			case "PopMenuState":
				if (!menu.Pop())
				{
					return;
				}

				break;

			case "SetSelectedItemGhosting":
			{
				// `VisibleElements[CurrentHighlightIdx].Ghosted = Float1 ~= 0`; no row, no change.
				string selected = chunk.Attribute(HighlightedAttribute);
				if (selected.Length == 0)
				{
					return;
				}

				bool ghost = chunk.Number("Float1") != 0f;
				menu.Ghosting[selected] = ghost;
				if (menu.Find(selected) is { } row)
				{
					row.Ghosted = ghost;
				}

				break;
			}

			case "SetSelectedItemEmphasis":
			{
				// `Emphasis = Float1` on the highlighted row; with none the script calls a global
				// `SetItemEmphasis()` that does not exist, so nothing changes.
				string selected = chunk.Attribute(HighlightedAttribute);
				if (selected.Length == 0)
				{
					return;
				}

				Engine_UiMenuConstants.Emphasis emphasis = EmphasisFrom(chunk.Number("Float1"));
				menu.Emphases[selected] = emphasis;
				if (menu.Find(selected) is { } row)
				{
					row.Emphasis = emphasis;
				}

				break;
			}

			case "RefreshSelectedButtonText":
			{
				// Only when the highlighted row IS `String1`; otherwise the script calls a global
				// `RefreshButtonText()` that does not exist.
				string selected = chunk.Attribute(HighlightedAttribute);
				string named = chunk.Attribute("String1");
				if (selected.Length == 0 ||
					!string.Equals(selected, named, System.StringComparison.Ordinal))
				{
					return;
				}

				string label = chunk.Attribute("String2");
				menu.Labels[selected] = label;
				if (menu.Find(selected) is { } row)
				{
					row.Label = label;
				}

				break;
			}

			case "RenameSelectedButton":
			{
				// `Name = String1` on the highlighted row, caption untouched.
				string selected = chunk.Attribute(HighlightedAttribute);
				string renamed = chunk.Attribute("String1");
				if (selected.Length == 0 || renamed.Length == 0)
				{
					return;
				}

				menu.Renames[selected] = renamed;
				if (menu.Find(selected) is { } row)
				{
					row.Name = renamed;
				}

				break;
			}

			case "UpdateAllGhosting":
				++menu.GhostRefreshes;
				break;

			default:
				// Not modelled: the formatter's fades, and the read-backs
				// (`GetHighlightedMenuItemData`, `IsHighlightedItemAtRoot`, `CountSpawnedItems`),
				// which the hooks answer natively.
				NoteUnhandled(chunk);
				return;
		}

		++menu.Generation;
	}

	/// <summary>
	/// `SpawnButton` — a new row, or an update in place where the name already exists: a
	/// repopulation that did not clear first is restating rows, not duplicating them.
	/// </summary>
	private static void Spawn(Menu menu, Engine_UiModel.MenuChunk chunk)
	{
		string name = chunk.Attribute("String1");
		if (name.Length == 0)
		{
			return;
		}

		Row? row = menu.Find(name);
		if (row is null)
		{
			row = new Row { Name = name, Ordinal = menu.Rows.Count };
			menu.Rows.Add(row);
		}

		row.Label = chunk.Attribute("String2");
		row.Icon = chunk.Attribute("String3");
		row.Parent = chunk.Attribute("String4");
		row.Id = PackedId(chunk);
		row.EventType = (int)chunk.Number("Float3");
		row.Rating = (int)chunk.Number("Float4");

		// Spawn default is not ghosted, no emphasis; re-applied because a respawn under the same
		// name is a fresh row.
		row.Ghosted = false;
		row.Emphasis = Engine_UiMenuConstants.Emphasis.None;
	}

	/// <summary>`Float1` low, `Float2` high — the guest's own 16/16 split of a 32-bit id.</summary>
	private static uint PackedId(Engine_UiModel.MenuChunk chunk) =>
		((uint)(int)chunk.Number("Float2") << 16) | ((uint)(int)chunk.Number("Float1") & 0xFFFFu);

	/// <summary>
	/// The emphasis enum, range-checked to 0..3 — a raw float from Lua; anything else is None.
	/// </summary>
	private static Engine_UiMenuConstants.Emphasis EmphasisFrom(float value)
	{
		int level = (int)value;
		return level >= 0 && level <= 3
			? (Engine_UiMenuConstants.Emphasis)level
			: Engine_UiMenuConstants.Emphasis.None;
	}

	/// <summary>
	/// The effective ghost state — `UpdateAllGhosting`'s two-term rule, `ghost = item.Ghosted or
	/// (EventType == Submenu and #ChildItems == 0 and AutomaticGhostingOnSubmenus)`, so an empty
	/// submenu ghosts itself.
	/// </summary>
	public static bool EffectivelyGhosted(Menu menu, Row row)
	{
		if (row.Ghosted)
		{
			return true;
		}

		if (!menu.AutomaticGhostingOnSubmenus ||
			row.EventType != (int)Engine_UiMenuConstants.EventType.Submenu)
		{
			return false;
		}

		return !menu.Rows.Exists(child =>
			string.Equals(child.Parent, row.Name, System.StringComparison.Ordinal));
	}

	private void NoteUnhandled(Engine_UiModel.MenuChunk chunk)
	{
		string method = chunk.Method;
		_unhandled.TryGetValue(method, out int count);
		_unhandled[method] = count + 1;
		if (count == 0)
		{
			GD.Print($"[menu-model] g_Menu:{method} not modelled — '{chunk.Chunk}' " +
				$"on presentation 0x{chunk.Presentation:X8}");
		}
	}

	/// <summary>One line for the report.</summary>
	public string Describe()
	{
		var parts = new List<string>();
		foreach ((uint presentation, Menu menu) in _menus)
		{
			if (menu.Rows.Count == 0)
			{
				continue;
			}

			int ghosted = 0;
			foreach (Row row in menu.Rows)
			{
				if (EffectivelyGhosted(menu, row))
				{
					++ghosted;
				}
			}

			parts.Add($"0x{presentation:X8}: {menu.Rows.Count} row(s), {ghosted} ghosted" +
				(menu.InitiallySelected != 0 ? $", initial {menu.InitiallySelected}" : string.Empty) +
				(menu.Settled ? ", settled" : string.Empty));
		}

		return parts.Count == 0
			? $"{_applied} statement(s), no menu stated yet"
			: $"{_applied} statement(s) | {string.Join(" | ", parts)}";
	}

	/// <summary>The menus the model holds, without the statement count — the report's change key.</summary>
	public string DescribeMenus()
	{
		string all = Describe();
		int bar = all.IndexOf(" | ", System.StringComparison.Ordinal);
		return bar < 0 ? string.Empty : all[(bar + 3)..];
	}

	/// <summary>
	/// Takes each row's label from the guest's own statements, BY NAME. True when anything changed.
	///
	/// The Babel tags all resolve, but only to the bare noun: the guest composes the live
	/// decoration before it spawns the row — "Abilities" becomes
	/// `Abilities &lt;s4&gt;(2)&lt;/s4&gt;` when there are points to spend, Items and Clothing
	/// pick up `&lt;exclamation.img&gt;` when something inside is new — and `SpawnButton`'s
	/// String2 carries it (`sub_8298DF98`, 0x8298e510..0x8298e5c8), recorded with the row's name.
	/// Host-only and mod rows are not the guest's and are never joined.
	/// </summary>
	/// <param name="clearUnnamed">
	/// Whether a row the stated rows do not name loses a label it took earlier — true for the pause
	/// list, built to match the guest's one for one; false for a template list that deliberately
	/// differs and whose unmatched rows carry their own tags.
	/// </param>
	public static bool ApplyStatedLabels(
		Engine_FrontEndMenu menu,
		System.Collections.Generic.IReadOnlyList<Engine_UiMenuModel.Row> stated,
		bool clearUnnamed = true)
	{
		// An empty capture is no news, not no labels: a pause list torn down for a tab change and
		// not yet repopulated reads the same as "the guest offers no rows".
		if (stated.Count == 0)
		{
			return false;
		}

		var byName = new System.Collections.Generic.Dictionary<string, string>(
			System.StringComparer.Ordinal);
		foreach (Engine_UiMenuModel.Row row in stated)
		{
			if (row.Name.Length != 0 && row.IsRoot && row.Label.Length != 0)
			{
				byName[row.Name] = row.Label;
			}
		}

		bool changed = false;
		foreach (Engine_UiExpandableMenu.Item item in menu.Roots)
		{
			if (!item.Available || item.HostOnly || item.FromMod)
			{
				continue;
			}

			if (byName.TryGetValue(item.Name, out string? label))
			{
				if (!string.Equals(item.StatedLabel, label, System.StringComparison.Ordinal))
				{
					item.StatedLabel = label;
					changed = true;
				}
			}
			else if (clearUnnamed && item.StatedLabel is not null)
			{
				// The guest's population is up and does not carry this row: its label belongs to a
				// list that no longer exists.
				item.StatedLabel = null;
				changed = true;
			}
		}

		return changed;
	}
}
