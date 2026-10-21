using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// A MOD'S MENU BOX: <c>menu_box</c> on every surface (<c>RefiiHost.menu_box</c>,
/// <c>Refii.menu_box</c>, <c>refii_mod_menu_box</c>, and <c>RefiiHost.message_box</c> with more than
/// two buttons), drawn in the game's own menu box art — the box the Golden Cock's flow uses — on the
/// front end and in game. One stands at a time: a new one replaces it, and the replaced one is
/// answered -1.
/// </summary>
/// <remarks>
/// More options than the box has rows are paged (<see cref="Engine_UiMenuBoxDialog.Page"/>):
/// "Next page" and "Previous page" rows step through them; the answer is always the option's index
/// in the list the mod gave. B on a cancelable box answers -1.
/// </remarks>
public sealed class Engine_UiModMenuBox : Engine_UiMenuBoxDialog
{
	/// <summary>The rows the box art holds.</summary>
	public static int RowsPerPage => Engine_UiMenuBox.MaxOptions;

	public const string NextPageLabel = "Next page";
	public const string PreviousPageLabel = "Previous page";

	/// <summary>The mod menu box up, or null.</summary>
	public static Engine_UiModMenuBox? Active { get; private set; }

	public uint Id { get; }

	/// <summary>Whose box it is: 1 or 2.</summary>
	public int Player { get; }

	private readonly string _body;
	private readonly IReadOnlyList<string> _options;
	private readonly bool _cancelable;
	private Action<int>? _answered;
	private int _page;

	private Engine_UiModMenuBox(uint id, int player, string body, IReadOnlyList<string> options, bool cancelable, Action<int>? answered)
	{
		Id = id;
		Player = player;
		_body = body;
		_options = options;
		_cancelable = cancelable;
		_answered = answered;
	}

	/// <summary>
	/// Puts up a menu box for <paramref name="player"/> (1 or 2): <paramref name="body"/> above
	/// <paramref name="options"/>; <paramref name="answered"/> gets the chosen option's index, or -1
	/// for B on a cancelable box or when another box replaced it.
	/// </summary>
	public static Engine_UiModMenuBox Open(
		uint id, int player, string body, IReadOnlyList<string> options, bool cancelable, Action<int>? answered)
	{
		Active?.Answer(-1);
		var box = new Engine_UiModMenuBox(id, player, body, options, cancelable, answered) { IsOpen = true };
		Active = box;
		box.ShowPageOf(0, 0);
		return box;
	}

	/// <summary>Takes the box down unanswered by a choice: -1.</summary>
	public void Cancel() => Answer(-1);

	private void ShowPageOf(int page, int highlight)
	{
		_page = page;
		ShowPage(_body, _cancelable ? Cancel : null, highlight);
		List<Row> rows = Page(_options.Count, page, RowsPerPage);
		foreach (Row row in rows)
		{
			if (row.Next)
			{
				AddChoice(NextPageLabel, () => ShowPageOf(_page + 1, 0));
			}
			else if (row.Previous)
			{
				AddChoice(PreviousPageLabel, () => ShowPageOf(_page - 1, 0));
			}
			else
			{
				int option = row.Option;
				AddChoice(_options[option], () => Answer(option));
			}
		}
	}

	private void Answer(int option)
	{
		Action<int>? answered = _answered;
		_answered = null;
		ClearPages();
		if (ReferenceEquals(Active, this))
		{
			Active = null;
		}

		answered?.Invoke(option);
	}
}
