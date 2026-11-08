using System.Collections.Generic;

using Item = Refii.Ui.Engine_UiExpandableMenu.Item;

namespace Refii.Ui;

/// <summary>
/// The front end's root menu, owned by Godot.
///
/// The guest's menu has no C++ item list and no C++ highlight — items, order, nesting and the
/// highlight all live in the shipped Lua `g_Menu`, and C++ only spawns into it and receives an
/// activation back, so there is nothing to mirror.
///
/// What still comes from the guest is whether an entry exists at all: "Load Game" only when
/// signed in with at least one save, "Language" only above one installed language.
///
/// Activation is BY NAME — the guest dispatches on the item's name string (a strcmp chain over
/// "New Game", "Load Game", "Downloadable Content", "SubtitlesOn", "SubtitlesOff"), never an
/// index, so a host-authored entry triggers a guest action by naming it and an entry the guest
/// never had simply never dispatches.
/// </summary>
public sealed class Engine_FrontEndMenu
{

	/// <summary>
	/// The navigation cadence from the shipped `guistateandconstants.lua`: the first repeat waits
	/// 0.25s, every one after 0.14s. Aliased onto <see cref="Engine_UiGuiConstants"/>, the
	/// translation of record — one copy, not two.
	/// </summary>
	public const double FirstNavigateInterval = Engine_UiGuiConstants.FirstNavigateInterval;

	public const double SubsequentNavigateInterval =
		Engine_UiGuiConstants.SubsequentNavigateInterval;

	public const double MinimumNavigateInterval = 0.20;

	/// <summary>
	/// `INPUT_INTERVAL_CLAMP` — a ceiling on the input accumulators, in SECONDS, not a step
	/// count. `uicontractlib.dis main.1` tests it against elapsed time (`pc21` for
	/// `TimeSinceNavigate`, `pc39` for `TimeSinceAdjust`) and stops accumulating past it, so a
	/// parked clock does not fire a burst on the next press. There is no ramp anywhere.
	/// </summary>
	public const int InputIntervalClamp = 10;

	/// <summary>
	/// Below this magnitude the stick counts as released — `THUMBSTICK_RELEASE_THRESHOLD`, half
	/// of a hysteresis pair with <see cref="ThumbstickPushThreshold"/>.
	/// </summary>
	public const double ThumbstickReleaseThreshold = 0.65;

	/// <summary>
	/// Above this magnitude the stick counts as pushed — `THUMBSTICK_PUSH_THRESHOLD`. A direction
	/// engages at 0.70 and does not release until the stick falls back under 0.65, which is why
	/// `guistateandconstants.lua` also keeps four `Pushing*Held` booleans. Never use one
	/// threshold for both.
	/// </summary>
	public const double ThumbstickPushThreshold = 0.70;

	/// <summary>
	/// The one model. This class holds only the FRONT END's state around it — the storage guards,
	/// the save enumeration, the refusal tags — and never a second menu.
	/// </summary>
	public Engine_UiExpandableMenu Model { get; } = new();

	private List<Item> _roots => Model.MenuRoot.Children;

	/// <summary>
	/// The root entries in spawn order, expanded children excluded. <see cref="Visible"/> cannot
	/// stand in: expanding a group splices its children inline, and the guest's own button pool
	/// never carries them.
	/// </summary>
	public IReadOnlyList<Item> Roots => _roots;

	/// <summary>
	/// The shipped repeat (<see cref="Engine_UiNavigationClock"/>): no ramp — `DoNavigate` swaps
	/// to `SUBSEQUENT_NAVIGATE_INTERVAL` the moment `NavigationCount` passes 1.
	/// </summary>
	private readonly Engine_UiNavigationClock _clock = new();

	/// <summary>The entries currently on screen, in order — roots plus any expanded children.</summary>
	public IReadOnlyList<Item> Visible => Model.Visible;

	/// <summary>Index into <see cref="Visible"/>.</summary>
	public int HighlightIndex => Model.CurrentHighlightIdx - 1;

	/// <summary>The highlighted entry, or null when the menu is empty.</summary>
	public Item? Highlighted =>
		HighlightIndex >= 0 && HighlightIndex < Visible.Count ? Visible[HighlightIndex] : null;

	/// <summary>
	/// Whether the highlighted row accepts A: explicit ghosting and a fold animation refuse
	/// every route, including host commands and screen openers. Automatic ghosting is visual
	/// only, as in the guest's SelectMenuItem; an empty category can be populated on demand.
	/// </summary>
	public bool CanActivate => !Model.Animating && Highlighted is { Ghosted: false };

	/// <summary>
	/// The entry whose children are showing, or null.
	///
	/// Derived rather than stored: openness is per item (`IsOpen`), the way the script keeps it,
	/// so a nested list can have several levels open at once and this names the outermost.
	/// </summary>
	/// <summary>
	/// True when B would actually collapse something — the cursor is INSIDE an open folder. Not
	/// the same question as <see cref="Expanded"/>: `CloseMenuItem` (`expandablemenuinput.dis
	/// main.6`) guards on the highlighted row having a non-root parent, so a folder left open
	/// with the cursor back out on a root tab collapses nothing and B must reach the guest.
	/// </summary>
	public bool CanCollapse =>
		Highlighted is { Parent: { } parent } && !ReferenceEquals(parent, Model.MenuRoot);

	public Item? Expanded
	{
		get
		{
			foreach (Item root in _roots)
			{
				if (root.IsOpen && root.Children.Count > 0)
				{
					return root;
				}
			}

			return null;
		}
	}

	/// <summary>
	/// The host row that ends the app, as its command. No guest dispatch behind it — a console
	/// front end had nowhere to quit to, so no entry, event or text tag exists to reach for.
	/// </summary>
	public const string QuitToDesktopCommand = "QuitToDesktop";

	/// <summary>What the storage layer last said, as the title's availability rules were told.</summary>
	public Engine_UiModel.SaveState Saves { get; set; } = new(false, -1, 0);

	/// <summary>
	/// A refusal the last <see cref="Activate"/> produced, as a text tag, or null. Latched, not
	/// thrown: a refusal is neither a dispatch nor a screen, the two things Activate returns.
	/// </summary>
	public string? Warning { get; private set; }

	/// <summary>The pending refusal, cleared by reading it.</summary>
	/// <summary>
	/// Raises a warning from the HOST side, through the same pipe the guest's own refusals
	/// use. The tag may be a Babel tag or plain text — resolution falls back to verbatim, so
	/// a host-authored message renders as written.
	/// </summary>
	public void RaiseWarning(string tag) => Warning = tag;

	public string? TakeWarning()
	{
		string? warning = Warning;
		Warning = null;
		return warning;
	}

	public void Add(Item item)
	{
		item.Parent = Model.MenuRoot;
		_roots.Add(item);
		foreach (Item child in item.Children)
		{
			child.Parent = item;
		}

		Rebuild();
	}

	/// <summary>
	/// Empties the list — for a model whose contents are the guest's and repopulate under it with
	/// no event to say so. A built-once menu never needs this.
	/// </summary>
	public void Clear() => Model.Clear();

	/// <summary>Re-derives the visible list after the title changed what the roots offer.</summary>
	public void Relayout() => Rebuild();

	/// <summary>
	/// Re-derives the visible list, following the ENTRY rather than the index: availability
	/// changes under the menu (signing in adds "Load Game" above the highlight). The model
	/// splices and filters; this only restores the cursor.
	/// </summary>
	private void Rebuild()
	{
		Item? was = Highlighted;
		Model.DirtyLayout();
		Ghost();

		if (was is null)
		{
			return;
		}

		int index = Model.VisibleIndexOf(was);
		Model.Highlight(index >= 1 ? index : 1);
	}

	/// <summary>
	/// Recomputes the automatic ghosting — `UpdateAllGhosting`, run with every layout, and from
	/// <see cref="Add"/> too: an arriving entry can make a category stop being empty.
	/// </summary>
	private void Ghost() => Model.UpdateAllGhosting();

	/// <summary>
	/// Advances the auto-repeat and returns how many steps to move this frame.
	///
	/// <paramref name="direction"/> is -1 up, +1 down, 0 released. The cadence is the guest's,
	/// and it is a STEP rather than a curve: act immediately on a fresh press, wait
	/// <see cref="FirstNavigateInterval"/> for the second, then
	/// <see cref="SubsequentNavigateInterval"/> for every one after — `DoNavigate`
	/// (`guiinput.dis main.8 pc13..20`) swaps the interval once, on `1 < NavigationCount`, and
	/// never touches it again.
	/// </summary>
	public int Navigate(int direction, double delta)
	{
		int steps = Repeat(direction, delta);
		for (int i = 0; i < steps; ++i)
		{
			Move(direction);
		}

		return steps;
	}

	/// <summary>
	/// The auto-repeat curve alone — how many steps this frame, without moving anything, for a
	/// list whose cursor is not ours: in-game screens commit against Lua's own
	/// <c>CurrentHighlightIdx</c>, so moving this model too would draw a highlight one row ahead.
	/// </summary>
	public int Repeat(int direction, double delta)
	{
		_clock.FirstInterval = FirstNavigateInterval;
		_clock.SubsequentInterval = SubsequentNavigateInterval;
		return _clock.Steps(direction, delta);
	}


	/// <summary>
	/// Moves the highlight. It CLAMPS at both ends and does not wrap: `expandablemenuinput.lua`
	/// guards `ScrollUp` on `1 &lt; CurrentHighlightIdx` and `ScrollDown` on
	/// `CurrentHighlightIdx &lt; #VisibleElements`.
	/// </summary>
	/// <summary>
	/// One step. CLAMPS at both ends and never wraps — `ScrollUp` stops at 1 and `ScrollDown`
	/// at the last entry, which is the script's own behaviour.
	/// </summary>
	private void Move(int direction)
	{
		if (direction < 0)
		{
			Model.ScrollUp();
		}
		else if (direction > 0)
		{
			Model.ScrollDown();
		}
	}

	/// <summary>
	/// Activates an accepted highlighted entry. Returns its command or guest identity, or null
	/// for a refused press, a group toggle or a screen opener. Host-only commands obey the
	/// same acceptance gate. Call the overload with an output to distinguish an accepted opener.
	///
	/// An entry that opens something must not also dispatch: <see cref="Item.Submenu"/> and
	/// <see cref="Item.Opens"/> return null too, not just <see cref="Item.Children"/>.
	/// </summary>
	public string? Activate() => Activate(out _);

	/// <summary>
	/// Activates once and identifies a screen opened by that same press. A fold can move the
	/// highlight onto a child, so the view must not infer an opener from the new highlight.
	/// </summary>
	/// <param name="screenToOpen">The accepted leaf's surface after storage guards pass;
	/// null for refusals, group toggles and command dispatches.</param>
	/// <returns>The command or guest identity to dispatch, or null when no command fired.</returns>
	public string? Activate(out string? screenToOpen)
	{
		screenToOpen = null;
		if (!CanActivate || Highlighted is not { } item)
		{
			return null;
		}

		if (item.Children.Count > 0)
		{
			// Through the model, never a re-implemented toggle: `Select()` is the port of
			// `SelectMenuItem` whole — the mid-animation swallow, the ghost test, the identity
			// push, auto-collapse and the cursor step. A null answer is the swallow and must
			// not fall through to a dispatch.
			if (Model.Select() is null)
			{
				return null;
			}

			Rebuild();
			return null;
		}

		if (item.Opens is not null)
		{
			if (Refii.Core.Engine_TitleGame.Active.FrontEndOpenGuard(this, item) is (true, var refusal))
			{
				Warning = refusal;
			}

			if (Warning is null)
			{
				screenToOpen = item.Opens;
			}

			return null;
		}

		if (item.HostOnly)
		{
			// A host-only entry still dispatches its COMMAND. The flag says the guest has no
			// `(EventType, id)` to commit this row against, not that activating it does nothing:
			// the achievements tab is host-owned end to end and reaches its screen this way.
			return item.Command;
		}

		// A command entry dispatches on its COMMAND, not its name — see Item.Command.
		return item.Command ?? item.Name;
	}

	/// <summary>Collapses an expanded submenu. True when it consumed the press.</summary>
	/// <summary>
	/// Backs out one level — `CloseParent`, which closes the highlighted row's PARENT and puts
	/// the cursor on it. False at the root, signalling that B is the guest's to answer.
	/// </summary>
	public bool Back() => Model.CloseParent();

	/// <summary>
	/// Puts the highlight on a named visible entry — for a menu whose INITIAL selection is the
	/// guest's own statement rather than the first row (the pause list opens on its second
	/// entry, `SetInitiallySelectedItem(2)`). No-op when the name is not currently visible.
	/// </summary>
	public void Highlight(string name)
	{
		for (int i = 0; i < Visible.Count; ++i)
		{
			if (Visible[i].Name == name)
			{
				Model.Highlight(i + 1);
				return;
			}
		}
	}

	/// <summary>
	/// Sets one root entry's availability and rebuilds. True when it changed — for lists whose
	/// availability is a live guest statement (the pause tabs follow the hero's restriction
	/// flags per frame) rather than a build-time condition.
	/// </summary>
	public bool SetAvailable(string name, bool available)
	{
		Item? item = Find(name);
		if (item is null || item.Available == available)
		{
			return false;
		}

		item.Available = available;
		Rebuild();
		return true;
	}

	/// <summary>
	/// Sets one entry's ghosting and rebuilds. True when it changed.
	///
	/// `SetItemGhosting` — the row stays in the list, drawn dim (heading opacity 40) and inert.
	/// The pause tabs arrive this way: `sub_8298F648` spawns all of them and then ghosts
	/// Map/Abilities/Items/Weapons/Clothing/Logbook from the hero's restriction bytes.
	/// </summary>
	public bool SetGhosted(string name, bool ghosted)
	{
		Item? item = Find(name);
		if (item is null || item.Ghosted == ghosted)
		{
			return false;
		}

		item.Ghosted = ghosted;
		Rebuild();
		return true;
	}

	/// <summary>
	/// The entry with this name, anywhere in the tree — the whole tree, not just the roots, or
	/// <see cref="SetAvailable"/> silently no-ops on every nested entry.
	/// </summary>
	public Item? Find(string name) => Model.Find(name);
}
