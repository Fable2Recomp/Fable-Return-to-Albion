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
	/// <summary>One row, as the C++ stated it.</summary>
	public sealed class Row
	{
		public required string Name { get; init; }

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
	public void Apply(Engine_UiModel.MenuChunk chunk)
	{
		Menu menu = For(chunk.Presentation);
		++_applied;

		switch (chunk.Method)
		{
			case "SpawnButton":
				Spawn(menu, chunk);
				break;

			case "DeleteAllSpawnedMenuItems":
				// The list boundary: rows, settle marker and initial selection all go, because
				// the next population states its own.
				menu.Rows.Clear();
				menu.Settled = false;
				menu.InitiallySelected = 0;
				break;

			case "SetItemGhosting":
				if (menu.Find(chunk.Attribute("String1")) is { } ghosting)
				{
					// The guest's own test: `Float1 == 0` is false, anything else true.
					ghosting.Ghosted = chunk.Number("Float1") != 0f;
				}

				break;

			case "SetItemEmphasis":
				if (menu.Find(chunk.Attribute("String1")) is { } emphasised)
				{
					emphasised.Emphasis = EmphasisFrom(chunk.Number("Float1"));
				}

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
					int.TryParse(chunk.Inline, out int selected) ? selected : 0;
				break;

			case "TurnOffAutomaticGhostingOnSubmenus":
				menu.AutomaticGhostingOnSubmenus = false;
				break;

			case "UpdateAfterSpawning":
				// Only the front-end lists send it; the in-game ones return, so its absence is
				// not evidence the list is incomplete.
				menu.Settled = true;
				break;

			default:
				// Not modelled: the menu stack (`PushMenuState`, `PopMenuState`), input gating
				// (`SetExpandableMenuDisabled`), the formatter's fades, and the read-backs
				// (`GetHighlightedMenuItemData`, `IsHighlightedItemAtRoot`, `CountSpawnedItems`)
				// which are questions with no effect to apply.
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
}
