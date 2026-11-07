using Godot;
using Refii.Core;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// ROW ACTIONS DRIVEN (<see cref="Engine_UiRowActions"/>): while a screen list whose menu template has
/// actions is up with a leaf row highlighted, the first action's prompt is drawn beside the page
/// buttons, every action's button is held from the guest, and the press — or a click on the prompt —
/// raises <c>mod_menu_row</c> with the list's template name, the row, its own command and the button,
/// then runs the action's command as a mod menu row's is run.
/// </summary>
public partial class GameUi
{
	private Engine_UiRowActionPrompt? _rowActionPrompt;
	private Engine_UiOwnedBook? _rowActionBook;
	private readonly Node3D?[] _rowActionAnchors = new Node3D?[2];
	private readonly bool[] _rowActionAnchorBelow = new bool[2];
	private readonly Node3D?[] _rowActionNativePrompts = new Node3D?[2];
	private readonly bool[] _rowActionNativeHidden = new bool[2];
	private string _rowActionNativeButton = string.Empty;

	/// <summary>Once a frame, first: the prompt hides and a native prompt it stood in for shows, until a drive shows it again.</summary>
	private void ResetRowActionPrompt()
	{
		_rowActionPrompt?.Hide();
		for (int i = 0; i < 2; i++)
		{
			if (_rowActionNativeHidden[i] && _rowActionNativePrompts[i] is { } native && IsInstanceValid(native))
			{
				native.Show();
				_rowActionNativeHidden[i] = false;
			}
		}
	}

	/// <summary>
	/// The row actions of the screen list up, on its highlighted row. True when one ran (the frame is
	/// taken); false lets the list's own drive go on.
	/// </summary>
	private bool DriveRowActions(uint screen) =>
		_screenMenu is { } menu &&
		DriveRowActionsOn(menu.PopulationScript, menu.Script.Highlighted, _screenMenuView, screen,
			Engine_UiCommandList.Screen);

	/// <summary>
	/// A list's ROW ACTIONS (ui/templates.json "rowActions", keyed by the menu's template name):
	/// the prompt beside the page buttons, X or Y pressed on the highlighted row. Every list runs
	/// this — a screen list, the pause list, a mod's menu opened over either, a mod's standalone
	/// menu — so a row action is one mechanism wherever its menu stands. The buttons it binds are
	/// withheld from the guest while the list stands (<see cref="Fable2Input.RowActionHolds"/>).
	/// </summary>
	private bool DriveRowActionsOn(
		string menuName, Engine_UiExpandableMenu.Item? row, Engine_FrontEndMenuView? view, uint screen,
		Engine_UiCommandList list)
	{
		if (menuName.Length == 0)
		{
			return false;
		}

		var actions = Engine_UiTemplates.RowActionsFor(menuName);
		if (actions.Count == 0)
		{
			return false;
		}

		if (row is null || row.IsGhosted || row.Children.Count != 0 ||
			row.EventType == (int)Engine_UiMenuConstants.EventType.Submenu)
		{
			return false;
		}

		foreach (Engine_UiRowActions.RowAction bound in actions)
		{
			Fable2Input.RowActionHolds |= bound.Button == "X" ? Fable2Input.PadButtonX : Fable2Input.PadButtonY;
		}

		int player = Engine_UiModMenus.MenuPlayer;
		Engine_UiRowActions.RowAction shown = actions[0];
		bool clicked = ShowRowActionPrompt(shown, player, screen);
		Engine_UiRowActions.RowAction? pressed = null;
		if (clicked)
		{
			pressed = shown;
		}
		else
		{
			foreach (Engine_UiRowActions.RowAction action in actions)
			{
				if ((action.Button == "X" && Fable2Input.MenuEquipPressed) || (action.Button == "Y" && Fable2Input.MenuFlipPressed))
				{
					pressed = action;
					break;
				}
			}
		}

		if (pressed is not { } chosen)
		{
			return false;
		}

		string rowCommand = RowOwnCommand(row);
		if (Engine_UiModMenus.RaiseRow(menuName, row.Name, player, chosen.Mod, rowCommand, chosen.Button).Cancelled)
		{
			return true;
		}

		var context = new Engine_UiCommandContext(list, menuName, row.Name, player, chosen.Mod)
		{
			Command = chosen.Command, RowCommand = rowCommand, Button = chosen.Button, View = view,
		};
		if (Engine_UiCommands.Run(chosen.Command, context).Outcome == Engine_UiCommandOutcome.NotOurs)
		{
			GD.Print($"[row-actions] '{menuName}/{row.Name}' {chosen.Button} names '{chosen.Command}', which no verb runs");
		}

		_rowActionPrompt?.Hide();
		return true;
	}

	/// <summary>
	/// The highlighted row's OWN command, as the event states it: the row's command string
	/// (<c>Inventory:use:1234</c>, <c>Inventory:equip:…</c>, <c>Ability:learn:9</c>), or for a logbook
	/// expression row — the guest spawns those with no command — the expression id.
	/// </summary>
	private static string RowOwnCommand(Engine_UiExpandableMenu.Item row)
	{
		if (row.EventType == (int)Engine_UiMenuConstants.EventType.LogbookExpressionItem &&
			row.IdLow is >= 0 and <= 65535 && row.IdHigh is >= 0 and <= 65535)
		{
			return (((uint)row.IdHigh << 16) | (uint)row.IdLow).ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		return row.Command ?? string.Empty;
	}

	/// <summary>Draws one action's prompt for <paramref name="player"/>; true when it was clicked.</summary>
	private bool ShowRowActionPrompt(Engine_UiRowActions.RowAction action, int player, uint screen)
	{
		if (_presentation?.Theme is not { } theme)
		{
			return false;
		}

		if (_rowActionPrompt is null || !IsInstanceValid(_rowActionPrompt))
		{
			_rowActionPrompt = new Engine_UiRowActionPrompt { Name = "RowAction" };
			_presentation.AddChild(_rowActionPrompt);
		}

		Engine_UiOwnedBook? book = screen == 0 ? null : _books.Book(screen);
		if (!ReferenceEquals(book, _rowActionBook) || _rowActionNativeButton != action.Button)
		{
			_rowActionBook = book;
			_rowActionNativeButton = action.Button;
			for (int i = 0; i < 2; i++)
			{
				string page = $"Foreground.Book.Pages.Page{i + 1}.Contents.";
				Node3D? beside = book?.NodeFor(page + "PageButtons.A");
				_rowActionAnchors[i] = beside ?? book?.NodeFor(page + "Buttons.Buttons.Learn");
				_rowActionAnchorBelow[i] = beside is null;
				// The shipped prompt for the same button stands aside while ours speaks for it.
				_rowActionNativePrompts[i] = book?.NodeFor(page + "PageButtons." + action.Button);
			}
		}

		for (int i = 0; i < 2; i++)
		{
			if (_rowActionNativePrompts[i] is { } native && IsInstanceValid(native) && native.Visible)
			{
				_rowActionNativeHidden[i] = true;
				native.Hide();
			}
		}

		Node3D? anchor = null;
		bool below = false;
		for (int i = 0; i < 2; i++)
		{
			if (_rowActionAnchors[i] is { } candidate && IsInstanceValid(candidate) && candidate.IsVisibleInTree())
			{
				anchor = candidate;
				below = _rowActionAnchorBelow[i];
				break;
			}
		}

		_rowActionPrompt.SetState(theme, action.Button, RowActionCaption(action.Tag), onPage: screen != 0);
		if (anchor is not null)
		{
			_rowActionPrompt.PlaceAt(anchor, below);
		}
		else
		{
			var frame = Engine_UiSeats.Of(GetViewport().GetVisibleRect().Size, Engine_UiSeats.For(player, Engine_Views.Split));
			Vector2 at = frame.ToCanvas(new Vector2(frame.Extent.X * 0.36f, -frame.Extent.Y * 0.5f + 110));
			_rowActionPrompt.Position = new Vector3(at.X, at.Y, 8500);
			_rowActionPrompt.Scale = new Vector3(frame.Scale, frame.Scale, 1);
		}

		return _rowActionPrompt.Clicked();
	}

	/// <summary>The prompt's caption: the tag's Babel text when the string book has it, else the tag itself.</summary>
	private static string RowActionCaption(string tag) =>
		Engine_UiInventorySource.Strings is { } strings && tag.Length != 0 && strings.TryGetString(tag, out string text) && text.Length != 0
			? text
			: tag;
}
