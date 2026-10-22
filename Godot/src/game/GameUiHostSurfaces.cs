using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// THE HOST'S OWN SURFACES WITH NOTHING OF THE GUEST'S UNDER THEM — a host box out of game, and a
/// mod's menu opened with no list on screen. Both are the host's end to end: the guest has no box
/// and no menu to answer, so the whole pad is the host's while either stands
/// (<see cref="GameInput.HostUiOwnsPad"/>).
/// </summary>
public partial class GameUi
{
	/// <summary>What the front end's dialog shows for a host box, or null while it shows none.</summary>
	private string? _hostBoxShown;

	private bool _menuHiddenForHostBox;

	private bool _hostMenuHiddenForHostBox;

	/// <summary>The list a mod's menu stands on when no list was up to open it over.</summary>
	private Engine_FrontEndMenuView? _hostMenuView;

	/// <summary>The menu the standalone list was opened on: its root's name.</summary>
	private string _hostMenuRoot = string.Empty;

	/// <summary>
	/// A HOST BOX OUT OF GAME (<see cref="Engine_UiHostBoxes"/>: a mod script's message box, a
	/// host question) in the front end's own dialog — the shipped <c>dialog</c> page the guest's
	/// refusals and the host's own warnings are drawn with (<see cref="Engine_FrontEndWarning"/>),
	/// on the title as on the main menu. In game the same boxes are the game's query and message
	/// boxes (Engine_UiQuerySurface / Engine_UiMessageSurface). The guest's own dialog, and a
	/// refusal the front end raised itself, stand first; the host box waits for them.
	/// </summary>
	private bool DriveFrontEndHostBox(double delta)
	{
		bool question = Engine_UiHostBoxes.Question is not null;
		string? body = Engine_UiHostBoxes.Question ?? Engine_UiHostBoxes.Notice;
		bool up = body is not null && !HostBridge.IsInGame && IsRevealed && _presentation?.Theme is not null &&
			!_guestDialogShown && Engine_UiSource.Current.GetFrontEndDialog() is null &&
			(_hostBoxShown is not null || _warningView is not { Visible: true });
		if (!up)
		{
			CloseFrontEndHostBox();
			return false;
		}

		// A QUESTION ON A PAGE: the host page book instead of the dialog — the body stands in
		// the book's scroll region, clipped and scrolled as every text page is, with Continue
		// and Back as its rows. The load fan and the row list go under it as they do under the
		// dialog, and come back when it is answered.
		if (question && Engine_UiHostBoxes.PageTitle is not null)
		{
			return DriveFrontEndHostPage(delta, body!);
		}

		if (_hostPageBook.IsOpen)
		{
			// The page question was replaced by a dialog one: the book goes before the dialog stands.
			_hostPageBook.Close();
		}

		if (_warningView is null)
		{
			AdoptWarning(_presentation!.Theme!);
		}

		if (_warningView is null)
		{
			return false;
		}

		string? accept = question ? Engine_UiHostBoxes.AcceptCaption : null;
		string? cancel = question ? Engine_UiHostBoxes.CancelCaption : null;
		string signature = $"{(question ? "Q" : "N")}\u0001{body}\u0001{accept}\u0001{cancel}";
		if (signature != _hostBoxShown)
		{
			_hostBoxShown = signature;
			_warningView.Show(_presentation!.Theme, body!, accept ?? "GUI_OK", twoButton: question, cancelTag: cancel);
			_warningView.AcceptOpens = null;
			GD.Print($"[game-ui] host {(question ? "question" : "notice")} on the front end — '{body}' — " +
				_warningView.LastReport);
		}

		if (_menuView is { Visible: true })
		{
			_menuView.Visible = false;
			_menuHiddenForHostBox = true;
		}

		// A mod's menu standing on its own goes under the box with the rest of the input.
		if (_hostMenuView is { Visible: true } && GodotObject.IsInstanceValid(_hostMenuView))
		{
			_hostMenuView.Visible = false;
			_hostMenuHiddenForHostBox = true;
		}

		GameInput.HostUiOwnsPad = true;
		int clicked = _warningView.Click();
		if (GameInput.MenuAcceptPressed || clicked > 0)
		{
			if (question)
			{
				Engine_UiHostBoxes.Answer(true);
			}
			else
			{
				Engine_UiHostBoxes.Dismiss();
			}
		}
		else if (GameInput.MenuBackPressed || clicked < 0)
		{
			// B answers a question no; a notice has one button, and B clears it as it clears the
			// front end's own one-button warning — a box only one key could close is a deadlock.
			if (question)
			{
				Engine_UiHostBoxes.Answer(false);
			}
			else
			{
				Engine_UiHostBoxes.Dismiss();
			}
		}

		return true;
	}

	/// <summary>
	/// One frame of a host question on the page book. The book is opened on the first frame the
	/// question stands (false until its art is read — "come back next frame", as the Active Mods
	/// book is opened); A on a row dispatches the row's command, which answers the question; B
	/// closes the book, which is the question's no.
	/// </summary>
	private bool DriveFrontEndHostPage(double delta, string body)
	{
		string signature = $"P\u0001{Engine_UiHostBoxes.PageTitle}\u0001{body}";
		if (!_hostPageBook.IsOpen && !_hostPageBook.Open())
		{
			if (signature != _hostBoxShown)
			{
				_hostBoxShown = signature;
				GD.PushWarning("[game-ui] host page question on the front end — book art not read yet, waiting");
			}

			GameInput.HostUiOwnsPad = true;
			return true;
		}

		if (signature != _hostBoxShown)
		{
			_hostBoxShown = signature;
			GD.Print($"[game-ui] host question on the front end's page book — '{Engine_UiHostBoxes.PageTitle}'");
		}

		if (_menuView is { Visible: true })
		{
			_menuView.Visible = false;
			_menuHiddenForHostBox = true;
		}

		if (_saveCardView is { Visible: true } && GodotObject.IsInstanceValid(_saveCardView))
		{
			_saveCardView.Visible = false;
			_fanHiddenForHostBox = true;
		}

		if (_hostMenuView is { Visible: true } && GodotObject.IsInstanceValid(_hostMenuView))
		{
			_hostMenuView.Visible = false;
			_hostMenuHiddenForHostBox = true;
		}

		GameInput.HostUiOwnsPad = true;
		PointerPage(_hostPageBook.Book);
		if (_hostPageBook.Drive(
				GameInput.MenuDirection,
				GameInput.MenuAcceptPressed || PointerList(_hostPageBook.View),
				GameInput.MenuBackPressed,
				false, delta, 0f))
		{
			ReportMenuView(_hostPageBook.View);
		}

		// B closed the book without a row: the question's no.
		if (!_hostPageBook.IsOpen && Engine_UiHostBoxes.PageTitle is not null)
		{
			Engine_UiHostBoxes.Answer(false);
		}

		return true;
	}

	/// <summary>The load fan was hidden under a host page question; it comes back with the answer.</summary>
	private bool _fanHiddenForHostBox;

	private void CloseFrontEndHostBox()
	{
		if (_hostBoxShown is null)
		{
			return;
		}

		_hostBoxShown = null;
		if (_warningView is not null && GodotObject.IsInstanceValid(_warningView))
		{
			_warningView.Visible = false;
		}

		if (_hostPageBook.IsOpen)
		{
			_hostPageBook.Close();
		}

		if (_menuHiddenForHostBox && _menuView is not null && GodotObject.IsInstanceValid(_menuView))
		{
			_menuView.Visible = true;
		}

		_menuHiddenForHostBox = false;
		if (_fanHiddenForHostBox && _saveCardView is not null && GodotObject.IsInstanceValid(_saveCardView))
		{
			_saveCardView.Visible = true;
		}

		_fanHiddenForHostBox = false;
		if (_hostMenuHiddenForHostBox && _hostMenuView is not null && GodotObject.IsInstanceValid(_hostMenuView))
		{
			_hostMenuView.Visible = true;
		}

		_hostMenuHiddenForHostBox = false;
	}

	/// <summary>
	/// A MOD'S MENU WITH NO LIST UNDER IT (<see cref="Engine_UiModMenus.TakeStandalone"/>): asked for
	/// by a script while nothing was on screen — in play, or on the title — it stands as a list of
	/// its own, dressed as the list it would have opened over (the pause book's in game, the front
	/// end's out of it), and is driven as every list is: the stick moves it, A runs the row, B folds
	/// or closes a menu opened over it, and B at its root closes it.
	/// </summary>
	private bool DriveHostModMenu(double delta)
	{
		if (Engine_UiModMenus.TakeStandalone() is { } request)
		{
			OpenHostModMenu(request);
		}

		if (_hostMenuView is null)
		{
			return false;
		}

		if (!GodotObject.IsInstanceValid(_hostMenuView) || !IsRevealed || _presentation is null)
		{
			CloseHostModMenu();
			return false;
		}

		GameInput.HostUiOwnsPad = true;
		Engine_UiModMenus.Focus(_hostMenuView, _hostMenuRoot, Engine_UiCommandList.HostList);
		_hostMenuView.Navigate(GameInput.MenuDirection, delta);
		bool clicked = PointerList(_hostMenuView);
		ReportMenuView(_hostMenuView);

		if (GameInput.MenuBackPressed)
		{
			if (!_hostMenuView.Back() && !Engine_UiModMenus.Back(_hostMenuView))
			{
				CloseHostModMenu();
			}

			return true;
		}

		if (!GameInput.MenuAcceptPressed && !clicked)
		{
			return true;
		}

		Engine_UiExpandableMenu.Item? chosen = _hostMenuView.Menu?.Highlighted;
		string? action = _hostMenuView.Activate();
		Engine_UiModMenus.Activated(_hostMenuView, _hostMenuRoot, chosen, action, Engine_UiCommandList.HostList,
			rootIsModMenu: true);
		if (_hostMenuView is not null && GodotObject.IsInstanceValid(_hostMenuView))
		{
			// An entry naming a SCREEN latches it for the front end; there is none under this list.
			_hostMenuView.TakeOpenedScreen();
			_hostMenuView.Refresh();
		}

		return true;
	}

	private void OpenHostModMenu(Engine_UiModMenus.Standalone request)
	{
		if (_presentation is null || Engine_UiTemplates.Menu(request.Menu) is not { } template)
		{
			return;
		}

		CloseHostModMenu();
		bool inGame = HostBridge.IsInGame;
		string presentation = inGame ? PausePresentation : FrontEndMenuPresentation;
		Engine_FrontEndMenuView view = Engine_FrontEndMenuView.Dressed(
			"HostModMenu", _presentation.PatternBookFor(presentation), bookLook: inGame,
			_presentation.DocumentFor(presentation), _presentation.Theme);

		// Over the HUD and every mirrored slab, under the boxes a row may raise.
		view.Anchor = new Vector3(view.Anchor.X, view.Anchor.Y, Engine_UiSurface<Engine_UiMenuBox>.PanelDepth);
		(inGame ? _presentation.MenuLayer : (Node)_presentation).AddChild(view);
		view.Bind(template.Build());
		_hostMenuView = view;
		_hostMenuRoot = request.Menu;
		Engine_UiModMenus.StandaloneOpened(view, request.Menu, request.Player);
	}

	private void CloseHostModMenu()
	{
		if (_hostMenuView is null)
		{
			return;
		}

		if (GodotObject.IsInstanceValid(_hostMenuView))
		{
			_hostMenuView.QueueFree();
		}

		_hostMenuView = null;
		Engine_UiModMenus.StandaloneClosed(_hostMenuRoot);
		_hostMenuRoot = string.Empty;
	}
}
