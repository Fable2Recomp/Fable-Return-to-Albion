using Godot;

namespace Refii.Ui;

// The owned-surface frame: the view contract, the shared base, the modal specialisation, the
// input record a surface consumes, and the set that stands them up. Concrete surfaces are in
// Engine_UiOwnedSurfaces.cs.

/// <summary>
/// What an owned surface's view answers for itself, and the contract a new surface must meet.
/// </summary>
public interface IUiSurfaceView
{
	/// <summary>True when the shipped art was cloned; false means text-only stand-ins.</summary>
	bool HasShippedArt { get; }

	/// <summary>One line about what the view is made of, for the ownership report.</summary>
	string LastReport { get; }
}

/// <summary>
/// An owned surface: one of the guest's presentations that Godot draws instead of mirroring.
/// A surface states four things — which presentation it owns, how to build its view, how to
/// resolve its join, and what to do with a sample.
///
/// The join is positional, so <see cref="ElementCount"/> is load-bearing: ordinals come from the
/// file-only parse and are read against the guest's live element array; a half-built presentation
/// reports fewer elements and every ordinal past that point names somebody else's field.
/// </summary>
public abstract class Engine_UiSurface<TView>
	where TView : Node3D, IUiSurfaceView
{
	/// <summary>The presentation this surface owns, without its extension.</summary>
	public abstract string Presentation { get; }

	/// <summary>What the ownership report calls it.</summary>
	public abstract string Label { get; }

	/// <summary>The built view, once the host has art for it.</summary>
	public TView? View { get; private set; }

	/// <summary>
	/// True while this view stands in for the guest's presentation, so the follower does not
	/// mirror it as a second copy. Art and join both: without a join nothing is ever applied,
	/// so suppressing the mirror on art alone would leave the screen empty.
	/// </summary>
	public bool Owned => _open && View is { HasShippedArt: true } && Joined;

	/// <summary>The guest has this surface up and it is being driven this frame; <see cref="Owned"/>
	/// hangs off it so the flag falls the same frame the box closes.</summary>
	private bool _open;

	/// <summary>True while the view is on screen, which is not the same as owning the
	/// presentation. Mask and input routing gate on this, so an undrawn box still answers to
	/// the guest's own input.</summary>
	public bool Standing => View is { Visible: true };

	/// <summary>The document's element count, resolved once with the join.</summary>
	protected int ElementCount { get; private set; }

	/// <summary>Above every mirrored slab (they stack 1000 apart).</summary>
	public const float ModalDepth = 9000f;

	/// <summary>Below the modal boxes, above the mirrored slabs.</summary>
	public const float PanelDepth = 8000f;

	/// <summary>Where the view sits in the host's depth stack — one of the two above.</summary>
	protected abstract float Depth { get; }

	/// <summary>
	/// True when this live presentation is the one this surface owns. The bank file is the
	/// presentation name plus extension; the guest's string is a full backslashed guest path,
	/// which <c>Presentation.Is</c> reduces.
	/// </summary>
	public bool Matches(Engine_UiModel.Presentation presentation) =>
		presentation.Is($"{Presentation}.bgf");

	/// <summary>
	/// True when this surface is standing in for that presentation. A surface whose art or join
	/// failed answers false and keeps the mirror.
	/// </summary>
	public bool Draws(Engine_UiModel.Presentation presentation) => Owned && Matches(presentation);

	private string? _shown;

	/// <summary>Clones this surface's art out of the file-only pattern book.</summary>
	protected abstract TView Build(Engine_UiTheme? art);

	/// <summary>Resolves the live-text ordinals from the parsed document.</summary>
	protected abstract void Join(Engine_UiScreen document);

	/// <summary>True once <see cref="Join"/> has produced something drivable.</summary>
	protected abstract bool Joined { get; }

	/// <summary>The host's <see cref="Engine_UiPresentationHost.BookGeneration"/> the view was built at.</summary>
	private int _generation;

	/// <summary>Stands the view up and builds its join, once per pattern book: again when the mount
	/// changed this presentation's files (<see cref="Engine_UiPresentationHost.BookGeneration"/>).
	/// With no pattern book the build degrades to text-only stand-ins rather than null, keeping the
	/// surface answerable.</summary>
	protected void Ensure(Engine_UiPresentationHost host)
	{
		_open = true;
		int generation = host.BookGeneration(Presentation);
		if (View is not null && GodotObject.IsInstanceValid(View) && generation == _generation)
		{
			return;
		}

		if (View is not null && GodotObject.IsInstanceValid(View))
		{
			View.QueueFree();
		}

		View = null;
		_shown = null;
		_generation = generation;

		Engine_UiTheme? book = host.PatternBookFor(Presentation);
		View = Build(book);
		View.Position = new Vector3(0f, 0f, Depth);
		host.AddChild(View);

		int framed = 0;
		if (host.DocumentFor(Presentation) is { } document)
		{
			Join(document);
			ElementCount = document.Nodes.Length;

			// Nine-slice borders, cloned at runtime: the box files carry the frame behaviour
			// and none of the slices (`framecloning.lua`).
			if (book is not null)
			{
				var binding = new Engine_UiCloneBinding();
				binding.Bind(View, document);
				framed = new Engine_UiFrameCloning(book).Dress(document, binding);
			}
		}

		Dress(host);
		GD.Print($"[ui-surface] Godot owns the {Label} — art {View.LastReport}, join " +
			(Joined
				? "resolved"
				: "NOT resolved — the authored structure is not what this surface expects") +
			(framed == 0 ? string.Empty : $", {framed} border piece(s) framed"));
	}

	/// <summary>Art a fresh view takes from OTHER presentations' pattern books — once per build,
	/// after the view stands and its join is resolved.</summary>
	protected virtual void Dress(Engine_UiPresentationHost host)
	{
	}

	/// <summary>
	/// The guest's live element array for one presentation, or null when its count disagrees
	/// with the document the join was built against.
	/// </summary>
	protected System.Collections.Generic.List<Engine_UiModel.UiElement>? Elements(uint address)
	{
		// Through the cache, not the walk: same list and ordinals, read once per presentation
		// and kept current by the guest's writers. See Engine_UiElementState.
		System.Collections.Generic.List<Engine_UiModel.UiElement>? elements =
			Engine_UiElementState.Current.Elements(address);
		return elements is not null && elements.Count == ElementCount ? elements : null;
	}

	/// <summary>
	/// True when this sample differs from the one on screen. The guest restates the same strings
	/// every frame, so applies gate on a signature to avoid re-meshing glyphs.
	/// </summary>
	protected bool Changed(string signature)
	{
		if (signature == _shown)
		{
			return false;
		}

		_shown = signature;
		return true;
	}

	/// <summary>Takes the surface down. The view is kept, but everything sampled off the guest is
	/// forgotten so the next opening re-reads.</summary>
	protected void Close()
	{
		if (View is not null)
		{
			View.Visible = false;
		}

		_open = false;
		_shown = null;
	}

	/// <summary>
	/// A live presentation's address by bank file name, 0 when the guest has none. Re-asked every
	/// opening, never cached across one: the guest recycles presentation addresses.
	/// </summary>
	public static uint AddressOf(string fileName)
	{
		foreach (Engine_UiModel.Presentation p in Engine_UiSource.Current.GetPresentations())
		{
			if (p.Is(fileName))
			{
				return p.Address;
			}
		}

		return 0;
	}
}

/// <summary>
/// An owned surface the player can answer: query box, message box, numeric adjuster,
/// received-item card, MenuBox. The guest states the strings and waits on an answer through its
/// own input router; the host clones the art, paints the text and posts back the code that
/// screen's handler expects. The outcome stays guest-side.
///
/// The mask engages only once the view is on screen — <see cref="Engine_UiSurface{TView}.Standing"/>,
/// not <see cref="Engine_UiSurface{TView}.Owned"/> — so an undrawn box is answerable, not a deadlock.
/// </summary>
public abstract class Engine_UiModalSurface<TView> : Engine_UiSurface<TView>
	where TView : Node3D, IUiSurfaceView
{
	protected override float Depth => ModalDepth;

	/// <summary>
	/// Told, every frame, whether this surface has taken the input. Supplied by the owner: the
	/// engine does not know which of the game's input masks a given box asserts.
	/// </summary>
	public System.Action<bool>? Mask { get; set; }

	/// <summary>The guest's live presentation, found the frame the surface opens and dropped
	/// the frame it closes — an address is a slot, not an identity.</summary>
	protected uint Address { get; private set; }

	/// <summary>True when the guest has this surface up; the one thing each box states
	/// differently.</summary>
	protected abstract bool IsOpen(Engine_UiModel.PauseState? pause);

	/// <summary>One sample of the guest's stated elements, gated on the count agreeing.</summary>
	protected abstract void Apply(
		System.Collections.Generic.List<Engine_UiModel.UiElement> elements,
		Engine_UiPresentationHost host);

	/// <summary>A host-raised box's text, shown without the guest's elements; true when it is one.</summary>
	protected virtual bool ApplyHost() => false;

	/// <summary>This frame's press, once the view is standing.</summary>
	protected abstract void Route(Engine_UiSurfaceInput input);

	/// <summary>
	/// False for a box drawn but answered by the guest's own input — one whose codes the host
	/// does not state. It is not masked and nothing is routed, so the guest reads the pad.
	/// </summary>
	protected virtual bool TakesInput => true;

	/// <summary>Anything the surface latched that has to fall with the box, e.g. a repeat clock;
	/// the base already drops the view, the address and the signature.</summary>
	protected virtual void Released()
	{
	}

	/// <summary>Takes the surface down because another has the frame; the shared mask is the
	/// winner's to write.</summary>
	public void StandDown()
	{
		Close();
		Address = 0;
		Released();
	}

	/// <summary>
	/// One frame of this surface. True when it has the frame, including the window between the
	/// guest opening the box and the view being ready — that window is not handed on behind it.
	/// </summary>
	public bool Drive(
		Engine_UiPresentationHost? host, Engine_UiModel.PauseState? pause, Engine_UiSurfaceInput input)
	{
		if (host is null || !IsOpen(pause))
		{
			Close();
			Mask?.Invoke(false);
			Address = 0;
			Released();
			return false;
		}

		Ensure(host);

		if (Address == 0)
		{
			Address = AddressOf($"{Presentation}.bgf");
		}

		// A box the HOST raises states its own text: it needs no guest presentation, and over a
		// screen with none (the world map) waiting for one left it unseen, deaf, and holding the
		// frame — every menu under it starved.
		if (!ApplyHost() && Address != 0 && Joined && Elements(Address) is { } elements)
		{
			Apply(elements, host);
		}

		Mask?.Invoke(Standing && TakesInput);
		if (!Standing || !TakesInput)
		{
			return true;
		}

		// THE BOX TAKES THE FRAME'S CLICK, wherever it landed: a click that missed the box
		// must not fall through as A, or clicking beside a yes/no question would answer yes.
		if (Engine_UiPointer.TakeLeft() && Engine_UiPointer.Canvas is { } at)
		{
			input = Point(input, at);
		}

		Route(input);
		return true;
	}

	/// <summary>
	/// A click on this surface, as the press it stands for. The default reads a click on the box
	/// as A and anything else as nothing; a box with more than one answer names its buttons.
	/// </summary>
	protected virtual Engine_UiSurfaceInput Point(Engine_UiSurfaceInput input, Vector2 at) =>
		View is { } view && Engine_UiPointer.CanvasBounds(view) is { } bounds && bounds.HasPoint(at)
			? input with { Accept = true }
			: input;
}

/// <summary>
/// One frame of menu input, as an owned surface consumes it. A record rather than a static read,
/// so the engine never reaches into the game's input class and the editor can fill one in.
///
/// These channels are the whole menu vocabulary. <see cref="Direction"/> and
/// <see cref="Horizontal"/> are separate axes because surfaces that repeat both keep a clock each.
/// </summary>
public readonly record struct Engine_UiSurfaceInput
{
	public bool Accept { get; init; }

	public bool Back { get; init; }

	/// <summary>Y — the received-item card's turn-over.</summary>
	public bool Flip { get; init; }

	/// <summary>X — the received-item card's equip (code 75, honoured only when the item can be worn).</summary>
	public bool Equip { get; init; }

	/// <summary>-1 up, +1 down, 0 none.</summary>
	public int Direction { get; init; }

	/// <summary>-1 left, +1 right, 0 none.</summary>
	public int Horizontal { get; init; }

	/// <summary>LB / RB.</summary>
	public bool TabPrev { get; init; }

	public bool TabNext { get; init; }

	/// <summary>The scroll stick's vertical, -1..1, up positive — the right stick in the game.</summary>
	public float Scroll { get; init; }

	/// <summary>Seconds since the last frame, for the surfaces that run their own repeat.</summary>
	public double Delta { get; init; }

	/// <summary>No press this frame — what an editor preview passes.</summary>
	public static readonly Engine_UiSurfaceInput None = new();
}

/// <summary>
/// Every owned surface the guest can raise over any screen, and the order they stand in: modal
/// boxes outermost, then the received-item card, then the MenuBox. At most one modal box can be
/// up — the guest's latch is a single field — so the three share one input mask.
/// </summary>
public sealed class Engine_UiSurfaceSet
{
	public Engine_UiQuerySurface Query { get; } = new();

	public Engine_UiMessageSurface Message { get; } = new();

	public Engine_UiNumericSurface Numeric { get; } = new();

	public Engine_UiReceiveItemsSurface ReceiveItems { get; } = new();

	public Engine_UiMenuBoxSurface MenuBox { get; } = new();

	/// <summary>The display-only pair, sampled by the presentation follower rather than
	/// driven — see <see cref="Engine_UiStatedSurface"/>.</summary>
	public Engine_UiStatedSurface Intermission { get; } = Engine_UiStatedSurface.Intermission();

	public Engine_UiStatedSurface DisplayBox { get; } = Engine_UiStatedSurface.DisplayBox();

	/// <summary>
	/// Wires the input masks. The three modal boxes share one flag; the owner must clear it at
	/// the top of the frame, which is where "no box raised at all" is stated.
	/// </summary>
	public Engine_UiSurfaceSet(
		System.Action<bool> modalMask,
		System.Action<bool> receiveItemsMask,
		System.Action<bool> menuBoxMask)
	{
		Query.Mask = modalMask;
		Message.Mask = modalMask;
		Numeric.Mask = modalMask;
		ReceiveItems.Mask = receiveItemsMask;
		MenuBox.Mask = menuBoxMask;
	}

	/// <summary>
	/// One frame of every input-taking surface, outermost first. True when one of them has the
	/// frame and nothing behind it should take input this pass.
	/// </summary>
	public bool Drive(
		Engine_UiPresentationHost? host, Engine_UiModel.PauseState? pause, Engine_UiSurfaceInput input)
	{
		// The router shows ONE modal at a time, and a surface only closes inside its own
		// drive — so the winner stands the others down, or a box replaced between two samples
		// (a notice then a question) stays up under its successor.
		if (Query.Drive(host, pause, input))
		{
			Message.StandDown();
			Numeric.StandDown();
			return true;
		}

		if (Message.Drive(host, pause, input))
		{
			Numeric.StandDown();
			return true;
		}

		return Numeric.Drive(host, pause, input)
			|| ReceiveItems.Drive(host, pause, input)
			|| MenuBox.Drive(host, pause, input);
	}

	/// <summary>
	/// True when a live presentation is one this set stands in for, so the follower does not
	/// mirror it. Asked per surface: one whose art or join failed answers false and keeps the
	/// mirror.
	/// </summary>
	public bool Owns(Engine_UiModel.Presentation presentation) =>
		Query.Draws(presentation) ||
		Message.Draws(presentation) ||
		Numeric.Draws(presentation) ||
		ReceiveItems.Draws(presentation) ||
		MenuBox.Draws(presentation);
}
