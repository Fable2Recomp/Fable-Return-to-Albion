using System.Collections.Generic;
using Godot;
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
	/// let go of it now. An in-game list's view paints with its book's fonts only and keeps no node
	/// of it; it takes the new art when its screen next opens.
	/// </summary>
	private void OnPatternBooksRetired(IReadOnlyList<string> names)
	{
		var retired = new HashSet<string>(names, System.StringComparer.OrdinalIgnoreCase);
		int books = _books.Retire(retired);
		_spells.Retire(retired);
		_infoPanel.Retire();

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

			_hud.Build();
			if (_hudTwo is not null && GodotObject.IsInstanceValid(_hudTwo))
			{
				_hudTwo.Build();
			}
		}

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
