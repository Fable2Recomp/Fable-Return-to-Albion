using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE GAME'S SCREENS WITHOUT THE GAME: one screen's owned book stood on a presentation host
/// exactly as the running game stands it — the same template, the same <see cref="Engine_UiBookSet"/>
/// build, the same rest stand and slide playback — with nothing from a guest. What the lab
/// (UiScreenViewer) and the editor's UI tab show is therefore what ships.
///
/// What the game's providers and the guest would state, the caller may state instead: a slide per
/// component (<see cref="Slides"/>, as a page provider states one), and the book's playback can be
/// replayed, slowed, paused and stepped to look at a transition.
/// </summary>
public sealed class Engine_UiScreenStand
{
	private readonly Engine_UiBookSet _books;

	public Engine_UiScreenStand(Engine_UiPresentationHost host)
	{
		_books = new Engine_UiBookSet { Host = host };
	}

	/// <summary>Every screen with a book to stand, in id order.</summary>
	public static IEnumerable<Engine_UiScreenTemplate> Screens()
	{
		var screens = new List<Engine_UiScreenTemplate>();
		foreach (Engine_UiScreenTemplate template in Engine_UiTemplates.Screens)
		{
			if (template.ChromeUnits.Count > 0 && template.Presentation.Length != 0)
			{
				screens.Add(template);
			}
		}

		screens.Sort((a, b) => a.ScreenId.CompareTo(b.ScreenId));
		return screens;
	}

	/// <summary>The screen standing, or <see cref="uint.MaxValue"/>.</summary>
	public uint ScreenId { get; private set; } = uint.MaxValue;

	/// <summary>Its book, or null while its art is still loading.</summary>
	public Engine_UiOwnedBook? Book { get; private set; }

	/// <summary>
	/// Every entry plays, as if the screen's own code had played what it shows — the opened
	/// picture. Off, an authored-paused slide waits at its start, as the guest's walk leaves it.
	/// </summary>
	public bool PlayEntries { get; set; } = true;

	/// <summary>Slides stated per component, keyed `#ordinal` — over the file's rest slides.</summary>
	public Dictionary<string, int> Slides { get; } = new(System.StringComparer.Ordinal);

	/// <summary>One component that owns slides: its ordinal, path and slide names in the guest's numbering.</summary>
	public readonly record struct Owner(int Ordinal, string Path, IReadOnlyList<string> SlideNames);

	/// <summary>Stands <paramref name="screenId"/> (the previous screen's book is hidden) and replays it.</summary>
	public void Stand(uint screenId)
	{
		if (Book is { } previous && Godot.GodotObject.IsInstanceValid(previous))
		{
			previous.Visible = false;
		}

		ScreenId = screenId;
		Book = null;
		Slides.Clear();
		Tick();
	}

	/// <summary>One frame: builds the book once its art is in, then stands it.</summary>
	public void Tick()
	{
		if (ScreenId == uint.MaxValue || Engine_UiTemplates.Screen(ScreenId) is not { } template)
		{
			return;
		}

		if (Book is null || !Godot.GodotObject.IsInstanceValid(Book))
		{
			Book = _books.Book(ScreenId);
			if (Book is null && _books.Ensure(ScreenId, template.Presentation) is { } made)
			{
				_books.Keep(ScreenId, made);
				_stood.Add(made);
				Book = made;
			}

			if (Book is null)
			{
				return;
			}

			Book.Visible = true;
			Book.RestartPlayback();
		}

		Book.HostSlides = Slides.Count > 0 ? Slides : null;
		Book.PlayEntries = PlayEntries;
		Book.SetRest();
	}

	private readonly List<Engine_UiOwnedBook> _stood = new();

	/// <summary>Frees every book this stand built — a host that outlives it keeps none of them.</summary>
	public void Clear()
	{
		foreach (Engine_UiOwnedBook book in _stood)
		{
			if (Godot.GodotObject.IsInstanceValid(book))
			{
				book.QueueFree();
			}
		}

		_stood.Clear();
		_books.ForgetAll();
		Book = null;
		ScreenId = uint.MaxValue;
		Slides.Clear();
	}

	/// <summary>The screen opens again: every component's slide plays from its start.</summary>
	public void Replay() => Book?.RestartPlayback();

	/// <summary>The components of the standing screen that own slides.</summary>
	public IReadOnlyList<Owner> Owners()
	{
		var owners = new List<Owner>();
		if (Book?.Document is not { } screen)
		{
			return owners;
		}

		var names = new Dictionary<int, List<string>>();
		foreach (BgfSlide slide in screen.Document.Slides)
		{
			if (slide.IsMaster || slide.OwnerElement < 0 || slide.OwnerElement >= screen.Nodes.Length)
			{
				continue;
			}

			if (!names.TryGetValue(slide.OwnerElement, out List<string>? list))
			{
				names[slide.OwnerElement] = list = new List<string>();
			}

			list.Add(slide.Name);
		}

		foreach ((int ordinal, List<string> list) in names)
		{
			string path = Engine_UiOwnedBook.DocumentPath(screen.Nodes[ordinal]);
			owners.Add(new Owner(ordinal, path.Length == 0 ? "(root)" : path, list));
		}

		owners.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
		return owners;
	}
}
