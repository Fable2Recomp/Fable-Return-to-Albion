using Godot;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The list on an in-game screen — abilities, items, weapons, clothing, logbook, options, crime,
/// shop, world map, gift, selection, decoration — read out of the guest and handed to the view the
/// front end and pause menu draw with. One model for all eleven: they share the
/// <c>ExpandableMenu/Buttons</c> pool, the <c>expandablemenuformatting.lua</c> slot table and the
/// <c>sub_826D58F0</c>/<c>sub_826D5AF0</c> commit pair (contract in
/// <see cref="Engine_ExpandableMenuItems"/>).
///
/// The guest owns the row CONTENTS and the HIGHLIGHT: a commit reads Lua's
/// <c>g_Menu.CurrentHighlightIdx</c>, never a C++ index (<c>sub_826C1B50</c> runs
/// <c>"g_Menu:GetHighlightedMenuItemData()"</c>; screen 27's A press is gated on it not being the
/// <c>0x0FFFFFFF</c> "nothing selected" sentinel). A move is made on the guest's own stick router
/// (<c>sub_82404ED0</c> / <c>sub_82405030</c>) so the shipped repeat cadence comes with it. The
/// host owns LAYOUT and ART: the row components are hidden and
/// <see cref="Engine_FrontEndMenuView"/> draws over them.
///
/// The highlight is solved, not assumed: <c>onAddChild</c> writes
/// <c>VisibleIndex = HIGHLIGHT_INDEX + DisplayIndex - CurrentHighlightIdx</c> and the formatter
/// turns that into the slot's authored y, so any laid-out row solves back for the highlight. Rows
/// that disagree mean a sample taken mid-slide — keep the previous answer.
/// </summary>
public sealed class Engine_UiScreenMenu
{
	/// <summary>The model, ready for <see cref="Engine_FrontEndMenuView.Bind"/>.</summary>
	public Engine_FrontEndMenu Menu { get; } = new();

	/// <summary>The shipped model, ported — see <see cref="Engine_UiExpandableMenu"/>. The same
	/// object the view draws, never a second one beside it.</summary>
	public Engine_UiExpandableMenu Script => Menu.Model;

	/// <summary>The guest's own <c>CurrentHighlightIdx</c>, 1-based. Zero until solved.</summary>
	public int GuestHighlight { get; private set; }

	/// <summary>How many rows the guest has spawned into the pool.</summary>
	public int Count => Menu.Visible.Count;


	private readonly List<string> _labels = new();
	private readonly List<int> _ordinals = new();

	/// <summary>Each row's stated indent, kept for the report — see <see cref="Describe"/>.</summary>
	private readonly List<float?> _indents = new();

	/// <summary>
	/// How far a laid-out row's x may sit from its slot's authored x and still be on the page the
	/// player is looking at. The pool holds more than one list — the gift screen keeps its category
	/// list and the open category's item list in the same 26 buttons, separated in x by about a
	/// page width — and reading both as one makes every row disagree about the highlight.
	/// <c>GetMenuItemTabOffset</c> is <c>-65 - 5*(depth-1)</c> plus 24 for a heading, so a live row
	/// sits 41..90 units left of its slot; widened to 120, since the bound that matters is the
	/// page displacement rather than the exact depth.
	/// </summary>
	private const float PageBand = 120f;

	/// <summary>
	/// The `MenuPopulationScript` the screen's widget names — see
	/// <see cref="Engine_ExpandableMenuItems.PopulationScriptOf"/>. Empty when it names none.
	/// </summary>
	public string PopulationScript { get; private set; } = string.Empty;

	/// <summary>
	/// Which in-game screen this list belongs to, set by the caller from the screen manager's own
	/// presentation-to-id map. 0 when it is not one of the manager's screens.
	///
	/// Only used to find a template for a widget that names no population script — see
	/// <see cref="Engine_UiScreenTemplate.MenuScript"/>.
	/// </summary>
	public uint ScreenId { get; set; }

	/// <summary>
	/// <see cref="ScreenId"/> as a capture key: 0 means "not one of the manager's screens" here,
	/// so it becomes <see cref="Engine_UiMenuSources.AnyScreen"/> rather than the pause root,
	/// which really is screen 0. A list with no screen takes the capture as it always did.
	/// </summary>
	private uint CaptureKey => ScreenId != 0 ? ScreenId : Engine_UiMenuSources.AnyScreen;

	/// <summary>The presentation this list's Lua lives in — one `lua_State` per `.bgf`.</summary>
	public uint Presentation { get; private set; }

	/// <summary>True while the model is the guest's STATED one rather than a position solve.</summary>
	public bool Stated => _stated;

	/// <summary>
	/// The menu model built from the C++'s own statements — rows, tree, ghosting and emphasis from
	/// the 28 `g_Menu:` statements. When it carries rows for this presentation it wins and the Lua
	/// VM is not read; null is legal and falls back to the position solve.
	/// </summary>
	public Engine_UiMenuModel? Model { get; set; }

	/// <summary>
	/// The string table, for rows the guest is not currently drawing: a folded row has no pool
	/// button, so `ComponentIdx` is -1 and the tree carries only a Babel tag
	/// (`INV_ITEM_SILVER_KEY_NAME`). Pool text wins where it exists — it carries the decorations
	/// the population appended.
	/// </summary>
	public Refii.Text.Engine_BabelDatabase? Strings;

	/// <summary>
	/// Our fold, keyed by PATH — a name is only unique among its siblings. The guest's own
	/// `SelectMenuItem` is unreachable: every host Lua chunk goes through `sub_82426470`, which
	/// defers rather than runs once `guiObject+0x64` is cleared (the GUI constructor sets it, the
	/// tail of the frame update clears it and nothing sets it back).
	/// </summary>
	private readonly System.Collections.Generic.HashSet<string> _open =
		new(System.StringComparer.Ordinal);

	/// <summary>Bumped whenever the fold changes, so the signature test rebuilds.</summary>
	private int _foldGeneration;

	private bool _stated;

	private bool _saidModel;
	private bool _saidGeometry;

	/// <summary>What the last stated read produced, so an unchanged model costs no rebuild.</summary>
	private readonly List<string> _signature = new();

	/// <summary>
	/// What the last stated rebuild was handed, so a fold can be followed per frame against text
	/// at worst one sample old (the label does not change when a folder opens).
	/// </summary>
	private IReadOnlyList<Engine_ExpandableMenuItems.Button>? _lastButtons;

	private Engine_UiScreen? _lastScreen;

	private IReadOnlyList<bool>? _lastActive;

	/// <summary>
	/// How many rows the guest said were showing when the model was last built from it. The
	/// trigger is a change in this, not a disagreement with our own visible count.
	/// </summary>
	private int _statedVisible = -1;

	/// <summary>
	/// Whether a template's root source answers anything for THIS screen — the same read
	/// `BuildInto` makes, bound to the same screen, without building. See the dry-template
	/// note in <see cref="Rebuild"/>.
	/// </summary>
	private bool SourceHasRows(Engine_UiMenuTemplate template)
	{
		uint boundWas = Engine_UiMenuSources.BoundScreen;
		Engine_UiMenuSources.BoundScreen = CaptureKey;
		try
		{
			return Engine_UiMenuSources.Rows(template.Source, template.Name).Count != 0;
		}
		finally
		{
			Engine_UiMenuSources.BoundScreen = boundWas;
		}
	}

	/// <summary>
	/// Builds the tree from the C++'s own statements. True when anything changed.
	/// </summary>
	/// <remarks>
	/// The parent is STATED: each row carries `String4`, the parent's name, so nothing depends on
	/// display order or on the list being laid out yet. Rows can arrive before their parents
	/// (spawn order is population order) and `Add` fixes the parent at construction, so rows go in
	/// waves until nothing new resolves; whatever still names an unspawned parent goes to the root.
	///
	/// Ghosting is the two-term rule via <see cref="Engine_UiMenuModel.EffectivelyGhosted"/> — the
	/// explicit flag OR an empty submenu while automatic submenu ghosting is on. The pool ordinal
	/// is left −1: it exists only for the position solve, and this path owns the cursor.
	/// </remarks>
	/// <summary>
	/// Builds the list from its POPULATION TEMPLATE and joins the guest's live state onto it.
	/// True when anything changed.
	/// </summary>
	/// <remarks>
	/// The row set, its order and its nesting are the template's; nothing here reads a live
	/// position or a pool button to decide WHAT the list contains. What the guest still supplies
	/// is what only it knows: the decorated label (`Abilities &lt;s4&gt;(2)&lt;/s4&gt;` when
	/// there are points to spend, `&lt;exclamation.img&gt;` when something inside is new) and the
	/// ghosting. Both are joined by <see cref="Engine_PauseMenu.ApplyStatedLabels"/>, which is
	/// the same authored-list-against-the-guest's-pool join the pause menu has always used —
	/// name first, then position, and an empty read treated as no news rather than no rows.
	///
	/// A row here IS committable: see <see cref="Engine_UiMenuTemplate.BuildInto"/>.
	/// </remarks>
	/// <summary>
	/// Shows the list the dye flow calls for, NOW: the dye list while a dye is held, the
	/// screen's own list once it is put away. The sampled rebuild runs when the guest's screen
	/// changes, and choosing a dye changes nothing of the guest's.
	/// </summary>
	public bool FollowDyeFlow()
	{
		string script = Engine_UiDyeFlow.Active ? Engine_UiDyeFlow.MenuScript : _boundScript;
		if (script.Length == 0 || Engine_UiTemplates.Menu(script) is not { } template)
		{
			return false;
		}

		PopulationScript = script;
		bool changed = RebuildFromTemplate(
			template, _lastButtons ?? System.Array.Empty<Engine_ExpandableMenuItems.Button>());
		GD.Print($"[screen-menu] the dye flow shows '{script}' — {Script.Visible.Count} row(s)");
		return changed;
	}

	private bool RebuildFromTemplate(
		Engine_UiMenuTemplate template,
		IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons)
	{
		// WHOSE POPULATION THE CAPTURE HOLDS. `capture:Root` asks for every row spawned under
		// `Root` and the capture is one list, so without this a screen took whatever was
		// populated last — the front end's rows once came up on the real-estate screen. Set for
		// the whole build so every source this template resolves is answered for THIS screen.
		uint boundWas = Engine_UiMenuSources.BoundScreen;
		Engine_UiMenuSources.BoundScreen = CaptureKey;
		try
		{
			return BuildBound(template, buttons);
		}
		finally
		{
			Engine_UiMenuSources.BoundScreen = boundWas;
		}
	}

	private bool BuildBound(
		Engine_UiMenuTemplate template,
		IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons)
	{
		// Built once and then only re-joined: the row set cannot change, so rebuilding it every
		// sample would throw the fold and the cursor away for nothing.
		bool built = false;
		if (_authoredFrom != template)
		{
			// THE FOLD AND THE CURSOR SURVIVE A CHANGE OF SOURCE. A list first built from the
			// g_Menu model (its id not yet known) is rebuilt from the template the moment the
			// id lands; the rows are the same rows under the same names, so what the player
			// had open and highlighted is carried across by name — the weapons book was
			// snapping shut on that rebuild.
			var open = new HashSet<string>(System.StringComparer.Ordinal);
			foreach (Engine_UiExpandableMenu.Item was in Script.SpawnedItems)
			{
				if (was.IsOpen && was.Children.Count > 0 && was.Name.Length != 0)
				{
					open.Add(was.Name);
				}
			}

			string? highlighted = Script.Highlighted?.Name;

			_authoredFrom = template;
			template.BuildInto(Menu, committable: true);
			PruneAuthoredDrops();
			Script.UpdateAfterSpawning();
			Script.UpdateAllGhosting();

			if (open.Count != 0)
			{
				foreach (Engine_UiExpandableMenu.Item now in Script.SpawnedItems)
				{
					if (now.Children.Count > 0 && open.Contains(now.Name))
					{
						now.IsOpen = true;
					}
				}

				Script.UpdateAfterSpawning();
			}

			if (!string.IsNullOrEmpty(highlighted))
			{
				IReadOnlyList<Engine_UiExpandableMenu.Item> visible = Script.Visible;
				for (int i = 0; i < visible.Count; ++i)
				{
					if (string.Equals(visible[i].Name, highlighted, System.StringComparison.Ordinal))
					{
						Script.Highlight(i + 1);
						break;
					}
				}
			}

			built = true;
		}
		else if (template.RefreshSources(Menu))
		{
			// A sourced row set that has changed — the guest's data arriving, or changing under
			// us. Authored rows are untouched, so the fold and the cursor survive.
			PruneAuthoredDrops();
			Script.UpdateAfterSpawning();
			Script.UpdateAllGhosting();
			built = true;
		}

		// The guest's own decorated labels and ghosting over our rows. The stated rows are the
		// spawn capture where there is one; with none, the join falls back to the pool by
		// position, which is exactly what the pause menu does before its Lua is up.
		IReadOnlyList<Engine_UiMenuModel.Row> stated =
			Model?.For(Presentation)?.Rows ?? NoStatedRows;

		// BY NAME ONLY. The positional fallback exists for the pause list, whose rows match the
		// guest's one for one; a template list need not. The logbook drops the guest's dead
		// `Achievements` row, so ours is four against the guest's five — and the fallback then
		// handed our fourth row (Help) the guest's fourth label, putting "Achievements" back on
		// screen with Help's identity underneath it. A template row carries a real name and its
		// own Babel tag, so an unmatched row has a correct label already.
		bool moved = Engine_PauseMenu.ApplyStatedLabels(
			Menu, buttons, stated, positionalFallback: false);

		// THE CATEGORY COUNTS ARE OURS NOW. `sub_826CF098` decorates a category header with
		// `" (" .. count .. ")"` when the count is non-zero, and the guest's own labels carried
		// that — `Headgear (6)`. Those labels reach us only through the spawn capture, and the
		// capture files its statements under the presentation the CHUNK carried, which is not
		// always the screen's: the equipment screen's rows were nowhere in the model, so the
		// name join found nothing and the headers read as bare `Hats`.
		//
		// A list built from the data areas does not need to be told: the count IS the number of
		// children we just sourced. So the decoration is applied here, by the guest's rule, and
		// the join is left to supply only what we genuinely cannot compute.
		foreach (Engine_UiExpandableMenu.Item row in Script.SpawnedItems)
		{
			if (row.EventType != (int)Engine_UiMenuConstants.EventType.Submenu ||
				row.TextTag is not { Length: > 0 } tag || Strings is null)
			{
				continue;
			}

			// A category the template says carries no count — the clothing screen's `Outfits`,
			// whose header the guest spawns through the countless `sub_826CF020`.
			if (template.Entry(row.Name) is { Counted: false })
			{
				continue;
			}

			// `Counted`, not merely `Sourced`: the guest's number comes from a separate array
			// that walks the INVENTORY, so a row standing for no inventory record — the clothing
			// screen's `None` — is not in it. Counting those put every equipment header one
			// above the guest's own, which is how the discrepancy was finally pinned.
			int sourcedChildren = 0;
			bool anyUnviewed = false;
			foreach (Engine_UiExpandableMenu.Item child in row.Children)
			{
				if (!child.Sourced || !child.Counted)
				{
					continue;
				}

				++sourcedChildren;
				anyUnviewed |= child.Unviewed;
			}

			if (!Strings.TryGetString(tag, out string caption) || caption.Length == 0)
			{
				continue;
			}

			// A count of zero is the BARE caption (`sub_829A52F8`: `if (a2 <= 0)` copies the
			// caption alone), written rather than skipped: skipping left the spawn-time label up,
			// so buying the last upgradeable ability kept "Will (1)" on the folder.
			// The second suffix, `sub_826CF098`'s `word_820AEFF0` — appended when the count
			// pair's `[1]` is above zero, and that is the number of unviewed items.
			string decorated = sourcedChildren == 0
				? caption
				: $"{caption} ({sourcedChildren})" +
					(anyUnviewed ? "<exclamation.img>" : string.Empty);
			if (!string.Equals(row.StatedLabel, decorated, System.StringComparison.Ordinal))
			{
				row.StatedLabel = decorated;
				moved = true;
			}
		}

		// THE GUEST'S IDENTITY FOR A DATA-AREA ROW, by name — A BRIDGE, NOT THE DESTINATION.
		//
		// A row built from the data areas has no `(EventType, id)` of its own, and the guest's id
		// is a per-population TICKET (`sub_82880AC8` hands out a counter and resets it on every
		// repopulate), so it cannot be derived — only borrowed. Our row and the guest's carry the
		// SAME NAME, because both are the item's `NameTag`, which is what makes the borrow safe.
		//
		// This exists so the lists stay usable while the host actions that replace it are built.
		// `Engine_UiMenuEntry.Command` is checked BEFORE any identity commit, so a row that gains
		// a real host action stops using this without anything here changing.
		if (stated.Count != 0)
		{
			foreach (Engine_UiMenuModel.Row row in stated)
			{
				// Re-borrowed whenever the guest's differs: the ticket is per POPULATION, so one
				// latched at the first borrow is dead after the guest repopulates.
				if (row.Name.Length == 0 || Menu.Find(row.Name) is not { Sourced: true } sourced ||
					(sourced.Committable && !sourced.BorrowedIdentity))
				{
					continue;
				}

				int low = (int)(row.Id & 0xFFFFu);
				int high = (int)((row.Id >> 16) & 0xFFFFu);
				if (sourced.BorrowedIdentity && sourced.EventType == row.EventType &&
					sourced.IdLow == low && sourced.IdHigh == high)
				{
					continue;
				}

				sourced.EventType = row.EventType;
				sourced.IdLow = low;
				sourced.IdHigh = high;
				sourced.Committable = true;
				sourced.BorrowedIdentity = true;
				moved = true;
			}
		}

		// THE GUEST'S EXPLICIT GHOSTING, by name — its only statement that a row is unavailable
		// right now (the childhood restrictions on the pause tabs, an empty category, a shop
		// item you cannot afford). Applied to `Ghosted`, never to `AutoGhosted`: the two are
		// separate channels and a recompute owns the second.
		if (Model?.For(Presentation)?.Ghosting is { Count: > 0 } ghosting)
		{
			foreach ((string named, bool ghost) in ghosting)
			{
				if (Menu.Find(named) is { } row && row.Ghosted != ghost)
				{
					row.Ghosted = ghost;
					moved = true;
				}
			}
		}

		if (!built && !moved)
		{
			return false;
		}

		_labels.Clear();
		_ordinals.Clear();
		_indents.Clear();
		foreach (Engine_UiExpandableMenu.Item shown in Script.Visible)
		{
			_labels.Add(shown.StatedLabel ?? shown.Name);
			_ordinals.Add(shown.Ordinal);
			_indents.Add(null);
		}

		Script.Highlight(System.Math.Clamp(Script.CurrentHighlightIdx, 1,
			System.Math.Max(1, Script.Visible.Count)));
		return true;
	}

	private static readonly List<Engine_UiMenuModel.Row> NoStatedRows = new();

	/// <summary>The template this list was built from, so it is built once and then only joined.</summary>
	private Engine_UiMenuTemplate? _authoredFrom;

	/// <summary>True while the row set is the TEMPLATE's rather than a read of the guest.</summary>
	private bool _authored;

	private bool _saidAuthored;

	private bool _saidTemplateDry;

	private bool _saidStatedScript;

	private string _boundScript = string.Empty;

	private int _saidMode = -1;

	private uint _saidUnbound = uint.MaxValue;

	/// <summary>The last orphan report, so it prints on change rather than per rebuild.</summary>
	private string? _orphanReported;

	private bool RebuildFromModel(Engine_UiMenuModel.Menu menu, Engine_UiScreen? screen)
	{
		var signature = new System.Collections.Generic.List<string>(menu.Rows.Count);
		foreach (Engine_UiMenuModel.Row row in menu.Rows)
		{
			signature.Add($"{row.Name}|{row.Parent}|{row.Label}|{row.Ghosted}|" +
				$"{(int)row.Emphasis}|{_foldGeneration}");
		}

		if (Same(signature, _signature))
		{
			return false;
		}

		_signature.Clear();
		_signature.AddRange(signature);

		Menu.Clear();
		_hostChildren = 0;

		var byName = new System.Collections.Generic.Dictionary<
			string, Engine_UiExpandableMenu.Item>(System.StringComparer.Ordinal);
		var pending = new System.Collections.Generic.List<Engine_UiMenuModel.Row>(menu.Rows);
		var placed = new System.Collections.Generic.List<
			(Engine_UiMenuModel.Row Row, Engine_UiExpandableMenu.Item Item)>(menu.Rows.Count);

		bool IsRoot(Engine_UiMenuModel.Row row) =>
			row.Parent.Length == 0 ||
			string.Equals(row.Parent, "Root", System.StringComparison.Ordinal);

		while (pending.Count != 0)
		{
			var next = new System.Collections.Generic.List<Engine_UiMenuModel.Row>();
			int placedThisWave = 0;

			foreach (Engine_UiMenuModel.Row row in pending)
			{
				Engine_UiExpandableMenu.Item? parent = null;
				if (!IsRoot(row) && !byName.TryGetValue(row.Parent, out parent))
				{
					next.Add(row);
					continue;
				}

				Engine_UiExpandableMenu.Item item = Script.Add(row.Name, parent);
				byName[row.Name] = item;
				placed.Add((row, item));
				++placedThisWave;
			}

			if (placedThisWave == 0)
			{
				// ORPHANS ARE DROPPED, NOT ROOTED — the shipped Lua's own behaviour.
				//
				// `g_Menu:AddChild(item, parent)` (expandablemenu main.6) looks the parent up by
				// name and, when `GetMenuItem` returns nothing, prints
				// "Somethings broken with GetMenuItem...!" and returns WITHOUT linking: the item
				// keeps no `ParentItem` and is in nobody's `ChildItems`, so it is in
				// `SpawnedItems` and in no visible list. Only a nil parent becomes ROOTNAME
				// (pc13-14) — a NAMED parent that does not resolve is not a root row.
				//
				// Rooting them instead put 32 outfit rows on the gift screen beside its nine
				// categories: the guest spawns them naming `Suits`, a category that screen never
				// spawns a header for, and the original therefore never shows them.
				var orphaned = new Dictionary<string, int>(System.StringComparer.Ordinal);
				foreach (Engine_UiMenuModel.Row row in next)
				{
					string named = row.Parent.Length == 0 ? "<none>" : row.Parent;
					orphaned[named] = orphaned.TryGetValue(named, out int seen) ? seen + 1 : 1;
				}

				if (orphaned.Count != 0)
				{
					var parts = new List<string>();
					foreach ((string named, int count) in orphaned)
					{
						parts.Add($"'{named}' x{count}");
					}

					parts.Sort();
					string line = $"{next.Count} row(s) name a parent that was never spawned: " +
						string.Join(", ", parts);
					if (line != _orphanReported)
					{
						_orphanReported = line;
						GD.Print($"[screen-menu] '{PopulationScript}' {line} — DROPPED, as " +
							"`AddChild` drops them");
					}
				}

				break;
			}

			pending = next;
		}

		foreach ((Engine_UiMenuModel.Row row, Engine_UiExpandableMenu.Item item) in placed)
		{
			// `String2` is the resolved, decorated string WHERE THE GUEST SUPPLIED ONE. It does
			// not always: the item lists spawn with `String1` set to the item's BABEL TAG and no
			// `String2` at all, leaving the Lua to resolve it — which is why the gift screen
			// showed 32 rows reading `INV_ITEM_CLOTHES_POOR_FEMALE_SUIT_NAME` verbatim. Leave
			// the tag on `TextTag` and no stated label, and the view resolves it through the
			// same book every other tag goes through (`INV_ITEM_CLOTHES_POOR_FEMALE_SUIT_NAME`
			// → 'Female Lower Class Outfit', checked against the shipped book.babel).
			if (row.Label.Length != 0)
			{
				item.StatedLabel = row.Label;
				item.TextTag = row.Label;
			}
			else
			{
				item.StatedLabel = null;
				item.TextTag = row.Name;
			}

			item.Ordinal = -1;
			item.HeadingOverride = null;
			item.EventType = row.EventType;
			item.IdLow = (int)(row.Id & 0xFFFFu);
			item.IdHigh = (int)((row.Id >> 16) & 0xFFFFu);
			item.Committable = true;
			item.RatingSlide = row.Rating;
			item.Ghosted = Engine_UiMenuModel.EffectivelyGhosted(menu, row);
			item.Emphasis = row.Emphasis;
			item.IsOpen = _open.Contains(Engine_UiScreenMenuSpawn.PathOf(item));
		}

		AttachSourcedFolders();
		PruneAuthoredDrops();

		Script.UpdateAfterSpawning();
		Script.UpdateAllGhosting();
		_labels.Clear();
		_ordinals.Clear();
		_indents.Clear();
		foreach (Engine_UiExpandableMenu.Item shown in Script.Visible)
		{
			_labels.Add(shown.StatedLabel ?? shown.Name);
			_ordinals.Add(shown.Ordinal);
			_indents.Add(null);
		}

		// The fold and the highlight are both ours here. `SetInitiallySelectedItem` is the one
		// cursor statement the C++ makes, and it applies on the first build only, as the guest's.
		if (menu.InitiallySelected >= 1 && GuestHighlight == 0)
		{
			GuestHighlight = menu.InitiallySelected;
			Script.Highlight(System.Math.Clamp(
				menu.InitiallySelected, 1, System.Math.Max(1, Script.Visible.Count)));
		}
		else
		{
			Script.Highlight(System.Math.Clamp(Script.CurrentHighlightIdx, 1,
				System.Math.Max(1, Script.Visible.Count)));
		}

		_statedVisible = Script.Visible.Count;
		return true;
	}

	public bool Rebuild(uint presentation, Engine_UiScreen? screen, IReadOnlyList<string?>? text,
		IReadOnlyList<bool>? active, IReadOnlyList<int>? slides,
		IReadOnlyList<float?[]?>? transform)
	{
		PopulationScript = Engine_ExpandableMenuItems.PopulationScriptOf(screen);

		// THE WIDGET DOES NOT ALWAYS SAY WHICH LIST IT IS. The gift family's states no
		// `MenuPopulationScript` attribute at all, which left it with no template to look up and
		// dropped it onto the raw model. The guest states it a second way — the argument of
		// `RepopulateMenuFromPopulationScript`, which the shipped Lua also writes onto the
		// element — so the captured statement stands in when the attribute is silent.
		if (PopulationScript.Length == 0 &&
			Model?.For(presentation)?.PopulationScript is { Length: > 0 } stated)
		{
			PopulationScript = stated;
			if (!_saidStatedScript)
			{
				_saidStatedScript = true;
				GD.Print($"[screen-menu] presentation 0x{presentation:X8} states no " +
					$"MenuPopulationScript attribute — taking '{stated}' from the guest's own " +
					"RepopulateMenuFromPopulationScript statement");
			}
		}

		// AND THE GIFT FAMILY STATES IT NEITHER WAY. `sub_82430468` spawns screen 15's rows from
		// native C++ and issues no `RepopulateMenuFromPopulationScript` at all, so the screen's
		// own template names the list instead.
		if (PopulationScript.Length == 0 && ScreenId != 0 &&
			Engine_UiTemplates.Screen(ScreenId) is { } screenTemplate)
		{
			// A SCREEN CAN BE SEVERAL LISTS. Screen 15 is one id over Items, Weapons and
			// Clothing, chosen by the mode word `sub_829B0A58` reads at `screen+372`; binding it
			// to a single script would put the item categories on the weapons list. The mode is
			// asked for only when the template says the screen has modes.
			string bound = screenTemplate.MenuScript;
			if (screenTemplate.MenuScriptByMode.Count != 0)
			{
				int mode = (int)Refii.Host.HostBridge.GetInventoryScreenMode();
				if (screenTemplate.MenuScriptByMode.TryGetValue(mode, out string? byMode))
				{
					bound = byMode;
				}
				else if (mode != _saidMode)
				{
					_saidMode = mode;
					GD.Print($"[screen-menu] screen {ScreenId} is in mode {mode}, which its " +
						$"template does not name a list for — falling back to '{bound}'");
				}
			}

			if (bound.Length > 0 && !string.Equals(bound, _boundScript, System.StringComparison.Ordinal))
			{
				_boundScript = bound;
				GD.Print($"[screen-menu] screen {ScreenId} names no population script — its " +
					$"template binds it to '{bound}'");
			}

			PopulationScript = bound;
		}

		// A HELD DYE TURNS THE ITEMS SCREEN INTO THE DYE LIST, and leaving that screen puts the
		// dye away — the guest's own state lives on the screen it leaves too.
		if (Engine_UiDyeFlow.Active)
		{
			if (ScreenId == Engine_UiDyeFlow.ItemsScreen)
			{
				PopulationScript = Engine_UiDyeFlow.MenuScript;
			}
			else if (ScreenId != 0)
			{
				Engine_UiDyeFlow.End();
			}
		}

		IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons =
			Engine_ExpandableMenuItems.Read(screen, text, active, slides);

		Presentation = presentation;

		// THE TEMPLATE FIRST, WHERE IT HAS ROWS. This list is Godot's: the rows come from the
		// population table the guest's own script declares, not from reading back what the guest
		// has laid out. Only the LIVE parts — the decorated label, the ghosting — are still the
		// guest's, joined on afterwards.
		//
		// THE SPLIT IS ALREADY IN THE DATA, which is why this needs no per-screen flag.
		// `Engine_UiMenuPopulation` gives eight tables real entries — HenchmanPauseMenu,
		// OptionsMenu, LogbookMenu, LogbookMenuOrb, CrimeMenu, ExpressionsMenu, AugmentMenu,
		// DyeMenu — and registers thirteen more as names alone: PauseMenu (its rows are the
		// C++'s, and `Engine_PauseMenu` builds them from `pause-root`), AbilitiesMenu,
		// EquipmentMenu, ItemsMenu, ClothingMenu, WorldMapMenu and the rest, which are populated
		// at runtime from the save. Those rows genuinely ARE the guest's data and still arrive
		// through the capture below, until they are read from the data areas instead.
		// A runtime template whose source is dry is not built. `BuildInto` clears the menu
		// before it spawns and the template is re-adopted every sample this falls through
		// (`_authoredFrom` is dropped below), so a template that yields nothing would empty the
		// list the MODEL built, and the model does not rebuild unchanged rows. The real-estate
		// screen is the case: its rows are spawned while the capture still states screen 0, so
		// its `capture:Root` is dry and only the model has its rows.
		Engine_UiMenuTemplate? authored = Engine_UiTemplates.Menu(PopulationScript);
		if (authored is { Entries.Count: 0, Source.Length: > 0 } runtime && !SourceHasRows(runtime))
		{
			if (!_saidTemplateDry)
			{
				_saidTemplateDry = true;
				GD.Print($"[screen-menu] '{PopulationScript}' has a template but its source " +
					$"'{runtime.Source}' yields NO rows — the list stays the model's. This is " +
					"the data hook, not the menu.");
			}

			authored = null;
		}

		if (authored is not null &&
			(authored.Entries.Count != 0 || authored.Source.Length != 0))
		{
			bool changed = RebuildFromTemplate(authored, buttons);
			if (Script.Visible.Count != 0)
			{
				if (!_saidAuthored)
				{
					_saidAuthored = true;
					GD.Print($"[screen-menu] '{PopulationScript}' is GODOT'S — " +
						$"{Script.Visible.Count} row(s) built from the population template" +
						(authored.Source.Length == 0
							? string.Empty
							: $" (source '{authored.Source}')") +
						", committing by identity; the guest supplies labels and ghosting only");
				}

				_stated = false;
				_authored = true;
				_lastButtons = buttons;
				_lastScreen = screen;
				_lastActive = active;
				return changed;
			}

			// THE TEMPLATE PRODUCED NOTHING, so its source is dry — for the runtime menus that
			// means the `g_Menu` capture has no rows, which is a DEAD DATA HOOK and not a menu
			// with no entries. Said once per list, because the fallback below hides it: reading
			// the rows back out of the button pool's geometry looks like it works right up until
			// a row is mid-tween or its identity is needed.
			if (!_saidTemplateDry)
			{
				_saidTemplateDry = true;
				GD.Print($"[screen-menu] '{PopulationScript}' has a template but it yielded NO " +
					$"rows (source '{authored.Source}') — falling back to reading the guest's " +
					"pool. This is the data hook, not the menu.");
			}
		}

		// NO SCRIPT AND NO BINDING — the list is nobody's template, so say which screen it was.
		//
		// The fallback below reads rows out of the button pool's geometry and looks like it
		// works, so an unowned screen is invisible unless it names itself. Every screen that
		// reaches this line is one still to be bound, and the id is the only handle on it: these
		// are exactly the screens whose widgets state no `MenuPopulationScript`.
		if (PopulationScript.Length == 0 && _saidUnbound != ScreenId)
		{
			_saidUnbound = ScreenId;
			GD.Print($"[screen-menu] screen {ScreenId} names NO population script and its " +
				"template binds none — the list is not owned by a template yet");
		}

		_authored = false;
		_authoredFrom = null;

		// The model next where it has rows: its rows are the C++'s own `g_Menu` statements, and
		// this path never touches the Gameface VM. Below it is the older position solve.
		if (Model?.For(presentation) is { Rows.Count: > 0 } modelled)
		{
			if (!_saidModel)
			{
				_saidModel = true;
				GD.Print($"[screen-menu] '{PopulationScript}' reads its model from the C++ " +
					$"STATEMENTS — {modelled.Rows.Count} row(s) on presentation " +
					$"0x{presentation:X8}, no Gameface read");
			}

			_stated = true;
			_lastButtons = buttons;
			_lastScreen = screen;
			_lastActive = active;
			return RebuildFromModel(modelled, screen);
		}

		// No Lua branch: the model is fed by every `g_Menu:` mutation the C++ makes, so a
		// presentation it has nothing for is one the C++ never populated.
		if (_stated || !_saidGeometry)
		{
			_saidGeometry = true;
			GD.Print($"[screen-menu] '{PopulationScript}' has NO readable g_Menu on " +
				$"presentation 0x{presentation:X8} — falling back to the position solve");
		}

		_stated = false;
		_signature.Clear();
		_statedVisible = -1;

		// Pool order is SPAWN order, not display order: an expanded category's children display
		// under their parent. Sort by the slot the guest laid each row on — `VisibleIndex` is
		// strictly increasing in the display index, so slot order IS display order. Ties (rows
		// parked in the zero-opacity gutter) keep pool order; an unlaid-out row sorts last.
		// THE PAGE BAND IS A TIE-BREAK, NOT A FILTER.
		//
		// It exists because one pool can hold two lists — the gift screen keeps its category
		// list and the open category's item list in the same 26 buttons, about a page apart in
		// x — and reading both as one makes every row disagree about the highlight.
		//
		// Applied as a filter it also deletes rows that are merely MOVING. `ExpandableMenu` is
		// authored off canvas and slid in, and every fold and page change re-tweens it, so a
		// sample taken during that motion finds each row far from its slot's authored x and
		// drops it — the list loses entries for no reason but timing. Worse than losing them:
		// these rows commit through the guest's own A press against ITS highlight index, so a
		// list that is one row short is a list where the wrong option is activated.
		//
		// The band only ever had to answer "which of these two is this slot's row". So collect
		// every filled button, group by the slot the guest laid it on, and let the band decide
		// only where a slot actually has more than one claimant. A slot with a single claimant
		// takes it wherever it currently sits.
		var claims = new Dictionary<int, List<(string Label, int Ordinal, float? Indent, int Slot, int Pool)>>();
		int pool = 0;
		foreach (Engine_ExpandableMenuItems.Button button in buttons)
		{
			if (button.IsFilled)
			{
				int slot = SlotOf(button.Ordinal, transform);
				int key = slot == 0 ? int.MaxValue : slot;
				if (!claims.TryGetValue(key, out var sharing))
				{
					sharing = new List<(string, int, float?, int, int)>();
					claims[key] = sharing;
				}

				sharing.Add((button.Text!, button.Ordinal, IndentOf(button.Ordinal, transform),
					key, pool));
			}

			++pool;
		}

		// Contention is the whole read's property, not one slot's. A second list in the pool
		// claims the SAME slots as the first — they share one slot table — so it shows up as
		// slots with two claimants. Deciding per slot would then admit the second list's tail
		// wherever it runs longer than the first and no slot is contested; deciding for the
		// read keeps the two apart exactly as before.
		int contested = 0;
		foreach ((int key, var sharing) in claims)
		{
			if (key != int.MaxValue && sharing.Count > 1)
			{
				++contested;
			}
		}

		var rows = new List<(string Label, int Ordinal, float? Indent, int Slot, int Pool)>();
		foreach ((int key, var sharing) in claims)
		{
			// One list in the pool: every filled button is a row of it, wherever the tween has
			// currently put it. The unlaid-out rows (slot 0) were always kept.
			if (contested == 0 || key == int.MaxValue)
			{
				rows.AddRange(sharing);
				continue;
			}

			bool anyOnPage = false;
			foreach (var claim in sharing)
			{
				if (OnPage(claim.Ordinal, transform))
				{
					rows.Add(claim);
					anyOnPage = true;
				}
			}

			// Every claimant off its slot means the assembly is moving, not that a second list
			// is in front: keep the first in pool order rather than emptying the slot.
			if (!anyOnPage)
			{
				rows.Add(sharing[0]);
			}
		}

		if (contested != 0 && !_saidContested)
		{
			_saidContested = true;
			GD.Print($"[screen-menu] '{PopulationScript}' has {contested} slot(s) claimed by more " +
				"than one filled button — two lists in one pool, so the page band decides this read");
		}

		rows.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.Pool.CompareTo(b.Pool));

		var labels = new List<string>();
		var ordinals = new List<int>();
		var indents = new List<float?>();
		foreach ((string label, int ordinal, float? indent, int _, int _) in rows)
		{
			labels.Add(label);
			ordinals.Add(ordinal);
			indents.Add(indent);
		}

		if (Same(labels))
		{
			return false;
		}

		_labels.Clear();
		_labels.AddRange(labels);
		_ordinals.Clear();
		_ordinals.AddRange(ordinals);
		_indents.Clear();
		_indents.AddRange(indents);

		// `IsOpen` is MODEL STATE in the shipped game, never derived from the layout: the only
		// seven writes are `CreateMenuItem` (false), the root (true), the A toggle, B,
		// `AutoCollapse` and `forceReset` (expandablemenu.dis main.9 pc45 / main.20 pc25,
		// expandablemenustateandconstants.dis main.0 pc14, expandablemenuinput.dis main.5 pc28 /
		// main.6 pc20, expandablemenuformatting.dis main.6 pc11 / main.10 pc40).
		//
		// Our rows do not survive `Clear`, so the fold has to be carried across by name.
		var wasOpen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
		CollectOpen(Script.MenuRoot, wasOpen);

		Menu.Clear();

		// The rows' identity, captured at the spawn: the `(Name, EventType, Id)` triple
		// `SelectMenuItem` commits with, taken from `sub_826CE8D0` on its way into a Lua table.
		//
		// Joined by the TREE, never by position: the capture is the whole tree flat (the C++ ships
		// every row at every depth once and never repopulates on a fold), so `capture[n]` is not
		// the nth visible row. `String4 == "Root"` marks a visible root and every other row names
		// its parent, while our rows carry a depth decoded from the stated tab offset — so a
		// depth-1 row takes the next unconsumed root, a depth-2 row the next unconsumed child of
		// whatever matched above it. A row this cannot name is left uncommittable and goes through
		// the guest's own A press; there is no positional fallback.
		System.Collections.Generic.List<Engine_UiModel.MenuRow> spawned =
			Engine_UiSource.Current.GetMenuRows(out _);

		const string rootKey = "\0Root";
		var byParent =
			new System.Collections.Generic.Dictionary<string,
				System.Collections.Generic.List<Engine_UiModel.MenuRow>>(
				System.StringComparer.Ordinal);
		foreach (Engine_UiModel.MenuRow captured in spawned)
		{
			string key = captured.IsRoot ? rootKey : captured.Parent;
			if (!byParent.TryGetValue(key, out var siblings))
			{
				siblings = new System.Collections.Generic.List<Engine_UiModel.MenuRow>();
				byParent[key] = siblings;
			}

			siblings.Add(captured);
		}

		var taken = new System.Collections.Generic.Dictionary<string, int>(
			System.StringComparer.Ordinal);
		var matchedAtDepth = new System.Collections.Generic.Dictionary<int, string>();
		int joined = 0;

		// The ported model, filled from the same read. The pool is flat and the nesting lives in
		// Lua, but the finished tab offset is on each row's position.x and `GetMenuItemTabOffset`
		// inverts cleanly, so depth and headedness both come back out of it (see DepthOf).
		// Decoded once, here, into real parent links and an explicit heading flag; nothing
		// downstream reads a live position again.
		var byDepth = new System.Collections.Generic.Dictionary<int, Engine_UiExpandableMenu.Item>();
		for (int i = 0; i < labels.Count; ++i)
		{
			bool isHeading = indents[i] is { } offset && Heading(offset);
			int depth = indents[i] is { } offsetted ? DepthOf(offsetted, isHeading) : 1;

			// `DepthOf` inverts `GetMenuItemTabOffset` geometrically, which needs the row to sit
			// where our slot table says its slot is — the abilities pool does not (x -55 against
			// a slot authored at +56). The offset can only separate the two families to depth 3
			// (see `Heading`), so anything past that is the inversion failing: treat it as root.
			if (depth > MaxStatedDepth)
			{
				if (_depthComplaint != PopulationScript)
				{
					_depthComplaint = PopulationScript;
					GD.Print($"[screen-menu] '{PopulationScript}' row {i + 1} states an indent " +
						$"of {indents[i]:0.#}, which inverts to depth {depth} — past the {MaxStatedDepth} " +
						"the offset can actually distinguish. Treating it as a root row; the " +
						"slot table and this screen's layout disagree.");
				}

				depth = 1;
			}
			byDepth.TryGetValue(depth - 1, out Engine_UiExpandableMenu.Item? parent);
			// The captured name is the row's identity — what the guest's dispatchers strcmp on;
			// `Row{n}` is only a placeholder. It is the next unconsumed sibling under whatever
			// matched one level up.
			Engine_UiModel.MenuRow? identity = null;
			string? parentKey = depth <= 1
				? rootKey
				: matchedAtDepth.TryGetValue(depth - 1, out string? above) ? above : null;

			if (parentKey is not null && byParent.TryGetValue(parentKey, out var siblings))
			{
				int used = taken.TryGetValue(parentKey, out int seen) ? seen : 0;
				if (used < siblings.Count)
				{
					identity = siblings[used];
					taken[parentKey] = used + 1;
					++joined;
				}
			}

			// The level below is only addressable while this one resolved; a gap must not let a
			// deeper row inherit a stale parent from an earlier branch.
			if (identity is { } matched)
			{
				matchedAtDepth[depth] = matched.Name;
			}
			else
			{
				matchedAtDepth.Remove(depth);
			}

			Engine_UiExpandableMenu.Item row = Script.Add(
				identity?.Name ?? $"Row{i + 1}", parent);

			// The guest's own answer where it stated one; the shipped rule where it did not.
			row.HeadingOverride = indents[i] is null ? null : isHeading;
			byDepth[depth] = row;
			// The guest's live text is already resolved through its own string table, so tag and
			// display string are the same thing here.
			row.StatedLabel = labels[i];
			row.TextTag = labels[i];
			row.Ordinal = ordinals[i];
			// A capture with no name is not an identity: pushing a blank matches no dispatcher's
			// strcmp, so such a row must not be committable.
			if (identity is { Name.Length: 0 })
			{
				GD.Print($"[screen-menu] row {i + 1} ('{labels[i]}') captured no NAME — " +
					"its identity is unusable and it will commit through the A press instead");
			}

			if (identity is { Name.Length: > 0 } captured)
			{
				row.EventType = captured.EventType;
				row.IdLow = (int)(captured.Id & 0xFFFF);
				row.IdHigh = (int)((captured.Id >> 16) & 0xFFFF);
				row.Committable = true;

				// The rating is nowhere else: `sub_826CE230` computes it and passes it to
				// SpawnButton as the ninth argument, and the star strip it drives is a SLIDE, so
				// neither the Lua table nor the element tree is readable for it.
				row.RatingSlide = captured.Rating;
			}
			// The fold is restored, never derived — a heading is not open for being a heading.
			// The one layout-driven open is the per-child pass below.
			row.IsOpen = wasOpen.Contains(FoldKey(row));
		}

		// The children the pool does not carry, attached from the capture — the host-owned fold.
		// The capture is the whole tree; the pool lays out only what the guest's Lua currently
		// displays, and on screens whose expansion never re-lays the pool the children are
		// otherwise unreachable. Every captured row still unconsumed under a matched parent
		// becomes a model child (name and identity from the capture, display string from
		// String2). A row the guest does lay out later consumes its capture entry in the join
		// above first, so nothing is attached twice.
		_hostChildren = 0;
		foreach (Engine_UiExpandableMenu.Item item in new List<Engine_UiExpandableMenu.Item>(
			Script.SpawnedItems))
		{
			if (item.Committable)
			{
				AttachCapturedChildren(item, byParent, taken, depth: 1);

				// A row that has just gained host children keeps whatever fold it had.
				item.IsOpen = wasOpen.Contains(FoldKey(item));
				_hostChildren += CountHostChildren(item);
			}
		}

		AttachSourcedFolders();
		PruneAuthoredDrops();

		// What the join reached, whenever it did not reach everything: a row without an identity
		// still draws and navigates, so a partial join is otherwise invisible. The capture's
		// shape rides along — an empty capture is a population not yet seen, a full one that
		// joined nothing is a tree whose depths do not line up with ours.
		if (joined != labels.Count)
		{
			int roots = byParent.TryGetValue(rootKey, out var topLevel) ? topLevel.Count : 0;
			GD.Print($"[screen-menu] identity join reached {joined} of {labels.Count} row(s) — " +
				$"the capture holds {spawned.Count} spawn(s), {roots} of them at the root; " +
				"the rest commit through the guest's own A press");
		}

		// Every row the guest is drawing children for is open, and only those: a parent whose
		// children are laid out must be open or the fold would hide rows that are on screen,
		// while captured children are ours to fold. The test is per CHILD — a pool child carries
		// an element ordinal, a host child -1.
		foreach (Engine_UiExpandableMenu.Item item in Script.SpawnedItems)
		{
			foreach (Engine_UiExpandableMenu.Item child in item.Children)
			{
				if (child.Ordinal >= 0)
				{
					item.IsOpen = true;
					break;
				}
			}
		}

		// `UpdateAfterSpawning` — what the front-end populations call when their list is complete.
		// The in-game ones never do, so it is called here, where the read has settled.
		Script.UpdateAfterSpawning();

		// Ghosting runs with the layout, so an empty category is dim and inert on the same frame.
		Script.UpdateAllGhosting();


		// Where the cursor starts: `HighlightIdx` is stated in the population table and nowhere
		// else, and cannot be recovered from a pool that is not laid out yet. The guest's own
		// solve takes over as soon as it lays the rows out.
		if (GuestHighlight == 0 &&
			Engine_UiMenuPopulation.For(PopulationScript) is { HighlightIndex: >= 1 } table)
		{
			Script.Highlight(table.HighlightIndex);
		}

		Follow();
		return true;
	}

	/// <summary>How many host-only rows the last read attached, at every depth.</summary>
	private int _hostChildren;

	/// <summary>
	/// B — collapse the highlighted row's parent and put the cursor on it. False at the root,
	/// the caller's signal to forward B: the guest's `sub_826D5AF0` then puts the screen back on
	/// the PAUSE ROOT (id 0), it does not leave the GUI. The set and the model move together —
	/// `CloseParent` alone is overwritten from <see cref="_open"/> on the next rebuild.
	/// </summary>
	public bool Collapse()
	{
		if (Script.Highlighted is not { Parent: { } parent } ||
			ReferenceEquals(parent, Script.MenuRoot))
		{
			return false;
		}

		if (_stated)
		{
			_open.Remove(FoldPath(parent));
			++_foldGeneration;
		}

		return Script.CloseParent();
	}

	/// <summary>An item's fold key — its path from the root. See <see cref="_open"/>.</summary>
	private string FoldPath(Engine_UiExpandableMenu.Item item)
	{
		string path = item.Name;
		for (Engine_UiExpandableMenu.Item? at = item.Parent;
			at is not null && !ReferenceEquals(at, Script.MenuRoot); at = at.Parent)
		{
			path = at.Name + "/" + path;
		}

		return path;
	}

	/// <summary>A row's fold identity across a rebuild: name plus label, since an unjoined row is
	/// called `Row{n}`, which is its position and moves when the list changes length.</summary>
	private static string FoldKey(Engine_UiExpandableMenu.Item item) =>
		$"{item.Name}\u0000{item.StatedLabel ?? string.Empty}";

	/// <summary>
	/// A leaf the host unfolds — the logbook's Achievements row, whose action the console's
	/// guide answered. Its children are ours (Ordinal -1), in the source's order, re-attached on
	/// every rebuild so a list that arrived after the row did still lands. The row is named by
	/// its own identity where the model or the capture gave one; a pool solved from positions
	/// names its rows `RowN`, and then the population script's root order names them — the
	/// guest's list IS that table while no folder is open, which is when a root can be pressed.
	/// </summary>
	/// <summary>
	/// Drops the rows this list's TEMPLATE says the guest may spawn but we do not show — see
	/// <see cref="Engine_UiMenuEntry.Drop"/>.
	///
	/// Through `RemoveChild`, the guest's own call (<see cref="Engine_UiExpandableMenu.Remove"/>):
	/// the row leaves its parent's children and the spawn list, everything under it goes with it,
	/// and the recompute clamps the cursor. Applied AFTER the sourced folders so a dropped row
	/// takes its attached children with it either way.
	///
	/// Keyed on the population script, which is what names the template — so this reaches the
	/// STATED build and the position solve alike. The position solve can only drop a row whose
	/// identity it joined; one that fell back to `Row{n}` has no name to match, and that is the
	/// same limit every other name-keyed rule on that path has.
	/// </summary>
	private void PruneAuthoredDrops()
	{
		if (PopulationScript.Length == 0)
		{
			return;
		}

		IReadOnlyList<Engine_UiTemplates.DroppedRow> dropped =
			Engine_UiTemplates.DroppedRows(PopulationScript);
		if (dropped.Count == 0)
		{
			return;
		}

		int gone = 0;
		var how = new List<string>();
		foreach (Engine_UiTemplates.DroppedRow drop in dropped)
		{
			// BY NAME, which is what a STATED build carries.
			while (drop.Name.Length != 0 && Script.Find(drop.Name) is { } named)
			{
				Detach(named);
				++gone;
				how.Add($"{drop.Name} (by name)");
			}

			// BY DISPLAYED TEXT, which is all the POSITION SOLVE carries. It joined 0 of the
			// logbook's 5 rows against the spawn capture in a real run — every row was called
			// `Row{n}` — so a name-only drop reaches nothing on the screen this exists for.
			// The tag resolves through the guest's own string table, so this is the same text
			// the player is reading, in whatever language is installed.
			if (drop.Tag.Length == 0 || Strings is null ||
				!Strings.TryGetString(drop.Tag, out string label) || label.Length == 0)
			{
				continue;
			}

			while (FindByLabel(label) is { } shown)
			{
				Detach(shown);
				++gone;
				how.Add($"'{label}' (by text, tag {drop.Tag})");
			}
		}

		if (gone != 0 && !_saidDrops)
		{
			_saidDrops = true;
			GD.Print($"[screen-menu] '{PopulationScript}' drops {gone} authored row(s): " +
				string.Join(", ", how));
		}

		void Detach(Engine_UiExpandableMenu.Item item)
		{
			// `RemoveChild`: out of the parent's children and out of the spawn list, subtree
			// and all. Done by ITEM rather than by name because a row matched on its text has
			// no usable name to look up again.
			item.Parent?.Children.Remove(item);
			Script.Forget(item);
		}
	}

	/// <summary>
	/// A row by the text it is DISPLAYING. Prefix rather than equality: the guest appends live
	/// decoration to a label before it writes the row — `Abilities &lt;s4&gt;(2)&lt;/s4&gt;` when
	/// there are points to spend — and that exists nowhere but in the string.
	/// </summary>
	private Engine_UiExpandableMenu.Item? FindByLabel(string label)
	{
		foreach (Engine_UiExpandableMenu.Item item in Script.SpawnedItems)
		{
			if (item.StatedLabel is { } shown &&
				shown.StartsWith(label, System.StringComparison.Ordinal))
			{
				return item;
			}
		}

		return null;
	}

	private bool _saidDrops;

	/// <summary>Said once when a slot really is claimed twice — see the page-band note.</summary>
	private bool _saidContested;

	private void AttachSourcedFolders()
	{
		// Bound the same way the template build is — a folded source reads the same capture.
		uint boundWas = Engine_UiMenuSources.BoundScreen;
		Engine_UiMenuSources.BoundScreen = CaptureKey;
		try
		{
			AttachSourcedFoldersBound();
		}
		finally
		{
			Engine_UiMenuSources.BoundScreen = boundWas;
		}
	}

	private void AttachSourcedFoldersBound()
	{
		var roots = new List<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item item in Script.SpawnedItems)
		{
			if (item.Parent is null || ReferenceEquals(item.Parent, Script.MenuRoot))
			{
				roots.Add(item);
			}
		}

		var tableRoots = new List<string>();
		if (Engine_UiMenuPopulation.For(PopulationScript) is { } table)
		{
			foreach (Engine_UiMenuPopulation.Entry entry in table.Entries)
			{
				if (entry.Parent.Length == 0)
				{
					tableRoots.Add(entry.Name);
				}
			}
		}

		for (int i = 0; i < roots.Count; ++i)
		{
			Engine_UiExpandableMenu.Item item = roots[i];
			string key = item.Name;
			if (key.StartsWith("Row", System.StringComparison.Ordinal) && tableRoots.Count == roots.Count)
			{
				key = tableRoots[i];
			}

			if (Engine_UiMenuSources.StatedFolderSource(PopulationScript, key) is not { } source)
			{
				continue;
			}

			foreach (Engine_UiMenuSources.Row sourced in Engine_UiMenuSources.Rows(source, key))
			{
				Engine_UiExpandableMenu.Item child = Script.Add(sourced.Name, item);
				child.StatedLabel = sourced.Label;
				child.TextTag = sourced.Label;
				child.Ordinal = -1;
				child.HeadingOverride = false;
				child.EventType = sourced.EventType;
				child.IdLow = (int)(sourced.Id & 0xFFFFu);
				child.IdHigh = (int)((sourced.Id >> 16) & 0xFFFFu);
				child.Committable = sourced.Committable;
				child.Ghosted = sourced.Ghosted;
				child.IsOpen = false;
				++_hostChildren;
			}
		}
	}

	/// <summary>Every open row in the tree, by <see cref="FoldKey"/>, before the model is cleared.</summary>
	private static void CollectOpen(
		Engine_UiExpandableMenu.Item item, System.Collections.Generic.HashSet<string> into)
	{
		foreach (Engine_UiExpandableMenu.Item child in item.Children)
		{
			if (child.IsOpen)
			{
				into.Add(FoldKey(child));
			}

			CollectOpen(child, into);
		}
	}

	private static int CountHostChildren(Engine_UiExpandableMenu.Item item)
	{
		int count = 0;
		foreach (Engine_UiExpandableMenu.Item child in item.Children)
		{
			if (child.Ordinal < 0)
			{
				++count;
			}

			count += CountHostChildren(child);
		}

		return count;
	}

	private void AttachCapturedChildren(
		Engine_UiExpandableMenu.Item parent,
		System.Collections.Generic.Dictionary<string,
			System.Collections.Generic.List<Engine_UiModel.MenuRow>> byParent,
		System.Collections.Generic.Dictionary<string, int> taken,
		int depth)
	{
		if (depth > 4 || !byParent.TryGetValue(parent.Name, out var siblings))
		{
			return;
		}

		// The guest wins where it is drawing: a parent with pool children lays its own expansion
		// out (the gift screen does), and captured rows beside those would double the level.
		foreach (Engine_UiExpandableMenu.Item existing in parent.Children)
		{
			if (existing.Ordinal >= 0)
			{
				return;
			}
		}

		int used = taken.TryGetValue(parent.Name, out int seen) ? seen : 0;
		for (int i = used; i < siblings.Count; ++i)
		{
			Engine_UiModel.MenuRow captured = siblings[i];
			if (captured.Name.Length == 0)
			{
				continue;
			}

			Engine_UiExpandableMenu.Item child = Script.Add(captured.Name, parent);
			child.StatedLabel =
				captured.Label.Length != 0 ? captured.Label : captured.Name;
			child.TextTag = child.StatedLabel;
			child.Ordinal = -1;
			child.HeadingOverride = false;
			child.EventType = captured.EventType;
			child.IdLow = (int)(captured.Id & 0xFFFF);
			child.IdHigh = (int)((captured.Id >> 16) & 0xFFFF);
			child.Committable = true;
			child.RatingSlide = captured.Rating;

			// Closed until the player opens it.
			child.IsOpen = false;
			AttachCapturedChildren(child, byParent, taken, depth + 1);
		}

		taken[parent.Name] = siblings.Count;
	}

	/// <summary>
	/// True while any visible row came from the capture rather than the pool. The guest's layout
	/// says nothing about those rows, so the cursor solve and the stick latch stand down.
	/// </summary>
	public bool HostExpanded
	{
		get
		{
			// A stated list is always host-driven: the fold is ours, so our visible list is not
			// the guest's and its cursor cannot mean anything on it.
			if (_stated)
			{
				return true;
			}

			foreach (Engine_UiExpandableMenu.Item item in Script.Visible)
			{
				if (item.Ordinal < 0)
				{
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>
	/// Follows the guest's cursor, per frame, from the live transform already read for every
	/// drawn screen. True when it moved and the view needs refreshing.
	/// </summary>
	public bool Track(IReadOnlyList<float?[]?>? transform)
	{
		// Nothing to follow while the model is stated: the fold and the cursor are both ours, so
		// the guest's per-frame state cannot move. Repopulation still arrives via `Rebuild`. The
		// solve below runs only for a presentation whose statements we could not reach.
		if (_stated)
		{
			return false;
		}

		// The pool's layout has no opinion about host-only rows — following it would yank the
		// cursor off a child and back onto whichever root the guest still highlights.
		if (HostExpanded)
		{
			return false;
		}

		int solved = Solve(transform);
		if (solved == 0 || solved == GuestHighlight)
		{
			return false;
		}

		GuestHighlight = solved;
		Follow();
		return true;
	}

	/// <summary>Puts the model's cursor where the guest's is, by index, on the one model.</summary>
	private void Follow()
	{
		if (GuestHighlight >= 1 && GuestHighlight <= Script.Visible.Count)
		{
			Script.Highlight(GuestHighlight);
		}
	}

	private bool Same(List<string> labels) => Same(labels, _labels);

	private static bool Same(List<string> read, List<string> held)
	{
		if (read.Count != held.Count)
		{
			return false;
		}

		for (int i = 0; i < read.Count; ++i)
		{
			if (!string.Equals(read[i], held[i], System.StringComparison.Ordinal))
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// The one highlight every laid-out row agrees on, or 0. A row at slot s with display index d
	/// solves <c>highlight = d - s + HIGHLIGHT_INDEX</c>; the two gutter slots (the fade ends) are
	/// skipped, since a row there is between two answers.
	/// </summary>
	private int Solve(IReadOnlyList<float?[]?>? transform)
	{
		int answer = 0;
		for (int i = 0; i < _ordinals.Count; ++i)
		{
			int slot = SlotOf(_ordinals[i], transform);
			if (slot <= 1 || slot >= Engine_FrontEndMenuView.Slots.Length)
			{
				continue;
			}

			int candidate = i + 1 - slot + Engine_FrontEndMenuView.HighlightSlot;

			// A row alone on a far slot mid-scroll solves to an index off either end of the
			// list; that is "no answer", not a cursor of -6.
			if (candidate < 1 || candidate > _ordinals.Count)
			{
				continue;
			}

			if (answer == 0)
			{
				answer = candidate;
			}
			else if (answer != candidate)
			{
				// Mid-slide. Keeping the previous answer beats picking one of two.
				return 0;
			}
		}

		return answer;
	}

	/// <summary>Which authored slot the guest put an element on, or 0 for none of them.</summary>
	private static int SlotOf(int ordinal, IReadOnlyList<float?[]?>? transform)
	{
		(_, float y, bool stated) = StatedAt(ordinal, transform);
		if (!stated)
		{
			return 0;
		}

		for (int i = 0; i < Engine_FrontEndMenuView.Slots.Length; ++i)
		{
			// Half the tightest gap in the table: a row mid-slide reads as "not on a slot".
			if (System.Math.Abs(Engine_FrontEndMenuView.Slots[i].Y - y) < 8f)
			{
				return i + 1;
			}
		}

		return 0;
	}

	/// <summary>
	/// Whether a row is on the page the player is looking at. A row the guest has not laid out
	/// counts as on-page — nothing has slid yet, and dropping it would empty the list.
	/// </summary>
	private static bool OnPage(int ordinal, IReadOnlyList<float?[]?>? transform)
	{
		(float x, _, bool stated) = StatedAt(ordinal, transform);
		if (!stated)
		{
			return true;
		}

		int slot = SlotOf(ordinal, transform);
		if (slot == 0)
		{
			return true;
		}

		return System.Math.Abs(x - Engine_FrontEndMenuView.Slots[slot - 1].X) <= PageBand;
	}

	/// <summary>
	/// The row indent the guest states for one element — its x relative to its slot's — or
	/// null when it states no position or lands on no slot.
	/// </summary>
	/// <summary>
	/// The deepest nesting the stated tab offset can distinguish: `GetMenuItemTabOffset` puts
	/// headings on -65, -70, -75 … and children on -41, -46, -51 …, and the two families
	/// interleave past the third step. A deeper inversion means the measurement was wrong.
	/// </summary>
	private const int MaxStatedDepth = 3;

	private string _depthComplaint = string.Empty;

	private static float? IndentOf(int ordinal, IReadOnlyList<float?[]?>? transform)
	{
		(float x, _, bool stated) = StatedAt(ordinal, transform);
		int slot = stated ? SlotOf(ordinal, transform) : 0;
		return slot == 0 ? null : x - Engine_FrontEndMenuView.Slots[slot - 1].X;
	}

	/// <summary>
	/// Whether an indent the guest stated belongs to a heading. `GetMenuItemTabOffset` is
	/// `-65 - 5*(depth-1)` plus 24 for a row that is NOT a heading, so headings land on -65, -70,
	/// -75 … and children on -41, -46, -51 … (bytecode in
	/// <see cref="Engine_UiMenuFormatter.TabOffsetFor"/>). The answer is stored as
	/// `HeadingOverride` and decides the row's tab texture (InnerRed vs tinted InnerBrown) and
	/// scale (1.025 vs 0.925). The split is the midpoint of the two families and is exact only to
	/// depth 3; past that they interleave and the depth would have to come from Lua.
	/// </summary>
	private static bool Heading(float indent) =>
		indent <= Engine_FrontEndMenuView.TabOffset +
			(Engine_FrontEndMenuView.HeadingTabBonus / 2f);

	/// <summary>
	/// The row's DEPTH, recovered from the stated tab offset. `GetMenuItemTabOffset` (main.38) is
	/// <code>offset = -65 - 5*(depth - 1); if IsHeading(item) then offset = offset + 24 end</code>
	/// so with the heading bonus taken back off the depth falls out — the only place a flat pool
	/// read can learn the nesting. Exact only to depth 3: past that the heading and plain families
	/// interleave (a depth-6 heading is -66, a depth-1 plain row -65).
	/// </summary>
	private static int DepthOf(float indent, bool heading)
	{
		// The bonus rides the CHILD, so a child's offset is the one to take it back off.
		float plain = indent - (heading ? 0f : Engine_FrontEndMenuView.HeadingTabBonus);
		int steps = (int)System.Math.Round(
			(Engine_FrontEndMenuView.TabOffset - plain) /
			-Engine_FrontEndMenuView.TabOffsetPerDepth);
		return System.Math.Max(1, steps + 1);
	}

	/// <summary>The guest's stated position for one element, and whether it stated one.</summary>
	private static (float X, float Y, bool Stated) StatedAt(int ordinal,
		IReadOnlyList<float?[]?>? transform)
	{
		if (transform is null || ordinal < 0 || ordinal >= transform.Count ||
			transform[ordinal] is not { } row || row.Length < 2 ||
			row[0] is not { } x || row[1] is not { } y)
		{
			return (0f, 0f, false);
		}

		return (x, y, true);
	}


	/// <summary>
	/// Runs a row's own <see cref="Engine_UiMenuEntry.Command"/>, if it names one that is
	/// implemented. True when the row is fully answered and nothing should reach the guest.
	///
	/// A row with a host action needs no guest identity — that is the whole point of having one —
	/// so this is tried ahead of both the identity push and `Committable`.
	/// </summary>
	private static bool RunHostAction(Engine_UiExpandableMenu.Item row, string label)
	{
		if (row.Command is not { Length: > 0 } action)
		{
			return false;
		}

		if (Engine_UiScreenActions.Run(action))
		{
			Engine_UiScreenActions.RefreshSettingLabel(row);
			GD.Print($"[screen-menu] '{label}' ran the HOST action '{action}' — " +
				"nothing was pushed to the guest");
			return true;
		}

		// Named but unknown: say so rather than silently falling through to the guest, which
		// would look like it worked while doing something else entirely.
		GD.Print($"[screen-menu] '{label}' names host action '{action}' but nothing " +
			"implements it — falling through");
		return false;
	}

	/// <summary>
	/// Commits the highlighted row BY IDENTITY, naming the row so our cursor is the only one — a
	/// plain A press commits whatever `g_Menu:GetHighlightedMenuItemData()` has as current.
	/// Ghosted rows are refused here: the script's early return lives in `SelectMenuItem` and the
	/// event push bypasses it. False when the row cannot be named (inventing an
	/// `(EventType, id)` pair would activate something else), so the caller falls back to the A
	/// press.
	/// </summary>
	/// <param name="discard">
	/// The Y press on the abilities screen, which is DISCARD, not activate. Two things have to
	/// change for it and both are easy to miss:
	///
	/// The row's own `Command` must NOT run. Every ability leaf carries
	/// `Ability:learn:&lt;type&gt;` (`Engine_UiAbilitySource.cs:167`), `RunHostAction` is tried
	/// ahead of the identity push on purpose, and that lever calls `sub_8246C970` — the
	/// PURCHASE — directly. So Y bought the ability and the discard never entered the picture,
	/// spending the player's gold on a keypress meant to refund it.
	///
	/// And the identity push must DISARM `+198`, because the guest's own Y does
	/// (`sub_826C2958` case 39 stores 0 at 0x826c2b2c; case 36 stores 1). `sub_826C2D40`
	/// @0x826c2d78 reads that byte to choose purchase over the discard confirm.
	/// </param>
	/// <summary>Whether the highlighted row is a mod ability's, which has nothing to unlearn.</summary>
	public bool HighlightedIsModAbility =>
		Script.Highlighted?.Command?.StartsWith(Engine_UiModAbilities.BuyAction, System.StringComparison.Ordinal) == true;

	public bool Commit(bool discard = false)
	{
		if (Script.Highlighted is not { } row)
		{
			GD.Print("[screen-menu] A pressed with NO highlighted row — falling back to the A press");
			return false;
		}

		// A mod ability cannot be unlearned, and its identity means nothing to the guest.
		if (discard && HighlightedIsModAbility)
		{
			return true;
		}

		string label = row.StatedLabel ?? row.Name;

		// Declared before the host-row branch below, which folds through the same flag.
		bool folds = row.Children.Count > 0;

		// A host-only row never touches the guest's cursor: its fold is ours, and its leaf commit
		// is the identity event — the `GUI:PushButtonEvent` `SelectMenuItem` posts, which the
		// drain dispatches to the screen's handler without consulting Lua's highlight.
		if (row.Ordinal < 0)
		{
			if (folds)
			{
				if (Script.Select() is null)
				{
					GD.Print($"[screen-menu] '{label}' — the press landed mid-slide and " +
						"SelectMenuItem swallows the WHOLE action");
					return true;
				}

				GD.Print($"[screen-menu] host fold: '{label}' " +
					$"({row.Children.Count} captured child(ren)) — now " +
					$"{(row.IsOpen ? "open" : "closed")}");
				return true;
			}

			// THE ROW'S OWN ACTION FIRST, and this branch used to skip it entirely.
			//
			// A row with no guest ordinal left here by the identity commit below without ever
			// consulting `Command`, so every sourced row that performs something host-side —
			// an ability purchase, an unequip — pushed a `(name, event, id)` at the guest's
			// dispatcher instead of doing the thing. A run showed it plainly: pressing Physique
			// logged `host commit BY IDENTITY` and bought nothing.
			//
			// The identity push stays as the fallback for rows that genuinely have no action of
			// their own, which is most of them.
			// A ghosted leaf is silent here as below — `SelectMenuItem` returns before pushing.
			if (row.Ghosted)
			{
				GD.Print($"[screen-menu] '{label}' is GHOSTED — no commit");
				return true;
			}

			if (!discard && RunHostAction(row, label))
			{
				return true;
			}

			uint hostId = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
			GD.Print($"[screen-menu] host commit BY IDENTITY{(discard ? " (DISCARD)" : string.Empty)}: " +
				$"'{label}' ({row.Name}, type {row.EventType}, id 0x{hostId:X8})");
			if (discard)
			{
				Engine_UiSource.Current.MenuDiscard(row.Name, row.EventType, hostId);
			}
			else
			{
				Engine_UiSource.Current.MenuEvent(row.Name, row.EventType, hostId);
			}
			return true;
		}

		// `Ghosted`, NOT `IsGhosted` — the auto-ghost dims a row, it does not silence it. See
		// Engine_UiExpandableMenu.Item.AutoGhosted.
		if (row.Ghosted)
		{
			GD.Print($"[screen-menu] '{label}' is GHOSTED — the shipped SelectMenuItem returns " +
				"before pushing anything, so neither do we");
			return true;
		}

		// `if self:WaitForInput() then return end` — the first line of `SelectMenuItem`
		// (expandablemenuinput.dis main.5 pc0..4), ahead of the `#ChildItems > 0` test, so a
		// press landing mid-slide is swallowed WHOLE (no fold and no event), leaves included.
		//
		// When the model is stated the fold is the guest's: `SelectMenuItem` runs its own gate
		// and toggle on the tree it owns and the next read carries both, so toggling here too
		// would be a second flip of one switch.
		//
		// THE CURSOR AS IT STOOD WHEN A WAS PRESSED. `Select()` opens a folder by putting the
		// cursor on its first child (`main.7 pc14..22`), and the guest's highlight is compared
		// against the row that was pressed, not the row the fold moved to — compared afterwards,
		// every folder press read as a disagreement and went out as a type-301 identity event
		// no handler owns, so the guest's highlight stayed on the heading and every page writer
		// keyed on it (`sub_826D1698`, `sub_826D2F80`, `sub_826C3C78`, `sub_826D9168`) stayed dark.
		int pressed = Script.CurrentHighlightIdx;
		if (!_stated && Script.Select() is null)
		{
			GD.Print($"[screen-menu] '{label}' — the press landed mid-slide and SelectMenuItem " +
				"swallows the WHOLE action, event included");
			return true;
		}

		// A folder folds here and nothing is posted. The guest's own `SelectMenuItem` is
		// unreachable: every host Lua chunk goes through `sub_82426470`, which defers rather than
		// runs once the GUI's `+0x64` byte is cleared (the constructor sets it, the frame update
		// clears it and nothing sets it back). The event is deliberately not pushed either — a
		// submenu row's event type is 301 (`sub_826CF098`, `li r9, 0x12D`) and no screen handler
		// has a case for it (`sub_826C6110` does not), so the expansion is the whole action.
		if (_stated && row.Children.Count > 0)
		{
			string path = FoldPath(row);
			bool opening = !_open.Contains(path);

			// The menu is an accordion: opening runs `AutoCollapse` first, or the visible count
			// runs past the twelve authored slots and rows land in the gutter either side.
			//
			// `expandablemenuformatting.dis main.6`, the shipped opcodes, verbatim:
			//
			//     for k, v in pairs(g_Menu.VisibleInfo.VisibleElements) do
			//       if v.IsOpen and k ~= keep then
			//         v.IsOpen = false
			//         self:CollapseMenuAndHighlight(k, true)
			//         self:onDirtyHighlight()
			//         return true                          -- pc11..19
			//       end
			//     end
			//     return false
			//
			// It walks the WHOLE visible list at every depth, so opening a nested category
			// closes the outer one — scoping it to siblings leaves two levels expanded.
			//
			// The toggle comes first: `SelectMenuItem` flips `IsOpen` at `main.5 pc26..28` and
			// only then fires the expand event (pc32..34), so the early return in
			// `onExpandMenuItem` (`main.7 pc8..13`) skips the CURSOR STEP, not the fold.
			if (opening)
			{
				_open.Add(path);
			}
			else
			{
				_open.Remove(path);
			}

			++_foldGeneration;
			row.IsOpen = opening;
			Script.DirtyLayout();

			string collapsed = string.Empty;
			if (opening)
			{
				// One finder, the formatter's — never a second copy of `main.6` here.
				if (Engine_UiMenuFormatter.AutoCollapse(Script, row) is { } other)
				{
					_open.Remove(FoldPath(other));
					++_foldGeneration;

					// `CollapseMenuAndHighlight(k, true)` — see the method for the arithmetic.
					Script.CollapseAndHighlight(other);
					collapsed = $" — AutoCollapse shut '{other.StatedLabel ?? other.Name}'";
				}
				else
				{
					// `CurrentHighlightIdx += 1` then `onScrollDown` (`main.7 pc14..22`) — the
					// cursor lands on the folder's first child. Only when nothing collapsed.
					Script.ScrollDown();
				}
			}

			GD.Print($"[screen-menu] fold: '{label}' {(opening ? "OPEN" : "SHUT")} " +
				$"({row.Children.Count} child(ren)) — {path}{collapsed}");
			return true;
		}

		if (!_stated && GuestHighlight == pressed)
		{
			GD.Print($"[screen-menu] selecting '{label}' through the guest's own SelectMenuItem " +
				$"— cursor {pressed}/{Script.Visible.Count}, " +
				$"({row.Name}, type {row.EventType})" +
				(row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu
					? " — a SUBMENU row, so the fold is the whole action"
					: string.Empty));

			// The fold already happened above, for every path — not repeated here.
			Engine_UiSource.Current.MenuSelect();

			// Fold probe, diagnostic: `SelectMenuItem`'s two gates (`WaitForInput`, `Ghosted`)
			// are invisible from outside, so ask the guest's own Lua and write the answer into
			// pool button 26's title — a row nothing fills — where the filled-count line prints it.
			if (row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu)
			{
				Engine_UiSource.Current.PostFrontEndAction(
					"GuiLua:local i=g_Menu.VisibleInfo.VisibleElements[g_Menu.CurrentHighlightIdx] " +
					"setAttribute(getElement(\"Icon.Title\", g_MenuFormatter.MenuItems[26].element)," +
					"\"textstring\", \"PROBE w=\"..tostring(g_Menu:WaitForInput())..\" g=\".." +
					"tostring(i.Ghosted)..\" c=\"..tostring(#i.ChildItems)..\" o=\"..tostring(i.IsOpen))");
			}

			return true;
		}

		// A HOST ACTION FIRST, AND IT NEVER REACHES THE GUEST'S DISPATCHER. A template row may
		// name what activating it DOES (`Engine_UiMenuEntry.Command`), and that is the seam by
		// which a menu stops needing the guest's UI at all: the row is ours, the action is ours,
		// and nothing is pushed into `PushButtonEvent` for a screen class to interpret.
		//
		// It is checked before `Committable` on purpose — a row with a host action needs no
		// guest identity, which is the whole point of having one.
		if (!discard && RunHostAction(row, label))
		{
			return true;
		}

		if (!row.Committable)
		{
			GD.Print($"[screen-menu] '{label}' has no usable NAME — falling back to the A press " +
				$"(guest cursor {GuestHighlight}, ours {Script.CurrentHighlightIdx})");
			return false;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		GD.Print($"[screen-menu] commit BY IDENTITY{(discard ? " (DISCARD)" : string.Empty)}: " +
			$"'{label}' ({row.Name}, type {row.EventType}, id 0x{id:X8}) — our cursor " +
			$"{Script.CurrentHighlightIdx}/{Script.Visible.Count}, the guest's {GuestHighlight}");
		if (discard)
		{
			Engine_UiSource.Current.MenuDiscard(row.Name, row.EventType, id);
		}
		else
		{
			Engine_UiSource.Current.MenuEvent(row.Name, row.EventType, id);
		}
		return true;
	}

	/// <summary>
	/// A one-line description for the build report. Every label is quoted and an empty one says
	/// so: a blank tab, a missing row and an unresolved label look identical otherwise.
	/// </summary>
	public string Describe()
	{
		var quoted = new List<string>();
		for (int i = 0; i < _labels.Count; ++i)
		{
			// The indent decides the row's face and nesting, so it is reported with the label: a
			// root row reads about -41 and a submenu row about -70, and an unstated one reads `-`.
			string indent = i < _indents.Count && _indents[i] is { } offset
				? $"@{offset:0}"
				: "@-";
			quoted.Add((_labels[i].Length == 0 ? "<EMPTY>" : $"'{_labels[i]}'") + indent);
		}

		// `Count` is the finished list; the quoted names are the READ it was built from, which a
		// template drop can leave shorter. Named so the two cannot be mistaken for each other.
		return $"{Count} row(s)" +
			(_authored
				? " [GODOT'S, from the population template]"
				: _stated ? " [STATED by g_Menu]" : " [solved from positions]") +
			(_hostChildren == 0
				? string.Empty
				: $" (+{_hostChildren} host child(ren), folded)") +
			", guest highlight " +
			$"{(GuestHighlight == 0 ? "unsolved" : GuestHighlight.ToString())}" +
			(_labels.Count == 0 ? string.Empty : $", read: {string.Join(", ", quoted)}") +
			$"  [script model: {Script.Describe()}]" +
			(PopulationScript.Length == 0
				? ", the widget names no population script"
				: $", population script '{PopulationScript}'" +
					(Engine_UiMenuPopulation.For(PopulationScript) is { } known
						? $" ({known.Entries.Count} static entr(y/ies), " +
							$"HighlightIdx {known.HighlightIndex})"
						: " — NOT IN THE TRANSLATED SET"));
	}
}
