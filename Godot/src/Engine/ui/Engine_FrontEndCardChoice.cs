using Godot;

namespace Refii.Ui;

/// <summary>
/// Fable 2's boy/girl card, drawn in Godot from the game's own card art.
///
/// WHY THIS EXISTS AT ALL. The guest's "New Game" does not start a game — it opens a gender
/// card and waits. `sub_826E0D60` is the function the answer arrives at, and it takes an EVENT
/// (`BOY_SELECTED` / `GIRL_SELECTED`, interned at `0x8349B5A0` / `0x8349B5A4`) rather than a
/// string, so the host route into a new game consumes the ANSWER to a question nobody has
/// asked. Godot owns the front-end menu now, which means Godot has to ask it. Posting a bare
/// "New Game" starts a male hero every time and says nothing about it.
///
/// THE PANEL IS THE GAME'S. `frontendmainmenu.bgf` ships a `choosecard` group under
/// `Component_0/Layer` holding a `Boy` and a `Girl` component (`cardboy.tex` / `cardgirl.tex`
/// over `cardback.tex`) and a full-screen `Background` tint, with nine slides that state both
/// cards' full transforms. So the layout is read, not chosen; what this owns is the selection
/// and the input.
///
/// WHAT THE GUEST ACTUALLY DOES HERE, from `sub_826DEE40` — the main menu screen's button
/// handler, vtable slot +12 of `0x820D0934`. In the card substates it stores NOTHING of its
/// own: it looks `choosecard` up by name in its presentation, sets a slide index on it, and
/// parks itself in substate 1 (frozen, all input ignored) until the presentation answers back
/// with an event. Offsets are from the main menu screen object at `GUI+184`:
///
/// <list type="bullet">
/// <item>The menu's New Game branch (`0x826E0200`) sets slide 1 (`Intro`) and `+24 = 2`.</item>
/// <item>`+24 == 2`, neither card active: LEFT (code 30) sets slide 2 `Boy` at `0x826DF3B0`,
/// RIGHT (31) sets slide 4 `BoytoGirl` at `0x826DF264`.</item>
/// <item>`+24 == 3` BOY_ACTIVE: RIGHT sets slide 3 `Girl` at `0x826DF1FC`. `+24 == 4`
/// GIRL_ACTIVE: LEFT sets slide 5 `GirltoBoy` at `0x826DF348`.</item>
/// <item>A (code 36) sets slide 6 `ChooseBoy` at `0x826DF5C0` from substate 3, slide 7
/// `ChooseGirl` at `0x826DF70C` from substate 4 — and in substate 2 it is NOT HANDLED, the
/// switch falling through to `default: return` at `0x826DF3E8`.</item>
/// <item>B (code 37) sets slide 8 `Outro` from any of 2, 3 and 4, at `0x826DFA9C`.</item>
/// </list>
///
/// The state changes come back the other way. `sub_826DE740` registers `sub_826E0D60` on the
/// `choosecard` element for four interned ids, and it is the presentation's own Behavior
/// elements that fire them: `BOY_ACTIVE` writes `+24 = 3` at `0x826E0D80`, `GIRL_ACTIVE`
/// writes 4 at `0x826E0DA8`, `UNFREEZE` writes 0 at `0x826E0DD0`, and `BOY_SELECTED` /
/// `GIRL_SELECTED` go straight to the commit at `sub_82379628`.
///
/// SO THE SLIDE COMMANDS ARE A TWEEN'S START, NOT ITS END. That is the only reading under
/// which all nine slides agree with the table above: `Boy` and `BoytoGirl` are both reached
/// from the neutral pose and both begin there; `Girl` is reached only while the boy is up and
/// begins boy-raised; `GirltoBoy` is reached only while the girl is up and begins girl-raised;
/// `ChooseBoy` begins boy-raised and `ChooseGirl` girl-raised; `Intro` begins off-canvas.
/// Triangulating them gives exactly three resting poses, each attested by two or three slides,
/// and that is <see cref="Poses"/>.
///
/// WHERE THE MENU LIST GOES while this is up is NOT read, and the caller decides it. Every
/// element of `ExpandableMenu` is at z 0 in the file and the active card's pose reaches
/// z -134, so the rows cut across it; and the six root slides all START the menu off-canvas at
/// x -800 and tween it in, so none of them is a parking spot the original uses here. Whatever
/// the shipped Lua does to clear the list, it is not in the file. See <c>GameUi</c>.
///
/// WHAT THIS DOES NOT REPRODUCE: the animation, and the VO. Each slide carries its duration in
/// its first command (`Intro` 617 ms, `ChooseBoy` 600, `ChooseGirl` 603) and the four
/// transition slides exist purely to tween between resting poses; cards here SNAP, the same
/// compromise <see cref="Engine_FrontEndMenuView"/> makes with its rows. The guest also queues
/// a line on entry (`TEXT_QUEST_QC010_FRONT_END_20`) and on each choice (`..._30_HM` /
/// `..._30_HF`), which is the audio path's business rather than this one's.
/// </summary>
public partial class Engine_FrontEndCardChoice : Node3D
{
	/// <summary>One card's resting transform, in the <c>choosecard</c> group's own space.</summary>
	public readonly struct Pose
	{
		public readonly float X;
		public readonly float Y;
		public readonly float Z;

		/// <summary>Radians. The cards are authored off-vertical, in opposite directions.</summary>
		public readonly float RollZ;

		public readonly float Scale;

		public Pose(float x, float y, float z, float rollZ, float scale)
		{
			X = x;
			Y = y;
			Z = z;
			RollZ = rollZ;
			Scale = scale;
		}

		public Transform3D ToTransform() => new(
			Basis.FromEuler(new Vector3(0f, 0f, RollZ)).Scaled(new Vector3(Scale, Scale, 1f)),
			new Vector3(X, Y, Z));
	}

	/// <summary>
	/// The panel's state, which is the guest's own — see `screen+24` in the class remarks.
	///
	/// <see cref="Neither"/> is not an empty initial value to be tidied away: it is a real
	/// substate the guest sits in, the one the card opens into, and the one where A is ignored.
	/// A panel that started with the boy already selected would let a player press A twice and
	/// start a game without ever having been asked.
	/// </summary>
	public enum Choice { Neither = 0, Boy = 1, Girl = 2 }

	/// <summary>
	/// The three resting poses, triangulated from the nine slides — see the class remarks for
	/// why the slide commands are read as a tween's start.
	///
	/// Indexed by <see cref="Choice"/>. The raised card goes to scale 1.0 at z -134 and the
	/// other stays at 0.75 at z -4: the raised one is FURTHER from the camera, not nearer,
	/// because the group's own <c>Background</c> tint sits between them — the chosen card is
	/// behind the tint at full size, the other in front of it and smaller.
	/// </summary>
	public static readonly Pose[,] Poses =
	{
		// Neither — the pose Intro lands on, and the start of both Boy and BoytoGirl.
		{ new(-168f, 0f, -4f, 0.1745f, 0.75f), new(139f, 0f, -4f, -0.1222f, 0.75f) },
		// Boy raised — the start of Girl and of ChooseBoy.
		{ new(-148f, 0f, -134f, 0.1745f, 1.00f), new(139f, 0f, -4f, -0.1222f, 0.75f) },
		// Girl raised — the start of GirltoBoy and of ChooseGirl.
		{ new(-168f, 0f, -4f, 0.1745f, 0.75f), new(108f, 0f, -134f, -0.1745f, 1.00f) },
	};

	/// <summary>
	/// The <c>Intro</c> slide's off-canvas parking spots, kept for the animation that is not
	/// written yet. Both cards are turned back-to-front there (<c>rotation.y</c> ±π) so the
	/// panel flies in showing <c>cardback.tex</c> and turns over. It is also why the panel is
	/// invisible on the menu screen while being built and standing there the whole time.
	/// </summary>
	public static readonly Pose[] IntroPoses =
	{
		new(-831f, -158f, -10f, 0.1745f, 0.75f),
		new(883f, -158f, -10f, -0.1222f, 0.75f),
	};

	/// <summary>Slide durations in milliseconds, for the tween this does not yet run.</summary>
	public const int IntroMilliseconds = 617;
	public const int ChooseBoyMilliseconds = 600;
	public const int ChooseGirlMilliseconds = 603;

	/// <summary>The group the shipped card art hangs off, inside the presentation.</summary>
	public const string GroupName = "choosecard";

	/// <summary>The two card components inside it, and the tint behind them.</summary>
	public const string BoyName = "Boy";
	public const string GirlName = "Girl";
	public const string BackgroundName = "Background";

	/// <summary>
	/// The names the guest's tick pump dispatches on.
	///
	/// "New Game" is the guest's own, out of its menu-action strcmp chain. "New Game Female"
	/// is the HOST's — the guest has no such string, because on its side the distinction is an
	/// event id rather than a menu entry — and the pump maps the pair onto the interned
	/// <c>BOY_SELECTED</c> / <c>GIRL_SELECTED</c> ids the guest computed for itself, so neither
	/// side has to know the other's hash.
	/// </summary>
	public const string BoyAction = "New Game";
	public const string GirlAction = "New Game Female";

	/// <summary>
	/// The card art, cloned out of the presentation. Null draws a stand-in.
	///
	/// Clones rather than the shipped nodes themselves, for the reason
	/// <see cref="Engine_UiTheme.Art"/> gives: the originals are still live inside the screen
	/// they came from, and that screen is rebuilt from the file on every guest state change.
	/// </summary>
	public Node3D? BoyTemplate { get; set; }

	public Node3D? GirlTemplate { get; set; }

	/// <summary>
	/// The group's own full-screen tint, which is what dims the menu behind the panel.
	///
	/// Every slide from `Boy` onward states <c>Background opacity 50</c> and the clone already
	/// carries it — the builder bakes the authored opacity into the quad's material — so
	/// nothing here has to set it.
	/// </summary>
	public Node3D? BackgroundTemplate { get; set; }

	/// <summary>Which card is up. Starts at <see cref="Choice.Neither"/>, as the guest's does.</summary>
	public Choice Chosen { get; private set; } = Choice.Neither;

	/// <summary>True when both cards found art to clone. False means stand-ins are on screen.</summary>
	public bool HasShippedArt { get; private set; }

	private Node3D? _boy;
	private Node3D? _girl;

	/// <summary>
	/// The direction currently held, so a hold does not act twice.
	///
	/// Edge-triggered rather than auto-repeating, and that is not a simplification of the row
	/// menu's curve — the two panels genuinely differ. A row menu's direction is RELATIVE and
	/// repeats to scroll; here LEFT means "the boy" and RIGHT means "the girl" absolutely, so
	/// a repeat has nothing left to do. The guest is edge-triggered too: `sub_826DEE40` is
	/// called on a button EVENT, and codes 34 and 35 are the left/right releases.
	/// </summary>
	private int _held;

	public override void _Ready()
	{
		Build();
		Refresh();
	}

	/// <summary>
	/// Puts the panel back to the state it opens in.
	///
	/// Called when the card is opened rather than when it is closed, so a cancelled choice
	/// cannot leak into the next one — the guest re-enters through the same `Intro` slide and
	/// substate 2 every time, whatever the player did last.
	/// </summary>
	public void Reset()
	{
		Chosen = Choice.Neither;
		_held = 0;
		Refresh();
	}

	/// <summary>
	/// Moves the choice. <paramref name="direction"/> is -1 left, +1 right, 0 released.
	///
	/// LEFT is the boy and RIGHT is the girl, from either the neutral pose or the other card —
	/// which is the whole of what the guest's four navigation arms do, since all four land on
	/// BOY_ACTIVE or GIRL_ACTIVE. Returns true when the choice changed.
	/// </summary>
	public bool Navigate(int direction)
	{
		if (direction == 0)
		{
			_held = 0;
			return false;
		}

		if (_held == direction)
		{
			return false;
		}

		_held = direction;

		Choice wanted = direction < 0 ? Choice.Boy : Choice.Girl;
		if (wanted == Chosen)
		{
			return false;
		}

		Chosen = wanted;
		Refresh();
		return true;
	}

	/// <summary>
	/// Confirms the chosen card and returns the NAME to dispatch to the guest, or null.
	///
	/// Null in <see cref="Choice.Neither"/> is the guest's own behaviour, not a guard bolted on
	/// here: its A arm has no case for substate 2 and returns without touching anything.
	/// </summary>
	public string? Activate() => Chosen switch
	{
		Choice.Boy => BoyAction,
		Choice.Girl => GirlAction,
		_ => null,
	};

	/// <summary>
	/// Builds the tint and the two cards once.
	///
	/// Persistent nodes rather than rebuilt on every move, for the same reason the row menu
	/// keeps its twelve slots: the tween, when it is added, has to hang off a node that
	/// survives the change it is animating.
	/// </summary>
	private void Build()
	{
		if (BackgroundTemplate is not null)
		{
			BackgroundTemplate.Name = "Background";
			AddChild(BackgroundTemplate);
		}

		_boy = CardNode(BoyTemplate, "BoyCard", new Color(0.42f, 0.52f, 0.85f));
		_girl = CardNode(GirlTemplate, "GirlCard", new Color(0.82f, 0.44f, 0.55f));
		HasShippedArt = BoyTemplate is not null && GirlTemplate is not null;

		AddChild(_boy);
		AddChild(_girl);
	}

	/// <summary>
	/// One card node: the shipped art if we were handed it, otherwise a flat stand-in.
	///
	/// A stand-in rather than nothing, because "the panel is up with no art" and "the panel
	/// never opened" are different faults that look identical when a missing clone draws
	/// nothing. It is deliberately not card-shaped art — a plain coloured rectangle at the
	/// authored card size reads as a placeholder rather than as a decision.
	/// </summary>
	private static Node3D CardNode(Node3D? template, string name, Color standIn)
	{
		if (template is not null)
		{
			template.Name = name;
			ShowFace(template, FrontFaceName, true);

			// THE REVERSE OF THE CARD, OFF.
			//
			// A card is two quads two units apart — `front` carrying the portrait at local
			// z +1, `back` carrying `cardback.tex` at z -1 — and the camera is orthographic at
			// z 10000 looking down -Z, so the portrait is the NEARER of the two. It still loses:
			// both quads are alpha-blended and therefore write no depth, which leaves which one
			// covers which entirely to the renderer's sort of two surfaces 2 units apart in a
			// 10000-unit view. The panel came up showing two blank card backs.
			//
			// Relying on that sort is the wrong shape of fix even when it happens to work. The
			// pose decides which face is toward the player: every pose this view uses has
			// rotation.y 0, so the portrait is the one facing out, and `back` exists for the
			// `Intro` flip that turns the card over (rotation.y ±π) before it lands. Saying so
			// is what the original gets for free from backface culling, which
			// Engine_UiSceneBuilder disables for every quad it builds.
			ShowFace(template, BackFaceName, false);
			return template;
		}

		return new MeshInstance3D
		{
			Name = name,
			Mesh = new QuadMesh { Size = new Vector2(StandInWidth, StandInHeight) },
			MaterialOverride = new StandardMaterial3D
			{
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoColor = standIn,
			},
		};
	}

	/// <summary>
	/// The two quads a card is made of — the portrait and its reverse. The artist's names.
	/// </summary>
	public const string FrontFaceName = "front";
	public const string BackFaceName = "back";

	/// <summary>
	/// Shows or hides one of a card's two faces.
	///
	/// Silently does nothing when the face is not there, which is the honest response to art
	/// that is not the shipped card — a stand-in has no faces to choose between.
	/// </summary>
	private static void ShowFace(Node card, string face, bool visible)
	{
		if (Engine_UiTheme.FindByName(card, face) is Node3D node)
		{
			node.Visible = visible;
		}
	}

	/// <summary>
	/// The stand-in's size, from the card texture's own footprint rather than by eye:
	/// <c>cardboy.tex</c> is 512x512 and the quad's authored U span is 0.596, so the art is
	/// 305 by 512 at scale 1 — the proportions the shipped card draws at.
	/// </summary>
	private const float StandInWidth = 305f;
	private const float StandInHeight = 512f;

	/// <summary>Puts both cards on their poses for the current choice.</summary>
	public void Refresh()
	{
		if (_boy is null || _girl is null)
		{
			return;
		}

		int state = (int)Chosen;
		_boy.Transform = Poses[state, 0].ToTransform();
		_girl.Transform = Poses[state, 1].ToTransform();
	}

	/// <summary>
	/// Finds the shipped panel inside a built presentation.
	///
	/// Returns null when the group is absent, which is not a failure — it means this
	/// presentation is not `frontendmainmenu`.
	/// </summary>
	public static Node3D? FindGroup(Node root) =>
		Engine_UiTheme.FindByName(root, GroupName) as Node3D;

	/// <summary>
	/// Builds a panel from the shipped <c>choosecard</c> group, and takes it off the screen.
	///
	/// ONE FACTORY, because the game and the harness must not disagree about what the panel is
	/// made of. They had a clone helper each, and the moment those drift the harness stops
	/// being evidence about the thing that ships.
	///
	/// It also HIDES the group it sourced from, for the reason the row menu hides its own: with
	/// Godot drawing the cards, leaving the guest's two components in the tree is two panels on
	/// one screen. That they are currently off screen is not protection — the `Intro` slide
	/// parks them at x -831 and 883, and a live slide from the guest brings them straight back.
	///
	/// <paramref name="group"/> may be null, which is not a failure: the panel falls back to
	/// stand-ins and says so, rather than inventing card art.
	/// </summary>
	public static Engine_FrontEndCardChoice FromPresentation(Node3D? group, string name = "HeroCard")
	{
		var panel = new Engine_FrontEndCardChoice
		{
			Name = name,

			// THE CARDS LOSE THEIR AUTHORED TRANSFORM AND THE TINT KEEPS ITS, and that split
			// is the file's rather than a preference. Every slide states both cards' position,
			// rotation and scale outright, so the artist's placement of `Boy` and `Girl` is a
			// starting frame the runtime immediately overwrites — keeping it would fight the
			// pose table. Not one slide touches `Background`'s transform; they set only its
			// opacity. Its authored transform IS the tint: it is what stretches a 128-pixel
			// texture across the 2000-unit square that dims the screen, and zeroing it left a
			// small rectangle floating over the menu.
			BoyTemplate = CloneFace(group, BoyName, keepTransform: false),
			GirlTemplate = CloneFace(group, GirlName, keepTransform: false),
			BackgroundTemplate = CloneFace(group, BackgroundName, keepTransform: true),
			Visible = false,
		};

		if (group is not null)
		{
			// The group's own place on the canvas, so the poses read out of its slides land
			// where the game puts them. Taken from the built node rather than hardcoded: the
			// slide table is expressed in the group's LOCAL space, and the group is a child of
			// `Layer` rather than of the screen root.
			panel.Transform = group.GlobalTransform;
			group.Visible = false;
		}

		return panel;
	}

	/// <summary>One piece of the shipped group, cloned. Null when it is not there.</summary>
	private static Node3D? CloneFace(Node3D? group, string name, bool keepTransform)
	{
		if (group is null || Engine_UiTheme.FindByName(group, name) is not Node3D found)
		{
			return null;
		}

		// Duplicated rather than reparented, for the reason Engine_UiTheme.Art gives: the
		// original stays live inside the screen it belongs to, and that screen is rebuilt from
		// the file on every guest state change.
		var clone = (Node3D)found.Duplicate();
		if (!keepTransform)
		{
			clone.Transform = Transform3D.Identity;
		}

		clone.Visible = true;
		return clone;
	}

	/// <summary>
	/// A one-line description of what is on screen, for a check tool's log.
	///
	/// It reports where the cards ACTUALLY ended up, in world space, not where the pose table
	/// says they should be. Those differ whenever the group's own transform is not what was
	/// assumed, and the two failures — a card behind something else and a card nowhere near the
	/// canvas — look identical in a screenshot.
	/// </summary>
	public string Describe() =>
		$"[card-choice] chosen={Chosen} art={(HasShippedArt ? "shipped" : "STAND-IN")} " +
		$"tint={(BackgroundTemplate is null ? "none" : "shipped")} " +
		$"group={Position} " +
		$"boy={(_boy is null ? "none" : _boy.GlobalPosition.ToString())} " +
		$"girl={(_girl is null ? "none" : _girl.GlobalPosition.ToString())}";
}
