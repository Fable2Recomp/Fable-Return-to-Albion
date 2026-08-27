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
				foreach (Engine_UiMenuModel.Row row in next)
				{
					Engine_UiExpandableMenu.Item item = Script.Add(row.Name, parent: null);
					byName[row.Name] = item;
					placed.Add((row, item));
				}

				break;
			}

			pending = next;
		}

		foreach ((Engine_UiMenuModel.Row row, Engine_UiExpandableMenu.Item item) in placed)
		{
			// `String2` is already the resolved, decorated string; there is no tag to resolve.
			item.StatedLabel = row.Label.Length != 0 ? row.Label : row.Name;
			item.TextTag = item.StatedLabel;
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

		IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons =
			Engine_ExpandableMenuItems.Read(screen, text, active, slides);

		Presentation = presentation;

		// The model first where it has rows: its rows are the C++'s own `g_Menu` statements, and
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
		var rows = new List<(string Label, int Ordinal, float? Indent, int Slot, int Pool)>();
		int pool = 0;
		foreach (Engine_ExpandableMenuItems.Button button in buttons)
		{
			// On this page only (see PageBand): a row of the list underneath is also a filled
			// button with real text, and taking it would put two lists in one.
			if (button.IsFilled && OnPage(button.Ordinal, transform))
			{
				int slot = SlotOf(button.Ordinal, transform);
				rows.Add((button.Text!, button.Ordinal, IndentOf(button.Ordinal, transform),
					slot == 0 ? int.MaxValue : slot, pool));
			}

			++pool;
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
	/// Commits the highlighted row BY IDENTITY, naming the row so our cursor is the only one — a
	/// plain A press commits whatever `g_Menu:GetHighlightedMenuItemData()` has as current.
	/// Ghosted rows are refused here: the script's early return lives in `SelectMenuItem` and the
	/// event push bypasses it. False when the row cannot be named (inventing an
	/// `(EventType, id)` pair would activate something else), so the caller falls back to the A
	/// press.
	/// </summary>
	public bool Commit()
	{
		if (Script.Highlighted is not { } row)
		{
			GD.Print("[screen-menu] A pressed with NO highlighted row — falling back to the A press");
			return false;
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

			uint hostId = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
			GD.Print($"[screen-menu] host commit BY IDENTITY: '{label}' " +
				$"({row.Name}, type {row.EventType}, id 0x{hostId:X8})");
			Engine_UiSource.Current.MenuEvent(row.Name, row.EventType, hostId);
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

		if (!_stated && GuestHighlight == Script.CurrentHighlightIdx)
		{
			GD.Print($"[screen-menu] selecting '{label}' through the guest's own SelectMenuItem " +
				$"— cursor {Script.CurrentHighlightIdx}/{Script.Visible.Count}, " +
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

		if (!row.Committable)
		{
			GD.Print($"[screen-menu] '{label}' has no usable NAME — falling back to the A press " +
				$"(guest cursor {GuestHighlight}, ours {Script.CurrentHighlightIdx})");
			return false;
		}

		uint id = ((uint)row.IdHigh << 16) | ((uint)row.IdLow & 0xFFFFu);
		GD.Print($"[screen-menu] commit BY IDENTITY: '{label}' " +
			$"({row.Name}, type {row.EventType}, id 0x{id:X8}) — our cursor " +
			$"{Script.CurrentHighlightIdx}/{Script.Visible.Count}, the guest's {GuestHighlight}");
		Engine_UiSource.Current.MenuEvent(row.Name, row.EventType, id);
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

		return $"{Count} row(s)" +
			(_stated ? " [STATED by g_Menu]" : " [solved from positions]") +
			(_hostChildren == 0
				? string.Empty
				: $" (+{_hostChildren} host child(ren), folded)") +
			", guest highlight " +
			$"{(GuestHighlight == 0 ? "unsolved" : GuestHighlight.ToString())}" +
			(Count == 0 ? string.Empty : $": {string.Join(", ", quoted)}") +
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
