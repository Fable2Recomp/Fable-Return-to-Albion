using Godot;

namespace Refii.Ui;

/// <summary>
/// THE ACHIEVEMENTS SCREEN, stood by Godot alone.
///
/// Every other in-game surface is a guest presentation the host mirrors or replaces: the guest
/// pushes a screen, its document reaches the registry, and an owned book stands over the slab.
/// This one has no guest behind it at all — see <see cref="Engine_UiAchievementPage"/> — so it
/// is built from the FILE-ONLY pattern book, needs no live sample, and can be opened on any
/// frame whether or not the logbook has ever been shown.
///
/// That has one consequence worth stating plainly: with no sample there is no DRAW GATE. The
/// book's gate reads the guest's per-element draw bits, and a null sample reads as "nothing
/// draws" — so this screen states an all-true gate of its own and lets the page provider switch
/// off the seven subject pages it does not want.
///
/// It states NO SLIDES for the same reason in reverse. A host slide is not a patch onto the
/// guest's array: naming one element builds the whole array with every other ordinal at 0, and 0
/// resolves an owner to its master baseline. `logbookscreen`'s master hides nearly everything, so
/// one slide statement took this book to `0/677 node(s) drawing` where the guest's own logbook
/// draws 359 of the same 677. With no array the resolve skips its slide pass and nothing is
/// hidden — which is the correct baseline for a screen with no sample.
/// </summary>
public sealed class Engine_UiAchievementScreen
{
	/// <summary>The `.bgf` the screen borrows — the logbook's own furniture.</summary>
	public const string Presentation = "logbookscreen";

	/// <summary>The host the book and the list are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>The book set the screen's owned book is built and kept in.</summary>
	public Engine_UiBookSet? Books { get; set; }

	/// <summary>Whether the screen is standing.</summary>
	public bool IsOpen { get; private set; }

	/// <summary>The row the cursor is on, for a caller that reports the list.</summary>
	public Engine_UiExpandableMenu.Item? Highlighted => _menu?.Highlighted;

	/// <summary>The view, for the shared list report.</summary>
	public Engine_FrontEndMenuView? View => _view;

	/// <summary>
	/// Opens the screen. False when its art is not read yet — a pattern book is built from the
	/// banks on first ask, so that is "come back next frame", not "never".
	/// </summary>
	public bool Open()
	{
		if (!Build())
		{
			return false;
		}

		IsOpen = true;
		return true;
	}

	/// <summary>Takes the screen down. The book and the list are kept, only hidden.</summary>
	public void Close()
	{
		IsOpen = false;
		if (_view is not null && GodotObject.IsInstanceValid(_view))
		{
			_view.Visible = false;
		}

		if (_book is not null && GodotObject.IsInstanceValid(_book))
		{
			_book.Visible = false;
		}
	}

	/// <summary>
	/// One frame of the standing screen: the list takes the input, the provider fills the page.
	/// True when the screen consumed the frame's input.
	/// </summary>
	/// <param name="baseZ">
	/// The slab the screen stands in front of — the caller's current in-game presentation depth.
	/// A whole <see cref="Engine_UiPresentationDepth.Step"/> in front of it, because this is not
	/// a book standing over a mirror but a surface standing over the entire screen behind it.
	/// </param>
	public bool Drive(int direction, bool accept, bool back, double delta, float baseZ)
	{
		if (!IsOpen || !Build())
		{
			return false;
		}

		// THE SAME TWO PLANES EVERY IN-GAME SCREEN USES, off a slab of our own.
		//
		// `GameUi` puts an owned book at `slab + BookOffset` and its row list at
		// `slab + ListLift`, and the default `ListLift` IS `BookOffset` — so the rows land on
		// the book's own root plane, and `LeftFrame`, authored in front of `Buttons` inside
		// `ExpandableMenu`, covers them. Standing the book at the slab and then lifting the
		// rows another quarter-step put every tab in front of the cover instead.
		//
		// The anchor's Z is deliberately NOT added, matching the in-game path; only the pause
		// list applies it, and that correction is being proved there first.
		float slab = baseZ + Engine_UiPresentationDepth.Step;
		_book!.Position = new Vector3(0f, 0f, slab + Engine_UiPresentationDepth.BookOffset);
		_book.Visible = true;

		_view!.Position = new Vector3(
			_anchor.X, _anchor.Y,
			slab +
			(Engine_UiTemplates.Screen(Engine_UiAchievementPage.ScreenId)?.ListLift ??
				Engine_UiPresentationDepth.BookOffset));
		_view.Visible = true;

		// The list arrives with the title's XDBF, which is read off the seam and can land after
		// the screen is first opened. Re-sourced every frame; `RefreshSources` compares the set
		// and does nothing when it has not moved, so a settled list costs one walk.
		if (_template!.RefreshSources(_menu!))
		{
			_view.Refresh();
		}

		_view.Navigate(direction, delta);

		if (back)
		{
			// The list has no folds of its own, so B is always "leave".
			Close();
			return true;
		}

		Fill();

		if (accept)
		{
			// Nothing to commit: an achievement is a fact, not a command. The press is still
			// consumed, so it cannot fall through to the pause list standing behind.
			return true;
		}

		return true;
	}

	/// <summary>Feeds the book the provider's four channels over an all-true draw gate.</summary>
	private void Fill()
	{
		Engine_UiExpandableMenu.Item? row = _menu?.Highlighted;
		_book!.HostValues = Engine_UiDetailPages.Values(
			Engine_UiAchievementPage.ScreenId, row,
			default, default, EmptyQuests, default, default, default, default,
			_book.Book?.Strings);
		_book.HostTextures =
			Engine_UiDetailPages.Textures(Engine_UiAchievementPage.ScreenId, row);
		_book.HostActive =
			Engine_UiDetailPages.Active(Engine_UiAchievementPage.ScreenId, row);

		// NO SLIDES — see Engine_UiAchievementPage. Naming even one builds the whole array with
		// every other ordinal at 0, which resolves each owner to `logbookscreen`'s master
		// baseline and takes the book to 0 of 677 nodes drawing.
		_book.HostSlides = null;

		// THE DRAW GATE THE GUEST WOULD HAVE STATED. Sized to the document once and reused; the
		// provider's switches are applied over it inside the book.
		_book.SetLive(null, null, null, _drawn, null, null);
	}

	/// <summary>Builds the book and the list once. False while the art is still being read.</summary>
	private bool Build()
	{
		if (_book is not null && !GodotObject.IsInstanceValid(_book))
		{
			_book = null;
		}

		if (_view is not null && !GodotObject.IsInstanceValid(_view))
		{
			_view = null;
		}

		if (Host is null || Books is null)
		{
			return false;
		}

		_book ??= Books.Ensure(Engine_UiAchievementPage.ScreenId, Presentation);
		if (_book is null)
		{
			return false;
		}

		if (_drawn is null)
		{
			if (Host.DocumentFor(Presentation) is not { } document)
			{
				return false;
			}

			_drawn = new bool[document.Nodes.Length];
			System.Array.Fill(_drawn, true);
		}

		if (_view is not null)
		{
			return true;
		}

		Engine_UiTheme? art = Host.PatternBookFor(Presentation);
		Node3D? pool = art is null ? null : Engine_UiButtonPool.FindButtonPool(art);
		if (art is null || pool is null)
		{
			return false;
		}

		// The anchor is the SCREEN's own composed spot, the same read the pause list makes: the
		// slot table is authored in the `Buttons` group's space and each screen parks that group
		// where it wants.
		_anchor = Engine_UiTheme.CanvasTransform(pool).Origin;

		Transform3D? cursorPose = null;
		if (art.ArtSourceNode("MenuHighlight") is { } highlight)
		{
			Transform3D authored = Engine_UiTheme.CanvasTransform(highlight);
			authored.Origin -= _anchor;
			cursorPose = authored;
		}

		_template = Engine_UiTemplates.Menu(Engine_UiAchievementPage.MenuName);
		_menu = _template?.Build();
		if (_menu is null)
		{
			return false;
		}

		_view = new Engine_FrontEndMenuView
		{
			Name = "AchievementsMenu",
			ScreenId = Engine_UiAchievementPage.ScreenId,
			Theme = art,
			RowTemplate = Engine_FrontEndMenuView.RowTemplateIn(pool),
			CursorTemplate = art.Art("MenuHighlight"),
			CursorPose = cursorPose,
			Anchor = _anchor,
			// Leather for earned and the folder, slate for locked; no rating strip.
			LockedRowsSlate = true,
			HideRatingStrip = true,
			Visible = false,
		};

		Host.AddChild(_view);
		_view.Bind(_menu);
		_view.ClipRect = Engine_UiButtonPool.ListClip(Host.DocumentFor(Presentation));

		GD.Print($"[achievements] the screen is up — {_menu.Visible.Count} row(s), " +
			$"row art {(_view.RowTemplate is null ? "NOT sourced (text-only rows)" : "cloned from the pattern book")}, " +
			$"anchor ({_anchor.X}, {_anchor.Y}, z {_anchor.Z})");
		return true;
	}

	private Engine_FrontEndMenu? _menu;

	private Engine_UiMenuTemplate? _template;

	private Engine_FrontEndMenuView? _view;

	private Engine_UiOwnedBook? _book;

	private Vector3 _anchor = Engine_FrontEndMenuView.CanvasAnchor;

	private bool[]? _drawn;

	private static readonly System.Collections.Generic.List<Refii.Host.HostBridge.CompletedQuest>
		EmptyQuests = new();
}
