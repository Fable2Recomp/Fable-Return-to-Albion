using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Fable 2's Load Game card fan, drawn in Godot from the game's own card art.
///
/// WHY THIS EXISTS. The front-end menu is Godot's now, and "Load Game" was the one root entry
/// that still had nowhere to go: <see cref="Engine_FrontEndMenu"/> dispatches the name, the
/// guest enumerates its saves and puts its own carousel up, and Godot mirrored the RESULT
/// without being able to move it — the same half-state the row menu was in before it was
/// taken over. This is the other half.
///
/// IT IS THE GENDER CARD'S CONTRACT, NOT A NEW ONE, and that is what makes it safe to own.
/// `frontendmainmenu.bgf` ships a `loadgame` component under `Component_0/Layer`, beside
/// `choosecard` and `dialog`, and the shipped Lua contributes exactly two behaviours to it:
///
/// <code>
/// behaviorloadcard.lua      onDeactivate -> fireEvent('LOAD_CARD_ACTIVE',   parent)
/// behaviorloadselected.lua  onDeactivate -> fireEvent('LOAD_CARD_SELECTED', parent)
/// </code>
///
/// That is the same shape as `BOY_ACTIVE` / `BOY_SELECTED`: the presentation's own Behavior
/// elements answer BACK to C++, and nothing in Lua enumerates a slot, lays a card out, or
/// counts anything. A sweep of all 587 shipped chunks finds no reference to `Card1`..`Card6`,
/// `SaveGameDescription`, `LoadingCardBack` or `cardback` anywhere. So — unlike the row menu,
/// whose every number came out of `expandablemenuformatting.lua` — the layout here is in the
/// FILE, and this class reads it from the slide table rather than from a script.
///
/// SIX CARDS, HARD-AUTHORED, which is the save cap agreeing with itself from the other side:
/// `sub_82378618` stores 6 over `dword_83319620` on front-end entry, and the artist drew six
/// card components. A seventh save has nowhere to be drawn because there cannot be one.
///
/// WHAT THE SLIDES SAY. The `loadgame` component carries three slides and each card carries
/// three of its own, and the two tiers do different jobs:
///
/// <list type="bullet">
/// <item>The PAGE's Slide1 parks all six cards at <c>position.y -598</c> with the description
/// and the tint at opacity 0 — off the bottom of the canvas. That is the closed state, and
/// its own duration is 764 ms.</item>
/// <item>The PAGE's Slide2 states the fan outright — every card's position, yaw and roll, the
/// description at opacity 100 and the tint at 50 — over 508 ms. That is <see cref="Fan"/>,
/// read rather than chosen.</item>
/// <item>EACH CARD's Slide1 and Slide2 move its inner <c>Group</c>: Slide1 to the origin,
/// Slide2 up and out of the arc by 118 to 199 units. Their durations are 250-257 ms, which is
/// one authored value with the artist's jitter on it.</item>
/// </list>
///
/// THE SELECTED CARD IS THE ONE THAT DOES NOT LIFT, and reading it the other way round is the
/// obvious mistake here — a raised card looks like a highlighted one. The guest says otherwise
/// outright. `sub_826E0BE0` is its cursor mover and it re-slides exactly two cards:
///
/// <code>
/// 826e0c8c  sub_82426CA8(v7,  a1[5], 2);   // the card being LEFT   -> slide 2
/// 826e0c9c  sub_82426CA8(v10, a1[5], 1);   // the card being MOVED TO -> slide 1
/// </code>
///
/// A guest slide index is 1-based over the NON-master slides — the convention
/// <see cref="Engine_BgfDocument.SetElementSlides"/> already mirrors live state through — so
/// slide 1 is `Slide1`, the origin, and slide 2 is `Slide2`, the lift. The selection therefore
/// sits at its arc position and everything else is held up above it.
///
/// The geometry agrees once it is read that way. Adding each card's lift to its own place in
/// the arc puts the five unselected cards at y 170, 174, 170, 189, 199 and 211 — a rough ROW
/// across the top — while the selected one alone drops back into the arc at y 0 to 55, over
/// the `SaveGameDescription` box the artist put at y -195. That is also why the lift is
/// per-card rather than one offset: card 1 starts at y 0 and needs 199 to reach the row, card
/// 3 starts at y 55 and needs 134. Under the inverted reading those six numbers mean nothing.
///
/// The two tiers compose because they write different nodes: the arc is on the card component
/// and the lift is on the <c>Group</c> INSIDE it, so a card can be placed and lifted at once
/// without either pose overwriting the other.
///
/// WHAT THIS DOES NOT OWN: which saves exist, or what they are called. Enumerating a save is a
/// live `XamContentCreateEnumerator` on the guest's side — a CALL, not a read — so the list
/// arrives from the caller as <see cref="Save"/> records and this draws whatever it is handed.
/// A view that went and asked would be a view that cannot be stood up without a guest, which
/// is the mistake <see cref="Engine_FrontEndMenu.PopulateLanguages"/> already had to undo.
/// </summary>
public partial class Engine_FrontEndSaveCards : Node3D
{
	/// <summary>
	/// One save, as much of it as a card shows.
	///
	/// <see cref="Description"/> is what goes in `SaveGameDescription`, already resolved — the
	/// element is a plain text box (20pt, centred, wrapped into 411x96) and the guest writes a
	/// finished string into it rather than a tag.
	/// </summary>
	public readonly record struct Save(string Description, Texture2D? Portrait = null);

	/// <summary>
	/// The quad inside a card that carries its portrait.
	///
	/// AUTHORED AS A PLACEHOLDER. Every one of the six ships pointing at
	/// `art\gui\questscreenshots\loadingcardback.tex`, and the real picture is chosen at load
	/// time from the save's own quest: `sub_826E1408` derives a texture hash from the record's
	/// QuestName and stamps it into the element's record at `+512`. So a card drawn straight
	/// from the file shows six identical backs — which is exactly what a run showed — and the
	/// portrait has to come from the guest.
	/// </summary>
	public const string PortraitName = "screenshot";

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
	/// The open fan — the <c>loadgame</c> component's Slide2, verbatim.
	///
	/// CARD 1 IS NOT PART OF THE ARC and that is the file's, not a transcription slip. Cards
	/// 2..6 run x -200, -100, 0, 100, 200 with y 38, 55, 52, 38, 0 and a constant yaw of
	/// -0.3491 (-20 degrees); card 1 sits further out at x -335 with y 0 and NO yaw at all, so
	/// it faces the reader flat while the rest are turned. The rolls are symmetric across all
	/// six — ±0.4538, ±0.2094, ±0.0698 — which is what makes it read as one spread anyway.
	///
	/// Only card 1's slide states a scale (0.65 on x and y); the other five inherit the
	/// authored transform, which is the same 0.65. <see cref="CardScale"/> holds it once.
	/// </summary>
	public static readonly Pose[] Fan =
	{
		new(-335f, 0f, -10f, 0f, 0.4538f),
		new(-200f, 38f, -20f, -0.3491f, 0.2094f),
		new(-100f, 55f, -40f, -0.3491f, 0.0698f),
		new(0f, 52f, -60f, -0.3491f, -0.0698f),
		new(100f, 38f, -80f, -0.3491f, -0.2094f),
		new(200f, 0f, -100f, -0.3491f, -0.4538f),
	};

	/// <summary>Every card's authored scale; stated on card 1's slide and inherited by the rest.</summary>
	public const float CardScale = 0.65f;

	/// <summary>
	/// Where a card sits when it is NOT the selection — each card's own Slide2, applied to its
	/// inner <c>Group</c>.
	///
	/// See the class remarks: this is the row the five unselected cards are held up in, not a
	/// highlight. The selected card's Slide1 puts its group back at the origin, so this table is
	/// the whole of the difference between the two states.
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
	/// The roll the row adds, in radians, on top of the card's own place in the arc. Cards 4, 5
	/// and 6 DO NOT ROTATE — their slides carry 13 commands where cards 1..3 carry 16, and the
	/// three missing ones are exactly the rotation writes. A table that gave them a roll would
	/// be inventing one.
	/// </summary>
	public static readonly float[] RowRoll = { -0.1396f, 0.0873f, 0.0873f, 0f, 0f, 0f };

	/// <summary>Where the closed page parks every card — the page's Slide1 <c>position.y</c>.</summary>
	public const float ClosedY = -598f;

	/// <summary>The page's own slide durations, in milliseconds.</summary>
	public const int OpenMilliseconds = 508;
	public const int CloseMilliseconds = 764;

	/// <summary>
	/// How long a card takes to move between the row and the arc. The six cards' own slides are
	/// 250, 251, 252, 253, 254 and 257 — one intended value with the artist's hand on it, so
	/// this is the round number rather than a per-card table that would imply a distinction
	/// nobody made.
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
	/// How many cards the page ships, and the most this can ever draw.
	///
	/// SIX FROM BOTH SIDES. The artist drew six card components, and the guest's own cursor
	/// mover clamps to the same number independently of the art — `sub_826E0BE0` at 0x826E0C20
	/// rejects an index at or past <c>min(count, 6)</c>:
	///
	/// <code>
	/// v4 = a1[22] - a1[21];                                    // end - begin, the filename vector
	/// if ( a2 &lt; 0 || a2 &gt;= ((((v4 &gt;&gt; 2) - 6) &gt;&gt; 31) &amp; ((v4 &gt;&gt; 2) - 6)) + 6 )
	/// </code>
	///
	/// which is <c>min(count - 6, 0) + 6</c>. So a seventh save could be enumerated and still
	/// could not be reached, and the cap agreeing with `sub_82378618`'s save limit of 6 is not a
	/// coincidence to be tidied into one constant — they are two different mechanisms.
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
	/// `screen+76`, and the reason a short list is CENTRED rather than left-aligned.
	///
	/// Without it the fan is wrong in the most ordinary case there is. The arc runs x -335,
	/// -200, -100, 0, 100 and 200, so three saves laid on cards 1..3 bunch into the left third
	/// of the screen with the right two thirds empty — which is exactly what the first capture
	/// of this panel showed.
	///
	/// The guest computes it from the count alone, at 0x826E0660..0x826E0698, and this is that
	/// branch verbatim:
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
	/// COMPUTED HERE RATHER THAN READ, even though the bridge reports the guest's value too.
	/// It is a pure function of a count this class already has, and the alternative is a panel
	/// that cannot lay itself out without a guest — which is the same objection that moved the
	/// language list and the save count out of <see cref="Engine_FrontEndMenu"/>. The guest's
	/// own number is carried alongside so a run where the two disagree can say so.
	/// </summary>
	public static int BaseSlotFor(int count) => count switch
	{
		2 => 3,
		3 or 4 => 2,
		// One save never reaches the carousel at all — that path commits directly at
		// 0x826E0470 — and five or six must start at card 1 or the last one would index a
		// seventh card that does not exist.
		_ => 1,
	};

	/// <summary>The slot the first save is on, 1-based. Follows <see cref="SetSaves"/>.</summary>
	public int BaseSlot { get; private set; } = 1;

	/// <summary>The card slot a save is drawn on, 0-based into <see cref="Fan"/>.</summary>
	private int SlotOf(int save) => BaseSlot - 1 + save;

	/// <summary>Which card is lifted. Always a real card while <see cref="Saves"/> is non-empty.</summary>
	public int Cursor { get; private set; }

	/// <summary>True when all six cards found shipped art to clone.</summary>
	public bool HasShippedArt { get; private set; }

	private readonly Node3D?[] _cards = new Node3D?[MaxCards];
	private readonly Node3D?[] _groups = new Node3D?[MaxCards];

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
			// THE AUTHORED TRANSFORM GOES, because every slide states each card's position,
			// rotation and scale outright — the artist's placement is a starting frame the
			// runtime overwrites, and keeping it would fight the fan table.
			panel.CardTemplates[i] = Clone(page, CardName(i), keepTransform: false);
		}

		// THE DESCRIPTION AND THE TINT KEEP THEIRS, and that split is the file's. Not one slide
		// touches either element's transform — they set only opacity — so the authored transform
		// IS the layout: it is what puts the text box under the fan and what stretches the tint
		// across the screen.
		panel.DescriptionTemplate = Clone(page, DescriptionName, keepTransform: true);
		panel.BackgroundTemplate = Clone(page, BackgroundName, keepTransform: true);

		if (page is not null)
		{
			// The page's own place on the canvas, taken from the built node rather than
			// hardcoded: the slide table is expressed in the page's LOCAL space, and the page is
			// a child of `Layer` rather than of the screen root.
			//
			// ONLY IF IT IS IN THE TREE. The caller may hand us the PATTERN BOOK's copy — a
			// detached tree kept as an art source — and `GlobalTransform` on a node outside the
			// scene tree is an engine error that returns identity. It logged one per rebuild,
			// which is noise over a real fault rather than a fault itself: `GameUi` overwrites
			// this with the LIVE page's transform straight afterwards. Silence here is right
			// because the detached case genuinely has no global transform to offer.
			if (page.IsInsideTree())
			{
				panel.Transform = page.GlobalTransform;
			}

			page.Visible = false;
		}

		return panel;
	}

	private static Node3D? Clone(Node3D? page, string name, bool keepTransform)
	{
		if (page is null || Engine_UiTheme.FindByName(page, name) is not Node3D found)
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
	/// Builds the tint, the six cards and the description once.
	///
	/// Persistent nodes rather than rebuilt per change, for the reason the row menu keeps its
	/// twelve slots: the raise is a tween, and a tween has to hang off a node that survives the
	/// change it is animating.
	/// </summary>
	private void Build()
	{
		if (BackgroundTemplate is not null)
		{
			BackgroundTemplate.Name = "Background";
			AddChild(BackgroundTemplate);
			_background = BackgroundTemplate;
		}

		bool shipped = true;
		for (int i = 0; i < MaxCards; i++)
		{
			Node3D card = CardNode(CardTemplates[i], $"Card{i + 1}");
			shipped &= CardTemplates[i] is not null;

			// The fan pose goes on the CARD; the raise goes on the group inside it. Applied here
			// rather than per frame because the fan never moves — only the selection does.
			card.Transform = Fan[i].ToTransform(CardScale);
			AddChild(card);
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
	/// One card node: the shipped art if we were handed it, otherwise a flat stand-in.
	///
	/// A stand-in rather than nothing, for the reason the gender card gives — "the fan is up
	/// with no art" and "the fan never opened" are different faults that look identical when a
	/// missing clone draws nothing. The size is the tarot frame's own footprint: the card quads
	/// are authored at scale 3.2 x 5.2 over the builder's 100-unit base quad, so a card is 320
	/// by 520 before the fan's own 0.65.
	///
	/// A stand-in still gets a <c>Group</c> child, because that is the node the raise moves and
	/// a panel whose selection does not visibly lift is not testable.
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
	/// Puts a list of saves on the fan and selects the first.
	///
	/// MORE THAN SIX IS TRUNCATED rather than refused, and it says so in <see cref="Describe"/>.
	/// The guest cannot produce a seventh — its own write path returns 0 once six exist — so a
	/// longer list means the caller's storage query disagrees with the guest's cap, and drawing
	/// six of them is a better answer than drawing none.
	/// </summary>
	public void SetSaves(IReadOnlyList<Save> saves)
	{
		_saves.Clear();
		for (int i = 0; i < saves.Count && i < MaxCards; i++)
		{
			_saves.Add(saves[i]);
		}

		BaseSlot = BaseSlotFor(_saves.Count);
		Cursor = 0;
		_held = 0;
		ApplyPortraits();
		Refresh();
	}

	/// <summary>
	/// Puts each save's portrait on the card it is drawn on.
	///
	/// The texture goes on the material rather than replacing the mesh, because the quad's UV
	/// window is the artist's — the atlas rectangle rides on `Uv1Scale`/`Uv1Offset` and a
	/// portrait is a whole texture, so only the albedo changes. A save with no portrait keeps
	/// the authored `loadingcardback`, which is the right picture for a card whose quest has no
	/// screenshot of its own.
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
				// The clone already owns its material — `CloneFace` duplicates the node and the
				// builder gives each quad its own material — so writing the texture here cannot
				// leak onto the shipped card the art came from.
				Engine_UiSceneBuilder.SetQuadTexture(quad, portrait);
			}
		}
	}

	/// <summary>The direction currently held, and the auto-repeat clock behind it.</summary>
	private int _held;

	private double _sinceNavigate;

	private double _navigateInterval = Engine_FrontEndMenu.FirstNavigateInterval;

	private int _navigateCount;

	/// <summary>
	/// Moves the selection. <paramref name="direction"/> is -1 left, +1 right, 0 released.
	///
	/// AUTO-REPEATING, unlike the gender card and for the reason that panel's remarks give from
	/// the other side: there, LEFT and RIGHT name the two cards absolutely and a repeat has
	/// nothing left to do. Here they are RELATIVE — the cursor walks a list of up to six — so
	/// the same curve the row menu uses applies, and it is the same curve because it comes from
	/// the same place: `guistateandconstants.lua`'s <c>GUI.Timing</c>, which is GUI-wide rather
	/// than the expandable menu's own.
	///
	/// It CLAMPS at both ends rather than wrapping, matching the row menu. Returns true when the
	/// selection moved.
	/// </summary>
	public bool Navigate(int direction, double delta)
	{
		if (direction == 0)
		{
			_held = 0;
			_navigateCount = 0;
			_navigateInterval = Engine_FrontEndMenu.FirstNavigateInterval;
			_sinceNavigate = 0.0;
			return false;
		}

		if (_held != direction)
		{
			_held = direction;
			_navigateCount = 0;
			_navigateInterval = Engine_FrontEndMenu.FirstNavigateInterval;
			_sinceNavigate = 0.0;
			return Move(direction);
		}

		_sinceNavigate += delta;
		if (_sinceNavigate < _navigateInterval)
		{
			return false;
		}

		_sinceNavigate = 0.0;
		if (_navigateCount < Engine_FrontEndMenu.InputIntervalClamp)
		{
			++_navigateCount;
			double t = (double)_navigateCount / Engine_FrontEndMenu.InputIntervalClamp;
			_navigateInterval = Engine_FrontEndMenu.FirstNavigateInterval +
				((Engine_FrontEndMenu.SubsequentNavigateInterval -
					Engine_FrontEndMenu.FirstNavigateInterval) * t);
		}

		return Move(direction);
	}

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
	/// Confirms the selected card and returns the index to load, or -1 when there is nothing on
	/// the fan.
	///
	/// AN INDEX, NOT A NAME, because that is the join the guest itself uses: it identifies a
	/// save by the ADDRESS of its handle slot inside its own vector — `Frontend_SetStartGameTarget`
	/// takes `*(screen+84) + 4*i` — so sending text would mean finding it again on the guest's
	/// side and trusting two orderings to agree.
	/// </summary>
	public int Activate() => _saves.Count == 0 ? -1 : Cursor;

	/// <summary>
	/// Puts the panel back to the state it opens in.
	///
	/// Called on the way IN rather than out, for the reason the gender card gives: the guest
	/// re-enters through the same slide every time, whatever the player did last.
	/// </summary>
	public void Reset()
	{
		Cursor = 0;
		_held = 0;
		_navigateCount = 0;
		_navigateInterval = Engine_FrontEndMenu.FirstNavigateInterval;
		Refresh();
	}

	/// <summary>
	/// Shows the cards the save list reaches, hides the rest, and starts the move tween.
	///
	/// A CARD WITH NO SAVE IS HIDDEN, NOT PARKED. The page's closed slide puts every card at
	/// y -598, and reusing that for an unused slot would leave a stack of blank cards below the
	/// canvas that a later open-tween would fly in — which is the empty-slot behaviour the file
	/// does not have, because the file has no concept of an empty slot at all.
	/// </summary>
	public void Refresh()
	{
		// VISIBILITY IS BY SLOT, NOT BY SAVE INDEX. With three saves the base slot is 2, so
		// cards 2, 3 and 4 draw and 1, 5 and 6 do not — testing `i < _saves.Count` here is the
		// left-aligned fan the centring exists to avoid.
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

		_elapsed = 0.0;
		_animating = true;
		ApplyPoses(0f);
	}

	/// <summary>
	/// Advances the move. Cards move only here, so the tween runs at the display rate rather
	/// than at whatever rate the caller polls navigation.
	/// </summary>
	public override void _Process(double delta)
	{
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
	/// Writes every card's inner group at a point on its own interpolation.
	///
	/// THE SELECTED CARD GOES TO THE ORIGIN and every other one to <see cref="Row"/> — see the
	/// class remarks for why that is the way round the guest states it, and why the opposite
	/// reading is the natural mistake.
	///
	/// A plain clamped lerp, which is what the engine does — the same interpolation
	/// <c>UpdateAnimations</c> runs for the row menu, with no easing curve to miss.
	/// </summary>
	private void ApplyPoses(float t)
	{
		for (int i = 0; i < MaxCards; i++)
		{
			if (_groups[i] is not { } group)
			{
				continue;
			}

			bool selected = _saves.Count > 0 && i == SlotOf(Cursor);
			Vector3 to = selected ? Vector3.Zero : Row[i];
			float roll = selected ? 0f : RowRoll[i];

			group.Position = _groupFrom[i].Lerp(to, t);
			group.Rotation = new Vector3(0f, 0f,
				_rollFrom[i] + ((roll - _rollFrom[i]) * t));
		}
	}

	/// <summary>
	/// Re-texts <c>SaveGameDescription</c> in place, keeping every piece of layout the file
	/// authored — the same mechanism <see cref="Engine_FrontEndWarning.Show"/> uses, and for the
	/// same reason: the scene builder stamps the artist's point size, alignment, wrap mode and
	/// box onto the node, so regenerating the glyphs from those values reproduces the layout
	/// exactly. Choosing them here would be inventing a layout next to one that already exists.
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

		(ArrayMesh? mesh, Texture2D? atlas) = Theme.Glyphs(
			display, size, opaque,
			horizontalAlign: horizontal, verticalAlign: vertical,
			boxWidth: box.X, boxHeight: box.Y, wrapMode: wrap);

		node.Mesh = mesh;
		node.MaterialOverride = mesh is null
			? null
			: Engine_UiSceneBuilder.TextMaterial(opaque, atlas);
	}

	/// <summary>Finds the shipped page inside a built presentation. Null means it is not there.</summary>
	public static Node3D? FindPage(Node root) =>
		Engine_UiTheme.FindByName(root, PageName) as Node3D;

	/// <summary>
	/// A one-line description of what is on screen, for a check tool's log.
	///
	/// It reports where the selected card ACTUALLY ended up, in world space, rather than where
	/// the table says it should be — those differ whenever the page's own transform is not what
	/// was assumed, and "a card behind something else" and "a card nowhere near the canvas" look
	/// identical in a screenshot.
	/// </summary>
	public string Describe()
	{
		int slot = SlotOf(Cursor);
		string where = _saves.Count > 0 && slot >= 0 && slot < MaxCards && _cards[slot] is { } card
			? card.GlobalPosition.ToString()
			: "none";

		return $"[save-cards] saves={_saves.Count} cursor={Cursor} slot={slot + 1}/{BaseSlot} " +
			$"art={(HasShippedArt ? "shipped" : "STAND-IN")} " +
			$"tint={(_background is null ? "none" : "shipped")} " +
			$"text={(_description is null ? "NOT FOUND" : "shipped")} " +
			$"page={Position} selected={where}";
	}
}
