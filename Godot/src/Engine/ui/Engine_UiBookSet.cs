using Godot;

namespace Refii.Ui;

/// <summary>
/// Every owned book — one per screen — and the two ways they are stood up.
///
/// A book is a screen's chrome rebuilt from its template out of the FILE-ONLY pattern book, in
/// place of the mirrored tree; the guest still states every value. One construction serves all,
/// so a screen becoming owned is a template entry and nothing else.
///
/// Two drivers: in-game books are found in the presentation registry, while the front end never
/// appears there and is stood up by name from its screen object's fields.
/// </summary>
public sealed class Engine_UiBookSet
{
	/// <summary>The host the books are parented to and take their art from.</summary>
	public Engine_UiPresentationHost? Host { get; set; }

	/// <summary>One screen's book, or null when it has none yet.</summary>
	public Engine_UiOwnedBook? Book(uint screenId) =>
		_books.TryGetValue(screenId, out Engine_UiOwnedBook? book) &&
		GodotObject.IsInstanceValid(book)
			? book
			: null;

	/// <summary>Forgets which book the front end is standing — the guest has left it.</summary>
	public void ReleaseFrontEnd() => FrontEndBookId = 0;

	/// <summary>
	/// Builds one screen's owned book from its template, or null when the template names no
	/// chrome for it. Built once per screen and kept: its art is cloned from the FILE-ONLY
	/// pattern book, so it survives the rebuilds a guest slide change triggers on the live tree.
	/// </summary>
	public Engine_UiOwnedBook? Ensure(uint screenId, string presentation)
	{
		if (Host is null ||
			Engine_UiTemplates.Screen(screenId) is not { ChromeUnits.Count: > 0 } template)
		{
			return null;
		}

		// The art has to exist first: a presentation appears in the registry before its pattern
		// book is read, and building then yields a book with no units. Null here means "not
		// yet"; the caller asks again next frame.
		Engine_UiTheme? art = Host.PatternBookFor(presentation);
		Engine_UiScreen? document = Host.DocumentFor(presentation);
		if (art is null || document is null)
		{
			return null;
		}

		var book = new Engine_UiOwnedBook
		{
			Name = $"OwnedBook{screenId}",
			Template = template,
			Book = art,
			Document = document,
			Visible = false,
			ResolveTexture = hash =>
				Host.TextureNames is { } names &&
				names.TryResolve(hash, out string path)
					? Host.LoadTexture(path)
					: null,
		};

		// Suppression is applied at bind time, so it must be registered before Build().
		foreach (string owned in template.OwnedElsewhere)
		{
			book.Suppressed.Add(owned);
		}

		book.Build();
		book.DriveFrames();
		Host.MenuLayer.AddChild(book);
		return book;
	}

	/// <summary>
	/// Stands a screen's owned book in place of its mirrored tree and feeds it the guest's stated
	/// values. The mirror is still sampled, only not drawn: every value the book paints comes off
	/// the same walk.
	/// </summary>
	private void Drive(uint screenId, Engine_UiDrawnPresentation drawn)
	{
		if (_books.TryGetValue(screenId, out Engine_UiOwnedBook? book) &&
			!GodotObject.IsInstanceValid(book))
		{
			_books.Remove(screenId);
			book = null;
		}

		if (book is null && !_bookless.Contains(screenId))
		{
			// "No chrome" is permanent; "not ready yet" is not. Only a template naming no chrome
			// is a settled answer — a presentation can reach the registry before its pattern
			// book is read.
			if (Engine_UiTemplates.Screen(screenId) is not { ChromeUnits.Count: > 0 })
			{
				_bookless.Add(screenId);
			}
			else if (Ensure(screenId, drawn.Name) is { } made)
			{
				_books[screenId] = made;
				book = made;
			}
		}

		// The mirror's visibility is restated every frame, never toggled: one presentation serves
		// several screens (`GiftGivingScreen.bgf` is ids 15, 25 and 27), so a tree left hidden
		// would be inherited dark by the next screen that has no book of its own.
		// THE GUEST'S TREE IS NEVER DRAWN. Godot owns every screen; a screen with no chrome yet
		// shows nothing rather than the guest's picture.
		if (GodotObject.IsInstanceValid(drawn.Node))
		{
			drawn.Node.Visible = false;
		}

		if (book is null || !GodotObject.IsInstanceValid(book))
		{
			return;
		}

		// The mirror tree is freed on every guest slide change; a book keeps its last depth
		// across that frame rather than reading a disposed node.
		if (GodotObject.IsInstanceValid(drawn.Node))
		{
			book.Position = new Vector3(0f, 0f, drawn.Node.Position.Z + (Engine_UiPresentationDepth.Step / 4f));
		}

		book.Visible = true;

		// The guest's mirror is still fed whole: its transform carries the CHROME's layout (the
		// screen is laid out once at open, before the highlight froze) and dropping it took the
		// book frame off the screen. What IS stale in it — the per-element state of fields the
		// HOST drives — is dropped inside the book, per channel, for exactly the elements the
		// host writes (Opaqued / Positioned).
		book.SetLive(
			drawn.Texts, drawn.Textures, drawn.LastTransform,
			drawn.Visible, drawn.Opacity, drawn.Slides);
	}

	/// <summary>
	/// Stands the FRONT END's own owned book and feeds it the guest's sample. The front end never
	/// appears in the presentation registry — it reaches Godot through its screen-object fields,
	/// read by name — so it cannot be driven by <see cref="Drive(uint, Engine_UiDrawnPresentation)"/>.
	/// The mirrored tree is still built (the live art catalogue and the owned front-end views'
	/// placement come off it); it just stops being what the player sees.
	///
	/// Returns true when the book is standing.
	/// </summary>
	public bool DriveFrontEnd(
		string presentation, int[]? slides, string?[]? texts, bool[]? visible,
		float?[]? opacity, float?[]?[]? transform, uint[]? textures)
	{
		if (Host is null ||
			Engine_UiTemplates.ScreenFor(presentation) is not { ChromeUnits.Count: > 0 } template)
		{
			return false;
		}

		uint screenId = template.ScreenId;
		if (_books.TryGetValue(screenId, out Engine_UiOwnedBook? book) && !GodotObject.IsInstanceValid(book))
		{
			_books.Remove(screenId);
			book = null;
		}

		if (book is null)
		{
			// The pattern book is read from the banks on first ask, so a null here means "come
			// back next frame", not "never".
			book = Ensure(screenId, presentation);
			if (book is null)
			{
				return false;
			}

			_books[screenId] = book;
		}

		book.Position = Vector3.Zero;
		book.Visible = true;
		book.SetLive(texts, textures, transform, visible, opacity, slides);
		FrontEndBookId = screenId;

		// Take the other front-end book down: title and main menu are two presentations and only
		// one is up. Only front-end books, tested through the same helper that defines the front
		// end — `scrollingbackground` is host-only too and stays up, being the title's panorama.
		foreach ((uint id, Engine_UiOwnedBook other) in _books)
		{
			if (id != screenId && GodotObject.IsInstanceValid(other) &&
				Engine_UiTemplates.Screen(id) is { } sibling &&
				Engine_UiFrontEndScreens.Is(sibling.Presentation))
			{
				other.Visible = false;
			}
		}

		return true;
	}

	/// <summary>
	/// Which owned book the FRONT END is standing, or 0 for none — the registry loop hides every
	/// book it did not stand, and the front end's never appears there.
	/// </summary>
	public uint FrontEndBookId { get; private set; }

	/// <summary>Screens whose template names no chrome — asked once, then left alone.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _bookless = new();

	/// <summary>Which books stood this pass — scratch for <see cref="Drive"/>, reused per frame.</summary>
	private readonly System.Collections.Generic.HashSet<uint> _standing = new();

	/// <summary>
	/// Stands every owned surface the guest currently has live.
	///
	/// Driven by the presentation registry, not the current screen id: some owned surfaces are
	/// never "current" (the spell wheel is id 2 and the manager never reports it) and several can
	/// be live at once. The screen id still wins where the manager states one, since three ids
	/// share `GiftGivingScreen`.
	/// </summary>
	public void Drive(
		System.Collections.Generic.IReadOnlyDictionary<uint, Engine_UiDrawnPresentation> drawn,
		bool revealed)
	{
		if (Host is null)
		{
			return;
		}

		// Reused, not reallocated: this runs every guest frame and the set never outlives the
		// call, so a fresh one is per-frame garbage whose collection pauses stall the sim thread.
		System.Collections.Generic.HashSet<uint> standing = _standing;
		standing.Clear();
		if (revealed)
		{
			Engine_UiModel.InGameScreens? screens = Engine_UiSource.Current.GetInGameScreens();
			System.Collections.Generic.IReadOnlyDictionary<uint, int>? owned = screens?.Owned;
			uint current = screens is { } state ? (uint)state.Current : uint.MaxValue;
			uint boxScreen = Engine_UiSource.Current.DisplayBoxScreen();

			foreach ((uint address, Engine_UiDrawnPresentation live) in drawn)
			{
				// The pause book is driven by the pause menu, which also owns its banner, its
				// markers and the root-only window it stands in.
				uint screenId = owned is not null && owned.TryGetValue(address, out int stated)
					? (uint)stated
					: Engine_UiTemplates.ScreenFor(live.Name) is { } byFile
						? byFile.ScreenId
						: uint.MaxValue;

				if (screenId is uint.MaxValue or 0)
				{
					continue;
				}

				// GODOT OWNS EVERY SCREEN. The guest's tree is never drawn — restated every
				// frame because the mirror tree is rebuilt on slide changes.
				if (GodotObject.IsInstanceValid(live.Node))
				{
					live.Node.Visible = false;
				}

				// Only the screen the player is on: the guest keeps the previous screen's
				// presentation in the registry after moving on, so standing a book for everything
				// drawn puts two books on screen at once. Host-only surfaces are exempt — their
				// ids sit past the guest's range and the manager never reports them as current.
				// A display box stands on its screen without ever being current.
				if (screenId < HostOnlyScreenId && screenId != current && screenId != boxScreen)
				{
					continue;
				}

				Drive(screenId, live);
				standing.Add(screenId);
			}
		}

		foreach ((uint id, Engine_UiOwnedBook book) in _books)
		{
			// The front end's book is not driven from the registry — see DriveFrontEnd — so
			// this loop has no standing entry for it and would take it straight back down.
			if (id != FrontEndBookId && !standing.Contains(id) && GodotObject.IsInstanceValid(book))
			{
				book.Visible = false;
			}
		}
	}

	/// <summary>
	/// Where the guest's screen ids end and ours begin — its own table stops at 29, so anything
	/// at or above this is a surface Godot names for itself and is never reported as current.
	/// </summary>
	public const uint HostOnlyScreenId = 1000;

	/// <summary>The title screen's host-only id — <see cref="Engine_UiTemplates"/> record 1011.</summary>
	public const uint TitleScreenId = 1011;

	/// <summary>One owned book per screen id, built on first sight of that screen.</summary>
	private readonly System.Collections.Generic.Dictionary<uint, Engine_UiOwnedBook> _books =
		new();
}
