using System;
using System.Collections.Generic;
using static Refii.Ui.Engine_UiGuiBoxes;

namespace Refii.Ui;

/// <summary>
/// A HOST MENU BOX: a question and its options in the game's own menu box art
/// (<see cref="Engine_UiMenuBox"/>), owned and answered by host code — the Golden Cock's flow
/// (<see cref="Engine_NewGameDialog"/>) and a mod's <c>menu_box</c> (<see cref="Engine_UiModMenuBox"/>).
/// One page at a time: a text, its choices (each with what A on it does), and what B does.
/// </summary>
/// <remarks>
/// The drive is the guest's menu box's: up and down move the highlight and wrap, A runs the
/// highlighted choice when it is enabled, B runs the page's back when it has one, a click picks a
/// row; nothing is taken for 0.2 s after a page opens or a step, so one press is one step. Pure
/// .NET, held by tests.
/// </remarks>
public class Engine_UiMenuBoxDialog
{
	private readonly List<Action> _actions = new();
	private readonly List<MenuOption> _options = new();
	private Action? _back;
	private double _inputDelay;

	/// <summary>The seconds after a page opens or a step before the next input is taken.</summary>
	public const double StepDelay = 0.2;

	public bool IsOpen { get; protected set; }

	/// <summary>The page up: its text, its options and its highlight; null between pages.</summary>
	public MenuBoxModel? Menu { get; private set; }

	/// <summary>Called first every <see cref="Drive"/>; true takes the frame (a page that waits on work).</summary>
	protected virtual bool Poll() => false;

	public void Drive(double delta, int direction, bool accept, bool back, int pointed = -1, bool clicked = false)
	{
		if (!IsOpen || Poll())
		{
			return;
		}

		_inputDelay -= delta;
		if (_inputDelay > 0 || Menu is null)
		{
			return;
		}

		if (back && Menu.Cancelable)
		{
			_back?.Invoke();
			return;
		}

		if (pointed >= 0 && pointed < _options.Count)
		{
			Menu.Highlight = pointed;
		}

		if (direction != 0 && _options.Count > 0)
		{
			Menu.Highlight = (Menu.Highlight + Math.Sign(direction) + _options.Count) % _options.Count;
			_inputDelay = StepDelay;
			return;
		}

		if ((accept || clicked && pointed >= 0 && pointed < _options.Count) &&
			_options.Count > 0 && _options[Menu.Highlight].Enabled)
		{
			_inputDelay = StepDelay;
			_actions[Menu.Highlight]();
		}
	}

	/// <summary>Starts a page: <paramref name="text"/>, and what B does (null: B does nothing).</summary>
	protected void ShowPage(string text, Action? back, int highlight = 0)
	{
		_actions.Clear();
		_options.Clear();
		Menu = new MenuBoxModel(text, _options, back is not null) { Highlight = Math.Max(0, highlight) };
		_inputDelay = StepDelay;
		_back = back;
	}

	/// <summary>A choice on the page started last.</summary>
	protected void AddChoice(string text, Action action, bool enabled = true)
	{
		_options.Add(new MenuOption(text, enabled));
		_actions.Add(action);
		if (Menu is { } menu && menu.Highlight >= _options.Count)
		{
			menu.Highlight = 0;
		}
	}

	/// <summary>Takes the box down and forgets its page.</summary>
	protected void ClearPages()
	{
		IsOpen = false;
		Menu = null;
		_back = null;
		_actions.Clear();
		_options.Clear();
	}

	/// <summary>One row of a paged list: an option by its index, or a step to the next or previous page.</summary>
	public readonly record struct Row(int Option, bool Next, bool Previous)
	{
		public static Row OptionAt(int index) => new(index, false, false);
		public static readonly Row NextPage = new(-1, true, false);
		public static readonly Row PreviousPage = new(-1, false, true);
	}

	/// <summary>
	/// The rows of page <paramref name="page"/> of <paramref name="count"/> options in a box of
	/// <paramref name="perPage"/> rows. Every option fits on one page when it can; otherwise each
	/// page gives a row to "Previous page" (all but the first) and to "Next page" (all but the
	/// last), and the options fill the rest in order. <paramref name="page"/> is clamped.
	/// </summary>
	public static List<Row> Page(int count, int page, int perPage)
	{
		var rows = new List<Row>();
		if (count <= 0 || perPage <= 0)
		{
			return rows;
		}

		if (count <= perPage || perPage < 3)
		{
			for (int i = 0; i < Math.Min(count, perPage); i++)
			{
				rows.Add(Row.OptionAt(i));
			}

			return rows;
		}

		int pages = PageCount(count, perPage);
		page = Math.Clamp(page, 0, pages - 1);
		int first = FirstOn(page, perPage);
		bool hasNext = page < pages - 1;
		int room = perPage - (page > 0 ? 1 : 0) - (hasNext ? 1 : 0);
		if (page > 0)
		{
			rows.Add(Row.PreviousPage);
		}

		for (int i = first; i < Math.Min(count, first + room); i++)
		{
			rows.Add(Row.OptionAt(i));
		}

		if (hasNext)
		{
			rows.Add(Row.NextPage);
		}

		return rows;
	}

	/// <summary>How many pages <see cref="Page"/> spreads <paramref name="count"/> options over.</summary>
	public static int PageCount(int count, int perPage)
	{
		if (count <= perPage || perPage < 3)
		{
			return 1;
		}

		// The first page holds perPage - 1 (a Next row), a middle page perPage - 2, the last perPage - 1.
		int left = count - (perPage - 1);
		int pages = 1;
		while (left > perPage - 1)
		{
			left -= perPage - 2;
			++pages;
		}

		return pages + 1;
	}

	/// <summary>The first option on <paramref name="page"/> (the paged case).</summary>
	private static int FirstOn(int page, int perPage) => page == 0 ? 0 : (perPage - 1) + (page - 1) * (perPage - 2);
}
