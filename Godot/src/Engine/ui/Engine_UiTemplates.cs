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
	/// The row's icon texture, as `SpawnButton` takes it — a `.tex` path relative to the game's
	/// art root, or empty for a row that draws none.
	/// </summary>
	public string Icon = string.Empty;

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
	public List<Engine_UiMenuEntry> Entries = new();

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
			changed |= RefreshEntry(menu, entry);
		}

		return changed;
	}

	private static bool RefreshEntry(Engine_FrontEndMenu menu, Engine_UiMenuEntry entry)
	{
		bool changed = false;
		foreach (Engine_UiMenuEntry child in entry.Children)
		{
			changed |= RefreshEntry(menu, child);
		}

		if (entry.Source.Length == 0 || menu.Find(entry.Name) is not { } item)
		{
			return changed;
		}

		IReadOnlyList<Engine_UiMenuSources.Row> rows =
			Engine_UiMenuSources.Rows(entry.Source, entry.Name);

		var current = new List<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item child in item.Children)
		{
			if (child.Sourced)
			{
				current.Add(child);
			}
		}

		if (Same(current, rows))
		{
			return changed;
		}

		foreach (Engine_UiExpandableMenu.Item stale in current)
		{
			item.Children.Remove(stale);
			menu.Model.Forget(stale);
		}

		foreach (Engine_UiMenuSources.Row row in rows)
		{
			Engine_UiExpandableMenu.Item sourced = menu.Model.SpawnButton(
				row.Name, row.Label, row.Icon, row.EventType,
				idLow: (int)(row.Id & 0xFFFF), idHigh: (int)((row.Id >> 16) & 0xFFFF),
				row.Rating, entry.Name);
			sourced.StatedLabel = row.Label;
			sourced.Committable = row.Committable;
			sourced.Sourced = true;
		}

		return true;
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
			if (!string.Equals(current[i].Name, rows[i].Name, System.StringComparison.Ordinal) ||
				!string.Equals(current[i].StatedLabel, rows[i].Label, System.StringComparison.Ordinal))
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
	public Engine_FrontEndMenu Build()
	{
		var menu = new Engine_FrontEndMenu();
		foreach (Engine_UiMenuEntry entry in Entries)
		{
			Spawn(entry, Engine_UiExpandableMenu.RootName);
		}

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

		return menu;

		void Spawn(Engine_UiMenuEntry entry, string parent)
		{
			// A category is a submenu row: an entry with children stating no event of its own
			// gets 301, which is what makes `SelectMenuItem` treat the press as a fold.
			int eventType = entry.EventType != 0
				? entry.EventType
				: entry.Children.Count > 0
					? (int)Engine_UiMenuConstants.EventType.Submenu
					: 0;

			Engine_UiExpandableMenu.Item item = menu.Model.SpawnButton(
				entry.Name, entry.Tag, entry.Icon, eventType,
				idLow: 0, idHigh: 0, entry.Rating, parent);

			// Host routing is not menu content, so it rides beside the spawn.
			item.Command = entry.Command;
			item.Opens = entry.Opens;
			item.HostOnly = entry.HostOnly;

			// A host-authored row has no guest identity: its `(type, 0)` would resolve row zero,
			// so it dispatches by name or by command instead.
			item.Committable = false;

			foreach (Engine_UiMenuEntry child in entry.Children)
			{
				Spawn(child, entry.Name);
			}

			// Then whatever the source supplies, after the authored children so a fixed row keeps
			// its place at the top. A source resolving to nothing leaves the entry as authored.
			foreach (Engine_UiMenuSources.Row row in
				Engine_UiMenuSources.Rows(entry.Source, entry.Name))
			{
				Engine_UiExpandableMenu.Item sourced = menu.Model.SpawnButton(
					row.Name, row.Label, row.Icon, row.EventType,
					idLow: (int)(row.Id & 0xFFFF), idHigh: (int)((row.Id >> 16) & 0xFFFF),
					row.Rating, entry.Name);

				// A sourced row carries the guest's own identity — the pair its dispatcher
				// strcmps and switches on — so unlike an authored one it IS committable.
				sourced.StatedLabel = row.Label;
				sourced.Committable = row.Committable;
				sourced.Sourced = true;
			}
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
	/// A full-canvas backdrop tint to stand behind the book, by art name. Empty for none. Cloned
	/// OVERSIZED: the shipped quad is exactly 1280x720 and leaves the revealed edges untinted at
	/// any other aspect.
	/// </summary>
	public string BackdropUnit = string.Empty;

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

	/// <summary>Live text fields an owned build re-meshes in place, dotted paths.</summary>
	public List<string> LiveTexts = new();

	/// <summary>The static menu this screen offers, or empty when the guest supplies rows.</summary>
	public string Menu = string.Empty;
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
/// The template registry: built-in records for the shipped screens, overridden by
/// <c>user://ui/templates.json</c>, snapshotted to <c>user://ui/templates.snapshot.json</c>
/// so the editor and modders start from what actually runs.
///
/// The built-ins are the decoded contracts as records — entry names the dispatch strcmps on,
/// event params, tab ids — so an override renaming an entry's LABEL keeps working and one
/// renaming its NAME stops dispatching, exactly as the guest behaves.
/// </summary>
public static class Engine_UiTemplates
{
	public const string OverridePath = "user://ui/templates.json";
	public const string SnapshotPath = "user://ui/templates.snapshot.json";

	private static Dictionary<uint, Engine_UiScreenTemplate>? _screens;
	private static Dictionary<string, Engine_UiMenuTemplate>? _menus;

	/// <summary>Every screen record, for a caller that works across the whole set — the component
	/// exporter and the editor's listing.</summary>
	public static IEnumerable<Engine_UiScreenTemplate> Screens
	{
		get
		{
			EnsureLoaded();
			return _screens!.Values;
		}
	}

	/// <summary>
	/// The modal answer table — see <see cref="Engine_UiModalTemplate"/>. An unlisted box keeps
	/// the guest's own input, deliberately: the coop boxes (39/40/41/43) answer only on the
	/// router's pad index 2 while our lever passes 0, the marketplace offer (42) carries an X
	/// download action and a sign-in gate, and photo mode's box has no dismiss code at all.
	/// </summary>
	private static readonly Engine_UiModalTemplate[] Modals =
	{
		// QueryBoxes — the saving, shop and real-estate confirms all answer 46/47.
		Query(2, 46, 47), Query(15, 46, 47), Query(17, 46, 47), Query(19, 46, 47),
		Query(23, 46, 47), Query(24, 46, 47), Query(25, 46, 47),
		Query(29, 46, 47), Query(30, 46, 47), Query(31, 46, 47), Query(32, 46, 47),
		Query(33, 46, 47), Query(34, 46, 47),
		Query(3, 46, 47), Query(4, 46, 47), Query(5, 46, 47), Query(6, 46, 47),
		Query(8, 46, 47), Query(9, 46, 47), Query(10, 46, 47), Query(11, 46, 47),

		// The henchman and generic boxes answer the uniform accept/back pair.
		Query(26, 36, 37), Query(27, 36, 37), Query(28, 36, 37), Query(44, 36, 37),

		// Fast travel confirms on the per-screen Y action.
		Query(13, 39, 37),

		// B does nothing here — see the note on Cancel.
		Query(20, 36, null),

		// MessageBoxes — a single dismiss. Ids 0 is per-screen; the rest are uniform.
		Dismiss(0, 36, screenId: 22),   // shop: already wearing that tattoo
		Dismiss(0, 46, screenId: 28),   // real estate: buy denied / cannot resell
		Dismiss(1, 36),                 // abilities: maxed / not enough experience
		Dismiss(12, 36),                // world map: cannot fast travel
		Dismiss(16, 36), Dismiss(18, 36), Dismiss(21, 36), Dismiss(22, 36),
		Dismiss(38, 36),                // equipment / expression / augment notices
	};

	private static Engine_UiModalTemplate Query(uint id, uint confirm, uint? cancel) =>
		new() { ModalId = id, Confirm = confirm, Cancel = cancel };

	private static Engine_UiModalTemplate Dismiss(uint id, uint code,
		uint screenId = uint.MaxValue) =>
		new() { ModalId = id, ScreenId = screenId, Confirm = code, Dismissal = true };

	/// <summary>
	/// The answer for a box, or null when it is not owned. A screen-specific row wins over the
	/// any-screen one, so id 0 can be per-screen while every other id states itself once.
	/// </summary>
	public static Engine_UiModalTemplate? Modal(uint modalId, uint screenId, bool dismissal)
	{
		Engine_UiModalTemplate? general = null;
		foreach (Engine_UiModalTemplate modal in Modals)
		{
			if (modal.ModalId != modalId || modal.Dismissal != dismissal)
			{
				continue;
			}

			if (modal.ScreenId == screenId)
			{
				return modal;
			}

			if (modal.ScreenId == uint.MaxValue)
			{
				general = modal;
			}
		}

		return general;
	}

	/// <summary>One screen's record, or null for a screen Godot has no template for.</summary>
	public static Engine_UiScreenTemplate? Screen(uint screenId)
	{
		EnsureLoaded();
		return _screens!.TryGetValue(screenId, out Engine_UiScreenTemplate? template)
			? template
			: null;
	}

	/// <summary>
	/// Whether ANY screen Godot owns is drawn from this presentation — not
	/// `ScreenFor(...) is not null`, which answers "which single screen is this" and returns null
	/// on an ambiguous file (three screens share `GiftGivingScreen.bgf`). Ambiguity is not the
	/// question here: every screen sharing a file draws the same chrome from the same pattern
	/// book, so if any owns it the guest's copy is redundant.
	/// </summary>
	public static bool AnyScreenOwns(string presentation)
	{
		EnsureLoaded();

		foreach (Engine_UiScreenTemplate template in _screens!.Values)
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

	/// <summary>The screen drawn from a given `.bgf`; null when no template names it, and null
	/// when several do — an ambiguous file cannot say which screen is up.</summary>
	public static Engine_UiScreenTemplate? ScreenFor(string presentation)
	{
		EnsureLoaded();

		Engine_UiScreenTemplate? found = null;
		foreach (Engine_UiScreenTemplate template in _screens!.Values)
		{
			if (!string.Equals(template.Presentation, presentation,
					System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (found is not null)
			{
				return null;
			}

			found = template;
		}

		return found;
	}

	/// <summary>A named menu template, or null.</summary>
	public static Engine_UiMenuTemplate? Menu(string name)
	{
		EnsureLoaded();
		return _menus!.TryGetValue(name, out Engine_UiMenuTemplate? menu) ? menu : null;
	}

	private static void EnsureLoaded()
	{
		if (_screens is not null)
		{
			return;
		}

		_screens = new Dictionary<uint, Engine_UiScreenTemplate>();
		_menus = new Dictionary<string, Engine_UiMenuTemplate>();
		BuildDefaults();
		int overridden = ApplyOverrides(OverridePath);
		SaveSnapshot(SnapshotPath);
		GD.Print($"[ui-templates] {_screens.Count} screen(s), {_menus.Count} menu(s)" +
			(overridden == 0 ? string.Empty : $"; {overridden} record(s) overridden"));
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
	private static void RegisterPopulationTables()
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

			_menus![table.Script] = menu;
		}
	}

	private static void BuildDefaults()
	{
		RegisterPopulationTables();

		// The pause root — `sub_8298DF98`'s spawn list verbatim: names are the identities its
		// dispatch recognises, the Saving children dispatch by EVENT PARAM ({type 2, param}),
		// the tabs by the tab lever's ids, and SetInitiallySelectedItem(2) is the highlight.
		_menus!["pause-root"] = new Engine_UiMenuTemplate
		{
			Name = "pause-root",
			Highlight = "Map",
			Entries =
			{
				new Engine_UiMenuEntry
				{
					Name = "Saving", Tag = "GUI_SCREEN_PAUSE_SAVING",

					// `spawn`, not the Gameface VM. Safe here only: see the Tab() note below.
					Source = "spawn",
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
							Tag = "GUI_SCREEN_PAUSE_SAVE_AND_QUIT",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}1",
						},
						new Engine_UiMenuEntry
						{
							Name = "QuitDontSave",
							Tag = "GUI_SCREEN_PAUSE_QUIT_DONT_SAVE",
							Command = $"{Engine_PauseMenu.SavingCommandPrefix}2",
						},
					},
				},
				Tab("Map", "GUI_SCREEN_PAUSE_WORLD_MAP", Engine_PauseMenu.TabMap),
				Tab("Abilities", "GUI_SCREEN_PAUSE_ABILITIES", Engine_PauseMenu.TabAbilities),
				Tab("Items", "GUI_SCREEN_PAUSE_ITEMS", Engine_PauseMenu.TabItems),
				Tab("Weapons", "GUI_SCREEN_PAUSE_WEAPONS", Engine_PauseMenu.TabWeapons),
				Tab("Clothing", "GUI_SCREEN_PAUSE_CLOTHING", Engine_PauseMenu.TabClothing),
				Tab("Logbook", "GUI_SCREEN_PAUSE_LOGBOOK", Engine_PauseMenu.TabLogbook),
				Tab("Options", "GUI_SCREEN_PAUSE_OPTIONS", Engine_PauseMenu.TabOptions),
			},
		};

		// The front-end root — `sub_826E2220`'s spawn order. "New Game" OPENS the gender card
		// rather than dispatching (slide 1 on `choosecard`, substate 2; activating by name would
		// consume the boy/girl answer unasked). Subtitles expands in place. Availability (Load
		// Game needs a save, Language two installed) stays the guest's spawn condition.
		_menus!["frontend-root"] = new Engine_UiMenuTemplate
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
				new Engine_UiMenuEntry
				{
					Name = "Downloadable Content", Tag = "GUI_DOWNLOADABLE_CONTENT",
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

		AddScreen(new Engine_UiScreenTemplate
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
			BackdropUnit = "BackgroundTint",
			LiveTexts =
			{
				"Contents.CurrentQuest", "Contents.Time", "Contents.Renown",
				"experienceorbs.Strength.valueText", "experienceorbs.Skill.valueText",
				"experienceorbs.Will.valueText", "experienceorbs.General.valueText",
				"Legend.CurrentObjective",
				"Legend.Fixed.KeyName1", "Legend.Fixed.KeyName2", "Legend.Fixed.KeyName3",
				"Legend.Fixed.KeyName4", "Legend.Fixed.KeyName5", "Legend.Fixed.KeyName6",
				"Legend.Fixed.KeyName7", "Legend.Fixed.KeyName8", "Legend.Fixed.KeyName9",
				"Money.Text", "Money.Back.Select",
			},
		});

		// The ABILITIES panel. `AbilityCost` carries the parameterised
		// `GUI_SCREEN_ABLITY_EXPERIENCE_COST` — the typo is the shipped binary's, not ours.
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 14, Name = "abilities", OwnsList = true,
			Presentation = "abilitiesscreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`. The assignment
			// blade too: `SpellAssigning` sits on the book's fourth page and
			// Engine_UiSpellAssign draws it, so leaving it in the book would double it.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight", "SpellAssigning" },
			PageRootUnit = "Pages", BackdropUnit = "background_red",
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
			AddScreen(new Engine_UiScreenTemplate
			{
				ScreenId = id, OwnsList = true,
				Name = id switch
				{
					15 => "items", 16 => "clothing", 25 => "gift",
					26 => "upgradefurniture", _ => "decoration",
				},
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

				// The backdrop is a LEAF name, never "Background" — that name has 27 bearers,
				// every earlier one a menu row's icon plate. UpgradeFurniture is grey where the
				// gift and equipment books are green, so this is per-id.
				BackdropUnit = id == 26 ? "background_grey" : "background_green",
				PanelFields = new List<Engine_UiScreenPanel.Field>(itemFields),
				PreviewAnchor = "Foreground.Book.Pages.Page1.Contents.ModelHider.model_proxy.model",
			});
		}

		// The logbook names its pages by SUBJECT, not `Page1`/`Page2` —
		// `Foreground.Book.Pages.<Subject>Page` — so a `PageN` path resolves to nothing here.
		// Eight pages, 280 text elements between them, hence swept rather than listed. Subject
		// order is the guest's EventType mapping: 60/64 morality, 61 expressions, 62 completed
		// quests, 65 renown, 66 attractiveness, 67 stats, 68 dog, 69 help.
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 17, Name = "logbook", OwnsList = true,
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

		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 18, Name = "options", OwnsList = true,
			Presentation = "optionsscreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_purple",
			PageElement = "Foreground.ExpandableMenu",
			PageSlide = 2,
		});

		// The CRIME panel — three pages of one multi-line text field each: fine plus charge
		// sheet, charge sheet alone, community-service text.
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 21, Name = "crime", OwnsList = true,
			Presentation = "crimescreen",
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_grey",
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 22, Name = "shop", OwnsList = true,
			Presentation = "shopscreen",

			// The shop hoists its page paper OUT of the pages: `FrontPage`, `PageTextureFar` and
			// `PageTextureNear` are siblings of `Book`, not children of a page.
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_green",
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 23, Name = "worldmap", OwnsList = true,
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 28, Name = "realestate", Presentation = "realestatescreen",
			OwnsList = true,
			ChromeUnits = { "Foreground" },

			// Godot owns the row list and its cursor; the rest of the assembly is guest art,
			// riding in on the guest's stated position for `ExpandableMenu`.
			OwnedElsewhere = { "ExpandableMenu.Buttons", "MenuHighlight" },
			PageRootUnit = "Pages", BackdropUnit = "background_grey",
		});

		// 1 PHOTO MODE — one dialog box, not a book.
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 1, Name = "photomode", Presentation = "photomodescreen",
			ChromeUnits = { "PhotoMode" },
		});

		// 2 THE SPELL WHEEL. A ring of slots rather than a page — `MenuItemTemplate` is the slot
		// the guest clones per spell, and `CurrentItemLight` is the selection glow. No backdrop:
		// the wheel stands over the world.
		AddScreen(new Engine_UiScreenTemplate
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 24, Name = "coop", Presentation = "coopscreen",
			ChromeUnits =
			{
				"LeftPage", "RightPage", "LeftFrame2", "RightFrame2",
				"LeftBackground", "RightBackground", "Connecting", "menu", "loadgame",
			},
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 5, Name = "questrepeatable", Presentation = "questrepeatable",
			ChromeUnits = { "PaperBackground", "Buttons", "Stars", "DetailsGroup" },
			PanelSubtrees = { "elements" },
		});

		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 11, Name = "minigamecompleted", Presentation = "minigamecompleted",

			// `Select` has two bearers — the button GROUP is 0, its label Text is 1 — so the
			// bare name is right here and the label comes through the text sweep.
			ChromeUnits = { "paperbox", "Select", "Motif", "Amounts" },
			PanelSubtrees = { "Component" },
		});

		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 12, Name = "questcompletion", Presentation = "questcompletion",

			// `Component` is the paperbox; `frame2` is a SECOND, hand-authored ten-piece frame
			// whose parts collide by name with the paperbox's, which is why the units are named
			// at the top level and never by their pieces.
			ChromeUnits =
			{
				"Component", "frame2", "leaf", "Bullet1", "Bullet2", "Buttons", "Image",
			},
			PanelSubtrees = { "elements" },
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 10, Name = "query", Presentation = "queryscreen",
			ChromeUnits = { "elements" },
			PanelSubtrees = { "elements" },
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
		AddScreen(new Engine_UiScreenTemplate
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

		AddScreen(new Engine_UiScreenTemplate
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
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 1012, Name = "backdrop", Presentation = "scrollingbackground",
			ChromeUnits = { "Scrolling" },
			PageRootUnit = "Scrolling",

			// The scroll is the guest's own statement: it moves `Group` leftwards and the clone
			// binding writes every stated transform, so the wrap copy rides in with it. A host
			// rate would be a second clock against the one the title's music and fade run on.
		});

		// Screen 8 — the receive-items card, owned elsewhere; recorded for its preview anchor.
		AddScreen(new Engine_UiScreenTemplate
		{
			ScreenId = 8, Name = "receiveitems", Presentation = "receiveitemsscreen",
			PreviewAnchor = "Foreground.SpinningCube.Cube",
		});
	}

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
	private static Engine_UiMenuEntry Tab(string name, string tag, uint tab) => new()
	{
		Name = name,
		Tag = tag,
		Command = $"{Engine_PauseMenu.TabCommandPrefix}{tab}",
		Source = "spawn",
	};

	private static void AddScreen(Engine_UiScreenTemplate template) =>
		_screens![template.ScreenId] = template;

	// -----------------------------------------------------------------------------------
	// JSON — the editor's and a mod's way in.
	// -----------------------------------------------------------------------------------

	/// <summary>
	/// Applies records from a file over the built-ins, whole-record: a screen or menu in the
	/// file replaces the default entirely, which is the only merge a person can predict.
	/// </summary>
	private static int ApplyOverrides(string path)
	{
		if (!FileAccess.FileExists(path))
		{
			return 0;
		}

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
		if (file is null)
		{
			return 0;
		}

		var parsed = Json.ParseString(file.GetAsText());
		if (parsed.VariantType != Variant.Type.Dictionary)
		{
			GD.PushWarning($"[ui-templates] '{path}' is not a JSON object; ignored");
			return 0;
		}

		int taken = 0;
		var root = parsed.AsGodotDictionary();
		if (root.TryGetValue("menus", out Variant menus) &&
			menus.VariantType == Variant.Type.Dictionary)
		{
			foreach (var entry in menus.AsGodotDictionary())
			{
				if (ParseMenu(entry.Key.AsString(), entry.Value) is { } menu)
				{
					_menus![menu.Name] = menu;
					++taken;
				}
			}
		}

		if (root.TryGetValue("screens", out Variant screens) &&
			screens.VariantType == Variant.Type.Dictionary)
		{
			foreach (var entry in screens.AsGodotDictionary())
			{
				if (uint.TryParse(entry.Key.AsString(), out uint id) &&
					ParseScreen(id, entry.Value) is { } screen)
				{
					_screens![id] = screen;
					++taken;
				}
			}
		}

		return taken;
	}

	private static Engine_UiMenuTemplate? ParseMenu(string name, Variant value)
	{
		if (value.VariantType != Variant.Type.Dictionary)
		{
			return null;
		}

		var record = value.AsGodotDictionary();
		var menu = new Engine_UiMenuTemplate
		{
			Name = name,
			Highlight = record.TryGetValue("highlight", out Variant highlight)
				? highlight.AsString()
				: string.Empty,
			Title = record.TryGetValue("title", out Variant title)
				? title.AsString()
				: string.Empty,
		};

		if (record.TryGetValue("entries", out Variant entries) &&
			entries.VariantType == Variant.Type.Array)
		{
			foreach (Variant entry in entries.AsGodotArray())
			{
				if (ParseEntry(entry) is { } parsed)
				{
					menu.Entries.Add(parsed);
				}
			}
		}

		return menu;
	}

	private static Engine_UiMenuEntry? ParseEntry(Variant value)
	{
		if (value.VariantType != Variant.Type.Dictionary)
		{
			return null;
		}

		var record = value.AsGodotDictionary();
		var entry = new Engine_UiMenuEntry
		{
			Name = record.TryGetValue("name", out Variant name) ? name.AsString() : string.Empty,
			Tag = record.TryGetValue("tag", out Variant tag) ? tag.AsString() : string.Empty,
			Command = record.TryGetValue("command", out Variant command)
				? command.AsString()
				: null,
			Opens = record.TryGetValue("opens", out Variant opens) ? opens.AsString() : null,
			HostOnly = record.TryGetValue("hostOnly", out Variant host) && host.AsBool(),
			Source = record.TryGetValue("source", out Variant source)
				? source.AsString()
				: string.Empty,
			Icon = record.TryGetValue("icon", out Variant icon) ? icon.AsString() : string.Empty,
			EventType = record.TryGetValue("eventType", out Variant type) ? type.AsInt32() : 0,
			Rating = record.TryGetValue("rating", out Variant rating)
				? rating.AsInt32()
				: Engine_UiMenuConstants.EmptyRatingSlide,
		};

		if (entry.Name.Length == 0)
		{
			return null;
		}

		if (record.TryGetValue("children", out Variant children) &&
			children.VariantType == Variant.Type.Array)
		{
			foreach (Variant child in children.AsGodotArray())
			{
				if (ParseEntry(child) is { } parsed)
				{
					entry.Children.Add(parsed);
				}
			}
		}

		return entry;
	}

	private static Engine_UiScreenTemplate? ParseScreen(uint id, Variant value)
	{
		if (value.VariantType != Variant.Type.Dictionary)
		{
			return null;
		}

		var record = value.AsGodotDictionary();
		var screen = new Engine_UiScreenTemplate
		{
			ScreenId = id,
			Name = record.TryGetValue("name", out Variant name) ? name.AsString() : $"screen{id}",
			OwnsList = record.TryGetValue("ownsList", out Variant owns) && owns.AsBool(),
			PreviewAnchor = record.TryGetValue("previewAnchor", out Variant preview)
				? preview.AsString()
				: string.Empty,
			PageElement = record.TryGetValue("pageElement", out Variant page)
				? page.AsString()
				: string.Empty,
			PageSlide = record.TryGetValue("pageSlide", out Variant slide)
				? (int)slide.AsDouble()
				: 0,
			Menu = record.TryGetValue("menu", out Variant menu) ? menu.AsString() : string.Empty,
			Presentation = record.TryGetValue("presentation", out Variant file)
				? file.AsString()
				: string.Empty,
			PageRootUnit = record.TryGetValue("pageRootUnit", out Variant root)
				? root.AsString()
				: string.Empty,
			BackdropUnit = record.TryGetValue("backdropUnit", out Variant backdrop)
				? backdrop.AsString()
				: string.Empty,
		};

		Strings(record, "chrome", screen.ChromeUnits);
		Strings(record, "panelSubtrees", screen.PanelSubtrees);
		Strings(record, "ownedElsewhere", screen.OwnedElsewhere);
		Strings(record, "liveTexts", screen.LiveTexts);

		if (record.TryGetValue("panelFields", out Variant fields) &&
			fields.VariantType == Variant.Type.Array)
		{
			foreach (Variant field in fields.AsGodotArray())
			{
				if (field.VariantType != Variant.Type.Dictionary)
				{
					continue;
				}

				var fieldRecord = field.AsGodotDictionary();
				if (!fieldRecord.TryGetValue("path", out Variant path))
				{
					continue;
				}

				screen.PanelFields.Add(new Engine_UiScreenPanel.Field(
					path.AsString(),
					fieldRecord.TryGetValue("multiline", out Variant multi) && multi.AsBool(),
					fieldRecord.TryGetValue("alternate", out Variant alt) && alt.AsBool()));
			}
		}

		return screen;
	}

	private static void Strings(Godot.Collections.Dictionary record, string key, List<string> into)
	{
		if (!record.TryGetValue(key, out Variant value) ||
			value.VariantType != Variant.Type.Array)
		{
			return;
		}

		foreach (Variant entry in value.AsGodotArray())
		{
			string text = entry.AsString();
			if (text.Length != 0)
			{
				into.Add(text);
			}
		}
	}

	/// <summary>Writes every record as it runs — the editor's starting point and the override
	/// file's documentation.</summary>
	private static void SaveSnapshot(string path)
	{
		DirAccess.MakeDirRecursiveAbsolute("user://ui");

		var menus = new Godot.Collections.Dictionary();
		foreach ((string name, Engine_UiMenuTemplate menu) in _menus!)
		{
			var entries = new Godot.Collections.Array();
			foreach (Engine_UiMenuEntry entry in menu.Entries)
			{
				entries.Add(EntryJson(entry));
			}

			menus[name] = new Godot.Collections.Dictionary
			{
				["highlight"] = menu.Highlight,
				["title"] = menu.Title,
				["entries"] = entries,
			};
		}

		var screens = new Godot.Collections.Dictionary();
		foreach ((uint id, Engine_UiScreenTemplate screen) in _screens!)
		{
			var fields = new Godot.Collections.Array();
			foreach (Engine_UiScreenPanel.Field field in screen.PanelFields)
			{
				fields.Add(new Godot.Collections.Dictionary
				{
					["path"] = field.Path,
					["multiline"] = field.Multiline,
					["alternate"] = field.Alternate,
				});
			}

			screens[id.ToString()] = new Godot.Collections.Dictionary
			{
				["name"] = screen.Name,
				["ownsList"] = screen.OwnsList,
				["previewAnchor"] = screen.PreviewAnchor,
				["pageElement"] = screen.PageElement,
				["pageSlide"] = screen.PageSlide,
				["menu"] = screen.Menu,
				["presentation"] = screen.Presentation,
				["pageRootUnit"] = screen.PageRootUnit,
				["backdropUnit"] = screen.BackdropUnit,
				["chrome"] = ToArray(screen.ChromeUnits),
				["panelSubtrees"] = ToArray(screen.PanelSubtrees),
				["ownedElsewhere"] = ToArray(screen.OwnedElsewhere),
				["liveTexts"] = ToArray(screen.LiveTexts),
				["panelFields"] = fields,
			};
		}

		var root = new Godot.Collections.Dictionary
		{
			["menus"] = menus,
			["screens"] = screens,
		};

		using FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write);
		file?.StoreString(Json.Stringify(root, "\t"));
	}

	private static Godot.Collections.Dictionary EntryJson(Engine_UiMenuEntry entry)
	{
		var record = new Godot.Collections.Dictionary
		{
			["name"] = entry.Name,
			["tag"] = entry.Tag,
		};

		if (entry.Command is not null)
		{
			record["command"] = entry.Command;
		}

		if (entry.Opens is not null)
		{
			record["opens"] = entry.Opens;
		}

		if (entry.Source.Length != 0)
		{
			record["source"] = entry.Source;
		}

		if (entry.HostOnly)
		{
			record["hostOnly"] = true;
		}

		// The three an author sets: picture, press meaning, stars. Written only when non-default.
		if (entry.Icon.Length != 0)
		{
			record["icon"] = entry.Icon;
		}

		if (entry.EventType != 0)
		{
			record["eventType"] = entry.EventType;
		}

		if (entry.Rating != Engine_UiMenuConstants.EmptyRatingSlide)
		{
			record["rating"] = entry.Rating;
		}

		if (entry.Children.Count != 0)
		{
			var children = new Godot.Collections.Array();
			foreach (Engine_UiMenuEntry child in entry.Children)
			{
				children.Add(EntryJson(child));
			}

			record["children"] = children;
		}

		return record;
	}

	private static Godot.Collections.Array ToArray(List<string> values)
	{
		var array = new Godot.Collections.Array();
		foreach (string value in values)
		{
			array.Add(value);
		}

		return array;
	}
}
