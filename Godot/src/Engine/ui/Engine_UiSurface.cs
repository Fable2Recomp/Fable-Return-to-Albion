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
/// resolve its join against the file, and what the guest stated for it to show. Nothing is read
/// back out of the guest's elements: its presentations are headless (gui_headless_presentation.cpp)
/// and list only the elements the guest named, so a count or an ordinal read against them names
/// nothing.
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
	///
	/// AND ONLY WHILE IT IS ACTUALLY ON SCREEN, past a short grace. Art and join say the view
	/// CAN draw; whether it HAS needs the guest to have stated what it shows, which can fail for
	/// a whole opening (an opener that is not hooked, a statement that never arrived). The grace
	/// covers the frames between the guest opening the box and the first statement.
	/// </summary>
	public bool Owned =>
		_open && View is { HasShippedArt: true } && Joined &&
		(Standing || Godot.Engine.GetProcessFrames() - OpenedFrame <= OwnerGraceFrames);

	/// <summary>Frames an open surface may take to stand before it is reported unseen.</summary>
	public const ulong OwnerGraceFrames = 30;

	/// <summary>The guest has this surface up and it is being driven this frame; <see cref="Owned"/>
	/// hangs off it so the flag falls the same frame the box closes.</summary>
	private bool _open;

	/// <summary>The process frame this opening began, for the grace and the starvation report.</summary>
	protected ulong OpenedFrame { get; private set; }

	/// <summary>True once this opening's starvation has been reported, so it prints once.</summary>
	protected bool Starved { get; set; }

	/// <summary>True while the view is on screen, which is not the same as owning the
	/// presentation. Mask and input routing gate on this, so an undrawn box still answers to
	/// the guest's own input.</summary>
	public bool Standing => View is { Visible: true };

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
		if (!_open)
		{
			OpenedFrame = Godot.Engine.GetProcessFrames();
			Starved = false;
		}
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
/// received-item card, MenuBox. The guest STATES what the box shows at the box's own entry
/// points — the router's openers (`@Modal.Open`), each display-box class's Open
/// (`@DisplayBox.Open`, `@MessageBox.Open`), the card's set-item, the menu box's populate — and
/// waits on an answer; the host clones the art, paints the stated text and posts back the code
/// the box's own handler expects. The outcome stays guest-side.
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

	/// <summary>Puts what was stated for the box that is up on the view; true when there was a
	/// statement to show. Called every frame the box is up — applies gate on
	/// <see cref="Engine_UiSurface{TView}.Changed"/>.</summary>
	protected abstract bool ApplyHost(Engine_UiPresentationHost host);

	/// <summary>Why nothing was stated, for the unseen report.</summary>
	protected virtual string Unstated => "its opener stated nothing for it to show";

	/// <summary>This frame's <see cref="ApplyHost"/>.</summary>
	private bool _stated;

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

		// RE-ASKED EVERY FRAME: the guest can drop a box's presentation and build a new one while
		// the box stays up — the received-item card does between items of one chest. Only the
		// told statements are keyed by it (the menu box's fallback); nothing stands on it.
		Address = AddressOf($"{Presentation}.bgf");

		// THE BOX STANDS ON WHAT THE GUEST STATED, never on its presentation: the presentation is
		// headless, and a box gated on reading it was never drawn — invisible, unanswered, the
		// game waiting for ever (the shop's "Ragdoll purchased.").
		_stated = ApplyHost(host);

		ReportStarvation();

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
	/// Names, once per opening, why an open surface has not stood past the grace: no art, the join,
	/// or no statement. Nothing else draws it — the guest's presentation is headless — so this line
	/// is what turns "the box did not appear" into a cause.
	/// </summary>
	private void ReportStarvation()
	{
		if (Starved || Standing || Godot.Engine.GetProcessFrames() - OpenedFrame <= OwnerGraceFrames)
		{
			return;
		}

		Starved = true;
		GD.Print($"[ui-surface] {Label} is up but NOT DRAWN after {OwnerGraceFrames} frames — " +
			(View is not { HasShippedArt: true }
				? "no shipped art for it"
				: !Joined
					? "its join did not resolve against the document"
					: _stated ? "the statement left the view hidden" : Unstated) +
			"; the guest's presentation is headless, so nothing is on screen — it is not masked, " +
			"and the guest's own input still reaches it");
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
