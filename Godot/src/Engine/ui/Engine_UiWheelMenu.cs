using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The quick wheel's model — `menu/menustateandconstants.lua`'s `g_Menu.State` and
/// `menu/menuhelpers.lua`'s predicates, translated.
///
/// `g_Menu` in the `menu/` package is this; `g_Menu` in the `expandablemenu/` package is
/// <see cref="Engine_UiExpandableMenu"/> — same global name, different object.
///
/// Folders descend by push, not by nesting: entering a folder replaces the ring with that
/// folder's contents and leaving puts the old ring back — `PushMenuState` / `PopMenuState`, one
/// saved menu, one level, no stack.
///
/// Items are cloned at runtime from `MenuItemTemplate` — see
/// <see cref="Engine_UiMenuCloning"/>.
/// </summary>
public sealed class Engine_UiWheelMenu
{
	/// <summary>
	/// The ring's items. The script's `MenuItems` (by component) and `SpawnedItems` (by spawn
	/// order) are one list here; nothing indexes by component.
	/// </summary>
	private readonly Engine_UiExpandableMenu _menu = new();

	/// <summary>The tree, cursor and push/pop — the shape is shared with the list.</summary>
	public Engine_UiExpandableMenu Items => _menu;

	/// <summary>`State.TimeElapsed` — how far into the current animation the ring is.</summary>
	public float TimeElapsed { get; private set; }

	/// <summary>`State.TimeSinceNav` — since the cursor last moved; the fade rides on it.</summary>
	public float TimeSinceNav { get; private set; }

	/// <summary>How deep into folders the ring currently is. 0 at the root.</summary>
	public int Depth { get; private set; }

	/// <summary>
	/// `g_Menu.ItemsToAnimate` — the items with an animation running, and which state each is in.
	/// On `g_Menu` itself, not on `g_Menu.State` (`menustateandconstants.dis main pc136..141`,
	/// where pc143..149 goes through `State` for `MenuItems`/`SpawnedItems`). A list rather than
	/// a per-item flag because several orbs move at once.
	/// </summary>
	private readonly List<Running> _animating = new();

	/// <summary>
	/// `g_Menu.ItemsToFade` — the second list `menustateandconstants.dis main pc139..141` puts on
	/// the wheel object. Separate from <see cref="Animating"/>: opacity runs off
	/// `Timing.OPACITY_FADE_SPEED` (`1 / ANIMATE_TIME`) against `State.TimeSinceNav`, not off a
	/// state machine's `percent_complete`.
	/// </summary>
	private readonly List<Engine_UiExpandableMenu.Item> _fading = new();

	/// <summary>One item mid-animation.</summary>
	public readonly record struct Running(
		Engine_UiExpandableMenu.Item Item,
		Engine_UiWheelConstants.AnimationState State,
		float Elapsed)
	{
		/// <summary>`percent_complete`, clamped so an overrun cannot lerp past the end.</summary>
		public float PercentComplete => Engine_UiWheelConstants.AnimateTime <= 0f
			? 1f
			: System.Math.Clamp(Elapsed / Engine_UiWheelConstants.AnimateTime, 0f, 1f);

		public bool Done => PercentComplete >= 1f;
	}

	/// <summary>Everything currently moving, for the drawing surface to apply.</summary>
	public IReadOnlyList<Running> Animating => _animating;

	/// <summary>`g_Menu.ItemsToFade` — the items whose opacity is being driven.</summary>
	public IReadOnlyList<Engine_UiExpandableMenu.Item> Fading => _fading;

	/// <summary>Puts an item on <see cref="Fading"/>, once.</summary>
	public void BeginFade(Engine_UiExpandableMenu.Item item)
	{
		if (!_fading.Contains(item))
		{
			_fading.Add(item);
		}
	}

	/// <summary>Takes an item off <see cref="Fading"/>.</summary>
	public bool EndFade(Engine_UiExpandableMenu.Item item) => _fading.Remove(item);

	/// <summary>
	/// `IsAFolder(item)` — the item's `MenuItemType` is `FOLDER`. Off the type, not off having
	/// children: an empty folder is still enterable.
	/// </summary>
	public static bool IsAFolder(Engine_UiExpandableMenu.Item item) =>
		item.EventType == (int)Engine_UiWheelConstants.ItemType.Folder;

	/// <summary>
	/// `IsAButton(item)` — the six-way `or` over `MenuItemType`, stated once in
	/// <see cref="Engine_UiWheelLib.IsAButton(int)"/> from `menuhelpers.dis main.5`. Not
	/// "anything that is not a folder": SPELLS_BUTTON, the world-map rows and the navigation
	/// triple all answer false.
	/// </summary>
	public static bool IsAButton(Engine_UiExpandableMenu.Item item) =>
		Engine_UiWheelLib.IsAButton(item.EventType);

	/// <summary>`IsContainedInAFolder(item)` — its parent is a folder rather than the root.</summary>
	public static bool IsContainedInAFolder(Engine_UiExpandableMenu.Item item) =>
		item.Parent is { } parent && IsAFolder(parent);

	/// <summary>`ObserveIfAtRoot()` — the ring showing is the top one.</summary>
	public bool ObserveIfAtRoot() => Depth == 0;

	/// <summary>`GetFirstItemWithName(name)`.</summary>
	public Engine_UiExpandableMenu.Item? GetFirstItemWithName(string name) => _menu.Find(name);

	/// <summary>
	/// Moves the cursor. Two animations per move — the wheel slides no window, so a move is
	/// `HighlightedToNormal` on the orb left and `NormalToHighlighted` on the one arrived at.
	/// </summary>
	public bool Navigate(int direction)
	{
		if (direction == 0 || _menu.Visible.Count == 0)
		{
			return false;
		}

		Engine_UiExpandableMenu.Item? leaving = _menu.Highlighted;
		bool moved = direction < 0 ? _menu.ScrollUp() : _menu.ScrollDown();
		if (!moved)
		{
			return false;
		}

		TimeSinceNav = 0f;

		if (leaving is { } was)
		{
			Begin(was, Engine_UiWheelConstants.AnimationState.HighlightedToNormal);
		}

		if (_menu.Highlighted is { } arrived)
		{
			Begin(arrived, Engine_UiWheelConstants.AnimationState.NormalToHighlighted);
		}

		return true;
	}

	/// <summary>
	/// The press: a folder descends via `PushMenuState` (the ring is saved whole, the folder's
	/// items are the guest's to spawn); anything else fires.
	/// </summary>
	public Engine_UiExpandableMenu.Fired? Select()
	{
		if (_menu.Highlighted is not { } item || item.IsGhosted)
		{
			return null;
		}

		Begin(item, Engine_UiWheelConstants.AnimationState.Pressed);

		if (!IsAFolder(item))
		{
			return _menu.Select();
		}

		Begin(item, Engine_UiWheelConstants.AnimationState.HighlightedToMaximised);
		_menu.PushMenuState();
		++Depth;
		return null;
	}

	/// <summary>
	/// Backs out of a folder — `PopMenuState`. False at the root, which is the caller's signal
	/// that B means "close the wheel".
	/// </summary>
	public bool Back()
	{
		if (Depth == 0 || !_menu.PopMenuState())
		{
			return false;
		}

		--Depth;
		if (_menu.Highlighted is { } restored)
		{
			Begin(restored, Engine_UiWheelConstants.AnimationState.MaximisedToHighlighted);
		}

		return true;
	}

	/// <summary>Stands a fresh ring up — `STATE_ROOT_INTRO` on every item at once.</summary>
	public void Intro()
	{
		_animating.Clear();
		TimeElapsed = 0f;
		TimeSinceNav = 0f;
		foreach (Engine_UiExpandableMenu.Item item in _menu.Visible)
		{
			Begin(item, Engine_UiWheelConstants.AnimationState.RootIntro);
		}
	}

	/// <summary>Advances every running animation, dropping the ones that have finished.</summary>
	public void Tick(double delta)
	{
		TimeElapsed += (float)delta;
		TimeSinceNav += (float)delta;

		for (int i = _animating.Count - 1; i >= 0; --i)
		{
			Running running = _animating[i] with { Elapsed = _animating[i].Elapsed + (float)delta };
			if (running.Done)
			{
				// Hold the finished entry for one more pass at t = 1 so the drawing surface,
				// which reads `Animating` after this, writes the end pose.
				_animating[i] = running with { Elapsed = Engine_UiWheelConstants.AnimateTime };
				if (_finished.Add(running.Item))
				{
					continue;
				}

				_animating.RemoveAt(i);
				_finished.Remove(running.Item);
				continue;
			}

			_animating[i] = running;
		}
	}

	private readonly HashSet<Engine_UiExpandableMenu.Item> _finished = new();

	/// <summary>Starts a state on an item, replacing whatever it was doing.</summary>
	private void Begin(
		Engine_UiExpandableMenu.Item item, Engine_UiWheelConstants.AnimationState state)
	{
		for (int i = 0; i < _animating.Count; ++i)
		{
			if (ReferenceEquals(_animating[i].Item, item))
			{
				_animating[i] = new Running(item, state, 0f);
				_finished.Remove(item);
				return;
			}
		}

		_animating.Add(new Running(item, state, 0f));
	}

	public string Describe() =>
		$"{_menu.Visible.Count} orb(s), cursor {_menu.CurrentHighlightIdx}, depth {Depth}, " +
		$"{_animating.Count} animating";
}
