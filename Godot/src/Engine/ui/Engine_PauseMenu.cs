namespace Refii.Ui;

/// <summary>
/// The pause menu's item list, built the way the guest builds its own.
/// </summary>
/// <remarks>
/// The list is the guest's; layout, highlight and navigation are ours — the same split as
/// <see cref="Engine_FrontEndMenu"/>.
///
/// Source: `sub_8298DF98`, the hero variant of the builder `sub_826A2C88` runs when it pushes
/// screen id 0. Items spawn through the same GUI-var stager as the front end's
/// (`sub_826CE8D0` -> `g_Menu:SpawnButton()`), whose argument mapping is `String1` = item NAME,
/// `String2` = display TEXT, `String3` = icon, `String4` = parent, `Float3` = the command. The
/// co-op henchman list (`sub_8298EDE0`) is not here, nor is the hero list's `Kick` entry —
/// `0x8298EC64` spawns it only when `*(*(dword_83496AB8+12)+284) == 1` and its label is a `%1`
/// substitution of the henchman's name.
///
/// The Saving children are named by their own displayed text: `sub_826CE848` takes a tag alone,
/// `sub_8230E0E8` localises it, `sub_82B3F898` narrows it to bytes, and the narrowed text is
/// passed as `String1` — so their identity is "Save and Continue", a string in the text bank
/// rather than an identifier in the image.
///
/// Dispatch is the tab lever, not the pump: each screen entry names the tab `sub_826B89E0`'s
/// switch maps to its screen, and the host writes the guest's pending-tab fields (see
/// Engine_UiSource.Current.PauseTabSelect). The tab path to Abilities omits the mode call the
/// ITEM path makes (`vfn+32(screen14, 0)`) and so inherits the screen's last mode — the guest's
/// own LB/RB behaves identically.
///
/// The Saving group's children dispatch by EVENT — {type 2, param} alone, per `sub_826A5FC0`:
/// param 0/1/2 open query boxes 23/24/25 (param 2 quits immediately when `sub_82448900` says
/// there is no unsaved progress). The tab lever cannot express that, so these go through the
/// guest-tick pump ("PauseSaving:<param>") and the guest's own query box takes the confirm.
/// </remarks>
public static class Engine_PauseMenu
{
	/// <summary>Command prefix a tab entry dispatches with; the suffix is the tab id.</summary>
	public const string TabCommandPrefix = "PauseTab:";

	/// <summary>Command prefix of a Saving child; the suffix is the guest's event param —
	/// 0 save-and-continue, 1 save-and-quit, 2 quit-without-saving.</summary>
	public const string SavingCommandPrefix = "PauseSaving:";

	/// <summary>
	/// Command prefix of a row that leaves the APP rather than the world; the suffix is the
	/// guest saving param to run first — 1 save then leave, 2 leave without saving.
	/// </summary>
	/// <remarks>
	/// Posts the same `PauseSaving:N` event the title rows do, then latches on the front end
	/// coming up. The confirmation box, the save and the teardown stay the guest's and stay in
	/// the guest's order, so the process cannot end while the save is still being written; a
	/// player who answers "no" never reaches the front end, so a refusal needs no signal.
	/// </remarks>
	public const string QuitCommandPrefix = "PauseQuit:";

	/// <summary>
	/// Command prefix of a tab the HOST owns outright; the suffix is the host-only screen id it
	/// stands (<see cref="Engine_UiBookSet.HostOnlyScreenId"/> and up). The tab lever cannot
	/// express these — `sub_826B89E0`'s switch has no case for a screen the guest never had —
	/// so they never reach the bridge and the guest's pause root stays exactly where it is.
	/// </summary>
	public const string OwnedCommandPrefix = "PauseOwned:";

	/// <summary>Tab ids as `sub_826B89E0`'s switch numbers them.</summary>
	public const uint TabMap = 2;
	public const uint TabAbilities = 3;
	public const uint TabItems = 4;
	public const uint TabWeapons = 5;
	public const uint TabClothing = 6;
	public const uint TabLogbook = 7;
	public const uint TabOptions = 8;

	/// <summary>
	/// Builds the hero pause list from the TEMPLATE — `sub_8298DF98`'s spawn list as data: same
	/// names, same event params on the Saving children, same tab ids, and the guest's own
	/// SetInitiallySelectedItem(2). A `user://ui/templates.json` override replaces the record.
	/// </summary>
	public static Engine_FrontEndMenu BuildHero() =>
		Engine_UiTemplates.Menu("pause-root")?.Build() ?? new Engine_FrontEndMenu();

	/// <summary>
	/// Applies the bridge's availability mask (bit N = tab N available) to the tab entries.
	/// True when anything changed, so the caller knows to refresh its view.
	///
	/// The mask is `sub_826B83D0`, the predicate the guest's LB/RB nav loops, read out of the
	/// hero's restriction flags. The push lever never re-checks it, so a ghosted entry must
	/// never dispatch.
	///
	/// Ghosted, not removed: `sub_8298F648` spawns every tab then calls
	/// `sub_823DF660(0, name, ghost, 1)` (`SetItemGhosting`) for Map, Abilities, Items, Weapons,
	/// Clothing and Logbook from the hero's bytes +52/+51/+45 and the childhood test. The tab
	/// cycler skips a ghosted row; the list does not.
	/// </summary>
	public static bool ApplyTabAvailability(Engine_FrontEndMenu menu, uint mask)
	{
		bool changed = false;
		changed |= menu.SetGhosted("Map", (mask & (1u << (int)TabMap)) == 0);
		changed |= menu.SetGhosted("Abilities", (mask & (1u << (int)TabAbilities)) == 0);
		changed |= menu.SetGhosted("Items", (mask & (1u << (int)TabItems)) == 0);
		changed |= menu.SetGhosted("Weapons", (mask & (1u << (int)TabWeapons)) == 0);
		changed |= menu.SetGhosted("Clothing", (mask & (1u << (int)TabClothing)) == 0);
		changed |= menu.SetGhosted("Logbook", (mask & (1u << (int)TabLogbook)) == 0);
		changed |= menu.SetGhosted("Options", (mask & (1u << (int)TabOptions)) == 0);
		return changed;
	}

	/// <summary>
	/// Takes each row's label from the guest's own button pool. True when anything changed.
	///
	/// The Babel tags all resolve, but only to the bare noun: the guest composes the live
	/// decoration before it writes the row — "Abilities" becomes
	/// `Abilities &lt;s4&gt;(2)&lt;/s4&gt;` when there are points to spend, Items and Clothing
	/// pick up `&lt;exclamation.img&gt;` when something inside is new — and that exists nowhere
	/// but in the string.
	///
	/// Matched by ORDER: both lists come from the same builder, `sub_8298DF98` spawns in the
	/// order <see cref="BuildHero"/> transcribes, and an unavailable tab is dropped from both
	/// (<see cref="ApplyTabAvailability"/>). The guest's buttons carry the finished label, not
	/// the identity, so a name join is unavailable and a count mismatch is a real disagreement —
	/// the shorter list wins and the caller reports the gap.
	/// </summary>
	/// <param name="positionalFallback">
	/// Whether a row the NAME join did not reach may take a label by its index in the guest's
	/// pool. True for the pause list, which is built to match the guest's one for one. FALSE for
	/// any list that deliberately differs from the guest's — a template list that drops a row is
	/// shorter, and then every row past the gap takes its predecessor's label.
	/// </param>
	public static bool ApplyStatedLabels(
		Engine_FrontEndMenu menu,
		System.Collections.Generic.IReadOnlyList<Engine_ExpandableMenuItems.Button> buttons,
		System.Collections.Generic.IReadOnlyList<Engine_UiMenuModel.Row> stated,
		bool positionalFallback = true)
	{
		var filled = new System.Collections.Generic.List<string>();
		foreach (Engine_ExpandableMenuItems.Button button in buttons)
		{
			if (button.IsFilled && button.Text is { } text)
			{
				filled.Add(text);
			}
		}

		// HOST-ONLY ROWS ARE NOT IN THE GUEST'S LIST AND MUST NOT BE COUNTED AGAINST IT. The
		// guest spawns seven tabs plus Saving; a host tab makes ours longer, and the positional
		// fallback below would then hand every row after it the label of its predecessor.
		var offered = new System.Collections.Generic.List<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item root in menu.Roots)
		{
			if (root.Available && !root.HostOnly)
			{
				offered.Add(root);
			}
		}

		// An empty read is no news, not no labels: a sample taken between the guest tearing the
		// pause list down for a tab change and building it again looks identical to "the guest
		// offers no rows", so neither may be acted on.
		if (filled.Count == 0 && stated.Count == 0)
		{
			return false;
		}

		// By name, from the C++'s own statements: `SpawnButton`'s String2 is the localised label
		// with decorations (unspent ability count, new-item marker) already appended at spawn
		// time. Taken from the capture, so it needs no VM read or pool join and arrives before
		// the guest has laid anything out.
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
		var named = new System.Collections.Generic.HashSet<Engine_UiExpandableMenu.Item>();
		foreach (Engine_UiExpandableMenu.Item item in offered)
		{
			if (!byName.TryGetValue(item.Name, out string? label))
			{
				continue;
			}

			named.Add(item);
			if (!string.Equals(item.StatedLabel, label, System.StringComparison.Ordinal))
			{
				item.StatedLabel = label;
				changed = true;
			}
		}

		if (named.Count == offered.Count || !positionalFallback)
		{
			return changed;
		}

		// The positional fallback, for rows the tree did not name — a pause list read before its
		// Lua is up, or a template entry the guest never spawned. Rows the name join settled are
		// skipped so the two cannot disagree.
		int at = 0;
		foreach (Engine_UiExpandableMenu.Item item in offered)
		{
			if (named.Contains(item))
			{
				++at;
				continue;
			}

			if (at < filled.Count)
			{
				if (!string.Equals(item.StatedLabel, filled[at], System.StringComparison.Ordinal))
				{
					item.StatedLabel = filled[at];
					changed = true;
				}
			}
			else if (stated.Count != 0 && item.StatedLabel is not null)
			{
				// The tree is up and does not carry this row, so its label belongs to a list that
				// no longer exists. Cleared only on that evidence, never on an empty read.
				item.StatedLabel = null;
				changed = true;
			}

			++at;
		}

		return changed;
	}

}
