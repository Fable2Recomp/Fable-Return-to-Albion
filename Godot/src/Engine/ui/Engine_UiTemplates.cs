using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// One menu entry — a tab, a sub-tab, a command — as data: <see cref="Name"/> is the identity a
/// dispatch recognises, <see cref="Tag"/> localises the label, <see cref="Command"/> is what
/// activating it does, <see cref="Children"/> nest sub-entries in place and <see cref="Opens"/>
/// names another surface. The AUTHORING form a JSON file and the editor hold; it maps one-to-one
/// onto the runtime model <see cref="Engine_UiExpandableMenu.Item"/>.
/// </summary>
public sealed class Engine_UiMenuEntry
{
	public string Name = string.Empty;
	public string Tag = string.Empty;
	public string? Command;
	public string? Opens;
	public bool HostOnly;

	/// <summary>
	/// A row the GUEST spawns that we do not show. The population tables are the guest's own
	/// spawn lists, so an authored list is not free to simply omit a row — the row arrives from
	/// the guest's `g_Menu` statements whatever the template says. This is the statement that it
	/// goes: `Engine_UiScreenMenu` runs `RemoveChild` on it, the guest's own call, and the row
	/// and everything under it leave the list.
	///
	/// Use it for a row whose action belonged to the console rather than to the game — the
	/// logbook's `Achievements`, whose answer was `XamShowAchievementsUI`.
	/// </summary>
	public bool Drop;

	/// <summary>
	/// The row's icon texture, as `SpawnButton` takes it — a `.tex` path relative to the game's
	/// art root, or empty for a row that draws none.
	/// </summary>
	public string Icon = string.Empty;

	/// <summary>
	/// The ROW CONDITION this entry is built under (<see cref="Engine_UiRowConditions"/>): a name,
	/// <c>!name</c>, or several joined by <c>,</c> that must all hold. Empty always holds. Siblings
	/// may share a name when their conditions never hold together — the pause root states the hero's
	/// <c>Map</c> and player two's <c>Map</c> that way — so one record serves both and a mod patches
	/// it once.
	/// </summary>
	public string When = string.Empty;

	/// <summary>
	/// The event this entry pushes, as `g_Menu.EventConst` numbers it. `Submenu` (301) is the
	/// marker a category carries, and it is the DEFAULT for an entry with children — the guest's
	/// own category rows are spawned with exactly that.
	/// </summary>
	public int EventType;

	/// <summary>
	/// The star or pip rating — see <see cref="Engine_UiExpandableMenu.Item.RatingSlide"/>.
	/// Defaults to the empty slide, which is what a menu of commands wants.
	/// </summary>
	public int Rating = Engine_UiMenuConstants.EmptyRatingSlide;

	/// <summary>
	/// Where this entry's children come from at RUNTIME, or empty for a hand-authored list: the
	/// guest supplies the list, the template the shape (<see cref="Engine_UiMenuSources"/>).
	/// Authored children and a source compose — the authored ones spawn first, in order, then the
	/// source's.
	/// </summary>
	public string Source = string.Empty;

	/// <summary>
	/// Whether this category's caption carries `" (N)"` for how many rows it holds.
	///
	/// The guest has TWO header spawners and the difference is exactly this field.
	/// `sub_826CF098` takes a pointer to the category's count pair and appends
	/// `" (" .. count .. ")"` when the first is above zero (0x826CF0D8), plus a second marker when
	/// the second is; `sub_826CF020` takes no count at all. The clothing screen calls the first
	/// for its seven garment slots and the second for `Suits` — which is why the real screen
	/// reads `Headgear (6)` but plain `Outfits`.
	/// </summary>
	public bool Counted = true;

	public List<Engine_UiMenuEntry> Children = new();
}

/// <summary>A named menu — its entries and which one starts highlighted.</summary>
public sealed class Engine_UiMenuTemplate
{
	public string Name = string.Empty;
	public string Highlight = string.Empty;

	/// <summary>
	/// `SetRootTitle` — the heading a screen shows above this list, as a Babel tag or literal. It
	/// lives on the root item, which is never drawn and outlives every repopulation.
	/// </summary>
	public string Title = string.Empty;

	/// <summary>
	/// Where the ROOT's rows come from at runtime, or empty for a hand-authored list — the same
	/// contract as <see cref="Engine_UiMenuEntry.Source"/> one level up. A host-owned list screen
	/// is a list, not a folder: its rows are headings, so a source hung on an entry would nest
	/// them one level too deep and make the whole screen a fold.
	///
	/// Authored entries and a source compose, the authored ones first.
	/// </summary>
	public string Source = string.Empty;

	public List<Engine_UiMenuEntry> Entries = new();

	/// <summary>
	/// A host action every SOURCED row on this menu performs, unless the row names its own.
	///
	/// Some screens do not answer a row by its identity at all. The furniture screens are the
	/// case: their rows carry event type 0, and the press handler at the screen's `vtable+20`
	/// (`sub_826B65F0`) has cases for 140 and 141 and falls straight out of everything else — an
	/// identity push there does nothing whatever. The commit runs from the INPUT handler on code
	/// 36 instead, which reads the highlighted row out of Lua
	/// (`sub_826C1B50` -&gt; `g_Menu:GetHighlightedMenuItemData()`) rather than off the event.
	///
	/// So the row's action is "press A on the screen", stated once for the whole menu.
	/// </summary>
	public string RowCommand = string.Empty;

	/// <summary>The authored entry with this name, at any depth, or null when none is authored.</summary>
	public Engine_UiMenuEntry? Entry(string name)
	{
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			if (Entry(entry, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static Engine_UiMenuEntry? Entry(Engine_UiMenuEntry entry, string name)
	{
		if (string.Equals(entry.Name, name, System.StringComparison.Ordinal))
		{
			return entry;
		}

		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			if (Entry(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	/// <summary>
	/// Re-syncs every SOURCED row against what its source says now. True when anything moved.
	/// Separate from `Build` because the shape is authored once at adoption while a source (the
	/// spawn capture above all) fills in later and keeps changing.
	///
	/// Authored children are never touched and the parent's fold survives: only rows carrying
	/// <see cref="Engine_UiExpandableMenu.Item.Sourced"/> are replaced, and only when the set
	/// differs — otherwise a steady source would rebuild its subtree every frame, cursor and all.
	/// </summary>
	public bool RefreshSources(Engine_FrontEndMenu menu)
	{
		bool changed = false;
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			changed |= RefreshEntry(menu, entry, RowCommand);
		}

		if (Source.Length != 0)
		{
			changed |= RefreshUnder(
				menu, menu.Model.MenuRoot, Engine_UiExpandableMenu.RootName, Source, Name,
				RowCommand);
		}

		return changed;
	}

	private static bool RefreshEntry(
		Engine_FrontEndMenu menu, Engine_UiMenuEntry entry, string rowCommand)
	{
		bool changed = false;
		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			changed |= RefreshEntry(menu, child, rowCommand);
		}

		if (entry.Source.Length == 0 || menu.Find(entry.Name) is not { } item)
		{
			return changed;
		}

		return changed |
			RefreshUnder(menu, item, entry.Name, entry.Source, entry.Name, rowCommand);
	}

	/// <summary>
	/// Re-syncs the sourced rows directly under one parent — an entry or the root. Only rows
	/// carrying <see cref="Engine_UiExpandableMenu.Item.Sourced"/> are touched, so an authored
	/// sibling keeps its place and the parent's fold survives.
	/// </summary>
	private static bool RefreshUnder(
		Engine_FrontEndMenu menu,
		Engine_UiExpandableMenu.Item item,
		string parentName,
		string source,
		string argument,
		string rowCommand)
	{
		List<Engine_UiMenuSources.Row> rows =
			Expanded(Engine_UiMenuSources.Rows(source, argument));

		// `current` in the order `Expanded` states: each sourced child, and under a group
		// HEADER its own sourced rows — `SpawnSourced` hangs a grouped row under its header, so
		// counting direct children alone never matched and a grouped list respawned, fold and
		// cursor lost, on every sample.
		var direct = new List<Engine_UiExpandableMenu.Item>();
		var current = new List<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item child in item.Children)
		{
			if (!child.Sourced)
			{
				continue;
			}

			direct.Add(child);
			current.Add(child);
			if (child.EventType == (int)Engine_UiMenuConstants.EventType.Submenu)
			{
				foreach (Engine_UiExpandableMenu.Item grouped in child.Children)
				{
					if (grouped.Sourced)
					{
						current.Add(grouped);
					}
				}
			}
		}

		if (Same(current, rows))
		{
			// The SAME rows, possibly re-emphasised — the world map's primary quest moving from
			// one row to another after `SetAsPrimary`. Written onto the standing items rather
			// than respawned, so the fold and the cursor survive; the view's signature carries
			// the emphasis and re-tints on its next pass.
			bool restated = false;
			for (int i = 0; i < rows.Count; ++i)
			{
				Engine_UiMenuConstants.Emphasis wanted =
					rows[i].Emphasis != Engine_UiMenuConstants.Emphasis.None ? rows[i].Emphasis
					: rows[i].Emphasised ? Engine_UiMenuConstants.Emphasis.PrimaryQuest
					: Engine_UiMenuConstants.Emphasis.None;
				if (current[i].Emphasis != wanted)
				{
					current[i].Emphasis = wanted;
				}

				// The rest of what a row states can change under an unchanged name and label
				// (a viewed flag, a new ticket in its command); restated in place. Not `Ghosted`:
				// the guest's by-name ghosting writes that after the source has spoken.
				Engine_UiMenuSources.Row row = rows[i];
				if (row.IsHeader)
				{
					continue;
				}

				// The rating too: an ability's rank changes under a label that does not (every
				// `..._TOUGHNESS_NAME_LEVEL_0N` reads "Toughness"), so a purchase restates only it.
				string? command = row.Command ?? (rowCommand.Length != 0 ? rowCommand : null);
				if (current[i].Unviewed != row.Unviewed ||
					current[i].Counted != row.Counted ||
					current[i].RatingSlide != row.Rating ||
					!string.Equals(current[i].Command, command, System.StringComparison.Ordinal))
				{
					current[i].Unviewed = row.Unviewed;
					current[i].Counted = row.Counted;
					current[i].RatingSlide = row.Rating;
					current[i].Command = command;
					restated = true;
				}
			}

			return restated;
		}

		foreach (Engine_UiExpandableMenu.Item stale in direct)
		{
			item.Children.Remove(stale);
			menu.Model.Forget(stale);
		}

		SpawnSourced(menu, rows, parentName, rowCommand);
		menu.Model.UpdateAfterSpawning();
		return true;
	}

	/// <summary>
	/// Spawns a source's rows under <paramref name="parentName"/>, creating a HEADER row for each
	/// distinct <see cref="Engine_UiMenuSources.Row.Group"/> as it is first seen.
	///
	/// A group header is a submenu row exactly like an authored category, so the fold, the
	/// ghosting and the cursor all behave the same — the only difference is that it exists
	/// because the data had rows for it rather than because a template listed it. Groups appear
	/// in first-seen order, which for the inventory is the order the guest's own list walks.
	/// </summary>
	internal static void SpawnSourced(
		Engine_FrontEndMenu menu,
		IReadOnlyList<Engine_UiMenuSources.Row> rows,
		string parentName,
		string rowCommand = "")
	{
		foreach (Engine_UiMenuSources.Row row in rows)
		{
			string under = row.Group.Length != 0 && !row.IsHeader ? row.Group : parentName;

			Engine_UiExpandableMenu.Item sourced = menu.Model.SpawnButton(
				row.Name, row.Label, row.Icon, row.EventType,
				idLow: (int)(row.Id & 0xFFFF), idHigh: (int)((row.Id >> 16) & 0xFFFF),
				row.Rating, under);
			sourced.StatedLabel = row.IsHeader ? null : row.Label;
			sourced.Committable = row.Committable;
			sourced.Sourced = true;
			sourced.Ghosted = row.Ghosted;
			sourced.Emphasis = row.Emphasis != Engine_UiMenuConstants.Emphasis.None
				? row.Emphasis
				: row.Emphasised ? Engine_UiMenuConstants.Emphasis.PrimaryQuest : Engine_UiMenuConstants.Emphasis.None;
			sourced.Counted = row.Counted;
			sourced.Unviewed = row.Unviewed;
			// The row's own action wins; the menu's stands in for a screen whose rows all commit
			// the same way — see `Engine_UiMenuTemplate.RowCommand`. Never on a HEADER: a header
			// folds, it does not activate.
			sourced.Command = row.Command ??
				(!row.IsHeader && rowCommand.Length != 0 ? rowCommand : null);
		}
	}

	/// <summary>
	/// A source's rows with a HEADER inserted before the first row of each group, in first-seen
	/// order. One sequence, used both to spawn and to decide whether anything changed — if the
	/// two disagreed the list would rebuild every frame, because what is in the model after a
	/// spawn is this and not the source's rows.
	///
	/// A header is a submenu row with the group's Babel tag and no stated label, so the view
	/// resolves the tag exactly as it does for an authored category.
	/// </summary>
	private static List<Engine_UiMenuSources.Row> Expanded(
		IReadOnlyList<Engine_UiMenuSources.Row> rows)
	{
		var seen = new HashSet<string>(System.StringComparer.Ordinal);
		var expanded = new List<Engine_UiMenuSources.Row>(rows.Count);

		foreach (Engine_UiMenuSources.Row row in rows)
		{
			if (row.Group.Length != 0 && seen.Add(row.Group))
			{
				expanded.Add(new Engine_UiMenuSources.Row(
					Name: row.Group,
					Label: row.GroupTag.Length != 0 ? row.GroupTag : row.Group,
					Icon: string.Empty,
					EventType: (int)Engine_UiMenuConstants.EventType.Submenu,
					Id: 0u,
					Rating: Engine_UiMenuConstants.EmptyRatingSlide,

					// A header carries no identity of the guest's and performs nothing itself.
					Committable: false,
					Ghosted: false,
					Command: null,
					Group: row.Group,
					GroupTag: row.GroupTag,
					IsHeader: true));
			}

			expanded.Add(row);
		}

		return expanded;
	}

	private static bool Same(
		List<Engine_UiExpandableMenu.Item> current,
		IReadOnlyList<Engine_UiMenuSources.Row> rows)
	{
		if (current.Count != rows.Count)
		{
			return false;
		}

		for (int i = 0; i < rows.Count; ++i)
		{
			// Against what the SPAWN would have set, not against the row's own label: a group
			// header is spawned with no stated label so the view resolves its Babel tag, and
			// comparing that null to the tag would report a difference on every sample and
			// rebuild the list — cursor, fold and all — every frame.
			// A header is compared by NAME alone: the bound list writes its "Caption (N)" count
			// onto the header's stated label afterwards.
			if (!string.Equals(current[i].Name, rows[i].Name, System.StringComparison.Ordinal) ||
				(!rows[i].IsHeader &&
				!string.Equals(current[i].StatedLabel, rows[i].Label, System.StringComparison.Ordinal)))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Builds the runtime model this template describes, through `SpawnButton` — the shipped
	/// construction the guest's own C++ makes for every row, taking the icon, the event, the id
	/// pair and the rating together.
	/// </summary>
	public Engine_FrontEndMenu Build(IReadOnlyDictionary<string, bool>? facts = null)
	{
		var menu = new Engine_FrontEndMenu();
		BuildInto(menu, committable: false, facts);
		return menu;
	}

	/// <summary>
	/// The same construction into an EXISTING model — for a list whose view is already bound to
	/// it, which cannot be handed a new object.
	/// </summary>
	/// <param name="committable">
	/// Whether these rows carry a guest identity. FALSE for a hand-authored list, where
	/// `(EventType, 0)` would resolve row zero and the row must dispatch by name or command.
	/// TRUE for a POPULATION table, where the name and the event type are transcribed from the
	/// guest's own spawn script and `(name, type, 0)` is exactly what its `SelectMenuItem`
	/// pushes — so the row commits by identity like any row the guest spawned itself.
	/// </param>
	/// <param name="facts">The row conditions the BUILDER knows (the pause list's <c>player-two</c>),
	/// asked before the registered ones — see <see cref="Engine_UiMenuEntry.When"/>.</param>
	public void BuildInto(Engine_FrontEndMenu menu, bool committable, IReadOnlyDictionary<string, bool>? facts = null)
	{
		menu.Clear();
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			Spawn(entry, Engine_UiExpandableMenu.RootName);
		}

		// The ROOT's own source, after the authored entries so a fixed row keeps the top.
		SpawnSourced(menu, Expanded(Engine_UiMenuSources.Rows(Source, Name)),
			Engine_UiExpandableMenu.RootName, RowCommand);

		// `UpdateAfterSpawning` — the population is complete, so the visible list and the
		// automatic ghosting are recomputed once rather than per entry.
		menu.Model.UpdateAfterSpawning();
		menu.Model.UpdateAllGhosting();

		// `SetRootTitle` where the record names one; the root is never drawn itself, but the
		// title is what a screen shows above the list.
		if (Title.Length != 0)
		{
			menu.Model.SetRootTitle(Title);
		}

		if (Highlight.Length != 0)
		{
			menu.Highlight(Highlight);
		}

		void Spawn(Engine_UiMenuEntry entry, string parent)
		{
			if (!Engine_UiRowConditions.Holds(entry.When, facts))
			{
				return;
			}

			// A category is a submenu row: an entry with children stating no event of its own
			// gets 301, which is what makes `SelectMenuItem` treat the press as a fold.
			// A SOURCE MAKES IT A SUBMENU TOO, not just authored children. The rows arrive after
			// the build — that is what a source is — so an entry with only sourced children was
			// spawned as a command row, and a command row never folds however many rows land
			// under it later. `Games` folded only because it happens to author one child.
			int eventType = entry.EventType != 0
				? entry.EventType
				: entry.Children.Count > 0 || entry.Source.Length != 0
					? (int)Engine_UiMenuConstants.EventType.Submenu
					: 0;

			Engine_UiExpandableMenu.Item item = menu.Model.SpawnButton(
				entry.Name, entry.Tag, entry.Icon, eventType,
				idLow: 0, idHigh: 0, entry.Rating, parent);

			// Host routing is not menu content, so it rides beside the spawn.
			item.Command = entry.Command;
			item.Opens = entry.Opens;
			item.HostOnly = entry.HostOnly;
			Engine_UiScreenActions.RefreshSettingLabel(item);

			// See the `committable` parameter: a hand-authored row has no guest identity, a
			// population-table row is the guest's own.
			item.Committable = committable;

			foreach (Engine_UiMenuEntry child in entry.Children)
			{
				Spawn(child, entry.Name);
			}

			// Then whatever the source supplies, after the authored children so a fixed row keeps
			// its place at the top. A source resolving to nothing leaves the entry as authored.
			// ONE SPAWN PATH FOR EVERY SOURCE, so a row that declares a GROUP gets its header
			// wherever it was sourced from, and the identity, ghosting and command channels
			// cannot drift between the three call sites — they already had, this one carrying a
			// `Command` the other two dropped.
			SpawnSourced(menu, Expanded(Engine_UiMenuSources.Rows(entry.Source, entry.Name)),
				entry.Name, RowCommand);
		}
	}
}

/// <summary>
/// Everything Godot knows about one in-game screen, as one record: whether it owns a list, its
/// panel fields, where the preview hangs, which chrome units an owned book clones. A shipped
/// screen is a record, a modded screen the same record from a file, a new menu a record the
/// editor writes; the runtime that consumes them is written once.
/// </summary>
public sealed class Engine_UiScreenTemplate
{
	public uint ScreenId;
	public string Name = string.Empty;

	/// <summary>Whether Godot owns this screen's ExpandableMenu list.</summary>
	public bool OwnsList;

	/// <summary>
	/// The menu template this screen's list is built from when the WIDGET NAMES NONE.
	///
	/// Most screens state a `MenuPopulationScript` and the list is looked up by that name. The
	/// gift family does not: `sub_82430468` builds screen 15's rows in native C++ and the only
	/// Lua it issues is `g_Menu:DeleteAllSpawnedMenuItems()` and `g_Menu:SpawnButton()`, so
	/// neither the attribute nor the `RepopulateMenuFromPopulationScript` fallback has anything
	/// to offer and the list dropped onto the raw model. This is the third way to say it, and it
	/// is a template field so a mod can rebind a screen without a rebuild.
	/// </summary>
	public string MenuScript = string.Empty;

	/// <summary>
	/// The menu template per SCREEN MODE, for a screen that is really several lists behind one
	/// id. Keyed by the guest's own mode value; <see cref="MenuScript"/> stands in when the mode
	/// is unknown or unlisted.
	///
	/// Screen 15 is the case and the only one: `sub_829B0A58` reads `screen+372` and spawns the
	/// item headers on 1, the `Melee`/`Ranged` pair on 3, and the shared row builder then keeps
	/// only the bucket that matches. Binding the id to one script would put the item categories
	/// on the weapons list.
	/// </summary>
	public Dictionary<int, string> MenuScriptByMode = new();

	/// <summary>
	/// How far the owned row view stands in FRONT of the mirrored tree, in Z — per screen,
	/// because the books are not z-ordered alike. The pause menu puts its `LeftFrame` cover
	/// INSIDE `ExpandableMenu` at +10 with `Buttons` at 0, so its rows must sit on the pool's own
	/// plane or the tabs draw in front of the cover; other screens carry page art in front of the
	/// pool, where a view on the pool's plane disappears behind it. Default
	/// `Engine_UiPresentationDepth.Step / 4` clears ordinary page art; pause overrides it to 0.
	/// </summary>
	public float ListLift = Engine_UiPresentationDepth.Step / 4f;

	/// <summary>The info-panel fields the screen states, dotted paths in its document.</summary>
	public List<Engine_UiScreenPanel.Field> PanelFields = new();

	/// <summary>
	/// Roots whose every text descendant the panel owns — see
	/// <see cref="Engine_UiScreenPanel.FieldsUnder"/>. Combined with <see cref="PanelFields"/>,
	/// so a screen can list the few fields it lays out specially and sweep the rest.
	/// </summary>
	public List<string> PanelSubtrees = new();

	/// <summary>Where the 3D preview hangs, or empty when the screen has none.</summary>
	public string PreviewAnchor = string.Empty;

	/// <summary>
	/// The element whose live slide says a second input LEVEL is up (the options category
	/// page), with the slide value that means "open". Empty when the screen has one level.
	/// </summary>
	public string PageElement = string.Empty;

	public int PageSlide;

	/// <summary>
	/// The `.bgf` this screen is drawn from, without extension — the guest's own mapping,
	/// `std::map<int,string>` at `manager+0xA0`. Not every owned surface is the current screen:
	/// the spell wheel (id 2) is opened by its own flag and simply appears in the presentation
	/// registry, so keying the owned build on the live presentation reaches those. Three ids
	/// share `GiftGivingScreen` (15, 25, 27), so a stated id wins and this is the fallback.
	/// </summary>
	public string Presentation = string.Empty;

	/// <summary>Chrome units an OWNED build clones from the pattern book, authored spots kept.</summary>
	public List<string> ChromeUnits = new();

	/// <summary>
	/// Which chrome unit is the PAGE STACK — the parent anything page-local hangs under (map
	/// tokens, quest markers). Empty when the screen has no page-local content.
	/// </summary>
	public string PageRootUnit = string.Empty;

	/// <summary>
	/// THE RIGHT-HAND PAGES THIS SCREEN HAS, as element paths — the set one of which is shown
	/// and the rest switched off.
	///
	/// A RECORD, NOT A CONSTANT, because the books do not agree and a mod's book agrees with
	/// none of them. The shipped ones alone run Page1+Page2, Page1+Page2+Page3 (shop),
	/// Page1+Page2+Page4 (abilities — there is no Page3) and Page1..Page4 (options).
	/// Omitting a page is not neutral: one nobody switches off keeps drawing over whichever is
	/// up.
	/// </summary>
	public List<string> Pages = new();

	/// <summary>
	/// Where the page stack sits when a page is up, or null to leave it where the file put it.
	///
	/// THE BOOKS DISAGREE, and it cost a whole debugging pass: `Foreground.Book.Pages` is
	/// authored at x = 0 in `giftgivingscreen` and `equipmentscreen` — already on the leaf — but
	/// at **x = 400** in `abilitiesscreen`, `shopscreen` and `worldmapscreen`, which is off a
	/// 960-wide canvas whose right edge is 480. Identical, correct page-selection code therefore
	/// showed a page on the items screen and an empty leaf on the abilities screen.
	///
	/// Godot owns the layout, so this is stated per screen rather than recovered from the file's
	/// own slides — and a modder's screen states its own.
	/// </summary>
	public Vector3? PageStackAt;

	/// <summary>
	/// A full-canvas backdrop tint to stand behind the book, by art name. Empty for none. Cloned
	/// OVERSIZED: the shipped quad is exactly 1280x720 and leaves the revealed edges untinted at
	/// any other aspect.
	/// </summary>
	public string BackdropUnit = string.Empty;

	/// <summary>
	/// For a modal BOX drawn by its own view rather than as a book: the `.bgf` it is cloned
	/// from. Separate from <see cref="Presentation"/>, which names a SCREEN's live file.
	/// </summary>
	public string BoxPresentation = string.Empty;

	/// <summary>The art units a box's view clones, by ROLE — the position is the role, stated
	/// on each built-in record.</summary>
	public List<string> BoxUnits = new();

	/// <summary>
	/// Element paths inside a box's art that its view reads or writes (texts, button rows,
	/// prompts), keyed by the built-in path. See <see cref="Engine_UiTemplates.BoxPath"/>.
	/// </summary>
	public Dictionary<string, string> BoxPaths = new(System.StringComparer.Ordinal);

	/// <summary>
	/// THE WIDE LAYOUT — what the screen does on a window wider than 16:9, as element paths.
	/// The books are authored to the 16:9 edges; wider than that the pieces in
	/// <see cref="WideLeft"/> ride out to the left edge and those in <see cref="WideRight"/> to
	/// the right (by <see cref="Engine_UiSceneBuilder.WideShift"/>), so the covers stay on the
	/// screen edges and the backdrop shows between the pages. A piece in neither list stays
	/// where it is — a centred card states nothing. The owned row list rides with
	/// <see cref="WideLeft"/> when that list is not empty, and a page's clip window follows
	/// its page.
	/// </summary>
	public List<string> WideLeft = new();

	/// <summary>See <see cref="WideLeft"/>.</summary>
	public List<string> WideRight = new();

	/// <summary>
	/// Page PAPER that widens instead of riding: it keeps its gutter edge and reaches the
	/// same distance further out, for a screen with no backdrop to show between the pages
	/// (the co-op screen), so the paper still meets in the middle under frames that rode out.
	/// </summary>
	public List<string> WideStretch = new();

	/// <summary>
	/// Chrome UNITS that are the screen's ground rather than its furniture (the world map's
	/// tiles): scaled about the canvas centre with the window's width, so the picture keeps
	/// its 16:9 framing and still reaches both edges.
	/// </summary>
	public List<string> WideGround = new();

	/// <summary>
	/// Elements INSIDE a cloned unit that a Godot-owned view already draws, by element name — not
	/// the same statement as leaving a unit out of <see cref="ChromeUnits"/>, since these are
	/// nested (`Book` carries the page stack, which carries `loadgame`). The subtree is dropped
	/// at bind time, root and all; the guest keeps its own copies live.
	///
	/// An entry may be a dotted tail, and for `Buttons` it must be: that name has six bearers in
	/// `abilitiesscreen.bgf`, so `ExpandableMenu.Buttons` names as much of the path as it takes
	/// to be unambiguous. A bare name matches every bearer.
	/// </summary>
	public List<string> OwnedElsewhere = new();

	/// <summary>
	/// Units whose TEXT the host takes from the guest's own live writes, as data. The owned
	/// books keep the guest out of the text path on purpose (a field the guest stopped
	/// writing went quietly stale); a unit named here is one the guest fills natively, once,
	/// at its screen's show — the shop's front page — and the host has nothing better than
	/// what the guest computed. A host value on a field under the unit still wins.
	/// </summary>
	public List<string> GuestTextUnits = new();

	/// <summary>Live text fields an owned build re-meshes in place, dotted paths.</summary>
	public List<string> LiveTexts = new();

	/// <summary>The static menu this screen offers, or empty when the guest supplies rows.</summary>
	public string Menu = string.Empty;

	/// <summary>
	/// The guest stat ids this screen's page shows, each with the element path it fills.
	///
	/// PURE DATA, and it was a 93-entry array inside the page class. It is the screen's own
	/// content — which stat appears where — so it belongs with the screen, where the editor can
	/// see it and a new row is a record rather than a recompile.
	/// </summary>
	public List<(int Id, string Path)> StatFields = new();

	/// <summary>
	/// Whether this screen takes input directly once revealed, rather than through the row list.
	/// The one-off panels do — they are reached only through the GameAction chain, which is dead
	/// under Godot's cursor.
	/// </summary>
	public bool OwnsRevealedInput;

	/// <summary>The action the accept button runs while revealed — see
	/// <see cref="Engine_UiScreenActions"/>. Empty for a screen with none.</summary>
	public string RevealedAccept = string.Empty;

	/// <summary>The action the back button runs while revealed.</summary>
	public string RevealedBack = string.Empty;

	/// <summary>
	/// The action the flip button runs on this screen. Abilities is the one that has one: its
	/// flip unlearns the highlighted ability.
	/// </summary>
	public string FlipAction = string.Empty;

	/// <summary>Whether this screen carries the spell-assignment blade.</summary>
	public bool HasSpellBlade;

	/// <summary>
	/// The screen's SCROLL REGION, as `sub_828C3250` registers one: a content group whose rows
	/// the right stick slides, the mask element whose band is the window, and the scrollbar
	/// element whose arrows and thumb say where the view stands. Empty means no region.
	/// The guest's own scroll (`sub_829ED788`) moves each registered row's Y inside the mask's
	/// clip; the owned book moves the group, which is the same transform.
	/// </summary>
	public string ScrollContent = string.Empty;

	public string ScrollMask = string.Empty;

	public string ScrollBar = string.Empty;

	/// <summary>
	/// The measured-text REFLOW, as `sub_829AB8A8` runs one: the element whose wrapped height is
	/// measured, the authored base marking where the block below starts, and every element that
	/// moves by the overflow delta. The guest never clips or shortens the measured text — the
	/// rows below it move down.
	/// </summary>
	public string FlowMeasured = string.Empty;

	public string FlowBase = string.Empty;

	public List<string> FlowMoved = new();

	/// <summary>
	/// The reflow's tail — `0x829abcdc`: the rows here stand at the anchor row's Y, less 30
	/// while the anchor itself draws (Mystery Island's gargoyle line over the silver keys).
	/// </summary>
	public string FlowTailAnchor = string.Empty;

	public List<string> FlowTailMoved = new();

	/// <summary>
	/// Whether the row list fades its TOP end. Per-screen and not Lua: `FadeOutTopItemEarly`
	/// (`expandablemenuformatting.dis main.0`) is run as `DoString` source from the screen
	/// contract vtables, and options (18, `0x826D9D40`) is the one screen that asks for the top
	/// end only — so real estate is the one that does NOT fade its top.
	/// </summary>
	public bool FadesTopEnd = true;

	/// <summary>
	/// Whether the row list fades its BOTTOM end. Real estate (28, `0x82998300`) asks for the
	/// bottom end only, so options is the one that does NOT fade its bottom.
	/// </summary>
	public bool FadesBottomEnd = true;

	/// <summary>
	/// The detail-page provider this screen uses, by name — see
	/// <see cref="Engine_UiPageProviders"/>. Empty means the screen has no host-filled page and
	/// every field stays on the guest's own channel.
	///
	/// THIS IS WHAT MAKES A SCREEN DATA. It used to be six switch statements on the screen id in
	/// `Engine_UiDetailPages`, one per question asked of a page, so a screen's behaviour was
	/// spread across six places and each screen could break its own way. Naming the provider here
	/// puts every screen on one path.
	/// </summary>
	public string Provider = string.Empty;

	/// <summary>
	/// THE DATA PROVIDER'S PAGE (<see cref="Engine_UiPageProviders.Data"/>): element path to what
	/// the field says — a literal, a Babel tag, <c>mod:&lt;key&gt;</c>, <c>player:&lt;key&gt;</c> or
	/// <c>row:label</c>/<c>row:name</c> (<see cref="Engine_UiValueSource"/>); <c>{row}</c> in a
	/// binding is the highlighted row's name. How a mod fills a screen of its own without C#.
	/// </summary>
	public Dictionary<string, string> Fields = new(System.StringComparer.Ordinal);

	/// <summary>
	/// The data provider's switches: element path to a binding that resolves on (<c>true</c>, a
	/// non-zero value) or off — see <see cref="Fields"/>.
	/// </summary>
	public Dictionary<string, string> Switches = new(System.StringComparer.Ordinal);

	/// <summary>A member-wise copy; <see cref="Engine_UiTemplateJson.Clone"/> deepens it.</summary>
	public Engine_UiScreenTemplate ShallowCopy() => (Engine_UiScreenTemplate)MemberwiseClone();
}

/// <summary>
/// How a MODAL answers — the input codes an owned box sends back through the guest's router, one
/// row per box id.
///
/// <see cref="Confirm"/> is A and <see cref="Cancel"/> is B; a null Cancel means B does nothing,
/// which the shop's purchased box genuinely wants (it answers 36 to continue and 75 to EQUIP,
/// and the X action stays unmasked and reaches the guest directly).
/// </summary>
public sealed class Engine_UiModalTemplate
{
	/// <summary>The guest's modal id, at `guiOwner+44`.</summary>
	public uint ModalId;

	/// <summary>
	/// The screen underneath, when the answer depends on it — `uint.MaxValue` for "any". The same
	/// box answers differently per screen: query id 0 dismisses on 36 at the shop and 46 at real
	/// estate, and photo mode's copy has no dismiss at all, so the id alone is not the key.
	/// </summary>
	public uint ScreenId = uint.MaxValue;

	public uint Confirm;

	public uint? Cancel;

	/// <summary>
	/// True for a MessageBox (one dismiss code) rather than a QueryBox (confirm and cancel).
	/// Explicit, not inferred from `Cancel == null`: "B does nothing" and "this box has only a
	/// dismiss" are different statements.
	/// </summary>
	public bool Dismissal;
}

/// <summary>
/// The template registry: the guest's population tables, then the GAME'S OWN FILE
/// (<see cref="Engine_UiGameTemplates"/>, the records below generated into the format a mod's file
/// takes), then every enabled mod's <c>ui/templates.json</c> in load order, then the player's
/// <c>user://ui/templates.json</c> — each composed by the same <see cref="Engine_UiTemplateSet"/> — and
/// snapshotted to <c>user://ui/templates.snapshot.json</c> so the editor and modders start from what
/// actually runs.
///
/// The authored records are the decoded contracts as records — entry names the dispatch strcmps on,
/// event params, tab ids — so an override renaming an entry's LABEL keeps working and one renaming
/// its NAME stops dispatching, exactly as the guest behaves.
/// </summary>
public static class Engine_UiTemplates
{
	public const string OverridePath = "user://ui/templates.json";
	public const string SnapshotPath = "user://ui/templates.snapshot.json";

	/// <summary>Where a mod ships its templates, inside its package.</summary>
	public const string ModTemplatesPath = "ui/templates.json";

	/// <summary>
	/// Two mods stating the same template piece: the higher-priority one won, the other is named.
	/// For the mod tooling's conflict view.
	/// </summary>
	public readonly record struct ModConflict(string Piece, string Winner, string Loser);

	/// <summary>The composition that runs: the game's file, the enabled mods', the player's.</summary>
	private static Engine_UiTemplateSet? _set;

	private static Engine_UiTemplateSet Set
	{
		get
		{
			EnsureLoaded();
			return _set!;
		}
	}

	public static IReadOnlyList<ModConflict> ModConflicts => Set.Conflicts;

	/// <summary>Every screen record, for a caller that works across the whole set — the component
	/// exporter and the editor's listing.</summary>
	public static IEnumerable<Engine_UiScreenTemplate> Screens => Set.Screens.Values;

	/// <summary>Every menu record by name, for the Studio's listings and the validator.</summary>
	public static IEnumerable<string> MenuNames => Set.Menus.Keys;

	/// <summary>Every modal row as it runs, by key — the editor's and the Studio's listing.</summary>
	public static IEnumerable<KeyValuePair<string, Engine_UiModalTemplate>> ModalRows => Set.Modals.Rows;

	/// <summary>
	/// The answer for a box, or null when it is not owned. A screen-specific row wins over the
	/// any-screen one, so id 0 can be per-screen while every other id states itself once.
	/// </summary>
	public static Engine_UiModalTemplate? Modal(uint modalId, uint screenId, bool dismissal) =>
		Set.Modals.Find(modalId, screenId, dismissal);

	/// <summary>
	/// The mod that stated a menu row (<c>menu:&lt;menu&gt;/&lt;row&gt;</c>, a patch), else the mod that
	/// stated the whole menu (<c>menu:&lt;menu&gt;</c>); empty for a shipped row or the player's own.
	/// </summary>
	public static string MenuOwner(string menu, string row)
	{
		Dictionary<string, string> owners = Set.Owners;
		if (row.Length != 0 && owners.TryGetValue($"menu:{menu}/{row}", out string? rowOwner))
		{
			return rowOwner;
		}

		return owners.TryGetValue("menu:" + menu, out string? owner) ? owner : string.Empty;
	}

	/// <summary>The mod that stated a screen record whole, or empty for the game's or the player's.</summary>
	public static string ScreenOwner(uint screenId) =>
		Set.Owners.TryGetValue("screen:" + screenId.ToString(System.Globalization.CultureInfo.InvariantCulture),
			out string? owner) ? owner : string.Empty;

	/// <summary>One screen's record, or null for a screen Godot has no template for.</summary>
	public static Engine_UiScreenTemplate? Screen(uint screenId) =>
		Set.Screens.TryGetValue(screenId, out Engine_UiScreenTemplate? template) ? template : null;
	/// <summary>
	/// Whether ANY screen Godot owns is drawn from this presentation — not
	/// `ScreenFor(...) is not null`, which answers "which single screen is this" and returns null
	/// on an ambiguous file (three screens share `GiftGivingScreen.bgf`). Ambiguity is not the
	/// question here: every screen sharing a file draws the same chrome from the same pattern
	/// book, so if any owns it the guest's copy is redundant.
	/// </summary>
	public static bool AnyScreenOwns(string presentation)
	{
		foreach (Engine_UiScreenTemplate template in Set.Screens.Values)
		{
			if (template.ChromeUnits.Count != 0 &&
				string.Equals(template.Presentation, presentation,
					System.StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// The screen drawn from a given `.bgf`; null when no template names it, and null when
	/// several GUEST screens do — an ambiguous file cannot say which screen is up.
	///
	/// A HOST-ONLY record never makes a guest file ambiguous. This is asked of a LIVE
	/// presentation — "the guest has this document up, whose screen is it" — and a host-only
	/// surface is one Godot stands for itself out of the file-only pattern book; it is never
	/// what the guest put in the registry. The achievements screen borrows `logbookscreen`, and
	/// without this rule adding it would have made the logbook itself answer "ambiguous" and go
	/// bookless on any frame the guest did not state its id.
	///
	/// So: a single guest-range record wins outright; a host-only one answers only when no guest
	/// screen names the file at all, which is how the front end's three (1010-1012) resolve.
	/// </summary>
	public static Engine_UiScreenTemplate? ScreenFor(string presentation)
	{
		Engine_UiScreenTemplate? guest = null;
		Engine_UiScreenTemplate? owned = null;
		bool guestAmbiguous = false;
		bool ownedAmbiguous = false;

		foreach (Engine_UiScreenTemplate template in Set.Screens.Values)
		{
			if (!string.Equals(template.Presentation, presentation,
					System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (template.ScreenId >= Engine_UiBookSet.HostOnlyScreenId)
			{
				ownedAmbiguous |= owned is not null;
				owned = template;
			}
			else
			{
				guestAmbiguous |= guest is not null;
				guest = template;
			}
		}

		if (guest is not null)
		{
			return guestAmbiguous ? null : guest;
		}

		return ownedAmbiguous ? null : owned;
	}

	/// <summary>
	/// One row a menu template drops, by BOTH the identities a live list can carry.
	///
	/// <see cref="Name"/> is the guest's spawn identity and is what the STATED build has. The
	/// position solve usually has no name at all — the logbook joined 0 of 5 rows against the
	/// spawn capture in a real run, leaving every row called `Row{n}` — and there the only
	/// identity is the DISPLAYED TEXT. <see cref="Tag"/> is the Babel tag the population table
	/// gives the row, which resolves to exactly that text in whatever language is installed, so
	/// matching on it is a join rather than a guess at an English string.
	/// </summary>
	public readonly record struct DroppedRow(string Name, string Tag);

	/// <summary>A named menu template, or null.</summary>
	public static Engine_UiMenuTemplate? Menu(string name) =>
		Set.Menus.TryGetValue(name, out Engine_UiMenuTemplate? menu) ? menu : null;

	/// <summary>
	/// Row names a menu template says the guest may spawn but we do not show — see
	/// <see cref="Engine_UiMenuEntry.Drop"/>. Empty for a template that drops nothing, and empty
	/// for a name no template carries.
	///
	/// Keyed by the MENU's name, which for a guest list is its `MenuPopulationScript`: every
	/// population table is registered as a template under that name, so any list the guest builds
	/// is addressable here whether or not Godot authored it.
	/// </summary>
	public static IReadOnlyList<DroppedRow> DroppedRows(string menuName)
	{
		Engine_UiTemplateSet set = Set;
		if (_dropped!.TryGetValue(menuName, out List<DroppedRow>? cached))
		{
			return cached;
		}

		var rows = new List<DroppedRow>();
		if (set.Menus.TryGetValue(menuName, out Engine_UiMenuTemplate? menu))
		{
			Collect(menu.Entries);
		}

		_dropped[menuName] = rows;
		return rows;

		void Collect(List<Engine_UiMenuEntry> entries)
		{
			foreach (Engine_UiMenuEntry entry in entries)
			{
				if (entry.Drop && (entry.Name.Length != 0 || entry.Tag.Length != 0))
				{
					rows.Add(new DroppedRow(entry.Name, entry.Tag));
				}

				Collect(entry.Children);
			}
		}
	}

	/// <summary>Memoised <see cref="DroppedRows"/>, cleared whenever the templates reload.</summary>
	private static Dictionary<string, List<DroppedRow>>? _dropped;

	private static void EnsureLoaded()
	{
		if (_set is not null)
		{
			return;
		}

		var set = new Engine_UiTemplateSet
		{
			Warn = warning => GD.PushWarning("[ui-templates] " + warning),
			Log = line => GD.Print("[ui-templates] " + line),
		};

		// THE GAME IS THE FIRST FILE. The guest's own population tables, then the game's records
		// (Engine_UiGameTemplates) through the same reader every mod's file takes, then the mods in
		// load order, then the player's own file.
		RegisterPopulationTables(set.Menus);
		set.ApplyText(Engine_UiGameTemplates.Text, owner: string.Empty, "the game's templates");
		int modded = 0;
		foreach ((string mod, string text) in _composing ?? Refii.Mods.ModEntries.ReadEnabledText(ModTemplatesPath))
		{
			modded += set.ApplyText(text, mod, $"{mod}:{ModTemplatesPath}");
		}

		int overridden = _composing is null ? ApplyOverrides(set, OverridePath) : 0;
		_dropped = new Dictionary<string, List<DroppedRow>>(System.StringComparer.Ordinal);
		_set = set;
		if (_composing is null)
		{
			SaveSnapshot(SnapshotPath);
		}

		GD.Print($"[ui-templates] {set.Screens.Count} screen(s), {set.Menus.Count} menu(s)" +
			(modded == 0 ? string.Empty : $"; {modded} piece(s) from mods") +
			(overridden == 0 ? string.Empty : $"; {overridden} record(s) overridden"));
	}

	/// <summary>
	/// The mods' files a <see cref="Compose"/> stands in for the enabled ones; null for the
	/// registry as it runs.
	/// </summary>
	private static IReadOnlyList<(string Mod, string Text)>? _composing;

	/// <summary>
	/// The registry as it composes with <paramref name="modFiles"/> (each mod's
	/// <see cref="ModTemplatesPath"/> text, LOWEST priority first) in place of the enabled mods'
	/// files, and without the player's file: the Mod Studio's view of a mod's templates as it is
	/// being edited, enabled or not. The records come back in the snapshot's shape (menus,
	/// screens, modals) with the conflicts that composition recorded, through the same
	/// <see cref="Engine_UiTemplateSet.ApplyText"/> every run composes with. No snapshot is
	/// written, and the running registry is rebuilt from the enabled mods on its next use.
	/// </summary>
	public static (System.Text.Json.Nodes.JsonObject Records, IReadOnlyList<ModConflict> Conflicts) Compose(
		IReadOnlyList<(string Mod, string Text)> modFiles)
	{
		_composing = modFiles;
		try
		{
			_set = null;
			_dropped = null;
			Engine_UiTemplateSet set = Set;
			return (set.Snapshot(), new List<ModConflict>(set.Conflicts));
		}
		finally
		{
			_composing = null;
			_set = null;
			_dropped = null;
		}
	}

	/// <summary>
	/// A menu record in the file shape (<c>{ highlight, title, source, rowCommand, entries }</c>) as
	/// the reader takes it, for an editor's preview of a record it has not written yet.
	/// </summary>
	public static Engine_UiMenuTemplate? ParseMenuRecord(string name, System.Text.Json.Nodes.JsonObject record) =>
		Engine_UiTemplateJson.ParseMenu(name, record);

	/// <summary>
	/// Drops every record and builds them again — the game's, mods, then the player's own file.
	/// For the editor, after a mod's templates change.
	/// </summary>
	public static void Reload()
	{
		_set = null;
		_dropped = null;
		EnsureLoaded();
	}

	/// <summary>
	/// The game's records as the reader composes them from <see cref="Engine_UiGameTemplates"/> —
	/// the population tables, then the game's file — with no mod and no player file. Pure: what a
	/// test holds against <see cref="AuthoredDefaults"/>.
	/// </summary>
	public static Engine_UiTemplateSet ComposeGame()
	{
		var set = new Engine_UiTemplateSet();
		RegisterPopulationTables(set.Menus);
		set.ApplyText(Engine_UiGameTemplates.Text, owner: string.Empty, "the game's templates");
		return set;
	}

	/// <summary>
	/// The game's records as AUTHORED here in C#, beside the evidence each was decoded from: the
	/// population tables, <see cref="BuildDefaults"/> and the wide layouts. Never what runs — the
	/// game runs <see cref="Engine_UiGameTemplates.Text"/> through the reader like a mod — but the
	/// form that file is generated from (<see cref="GameTemplatesText"/>), and a test holds the two
	/// identical record for record.
	/// </summary>
	public static Engine_UiTemplateSet AuthoredDefaults()
	{
		var set = new Engine_UiTemplateSet();
		RegisterPopulationTables(set.Menus);
		BuildDefaults(set.Menus, set.Screens);
		StateWideLayouts(set.Screens);
		return set;
	}

	/// <summary>
	/// The game's templates file as generated from <see cref="AuthoredDefaults"/>: every menu the
	/// authored records add or change over the population tables, whole, and every screen, each
	/// field only where it is not the reader's default. <see cref="Engine_UiGameTemplates.Text"/>
	/// is this text.
	/// </summary>
	public static string GameTemplatesText()
	{
		Engine_UiTemplateSet authored = AuthoredDefaults();
		var tables = new Dictionary<string, Engine_UiMenuTemplate>();
		RegisterPopulationTables(tables);

		var menus = new System.Text.Json.Nodes.JsonObject();
		foreach ((string name, Engine_UiMenuTemplate menu) in authored.Menus)
		{
			System.Text.Json.Nodes.JsonObject record = Engine_UiTemplateJson.MenuJson(menu);
			if (tables.TryGetValue(name, out Engine_UiMenuTemplate? table) &&
				Engine_UiTemplateJson.Write(Engine_UiTemplateJson.MenuJson(table)) == Engine_UiTemplateJson.Write(record))
			{
				continue;
			}

			menus[name] = record;
		}

		var screens = new System.Text.Json.Nodes.JsonObject();
		foreach ((uint id, Engine_UiScreenTemplate screen) in authored.Screens)
		{
			screens[id.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
				Engine_UiTemplateJson.ScreenJson(screen, full: false);
		}

		return Engine_UiTemplateJson.Write(new System.Text.Json.Nodes.JsonObject
		{
			["menus"] = menus,
			["screens"] = screens,
		});
	}

	// -----------------------------------------------------------------------------------
	// The built-in records — the decoded contracts as data.
	// -----------------------------------------------------------------------------------

	/// <summary>
	/// Registers every table translated from `expandablemenupopulationscript.lua` as a menu
	/// record, keyed by the `MenuPopulationScript` name the `.bgf` states. These are the EDITABLE
	/// form of those lists — snapshotted and overridable — never a second source of content: the
	/// rows a screen shows at runtime are the guest's, through the button pool.
	///
	/// A hand-written record with the same name wins — `BuildDefaults` runs this first and then
	/// states its own, so the pause and front-end roots keep the forms decoded from the binary.
	/// </summary>
	private static void RegisterPopulationTables(Dictionary<string, Engine_UiMenuTemplate> menus)
	{
		foreach (Engine_UiMenuPopulation.Table table in Engine_UiMenuPopulation.All)
		{
			var menu = new Engine_UiMenuTemplate { Name = table.Script };
			var byName = new Dictionary<string, Engine_UiMenuEntry>(System.StringComparer.Ordinal);

			foreach (Engine_UiMenuPopulation.Entry entry in table.Entries)
			{
				var record = new Engine_UiMenuEntry
				{
					Name = entry.Name,
					Tag = entry.TextTag,
					Icon = entry.IconTexture,
					EventType = (int)entry.EventType,
				};
				byName[entry.Name] = record;

				if (entry.Parent.Length != 0 &&
					byName.TryGetValue(entry.Parent, out Engine_UiMenuEntry? parent))
				{
					parent.Children.Add(record);
				}
				else
				{
					menu.Entries.Add(record);
				}
			}

			if (table.HighlightIndex >= 1 && table.HighlightIndex <= menu.Entries.Count)
			{
				menu.Highlight = menu.Entries[table.HighlightIndex - 1].Name;
			}

			// EVERY MENU DECLARES WHERE ITS ROWS COME FROM. A table with no authored entries is
			// one the guest populates at runtime — the item, clothing, quest and region lists —
			// and its rows are the guest's DATA, not its layout. Naming `capture:Root` here says
			// that in the template rather than in a branch in the list builder: the assembly is
			// the same for every menu and only the SOURCE differs, so a screen that breaks
			// breaks the way all of them would.
			//
			// `capture` is the spawn capture, which is where those rows live today. Pointing a
			// menu at a real data-area provider later is an edit to this one field.
			if (menu.Entries.Count == 0)
			{
				menu.Source = "capture:" + Engine_UiExpandableMenu.RootName;
			}

			// A CATEGORY WITH NO AUTHORED CHILDREN TAKES THEM FROM THE CAPTURE. The population
			// script declares the category and the guest fills it at runtime — the logbook's
			// `Expressions` and `CompletedQuests`, the equipment screen's slots — so an entry
			// that is a submenu and authors nothing under itself is not an empty menu, it is one
			// whose children are data.
			//
			// It has to say so, because `UpdateAllGhosting` auto-ghosts a submenu with zero
			// children (`AutomaticGhostingOnSubmenus`) and a ghosted row refuses its own press.
			// Without this the logbook built three categories that were greyed out and could not
			// be opened at all.
			foreach (Engine_UiMenuEntry entry in menu.Entries)
			{
				SourceChildlessSubmenus(entry);
			}

			menus[table.Script] = menu;
		}
	}

	/// <summary>`sub_829A4680` and its three siblings — all four abilities rows share it.</summary>
	private const string AbilityHeaderIcon =
		@"Art\GUI\WheelIcons\Abilities\icon_strength.tga";

	/// <summary>One abilities category — its children read from the GDB and the hero's levels.</summary>
	private static Engine_UiMenuEntry AbilityCategory(string name, string tag) =>
		new()
		{
			Name = name,
			Tag = tag,
			Icon = AbilityHeaderIcon,
			EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
			Source = Engine_UiAbilitySource.SourceName + ":" + name,
		};

	/// <summary>
	/// The gift screen's gold row event — `sub_82430468` spawns it with 7 in both the packed
	/// Float1/Float2 slot and the event type, and `sub_826C45C8` branches on the type alone.
	/// </summary>
	private const int GiftGoldEventType = 7;

	/// <summary>
	/// One shop group row — `sub_82994218` states this icon for all six
	/// (`dword_8349D040`). Children come from the capture; see the call site.
	/// </summary>
	private static Engine_UiMenuEntry ShopCategory(string name, string tag) => new()
	{
		Name = name,
		Tag = tag,
		Icon = @"Art\GUI\WheelIcons\icon_items.tga",
		EventType = (int)Engine_UiMenuConstants.EventType.Submenu,

		// The capture's rows with the transaction stamped on — see Engine_UiMenuSources.ShopRows.
		// Without a command a shop row falls back to an identity push, and the shop's buy and
		// sell are reached through the screen's own dispatcher, which takes a ticket rather than
		// anything an identity names.
		Source = Engine_UiMenuSources.ShopSource + ":" + name,
	};

	/// <summary>One weapons-screen category row, its half of the category from the data areas.</summary>
	private static Engine_UiMenuEntry WeaponCategory(string name, string tag, string half) => new()
	{
		Name = name,
		Tag = tag,

		// `dword_8349D930` again — the shared category icon.
		Icon = @"Art\GUI\WheelIcons\icon_world_map.tga",
		EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
		Source = Engine_UiInventorySource.WeaponsSource + ":" + half,
	};

	/// <summary>One world-map category row — a submenu, its children from the named source.</summary>
	private static Engine_UiMenuEntry WorldMapCategory(string name, string tag, string source) =>
		new()
		{
			Name = name,
			Tag = tag,

			// `dword_8349D354` — every world-map child row states this icon.
			Icon = @"Art\GUI\WheelIcons\icon_logbook.tga",
			EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
			Source = source,
		};

	/// <summary>The entry, with its caption's `" (N)"` suppressed — see `Engine_UiMenuEntry.Counted`.</summary>
	private static Engine_UiMenuEntry Uncounted(Engine_UiMenuEntry entry)
	{
		entry.Counted = false;
		return entry;
	}

	/// <summary>One item-screen category row: a submenu whose children are the data areas.</summary>
	/// <summary>
	/// The category rows' shared tag prefix. All eighteen resolve in the shipped `book.babel`,
	/// checked before use — and note the display text is NOT the internal name: `..._TOOLS` is
	/// 'Miscellaneous', `..._BOOKS` is 'Books &amp; Documents', `..._SHIRTS` is 'Upper Body'.
	/// </summary>
	private const string CategoryTag = "GUI_SCREEN_INVENTORY_CATEGORIES_";

	private static Engine_UiMenuEntry ItemCategory(string name, string tag, params int[] categories) =>
		ItemCategory(name, tag, string.Empty, categories);

	/// <summary>
	/// One category row whose children are the data areas, and what activating one of them DOES.
	/// The operation rides on the source because it needs each row's own record hash.
	/// </summary>
	private static Engine_UiMenuEntry ItemCategory(
		string name, string tag, string operation, params int[] categories) =>
		new()
		{
			Name = name,
			Tag = tag,

			// `dword_8349D930` — every one of the nine states the same icon.
			Icon = @"Art\GUI\WheelIcons\icon_world_map.tga",
			EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
			Source = Engine_UiInventorySource.SourceName + ":" +
				(operation.Length == 0 ? string.Empty : operation + ":") +
				string.Join(",", categories),
		};

	/// <summary>
	/// Points a childless submenu entry at the spawn capture for its children, recursively. See
	/// the call site for why an authored category must declare this.
	/// </summary>
	private static void SourceChildlessSubmenus(Engine_UiMenuEntry entry)
	{
		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			SourceChildlessSubmenus(child);
		}

		if (entry.Children.Count == 0 && entry.Source.Length == 0 && entry.Name.Length != 0 &&
			entry.EventType == (int)Engine_UiMenuConstants.EventType.Submenu)
		{
			entry.Source = "capture:" + entry.Name;
		}
	}

	private static void BuildDefaults(
		Dictionary<string, Engine_UiMenuTemplate> menus, Dictionary<uint, Engine_UiScreenTemplate> screens)
	{
		// THE LOGBOOK DOES NOT OFFER `Achievements`. The guest spawns the row — it is in its own
		// population table — and its action is `XamShowAchievementsUI` (`sub_826D9620` case 150),
		// the console's guide blade, which on this platform opens nothing at all. Every press of
		// it was a press that did nothing.
		//
		// The achievements are their own screen now, on their own pause tab; see
		// `Engine_UiHostScreen`. This is the statement that the dead row goes, and it is a
		// TEMPLATE RECORD rather than a special case in the list builder, so any other row on any
		// other guest menu can be dropped the same way — and put back, from
		// `user://ui/templates.json`, without a rebuild.
		// THE DYE LIST'S CHILDREN ARE THE HERO'S CLOTHES. The population table authors the seven
		// category rows; `sub_829B2168` fills them from the inventory (categories 17..24, worn or
		// not) and adds `Dye Hero Hair` with the hair, beard and moustache that are on.
		if (menus.TryGetValue(Engine_UiDyeFlow.MenuScript, out Engine_UiMenuTemplate? dyes))
		{
			foreach (Engine_UiMenuEntry entry in dyes.Entries)
			{
				int category = entry.Name switch
				{
					"Hats" => Engine_UiInventorySource.Category.Hats,
					"Coats" => Engine_UiInventorySource.Category.Coats,
					"Shirts" => Engine_UiInventorySource.Category.Shirts,
					"Gloves" => Engine_UiInventorySource.Category.Gloves,
					"Pants" => Engine_UiInventorySource.Category.Pants,
					"Shoes" => Engine_UiInventorySource.Category.Shoes,
					"Accessories" => Engine_UiInventorySource.Category.Accessories,
					_ => 0,
				};

				if (category != 0)
				{
					// Masks and the dog's collar share the row, as on the clothing screen.
					entry.Source = $"{Engine_UiInventorySource.SourceName}:dye:{category}" +
						(category == Engine_UiInventorySource.Category.Accessories
							? $",{Engine_UiInventorySource.Category.Dog}"
							: string.Empty);
				}
			}

			dyes.Entries.Add(new Engine_UiMenuEntry
			{
				Name = "Dye Hero Hair",
				Tag = CategoryTag + "DYE_HERO_HAIR",
				Icon = @"Art\GUI\WheelIcons\icon_world_map.tga",
				EventType = (int)Engine_UiMenuConstants.EventType.Submenu,
				Source = $"{Engine_UiInventorySource.SourceName}:dye:" +
					$"{Engine_UiInventorySource.Category.Hair}," +
					$"{Engine_UiInventorySource.Category.Beard}," +
					$"{Engine_UiInventorySource.Category.Moustache}",
			});
		}

		if (menus.TryGetValue("OptionsMenu", out Engine_UiMenuTemplate? options) &&
			options.Entries.FindIndex(entry => entry.Name == "Video") is >= 0 and var videoIndex)
		{
			Engine_UiMenuEntry gamma = options.Entries[videoIndex];
			gamma.Tag = "GUI_SCREEN_OPTIONS_GAMMA";
			options.Entries[videoIndex] = new Engine_UiMenuEntry
			{
				Name = "VideoSettings",
				Tag = "GUI_SCREEN_OPTIONS_VIDEO",
				Icon = gamma.Icon,
				HostOnly = true,
				Counted = false,
				Children =
				{
					gamma,
					new Engine_UiMenuEntry
					{
						Name = "DynamicSkyAndWeather",
						Tag = "Dynamic Sky and Weather",
						Icon = gamma.Icon,
						HostOnly = true,
						Counted = false,
						Command = Engine_UiWeatherOptions.OpenCommand,
					},
				},
			};

			// MODS: a fold with one row per enabled mod that ships settings, each opening that
			// mod's host options page (Engine_UiHostOptionsPage) on the same page furniture the
			// sky's switches use. Sourced, so the fold follows the load order and an empty one
			// leaves the list; host-only, because the guest has no screen behind any of it.
			options.Entries.Insert(videoIndex + 1, new Engine_UiMenuEntry
			{
				Name = "ModSettings",
				Tag = "Mods",
				Icon = gamma.Icon,
				HostOnly = true,
				Counted = false,
				Source = Refii.Mods.ModSettingsUi.SourceName,
			});
		}

		if (options is not null)
		{
			options.Entries.Insert(1, new Engine_UiMenuEntry
			{
				Name = "CharacterSettings", Tag = "Character",
				Icon = @"Art\GUI\WheelIcons\Abilities\icon_logbook.tga",
				HostOnly = true, Counted = false,
				Children =
				{
					new Engine_UiMenuEntry
					{
						Name = "CharacterMorphs", Tag = "Morphs",
						Icon = @"Art\GUI\WheelIcons\Abilities\icon_logbook.tga",
						HostOnly = true, Counted = false,
						Command = Engine_UiCharacterOptions.OpenCommand,
					},
				},
			});
		}

		// CONTROLS: the game's own Controls page folded with the game's rumble switch
		// (Engine_UiVibrationOptions — the guest's byte, which shipped with no page behind it)
		// and one row per enabled mod that declares rebindable input actions (input.json), each
		// opening that mod's Controls page (ModInputUi) on the host page furniture. The guest's
		// row keeps its name and event, so the fold's first child is the page it always was.
		if (options is not null &&
			options.Entries.FindIndex(entry => entry.Name == "Controls") is >= 0 and var controlsIndex)
		{
			Engine_UiMenuEntry controls = options.Entries[controlsIndex];
			options.Entries[controlsIndex] = new Engine_UiMenuEntry
			{
				Name = "ControlsSettings",
				Tag = controls.Tag,
				Icon = controls.Icon,
				HostOnly = true,
				Counted = false,
				Children =
				{
					controls,
					new Engine_UiMenuEntry
					{
						Name = Engine_UiVibrationOptions.PageName,
						Tag = Engine_UiVibrationOptions.PageTitle,
						Icon = controls.Icon,
						HostOnly = true,
						Counted = false,
						Command = Engine_UiVibrationOptions.OpenCommand,
					},
					new Engine_UiMenuEntry
					{
						Name = "Quickslots", Tag = "Quickslots", Icon = controls.Icon, HostOnly = true, Counted = false, When = Engine_UiRowConditions.InGame + "," + Engine_UiRowConditions.Hotkeys,
						Children =
						{
							new Engine_UiMenuEntry { Name = "QuickslotsBase", Tag = "D-pad", HostOnly = true, Counted = false, Command = Engine_UiQuickSlotOptions.Prefix + "0" },
							new Engine_UiMenuEntry { Name = "QuickslotsLT", Tag = "LT + D-pad", HostOnly = true, Counted = false, Command = Engine_UiQuickSlotOptions.Prefix + "1" },
							new Engine_UiMenuEntry { Name = "QuickslotsRT", Tag = "RT + D-pad", HostOnly = true, Counted = false, Command = Engine_UiQuickSlotOptions.Prefix + "2" },
						},
					},
				},
				Source = Refii.Mods.ModInputUi.SourceName,
			};
		}

		if (menus.TryGetValue("LogbookMenu", out Engine_UiMenuTemplate? logbook))
		{
			foreach (Engine_UiMenuEntry entry in logbook.Entries)
			{
				if (string.Equals(entry.Name, Engine_UiAchievementPage.FolderRowName,
						System.StringComparison.Ordinal))
				{
					entry.Drop = true;
				}
			}
		}

		// THE PAUSE ROOT — ONE RECORD FOR BOTH PLAYERS. The hero's rows are `sub_8298DF98`'s spawn
		// list verbatim: names are the identities its dispatch recognises, the Saving children
		// dispatch by EVENT PARAM ({type 2, param}), the tabs by the tab lever's ids, and
		// SetInitiallySelectedItem(2) is the highlight. Player two's rows are ours (below) and
		// stand under the row condition `player-two` (Engine_UiRowConditions), which
		// `Engine_PauseMenu.BuildHero` states; a row with no condition is on both lists, so a mod
		// patches this one record and reaches both players.
		const string hero = "!" + Engine_UiRowConditions.PlayerTwo;
		const string playerTwo = Engine_UiRowConditions.PlayerTwo;
		menus["pause-root"] = new Engine_UiMenuTemplate
		{
			Name = "pause-root",
			Highlight = "Map",
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Saving", Tag = "GUI_SCREEN_PAUSE_SAVING", When = hero,

					// AUTHORED, NOT SOURCED. `sub_8298DF98` spawns three of these under
					// "Saving" once (0x8298E0F8/E138/E178, EventType 2, String1 = the localised
					// label); sourcing them as well seats the same rows twice, and only the
					// authored ones carry the `PauseSaving:N` commands.
					//
					// FIVE ROWS. The guest's "quit" means quit to its TITLE; the console owned
					// everything past that, so there is no shipped row that leaves the app. The
					// two desktop rows are HOST-ONLY and post the SAME guest event as their
					// title counterparts, so the guest's confirmation box and its save
					// sequencing run either way — see Engine_PauseMenu.QuitCommandPrefix.
					//
					// The four quit labels are LITERALS: the text bank has tags for "Save and
					// Quit" and "Quit and Don't Save" but none naming a destination.
					// GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE keeps its tag, that row is unchanged.
					Children =
					{
						new Engine_UiMenuEntry
						{
							Name = "SaveAndContinue",
							Tag = "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}0",
						},
						new Engine_UiMenuEntry
						{
							Name = "SaveAndQuit",
							Tag = "Save and Quit to Title",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}1",
						},
						new Engine_UiMenuEntry
						{
							Name = "SaveAndQuitToDesktop", HostOnly = true,
							Tag = "Save and Quit to Desktop",
							Command = $"{Engine_PauseMenu.QuitCommandPrefix}1",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitDontSave",
							Tag = "Quit to Title and Don't Save",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}2",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitToDesktopDontSave", HostOnly = true,
							Tag = "Quit to Desktop and Don't Save",
							Command = $"{Engine_PauseMenu.QuitCommandPrefix}2",
						},
					},
				},

				// THE TABS, A VARIANT PER PLAYER. Player two is a hero too — the hero's book, Fable
				// 3's own menus per player (user, 2026-10-19) — but their tabs are HOST-ONLY (see
				// `HostTab`): the guest's capture on that machine is the henchman list, and the
				// stated-label pass would hand these rows its captions by ordinal.
				Tab("Map", "GUI_SCREEN_PAUSE_WORLD_MAP", Engine_PauseMenu.TabMap, hero),
				HostTab("Map", "GUI_SCREEN_PAUSE_WORLD_MAP", Engine_PauseMenu.TabMap, playerTwo),
				Tab("Abilities", "GUI_SCREEN_PAUSE_ABILITIES", Engine_PauseMenu.TabAbilities, hero),
				HostTab("Abilities", "GUI_SCREEN_PAUSE_ABILITIES", Engine_PauseMenu.TabAbilities, playerTwo),
				Tab("Items", "GUI_SCREEN_PAUSE_ITEMS", Engine_PauseMenu.TabItems, hero),
				HostTab("Items", "GUI_SCREEN_PAUSE_ITEMS", Engine_PauseMenu.TabItems, playerTwo),
				Tab("Weapons", "GUI_SCREEN_PAUSE_WEAPONS", Engine_PauseMenu.TabWeapons, hero),
				HostTab("Weapons", "GUI_SCREEN_PAUSE_WEAPONS", Engine_PauseMenu.TabWeapons, playerTwo),
				Tab("Clothing", "GUI_SCREEN_PAUSE_CLOTHING", Engine_PauseMenu.TabClothing, hero),
				HostTab("Clothing", "GUI_SCREEN_PAUSE_CLOTHING", Engine_PauseMenu.TabClothing, playerTwo),
				Tab("Logbook", "GUI_SCREEN_PAUSE_LOGBOOK", Engine_PauseMenu.TabLogbook, hero),
				HostTab("Logbook", "GUI_SCREEN_PAUSE_LOGBOOK", Engine_PauseMenu.TabLogbook, playerTwo),

				// ACHIEVEMENTS — a host tab, sitting between Logbook and Options because THIS
				// LIST IS THE ORDER. `HostOnly` keeps the entry out of the label join against the
				// guest's button pool and out of the availability mask, neither of which has a row
				// for it, so the guest's own rows still join by NAME wherever they end up.
				//
				// Its command OPENS A SCREEN (Engine_UiHostScreens) — `sub_826B89E0`'s switch has
				// no case to select — which is what lets it sit anywhere at all. The tag is a
				// LITERAL: the shipped text bank has no achievements string.
				new Engine_UiMenuEntry
				{
					Name = "Achievements", Tag = "Achievements", HostOnly = true,
					Command = Engine_UiHostScreens.OpenCommand(Engine_UiAchievementPage.ScreenId),
				},

				// The multiplayer book, on the same verb.
				new Engine_UiMenuEntry
				{
					Name = "Multiplayer", Tag = "Multiplayer", HostOnly = true,
					Command = Engine_UiHostScreens.OpenCommand(Engine_UiMultiplayerPage.ScreenId),
				},

				Tab("Options", "GUI_SCREEN_PAUSE_OPTIONS", Engine_PauseMenu.TabOptions),

				// FABLE 3'S KICK, the online host's (the guest's own root adds "Kick" only when
				// game+0x11C == 1 and the henchman exists, `sub_8298DF98`). Offered by GameUi only
				// while an online partner stands; the child is the confirmation, worded as Fable 3's
				// GUI_SCREEN_PAUSE_CONFIRM_KICK_HENCHMAN.
				new Engine_UiMenuEntry
				{
					Name = Engine_PauseMenu.KickRow, Tag = "Kick", HostOnly = true, When = hero,
					Children =
					{
						new Engine_UiMenuEntry
						{
							Name = "KickConfirm", HostOnly = true,
							Tag = "Kick the other Hero out of your game (they lose unsaved progress)",
							Command = $"{Engine_PauseMenu.CoopCommandPrefix}kick",
						},
					},
				},

				// LEAVING, as Fable 3's "Leave": player two leaves the game, the partner plays on.
				// `PauseCoop:leave` runs the guest's own drop-out answer (`sub_826A6E70`): an online
				// joiner's HeroSave is written on the way out; local player two's is written when
				// they loaded one (box 27), else their earnings go to player one (box 26). The
				// "don't save" pair below quits this machine's whole game. Saving stays the host's —
				// the world is theirs; player two's HeroSave is written by the guest on leave and
				// autosave.
				new Engine_UiMenuEntry
				{
					Name = "HenchmanQuit", Tag = "Leave or Quit", HostOnly = true, When = playerTwo,
					Children =
					{
						new Engine_UiMenuEntry
						{
							Name = "LeaveGame", HostOnly = true,
							Tag = "Leave this co-op game (your Hero is saved)",
							Command = $"{Engine_PauseMenu.CoopCommandPrefix}leave",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitDontSave",
							Tag = "Quit to Title",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}2",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitToDesktopDontSave", HostOnly = true,
							Tag = "Quit to Desktop",
							Command = $"{Engine_PauseMenu.QuitCommandPrefix}2",
						},
					},
				},
			},
		};

		// PLAYER TWO'S ROWS ARE OURS, AUTHORED, NOT THE GUEST'S. Both players' pause screens name
		// the same population script — a live run showed `[screen-menu] 'PauseMenu' is GODOT'S
		// ... (source 'capture:Root')` on the joiner, not `HenchmanPauseMenu` — so the variant
		// cannot be chosen by that name; the `player-two` fact is `HostBridge.IsCoopJoiner`'s
		// reading in GameUi. Without it the joiner got the capture's four rows AND the whole
		// single-player tab set: nine-plus rows with a duplicate `Options`, every command running
		// against a row it did not belong to, so picking `Abilities` reached `Save`.
		//
		// The guest states four rows there (`HenchmanAbilities`, `HenchmanConfig`, `Options`,
		// `HenchmanQuit`). They are not the authority on what this menu contains — Godot owns the
		// list, the order and the layout; what we take from the guest is the AVAILABILITY mask and
		// the commit entry points, exactly as the solo menu does. A pre-generated HENCHMAN's gear is
		// fixed by the co-op screen's pickboxes; `Engine_PauseMenu.ApplyTabAvailability` ghosts
		// whichever tabs the mask (`RefiiHostPauseTabsAvailable`, the guest's own `sub_826B83D0`)
		// says are unavailable. IF A RUN SHOWS the mask reporting Weapons/Clothing AVAILABLE to a
		// henchman, they need dropping outright rather than ghosting; that is the one thing here
		// not yet confirmed against a session.

		// THE ITEM SCREEN'S NINE CATEGORY ROWS, from the guest's own spawn order in
		// `sub_829B1578` (0x829B15E4 .. 0x829B1886): KeyItems, Books, Food and Drink, Potions,
		// Gifts, Trophies, Augmentations, Dyes, Furniture. Every one carries the same icon
		// (`dword_8349D930`) and event type 301, and every one is parented to `Root`.
		//
		// FIXED, NOT DERIVED FROM WHAT THE HERO CARRIES: the guest spawns all nine and GHOSTS the
		// empty ones rather than dropping them — a real run showed `Food ghosted` and
		// `Trophies ghosted` sitting between populated rows. Our own automatic submenu ghosting
		// reproduces that for free once the children come from the source below.
		//
		// The children are the DATA AREAS, not the guest's spawn stream: each row names the
		// category integers it holds and `Engine_UiInventorySource` reads the hero's inventory
		// and `globals.gdb` for them. `KeyItems` takes eight integers because the guest folds
		// 9..14 and 26 onto it — which is why hairstyles and makeup are filed there.
		// THE ABILITIES SCREEN — `sub_829A45A0`'s four root rows, which it spawns with direct
		// `sub_826CE8D0` calls rather than through `sub_826CF098`. All four state the SAME icon
		// (`Art\GUI\WheelIcons\Abilities\icon_strength.tga`) and an empty `String2`; the
		// captions arrive later through `g_Menu:RefreshButton()` in `sub_829A5398`.
		//
		// `SpellAssigning` is NOT a category — it has no children and event type 23, and
		// activating it hands the screen to the spell-assignment widget at `screen+200` in place
		// (`sub_826C2480`). Its Babel tag reads 'Spell Selector', which is the row observed live.
		menus["AbilitiesMenu"] = new Engine_UiMenuTemplate
		{
			Name = "AbilitiesMenu",
			Entries =
			{
				AbilityCategory(Engine_UiAbilitySource.Strength,
					"GUI_SCREEN_ABILITIES_LEARN_STRENGTH"),
				AbilityCategory(Engine_UiAbilitySource.Skill,
					"GUI_SCREEN_ABILITIES_LEARN_SKILL"),
				AbilityCategory(Engine_UiAbilitySource.Will,
					"GUI_SCREEN_ABILITIES_LEARN_WILL"),
				new Engine_UiMenuEntry
				{
					Name = "SpellAssigning",
					Tag = "GUI_SCREEN_ABILITIES_SPELL_ASSIGNER",
					Icon = AbilityHeaderIcon,

					// 23 — the spell-assign event, which the screen answers itself.
					EventType = (int)Engine_UiMenuConstants.EventType.AssignSpellsAbilityItem,
				},
			},
		};

		// THE WORLD MAP'S FIVE CATEGORY ROWS — `sub_829A2F40`'s spawn order, with the internal
		// names it files children under (`dword_8349D340..350`): Regions, Quests, Jobs, Sales,
		// Family. Note the key is `Family` while the tag reads 'Families'.
		//
		// All five now read the data areas — see Engine_UiQuestSource. Quests and Jobs are ONE
		// list split by the definition's GDB `Type`; Sales, Regions and Family have their own.
		menus["WorldMapMenu"] = new Engine_UiMenuTemplate
		{
			Name = "WorldMapMenu",
			Entries =
			{
				WorldMapCategory("Quests", "GUI_SCREEN_WORLD_MAP_QUESTS",
					Engine_UiQuestSource.QuestsSource),
				WorldMapCategory("Jobs", "GUI_SCREEN_WORLD_MAP_JOBS",
					Engine_UiQuestSource.JobsSource),

				// No source named: `SourceChildlessSubmenus` gives each of these
				// `capture:<name>`, which is where their rows come from today.
				WorldMapCategory("Sales", "GUI_SCREEN_WORLD_MAP_SALES",
					Engine_UiQuestSource.SalesSource),
				WorldMapCategory("Family", "GUI_SCREEN_WORLD_MAP_FAMILY",
					Engine_UiQuestSource.FamilySource),
				WorldMapCategory("Regions", "GUI_SCREEN_WORLD_MAP_REGIONS",
					Engine_UiQuestSource.RegionsSource),

				// The player's properties with Fable 3's condition and repair (no shipped tag).
				WorldMapCategory("Properties", "Properties", Engine_UiPropertySource.SourceName),
			},
		};

		// THE THREE CONDITIONAL LISTS — real estate and the two furniture screens.
		//
		// Each names no population script and each spawns rows that depend on state we do not
		// read: `sub_82998300` walks a decision tree over the property record and emits only the
		// applicable actions, and `sub_8299D5B0` branches on whether the target has component 68
		// (upgrade) or 239 (replace). Authoring their rows would show actions the game has
		// disabled, so the rows come from the capture — but the MENU is a template record now,
		// so a row can be dropped, renamed or reordered from `templates.json` exactly as on
		// every other screen, and all of them go through one assembly path.
		// REAL ESTATE AND THE TWO FURNITURE LISTS STAY UNBOUND, and that is a retraction.
		//
		// I bound all three to templates sourced `capture:Root`, and it bled: the real-estate
		// screen came up showing `New Game`, `Continue`, `Downloadable Content`, `Language` and
		// `Subtitles` above its own two rows. The spawn capture is ONE GLOBAL LIST —
		// `Engine_UiSource.GetMenuRows` returns the last population with a generation and
		// `Engine_UiModel.MenuRow` carries no presentation — so `capture:Root` hands back
		// whatever was spawned under `Root` most recently, and the front end's rows were still
		// in it.
		//
		// That is harmless for a screen whose parent names are its own (the shop's `BuyItems`,
		// the weapons screen's `Melee`) and fatal for one asking for the bare root. These three
		// ask for the root, so they keep the per-presentation model path they had — which was
		// working — until the capture is keyed by presentation.
		//
		// COST OF THE RETRACTION: the furniture press fix goes with it. Those rows carry event
		// type 0 and `sub_826B65F0` handles only 140/141, so an identity push does nothing; the
		// commit is the A press through the input handler (`sub_826B6A18` / `sub_826B7550`),
		// which is what `Engine_UiScreenActions.ScreenSelect` and the template's `RowCommand`
		// were for. Both remain, unused by these two screens, for when the capture is scoped.

		// THE SHOP'S SIX GROUPS — `sub_82994218`'s own spawn order, each a counted header
		// through `sub_826CF098` and each emitted only when its own count is non-zero (the guest
		// then greys it with `sub_826D01B8`). The internal names are the globals at
		// `0x8349D048..D05C`, and they are what the rows are filed under as `String4`.
		//
		// The rows stay on the CAPTURE, for the same reason the weapons rows do and one more: a
		// shop row's identity is a per-population ticket from `sub_82880AC8`, and the buy and
		// sell handlers are reached through the shop screen's own dispatcher rather than through
		// anything that takes a record. Authoring the six groups makes the SHAPE editable —
		// rename them, reorder them, drop the sell half — without touching the rows.
		//
		// All six tags checked against the shipped book before use.
		menus["ShopMenu"] = new Engine_UiMenuTemplate
		{
			Name = "ShopMenu",
			Entries =
			{
				ShopCategory("BuyItems", "GUI_SCREEN_SHOP_BUY_ITEMS"),
				ShopCategory("SellItems", "GUI_SCREEN_SHOP_SELL_ITEMS"),
				ShopCategory("BuyWeapons", "GUI_SCREEN_SHOP_BUY_WEAPONS"),
				ShopCategory("SellWeapons", "GUI_SCREEN_SHOP_SELL_WEAPONS"),
				ShopCategory("BuyClothing", "GUI_SCREEN_SHOP_BUY_CLOTHING"),
				ShopCategory("SellClothing", "GUI_SCREEN_SHOP_SELL_CLOTHING"),
			},
		};

		// THE WEAPONS LIST — screen 15 in mode 3, `sub_829B1890`'s two headers over
		// `sub_829B1968`'s rows.
		//
		// THE ROWS ARE OURS, AND SO IS THE EQUIP. `screen+396` turned out to be a 16-byte item
		// HANDLE, not a scalar — the screen's Update refills it every frame from
		// `g_Menu:GetHighlightedMenuItemData()`, so the guest's own answer to event 3 equips
		// whatever ITS cursor is on and an identity push is right only while the two cursors
		// agree. `sub_826C9AB8` takes that handle by address and reads nothing else, so naming
		// the record and building the handle from the hero's component 13 removes the cursor from
		// the path altogether.
		//
		// Both tags checked against the shipped book before use.
		menus["WeaponsMenu"] = new Engine_UiMenuTemplate
		{
			Name = "WeaponsMenu",
			Entries =
			{
				// `sub_829B1890` spawns both through `sub_826CF098`, so both carry a count.
				WeaponCategory("Melee", CategoryTag + "MELEE_WEAPONS", "melee"),
				WeaponCategory("Ranged", CategoryTag + "RANGED_WEAPONS", "ranged"),
			},
		};

		// THE GIFT SCREEN IS THE INVENTORY, GROUPED, FILTERED BY `CanBeGift`.
		//
		// It has no population script of its own — screen 15 is built natively by `sub_82430468`,
		// which is why this template names the rows rather than adopting a table. That builder
		// walks 27 category slots and emits a header only where the count is non-zero
		// (`sub_826CF098`), which is exactly what a null category list plus grouping does here.
		//
		// The filter is `sub_8242FB28`: `CanBeGift` off the item's own `GiftComponent`, category
		// 25 rejected outright, and a worn or wielded item rejected when the stack holds only
		// one. All three are gift rules and live behind the `gift` operation — see
		// `Engine_UiInventorySource.Listable` for what happened when they were applied globally.
		//
		// THE GOLD ROW IS THE GUEST'S OWN, and it needs nothing of ours. `sub_82430468` appends
		// it after the categories with `sub_826CE848(a1, "GUI_SCREEN_GIFT_GOLD", icon_gold_coin,
		// "Root", 7, 7, lua, 0)` — an event type of 7 and no item behind it. Pressing it lands in
		// `sub_826C45C8`, which latches the type at `guiMgr+84` and, because it is 7, opens the
		// amount picker (`sub_8236FCA8` with `min(gold, 10)`) instead of the yes/no box; the
		// answer is then spent by `sub_826C5258` -> `sub_8242E578`. All of that is the guest's,
		// so the row is authored here and committed by its event type alone. The id it carries
		// is never read on this path — only `*(msg + 4)`, the type, decides.
		menus["GiftGivingMenu"] = new Engine_UiMenuTemplate
		{
			Name = "GiftGivingMenu",

			// No categories named: every one, and the headers follow the data.
			Source = Engine_UiInventorySource.SourceName + ":gift:",
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Gold",
					Tag = "GUI_SCREEN_GIFT_GOLD",
					Icon = @"Art\GUI\WheelIcons\icon_gold_coin.tga",
					EventType = GiftGoldEventType,
				},
			},
		};

		foreach (string itemScript in new[] { "ItemsMenu" })
		{
			menus[itemScript] = new Engine_UiMenuTemplate
			{
				Name = itemScript,
				Entries =
				{
					ItemCategory("KeyItems", CategoryTag + "TOOLS", "use",
						Engine_UiInventorySource.Category.KeyItemsFold),
					ItemCategory("Books", CategoryTag + "BOOKS", "use",
						Engine_UiInventorySource.Category.Books),
					ItemCategory("Food and Drink", CategoryTag + "FOOD", "use",
						Engine_UiInventorySource.Category.Food),
					ItemCategory("Potions", CategoryTag + "POTIONS", "use",
						Engine_UiInventorySource.Category.Potions),
					ItemCategory("Gifts", CategoryTag + "GIFTS", "use",
						Engine_UiInventorySource.Category.Gifts),
					ItemCategory("Trophies", CategoryTag + "TROPHIES", "use",
						Engine_UiInventorySource.Category.Trophies),
					ItemCategory("Augmentations", CategoryTag + "AUGMENTATIONS", "use",
						Engine_UiInventorySource.Category.Augmentations),
					ItemCategory("Dyes", CategoryTag + "DYES", "dyeselect",
						Engine_UiInventorySource.Category.Dyes),
					ItemCategory("Furniture", CategoryTag + "FURNITURE", "use",
						Engine_UiInventorySource.Category.Furniture),
				},
			};
		}

		// THE CLOTHING SCREEN'S EIGHT ROWS — `sub_829B3340`'s seven plus `Outfits` above them,
		// which is the read a real run gives: Outfits, Headgear (6), Coats (5), Upper Body (3),
		// Hands (4), Lower Body (5), Feet (6), Masks (2). The counts array is indexed the same
		// way, `16 * Category`, for 17..23.
		//
		// `Outfits` is category 25 (Suits) — the one the ITEM list excludes outright and this
		// one is entirely about, which is why the exclusion is per-request rather than global.
		// `EquipmentMenu` is the name the screen's widget actually states — confirmed in a run,
		// which reported `population script 'EquipmentMenu'` and the eight rows in exactly this
		// order. `ClothingMenu` is registered too but is not what screen 16 asks for.
		menus["EquipmentMenu"] = new Engine_UiMenuTemplate
		{
			Name = "EquipmentMenu",
			Entries =
			{
				// `sub_829B2D58` spawns this header through `sub_826CF020`, which takes no count
				// — the only one of the eight that does.
				Uncounted(ItemCategory("Suits", CategoryTag + "SUITS", "wear",
					Engine_UiInventorySource.Category.Suits)),
				ItemCategory("Hats", CategoryTag + "HATS", "wear",
					Engine_UiInventorySource.Category.Hats),
				ItemCategory("Coats", CategoryTag + "COATS", "wear",
					Engine_UiInventorySource.Category.Coats),
				ItemCategory("Shirts", CategoryTag + "SHIRTS", "wear",
					Engine_UiInventorySource.Category.Shirts),
				ItemCategory("Gloves", CategoryTag + "GLOVES", "wear",
					Engine_UiInventorySource.Category.Gloves),
				ItemCategory("Pants", CategoryTag + "PANTS", "wear",
					Engine_UiInventorySource.Category.Pants),
				ItemCategory("Shoes", CategoryTag + "SHOES", "wear",
					Engine_UiInventorySource.Category.Shoes),

				// Internal name `Accessories`, Babel tag `..._MASKS` — the two disagree and the
				// tag is the one that draws.
				ItemCategory("Accessories", CategoryTag + "MASKS", "wear",
					Engine_UiInventorySource.Category.Accessories,
					Engine_UiInventorySource.Category.Dog),
			},
		};

		// THE ACHIEVEMENTS SCREEN'S LIST — one authored summary row, then one row per
		// achievement straight from the title's own XDBF (`Engine_Achievements`).
		//
		// The source hangs on the ROOT, not on the summary row: these are headings on a list
		// screen, and nesting them under the summary would make the whole screen a fold that has
		// to be opened before anything shows.
		//
		// The summary row is authored and host-only — it carries no achievement of its own, it
		// is where the tally is read — and it starts highlighted so the screen opens on the
		// count rather than on whichever achievement happens to be first.
		// THE MULTIPLAYER BOOK'S LIST. One sourced entry at the root: the source tags every row
		// with its group and `Expanded` raises a header per group, so Friends, Friend Requests,
		// Invitations and Games are folds without four templates to keep in step.
		// THE MULTIPLAYER BOOK'S LEFT LIST. Static: three actions that act where they stand,
		// then the four subjects, each of which OPENS A RIGHT PAGE rather than folding. The rows
		// of a subject are a list with a cursor of its own — see Engine_UiMultiplayerScreen.
		var multiplayer = new Engine_UiMenuTemplate
		{
			Name = Engine_UiMultiplayerPage.MenuName,
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Hosting", Tag = "My game", HostOnly = true,
					Source = Engine_UiNetworkSource.HostingSource,
				},
				new Engine_UiMenuEntry
				{
					Name = "AddFriend", Tag = "Add a friend (paste code)", HostOnly = true,
					Command = $"{Engine_UiNetworkSource.CommandPrefix}paste",
				},
				new Engine_UiMenuEntry
				{
					Name = "MyCode", Tag = "My friend code", HostOnly = true,
					Command = $"{Engine_UiNetworkSource.CommandPrefix}code",
				},
			},
		};

		// EACH SUBJECT IS A FOLD, the same shape as `My game`: its rows are its children, so an
		// empty one still opens onto a row that says so. The right page shows whatever the
		// cursor is on — the list is the subject, the page is the detail.
		foreach ((string source, string title) in Engine_UiNetworkSource.Pages)
		{
			multiplayer.Entries.Add(new Engine_UiMenuEntry
			{
				Name = title, Tag = title, HostOnly = true, Source = source,
			});
		}

		menus[Engine_UiMultiplayerPage.MenuName] = multiplayer;

		// THE ACTIVE MODS BOOK'S LIST: the enabled mods at the root, in load order — the same
		// source the title's fold lists, so the book opens on the row that was pressed.
		menus[Engine_UiMultiplayerPage.ModsMenuName] = new Engine_UiMenuTemplate
		{
			Name = Engine_UiMultiplayerPage.ModsMenuName,
			Title = "Active Mods",
			Source = Engine_UiNetworkSource.ModsSource,
		};

		// THE HOST PAGE BOOK'S LIST: a host question's two answers as rows (Engine_UiHostBoxPage).
		menus[Engine_UiHostBoxPage.MenuName] = new Engine_UiMenuTemplate
		{
			Name = Engine_UiHostBoxPage.MenuName,
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Continue", Tag = "Continue", HostOnly = true,
					Command = Engine_UiHostBoxPage.AcceptCommand,
				},
				new Engine_UiMenuEntry
				{
					Name = "Back", Tag = "Back", HostOnly = true,
					Command = Engine_UiHostBoxPage.CancelCommand,
				},
			},
		};

		menus["achievements-root"] = new Engine_UiMenuTemplate
		{
			Name = "achievements-root",
			Highlight = Engine_UiAchievementPage.FolderRowName,
			Source = Engine_UiAchievementPage.SourceName,
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = Engine_UiAchievementPage.FolderRowName,
					Tag = "Achievements",
					EventType = Engine_UiAchievementPage.EventType,
					HostOnly = true,
				},
			},
		};

		// The front-end root — `sub_826E2220`'s spawn order. "New Game" OPENS the gender card
		// rather than dispatching (slide 1 on `choosecard`, substate 2; activating by name would
		// consume the boy/girl answer unasked). Subtitles expands in place. Availability (Load
		// Game needs a save, Language two installed) stays the guest's spawn condition.
		menus["frontend-root"] = new Engine_UiMenuTemplate
		{
			Name = "frontend-root",
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "New Game", Tag = "GUI_SCREEN_FRONT_END_NEW_GAME",
					Opens = Engine_FrontEndMenu.HeroCardScreen,
				},
				new Engine_UiMenuEntry { Name = "Load Game", Tag = "GUI_SCREEN_FRONT_END_LOAD_GAME" },
				// TWO FOLDS, not one. Mods are content; friends, invitations and games are a
				// different subject and were only ever together because there was one row to
				// hang them on. The front-end list already nests (`Subtitles` does) and a
				// sourced entry refreshes its children in place, so neither needs a screen, a
				// second cursor or an input route of its own. Every child is HostOnly — the
				// guest has no screen for any of this.
				new Engine_UiMenuEntry
				{
					Name = "Downloadable Content", Tag = "GUI_DOWNLOADABLE_CONTENT",
					HostOnly = true,
					Children =
					{
						// A ROW, NOT A FOLD: it opens the Active Mods book, the mods listed on its
						// left page and the highlighted one's description on its right.
						new Engine_UiMenuEntry
						{
							Name = "Mods", Tag = "Active Mods", HostOnly = true,
							Command = $"{Engine_UiNetworkSource.CommandPrefix}mods",
						},
					},
				},
				// ONE ROW, NOT A FOLD. It opens the multiplayer BOOK — the same screen the pause
				// menu's tab opens — because the subjects are lists with cursors of their own
				// and a fold has nowhere to put them. See Engine_UiMultiplayerScreen.
				new Engine_UiMenuEntry
				{
					Name = "Multiplayer", Tag = "Multiplayer", HostOnly = true,
					Command = $"{Engine_UiNetworkSource.CommandPrefix}book",
				},
				new Engine_UiMenuEntry { Name = "Language", Tag = "GUI_SCREEN_FRONT_END_LANGUAGE" },
				new Engine_UiMenuEntry
				{
					Name = "Subtitles", Tag = "GUI_SCREEN_OPTIONS_SUBTITLES",
					Children =
					{
						new Engine_UiMenuEntry { Name = "SubtitlesOn", Tag = "GUI_CONTROLS_ON" },
						new Engine_UiMenuEntry { Name = "SubtitlesOff", Tag = "GUI_CONTROLS_OFF" },
					},
				},

				// THE WAY OUT. A console front end had none, so there is no guest dispatch to
				// reach and no shipped tag to localise against — host-only, literal label, for
				// the same reasons as `Achievements`. Last because this list IS the order.
				new Engine_UiMenuEntry
				{
					Name = "QuitToDesktop", Tag = "Quit to Desktop", HostOnly = true,
					Command = Engine_FrontEndMenu.QuitToDesktopCommand,
				},
			},
		};

		// The item-book vocabulary shared by five sibling screen classes — the dotted paths each
		// copies into its own globals (writer `sub_829B6B50`, 0x829b6cc8..0x829b7094).
		// `ItemValueName`/`ItemValue2` are the same label under two names (items/gift vs
		// clothing); both pages are listed because the panel gates on the guest's draw bit.
		var itemFields = new List<Engine_UiScreenPanel.Field>
		{
			new("Foreground.Book.Pages.Page2.Contents.ItemName"),
			new("Foreground.Book.Pages.Page2.Contents.ItemValueName", Alternate: true),
			new("Foreground.Book.Pages.Page2.Contents.ItemValue2", Alternate: true),
			new("Foreground.Book.Pages.Page2.Contents.ItemValue"),
			new("Foreground.Book.Pages.Page2.Contents.ItemDescription", Multiline: true),
			new("Foreground.Book.Pages.Page1.Contents.ItemName", Alternate: true),
			new("Foreground.Book.Pages.Page1.Contents.Info", Alternate: true),
		};

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 0, Name = "pause", Presentation = "pausemainmenu",

			// The cover is inside the pool here — `LeftFrame` at +10, `Buttons` at 0 — so the
			// rows belong exactly where `Buttons` is and any lift clears the cover.
			ListLift = 0f,
			Menu = "pause-root",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages",

			// The minimap legend scrolls: `sub_826A19E0` registers it under `Mask` with the
			// page's bar (0x826A23BC / 0x826A23DC), when the legend is the page shown.
			ScrollContent = "Foreground.Pages.Contents.MiniMap.Legend",
			ScrollMask = "Mask",
			ScrollBar = "Foreground.Pages.Contents.Scrollbar",
			BackdropUnit = "BackgroundTint",
			// WHAT THE GUEST STILL WRITES HERE, and nothing more.
			//
			// Renown, the clock, the purse and the four orb readouts came off this list too —
			// re-meshed from whatever the mirrored tree last said, which is right only while
			// the guest keeps writing them. They are plain reads off the hero the host already
			// samples, so `Engine_UiPausePage` states them and they are gone from here.
			//
			// The quest lines and the nine legend captions stay for now: they want sources of
			// their own, and replacing a stale string with an invented one is worse.
			LiveTexts =
			{
				"Contents.CurrentQuest",
				"Legend.CurrentObjective",
				"Legend.Fixed.KeyName1", "Legend.Fixed.KeyName2", "Legend.Fixed.KeyName3",
				"Legend.Fixed.KeyName4", "Legend.Fixed.KeyName5", "Legend.Fixed.KeyName6",
				"Legend.Fixed.KeyName7", "Legend.Fixed.KeyName8", "Legend.Fixed.KeyName9",
				"Money.Back.Select",
			},
		});

		// THE ITEM-BOOK FAMILY, which had no template records at all — items, clothing, gift
		// giving, furniture upgrade and decoration. They were five bare ids in a `uint[]` inside
		// the dispatcher, so they were the one part of the screen set that no template described
		// and the editor could never see. Three `.bgf` files, one skeleton, one provider; the
		// shop (22) is the same family and is recorded with its own art above.
		// ONE REGISTRATION PER ID. There were two passes over 15/16/25/26/27 and `AddScreen`
		// overwrites, so everything the first pass set was discarded before anything read it —
		// twice over. It cost the list binding once, and it cost these five screens their PAGE
		// PROVIDER for far longer: `Provider` was set only on the pass that lost, so
		// `Engine_UiDetailPages.Owns` was false for the whole item-book family and the right
		// page was left entirely to the guest, which fills it on a highlight-change edge keyed
		// off ITS Lua cursor — a cursor that stops moving the moment Godot owns the list. That
		// is the "the page appears and then hides, and is never consistent" report: whatever the
		// guest last wrote stayed until something else overwrote it.
		//
		// The surviving pass is below and now carries the provider.

		// The ABILITIES panel. `AbilityCost` carries the parameterised
		// `GUI_SCREEN_ABLITY_EXPERIENCE_COST` — the typo is the shipped binary's, not ours.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 14, Name = "abilities", OwnsList = true,
			Provider = Engine_UiPageProviders.Abilities,
			HasSpellBlade = true,
			FlipAction = Engine_UiScreenActions.ScreenUnlearn,
			Presentation = "abilitiesscreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu` — the assignment
			// blade included: `sub_826C2480` binds `SpellAssigning` on the LIVE presentation and
			// the guest's own widget writes every slide, icon texture and adjuster position
			// there, so the book's clone follows it and the host only forwards the levers.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_red",

			// Page2's description scrolls: the Layer `Mask` is a `Scroller`, so the automatic
			// registrar (`sub_828C2F38` via `sub_829A7390`) takes it, bar at 0x829A73B4.
			ScrollContent = "Foreground.Book.Pages.Page2.Contents.Text",
			ScrollMask = "Mask",
			ScrollBar = "Foreground.Book.Pages.Page2.Contents.Scrollbar",

			// Page4, not Page3 — the abilities book skips one. Its stack parks at x = 400.
			Pages =
			{
				"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
				"Foreground.Book.Pages.Page4",
			},
			PageStackAt = Vector3.Zero,
			PanelFields =
			{
				new("Foreground.Book.Pages.Page1.Contents.AbilityName"),
				new("Foreground.Book.Pages.Page1.Contents.TotalXP"),
				new("Foreground.Book.Pages.Page2.Contents.AbilityName"),
				new("Foreground.Book.Pages.Page2.Contents.Text.Description", Multiline: true),

				// Page ONE, not Page4: in `abilitiesscreen.bgf` AbilityName (ord 49) and
				// AbilityCost (ord 50) are siblings under Page1.Contents.
				new("Foreground.Book.Pages.Page1.Contents.AbilityCost"),
			},
		});

		foreach (uint id in new uint[] { 15, 16, 25, 26, 27 })
		{
			AddScreen(screens, new Engine_UiScreenTemplate
			{
				ScreenId = id, OwnsList = true,

				// Page2's item description scrolls — automatic `Mask` registration on items,
				// clothing (`sub_829B7328`), gift (`sub_829A9E08`) and decoration
				// (`sub_8299CEA8`). Upgrade furniture (26) has no mask: the guest registers
				// nothing there.
				ScrollContent = id == 26 ? string.Empty : "Foreground.Book.Pages.Page2.Contents.ItemDescription",
				ScrollMask = id == 26 ? string.Empty : "Mask",
				ScrollBar = id == 26 ? string.Empty : "Foreground.Book.Pages.Page2.Contents.Scrollbar",

				// Two pages, authored ON the leaf (x = 0) in every book of this family —
				// `giftgivingscreen`, `equipmentscreen` and `upgradefurniture` — so no
				// PageStackAt. (`upgradefurniture` also authors `Page1_Background`, which is
				// Page1's backdrop, not a selectable page.)
				Pages =
				{
					"Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2",
				},
				Name = id switch
				{
					15 => "items", 16 => "clothing", 25 => "gift",
					26 => "upgradefurniture", _ => "decoration",
				},

				// Three `.bgf` files, one skeleton, ONE PROVIDER — the right-hand page of all
				// five, and of the shop, which records the same provider with its own art.
				Provider = Engine_UiPageProviders.ItemBook,

				// WHICH LIST EACH OF THESE IS, because not one of them names a population
				// script — `sub_82430468` (gift) and `sub_829B0A58` (inventory) build their rows
				// in native C++ and issue no `RepopulateMenuFromPopulationScript` at all, so
				// both the attribute and the captured-statement fallback come up empty and the
				// list dropped onto the raw model.
				//
				// A run settled which id is which: screen 15 came up holding exactly the nine
				// item categories — KeyItems, Books, Food, Potions (7), Gifts, Trophies,
				// Augments, Dyes, Furniture — with X and Y reading 'Use' and 'Remove'. The
				// 'Gift' title on the same presentation is the shared file's authored default,
				// not the screen's claim: 15, 25 and 27 are all `GiftGivingScreen.bgf`.
				// 26 AND 27 ARE DELIBERATELY UNBOUND — see the conditional-list note below.
				MenuScript = id switch
				{
					15 => "ItemsMenu",
					25 => "GiftGivingMenu",
					_ => string.Empty,
				},

				// SCREEN 15 HAS TWO LISTS, NOT THREE. Its mode word at `screen+372` only ever
				// holds 1 (Items) or 3 (Weapons): the pause dispatcher `sub_826B89E0` writes
				// `*(screen15 + 372) = 1` for tab 4 and `= 3` for tab 5, and for tab 6 it takes
				// a DIFFERENT screen — `*(screen16 + 220) = 4`. Clothing is screen 16.
				//
				// The `[4] = ClothingMenu` entry here could therefore never fire. (Mode 2 is
				// also real but transient: screen 16's back handler writes it when returning
				// from dye, and screen 15's activate coerces it straight back to 1.)
				MenuScriptByMode = id == 15
					? new Dictionary<int, string> { [1] = "ItemsMenu", [3] = "WeaponsMenu" }
					: new Dictionary<int, string>(),
				Presentation = id switch
				{
					16 => "equipmentscreen", 26 => "upgradefurniture", _ => "giftgivingscreen",
				},

				// 15, 25 and 27 are `GiftGivingScreen.bgf`, 16 `EquipmentScreen.bgf`, 26
				// `UpgradeFurniture.bgf` — the same skeleton, each cloned from its own file.
				ChromeUnits = { "Foreground" },

				// Godot owns the row list and its cursor; the rest of the assembly is guest art,
				// riding in on the guest's stated position for `ExpandableMenu`.
				OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
				PageRootUnit = "Pages",

				// THE PAGE STACK IS PINNED ON THE LEAF. The Book's own fold slides move `Pages`
				// (`ShowPages` x = 405, `HidePages` x = 0) on the guest's highlight edge, which
				// no longer fires — so which of the two `Pages` sat at depended on where the
				// guest's mirror froze, and at 405 every page's content lay past the right edge
				// (the potion pages drew nothing). Godot owns the page: it stays where the
				// content fits.
				PageStackAt = Vector3.Zero,

				// The backdrop is a LEAF name, never "Background" — that name has 27 bearers,
				// every earlier one a menu row's icon plate. UpgradeFurniture is grey where the
				// gift and equipment books are green, so this is per-id.
				BackdropUnit = id == 26 ? "background_grey" : "background_green",
				PanelFields = new List<Engine_UiScreenPanel.Field>(itemFields),
				// Same skeleton, different parent for the proxy: equipmentscreen.bgf parks it
				// under TypeSpecific, the gift and furniture books under ModelHider.
				PreviewAnchor = id == 16
					? "Foreground.Book.Pages.Page1.Contents.TypeSpecific.model_proxy.model"
					: "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
			});
		}

		// The logbook names its pages by SUBJECT, not `Page1`/`Page2` —
		// `Foreground.Book.Pages.<Subject>Page` — so a `PageN` path resolves to nothing here.
		// Eight pages, 280 text elements between them, hence swept rather than listed. Subject
		// order is the guest's EventType mapping: 60/64 morality, 61 expressions, 62 completed
		// quests, 65 renown, 66 attractiveness, 67 stats, 68 dog, 69 help.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 17, Name = "logbook", OwnsList = true,

			// The logbook's pages are named by SUBJECT, not numbered — its provider switches
			// them — but its stack parks at x = 400 like the abilities book's, so it is placed.
			PageStackAt = Vector3.Zero,
			Provider = Engine_UiPageProviders.Logbook,

			// THE STATS PAGE IS ONE 3300-UNIT COLUMN on a single `FullPage` slide (eight
			// categories, Combat at y 190 down to Time Played at y −3100) behind the page's
			// Layer-level `Mask` (500×440 at (208, −6)); the guest's `sub_829BAEB8` scrolls it
			// with the right stick and its `Scrollbar` is a stick-prompt row with Up/Down arrows
			// and the stick glyph — the same region contract the region page ships on screen 23.
			// The Renown page scrolls the same way (`sub_828C3250(…, "Foreground.Mask",
			// "…RenownPage")`): its last two expression rows sit below the mask's band.
			//
			// Expressions, Completed Quests and Help register too (`sub_829BCD58`,
			// `sub_829BD418`, `sub_829BDA90`); their rows author no MaskName, the guest clipping
			// them at registration — see Engine_UiOwnedBook.AssignRegionMasks.
			ScrollContent =
				"Foreground.Book.Pages.StatsPage.Contents.LogBook.Stats|" +
				"Foreground.Book.Pages.RenownPage.Contents.LogBook.Renown|" +
				"Foreground.Book.Pages.ExpressionsPage.Contents|" +
				"Foreground.Book.Pages.CompletedQuestsPage.Contents.Quests|" +
				"Foreground.Book.Pages.HelpPage.Contents.LogBook",
			ScrollMask = "Mask|Mask|Mask|Mask|Mask",
			ScrollBar =
				"Foreground.Book.Pages.StatsPage.Contents.LogBook.Scrollbar|" +
				"Foreground.Book.Pages.RenownPage.Contents.LogBook.Scrollbar|" +
				"Foreground.Book.Pages.ExpressionsPage.Scrollbar|" +
				"Foreground.Book.Pages.CompletedQuestsPage.Contents.Scrollbar|" +
				"Foreground.Book.Pages.HelpPage.Contents.Scrollbar",

			// The Expressions page hangs its effects block under the measured description
			// (`sub_829BD340`: `Effects.y = Description.y − wrapped height`). With the block as
			// its own base the reflow's delta lands it exactly there.
			FlowMeasured = "Foreground.Book.Pages.ExpressionsPage.Contents.Description",
			FlowBase = "Foreground.Book.Pages.ExpressionsPage.Contents.Effects",
			FlowMoved = { "Foreground.Book.Pages.ExpressionsPage.Contents.Effects" },

			// Lifted verbatim from `Engine_UiLogbookPage.StatRows`.
			StatFields =
			{
		// combat
		(2,"combat.DecapitationValue"),(3,"combat.GroinshotsValue"),(4,"combat.EnemiesDisarmedValue"),
		(5,"combat.ReversalsValue"),(6,"combat.DamageDealtValue"),(7,"combat.TotalManaSpentValue"),
		(8,"combat.DamageTakenValue"),(9,"combat.DeathsValue"),(11,"combat.ResurrectionPhialsValue"),
		(13,"combat.HitsTakenValue"),(14,"combat.AttacksBlockedValue"),(15,"combat.FlourishesDoneValue"),
		(16,"combat.CoopStrikesValue"),
		// kills
		(109,"kills.TotalValue"),(110,"kills.BansheesKilledValue"),(111,"kills.BalverinesKilledValue"),
		(112,"kills.BanditsKilledValue"),(113,"kills.BeetlesKilledValue"),(114,"kills.TrollsKilledValue"),
		(115,"kills.ShardsKilledValue"),(116,"kills.ShadowsKilledValue"),
		(117,"kills.HollowmenKilledValue"),(118,"kills.GuardsKilledValue"),
		(119,"kills.LucienGuardsKilledValue"),(120,"kills.LucienSoldiersKilledValue"),
		(121,"kills.HobbesKilledValue"),(122,"kills.VillagersKilledValue"),
		(123,"kills.HighwayMenKilledValue"),
		// money
		(17,"money.MoneyPropertiesOwnedValue"),(18,"money.MoneyEarnedValue"),(19,"money.MoneySpentValue"),
		(20,"money.MoneyRightNowValue"),(22,"money.MoneyMadeMageDiceValue"),
		(23,"money.MoneyMadeBartendingValue"),(24,"money.MoneyMadeWoodchoppingValue"),
		(25,"money.MoneyMadeBlacksmithingValue"),(27,"money.MoneyMadeKeystoneValue"),
		(28,"money.MoneyMadeWizardsTowerValue"),(29,"money.MoneyMadeRealEstateValue"),
		(30,"money.MoneyMadeAssasinationsValue"),(31,"money.MoneyMadeBountyHuntsValue"),
		(32,"money.MoneyMadeSlaveryValue"),
		// adventure
		(33,"adventure.TotalRenownValue"),(106,"adventure.MostTrophyRenownValue"),
		(35,"adventure.GoldDonatedToTempleValue"),(36,"adventure.PeopleSacrificedToEvilValue"),
		(37,"adventure.AssassinationsCompletedValue"),(38,"adventure.BountiesCollectedValue"),
		(39,"adventure.SlavesRescuedValue"),(40,"adventure.PeoplePutIntoSlaveryValue"),
		(41,"adventure.MostGoodPointsValue"),(42,"adventure.MostEvilPointsValue"),
		(43,"adventure.MostNobilityPointsValue"),(44,"adventure.MostCorruptionPointsValue"),
		(45,"adventure.TotalExpEarnedValue"),(46,"adventure.TotalStrengthExpEarnedValue"),
		(47,"adventure.TotalSkillExpEarnedValue"),(48,"adventure.TotalWillExpEarnedValue"),

		// `Genearal` is the SHIPPED SPELLING. The element in `logbookscreen.bgf` carries the
		// typo, so correcting it here would resolve to nothing at all.
		(49,"adventure.TotalGenearalExpEarnedValue"),

		// games — note 83 is registered LAST despite its lower id
		(50,"games.TopScoreWestcliffShootingValue"),(96,"games.BiggestWinMageDiceValue"),
		(97,"games.BiggestWinKeystoneValue"),(98,"games.BiggestWinWizardsTowerValue"),
		(83,"games.BestFullCrucibleTimeValue"),
		// relationships
		(54,"relationships.NumPeopleFallenInLoveWithYouValue"),(55,"relationships.TimesMarriedValue"),
		(56,"relationships.TimesDivorcedValue"),(57,"relationships.TimesWidowedValue"),
		(58,"relationships.NumChildrenValue"),(59,"relationships.MostLoveEarnedValue"),
		(60,"relationships.MostHumourEarnedValue"),(61,"relationships.MostFearEarnedValue"),
		(62,"relationships.MostHateEarnedValue"),(63,"relationships.TimesMaritalSexValue"),
		(64,"relationships.TimesExtraMaritalSexValue"),(65,"relationships.TimesPaidForSexValue"),
		(66,"relationships.NumNpcRewardsCollectedValue"),
		(67,"relationships.MostCoopExpressionBonusesValue"),
		// crime
		(69,"crime.NumCrimesCommitedValue"),(70,"crime.CrimeFinesLeviedValue"),
		(71,"crime.CrimeFinesPaidValue"),(72,"crime.CrimeFinesPaidCommunityServiceValue"),
		// other
		(74,"other.TotalFoodDrinkConsumedValue"),(75,"other.TotalAlcoholConsumedValue"),
		(76,"other.TotalPotionsConsumedValue"),(77,"other.AmountOfTimeDrunkValue"),
		(78,"other.TotalVomitsValue"),(79,"other.TotalDistanceTraveledValue"),
		(80,"other.NumChickensKickedValue"),(81,"other.FurthestChickenKickValue"),
		(0,"other.ExpressionsValue"),(84,"other.STDsValue"),(1,"other.TimePlayedValue"),
			},
			Presentation = "logbookscreen",

			// `Money` has two bearers here; the money bar is the second — bearer 0 is a stats
			// row, `StatsPage.Contents.LogBook.Stats.money.Money`.
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_yellow",
			PanelSubtrees =
			{
				"Foreground.Book.Pages.MoralityPage",
				"Foreground.Book.Pages.ExpressionsPage",
				"Foreground.Book.Pages.CompletedQuestsPage",
				"Foreground.Book.Pages.RenownPage",
				"Foreground.Book.Pages.AttractivenessPage",
				"Foreground.Book.Pages.StatsPage",
				"Foreground.Book.Pages.DogPage",
				"Foreground.Book.Pages.HelpPage",
			},
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 18, Name = "options", OwnsList = true,
			// Four pages; the stack is authored on the leaf already.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2", "Foreground.Book.Pages.Page3", "Foreground.Book.Pages.Page4" },
			Provider = Engine_UiPageProviders.Options,
			FadesBottomEnd = false,
			Presentation = "optionsscreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_purple",
			// The stack is authored at x 405 — off the leaf — like the logbook's.
			PageStackAt = Vector3.Zero,
			PageElement = "Foreground.ExpandableMenu",
			PageSlide = 2,
		});

		// The CRIME panel — three pages of one multi-line text field each: fine plus charge
		// sheet, charge sheet alone, community-service text.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 21, Name = "crime", OwnsList = true,
			// Three pages, one per row; the stack is authored on the leaf.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2", "Foreground.Book.Pages.Page3" },
			Provider = Engine_UiPageProviders.Crime,
			Presentation = "crimescreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_grey",

			// Each crime page scrolls its text — `sub_82991338`, `sub_82991500`, `sub_829915A0`
			// register `PageN.Contents.Text` under `Mask` after writing it.
			ScrollContent = "Foreground.Book.Pages.Page1.Contents.Text|Foreground.Book.Pages.Page2.Contents.Text|Foreground.Book.Pages.Page3.Contents.Text",
			ScrollMask = "Mask|Mask|Mask",
			ScrollBar =
				"Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar|Foreground.Book.Pages.Page3.Contents.Scrollbar",
			PanelFields =
			{
				new("Foreground.Book.Pages.Page1.Contents.Text", Multiline: true),
				new("Foreground.Book.Pages.Page2.Contents.Text", Multiline: true),
				new("Foreground.Book.Pages.Page3.Contents.Text", Multiline: true),
			},
		});

		// The SHOP's panel: `PriceValue` is the full chain of base price, shop multiplier,
		// wealth and opinion (`sub_82992F90` via `sub_828727B8`/`sub_82872910`), while
		// `ValueValue` is the unmodified `ShopItemComponent.BasePrice`.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 22, Name = "shop", OwnsList = true,
			// Three pages, and the stack parks at x = 400 — off the canvas — so it is placed.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2", "Foreground.Book.Pages.Page3" },
			PageStackAt = Vector3.Zero,

			// `ShopMenu` is not a string in the binary — `sub_82994218` builds the list natively
			// and names no population script, exactly as the gift and inventory screens do.
			MenuScript = "ShopMenu",
			Provider = Engine_UiPageProviders.ItemBook,
			Presentation = "shopscreen",

			// The shop hoists its page paper OUT of the pages: `FrontPage`, `PageTextureFar` and
			// `PageTextureNear` are siblings of `Book`, not children of a page.
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_green",

			// ONE guest region holding two rows (`sub_829933C8`, automatic): Page2's text and
			// Page3's group. Only Page2's bar is ever handed to `sub_828C33F0`.
			ScrollContent = "Foreground.Book.Pages.Page2.Contents.Text.Group|Foreground.Book.Pages.Page3.Contents.Group",
			ScrollMask = "Mask|Mask",
			ScrollBar = "Foreground.Book.Pages.Page2.Contents.Scrollbar|",

			// The front page (name, anecdote, keeper, owner, opinions, sale, discount, wealth)
			// is written natively by `sub_82991800` at the screen's show and never again —
			// see [[refii-shop-front-page]]. The host reads those writes as data.
			GuestTextUnits = { "Foreground.FrontPage" },
			PanelFields =
			{
				new("Foreground.Book.Pages.Page1.Contents.ItemName"),
				new("Foreground.Book.Pages.Page1.Contents.Text.TextElements.PriceValue"),
				new("Foreground.Book.Pages.Page1.Contents.Text.TextElements.ValueValue"),
				new("Foreground.Book.Pages.Page2.Contents.ItemName"),
				new("Foreground.Book.Pages.Page2.Contents.TextElements.PriceValue"),
				new("Foreground.Book.Pages.Page2.Contents.Text.Group.Group.ItemDescription",
					Multiline: true),
			},
			PreviewAnchor = "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
		});

		// The WORLD MAP's region info page — sub-state +88's writer `sub_829AB0E0`, mode 41.
		// StarRating is a METER and stays off this list.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 23, Name = "worldmap", OwnsList = true,
			// Two numbered pages (the named category pages are the provider's); the stack parks at 400.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2" },
			PageStackAt = Vector3.Zero,
			Provider = Engine_UiPageProviders.WorldMap,
			Presentation = "worldmapscreen",

			// NO TINT BACKDROP — the world map's ground is six map tiles, so they are chrome
			// rather than a backdrop, and there is nothing to stretch over the revealed edges.
			ChromeUnits =
			{
				"WorldMap_Spring_A", "WorldMap_Spring_B", "WorldMap_Spring_C",
				"WorldMap_Spring_D", "WorldMap_Spring_C2", "WorldMap_Spring_A2",
				"Foreground",
			},

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages",

			// The region page scrolls — `sub_829AB060` registers `Page1.Contents.Text` under
			// `MaskPage2` with the page's own scrollbar, and `sub_826C63C8` code 27 (the right
			// stick) drives it.
			// The Quests (`sub_829AF5D0`, `MaskQuests`), Family (`sub_829B03F0`, `MaskFamily`)
			// and Legend (`sub_829ABF30`, automatic, `MaskPage3`) pages scroll too. Sales
			// registers `MaskPage2` over its Description with its own bar — a property's sign
			// runs longer than the page (Engine_UiPropertySource.FillPage).
			ScrollContent =
				"Foreground.Book.Pages.Page1.Contents.Text|" +
				"Foreground.Book.Pages.QuestsPage.Contents.Description|" +
				"Foreground.Book.Pages.FamilyPage.Contents.Group|" +
				"Foreground.Book.Pages.Page2.Contents.Legend|" +
				"Foreground.Book.Pages.SalesPage.Contents.Description",
			ScrollMask = "MaskPage2|MaskQuests|MaskFamily|MaskPage3|MaskPage2",
			ScrollBar =
				"Foreground.Book.Pages.Page1.Contents.Scrollbar|" +
				"Foreground.Book.Pages.QuestsPage.Contents.Scrollbar|" +
				"Foreground.Book.Pages.FamilyPage.Contents.Scrollbar|" +
				"Foreground.Book.Pages.Page2.Contents.Scrollbar|" +
				"Foreground.Book.Pages.SalesPage.Contents.Scrollbar",

			// And it reflows — `sub_829AB8A8` measures the wrapped merchants list and moves
			// every row below it by the overflow, silver keys taking the gargoyle row's place
			// everywhere but Mystery Island. The list is the guest's own move list, verbatim.
			FlowMeasured = "Foreground.Book.Pages.Page1.Contents.Text.MerchantsList",
			FlowBase = "Foreground.Book.Pages.Page1.Contents.Text.MerchantListBase",
			FlowMoved =
			{
				"Foreground.Book.Pages.Page1.Contents.Text.OwnedAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.BuildingsOwned",
				"Foreground.Book.Pages.Page1.Contents.Text.ChestAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.ChestsOpened",
				"Foreground.Book.Pages.Page1.Contents.Text.DigspotAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.DigspotsDug",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line10",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line11",
				"Foreground.Book.Pages.Page1.Contents.Text.DivespotsSearchedAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.DivespotsSearched",
				"Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyedAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyed",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line12",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line13",
				"Foreground.Book.Pages.Page1.Contents.Text.Lines.line14",
			},
			FlowTailAnchor = "Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyedAmount",
			FlowTailMoved =
			{
				"Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFoundAmount",
				"Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFound",
			},
			PanelFields =
			{
				new("Foreground.Book.Pages.Page1.Contents.PageTitle"),
				new("Foreground.Book.Pages.Page1.Contents.Text.DistanceAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.TraveltimeAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.FineAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.MerchantsList", Multiline: true),
				new("Foreground.Book.Pages.Page1.Contents.Text.OwnedAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.ChestAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.DigspotAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.DivespotsSearchedAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.GargoylesDestroyedAmount"),
				new("Foreground.Book.Pages.Page1.Contents.Text.SilverKeysFoundAmount"),
			},
		});

		// 28 REAL ESTATE — `sub_82998300` spawns the conditional command rows (buy, sell, rent
		// out, evict, adjust rent…) and the detail text is written on activate, so the list is
		// owned like every other book's.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 28, Name = "realestate", Presentation = "realestatescreen",
			// Two pages describing the one building; the stack is authored on the leaf.
			Pages = { "Foreground.Book.Pages.Page1", "Foreground.Book.Pages.Page2" },
			Provider = Engine_UiPageProviders.RealEstate,
			OwnsList = true,

			// DELIBERATELY UNBOUND — see the conditional-list note in BuildDefaults.
			FadesTopEnd = false,
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_grey",

			// Both pages scroll under `Mask` (explicit — the mask is not a `Scroller`):
			// `sub_82998FB0` registers `Page1.Group` (not under Contents) and `sub_8299B068`
			// `Page2.Contents.Group`.
			ScrollContent = "Foreground.Book.Pages.Page1.Group|Foreground.Book.Pages.Page2.Contents.Group",
			ScrollMask = "Mask|Mask",
			ScrollBar = "Foreground.Book.Pages.Page1.Contents.Scrollbar|Foreground.Book.Pages.Page2.Contents.Scrollbar",
		});

		// 1 PHOTO MODE — one dialog box, not a book.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1, Name = "photomode", Presentation = "photomodescreen",
			ChromeUnits = { "PhotoMode" },
		});

		// 2 THE SPELL WHEEL. A ring of slots rather than a page — `MenuItemTemplate` is the slot
		// the guest clones per spell, and `CurrentItemLight` is the selection glow. No backdrop:
		// the wheel stands over the world.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 2, Name = "spellwheel", Presentation = "spellsmenu",

			// No chrome, deliberately: `SpellsMenu.bgf` is stale art. Its `Ring`, `Root`,
			// `MenuItemTemplate` and `CurrentItemLight` name no string in the binary, and
			// `clone` appears zero times across the nine live `expandablemenu` scripts — rows
			// come from a FIXED AUTHORED POOL (`MenuItemButton1..N`, matching `NUM_VISIBLE`)
			// rebound as the list scrolls.
			//
			// The real spell widget is `SpellAssigning`, in `hud.bgf` and in the pause book at
			// `Foreground.Book.Pages.Page4.Contents.Blade.SpellAssigning`: five rows at y
			// -160/-80/0/+80/+160 with a five-cell carousel, the same controller as the
			// abilities blade.
		});

		// 24 CO-OP — NOT A BOOK. A two-page spread with its own frames and backgrounds, a
		// connecting state and a separate load-game panel; its canvas is 1280x720 where the
		// in-game books are 960x720.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 24, Name = "coop", Presentation = "coopscreen",
			ChromeUnits =
			{
				"LeftPage", "RightPage", "LeftFrame2", "RightFrame2",
				"LeftBackground", "RightBackground", "Connecting", "menu", "loadgame",
			},

			// No backdrop unit: coopscreen.bgf ships none (its `BackgroundTint` is the load-game
			// dimmer). The provider anchors the frame groups to the visible edge instead.

			// WHERE THE MANNEQUIN HANGS. Player two's henchman preview is a live entity the guest
			// spawns (`sub_82438818`, stored at `screen+276`), drawn by the host at the right page's
			// authored model anchor. The left page's anchor is player one's and stays empty until
			// the host's own hero is previewed there.
			//
			// NO `Foreground.` PREFIX. This book clones `LeftPage` and `RightPage` as separate chrome
			// units, each a top-level node named for the unit (`clone.Name = unit`), so unlike the
			// item books — one `Foreground` unit — there is no `Foreground` node to walk from. The
			// authored path matched nothing for exactly that reason.
			PreviewAnchor = "RightPage.model_proxy.model",

			// THE PICKBOXES AND THE BUTTON CAPTIONS STAY THE GUEST'S. Chrome alone drew the two
			// pages with the .bgf's AUTHORED labels, which is why `Player 1`, `Cancel` and
			// `Accept` appeared while the runtime values did not.
			//
			// The SLIDERS ARE NOT IN THIS LIST ANY MORE. Taking them from the guest did not
			// hold: a run showed all six reading `tag='Text' shows='Text'`, the authored
			// placeholder, once the guest stopped writing them — the exact failure this field's
			// own contract warns about ("a field the guest stopped writing went quietly
			// stale"). `CoopProvider` hides them now: progression is per player, so there is no
			// share to show.
			// `loadgame` too: the save picker Y opens (`sub_8243B070`) writes each card's quest line
			// and the description into that unit, and without it the authored placeholder
			// ("fdggdf dfg …") stood where the guest's text was.
			GuestTextUnits = { "LeftPage", "RightPage", "Connecting", "loadgame" },

			// AND THIS IS WHAT LIGHTS THE PROFILE BLOCKS. The gamertag, its stars, the
			// reputation and gamerscore captions and `Select Gamer Profile` are all BUILT and
			// POPULATED — a live read showed the gamertag already resolved to a real name — and
			// held dark by the guest's slides.
			//
			// WRITING A VALUE IS NOT ENOUGH HERE, which this note used to claim. A value only
			// DROPS the guest's opacity so the slide or the authored number takes over, and
			// both are zero for this block: `ProfileSelector.Buttons` carries `auth=0`. Nor
			// does opacity inherit — a live read found `ProfileSelector` at 1.00 with all
			// forty-odd elements under it at 0.01. The provider states the number outright
			// over the whole subtree; see its `Opacities`.
			Provider = Engine_UiPageProviders.Coop,
		});

		// The sub-screens are CUT CONTENT, so they have no templates: `expressionsscreen`,
		// `orbinfoscreen`, `regionsscreen`, `questsscreen`, `weaponsscreen`,
		// `inventoryitemsscreen` and `quickmainmenu` ship in the bank and are never loaded — the
		// executable names 33 of the 52 `.bgf`, and every path into the loader passes a global
		// literal or a value out of the screen-id table.
		//
		// Regions is a SUBMENU of the world map instead, populated by `sub_829A3A30` with row
		// id = index + 999 and event type 41 (the fast-travel contract). Quests live on the
		// logbook, the world map's page and the HUD banner.

		// The three remaining dialogs with no owned view: all flat — no book, no page stack.
		//
		// THE QUEST CARD IS A DISPLAY BOX (`GUI.DisplayQuestBoxParams`, class 0x820AE6C8), never
		// the current screen: it stands from `DisplayBoxScreen`. Its Open `sub_823CFBE0` writes
		// every string once — QuestDescription, the DetailsGroup amounts, Remember, the
		// conditions — so the text is the guest's; the info panel runs only for a current screen.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 5, Name = "questrepeatable", Presentation = "questrepeatable",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.BoxDismiss,
			ChromeUnits =
			{
				"PaperBackground", "Buttons", "Stars", "DetailsGroup", "QuestTitle",
				"QuestDescription", "QuestDescription2", "RiskLevel", "Remember",
				"Condition1", "Condition2",
			},
			GuestTextUnits = { "elements" },
		});

		// THE END CREDITS — drawn by Engine_UiCreditsRoll (the file's elements are anonymous,
		// so nothing here names chrome). The template carries only the input: the handler
		// `sub_826B7EF8` skips the roll on 36 or 37.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 29, Name = "creditsscreen", Presentation = "creditsscreen",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.ScreenSelect,
			RevealedBack = Engine_UiScreenActions.ScreenBack,
		});

		// THE OLD-STYLE INFO CARD: `GUI.DisplayInfoBoxParams` with no `DisplayBoxStyle` (the
		// native's default is 0, `sub_823033D0`) builds class 0x820EBF1C, and photo mode's
		// uploading notice is the same class. Its Open `sub_8299E490` writes Paper.Text / Title /
		// Accept / Info and the button alphas, then plays root slide 7 — the `Paper` variant the
		// display-box surface vetoes. Only `Paper` is chrome here, so while the acceptance and
		// button boxes (DefaultGroup) are up this book has nothing active to draw. Input stays
		// the guest's: A and hold-A are the class's raw slot, and it does not freeze the game.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 7, Name = "displaybox", Presentation = "displaybox",
			ChromeUnits = { "Paper" },
			GuestTextUnits = { "Paper" },
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 11, Name = "minigamecompleted", Presentation = "minigamecompleted",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.ScreenDismiss,

			// `Select` has two bearers — the button GROUP is 0, its label Text is 1 — so the
			// bare name is right here and the label comes through the text sweep.
			// `Title` too: un-cloned it had no node, and the tally drew with no heading.
			ChromeUnits = { "paperbox", "Select", "Motif", "Amounts", "Title" },
			PanelSubtrees = { "Component" },

			// EVERY STRING IS THE GUEST'S: `TurnOnMiniGameComplete` publishes MiniGameTitle and
			// MiniGameButton, `AddAmountToMiniGameComplete` MiniGameName%d/Amount%d, and the
			// tally's own tick republishes the amounts as they count up — all through TextName
			// registry 6 onto these elements. Without this unit the book showed the file's
			// placeholders ('Money earned', '0000001') on every row and no title.
			GuestTextUnits = { "Component" },
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 12, Name = "questcompletion", Presentation = "questcompletion",
			OwnsRevealedInput = true,
			RevealedAccept = Engine_UiScreenActions.ScreenDismiss,

			// `Component` is the paperbox; `frame2` is a SECOND, hand-authored ten-piece frame
			// whose parts collide by name with the paperbox's, which is why the units are named
			// at the top level and never by their pieces.
			//
			// THE FIVE TEXT ELEMENTS ARE UNITS TOO. `QuestTitle`, `QuestDescription`, `Reward`,
			// `RenownPoints` and `Points` are SIBLINGS of the units above under `elements`, and
			// `elements` is not itself a unit — so nothing cloned them, they got no field, and
			// the page drew a title, one bullet and the Done button over an otherwise blank
			// parchment. That is the whole of the "reward screen is incomplete" report: no
			// heading, no description, no renown line, no amount. They cannot be reached by
			// naming `elements` instead, because that would fold `Component` and `frame2` into
			// one nested unit and collide their ten-piece frames.
			ChromeUnits =
			{
				"Component", "frame2", "leaf", "Bullet1", "Bullet2", "Buttons", "Image",
				"QuestTitle", "QuestDescription", "Reward", "RenownPoints", "Points",
			},
			PanelSubtrees = { "elements" },

			// EVERY STRING IS THE GUEST'S, as on the tally next door. `SetGoldRewardInEpilogue`
			// and `AddItemRewardToEpilogueScreen` publish EpilogueTitle, EpilogueDescription,
			// EpilogueReward, EpilogueRenown, EpilogueRenownAmt and EpilogueItem1/2 through
			// TextName registry 6 onto these elements, and taking a native does not suppress
			// the guest's own write. Without this the freshly cloned fields would draw the
			// FILE'S placeholders — "A Bridge too Far", "Ripper has been thwarted…", "1000" —
			// which is worse than blank, because it reads as real data.
			GuestTextUnits = { "elements" },
		});

		// 10 QUERY — the villager card. Opened from three sites — the interaction chain
		// `sub_823FDA78`, the queued-request switch `sub_82805360` case 14, and the script
		// command `TriggerRenameScreen` — all calling `sub_826CCC88(screen, entity)`, which
		// stores the subject at `screen+28` and fills ONCE; there is no per-frame filler.
		//
		// Everything it shows is published by name, so it needs no provider: three float meters
		// (`Love`, `Funny`, `Attractive` — the middle one INVERTED, `(x-1) * -0.5 * 100`) plus
		// string headings and likes/dislikes rows, all through the FNV-1 registries the HUD
		// reads. `elements` is the whole card; `Layer`'s other two children are the key light
		// and the camera, which the host supplies.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 10, Name = "query", Presentation = "queryscreen",
			OwnsRevealedInput = true,
			RevealedBack = Engine_UiScreenActions.ScreenBack,
			ChromeUnits = { "elements" },
			PanelSubtrees = { "elements" },

			// The card is entirely the guest's strings and this screen has no provider; without
			// this every field falls back to the file's unresolved caption keys.
			// `sub_826CCC88` fills once on open: `character` ('<Name> the <Job>'),
			// `characterdescription`, the `Opinion`/`Likes`/`Dislikes` headings, `Likes1..3`,
			// `LikesType1..3`, `Dislikes1..3` and the button caption under `buttons`.
			GuestTextUnits = { "elements" },
		});

		// THE FRONT END. Its overlays — the storage dialog, the loading animation, the countdown
		// — live in the same tree as the menu frames and the guest lights one at a time, which
		// the per-unit draw gate handles. One unit: `Layer` has fourteen spatial children and
		// all the menu chrome hangs off `Foreground`:
		//
		//     Foreground / ExpandableMenu (x -800) / Buttons (26 rows), MenuHighlight,
		//                                            KeyLight, LeftFrame
		//                / Book (x 400)            / Pages / FriendsPage, RightFrame
		//
		// `LeftFrame` is INSIDE ExpandableMenu, not a sibling of `Book`: the assembly is authored
		// at x -800 and the shipped Lua tweens it in, so cloning `LeftFrame` alone would park it
		// at its authored canvas x of -1036, off screen. Taking `Foreground` whole keeps the
		// parenting, so the guest's one stated position on `ExpandableMenu` carries it in.
		//
		// `Theme` is not a unit: it is the off-canvas art library at y -1000 the frames are
		// cloned FROM. `choosecard`, `loadgame` and `dialog` are siblings of `Foreground`, drawn
		// by their own Engine_FrontEnd* views.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1010, Name = "frontend", Presentation = "frontendmainmenu",
			ChromeUnits = { "Foreground", "LiveCountdown", "LoadingAnimation" },
			PageRootUnit = "Pages",

			// The row pool and its cursor sit INSIDE `Foreground` and Godot draws both.
			OwnedElsewhere =
			{
				"ExpandableMenu.Buttons", "MenuHighlight", "choosecard", "loadgame", "dialog",
			},
		});

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1011, Name = "title", Presentation = "frontendstartupscreen",

			// "Press Start" has a space in the element name.
			//
			// `White` is a UNIT, not a backdrop: a direct child of `Layer`, a 1280x720 quad at
			// (0,0,0) scale (12.8, 7.2), sibling of `logo`. Canvas +Z is toward the camera, so
			// at z 0 it sits IN FRONT of the logo at z -1 and PressStart at z -2 — the logo's
			// white flash, authored at full opacity with its fade in the Anark scene and in no
			// script. Engine_FrontEndTitle runs that fade.
			ChromeUnits = { "logo", "White", "PressStart", "Legal", "dialog" },
		});

		// THE SCROLLING BACKDROP is a 7x2 grid of 512x512 tiles with a second copy at +3453 to
		// hide the wrap — the period is 3453, not the 3584 the tile maths suggests, because the
		// copy overlaps by 131. One unit: the whole `Scrolling` group, scrolled as a whole.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 1012, Name = "backdrop", Presentation = "scrollingbackground",
			ChromeUnits = { "Scrolling" },
			PageRootUnit = "Scrolling",

			// The scroll is the guest's own statement: it moves `Group` leftwards and the clone
			// binding writes every stated transform, so the wrap copy rides in with it. A host
			// rate would be a second clock against the one the title's music and fade run on.
		});

		// THE ACHIEVEMENTS SCREEN — host-only id 1013, on the logbook's own furniture.
		//
		// The guest has no screen for these. Its answer to the logbook's `Achievements` row is
		// `XamShowAchievementsUI` (`sub_826D9620` case 150), the console's guide blade, which on
		// this platform opens nothing at all — so there is no guest screen id to borrow and no
		// tab for `sub_826B89E0`'s switch to map. It is ours end to end.
		//
		// On `logbookscreen` because the shape already fits: a full-height row list on the left
		// and a subject page laying out a title, a 64-unit picture and a paragraph on the right,
		// which is exactly an achievement. Nothing about that art is logbook-specific — the book
		// is cloned from the FILE-ONLY pattern book, so this record needs no live presentation of
		// the guest's and stands whether or not the logbook has ever been opened.
		//
		// `PanelSubtrees` carries the whole page stack rather than `ExpressionsPage` alone: the
		// provider switches seven of the eight off and the panel must know the fields it is
		// hiding, the same statement screen 17 makes.
		// The Active Mods book is the same screen over its own list: one page, two subjects. The
		// host page book (Engine_UiHostBoxPage) is the same screen again: a host question's
		// title and body on the page, Continue and Back as the rows.
		foreach ((uint id, string name, string provider) in new[]
		{
			(Engine_UiMultiplayerPage.ScreenId, "multiplayer", Engine_UiPageProviders.Multiplayer),
			(Engine_UiMultiplayerPage.ModsScreenId, "mods", Engine_UiPageProviders.Multiplayer),
			(Engine_UiHostBoxPage.ScreenId, "hostbox", Engine_UiPageProviders.HostBox),
		})
		{
			AddScreen(screens, new Engine_UiScreenTemplate
			{
				ScreenId = id, Name = name, OwnsList = true,
				Provider = provider,
				Presentation = "logbookscreen",
				ChromeUnits = { "Foreground" },
				OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
				PageRootUnit = "Pages", BackdropUnit = "background_yellow",

				// THE PAGE IS BOXED AND SCROLLS: the borrowed Expressions page's contents are the
				// logbook's own scroll region (screen 17), clipped to `Mask`, with its scroll bar —
				// a mod's description or a friend's status runs as long as it is written.
				ScrollContent = "Foreground.Book.Pages.ExpressionsPage.Contents",
				ScrollMask = "Mask",
				ScrollBar = "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
				PanelSubtrees =
				{
					"Foreground.Book.Pages.MoralityPage",
					"Foreground.Book.Pages.ExpressionsPage",
					"Foreground.Book.Pages.CompletedQuestsPage",
					"Foreground.Book.Pages.RenownPage",
					"Foreground.Book.Pages.AttractivenessPage",
					"Foreground.Book.Pages.StatsPage",
					"Foreground.Book.Pages.DogPage",
					"Foreground.Book.Pages.HelpPage",
				},
			});
		}

		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = Engine_UiAchievementPage.ScreenId, Name = "achievements", OwnsList = true,
			Provider = Engine_UiPageProviders.Achievements,
			Presentation = "logbookscreen",

			// The list a host screen stands (Engine_UiHostScreen).
			Menu = Engine_UiAchievementPage.MenuName,
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor. On a host-only screen it owns the anchor
			// too — there is no guest `ExpandableMenu` position to ride in on, so the view sits
			// at the pool's authored spot.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_yellow",

			// THE PAGE IS BOXED AND SCROLLS, as its siblings on this book: an achievement's
			// description runs as long as it is written.
			ScrollContent = "Foreground.Book.Pages.ExpressionsPage.Contents",
			ScrollMask = "Mask",
			ScrollBar = "Foreground.Book.Pages.ExpressionsPage.Scrollbar",
			PanelSubtrees =
			{
				"Foreground.Book.Pages.MoralityPage",
				"Foreground.Book.Pages.ExpressionsPage",
				"Foreground.Book.Pages.CompletedQuestsPage",
				"Foreground.Book.Pages.RenownPage",
				"Foreground.Book.Pages.AttractivenessPage",
				"Foreground.Book.Pages.StatsPage",
				"Foreground.Book.Pages.DogPage",
				"Foreground.Book.Pages.HelpPage",
			},
		});

		// Screen 8 — the receive-items card, owned elsewhere; recorded for its preview anchor.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = 8, Name = "receiveitems", Presentation = "receiveitemsscreen",
			PreviewAnchor = "Foreground.SpinningCube.Cube",

			// Front, back — see `BoxUnit`.
			BoxPresentation = "receiveitemsscreen",
			BoxUnits = { "Foreground", "CardBack" },
			BoxPaths = { ["NameAndIcon"] = "NameAndIcon", ["Title"] = "Title", ["Text"] = "Text", ["IconGraphic"] = "IconGraphic", ["Buttons"] = "Buttons", ["Mask"] = "Mask", ["NameAndIcon/IconGraphic"] = "NameAndIcon/IconGraphic", ["NameAndIcon/Title"] = "NameAndIcon/Title", ["NameAndIcon/Text"] = "NameAndIcon/Text", ["Buttons/Buttons/"] = "Buttons/Buttons/", ["PaperBackground"] = "PaperBackground", ["Group"] = "Group", ["Scrollbar/UpArrow"] = "Scrollbar/UpArrow", ["Scrollbar/DownArrow"] = "Scrollbar/DownArrow", ["Scrollbar/Icon"] = "Scrollbar/Icon" },
		});

		// THE MODAL BOXES. Drawn by their own views (`Engine_UiBoxes`), which take the
		// presentation and the art units from these records BY ROLE — the position in
		// `boxUnits` is the role, stated beside each — so a mod re-points a box at its own art
		// in the same file it re-points a book. `boxPresentation`, not `presentation`: these
		// are not screens, and a record that named a live file would be taken for its book.
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = MessageBoxId, Name = "messagebox", BoxPresentation = "messageboxmulti",
			BoxUnits = { "Background", "paper" },             // dim, paper
			BoxPaths = { ["MessageBox"] = "MessageBox", ["paperbox"] = "paperbox", ["MessageText"] = "MessageText", ["MessageBox/Action"] = "MessageBox/Action", ["MessageBox/MessageText"] = "MessageBox/MessageText" },
		});
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = QueryBoxId, Name = "querybox", BoxPresentation = "querybox",
			BoxUnits = { "QueryBox", "Background", "Paper" }, // box, dim, paper
			BoxPaths = { ["Choices"] = "Choices", ["Affirmative"] = "Affirmative", ["Negative"] = "Negative", ["XAChoices"] = "XAChoices", ["EquipAndContinue"] = "EquipAndContinue", ["Continue"] = "Continue", ["ReloadChoices"] = "ReloadChoices", ["Reload"] = "Reload", ["Cancel"] = "Cancel", ["Text"] = "Text" },
		});
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = NumericBoxId, Name = "numericbox", BoxPresentation = "numericadjusterbox",
			BoxUnits = { "QueryBox", "Background", "Paper" }, // box, dim, paper
			BoxPaths = { ["Text"] = "Text", ["PriceWidget"] = "PriceWidget", ["Arrows"] = "Arrows", ["up"] = "up", ["down"] = "down", ["PriceWidget/Text"] = "PriceWidget/Text", ["PriceWidget/Arrows/"] = "PriceWidget/Arrows/" },
		});
		AddScreen(screens, new Engine_UiScreenTemplate
		{
			ScreenId = MenuBoxId, Name = "menubox", BoxPresentation = "menubox",
			BoxUnits = { "Group" },                           // the option group
			BoxPaths = { ["Choices"] = "Choices", ["Title"] = "Title", ["Multiplayer_Button"] = "Multiplayer_Button", ["Choice1"] = "Choice1", ["Frame"] = "Frame", ["OK/CANCEL"] = "OK/CANCEL", ["Negative"] = "Negative", ["GamerTag"] = "GamerTag", ["gamertag"] = "gamertag", ["Choices/Multiplayer_Button"] = "Choices/Multiplayer_Button", ["Choice1/Frame/Choice1"] = "Choice1/Frame/Choice1", ["Choice1/Choice1"] = "Choice1/Choice1", ["Choice1/RimSelect"] = "Choice1/RimSelect", ["OK/CANCEL/Negative"] = "OK/CANCEL/Negative" },
		});
	}

	/// <summary>Host-only template ids for the modal boxes (the guest's box ids are modal
	/// codes, not screens).</summary>
	public const uint MessageBoxId = 1020u;
	public const uint QueryBoxId = 1021u;
	public const uint NumericBoxId = 1022u;
	public const uint MenuBoxId = 1023u;

	/// <summary>The `.bgf` a box is drawn from, off its record.</summary>
	public static string BoxPresentation(uint id, string fallback) =>
		Screen(id) is { BoxPresentation.Length: > 0 } box ? box.BoxPresentation : fallback;

	/// <summary>
	/// An element path inside a box's art. The built-in path is the KEY; the record's
	/// `boxPaths` maps it to the path in the art actually loaded, and an unmapped key is itself.
	/// </summary>
	public static string BoxPath(uint id, string path) =>
		Screen(id) is { } box && box.BoxPaths.TryGetValue(path, out string? mapped) &&
		mapped.Length != 0
			? mapped
			: path;

	/// <summary>A box's art unit by ROLE — its position in the record's `boxUnits` list.</summary>
	public static string BoxUnit(uint id, int role, string fallback) =>
		Screen(id) is { } box && role < box.BoxUnits.Count && box.BoxUnits[role].Length != 0
			? box.BoxUnits[role]
			: fallback;

	/// <summary>
	/// One pause tab — the command the guest's dispatch recognises, plus whatever the guest
	/// spawned UNDER it. `Source = "spawn"` reads the SPAWN CAPTURE alone (no Gameface Lua): the
	/// whole spawn tree flat, each row's parent stated by name (`String4`), so a tab's children
	/// are in hand whether or not the Lua has laid them out.
	///
	/// The capture is taken AT `SpawnButton`, so it carries name, parent, the decorated label,
	/// `(EventType, id)` and the rating, but NOT `Flags` (ghosted/showing) or `Emphasis`, which
	/// are written onto the Lua item afterwards by `SetItemEmphasis` and the ghosting setters —
	/// which is why the in-game screens cannot use `spawn` yet: a ghosted row that became
	/// committable would let the player activate something the game disabled. The pause menu is
	/// exempt because its ghosting is already ours —
	/// <see cref="Engine_PauseMenu.ApplyTabAvailability"/> ghosts an unavailable tab from
	/// `RefiiHostPauseTabsAvailable`, the guest's `sub_826B83D0` read directly.
	/// </summary>
	private static Engine_UiMenuEntry Tab(string name, string tag, uint tab, string when = "") => new()
	{
		Name = name,
		Tag = tag,
		When = when,
		Command = $"{Engine_PauseMenu.TabCommandPrefix}{tab}",
		Source = "spawn",

		// A TAB ENTERS A SCREEN — `sub_8298DF98` spawns every tab with event type 300
		// (0x8298e438 …). Left 0, the source made it a submenu (301) with no children, and the
		// automatic submenu ghosting greyed Map, Items, Weapons, Clothing, Logbook and Options
		// for good. The guest's only tab ghosting is `sub_8298F648`'s availability, which
		// Engine_PauseMenu.ApplyTabAvailability states.
		EventType = (int)Engine_UiMenuConstants.EventType.EnterScreen,
	};

	/// <summary>
	/// A tab row the HOST authors outright — label from its tag, no guest row behind it. For
	/// the co-op variant: player two's guest pause root is the HENCHMAN's, whose spawned rows
	/// are other rows entirely, so a spawn-sourced "Weapons" bound to its ordinal came up
	/// captioned "Config" and ghosted by the henchman mask.
	/// </summary>
	private static Engine_UiMenuEntry HostTab(string name, string tag, uint tab, string when = "") => new()
	{
		Name = name,
		Tag = tag,
		When = when,
		Command = $"{Engine_PauseMenu.TabCommandPrefix}{tab}",
		// HOST-ONLY, so the stated-label pass leaves it alone: the positional fallback there
		// handed these rows the henchman list's captions by ordinal ('Config', 'Options').
		HostOnly = true,
	};

	private static void AddScreen(Dictionary<uint, Engine_UiScreenTemplate> screens, Engine_UiScreenTemplate template) =>
		screens[template.ScreenId] = template;

	/// <summary>
	/// The built-in books' wide layouts, from each file's own tree (`ui_viewer`,
	/// `REFII_UI_TREE=Layer`): the list assembly `Foreground.ExpandableMenu` carries the left
	/// cover, and every other child of `Foreground` belongs to the right-hand page. Stated in
	/// one table because the shipped books share one skeleton; an override record states its
	/// own lists.
	/// </summary>
	private static void StateWideLayouts(Dictionary<uint, Engine_UiScreenTemplate> screens)
	{
		const string list = "Foreground.ExpandableMenu";
		var right = new Dictionary<uint, string[]>
		{
			[0] = new[] { "Foreground.RightFrame", "Foreground.Pages", "Foreground.ReusableTokens" },
			[14] = new[] { "Foreground.Book" },
			[15] = new[] { "Foreground.Book" },
			[16] = new[] { "Foreground.Book" },
			[25] = new[] { "Foreground.Book" },
			[26] = new[] { "Foreground.Book" },
			[27] = new[] { "Foreground.Book" },
			[17] = new[] { "Foreground.Book", "Foreground.Mask" },
			[18] = new[] { "Foreground.Book" },
			[21] = new[] { "Foreground.Book" },
			[22] = new[]
			{
				"Foreground.Book", "Foreground.FrontPage",
				"Foreground.PageTextureFar", "Foreground.PageTextureNear",
			},
			[23] = new[] { "Foreground.Book" },
			[28] = new[] { "Foreground.Book" },
			[Engine_UiMultiplayerPage.ScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
			[Engine_UiMultiplayerPage.ModsScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
			[Engine_UiHostBoxPage.ScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
			[Engine_UiAchievementPage.ScreenId] = new[] { "Foreground.Book", "Foreground.Mask" },
		};

		foreach ((uint id, string[] pieces) in right)
		{
			if (screens.TryGetValue(id, out Engine_UiScreenTemplate? screen))
			{
				screen.WideLeft = new List<string> { list };
				screen.WideRight = new List<string>(pieces);
			}
		}

		// The front end is the same book skeleton; its painting is the `backdrop` record's.
		if (screens.TryGetValue(1010, out Engine_UiScreenTemplate? frontEnd))
		{
			frontEnd.WideLeft = new List<string> { list };
			frontEnd.WideRight = new List<string> { "Foreground.Book" };
		}

		if (screens.TryGetValue(1012, out Engine_UiScreenTemplate? painting))
		{
			painting.WideGround = new List<string> { "Scrolling" };
		}

		// CO-OP HAS NO BACKDROP (coopscreen.bgf, `Foreground` at 1.3): the pages and frames
		// ride out and each page's parchment widens from the gutter to stay under them.
		if (screens.TryGetValue(24, out Engine_UiScreenTemplate? coop))
		{
			coop.WideLeft = new List<string> { "Foreground.LeftPage", "Foreground.LeftFrame2" };
			coop.WideRight = new List<string> { "Foreground.RightPage", "Foreground.RightFrame2" };
			coop.WideStretch = new List<string>
			{
				"Foreground.LeftBackground.PageTexture", "Foreground.RightBackground.PageTexture",
			};
		}

		if (screens.TryGetValue(23, out Engine_UiScreenTemplate? worldMap))
		{
			worldMap.WideGround = new List<string>
			{
				"WorldMap_Spring_A", "WorldMap_Spring_B", "WorldMap_Spring_C",
				"WorldMap_Spring_D", "WorldMap_Spring_C2", "WorldMap_Spring_A2",
			};
		}
	}

	/// <summary>How far the owned row list of a screen rides left on a wide window.</summary>
	public static float WideListShift(uint screenId) =>
		Screen(screenId) is { WideLeft.Count: > 0 } ? Engine_UiSceneBuilder.WideShift : 0f;

	// -----------------------------------------------------------------------------------
	// Files — the player's own and the snapshot. Every record is read and written by
	// Engine_UiTemplateJson, composed by Engine_UiTemplateSet.
	// -----------------------------------------------------------------------------------

	/// <summary>The player's own file, applied after every mod.</summary>
	private static int ApplyOverrides(Engine_UiTemplateSet set, string path)
	{
		if (!FileAccess.FileExists(path))
		{
			return 0;
		}

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		return file is null ? 0 : set.ApplyText(file.GetAsText(), owner: string.Empty, path);
	}

	/// <summary>Writes every record as it runs — the editor's starting point and the override
	/// file's documentation.</summary>
	/// <summary>
	/// Writes the composed templates for authors to read. A side product of loading: a failure here
	/// is reported and the templates stand — it once threw from the HUD build and took the whole UI
	/// down with it.
	/// </summary>
	private static void SaveSnapshot(string path)
	{
		try
		{
			string text = _set!.SnapshotText();
			DirAccess.MakeDirRecursiveAbsolute("user://ui");
			using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
			file?.StoreString(text);
		}
		catch (System.Exception exception)
		{
			GD.PushError($"[ui-templates] the snapshot was not written ({path}): {exception.Message}");
		}
	}
}
