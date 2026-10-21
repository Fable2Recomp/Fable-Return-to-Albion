using System.Collections.Generic;
using Godot;
using Refii.Host;
using Refii.Ui;

namespace Refii.Game;

public partial class GameUi
{
	/// <summary>The front end's presentation: the menu, the gender card, the load fan and the dialog are its pages.</summary>
	private const string FrontEndMenuPresentation = "frontendmainmenu";

	/// <summary>
	/// THE MOUNT CHANGED A PRESENTATION'S FILES (<see cref="Engine_UiPresentationHost.PatternBooksRetired"/>):
	/// a mod's <c>.bgf</c> now stands in for one a view was built from, or no longer does. Every
	/// view, book and theme built from a retired pattern book is dropped here — the host frees the
	/// retired trees at the end of this frame — and each is built again from the new book where it
	/// is next needed, by the same code that built it first. The ones that hold the book itself
	/// (owned books, the HUD's art, the spell widgets, the info panel, the co-op fan's pattern) must
	/// let go of it now. An OPEN list — a screen's list, a mod's menu standing on its own — is
	/// dressed again from the new book at once, keeping its rows, its highlight and every menu
	/// opened over it. The HUD derives its layout again from the new document (a mod that changed
	/// `hud.bgf`'s structure changes the pieces), and player two's view and the widgets follow it.
	/// </summary>
	private void OnPatternBooksRetired(IReadOnlyList<string> names)
	{
		var retired = new HashSet<string>(names, System.StringComparer.OrdinalIgnoreCase);
		int books = _books.Retire(retired);
		_spells.Retire(retired);
		_infoPanel.Retire();

		// Host screens rebuild from the records and art as they stand now, on their next open.
		ForgetHostScreens();

		if (retired.Contains("coopscreen"))
		{
			_coopPattern = null;
		}

		if (retired.Contains(HudPresentation) && _presentation is not null && _hud is not null &&
			GodotObject.IsInstanceValid(_hud))
		{
			// One art object serves both players' HUDs; the world icons clone from the book itself.
			Engine_UiTheme? art = _presentation.PatternBookFor(HudPresentation);
			_hud.Art.Shipped = art;
			foreach (Engine_HudWorldIcons? icons in new[] { _worldIcons, _worldIconsTwo })
			{
				if (icons is not null && GodotObject.IsInstanceValid(icons))
				{
					icons.Art = art;
				}
			}

			// Derived again from the new document (Engine_HudView.Derive), player two's view and
			// the widgets after it (OnHudRebuilt).
			_hud.Build();
		}

		RedressOpenLists(retired);

		if (retired.Contains(FrontEndMenuPresentation))
		{
			// Re-adopted by AdoptMenu on the front end's next build, as after LeaveFrontEnd.
			FreeView(ref _menuView);
			FreeView(ref _cardView);
			FreeView(ref _saveCardView);
			FreeView(ref _warningView);
			_guestDialogShownText = null;
			_hostBoxShown = null;
		}

		if (retired.Contains(PausePresentation))
		{
			FreeView(ref _pauseView);
			_pauseBook = null;
		}

		if (retired.Contains(CyclerPresentation))
		{
			FreeView(ref _pauseBanner);
		}

		if (retired.Contains(Engine_UiMenuBox.Presentation))
		{
			FreeView(ref _goldenCockView);
			_goldenCockPage = null;
		}

		GD.Print($"[game-ui] the mount changed {string.Join(", ", names)} — {books} owned book(s) and the views " +
			"built from them go, and are built again from the new files");
	}

	/// <summary>The presentation the open screen list was dressed from (<see cref="AdoptScreenMenu"/>).</summary>
	private string _screenMenuPresentation = string.Empty;

	/// <summary>The screen list's cursor at the book's authored <c>MenuHighlight</c>, relative to the list's anchor.</summary>
	private Transform3D? ScreenMenuCursorPose(Engine_UiTheme? book)
	{
		if (book?.ArtSourceNode("MenuHighlight") is not { } highlight)
		{
			return null;
		}

		Transform3D authored = Engine_UiTheme.CanvasTransform(highlight);
		authored.Origin -= new Vector3(_screenMenuAnchor.X, _screenMenuAnchor.Y, 0f);
		return authored;
	}

	/// <summary>
	/// The lists open now whose book was retired, dressed again from the new one (G.22): the
	/// screen's list and a mod's menu standing on its own.
	/// </summary>
	private void RedressOpenLists(ISet<string> retired)
	{
		if (_presentation is null)
		{
			return;
		}

		if (_screenMenuView is not null && GodotObject.IsInstanceValid(_screenMenuView) &&
			retired.Contains(_screenMenuPresentation))
		{
			Engine_UiTheme? book = _presentation.PatternBookFor(_screenMenuPresentation);
			Node3D? pattern = book is null ? null : Engine_UiButtonPool.FindButtonPool(book);
			if (pattern is not null)
			{
				_screenMenuAnchor = Engine_UiTheme.CanvasTransform(pattern).Origin;
			}

			_screenMenuView.Redress(book, pattern is null ? null : Engine_FrontEndMenuView.RowTemplateIn(pattern),
				book?.Art("MenuHighlight"), ScreenMenuCursorPose(book));
			GD.Print($"[game-ui] the open list on '{_screenMenuPresentation}' takes the new art now");
		}

		if (_hostMenuView is not null && GodotObject.IsInstanceValid(_hostMenuView))
		{
			bool inGame = HostBridge.IsInGame;
			string presentation = inGame ? PausePresentation : FrontEndMenuPresentation;
			if (retired.Contains(presentation))
			{
				_hostMenuView.RedressFrom(_presentation.PatternBookFor(presentation), bookLook: inGame, _presentation.Theme);
				GD.Print($"[game-ui] the mod menu standing on its own takes the new '{presentation}' art now");
			}
		}
	}

	private static void FreeView<T>(ref T? view)
		where T : Node
	{
		if (view is not null && GodotObject.IsInstanceValid(view))
		{
			view.QueueFree();
		}

		view = null;
	}
}
