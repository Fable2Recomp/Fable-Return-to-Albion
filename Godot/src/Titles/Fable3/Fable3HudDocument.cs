namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Godot;
using Refii.Ui;

/// <summary>
/// One Fable 3 presentation on the SHARED HUD view: its `.bgf` read in place by the presentation
/// host, laid out by <see cref="Engine_HudLayout"/> with the widgets the title cuts whole, drawn by
/// <see cref="Engine_HudView"/> with its authored art, and driven by the element statements of the
/// presentation that writes it (<see cref="Fable3Elements"/>, joined by
/// <see cref="Fable3HudStatements"/>) and the registry values the bridge publishes (hud_feed.cpp,
/// through <see cref="Engine_HudValues"/>). <see cref="Fable3Hud"/> stands one per presentation.
/// </summary>
/// <remarks>
/// WHAT FABLE 2 SAMPLES, FABLE 3 STATES. Fable 2's feed reads every element's live slide, active
/// bit, opacity, text, texture and transform off the guest's presentation each frame; Fable 3's
/// element hooks post each write as it happens (gui_element_hooks.cpp). Folded per ordinal they are
/// the same channels, plus the timeline position Fable 2 never had (`sub_8281A5D8`, a meter's
/// `fraction × 1000`), which scrubs the group's meters through <see cref="Engine_HudView.LiveTimes"/>.
/// The position is the element's local `position.x/.y/.z` (sub_82818580), the column the view's
/// stated transform takes; the texture is the path the renderable hashes (sub_82818EF0).
/// </remarks>
public sealed class Fable3HudDocument
{
	/// <param name="document">The presentation's name in the banks (`hud`, `interactionmode`).</param>
	/// <param name="groups">The widgets cut whole.</param>
	/// <param name="presentation">The guest presentation writing this document now, or 0.</param>
	/// <param name="layouts">Whether this document's arrangement is the title's HUD layout file (snapshot written, player file applied).</param>
	public Fable3HudDocument(
		string document, IReadOnlyList<Engine_HudLayout.GroupSubtree> groups, System.Func<uint> presentation, bool layouts)
	{
		Document = document;
		Groups = groups;
		_presentation = presentation;
		_layouts = layouts;
	}

	public string Document { get; }

	public IReadOnlyList<Engine_HudLayout.GroupSubtree> Groups { get; }

	/// <summary>The view, once the document is read.</summary>
	public Engine_HudView? View { get; private set; }

	private readonly System.Func<uint> _presentation;
	private readonly bool _layouts;
	private Engine_UiScreen? _screen;
	private Engine_UiBindings? _bindings;
	private Fable3HudStatements? _statements;
	private Engine_UiElementResolve? _resolve;
	private Engine_UiWidescreen? _wide;

	/// <summary>Texture hashes the view asked for and could not load, each named once.</summary>
	private readonly HashSet<uint> _missed = new();

	private int[] _slides = System.Array.Empty<int>();
	private float?[] _opacity = System.Array.Empty<float?>();
	private string?[] _texts = System.Array.Empty<string?>();
	private float?[] _times = System.Array.Empty<float?>();
	private float?[]?[] _transforms = System.Array.Empty<float?[]?>();
	private uint[] _textures = System.Array.Empty<uint>();
	private bool[] _active = System.Array.Empty<bool>();

	/// <summary>Per ordinal: the meter name bound there, for the time an unstated meter stands at.</summary>
	private string?[] _meters = System.Array.Empty<string?>();

	/// <summary>Per ordinal: the text name bound there, for a text the registry states and no element write carried.</summary>
	private string?[] _textNames = System.Array.Empty<string?>();

	private readonly List<Fable3HudStatements.Statement> _gathered = new();
	private ulong _fed = ulong.MaxValue;
	private uint _fedPresentation;
	private int _reportedUnmapped = -1;

	/// <summary>Builds the view under <paramref name="parent"/> from the host's banks; false when the document is not there.</summary>
	public bool Stand(Fable3Presentations host, Node parent)
	{
		_screen = host.DocumentFor(Document);
		if (_screen is null)
		{
			GD.PushWarning($"[fable3-hud] '{Document}' is not in the banks.");
			return false;
		}

		_bindings = Engine_UiBindings.Read(_screen.Document);
		_wide = Engine_UiWidescreen.Read(_screen.Document);
		var layout = Engine_HudLayout.FromBindings(_screen, _bindings, _wide, Groups);
		View = new Engine_HudView
		{
			Name = "Hud_" + Document,
			Theme = host.Theme,
			Screen = _screen,
			Art = new Engine_HudArt { Shipped = host.PatternBookFor(Document) },
			ResolveTexture = hash => ResolveTexture(host, hash),
			Layout = layout,
			StatesTimes = true,
		};
		View.Values.Strings = host.Strings ?? Fable3Strings.Current;
		parent.AddChild(View);

		// The title's arrangement files (Engine_HudLayout.FolderFor): shipped < mods < the player's,
		// the snapshot written beneath the player's file for the player's tool and modders.
		Refii.Core.Engine_Title title = Refii.Core.Engine_Title.Fable3;
		string folder = Engine_HudLayout.FolderFor(title);
		DirAccess.MakeDirRecursiveAbsolute(folder);
		int modArranged = layout.ApplyModLayout();
		if (_layouts)
		{
			layout.Save(Engine_HudLayout.SnapshotPathFor(title), Engine_HudLayout.SnapshotLayer);
		}

		int arranged = layout.ApplyOverrides(Engine_HudLayout.LayoutPathFor(title));
		var ids = new List<string>();
		foreach (Engine_HudLayout.Piece piece in layout.Pieces)
		{
			ids.Add(piece.Id);
		}

		int replaced = View.Art.LoadOverrides(folder, ids);

		int count = _screen.Nodes.Length;
		_slides = new int[count];
		_opacity = new float?[count];
		_texts = new string?[count];
		_times = new float?[count];
		_transforms = new float?[]?[count];
		_textures = new uint[count];
		_active = new bool[count];
		_meters = new string?[count];
		_textNames = new string?[count];
		foreach (Engine_UiBindings.Binding binding in _bindings.All)
		{
			if (binding.ElementOrdinal < 0 || binding.ElementOrdinal >= count)
			{
				continue;
			}

			if (binding.Kind == Engine_UiBindings.Kind.Meter)
			{
				_meters[binding.ElementOrdinal] ??= binding.Name;
			}
			else if (binding.Kind == Engine_UiBindings.Kind.Text)
			{
				_textNames[binding.ElementOrdinal] ??= binding.Name;
			}
		}

		_statements = new Fable3HudStatements(PathsOf(_screen), Bound, SlideOwners(_screen.Document));
		// The rest state from the slides alone: an ancestor's slide gates a child component over
		// the child's own self-activating entry (Engine_UiElementResolve.AncestorsWinAtRest).
		_resolve = new Engine_UiElementResolve(_screen) { AncestorsWinAtRest = true };
		View.Build();
		GD.Print($"[fable3-hud] '{Document}' {count} element(s), {_bindings.Describe()}; " +
			$"{layout.Pieces.Count} piece(s), {Groups.Count} cut whole; {replaced} piece(s) replaced from {folder}" +
			(modArranged == 0 ? string.Empty : $"; {modArranged} moved by mods") +
			(arranged == 0 ? string.Empty : $"; {arranged} moved by {Engine_HudLayout.LayoutPathFor(title)}"));
		return true;
	}

	/// <summary>The ordinals bound to `Attribute:value`, by the attribute's spelling in the file.</summary>
	private IReadOnlyList<int> Bound(string attribute, string value)
	{
		if (_bindings is null)
		{
			return System.Array.Empty<int>();
		}

		foreach ((Engine_UiBindings.Kind kind, string spelled) in Engine_UiBindings.Attributes)
		{
			if (spelled == attribute)
			{
				return _bindings.Elements(kind, value);
			}
		}

		return System.Array.Empty<int>();
	}

	/// <summary>
	/// One frame: the window, and on a statement or the guest's frame (an unstated meter stands at
	/// the registry value it published, which moves without an element write) the folded channels.
	/// </summary>
	public void Frame(Vector2 window)
	{
		if (View is null || _statements is null || _resolve is null)
		{
			return;
		}

		View.Window = window;
		uint presentation = _presentation();
		if (presentation == 0u || (Fable3Elements.Generation == _fed && presentation == _fedPresentation &&
			!Refii.Core.Engine_GuestFrame.Changed))
		{
			return;
		}

		_fed = Fable3Elements.Generation;
		_fedPresentation = presentation;
		Feed(presentation);
	}

	private void Feed(uint presentation)
	{
		_gathered.Clear();
		foreach (KeyValuePair<(uint Presentation, string Path), Fable3Elements.Element> entry in Fable3Elements.Of(presentation))
		{
			Fable3Elements.Element element = entry.Value;
			_gathered.Add(new Fable3HudStatements.Statement(
				entry.Key.Path, element.State, element.Opacity, element.Text, element.Time, element.Stamp,
				element.Position, element.Texture));
		}

		_statements!.Apply(_gathered, _slides, _opacity, _texts, _times, _transforms, _textures);
		StateWidescreen();
		if (_statements.Unmapped != _reportedUnmapped)
		{
			_reportedUnmapped = _statements.Unmapped;
			GD.Print($"[fable3-hud] {_statements.Mapped} statement(s) joined to '{Document}', {_statements.Unmapped} not" +
				(_statements.Unmapped == 0 ? string.Empty : $": {string.Join(", ", _statements.UnmappedPaths)}"));
		}

		Engine_HudValues values = View!.Values;
		for (int i = 0; i < _slides.Length; ++i)
		{
			// A text the registry states (hud_feed.cpp, registry 6) where no element write carried one.
			if (_texts[i] is null && _textNames[i] is { } textName)
			{
				_texts[i] = values.Text(Engine_HudValues.Handle.For(textName));
			}

			// A meter's timeline: the stated position, else the registry's value (`pct × 10`, the
			// guest's own scale, sub_8281AD48 at 0x8281ADA4), else its start — a paused meter the
			// guest has not positioned stands at 0, not at its last key.
			if (_times[i] is null && _meters[i] is { } meter)
			{
				_times[i] = values.Meter(Engine_HudValues.Handle.For(meter)) is { } percent
					? Mathf.Clamp(percent, 0f, 100f) * 10f
					: 0f;
			}
		}

		_resolve!.Apply(_slides, null, _opacity);
		for (int i = 0; i < _active.Length; ++i)
		{
			// Drawn, by its slide and its parents', and not faded out — Fable 2's one answer.
			_active[i] = _resolve.Drawn[i] && _resolve.Opacity[i] > 0.01f;
		}

		View.Resolve = _resolve;
		View.LiveSlides = _slides;
		View.ActiveInputs = null;
		View.StatedOpacity = _opacity;
		View.LiveTexts = _texts;
		View.LiveTimes = _times;
		View.LiveTransform = _transforms;
		View.LiveTextures = _textures;
		View.ElementActive = _active;
	}

	/// <summary>
	/// THE WIDESCREEN ADAPTER'S WRITE IS A STATEMENT. `widescreenadapter.lua` (onInitializeReal →
	/// setWidescreenMode) reads its own `widescreenXPos` / `widescreenYPos` / `WidescreenXWidth` and
	/// `setAttribute`s them onto its PARENT's `position.x` / `.y` / `scale.x` when `GUI.State.
	/// IsWidescreen` — a Lua write, not through sub_82818580, so no hook carries it. The layout
	/// takes it for a cut piece's root (Engine_HudLayout's wide position); an adapter on an element
	/// INSIDE a piece (`ReceivedItems.ReceivedItemAlert`, -136; `CornerBox…Group`, -135) moved
	/// nothing, which left the parked received-item alert's gift on screen. Stated here wherever the
	/// guest has not written that channel itself.
	/// </summary>
	private void StateWidescreen()
	{
		if (_wide is null)
		{
			return;
		}

		foreach (Engine_UiWidescreen.Override over in _wide.Overrides)
		{
			int ordinal = over.ParentOrdinal;
			if (ordinal < 0 || ordinal >= _transforms.Length)
			{
				continue;
			}

			float?[] row = _transforms[ordinal] ??= new float?[Fable3HudStatements.TransformWidth];
			row[0] ??= over.X;
			row[1] ??= over.Y;
			row[3] ??= over.ScaleX;
		}
	}

	/// <summary>A texture the view asks for by hash, through the host's name table; a miss is named once.</summary>
	private Texture2D? ResolveTexture(Fable3Presentations host, uint hash)
	{
		if (host.TextureNames is { } names && names.TryResolve(hash, out string path))
		{
			if (host.LoadTexture(path) is { } texture)
			{
				return texture;
			}

			if (_missed.Add(hash))
			{
				GD.Print($"[fable3-hud] '{Document}' texture {hash:X8} '{path}' is named but did not load");
			}

			return null;
		}

		if (_missed.Add(hash))
		{
			GD.Print($"[fable3-hud] '{Document}' texture {hash:X8} is not in the banks' name table");
		}

		return null;
	}

	/// <summary>Per ordinal, the element's dotted path from beneath the root (the root's is empty).</summary>
	private static string[] PathsOf(Engine_UiScreen screen)
	{
		var paths = new string[screen.Nodes.Length];
		for (int i = 0; i < screen.Nodes.Length; ++i)
		{
			UiNode node = screen.Nodes[i];
			paths[i] = node.Parent is { } parent && parent.Ordinal >= 0 && parent.Ordinal < i && paths[parent.Ordinal] is { } above
				? (above.Length == 0 ? node.Name : above + "." + node.Name)
				: string.Empty;
		}

		return paths;
	}

	/// <summary>Every element that owns numbered slides.</summary>
	private static List<int> SlideOwners(Engine_BgfDocument document)
	{
		var owners = new HashSet<int>();
		foreach (BgfSlide slide in document.Slides)
		{
			if (!slide.IsMaster)
			{
				owners.Add(slide.OwnerElement);
			}
		}

		return new List<int>(owners);
	}
}
