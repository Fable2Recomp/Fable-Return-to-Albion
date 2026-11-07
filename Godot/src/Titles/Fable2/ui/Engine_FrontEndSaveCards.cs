using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Fable 2's Load Game card fan, drawn in Godot from the game's own card art.
///
/// `frontendmainmenu.bgf` ships a `loadgame` component under `Component_0/Layer`, beside
/// `choosecard` and `dialog`. The Lua contributes only two Behavior elements that answer back
/// to C++ (`behaviorloadcard.lua` fires `LOAD_CARD_ACTIVE`, `behaviorloadselected.lua` fires
/// `LOAD_CARD_SELECTED`); nothing in the 587 shipped chunks enumerates a slot or lays a card
/// out. The layout is in the FILE, read from the slide table.
///
/// Six cards, hard-authored, matching `sub_82378618`'s save cap of 6 stored over
/// `dword_83319620` on front-end entry.
///
/// Two tiers of slide:
/// <list type="bullet">
/// <item>The PAGE's Slide1 is the OPEN: its commands park all six at <c>position.y -598</c>
/// and its tracks carry them into the fan, one card after another, over 764 ms. The guest
/// holds the page on it while the carousel is up.</item>
/// <item>The PAGE's Slide2 is the CLOSE: its commands state the fan and its tracks park the
/// cards again over 508 ms. Where the tracks of the one end and the other start is
/// <see cref="Fan"/>, inside the cards' authored parent group.</item>
/// <item>EACH CARD's Slide1/Slide2 move its inner <c>Group</c>: Slide1 parks it at the origin
/// and its tracks raise it 118..199 units over 250 ticks; Slide2 parks it raised and lowers it
/// back. Durations 250-257 ms.</item>
/// </list>
///
/// The cursor mover `sub_826E0BE0` re-slides exactly two cards — the one being left to slide 2
/// (826e0c8c) and the one moved to slide 1 (826e0c9c); a guest slide index is 1-based over the
/// NON-master slides. A slide's COMMANDS are the tween's START and its activated TRACKS are the
/// motion, so the card moved to lifts and the card left drops back into the arc.
///
/// The two tiers compose because they write different nodes: the arc is on the card component,
/// the lift on the <c>Group</c> inside it.
///
/// This does not own which saves exist — enumerating one is a live
/// `XamContentCreateEnumerator` CALL on the guest side — so the list arrives from the caller as
/// <see cref="Save"/> records.
/// </summary>
public partial class Engine_FrontEndSaveCards : Node3D
{
	/// <summary>
	/// One save, as much of it as a card shows. <see cref="Description"/> goes into
	/// `SaveGameDescription` already resolved — a plain text box (20pt, centred, wrapped into
	/// 411x96) the guest writes a finished string into, not a tag.
	/// </summary>
	public readonly record struct Save(string Description, Texture2D? Portrait = null);

	/// <summary>
	/// The quad inside a card that carries its portrait. Authored as a placeholder — all six
	/// point at `art\gui\questscreenshots\loadingcardback.tex`; `sub_826E1408` derives the real
	/// texture hash from the save record's QuestName and stamps it at element `+512`, so the
	/// portrait has to come from the guest.
	/// </summary>
	public const string PortraitName = "screenshot";

	/// <summary>The ordinals of the cards' portrait elements in walk (card) order, at most <see cref="MaxCards"/>.</summary>
	public static System.Collections.Generic.List<int> PortraitOrdinals(Refii.Ui.Engine_UiScreen screen)
	{
		var ordinals = new System.Collections.Generic.List<int>();
		for (int i = 0; i < screen.Nodes.Length && ordinals.Count < MaxCards; ++i)
		{
			if (string.Equals(screen.Nodes[i].Name, PortraitName, System.StringComparison.Ordinal))
			{
				ordinals.Add(i);
			}
		}

		return ordinals;
	}

	/// <summary>One card's place in the fan, in the <c>loadgame</c> component's own space.</summary>
	public readonly struct Pose
	{
		public readonly float X;
		public readonly float Y;
		public readonly float Z;

		/// <summary>Radians about Y — the card turns AWAY from the reader. Zero on card 1.</summary>
		public readonly float YawY;

		/// <summary>Radians about Z. Symmetric about the middle of the spread.</summary>
		public readonly float RollZ;

		public Pose(float x, float y, float z, float yawY, float rollZ)
		{
			X = x;
			Y = y;
			Z = z;
			YawY = yawY;
			RollZ = rollZ;
		}

		public Transform3D ToTransform(float scale) => new(
			Basis.FromEuler(new Vector3(0f, YawY, RollZ)).Scaled(new Vector3(scale, scale, 1f)),
			new Vector3(X, Y, Z));
	}

	/// <summary>
	/// The open fan, in the space of the cards' authored parent <c>loadgame.Group</c> — the
	/// page's Slide1 is the OPEN tween (its commands park every card at y -598 and its tracks
	/// end here, 362..764 ms), Slide2 the close (starts here, parks them). Read from the file's
	/// tracks. Card 1 is not part of the arc: it sits further out at x -335, y 0 with NO yaw,
	/// while cards 2..6 carry a constant -0.3491 rad. Rolls are symmetric across all six. Only
	/// card 1's slide states a scale; the rest inherit the same 0.65 — see <see cref="CardScale"/>.
	/// The group itself is authored at (44, -3, 0) under the page and no slide moves it; that
	/// offset is carried by <see cref="ArcTransform"/>, not folded into this table.
	/// </summary>
	public static readonly Pose[] Fan =
	{
		new(-335f, 0f, -10f, 0f, 0.4538f),
		new(-200f, 38f, -20f, -0.3491f, 0.2094f),
		new(-100f, 55f, -40f, -0.3491f, 0.0698f),
		new(0f, 52f, -60f, -0.3491f, -0.0698f),
		new(100f, 38f, -80f, -0.3491f, -0.2094f),
		new(200f, -5f, -100f, -0.3491f, -0.4538f),
	};

	/// <summary>
	/// The authored transform of the cards' parent group inside the page (<c>loadgame.Group</c>,
	/// (44, -3, 0)), composed up to the page. The six card components are cloned out of that
	/// group, so without it the whole arc stands where the page's origin is — 44 units left of
	/// where the artist put it. Identity when the page carried no such parent.
	/// </summary>
	public Transform3D ArcTransform { get; set; } = Transform3D.Identity;

	/// <summary>Every card's authored scale; stated on card 1's slide and inherited by the rest.</summary>
	public const float CardScale = 0.65f;

	/// <summary>
	/// Where a card sits when it is NOT the selection — each card's own Slide2 applied to its
	/// inner <c>Group</c>. The selected card's Slide1 returns its group to the origin, so this
	/// table is the whole difference between the two states.
	/// </summary>
	public static readonly Vector3[] Row =
	{
		new(25f, 199f, 0f),
		new(0f, 173f, 0f),
		new(-45f, 134f, 0f),
		new(-34f, 118f, 0f),
		new(0f, 136f, 0f),
		new(0f, 170f, 0f),
	};

	/// <summary>
	/// The roll the row adds, in radians, on top of the card's place in the arc. Cards 4, 5 and 6
	/// do not rotate — their slides carry 13 commands to cards 1..3's 16, the three missing ones
	/// being the rotation writes.
	/// </summary>
	public static readonly float[] RowRoll = { -0.1396f, 0.0873f, 0.0873f, 0f, 0f, 0f };

	/// <summary>Where the closed page parks every card — the page's Slide1 <c>position.y</c>.</summary>
	public const float ClosedY = -598f;

	/// <summary>The page's own slide durations, in milliseconds.</summary>
	public const int OpenMilliseconds = 508;
	public const int CloseMilliseconds = 764;

	/// <summary>
	/// How long a card takes to move between the row and the arc. The six cards' slides are
	/// 250..257 ms; this is the round number rather than a per-card table.
	/// </summary>
	public const int MoveMilliseconds = 250;

	/// <summary>The shipped page this is built from, under <c>Component_0/Layer</c>.</summary>
	public const string PageName = "loadgame";

	/// <summary>The six card components inside it. One-based, as the artist numbered them.</summary>
	public static string CardName(int index) => $"Card{index + 1}";

	/// <summary>The card's inner group — what the raise moves — and its three quads.</summary>
	public const string CardGroupName = "Group";

	/// <summary>The text box under the fan, and the full-screen tint behind it.</summary>
	public const string DescriptionName = "SaveGameDescription";
	public const string BackgroundName = "Background";

	/// <summary>
	/// How many cards the page ships, and the most this can draw. Six from both sides: the artist
	/// drew six components, and `sub_826E0BE0` at 0x826E0C20 rejects an index at or past
	/// <c>min(count, 6)</c>:
	///
	/// <code>
	/// v4 = a1[22] - a1[21];                                    // end - begin, the filename vector
	/// if ( a2 &lt; 0 || a2 &gt;= ((((v4 &gt;&gt; 2) - 6) &gt;&gt; 31) &amp; ((v4 &gt;&gt; 2) - 6)) + 6 )
	/// </code>
	///
	/// A seventh save could be enumerated and still not reached. Separate mechanism from
	/// `sub_82378618`'s save limit of 6 — do not fold them into one constant.
	/// </summary>
	public const int MaxCards = 6;

	/// <summary>
	/// The shipped page's own tint opacity while the fan is open — the page's Slide2 states
	/// <c>Background opacity 50</c>, the same value `choosecard` uses.
	/// </summary>
	public const float TintOpacity = 0.5f;

	/// <summary>The look this draws text with. Without it the description is absent, not wrong.</summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>The saves currently on the fan, in the guest's own enumeration order.</summary>
	public IReadOnlyList<Save> Saves => _saves;

	private readonly List<Save> _saves = new();

	/// <summary>
	/// Which of the six card slots the first save is drawn on, 1-based — the guest's
	/// `screen+76`, which is what centres a short list instead of left-aligning it. The guest
	/// computes it from the count alone at 0x826E0660..0x826E0698, transcribed here:
	///
	/// <code>
	/// 826e0670  cmplwi cr6, r11, 2       ; r11 = (screen+88 - screen+84) >> 2
	/// 826e0674  beq    cr6, -> li r11, 3
	/// 826e0678  ble    cr6, -> stw r24   ; count &lt;= 1
	/// 826e067c  cmplwi cr6, r11, 4
	/// 826e0680  bgt    cr6, -> stw r24   ; count &gt; 4
	/// 826e0684  li     r11, 2            ; count is 3 or 4
	/// </code>
	///
	/// Computed rather than read so the panel lays itself out without a guest; the guest's own
	/// number is carried alongside so a disagreement can be reported.
	/// </summary>
	public static int BaseSlotFor(int count) => count switch
	{
		2 => 3,
		3 or 4 => 2,
		// One save never reaches the carousel (that path commits directly at 0x826E0470); five
		// or six must start at card 1 or the last would index a seventh card.
		_ => 1,
	};

	/// <summary>The slot the first save is on, 1-based. Follows <see cref="SetSaves"/>.</summary>
	public int BaseSlot { get; private set; } = 1;

	/// <summary>The card slot a save is drawn on, 0-based into <see cref="Fan"/>.</summary>
	private int SlotOf(int save) => BaseSlot - 1 + save;

	/// <summary>
	/// How far the arc slides along x so the <paramref name="count"/> cards drawn from slot
	/// <paramref name="first"/> are centred where all six are: the midpoint of the shown cards'
	/// authored x brought to the midpoint of the full fan's. Zero for the full fan and for none.
	/// </summary>
	public static float ArcCentreOffset(int first, int count)
	{
		if (count <= 0 || first < 0 || first + count > MaxCards)
		{
			return 0f;
		}

		float full = (Fan[0].X + Fan[MaxCards - 1].X) * 0.5f;
		float shown = (Fan[first].X + Fan[first + count - 1].X) * 0.5f;
		return full - shown;
	}

	/// <summary>Which card is lifted. Always a real card while <see cref="Saves"/> is non-empty.</summary>
	public int Cursor { get; private set; }

	/// <summary>True when all six cards found shipped art to clone.</summary>
	public bool HasShippedArt { get; private set; }

	private readonly Node3D?[] _cards = new Node3D?[MaxCards];
	private readonly Node3D?[] _groups = new Node3D?[MaxCards];

	/// <summary>The cards' parent, standing at <see cref="ArcTransform"/> while the fan is drawn.</summary>
	private Node3D? _arc;

	/// <summary>Where each card's inner group is now, so a cursor move tweens from where it is.</summary>
	private readonly Vector3[] _groupFrom = new Vector3[MaxCards];
	private readonly float[] _rollFrom = new float[MaxCards];

	private Node3D? _description;
	private Node3D? _background;

	private double _elapsed;
	private bool _animating;

	/// <summary>
	/// Templates cloned out of the shipped page. Null means a stand-in will be drawn instead,
	/// which is deliberately distinguishable from the panel never having opened.
	/// </summary>
	public Node3D?[] CardTemplates { get; } = new Node3D?[MaxCards];

	public Node3D? DescriptionTemplate { get; set; }

	public Node3D? BackgroundTemplate { get; set; }

	public override void _Ready()
	{
		Build();
		Refresh();
	}

	/// <summary>
	/// Builds the panel from the shipped <c>loadgame</c> page and takes it off the screen.
	///
	/// ONE FACTORY, for the reason <see cref="Engine_FrontEndCardChoice.FromPresentation"/>
	/// gives: the game and the harness must not disagree about what the panel is made of.
	///
	/// It HIDES the page it sourced from. With Godot drawing the fan, leaving the guest's six
	/// components live is two fans on one screen — and their being parked at y -598 is not
	/// protection, because a live slide from the guest brings them straight back.
	///
	/// <paramref name="page"/> may be null, which is not a failure: the fan falls back to
	/// stand-ins and says so, rather than inventing card art.
	/// </summary>
	public static Engine_FrontEndSaveCards FromPresentation(Node3D? page, string name = "SaveCards")
	{
		var panel = new Engine_FrontEndSaveCards
		{
			Name = name,
			Visible = false,
		};

		for (int i = 0; i < MaxCards; i++)
		{
			// The authored transform goes: every slide states each card's position, rotation and
			// scale outright, so keeping it would fight the fan table.
			panel.CardTemplates[i] = Clone(page, CardName(i), keepTransform: false);
		}

		// The cards' PARENT keeps its transform: the slides state each card inside
		// `loadgame.Group`, and that group is authored off the page's origin.
		panel.ArcTransform = AuthoredArc(page);

		// The description and the tint keep theirs: no slide touches either element's transform,
		// only its opacity, so the authored transform IS their layout.
		panel.DescriptionTemplate = Clone(page, DescriptionName, keepTransform: true);
		panel.BackgroundTemplate = Clone(page, BackgroundName, keepTransform: true);

		if (page is not null)
		{
			// The page's place on the canvas, from the built node: the slide table is in the
			// page's LOCAL space and the page is a child of `Layer`, not the screen root.
			// Only when in the tree — the caller may hand us the pattern book's detached copy,
			// and `GlobalTransform` outside the tree is an engine error returning identity.
			if (page.IsInsideTree())
			{
				panel.Transform = page.GlobalTransform;
			}

			page.Visible = false;
		}

		return panel;
	}

	/// <summary>
	/// The transform of card 1's authored parent chain, composed up to (not including) the page.
	/// Identity when the card sits directly under the page or was not found.
	/// </summary>
	private static Transform3D AuthoredArc(Node3D? page)
	{
		Transform3D arc = Transform3D.Identity;
		if (page is null || Engine_UiTheme.FindByName(page, CardName(0)) is not Node3D card)
		{
			return arc;
		}

		for (Node? node = card.GetParent(); node is Node3D parent && !ReferenceEquals(node, page); node = node.GetParent())
		{
			arc = parent.Transform * arc;
		}

		return arc;
	}

	private static Node3D? Clone(Node3D? page, string name, bool keepTransform)
	{
		if (page is null || Engine_UiTheme.FindByName(page, name) is not Node3D found)
		{
			return null;
		}

		// Duplicated rather than reparented: the original stays live inside its own screen,
		// which is rebuilt from the file on every guest state change.
		var clone = (Node3D)found.Duplicate();
		if (!keepTransform)
		{
			clone.Transform = Transform3D.Identity;
		}

		clone.Visible = true;
		return clone;
	}

	/// <summary>
	/// Builds the tint, the six cards and the description once. Persistent nodes, not rebuilt per
	/// change: the raise is a tween and must hang off a node that survives it.
	/// </summary>
	private void Build()
	{
		if (BackgroundTemplate is not null)
		{
			BackgroundTemplate.Name = "Background";
			AddChild(BackgroundTemplate);
			_background = BackgroundTemplate;
		}

		// The cards stand in their authored parent group, as the file lays them out.
		_arc = new Node3D { Name = CardGroupName, Transform = ArcTransform };
		AddChild(_arc);

		bool shipped = true;
		for (int i = 0; i < MaxCards; i++)
		{
			Node3D card = CardNode(CardTemplates[i], $"Card{i + 1}");
			shipped &= CardTemplates[i] is not null;

			// The fan pose goes on the CARD; the raise goes on the group inside it. Applied here
			// rather than per frame because the fan never moves — only the selection does.
			card.Transform = Fan[i].ToTransform(CardScale);
			_arc.AddChild(card);
			_cards[i] = card;

			_groups[i] = Engine_UiTheme.FindByName(card, CardGroupName) as Node3D;
			_groupFrom[i] = Vector3.Zero;
			_rollFrom[i] = 0f;
		}

		HasShippedArt = shipped;

		if (DescriptionTemplate is not null)
		{
			DescriptionTemplate.Name = "Description";
			AddChild(DescriptionTemplate);
			_description = DescriptionTemplate;
		}
	}

	/// <summary>
	/// One card node: the shipped art when handed it, otherwise a flat stand-in, so "no art" and
	/// "never opened" stay distinguishable. The stand-in's size is the tarot frame's footprint —
	/// the card quads are authored at scale 3.2 x 5.2 over the builder's 100-unit base quad, so
	/// 320 by 520 before the fan's 0.65. It still gets a <c>Group</c> child, the node the raise
	/// moves.
	/// </summary>
	private static Node3D CardNode(Node3D? template, string name)
	{
		if (template is not null)
		{
			template.Name = name;
			return template;
		}

		var card = new Node3D { Name = name };
		var group = new Node3D { Name = CardGroupName };
		group.AddChild(new MeshInstance3D
		{
			Name = "front",
			Mesh = new QuadMesh { Size = new Vector2(StandInWidth, StandInHeight) },
			MaterialOverride = new StandardMaterial3D
			{
				Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				CullMode = BaseMaterial3D.CullModeEnum.Disabled,
				AlbedoColor = new Color(0.55f, 0.42f, 0.26f, 0.9f),
			},
		});

		card.AddChild(group);
		return card;
	}

	private const float StandInWidth = 320f;
	private const float StandInHeight = 520f;

	/// <summary>
	/// Puts a list of saves on the fan and selects the first. More than six is truncated rather
	/// than refused, and <see cref="Describe"/> says so — the guest's write path returns 0 once
	/// six exist, so a longer list means the caller's storage query disagrees with its cap.
	/// <paramref name="baseSlot"/> is the guest's own first card where it states one: the co-op
	/// picker centres even a single save (`sub_8243B070`: one or two saves start at card 3).
	/// </summary>
	public void SetSaves(IReadOnlyList<Save> saves, int? baseSlot = null)
	{
		_saves.Clear();
		for (int i = 0; i < saves.Count && i < MaxCards; i++)
		{
			_saves.Add(saves[i]);
		}

		BaseSlot = baseSlot is >= 1 and <= MaxCards ? baseSlot.Value : BaseSlotFor(_saves.Count);
		Cursor = 0;
		_lockLeft = _lockRight = 0.0;
		ApplyPortraits();
		Refresh();
	}

	/// <summary>
	/// Puts each save's portrait on the card it is drawn on. Only the albedo changes — the atlas
	/// rectangle rides on `Uv1Scale`/`Uv1Offset` and a portrait is a whole texture. A save with
	/// no portrait keeps the authored `loadingcardback`.
	/// </summary>
	private void ApplyPortraits()
	{
		int first = BaseSlot - 1;
		for (int i = 0; i < _saves.Count; i++)
		{
			int slot = first + i;
			if (slot < 0 || slot >= MaxCards || _cards[slot] is not { } card ||
				_saves[i].Portrait is not { } portrait)
			{
				continue;
			}

			if (Engine_UiTheme.FindByName(card, PortraitName) is MeshInstance3D quad)
			{
				// The clone owns its own material (`CloneFace` duplicates the node, the builder
				// gives each quad its own), so this cannot leak onto the shipped card.
				Engine_UiSceneBuilder.SetQuadTexture(quad, portrait);
			}
		}
	}

	/// <summary>
	/// One frame of the fan's left/right, returning true when the selection moved.
	///
	/// The fan is C++, not the list's Lua clock: `sub_826DEE40` (state 5, codes 30/31) keeps a
	/// lockout float per direction at `screen+212` (left) and `+216` (right). A code is honoured
	/// only while its lockout is at or below zero; a move that succeeded (`sub_826E0BE0`
	/// returned 1) writes 0.25 to its own and ZERO to the other's; a move refused at the edge
	/// writes 0.5. No first/subsequent interval and no count — the stick translator re-emits the
	/// code while held.
	/// </summary>
	public bool Navigate(int direction, double delta)
	{
		_lockLeft = System.Math.Max(0.0, _lockLeft - delta);
		_lockRight = System.Math.Max(0.0, _lockRight - delta);
		if (direction == 0)
		{
			return false;
		}

		ref double own = ref direction < 0 ? ref _lockLeft : ref _lockRight;
		ref double other = ref direction < 0 ? ref _lockRight : ref _lockLeft;
		if (own > 0.0)
		{
			return false;
		}

		if (Move(direction))
		{
			own = MoveLockout;
			other = 0.0;
			return true;
		}

		own = EdgeLockout;
		return false;
	}

	/// <summary>
	/// The save whose card is under a canvas point, or -1. Where the fanned cards overlap the
	/// nearer one wins (larger z) — the lifted card stands in front of its neighbours.
	/// </summary>
	public int SaveAt(Vector2 at)
	{
		int best = -1;
		float bestZ = float.NegativeInfinity;
		for (int save = 0; save < _saves.Count; ++save)
		{
			int slot = SlotOf(save);
			if (slot < 0 || slot >= MaxCards || _cards[slot] is not { Visible: true } card ||
				Engine_UiPointer.CanvasBounds(card) is not { } bounds || !bounds.HasPoint(at))
			{
				continue;
			}

			float z = (_groups[slot] ?? card).GlobalPosition.Z;
			if (z > bestZ)
			{
				best = save;
				bestZ = z;
			}
		}

		return best;
	}

	/// <summary>Lifts the card of one save, as the stick would reach it. True when it moved.</summary>
	public bool Select(int save)
	{
		if (save < 0 || save >= _saves.Count || save == Cursor)
		{
			return false;
		}

		Cursor = save;
		Refresh();
		return true;
	}

	/// <summary>`screen+212/216 = 0.25` after a move that happened.</summary>
	private const double MoveLockout = 0.25;

	/// <summary>`screen+212/216 = 0.5` after a move the edge refused.</summary>
	private const double EdgeLockout = 0.5;

	private double _lockLeft;

	private double _lockRight;

	private bool Move(int direction)
	{
		int next = Cursor + direction;
		if (next < 0 || next >= _saves.Count)
		{
			return false;
		}

		Cursor = next;
		Refresh();
		return true;
	}

	/// <summary>
	/// Confirms the selected card and returns the index to load, or -1 when the fan is empty.
	/// An index, not a name: the guest identifies a save by the address of its handle slot in its
	/// own vector — `Frontend_SetStartGameTarget` takes `*(screen+84) + 4*i`.
	/// </summary>
	public int Activate() => _saves.Count == 0 ? -1 : Cursor;

	/// <summary>
	/// Puts the panel back to the state it opens in. Called on the way IN, not out: the guest
	/// re-enters through the same slide every time.
	/// </summary>
	public void Reset()
	{
		Cursor = 0;
		_lockLeft = _lockRight = 0.0;
		Refresh();
	}

	/// <summary>
	/// Shows the cards the save list reaches, hides the rest, and starts the move tween. A card
	/// with no save is HIDDEN, not parked: reusing the closed slide's y -598 would stack blank
	/// cards below the canvas for a later open-tween to fly in.
	/// </summary>
	public void Refresh()
	{
		// Visibility is by SLOT, not by save index: with three saves the base slot is 2, so
		// cards 2, 3 and 4 draw. Testing `i < _saves.Count` gives the left-aligned fan.
		int first = BaseSlot - 1;
		for (int i = 0; i < MaxCards; i++)
		{
			if (_cards[i] is not { } card)
			{
				continue;
			}

			card.Visible = i >= first && i < first + _saves.Count;

			if (_groups[i] is { } group)
			{
				_groupFrom[i] = group.Position;
				_rollFrom[i] = group.Rotation.Z;
			}
		}

		SetDescription(_saves.Count == 0 ? string.Empty : _saves[Cursor].Description);
		LayCards();

		_elapsed = 0.0;
		_animating = true;
		ApplyPoses(0f);
	}

	// THE PC ROW. With the keyboard and mouse the fan's overlap is a liability — the pointer
	// lands on two cards at once — so the cards stand in one straight row instead, upright and
	// apart, scaled to fit, the selected one enlarged and brought forward. The controller gets
	// the shipped fan back the moment it is touched.

	private const float RowGap = 18f;

	/// <summary>The widest the row may run: inside the book's frame, with the selected card's
	/// growth to spare.</summary>
	private const float RowSpan = 740f;

	private const float RowY = 30f;

	private const float SelectedGrowth = 1.12f;

	/// <summary>Which layout the cards stand in now.</summary>
	private bool _laidOutPc;

	/// <summary>A card's drawn width at scale 1, measured from its meshes; 0 until measured.</summary>
	private float _cardWidth;

	private readonly Transform3D[] _cardFrom = new Transform3D[MaxCards];

	private readonly Transform3D[] _cardTo = new Transform3D[MaxCards];

	/// <summary>
	/// The narrowest card's width in its own space at scale 1. Measured in card space so the
	/// fan's yaw and the card's scale drop out; the narrowest so the lifted card's roll does not
	/// widen it.
	/// </summary>
	private float CardWidth()
	{
		if (_cardWidth > 0f)
		{
			return _cardWidth;
		}

		float width = float.PositiveInfinity;
		foreach (Node3D? card in _cards)
		{
			if (card is null)
			{
				continue;
			}

			Rect2? local = null;
			LocalBounds(card, card.GlobalTransform.AffineInverse(), ref local);
			if (local is { } rect && rect.Size.X > 0f)
			{
				width = Mathf.Min(width, rect.Size.X);
			}
		}

		_cardWidth = float.IsFinite(width) ? width : 0f;
		return _cardWidth;
	}

	private static void LocalBounds(Node node, Transform3D toCard, ref Rect2? bounds)
	{
		if (node is MeshInstance3D { Mesh: not null } mesh)
		{
			Aabb box = toCard * mesh.GlobalTransform * mesh.GetAabb();
			var rect = new Rect2(box.Position.X, box.Position.Y, box.Size.X, box.Size.Y);
			bounds = bounds is { } so ? so.Merge(rect) : rect;
		}

		foreach (Node child in node.GetChildren())
		{
			LocalBounds(child, toCard, ref bounds);
		}
	}

	private static bool PcMode => Engine_UiInputMode.KeyboardMouse;

	/// <summary>Stands every card in the fan, or in the PC row with the selection enlarged.</summary>
	private void LayCards()
	{
		bool switched = _laidOutPc != PcMode;
		_laidOutPc = PcMode;

		int first = BaseSlot - 1;
		int count = Mathf.Max(1, _saves.Count);

		// The fan stands where the file puts its group, slid so the cards actually shown sit
		// where the full six would: the shipped game leaves a short fan hanging off one side
		// (five saves draw slots 1..5, so card 1 at x -335 stands alone on the left). The PC row
		// is laid out about the page's own origin, so the authored offset applies to the fan alone.
		if (_arc is not null)
		{
			_arc.Transform = _laidOutPc
				? Transform3D.Identity
				: ArcTransform.TranslatedLocal(new Vector3(ArcCentreOffset(first, _saves.Count), 0f, 0f));
		}
		float width = CardWidth();
		float scale = width <= 0f
			? CardScale
			: Mathf.Min(CardScale, (RowSpan - (RowGap * (count - 1))) / (count * width));
		float pitch = (width * scale) + RowGap;
		for (int i = 0; i < MaxCards; i++)
		{
			if (_cards[i] is not { } card)
			{
				continue;
			}

			if (!_laidOutPc)
			{
				card.Transform = Fan[i].ToTransform(CardScale);
				continue;
			}

			int k = i - first;
			bool selected = _saves.Count > 0 && k == Cursor;
			float s = scale * (selected ? SelectedGrowth : 1f);
			_cardTo[i] = new Transform3D(
				Basis.Identity.Scaled(new Vector3(s, s, 1f)),
				new Vector3((k - ((count - 1) * 0.5f)) * pitch, RowY, selected ? 0f : -20f));

			// Entering the row snaps; within it the growth rides the move tween.
			_cardFrom[i] = switched ? _cardTo[i] : card.Transform;
			card.Transform = _cardFrom[i];
		}
	}

	/// <summary>
	/// Advances the move. Cards move only here, so the tween runs at the display rate rather
	/// than at whatever rate the caller polls navigation.
	/// </summary>
	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("frontendsavecards");
		if (_laidOutPc != PcMode && _cards[0] is not null)
		{
			Refresh();
		}

		if (!_animating)
		{
			return;
		}

		_elapsed += delta;
		double duration = MoveMilliseconds / 1000.0;
		float t = duration <= 0.0 ? 1f : (float)(_elapsed / duration);
		if (t >= 1f)
		{
			t = 1f;
			_animating = false;
		}

		ApplyPoses(t);
	}

	/// <summary>
	/// Writes every card's inner group at a point on its own interpolation. The selected card
	/// goes to the origin and every other one to <see cref="Row"/>. A plain clamped lerp, as the
	/// engine does — no easing curve.
	/// </summary>
	private void ApplyPoses(float t)
	{
		for (int i = 0; i < MaxCards; i++)
		{
			if (_groups[i] is not { } group)
			{
				continue;
			}

			// The selected card rises: `sub_826E0BE0` puts the new card on Slide1 and the one it
			// left on Slide2. Slide1's commands park `Group` at the origin and its tracks
			// (records 2741..2746) carry it to (25, 199, roll -0.14) over 250 ticks; Slide2 runs
			// them back. Cards never selected sit on the master slide, at the origin.
			// The PC row lifts nothing: the selected card is enlarged in place instead, so no
			// card ever rises over its neighbour.
			bool selected = _saves.Count > 0 && i == SlotOf(Cursor) && !_laidOutPc;
			Vector3 to = selected ? Row[i] : Vector3.Zero;
			float roll = selected ? RowRoll[i] : 0f;

			group.Position = _groupFrom[i].Lerp(to, t);
			group.Rotation = new Vector3(0f, 0f,
				_rollFrom[i] + ((roll - _rollFrom[i]) * t));

			if (_laidOutPc && _cards[i] is { } card)
			{
				card.Transform = _cardFrom[i].InterpolateWith(_cardTo[i], t);
			}
		}
	}

	/// <summary>
	/// Re-texts <c>SaveGameDescription</c> in place, keeping the file's layout — the scene builder
	/// stamps the artist's point size, alignment, wrap mode and box onto the node, so
	/// regenerating the glyphs from those values reproduces it exactly.
	/// </summary>
	private void SetDescription(string display)
	{
		if (_description is not MeshInstance3D node || Theme is null)
		{
			return;
		}

		int size = node.HasMeta("text_size") ? (int)node.GetMeta("text_size") : 20;
		int horizontal = node.HasMeta("text_halign") ? (int)node.GetMeta("text_halign") : 1;
		int vertical = node.HasMeta("text_valign") ? (int)node.GetMeta("text_valign") : 0;
		int wrap = node.HasMeta("text_wrap") ? (int)node.GetMeta("text_wrap") : 1;
		Vector2 box = node.HasMeta("text_box") ? (Vector2)node.GetMeta("text_box") : Vector2.Zero;
		Color colour = node.HasMeta("text_colour") ? (Color)node.GetMeta("text_colour") : Colors.White;

		// Opaque, as Engine_UiSceneBuilder builds text: the engine overwrites a .bgf's own text
		// alpha with 1.0.
		var opaque = new Color(colour.R, colour.G, colour.B, 1f);

		Theme.Paint(
			node, display, size, opaque,
			horizontalAlign: horizontal, verticalAlign: vertical,
			boxWidth: box.X, boxHeight: box.Y, wrapMode: wrap);
	}

	/// <summary>Finds the shipped page inside a built presentation. Null means it is not there.</summary>
	public static Node3D? FindPage(Node root) =>
		Engine_UiTheme.FindByName(root, PageName) as Node3D;

	/// <summary>
	/// A one-line description of what is on screen. Reports where the selected card actually
	/// ended up in world space, not where the table says — the two differ whenever the page's own
	/// transform is not what was assumed.
	/// </summary>
	public string Describe()
	{
		int slot = SlotOf(Cursor);
		string where = _saves.Count > 0 && slot >= 0 && slot < MaxCards && _cards[slot] is { } card
			? card.GlobalPosition.ToString()
			: "none";

		int portraits = 0;
		foreach (Save save in _saves)
		{
			if (save.Portrait is not null)
			{
				++portraits;
			}
		}

		return $"[save-cards] saves={_saves.Count} portraits={portraits}/{_saves.Count} cursor={Cursor} slot={slot + 1}/{BaseSlot} " +
			$"art={(HasShippedArt ? "shipped" : "STAND-IN")} " +
			$"tint={(_background is null ? "none" : "shipped")} " +
			$"text={(_description is null ? "NOT FOUND" : "shipped")} " +
			$"page={Position} selected={where}";
	}
}
