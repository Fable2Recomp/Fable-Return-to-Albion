using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Draws an <see cref="Engine_FrontEndMenu"/> the way Fable 2's expandable menu draws itself. The
/// model owns what the menu CONTAINS; this owns where it goes, and every number is read out of the
/// shipped Lua in <c>scripts/gameface/expandablemenu/</c> (<c>tools/lua51dis.py</c>):
///
/// <list type="bullet">
/// <item>Twelve fixed slots, not a pitch: <c>g_MenuPositionSlots</c>, rebased at chunk load by
/// <c>x + 345, y - 185</c>. The column bows — x runs -345, -323, -303, -289 … -289, -295, -303,
/// -323, -345.</item>
/// <item>The highlight does not move; the list does. <c>HIGHLIGHT_INDEX = 4</c> and
/// <c>forceReset</c> gives slot = displayIndex - highlight + 4, at most two entries above.</item>
/// <item>Slots 1 and 12 are gutters (<c>{scale 0.65, opacity 0}</c>); 2 and 11 are 50% at 0.75,
/// 3 and 10 are 75% at 0.85, 4..9 full — ten slots carry content.</item>
/// <item>Depth is an indent: <c>GetMenuItemTabOffset</c> = <c>-65 - 5*(depth-1)</c>, +24 when
/// <c>IsHeading</c> (<c>item.ParentItem == g_Menu.MenuRoot</c>, a root entry being depth 1).
/// <c>Icon.Title</c> sits at position.x -54 for a heading, -30 for a child.</item>
/// <item><c>HEADER_SCALE</c> 1.025 / <c>DEFAULT_SCALE</c> 0.925, added to the slot's own scale
/// minus one. Live label colour (255, 224, 128) at opacity 100, point size 18 in every style.</item>
/// <item>The slide is a plain clamped lerp over <c>ANIMATE_TIME</c> 0.15, no easing; what moves is
/// the COMPONENT, so rows here are keyed by ENTRY and change slot.</item>
/// </list>
///
/// Not reproduced: truncation (<see cref="Engine_UiTextMesh"/> has no truncate mode, so an
/// over-long label overhangs).
///
/// The frame art comes from the presentation and is NOT cloned in the original —
/// <c>expandablemenucloning.lua</c> is an empty chunk; <c>g_MenuFormatter:onInitialize</c> keeps
/// the twelve <c>component</c> children of <c>getElement('Buttons', self.parent)</c>, each carrying
/// both tab faces (<c>Tab.InnerBrown</c>, <c>Tab.InnerRed</c>) and an <c>Icon.Title</c>.
/// <see cref="RowTemplate"/> takes one such node and this duplicates it per slot, Godot having no
/// component instancing; null gives text-only rows.
/// </summary>
public partial class Engine_FrontEndMenuView : Node3D
{
	/// <summary>
	/// The rating states read out of this screen's own document; null on a screen whose button pool
	/// carries no rating art, which is most of them.
	/// </summary>
	public Engine_UiRowRating? Ratings { get; set; }

	/// <summary>
	/// The face states LOCKED, not depth: a ghosted row wears the submenu's slate tab, every other
	/// row — the folder included — the heading's leather. The indent and label layout still
	/// follow depth. For the achievements list.
	/// </summary>
	public bool LockedRowsSlate { get; set; }

	/// <summary>Hides the row art's star strip (`Tab.Stars`) outright, for a list whose rows
	/// carry no rating at all.</summary>
	public bool HideRatingStrip { get; set; }

	/// <summary>
	/// The `Buttons` group's live opacity, 0..1 — the focus dim. `ExpandableMenu.LoseFocus` runs
	/// `Buttons.opacity` 100 → 10 over 500 ticks and `GetFocus` runs it back; the guest still plays
	/// both, so the host reads the value off the element and multiplies every row by it.
	/// </summary>
	public float FocusOpacity { get; set; } = 1f;

	/// <summary>One of the twelve authored positions, in the menu component's own space.</summary>
	public readonly struct Slot
	{
		public readonly float X;
		public readonly float Y;

		/// <summary>1-based index into <see cref="Styles"/>.</summary>
		public readonly int StyleIndex;

		public Slot(float x, float y, int styleIndex)
		{
			X = x;
			Y = y;
			StyleIndex = styleIndex;
		}
	}

	/// <summary>One <c>g_MenuOpacityScaleSlots</c> record.</summary>
	public readonly struct Style
	{
		public readonly float Scale;

		/// <summary>Authored 0..100.</summary>
		public readonly float Opacity;

		public readonly int FontSize;

		/// <summary>Width the engine clips the label to. Not applied — see the class remarks.</summary>
		public readonly float Truncate;

		public Style(float scale, float opacity, int fontSize, float truncate)
		{
			Scale = scale;
			Opacity = opacity;
			FontSize = fontSize;
			Truncate = truncate;
		}
	}

	/// <summary>
	/// <c>g_MenuPositionSlots</c>, already carrying the <c>x + 345, y - 185</c> rebase the
	/// chunk applies at load — these are the values the running script actually indexes.
	/// </summary>
	public static readonly Slot[] Slots = BuildSlots();

	/// <summary>Taken from <see cref="Engine_UiMenuConstants.Slots"/> rather than transcribed again.</summary>
	private static Slot[] BuildSlots()
	{
		var slots = new Slot[Engine_UiMenuConstants.Slots.Length];
		for (int i = 0; i < slots.Length; ++i)
		{
			Engine_UiMenuConstants.SlotRecord record = Engine_UiMenuConstants.Slots[i];
			slots[i] = new Slot(record.Position.X, record.Position.Y, record.StyleIndex);
		}

		return slots;
	}

	/// <summary>
	/// <c>g_MenuOpacityScaleSlots</c>. Entries 1 and 5 are identical; only 1 is reachable from
	/// the slot table as shipped, 5 being what <c>FadeOutTopItemEarly</c> and
	/// <c>FadeOutBottomItemEarly</c> retarget slots 2 and 11 to at the ends of a long list.
	/// </summary>
	public static readonly Style[] Styles = BuildStyles();

	private static Style[] BuildStyles()
	{
		var styles = new Style[Engine_UiMenuConstants.Styles.Length];
		for (int i = 0; i < styles.Length; ++i)
		{
			Engine_UiMenuConstants.Style record = Engine_UiMenuConstants.Styles[i];
			styles[i] = new Style(record.Scale, record.Opacity, record.FontSize, record.Truncate);
		}

		return styles;
	}

	/// <summary>The slot the highlighted entry is pinned to — <c>HIGHLIGHT_INDEX</c>, 1-based.</summary>
	public const int HighlightSlot = Engine_UiExpandableMenu.HighlightSlot;

	/// <summary>How many entries the window may hold — <c>g_Menu.Const.NUM_VISIBLE</c>.
	/// TEN, not the twelve of <c>NUM_VIS_SLOTS</c>: slots 1 and 12 are the fade gutters.</summary>
	public const int WindowSize = Engine_UiExpandableMenu.NumVisible;

	/// <summary><c>GetMenuItemTabOffset</c>: the base, the per-depth step, and the heading bonus.</summary>
	public const float TabOffset = Engine_UiMenuConstants.TabOffset;
	public const float TabOffsetPerDepth = Engine_UiMenuConstants.TabOffsetPerDepth;
	public const float HeadingTabBonus = Engine_UiMenuConstants.HeadingTabBonus;

	/// <summary><c>Icon.Title</c>'s position.x, from <c>SetAttributesToVisibleIndex</c>.</summary>
	public const float HeadingLabelX = Engine_UiMenuConstants.HeadingLabelX;
	public const float ChildLabelX = Engine_UiMenuConstants.ChildLabelX;

	/// <summary><c>HEADER_SCALE</c> / <c>DEFAULT_SCALE</c> from <c>g_MenuFormatter:onInitialize</c>.</summary>
	public const float HeaderScale = Engine_UiMenuConstants.HeaderScale;
	public const float DefaultScale = Engine_UiMenuConstants.DefaultScale;

	/// <summary>The live entry colour written by <c>SetGhosting</c> — (255, 224, 128).</summary>
	public static readonly Color LabelColour = new(1f, 224f / 255f, 128f / 255f);

	/// <summary>A ghosted ROOT entry: same colour, opacity 40. Unused until the model has the flag.</summary>
	public const float GhostedHeadingOpacity = 40f;

	/// <summary>A ghosted CHILD entry: black at opacity 90.</summary>
	public static readonly Color GhostedChildColour = new(0f, 0f, 0f);
	public const float GhostedChildOpacity = 90f;

	/// <summary>
	/// Where the menu component sits on the screen. The slot table is component-local because of the
	/// load-time rebase; undoing it puts the rows back in <c>frontendmainmenu</c>'s authored
	/// 1280x720 canvas. Mounted under a real ExpandableMenu node, set this to zero.
	/// </summary>
	public static readonly Vector3 CanvasAnchor = new(-345f, 185f, 0f);

	/// <summary>
	/// Where this view sits on the canvas — the AUTHORED Buttons-group origin of the screen whose
	/// art it draws, when the adopter has a pattern book to read it from. Each screen parks the
	/// group where it wants (both shipped menus compose ExpandableMenu(-400,0) + Buttons(63,210) =
	/// (-337,210)); <see cref="CanvasAnchor"/> is the fallback for a caller with no book.
	/// </summary>
	public Vector3 Anchor { get; set; } = CanvasAnchor;

	/// <summary>
	/// Row pitch through the full-size band, derived from the slot table rather than assumed:
	/// slots 5..8 are 58 apart. Only used to size the stand-in cursor.
	/// </summary>
	public static float RowPitch => Slots[4].Y - Slots[5].Y;

	/// <summary>
	/// The shipped look this draws with — fonts, strings and the text material. Without it rows
	/// still take their positions from the slot table and the labels are absent, which
	/// <see cref="LabelsWithoutGlyphs"/> reports.
	/// </summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>A built row component to duplicate per slot, or null for text-only rows.</summary>
	public Node3D? RowTemplate { get; set; }

	/// <summary>
	/// The highlight cursor to place at <see cref="HighlightSlot"/>, or null for the stand-in. The
	/// scripts never move it — <c>UpdateCursorColor</c> only sets the opacity of
	/// <c>Group.green_top</c> and <c>Group.green_top_translucent</c> inside <c>MenuHighlight</c>,
	/// its position being authored in the <c>.bgf</c> — so the stand-in's slot-4 placement is
	/// inferred from the pinning rule, not read.
	/// </summary>
	public Node3D? CursorTemplate { get; set; }

	/// <summary>
	/// The cursor template is the view's to free: <c>Art()</c> hands over a detached duplicate
	/// with its own materials, <see cref="BuildCursor"/> only duplicates it again, and a node
	/// outside the tree outlives the tree. Left behind, every view leaked its three quads into
	/// Godot's exit, where the leaked instances are destroyed after the storage they depend on
	/// (the SIGSEGV in ~RendererSceneCull that ended every session; 15 instances in the core).
	/// <see cref="RowTemplate"/> is not freed here: it is a node inside the pattern book.
	/// </summary>
	public override void _Notification(int what)
	{
		if (what != NotificationPredelete)
		{
			return;
		}

		if (CursorTemplate is not null && GodotObject.IsInstanceValid(CursorTemplate) &&
			!CursorTemplate.IsInsideTree())
		{
			CursorTemplate.Free();
		}

		CursorTemplate = null;
	}

	/// <summary>The model being drawn. Null until <see cref="Bind"/>.</summary>
	public Engine_FrontEndMenu? Menu { get; private set; }

	/// <summary>
	/// How deep into open submenus the highlighted row sits, 0 at the root, read off the TREE:
	/// `expandablemenu.lua` has no second menu object, only children and `IsOpen`.
	/// </summary>
	public int Depth => Menu?.Highlighted is { } item ? item.Depth - 1 : 0;

	/// <summary>Rows currently on screen, for a caller that wants to assert on the layout.</summary>
	public int RowsDrawn { get; private set; }

	/// <summary>Labels that produced no glyphs. Non-zero with a non-zero <see cref="RowsDrawn"/> means no font.</summary>
	public int LabelsWithoutGlyphs { get; private set; }

	/// <summary>
	/// How long a row takes to reach its new slot — <c>ANIMATE_TIME</c>, a global in
	/// <c>expandablemenuformatting.lua</c>. Not <c>ANIMATE_IN_TIME</c> / <c>ANIMATE_OUT_TIME</c>,
	/// both 0.08, which belong to an item entering or leaving the list.
	/// </summary>
	public const double AnimateTime = Engine_UiMenuConstants.AnimateTime;

	/// <summary>Where a row is: the four properties a slide interpolates.</summary>
	private readonly struct RowPose
	{
		public readonly float X;
		public readonly float Y;
		public readonly float Scale;

		/// <summary>Authored 0..100, as the slot table states it.</summary>
		public readonly float Opacity;

		/// <summary>The same pose moved along x — a flying-in row's start.</summary>
		public RowPose ShiftedX(float dx) => new(X + dx, Y, Scale, Opacity)
		{
			LabelColour = LabelColour,
			LabelOpacity = LabelOpacity,
		};

		public RowPose(float x, float y, float scale, float opacity)
		{
			X = x;
			Y = y;
			Scale = scale;
			Opacity = opacity;
			LabelColour = Engine_UiMenuConstants.LiveTextColour;
			LabelOpacity = 100f;
		}

		/// <summary>
		/// The label's colour and opacity — `SetGhosting` (main.21) writes only on `Icon.Title`,
		/// never the tab. Live is (255, 224, 128) at 100; a ghosted heading keeps that colour at 40,
		/// a ghosted child goes black at 90.
		/// </summary>
		public Color LabelColour { get; init; }

		public float LabelOpacity { get; init; }

		/// <summary>
		/// A plain clamped lerp, as <c>UpdateAnimations</c> does: <c>t = (now - start_time) /
		/// duration</c> clamped at 1, then <c>start_val + (end_val - start_val) * t</c>. No easing.
		/// </summary>
		public static RowPose Lerp(RowPose from, RowPose to, float t) => new(
			from.X + ((to.X - from.X) * t),
			from.Y + ((to.Y - from.Y) * t),
			from.Scale + ((to.Scale - from.Scale) * t),
			from.Opacity + ((to.Opacity - from.Opacity) * t))
		{
			// The ghost treatment is the DESTINATION's, not a blend: a row arrives ghosted or live.
			LabelColour = to.LabelColour,
			LabelOpacity = to.LabelOpacity,
		};
	}

	private readonly Node3D[] _rows = new Node3D[Slots.Length];
	private readonly MeshInstance3D[] _labels = new MeshInstance3D[Slots.Length];
	private readonly string[] _labelText = new string[Slots.Length];
	private readonly int[] _labelSize = new int[Slots.Length];

	/// <summary>The truncate width each label was last laid out against.</summary>
	private readonly float[] _labelTruncate = new float[Slots.Length];

	/// <summary>
	/// The label's depth WITHIN its row — the authored z of the template's own <c>Icon.Title</c>,
	/// read before the clone is stripped of it. Must not be 0: the row is alpha-blended with no
	/// depth write, so z alone decides cover order, and the shipped component authors its faces at
	/// 0/-1/-2, decorations at +1 and the Title at +3. A z tie has no defined winner.
	/// </summary>
	private float _labelLift;

	/// <summary>
	/// The canvas-space band rows may draw in, or null for no clip — the screen's own MASK rect,
	/// resolved by the adopter from the file's `MaskName` binding. The original clips the pool with
	/// a mask element, so its faded slots 11 and 12 are cut by the page edge. Only Y matters, and a
	/// row is cut whole rather than sliced.
	/// </summary>
	public Rect2? ClipRect { get; set; }

	/// <summary>Which screen this list belongs to, when the caller knows. Only the end-fade reads it.</summary>
	public uint? ScreenId { get; set; }

	/// <summary>
	/// Whether slot 2 / slot 11 take the zero-opacity style. The end fade is per-screen and not Lua:
	/// `FadeOutTopItemEarly` / `FadeOutBottomItemEarly` (`expandablemenuformatting.dis main.0` /
	/// `main.1`) have no Lua callers; the C++ runs them as `DoString` source (literals at
	/// `0x820CD420` and `0x820CD3F4`) from thirteen screen contract vtables. Two ask for one end
	/// only — Options (id 18, `0x826D9D40`) TOP, real estate (id 28, `0x82998300`) BOTTOM — and the
	/// front end calls neither (`sub_826E1F70`), so with no ScreenId both ends stay at 50%.
	/// </summary>
	/// THE SCREEN STATES IT. This was two id comparisons here; it is `FadesTopEnd` /
	/// `FadesBottomEnd` on the screen's template now, so a screen's fade is authored with the
	/// rest of it rather than found in this file. With no ScreenId the front end fades neither
	/// end, which is what `sub_826E1F70` does.
	private bool FadesTopEnd =>
		ScreenId is { } id && (Engine_UiTemplates.Screen(id)?.FadesTopEnd ?? true);

	private bool FadesBottomEnd =>
		ScreenId is { } id && (Engine_UiTemplates.Screen(id)?.FadesBottomEnd ?? true);

	/// <summary>Which entry each row node is currently drawing. Null means the node is free.</summary>
	private readonly Engine_UiExpandableMenu.Item?[] _rowItem =
		new Engine_UiExpandableMenu.Item?[Slots.Length];

	/// <summary>The entry and emphasis each row's tab face was last built for.</summary>
	private readonly Engine_UiExpandableMenu.Item?[] _rowFaced =
		new Engine_UiExpandableMenu.Item?[Slots.Length];

	private readonly int[] _rowEmphasis = new int[Slots.Length];

	/// <summary>The rating value each row's strip was last built for.</summary>
	private readonly int[] _rowRating = new int[Slots.Length];

	private readonly RowPose[] _from = new RowPose[Slots.Length];
	private readonly RowPose[] _to = new RowPose[Slots.Length];

	/// <summary>
	/// Each row's start delay on the running slide — `time_delay` on its animation set. A fold flies
	/// rows in from the left: `onExpandMenuItem` (`expandablemenuformatting.dis main.7` pc126..132)
	/// runs `AnimateComponentIn(child, ANIMATE_IN_TIME / n)`, which writes `position.x` to
	/// `slot.x - 500` before tweening (`main.8` pc26..30); `CollapseMenuAndHighlight` (`main.39`
	/// pc162..174) does the same from below. The delay is ONE value for the whole batch —
	/// `ANIMATE_IN_TIME` over the batch size, capped at the eight slots below the highlight — not a
	/// per-row stagger. A plain scroll never sets it.
	/// </summary>
	private readonly double[] _delay = new double[Slots.Length];

	/// <summary>`slot.x - 500` — where a flying-in row starts.</summary>
	private const float FlyInDistance = 500f;

	/// <summary>The folder open when the rows were last laid out, to tell a fold from a scroll.</summary>
	private Engine_UiExpandableMenu.Item? _lastExpanded;

	/// <summary>Where one row is on its own from/to interpolation, its delay honoured.</summary>
	private float RowT(int row)
	{
		if (!_sliding || AnimateTime <= 0.0)
		{
			return 1f;
		}

		return Mathf.Clamp((float)((_elapsed - _delay[row]) / AnimateTime), 0f, 1f);
	}

	/// <summary>
	/// An entry's <c>g_Menu.EmphasisConst</c> value, as the model carries it — the world map
	/// states primary (1) and active (2) quests apart, and they wear different tints.
	/// </summary>
	private static int Emphasis(Engine_UiExpandableMenu.Item item) => (int)item.Emphasis;

	private double _elapsed;

	/// <summary>
	/// Rows are mid-slide. This is the TWEEN; <see cref="Animating"/> is the PRESS GATE, and they
	/// are separate: a plain cursor move slides the column but never blocks A (its delay is 0),
	/// while a fold cascade does both.
	/// </summary>
	private bool _sliding;
	private bool _animating;

	/// <summary>
	/// Whether this list draws its cursor. False for a list that is still on screen but no
	/// longer has the input — two cursors on one screen say nothing about which one moves.
	/// </summary>
	public bool CursorShown
	{
		get => _cursorShown;
		set
		{
			_cursorShown = value;
			if (_cursor is not null && GodotObject.IsInstanceValid(_cursor))
			{
				_cursor.Visible = value;
			}
		}
	}

	private bool _cursorShown = true;

	private Node3D? _cursor;

	public override void _Ready()
	{
		Position = Anchor;
		BuildRows();
		Refresh();
	}

	/// <summary>Points the view at a model and lays it out. Safe before <c>_Ready</c>.</summary>
	public void Bind(Engine_FrontEndMenu menu)
	{
		Menu = menu;
		_pcCentre = PcMinCentre;
		if (_rows[0] is not null)
		{
			Refresh();
		}
	}

	/// <summary>
	/// Advances the highlight on whichever model is currently BOUND — navigate through the view, not
	/// a model reference the caller kept, which may be a list that is no longer on screen. True when
	/// the highlight moved.
	/// </summary>
	public bool Navigate(int direction, double delta)
	{
		if (Menu is null || Menu.Navigate(direction, delta) <= 0)
		{
			return false;
		}

		Refresh();
		return true;
	}

	/// <summary>
	/// Activates the highlighted entry — the single entry point for "the player pressed A". Returns
	/// the NAME to dispatch to the guest, or null when the press was handled here (expand, collapse,
	/// submenu, screen opening, host-only entry). A screen name is latched into
	/// <see cref="TakeOpenedScreen"/> and the menu stays put, as the original leaves it.
	/// </summary>
	public string? Activate()
	{
		if (Menu is null)
		{
			return null;
		}

		// The model first: it owns expand/collapse and decides whether the press is the guest's
		// business. An entry with both children and a submenu expands, as the original does.
		Engine_UiExpandableMenu.Item? item = Menu.Highlighted;
		string? dispatch = Menu.Activate();

		if (dispatch is null && item?.Opens is { } screen)
		{
			// A refused entry opens nothing; the model latched the reason as a text tag.
			if (Menu.Warning is null)
			{
				_openedScreen = screen;
			}

			return null;
		}

		Refresh();
		return dispatch;
	}

	/// <summary>Whether the column is mid-slide — a pointer resting on it must not re-pick.</summary>
	public bool Sliding => _sliding;

	/// <summary>
	/// The entry drawn under a canvas point (the UI camera is orthographic, so global X/Y are
	/// canvas units), or null. Only rows that are drawn and not faded out count; the band clip
	/// has already hidden rows past the page edge. On overlap the nearer row (larger z) wins.
	/// </summary>
	public Engine_UiExpandableMenu.Item? EntryAt(Vector2 canvas)
	{
		Engine_UiExpandableMenu.Item? best = null;
		float bestZ = float.NegativeInfinity;
		for (int r = 0; r < _rows.Length; ++r)
		{
			if (_rows[r] is not { } row || !row.IsVisibleInTree() || _rowItem[r] is not { } item ||
				_to[r].Opacity <= 0f)
			{
				continue;
			}

			if (Engine_UiPointer.CanvasBounds(row) is { } bounds && bounds.HasPoint(canvas) &&
				row.GlobalPosition.Z > bestZ)
			{
				best = item;
				bestZ = row.GlobalPosition.Z;
			}
		}

		return best;
	}

	/// <summary>
	/// Moves the highlight onto <paramref name="item"/>, as a press of the stick would. True when
	/// it moved. Ignored mid-slide, like the stick: the guest's `WaitForInput` gate.
	/// </summary>
	public bool PointAt(Engine_UiExpandableMenu.Item item)
	{
		if (Menu is null || Animating)
		{
			return false;
		}

		int index = Menu.Model.VisibleIndexOf(item);
		if (index == 0 || index == Menu.Model.CurrentHighlightIdx)
		{
			return false;
		}

		Menu.Model.Highlight(index);
		Refresh();
		return true;
	}

	// ---------------------------------------------------------------------------------------
	// THE PC LIST — keyboard and mouse
	// ---------------------------------------------------------------------------------------
	//
	// With the keyboard and mouse active the column is a plain PC list: ten rows straight down
	// at full size, the highlight moving over them, and the list scrolling only when the
	// highlight would leave it (or by the wheel and the scroll bar). The console carousel —
	// highlight pinned to slot 4, the column bowing and fading at its ends — comes back the
	// moment the controller is touched. Both lay out through the same slots and `Window`: the
	// PC list is `Window(centre)` with the centre held between PcMinCentre and count − 7, which
	// puts the first row in slot 2 and the tenth in slot 11.

	/// <summary>The centre that puts row 1 in slot 2: `Window(3, n)` is rows 1..10.</summary>
	private const int PcMinCentre = 3;

	/// <summary>
	/// The book this list stands in, or null for a list with none (the title). Its footer —
	/// `ExpandableMenu.Money`, the Back prompt and the gold, the same group in every book —
	/// frames the PC column: rows stop above it, and the column's top margin mirrors the
	/// footer's bottom one. Every list frames itself this one way.
	/// </summary>
	public Engine_UiOwnedBook? Book { get; set; }

	/// <summary>The footer's canvas rectangle as of this frame, or null when none is drawn.</summary>
	private Rect2? _footer;

	private void MeasureFooter() =>
		_footer = Book is { } book && IsInstanceValid(book) &&
			book.NodeFor("ExpandableMenu.Money") is { } money
				? Engine_UiPointer.CanvasBounds(money)
				: null;

	/// <summary>The top of the footer in canvas units, or null: no row is drawn under it.</summary>
	private float? FooterTop => _footer?.End.Y;

	/// <summary>The bottom of the footer in canvas units, or null.</summary>
	private float? FooterBottom => _footer?.Position.Y;

	/// <summary>
	/// How far the PC column stands below the slot table: enough that the first row's top is as
	/// far under the screen's top edge as the footer's bottom is over its bottom edge, so the
	/// list is framed the same at both ends. Zero without a footer.
	/// </summary>
	private float PcDrop
	{
		get
		{
			if (FooterBottom is not { } bottom || Engine_UiPointer.VisibleCanvas is not { } canvas)
			{
				return 0f;
			}

			float margin = bottom - canvas.Position.Y;
			float firstTop = GlobalPosition.Y + Slots[HighlightSlot - 1].Y -
				((2 - HighlightSlot) * RowPitch) + (RowPitch * 0.5f);
			return Mathf.Max(0f, firstTop - (canvas.End.Y - margin));
		}
	}

	/// <summary>The canvas y of a PC slot's row origin.</summary>
	private float PcSlotY(int slot) =>
		GlobalPosition.Y + Slots[HighlightSlot - 1].Y - ((slot - HighlightSlot) * RowPitch) - PcDrop;

	/// <summary>Rows the PC list shows at once: slots 2 on, as many as clear the footer.</summary>
	public int PcRows
	{
		get
		{
			int rows = 0;
			for (int slot = 2; slot <= 1 + WindowSize; ++slot)
			{
				if (FooterTop is { } top && PcSlotY(slot) - (RowPitch * 0.5f) < top)
				{
					break;
				}

				++rows;
			}

			return Math.Max(1, rows);
		}
	}

	/// <summary>Rows below the centre the PC window reaches.</summary>
	private int PcBelowCentre => PcRows - (HighlightSlot - 2) - 1;

	/// <summary>The PC list's scroll position, as the centre <see cref="Window"/> is built on.</summary>
	private int _pcCentre = PcMinCentre;

	/// <summary>Which mode the rows were last laid out for.</summary>
	private bool _laidOutPc;

	/// <summary>The <see cref="PcDrop"/> the rows were last laid out with.</summary>
	private float _laidOutDrop;

	private static bool PcMode => Engine_UiInputMode.KeyboardMouse;

	private int PcMaxCentre => System.Math.Max(PcMinCentre, (Menu?.Visible.Count ?? 0) - PcBelowCentre);

	/// <summary>Scrolls the PC list by wheel notches, leaving the highlight where it is.</summary>
	public bool Scroll(int steps)
	{
		if (Menu is null || steps == 0 || !PcMode)
		{
			return false;
		}

		int to = System.Math.Clamp(_pcCentre + steps, PcMinCentre, PcMaxCentre);
		if (to == _pcCentre)
		{
			return false;
		}

		_pcCentre = to;
		Refresh();
		return true;
	}

	/// <summary>Whether the PC list has more rows than it shows — the scroll bar's condition.</summary>
	public bool Scrollable => PcMode && Menu is not null && Menu.Visible.Count > PcRows;

	/// <summary>The scroll position as 0..1 and the shown fraction, for the scroll bar.</summary>
	public (float Position, float Shown) ScrollState()
	{
		int count = Menu?.Visible.Count ?? 0;
		if (count <= PcRows)
		{
			return (0f, 1f);
		}

		float span = PcMaxCentre - PcMinCentre;
		return (span <= 0f ? 0f : (_pcCentre - PcMinCentre) / span, PcRows / (float)count);
	}

	/// <summary>Scrolls the PC list to a 0..1 position — the scroll bar's drag.</summary>
	public void ScrollTo(float position)
	{
		int to = PcMinCentre + Mathf.RoundToInt(Mathf.Clamp(position, 0f, 1f) * (PcMaxCentre - PcMinCentre));
		if (to != _pcCentre)
		{
			_pcCentre = to;
			Refresh();
		}
	}

	// THE SCROLL BAR rides the book's curved left edge, where the console's A cursor sits: a
	// bronze line through the ring's place at every shown row — bowed as the console column is
	// bowed (`g_MenuPositionSlots` x 0, 22, 42, 56 … 56, 50, 42, 22, 0), the curve its rows hug —
	// and a bronze ring for a thumb. The cursor itself is put away in the PC list.

	private MeshInstance3D? _barTrack;
	private MeshInstance3D? _barThumb;

	/// <summary>The track's points in canvas units, top to bottom; empty while hidden.</summary>
	private readonly List<Vector2> _barPoints = new();

	private float _thumbSize;

	private const float TrackWidth = 5f;

	private void UpdateScrollBar()
	{
		// The ring's centre, from the green glyph's own quad — its transform, not its drawn
		// bounds, so it measures while the PC list has the cursor put away.
		if (_ringLocal is null && _cursor is not null &&
			Engine_UiTheme.FindByName(_cursor, "green_top") is MeshInstance3D { Mesh: not null } glyph)
		{
			_ringLocal = _cursor.ToLocal(glyph.GlobalTransform * glyph.GetAabb().GetCenter());
		}

		_barPoints.Clear();
		if (!Scrollable || !IsVisibleInTree() || RingAtRest() is not { } ring)
		{
			if (_barTrack is not null)
			{
				_barTrack.Visible = false;
				_barThumb!.Visible = false;
			}

			return;
		}

		// The ring's place at each shown slot: its rest x plus the console bow for that slot.
		float restSlotY = Slots[HighlightSlot - 1].Y;
		for (int slot = 2; slot <= 1 + PcRows; ++slot)
		{
			float bow = Slots[slot - 1].X - Slots[HighlightSlot - 1].X;
			float y = ring.Y + (PcSlotY(slot) - (GlobalPosition.Y + restSlotY));
			_barPoints.Add(new Vector2(ring.X + bow, y));
		}

		EnsureScrollBar();
		_thumbSize = RowPitch * 0.62f;
		DrawTrack();
		(float position, _) = ScrollState();
		Vector2 at = PointAlong(position);
		_barThumb!.Visible = true;
		((QuadMesh)_barThumb.Mesh).Size = new Vector2(_thumbSize, _thumbSize);
		_barThumb.GlobalPosition = new Vector3(at.X, at.Y, GlobalPosition.Z + 6f);
	}

	/// <summary>The cursor ring's centre in the cursor's own units, measured once.</summary>
	private Vector3? _ringLocal;

	/// <summary>The cursor ring's centre at its rest slot, in canvas units.</summary>
	private Vector2? RingAtRest()
	{
		if (_ringLocal is not { } local)
		{
			return null;
		}

		Vector3 atRest = ToGlobal(_cursorRest * local);
		return new Vector2(atRest.X, atRest.Y);
	}

	/// <summary>The point a 0..1 position stands for, along the track between its ends.</summary>
	private Vector2 PointAlong(float t)
	{
		if (_barPoints.Count == 0)
		{
			return Vector2.Zero;
		}

		float f = Mathf.Clamp(t, 0f, 1f) * (_barPoints.Count - 1);
		int i = Math.Min((int)f, _barPoints.Count - 2);
		return i < 0 ? _barPoints[0] : _barPoints[i].Lerp(_barPoints[i + 1], f - i);
	}

	private void DrawTrack()
	{
		var mesh = (ImmediateMesh)_barTrack!.Mesh;
		mesh.ClearSurfaces();
		mesh.SurfaceBegin(Mesh.PrimitiveType.TriangleStrip);
		float z = GlobalPosition.Z + 5f;
		for (int i = 0; i < _barPoints.Count; ++i)
		{
			Vector2 here = _barPoints[i];
			Vector2 along = (_barPoints[Math.Min(i + 1, _barPoints.Count - 1)] -
				_barPoints[Math.Max(i - 1, 0)]).Normalized();
			Vector2 side = new Vector2(-along.Y, along.X) * (TrackWidth * 0.5f);
			mesh.SurfaceAddVertex(new Vector3(here.X + side.X, here.Y + side.Y, z));
			mesh.SurfaceAddVertex(new Vector3(here.X - side.X, here.Y - side.Y, z));
		}

		mesh.SurfaceEnd();
		_barTrack.Visible = true;
	}

	private void EnsureScrollBar()
	{
		if (_barTrack is not null)
		{
			return;
		}

		_barTrack = new MeshInstance3D
		{
			Name = "ScrollTrack",
			Mesh = new ImmediateMesh(),
			TopLevel = true,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = Flat(new Color(0.36f, 0.20f, 0.10f, 0.85f), null),
		};
		AddChild(_barTrack);
		_barTrack.GlobalTransform = Transform3D.Identity;

		_barThumb = new MeshInstance3D
		{
			Name = "ScrollThumb",
			Mesh = new QuadMesh(),
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			MaterialOverride = Flat(Colors.White, ThumbTexture()),
		};
		AddChild(_barThumb);
	}

	private static StandardMaterial3D Flat(Color colour, Texture2D? texture) => new()
	{
		Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
		ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
		CullMode = BaseMaterial3D.CullModeEnum.Disabled,
		AlbedoColor = colour,
		AlbedoTexture = texture,
		TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps,
	};

	/// <summary>
	/// The thumb: a bronze ring in the cursor's own colours — a dark band, a lit rim on the upper
	/// left, a shadowed lower right — around a polished dark centre.
	/// </summary>
	private static ImageTexture ThumbTexture()
	{
		const int size = 96;
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var bronze = new Color(0.72f, 0.46f, 0.24f);
		var dark = new Color(0.30f, 0.17f, 0.08f);
		var centre = new Color(0.16f, 0.10f, 0.06f);
		float r = size * 0.5f;
		for (int y = 0; y < size; ++y)
		{
			for (int x = 0; x < size; ++x)
			{
				var d = new Vector2(x + 0.5f - r, y + 0.5f - r);
				float dist = d.Length() / r;
				if (dist > 1f)
				{
					image.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
					continue;
				}

				float edge = Mathf.Clamp((1f - dist) * r, 0f, 1f);
				float light = Mathf.Clamp(0.5f - ((d.X + d.Y) / (2.8f * r)), 0f, 1f);
				Color colour = dist > 0.62f
					? dark.Lerp(bronze, light + (0.35f * Mathf.Sin((dist - 0.62f) / 0.38f * Mathf.Pi)))
					: centre.Lerp(dark, light * 0.6f);
				image.SetPixel(x, y, new Color(colour.R, colour.G, colour.B, edge));
			}
		}

		image.GenerateMipmaps();
		return ImageTexture.CreateFromImage(image);
	}

	/// <summary>
	/// The 0..1 position a canvas height stands for on the track, clamped to its ends — a drag
	/// keeps tracking the pointer's height after it strays off the bar.
	/// </summary>
	public float ScrollBarPositionAt(float y)
	{
		if (_barPoints.Count < 2)
		{
			return 0f;
		}

		float top = _barPoints[0].Y;
		float bottom = _barPoints[^1].Y;
		return top == bottom ? 0f : Mathf.Clamp((top - y) / (top - bottom), 0f, 1f);
	}

	/// <summary>
	/// Where on the scroll bar a canvas point is, as a 0..1 position; null off it. Near the
	/// curve, within the thumb's reach either side, as a PC bar's hit area is wider than it looks.
	/// </summary>
	public float? ScrollBarAt(Vector2 at)
	{
		if (_barPoints.Count < 2)
		{
			return null;
		}

		float reach = Math.Max(_thumbSize * 0.6f, 14f);
		for (int i = 0; i + 1 < _barPoints.Count; ++i)
		{
			Vector2 a = _barPoints[i];
			Vector2 b = _barPoints[i + 1];
			Vector2 ab = b - a;
			float t = ab.LengthSquared() <= 0f ? 0f : Mathf.Clamp((at - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
			if ((a + (ab * t)).DistanceTo(at) <= reach)
			{
				return ScrollBarPositionAt(at.Y);
			}
		}

		return null;
	}

	/// <summary>
	/// The PC list's centre for this layout: held where it was, moved just enough to keep the
	/// highlight on screen when the keys or the guest move it past an edge.
	/// </summary>
	private int PcCentre(int highlight)
	{
		if (highlight < _pcCentre - (HighlightSlot - 2))
		{
			_pcCentre = highlight + (HighlightSlot - 2);
		}
		else if (highlight > _pcCentre + PcBelowCentre)
		{
			_pcCentre = highlight - PcBelowCentre;
		}

		_pcCentre = System.Math.Clamp(_pcCentre, PcMinCentre, PcMaxCentre);
		return _pcCentre;
	}

	/// <summary>The screen the last <see cref="Activate"/> asked for, cleared by reading it.</summary>
	public string? TakeOpenedScreen()
	{
		string? screen = _openedScreen;
		_openedScreen = null;
		return screen;
	}

	private string? _openedScreen;

	/// <summary>
	/// Backs out one level: collapses an expanded entry, else closes a submenu. False means nothing
	/// was left to back out of, which is the caller's signal that B means "leave this screen".
	/// </summary>
	public bool Back()
	{
		if (Menu is null)
		{
			return false;
		}

		if (Menu.Back())
		{
			Refresh();
			return true;
		}

		return false;
	}

	/// <summary>
	/// Builds twelve persistent row nodes once — persistent because the original moves the SAME
	/// component between slots (<c>onScrollDown</c>), which is what the tween hangs off.
	/// </summary>
	private void BuildRows()
	{
		// Read from the TEMPLATE (the clone is about to lose its text), and only when there is art
		// to sit above: a text-only view keeps z 0 because there is nothing to tie with.
		_labelLift = 0f;
		if (RowTemplate is not null && FindText(RowTemplate) is { } authoredTitle)
		{
			_labelLift = authoredTitle.Position.Z;
		}

		for (int i = 0; i < Slots.Length; i++)
		{
			var row = new Node3D { Name = $"Slot{i + 1:00}", Visible = false };
			AddChild(row);
			_rows[i] = row;

			if (RowTemplate is not null)
			{
				var frame = (Node3D)RowTemplate.Duplicate();
				frame.Name = "Frame";
				// The template was authored at its own place in the presentation; the slot
				// supplies the position.
				frame.Position = Vector3.Zero;
				row.AddChild(frame);

				// Art and nothing else: a shipped `MenuItemButton` carries its own `Icon.Title`
				// with the authored placeholder still in it, and the view owns the label.
				StripText(frame);

				// Each row needs its OWN materials: Node.Duplicate shares sub-resources, and a
				// row's opacity is per-slot (the two gutters are 0). This also records every
				// quad's authored alpha over the whole subtree, which `FadeArt` requires — it
				// skips any mesh with no recorded alpha, and the script writes the slot opacity
				// on the row COMPONENT, so it must accumulate down the whole row.
				Engine_UiSceneBuilder.OwnSubtreeMaterials(frame);
			}

			var label = new MeshInstance3D { Name = "Label" };
			row.AddChild(label);
			_labels[i] = label;
			_labelText[i] = string.Empty;
		}

		BuildCursor();
	}

	/// <summary>
	/// The first TEXT element in a row subtree — the authored <c>Icon.Title</c>, keyed on the
	/// element type the scene builder recorded rather than the name.
	/// </summary>
	private static Node3D? FindText(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child is Node3D spatial && child.HasMeta("element_type") &&
				(string)child.GetMeta("element_type") == "Text")
			{
				return spatial;
			}

			if (FindText(child) is { } inner)
			{
				return inner;
			}
		}

		return null;
	}

	/// <summary>
	/// Removes the TEXT a cloned row component brings with it, keyed on the element type rather
	/// than the name: the row is taken for its frame and star, and the view draws the label.
	/// </summary>
	private static void StripText(Node node)
	{
		foreach (Node child in node.GetChildren())
		{
			if (child.HasMeta("element_type") && (string)child.GetMeta("element_type") == "Text")
			{
				node.RemoveChild(child);
				child.QueueFree();
				continue;
			}

			StripText(child);
		}
	}

	/// <summary>Where a row quad's authored alpha is kept, so fading never compounds.</summary>
	private const string AuthoredAlphaMeta = "authored_alpha";

	/// <summary>
	/// The two alternative tab faces a row component ships with, and the three border pieces each
	/// carries. The border names differ between the faces — that is the file's own spelling.
	/// </summary>
	public const string BrownTabFace = "InnerBrown";
	public const string RedTabFace = "InnerRed";
	private static readonly string[] BrownBorders = Engine_UiMenuConstants.BrownBorders;
	private static readonly string[] RedBorders = Engine_UiMenuConstants.RedBorders;

	/// <summary><c>g_Menu.EmphasisConst</c>, from <c>expandablemenustateandconstants.lua</c>.</summary>
	public const int EmphasisNone = (int)Engine_UiMenuConstants.Emphasis.None;
	public const int EmphasisPrimaryQuest = (int)Engine_UiMenuConstants.Emphasis.PrimaryQuest;
	public const int EmphasisActiveQuest = (int)Engine_UiMenuConstants.Emphasis.ActiveQuest;
	public const int EmphasisSuggestedMenuItem = (int)Engine_UiMenuConstants.Emphasis.SuggestedMenuItem;

	/// <summary>
	/// Puts the tab face and border tint this entry calls for on a row. The face is chosen by
	/// EMPHASIS, not depth: `UpdateTabInnerTexture` (`main.24`) splits on <c>IsHeading</c> and the
	/// halves do not mirror.
	///
	/// <list type="bullet">
	/// <item>A root row (<c>UpdateTabInnerTextureMenu</c>, `main.22`) wears <c>InnerRed</c> at
	/// opacity 100 with untinted borders, and swaps to <c>InnerBrown</c> tinted 0.9 toward
	/// (10, 10, 40) only WHEN EMPHASISED — brown is the mark, not the default.</item>
	/// <item>A submenu row (<c>UpdateTabInnerTextureSubMenu</c>, `main.23`) is always
	/// <c>InnerBrown</c> and its emphasis picks the border tint: none 0.9 toward (30, 30, 30),
	/// primary quest 0.9 toward (40, 40, 120), active quest 0.9 toward (10, 10, 40), suggested
	/// item untinted.</item>
	/// </list>
	///
	/// The opacity goes on the GROUPS, never their quads — that is all
	/// `setAttribute(group, 'opacity', …)` does, and the brown group's full-bleed backing plate is
	/// zero only because the group is. The tint goes on the BORDERS, through
	/// <c>SetTintingAmountAndDiffuseColour(GetFirstChild(border), amount, r, g, b)</c>; our builder
	/// folds that `material` element into its parent's quad, so it lands on the border node.
	/// </summary>
	private void SetTabFace(int row, bool heading, int emphasis)
	{
		if (_rows[row].GetNodeOrNull<Node3D>("Frame") is not { } frame)
		{
			return;
		}

		// A root row shows the brown face only as the emphasis mark; a submenu row is brown
		// unconditionally.
		bool brown = !heading || emphasis != EmphasisNone;

		(float amount, Color colour) = heading
			? Engine_UiMenuConstants.HeadingTint(brown)
			: Engine_UiMenuConstants.SubmenuTint((Engine_UiMenuConstants.Emphasis)emphasis);

		Face(BrownTabFace, brown ? 1f : 0f);
		Face(RedTabFace, brown ? 0f : 1f);

		if (Engine_UiTheme.FindByName(frame, brown ? BrownTabFace : RedTabFace) is Node3D face)
		{
			foreach (string border in brown ? BrownBorders : RedBorders)
			{
				if (Engine_UiTheme.FindByName(face, border) is Node3D piece)
				{
					Tint(piece, colour, amount);
				}
			}
		}

		static void Tint(Node node, Color colour, float amount)
		{
			if (node is MeshInstance3D mesh)
			{
				Engine_UiSceneBuilder.SetQuadTint(mesh, colour, amount);
			}

			foreach (Node child in node.GetChildren())
			{
				Tint(child, colour, amount);
			}
		}

		void Face(string name, float opacity)
		{
			if (Engine_UiTheme.FindByName(frame, name) is Node3D group)
			{
				Recompute(group, Inherited(group) * opacity);
			}
		}

		// The group's own factor is REPLACED; everything below keeps the artist's — what
		// setAttribute on a group does.
		static void Recompute(Node node, float accumulated)
		{
			if (node is MeshInstance3D mesh)
			{
				// Written to the fade baseline rather than the material: the slot opacity is
				// applied on top of it every frame.
				mesh.SetMeta(AuthoredAlphaMeta, accumulated);
			}

			foreach (Node child in node.GetChildren())
			{
				Recompute(child, accumulated * Own(child));
			}
		}

		static float Own(Node node) =>
			node.HasMeta("opacity") ? (float)node.GetMeta("opacity") : 1f;

		static float Inherited(Node node) =>
			node.HasMeta("opacity_inherited") ? (float)node.GetMeta("opacity_inherited") : 1f;
	}

	/// <summary>Scales a whole row's art by a slot opacity, from its authored alpha.</summary>
	private static void FadeArt(Node node, float opacity, Node? skip = null)
	{
		if (ReferenceEquals(node, skip))
		{
			return;
		}

		if (node is MeshInstance3D mesh && mesh.HasMeta(AuthoredAlphaMeta))
		{
			float authored = (float)mesh.GetMeta(AuthoredAlphaMeta);
			Engine_UiSceneBuilder.SetQuadOpacity(mesh, authored * opacity);
		}

		foreach (Node child in node.GetChildren())
		{
			FadeArt(child, opacity, skip);
		}
	}

	/// <summary>
	/// The cursor's pose in VIEW space, from the source art's authored transform chain. The shipped
	/// `MenuHighlight` is not centred art — its visuals live in an inner wrapper authored 210 up
	/// from the root at x0.8 root scale — so a clone with a zeroed root paints the badge ~168 above
	/// where it is parked. Null keeps the pinned-slot fallback for a stand-in cursor.
	/// </summary>
	public Transform3D? CursorPose { get; set; }

	private static int CountQuads(Node root)
	{
		int n = root is MeshInstance3D { Mesh: not null } ? 1 : 0;
		foreach (Node child in root.GetChildren())
		{
			n += CountQuads(child);
		}

		return n;
	}

	private void BuildCursor()
	{
		if (CursorTemplate is not null)
		{
			_cursor = (Node3D)CursorTemplate.Duplicate();
			_cursor.Name = "Cursor";

			// `UpdateCursorColor` (`expandablemenuformatting.dis main.16` pc22-31) writes
			// opacity 100 to BOTH `Group.green_top` and `Group.green_top_translucent` on every
			// highlight change; the second is authored at 0.
			int raised = 0;
			foreach (string layer in new[] { "green_top", "green_top_translucent" })
			{
				if (Engine_UiTheme.FindByName(_cursor, layer) is Node3D top)
				{
					foreach (Node child in top.GetChildren())
					{
						if (child is MeshInstance3D quad && Engine_UiSceneBuilder.SetQuadOpacity(quad, 1f))
						{
							++raised;
						}
					}

					if (top is MeshInstance3D own && Engine_UiSceneBuilder.SetQuadOpacity(own, 1f))
					{
						++raised;
					}
				}
			}

			if (CursorPose is { } pose)
			{
				_cursor.Transform = pose;
				_cursorRest = pose;
				AddChild(_cursor);
				GD.Print($"[menu-view] {Name} cursor: shipped art at view-local " +
					$"({pose.Origin.X:F0}, {pose.Origin.Y:F0}, {pose.Origin.Z:F0}) scale {pose.Basis.Scale.X:F2}, " +
					$"{raised} green layer(s) raised, {CountQuads(_cursor)} quad(s)");
				return;
			}
		}
		else
		{
			// A stand-in, not the shipped cursor: sized off the table — the widest style's
			// truncate width by the full-band row pitch.
			_cursor = new MeshInstance3D
			{
				Name = "CursorStandIn",
				Mesh = new QuadMesh { Size = new Vector2(Styles[3].Truncate, RowPitch) },
				MaterialOverride = new StandardMaterial3D
				{
					Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
					ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
					CullMode = BaseMaterial3D.CullModeEnum.Disabled,
					AlbedoColor = new Color(LabelColour.R, LabelColour.G, LabelColour.B, 0.18f),
				},
			};
		}

		Slot slot = Slots[HighlightSlot - 1];
		// Behind the label: the canvas camera looks down -Z, so a smaller Z is further away.
		_cursor.Position = new Vector3(slot.X + TabOffset + HeadingTabBonus, slot.Y, -1f);
		_cursorRest = _cursor.Transform;
		AddChild(_cursor);
	}

	/// <summary>The cursor's transform on the highlight slot, as built.</summary>
	private Transform3D _cursorRest = Transform3D.Identity;

	/// <summary>
	/// Puts the cursor on the highlighted row's slot. With the list anchored under the pointer
	/// the highlight need not be in slot 4: the art moves to the row and takes that slot's
	/// scale, about the row's own origin, as the row itself does.
	/// </summary>
	private void PlaceCursor(int highlight, int centre)
	{
		if (_cursor is null || Menu?.Highlighted is not { } item)
		{
			return;
		}

		int slot = Engine_UiMenuFormatter.SlotFor(highlight, centre);
		if (slot < 1 || slot > Slots.Length)
		{
			_cursor.Visible = false;
			return;
		}

		RowPose at = PoseFor(slot, item);
		RowPose home = PoseFor(HighlightSlot, item);
		float k = home.Scale != 0f ? at.Scale / home.Scale : 1f;
		var move =
			new Transform3D(Basis.Identity, new Vector3(at.X, at.Y, 0f)) *
			new Transform3D(Basis.Identity.Scaled(new Vector3(k, k, 1f)), Vector3.Zero) *
			new Transform3D(Basis.Identity, new Vector3(-home.X, -home.Y, 0f));
		_cursor.Transform = move * _cursorRest;
	}

	/// <summary>Lays the visible window out across the slots. Call after any change to the model.</summary>
	public void Refresh()
	{
		RowsDrawn = 0;
		LabelsWithoutGlyphs = 0;

		if (_rows[0] is null)
		{
			// Bound before the node entered the tree. _Ready refreshes once the rows exist.
			return;
		}

		if (Menu is null || Menu.Visible.Count == 0)
		{
			foreach (Node3D row in _rows)
			{
				row.Visible = false;
			}

			System.Array.Clear(_rowItem);
			System.Array.Clear(_rowFaced);

			if (_cursor is not null)
			{
				_cursor.Visible = false;
			}
			return;
		}

		// The PC list draws no cursor: the console's ring is A over B (`green_top` over
		// `rim_and_red`), a pad prompt the mouse has no use for. The highlighted label lights
		// instead, and the scroll bar takes the ring's place.
		if (_cursor is not null)
		{
			_cursor.Visible = !PcMode;
		}

		int highlight = Menu.HighlightIndex + 1;
		_laidOutPc = PcMode;
		_laidOutDrop = PcMode ? PcDrop : 0f;
		int centre = _laidOutPc ? PcCentre(highlight) : highlight;
		(int first, int last) = Window(centre, Menu.Visible.Count);
		PlaceCursor(highlight, centre);


		// Which slot each entry is headed for, computed before anything is assigned: a row is
		// matched to the node ALREADY DRAWING THAT ENTRY, which makes a scroll a movement.
		var wanted = new int[_rows.Length];
		var entry = new Engine_UiExpandableMenu.Item?[_rows.Length];
		int count = 0;

		for (int display = first; display <= last && count < _rows.Length; display++)
		{
			// `Engine_UiMenuFormatter.SlotFor` is the one statement of `forceReset`'s arithmetic.
			int slot = Engine_UiMenuFormatter.SlotFor(display, centre);
			if (slot < 1 || slot > Slots.Length || (_laidOutPc && slot > 1 + PcRows))
			{
				// Skipping rather than clamping keeps an off-by-one visible as a missing row
				// instead of two entries stacked in one slot.
				continue;
			}

			wanted[count] = slot;
			entry[count] = Menu.Visible[display - 1];
			++count;
		}

		// Each entry keeps the node it was already on: `AnimatePropertiesToIndex(comp, index)`
		// moves a COMPONENT to a slot, so the component persists and the slot changes.
		// Which entries were on screen before this pass decides whether the press gate arms.
		var drawnBefore = new System.Collections.Generic.HashSet<Engine_UiExpandableMenu.Item>(
			ReferenceEqualityComparer.Instance as
				System.Collections.Generic.IEqualityComparer<Engine_UiExpandableMenu.Item>);
		foreach (Engine_UiExpandableMenu.Item? was in _rowItem)
		{
			if (was is not null)
			{
				drawnBefore.Add(was);
			}
		}

		bool cascade = false;
		for (int i = 0; i < count; ++i)
		{
			if (!drawnBefore.Remove(entry[i]!))
			{
				cascade = true;
			}
		}

		// Anything left in the set was drawn a moment ago and is not now: a component animating
		// OUT counts as much as one animating in.
		cascade |= drawnBefore.Count > 0;

		var taken = new bool[_rows.Length];
		var assigned = new int[count];
		for (int i = 0; i < count; ++i)
		{
			assigned[i] = -1;
			for (int r = 0; r < _rows.Length; ++r)
			{
				if (!taken[r] && ReferenceEquals(_rowItem[r], entry[i]))
				{
					assigned[i] = r;
					taken[r] = true;
					break;
				}
			}
		}

		for (int i = 0; i < count; ++i)
		{
			if (assigned[i] >= 0)
			{
				continue;
			}

			for (int r = 0; r < _rows.Length; ++r)
			{
				if (!taken[r])
				{
					assigned[i] = r;
					taken[r] = true;
					break;
				}
			}
		}

		// Anything not carrying a visible entry any more goes away.
		for (int r = 0; r < _rows.Length; ++r)
		{
			if (!taken[r])
			{
				_rows[r].Visible = false;
				_rowItem[r] = null;
				_rowRating[r] = int.MinValue;
				_delay[r] = 0.0;
			}
		}

		// Was this list on screen at all a moment ago? Asked once, before any row is touched; the
		// entering pose below needs it and the per-row answer cannot give it.
		bool listWasOnScreen = false;
		for (int r = 0; r < _rows.Length; ++r)
		{
			if (_rowItem[r] is not null && _rows[r].Visible)
			{
				listWasOnScreen = true;
				break;
			}
		}

		// A fold, not a scroll: the open folder changed, so the arriving rows fly in from the left.
		// Counted before any row is touched, so the batch delay is one number for all of them.
		bool foldChanged = !ReferenceEquals(_lastExpanded, Menu.Expanded);
		_lastExpanded = Menu.Expanded;
		int arriving = 0;
		for (int i = 0; i < count; ++i)
		{
			int r = assigned[i];
			if (r >= 0 && entry[i]!.Depth > 1 && !(_rowItem[r] is not null && _rows[r].Visible))
			{
				++arriving;
			}
		}

		double flyDelay = foldChanged && arriving > 0
			? Engine_UiMenuConstants.AnimateInTime /
				System.Math.Max(1, System.Math.Min(arriving, Slots.Length - HighlightSlot))
			: 0.0;

		for (int i = 0; i < count; ++i)
		{
			int row = assigned[i];
			if (row < 0)
			{
				continue;
			}

			bool wasDrawn = _rowItem[row] is not null && _rows[row].Visible;

			// An arriving entry starts one slot further out: the window moved by one, and slots 1
			// and 12 are the zero-opacity gutters, so it fades up on the way in.
			//
			// A row already drawn starts from WHERE IT IS, not from its previous target —
			// `AnimatePropertiesToIndex` reads every `start_val` with `getAttribute`, so an
			// interrupted slide continues from the pose on screen instead of snapping forward.
			//
			// A list that was not on screen at all is not scrolling, it is arriving: the
			// one-slot-out offset is signed about the highlight, so applying it to every row tears
			// the column in half. The original slides the whole assembly in from x −800 as ONE
			// group, rows already in their places.
			RowPose resting = PoseFor(wanted[i], entry[i]!);
			_delay[row] = 0.0;
			if (wasDrawn)
			{
				_from[row] = RowPose.Lerp(_from[row], _to[row], RowT(row));
			}
			else if (listWasOnScreen && foldChanged && entry[i]!.Depth > 1)
			{
				// Children only: `main.39` pc143..155 adds a newly placed component to the fly-in
				// batch only when `ParentItem ~= g_Menu.MenuRoot`; a root row coming up on a
				// collapse scrolls from the gutter slot instead (pc156..160).
				_from[row] = resting.ShiftedX(-FlyInDistance);
				_delay[row] = flyDelay;
			}
			else if (listWasOnScreen)
			{
				_from[row] = PoseFor(System.Math.Clamp(
					wanted[i] + (wanted[i] <= HighlightSlot ? -1 : 1), 1, Slots.Length), entry[i]!);
			}
			else
			{
				_from[row] = resting;
			}

			_to[row] = resting;

			if (!ReferenceEquals(_rowItem[row], entry[i]))
			{
				_rowItem[row] = entry[i];
				_rowRating[row] = int.MinValue;
			}

			// The label is keyed on the STRING, not the entry: `ApplyStatedLabels` writes the
			// spawn-time decorations (unspent ability count, new-item marker) onto the same Item
			// objects the rows already hold, so a reference test would never see them. Cheap every
			// pass — SetLabel returns immediately when the string and size are unchanged.
			SetLabel(row, entry[i]!, wanted[i]);

			// The rating is keyed on the VALUE, not the entry: a row's stars change while the row
			// stays put. Every row, no screen-level gate — `UpdateStarRating`
			// (`expandablemenuformatting.dis` main.30) ends on `goToSlide(Tab.Stars, slide)` with
			// slide 11 for anything outside 1..10, so a command row wears the EMPTY slide. A pool
			// with no Stars art resolves to null and there is nothing to drive.
			if (Ratings is not null && _rowRating[row] != entry[i]!.RatingSlide)
			{
				_rowRating[row] = entry[i]!.RatingSlide;
				if (_rows[row].GetNodeOrNull<Node3D>("Frame") is { } rated)
				{
					Ratings.Apply(rated, Engine_UiMenuFormatter.RatingFor(entry[i]!));
				}
			}

			// The face follows emphasis, which changes without the row changing (the subtitles
			// chooser moves its mark between two rows that stay put), so it is keyed on the value
			// actually applied.
			int emphasis = Emphasis(entry[i]!);
			if (_rowEmphasis[row] != emphasis || !ReferenceEquals(_rowFaced[row], entry[i]))
			{
				_rowEmphasis[row] = emphasis;
				_rowFaced[row] = entry[i];
				SetTabFace(row,
					LockedRowsSlate ? !entry[i]!.Ghosted : IsHeading(entry[i]!), emphasis);
			}

			if (HideRatingStrip &&
				_rows[row].GetNodeOrNull<Node3D>("Frame") is { } frame &&
				Engine_UiTheme.FindByName(frame, "Stars") is Node3D stars)
			{
				stars.Visible = false;
			}

			_rows[row].Visible = true;
			RowsDrawn++;
		}

		// One clock for every row: the whole column slides one slot per press.
		_elapsed = 0.0;
		_sliding = count > 0;

		// The press gate arms only for a cascade. `WaitForInput` (`expandablemenuinput.dis main.4`)
		// blocks on a set's `time_delay`, never its duration (`pc17 LT R7 &lt; R6`), and
		// `ProcessAnimationList` stamps `start_time` only on a set whose delay is non-zero
		// (`expandablemenuanimation.dis main.3 pc7..13`). The two scroll call sites pass three
		// arguments (`formatting main.12 pc86..89`, `main.13 pc108..111`) and
		// `AnimatePropertiesToIndex` defaults the delay to 0 (`animation main.7 pc45..47 TESTSET`),
		// so a plain cursor move never blocks A; only `AnimateComponentIn`/`Out` passes a delay.
		Animating = cascade;
		ApplyPoses();
	}

	/// <summary>
	/// Advances the slide. Rows move only here, so the tween runs at the display rate rather than
	/// at whatever rate the caller polls navigation.
	/// </summary>
	public override void _Process(double delta)
	{
		using var _framePhase = Refii.Core.Engine_FramePhases.Measure("frontendmenuview");

		MeasureFooter();

		// The device changed, or the footer the PC column is framed against: lay the rows out
		// again now, not at the next press.
		if (Menu is not null && (_laidOutPc != PcMode ||
			(PcMode && Mathf.Abs(PcDrop - _laidOutDrop) > 0.5f)))
		{
			Refresh();
		}

		UpdateScrollBar();
		if (!_sliding)
		{
			return;
		}

		_elapsed += delta;
		double longest = 0.0;
		for (int r = 0; r < _rows.Length; ++r)
		{
			longest = System.Math.Max(longest, _delay[r]);
		}

		if (AnimateTime <= 0.0 || _elapsed >= AnimateTime + longest)
		{
			_sliding = false;
			Animating = false;
		}

		ApplyPoses();
	}

	/// <summary>
	/// Whether a row is mid-slide — `g_Menu:WaitForInput()`. Published to the model, which
	/// implements `SelectMenuItem`'s first line, `if self:WaitForInput() then return end`; the
	/// drawing surface is the only thing here that knows.
	/// </summary>
	private bool Animating
	{
		get => _animating;
		set
		{
			_animating = value;
			if (Menu is not null)
			{
				Menu.Model.Animating = value;
			}
		}
	}

	/// <summary>Writes every visible row at its own point on its from/to interpolation.</summary>
	private void ApplyPoses()
	{
		for (int r = 0; r < _rows.Length; ++r)
		{
			// Keyed on the assignment, not on `Visible`: the clip below hides a row whose slide
			// STARTS outside the band, and a skip on that hide would keep it hidden at rest.
			if (_rowItem[r] is null)
			{
				continue;
			}

			RowPose pose = RowPose.Lerp(_from[r], _to[r], RowT(r));

			// The mask, before anything is drawn at this pose: a row outside the page band hides
			// whole. Checked per frame because the slide animates rows across the edge.
			if (ClipRect is { } clip)
			{
				float rowY = Position.Y + pose.Y;
				if (rowY < clip.Position.Y || rowY > clip.End.Y)
				{
					_rows[r].Visible = false;
					continue;
				}

				_rows[r].Visible = true;
			}

			_rows[r].Visible = true;

			_rows[r].Position = new Vector3(pose.X, pose.Y, 0f);
			_rows[r].Scale = new Vector3(pose.Scale, pose.Scale, 1f);

			// Emphasis is not drawn here: `UpdateTabInnerTexture` states it as the tab FACE and its
			// border tint, which SetTabFace applies. Dimming as well would apply the mark twice.
			// The slot's opacity and the row's ghosting MULTIPLY — the script writes them on
			// different elements, the component and `Icon.Title`, so they compose.
			var tint = new Color(
				pose.LabelColour.R, pose.LabelColour.G, pose.LabelColour.B,
				pose.Opacity / 100f * (pose.LabelOpacity / 100f) * FocusOpacity);
			Engine_UiTheme.Retint(_labels[r], tint);

			// The frame fades with the label: the scripts set opacity on the row COMPONENT, so tab
			// and text carry one value. The WHOLE row, not a child called `Frame` — screen rows do
			// not keep their art under that name. The label is excluded because it carries the
			// ghost tint and was written just above; fading it here would apply the slot twice.
			FadeArt(_rows[r], pose.Opacity / 100f * FocusOpacity, _labels[r]);
		}
	}

	/// <summary>Where a given entry sits when it is resting in a given slot.</summary>
	private RowPose PoseFor(int slot, Engine_UiExpandableMenu.Item item)
	{
		Slot placement = Slots[slot - 1];
		Style style = Styles[placement.StyleIndex - 1];

		// The PC list: one straight column at the full row's x and pitch, every shown row at full
		// size and opacity; the two gutter slots stay empty.
		if (PcMode)
		{
			placement = new Slot(
				Slots[HighlightSlot - 1].X,
				Slots[HighlightSlot - 1].Y - ((slot - HighlightSlot) * RowPitch) - PcDrop,
				slot is 1 or 12 ? 1 : HighlightSlot);
			style = Styles[placement.StyleIndex - 1];
		}

		// The end-slot fade, from `expandablemenuformatting.lua`:
		//
		//   main.0 FadeOutTopItemEarly()    g_MenuPositionSlots[2].opacity_scale_index  = 5
		//   main.1 FadeOutBottomItemEarly() g_MenuPositionSlots[11].opacity_scale_index = 5
		//
		// Style 5 is the zero-opacity one. Both are called from a screen's POPULATE, so they are a
		// standing property of the laid-out column, not a per-scroll condition. The list edge is an
		// opacity treatment; the pools have no mask.
		if (!PcMode && ((slot == 2 && FadesTopEnd) || (slot == 11 && FadesBottomEnd)))
		{
			style = Styles[4];
		}

		// The TREE states the indent, not the guest's laid-out row: `Engine_UiMenuFormatter`
		// computes the tab offset and the heading face forward from the row's depth, the direction
		// the script computes them. A stated x is absent before the guest lays the pool out, stale
		// for a frame after it moves, and wrong for any row parked in a gutter.
		bool heading = item.IsHeading;
		float tab = Engine_UiMenuFormatter.TabOffsetFor(item.Depth, heading);
		float scale = style.Scale + ((heading ? HeaderScale : DefaultScale) - 1f);

		// `SetGhosting` — the label's colour and opacity, the visible cue that a category cannot
		// be entered.
		return new RowPose(placement.X + tab, placement.Y, scale, style.Opacity)
		{
			// The PC list has no A cursor; the highlighted row's label lights instead.
			LabelColour = item.IsGhosted && !heading
				? Engine_UiMenuConstants.GhostedChildColour
				: PcMode && ReferenceEquals(item, Menu?.Highlighted)
					? Colors.White
					: Engine_UiMenuConstants.LiveTextColour,
			LabelOpacity = item.IsGhosted
				? heading
					? Engine_UiMenuConstants.GhostedHeadingOpacity
					: Engine_UiMenuConstants.GhostedChildOpacity
				: 100f,
		};
	}

	/// <summary>
	/// Whether an entry draws as a heading — <c>IsHeading(item)</c> is
	/// <c>item.ParentItem == g_Menu.MenuRoot</c>, answered from the tree. It picks the tab FACE as
	/// well as the indent and label position.
	/// </summary>
	private static bool IsHeading(Engine_UiExpandableMenu.Item item) => item.IsHeading;

	/// <summary>
	/// The window of entries on screen, 1-based and inclusive — <c>GetVisibleIndices()</c> in
	/// <c>expandablemenu.lua</c>, verbatim:
	///
	/// <code>
	/// first = max(highlight - 2, 1)
	/// count = min(total - first + 1, NUM_VISIBLE - (2 - (highlight - first)))
	/// last  = first + count - 1
	/// </code>
	///
	/// The second term keeps the window off slot 11: while the highlight is in the first two
	/// entries it has fewer than two rows above it, and the cap shrinks by that shortfall.
	/// </summary>
	public static (int First, int Last) Window(int highlight, int total) =>
		Engine_UiExpandableMenu.Window(highlight, total);

	/// <summary>
	/// Puts an entry's LABEL on a row node. The glyph mesh is the one costly thing here, so it is
	/// kept off the per-frame path — a slide writes only transforms and materials.
	/// </summary>
	private void SetLabel(int row, Engine_UiExpandableMenu.Item item, int slot)
	{
		bool heading = IsHeading(item);
		MeshInstance3D label = _labels[row];
		label.Position = new Vector3(heading ? HeadingLabelX : ChildLabelX, 0f, _labelLift);

		// The guest's own string wins: its rows carry more than the tag resolves to — an unspent
		// ability count, the new-item marker — composed at spawn time.
		string display = item.StatedLabel ?? Resolve(item.TextTag);

		// The SLOT's own style, not slot 4's for everything: `SetAttributesToVisibleIndex`
		// (`expandablemenuformatting.dis main.33` pc67..92) writes `Icon.Title.size` and, where the
		// element has it, `Icon.Title.truncate` from the style the CURRENT slot names, and the
		// truncate widths differ per slot (200 / 210 / 240 / 305).
		//
		// The RESTING slot, not the animated one: the shipped code tweens `titletexttruncate`, but
		// re-meshing the string every frame would cost more than the effect is worth. The point
		// size is 18 in every style record; it is taken from the table anyway.
		Style rowStyle = Styles[Slots[System.Math.Clamp(slot, 1, Slots.Length) - 1].StyleIndex - 1];
		int size = rowStyle.FontSize;
		float truncate = rowStyle.Truncate;

		if (_labelText[row] == display && _labelSize[row] == size &&
			_labelTruncate[row] == truncate)
		{
			return;
		}

		_labelText[row] = display;
		_labelSize[row] = size;
		_labelTruncate[row] = truncate;

		// Built opaque; ApplyPoses carries the slot's opacity every frame. Engine_UiSceneBuilder
		// forces authored text alpha to 1 because the engine overwrites the .fac's own alpha.
		var opaque = new Color(LabelColour.R, LabelColour.G, LabelColour.B, 1f);

		// Left-aligned and vertically centred on the slot: the row's x offsets are negative and its
		// truncate width positive, so the string runs rightward from the anchor. Icon.Title's own
		// alignment is in the .fac rather than the scripts, so this placement is inferred.
		// Wrap mode 0 is the engine's single-line, truncate-don't-fold mode; the truncate width
		// itself is not applied.
		bool drew = Theme?.Paint(
			label, display, size, opaque,
			horizontalAlign: 0, verticalAlign: 1,
			boxWidth: truncate, boxHeight: 0f, wrapMode: 0) ?? false;

		if (!drew)
		{
			LabelsWithoutGlyphs++;
		}

		// THE LABEL STAYS ON ITS ROW. Wrap mode 0 never folds, so a long name ran past the tab
		// and across the gutter; it is drawn smaller instead, down to a floor, so the whole
		// name stays readable inside the slot's truncate width.
		if (!label.HasMeta(LabelBaseScaleMeta))
		{
			label.SetMeta(LabelBaseScaleMeta, label.Scale);
		}

		Vector3 baseScale = label.GetMeta(LabelBaseScaleMeta).AsVector3();
		float width = label.Mesh is { } mesh ? mesh.GetAabb().Size.X : 0f;
		float fit = truncate > 0f && width > truncate
			? Mathf.Max(truncate / width, MinimumLabelFit)
			: 1f;
		label.Scale = new Vector3(baseScale.X * fit, baseScale.Y * fit, baseScale.Z);
	}

	private static readonly StringName LabelBaseScaleMeta = "label_base_scale";

	/// <summary>The smallest a long row label is drawn, as a fraction of its slot's size.</summary>
	private const float MinimumLabelFit = 0.6f;

	/// <summary>
	/// A tag to its display string, through the theme's book. An unresolved tag is shown VERBATIM
	/// rather than blanked, as <see cref="Engine_UiScreen"/> falls back too. An EMPTY result is not
	/// a resolved one — `Engine_UiTheme.Resolve` hands back whatever the book held, "" included,
	/// and an empty string meshes no glyphs. The shipped code cannot reach that state:
	/// `CreateMenuItem` defaults an absent tag to `'_no tag_'` (`expandablemenu.dis main.9 pc13..16`).
	/// </summary>
	public string Resolve(string tag)
	{
		if (Theme?.Resolve(tag) is { Length: > 0 } resolved)
		{
			return resolved;
		}

		return tag;
	}

	/// <summary>
	/// Finds a row component to clone inside a built presentation, by the script's own test:
	/// <c>g_MenuFormatter:onInitialize</c> reads <c>getElement('Buttons', self.parent)</c> and keeps
	/// every child whose <c>getElementType</c> is <c>component</c>. Null means this presentation is
	/// not the one carrying the menu.
	/// </summary>
	public static Node3D? FindRowTemplate(Node root) =>
		Engine_UiTheme.FindByName(root, RowGroupName) is { } group ? RowTemplateIn(group) : null;

	/// <summary>
	/// The group the shipped row components hang off — <c>getElement('Buttons', self.parent)</c>.
	/// Also what the game HIDES: Godot draws the rows, so leaving the guest's twelve components in
	/// the tree puts two menus on one screen.
	/// </summary>
	public const string RowGroupName = "Buttons";

	/// <summary>A row component to clone, taken from an already-located group.</summary>
	public static Node3D? RowTemplateIn(Node group)
	{
		foreach (Node child in group.GetChildren())
		{
			if (child is Node3D node && child.HasMeta("element_type") &&
				(string)child.GetMeta("element_type") == "Component")
			{
				return node;
			}
		}

		return null;
	}

	/// <summary>
	/// A cheap key over everything <see cref="Describe"/> prints EXCEPT the row positions, so the
	/// line is built only on the frames it would actually differ.
	/// </summary>
	/// <remarks>
	/// The caller reports on change and ignores the `@(x,y)` row positions, which move every
	/// frame of a slide. Building the line just to compare it is per-frame garbage, and GC
	/// pauses stall the guest's simulation thread.
	///
	/// The key covers the same inputs the stripped line does — the counts, the two MISSING
	/// tests, the lift, and per visible row the mark, the row node, the entry, the label text,
	/// whether it produced a glyph mesh, and the opacity at the precision the line prints it.
	/// Row identity is the node's instance id rather than its name: a name is a `StringName`
	/// whose getter allocates, and these nodes are clones that are never renamed, so the id
	/// moves exactly when the name could.
	/// </remarks>
	public long DescribeKey()
	{
		if (Menu is null)
		{
			return 0L;
		}

		long key = 1L;
		int blank = 0;
		for (int r = 0; r < _rows.Length; ++r)
		{
			if (_rows[r] is not { Visible: true } row || _rowItem[r] is not { } item)
			{
				continue;
			}

			bool glyphs = _labels[r].Mesh is not null;
			if (!glyphs)
			{
				++blank;
			}

			key = (key * 1000003L) ^ (long)row.GetInstanceId();
			key = (key * 1000003L) ^ (item.Name?.GetHashCode() ?? 0);
			key = (key * 1000003L) ^ (_labelText[r]?.GetHashCode() ?? 0);
			key = (key * 1000003L) ^ (ReferenceEquals(item, Menu.Highlighted) ? 1 : 0);
			key = (key * 1000003L) ^ (glyphs ? 2 : 0);
			key = (key * 1000003L) ^
				(int)System.MathF.Round(_to[r].Opacity, System.MidpointRounding.AwayFromZero);
		}

		key = (key * 1000003L) ^ Menu.HighlightIndex;
		key = (key * 1000003L) ^ Menu.Visible.Count;
		key = (key * 1000003L) ^ RowsDrawn;
		key = (key * 1000003L) ^ LabelsWithoutGlyphs;
		key = (key * 1000003L) ^ blank;
		key = (key * 1000003L) ^ (Theme is null ? 1 : 0);
		key = (key * 1000003L) ^ (RowTemplate is null ? 2 : 0);
		return (key * 1000003L) ^
			(long)System.MathF.Round(_labelLift * 10f, System.MidpointRounding.AwayFromZero);
	}

	/// <summary>
	/// A one-line description of what this view PUT ON SCREEN, row by row, walking the row NODES
	/// rather than the model: which entry each took, what it resolved to, whether that produced a
	/// glyph mesh, and where the node ended up.
	/// </summary>
	public string Describe()
	{
		if (Menu is null)
		{
			return "[menu-view] no model bound";
		}

		var parts = new List<string>();
		int blank = 0;
		for (int r = 0; r < _rows.Length; ++r)
		{
			if (_rows[r] is not { Visible: true } row || _rowItem[r] is not { } item)
			{
				continue;
			}

			bool glyphs = _labels[r].Mesh is not null;
			if (!glyphs)
			{
				++blank;
			}

			string mark = ReferenceEquals(item, Menu.Highlighted) ? ">" : " ";
			Vector3 at = row.Position;
			parts.Add($"{mark}{row.Name}:{item.Name}='{_labelText[r]}'" +
				$"{(glyphs ? string.Empty : " NO-MESH")}" +
				$"@({at.X:0},{at.Y:0}) op={_to[r].Opacity:0}" +
				// The ghost as the MODEL holds it and as the pose states it: a locked achievement
				// row drew in the live colour with the flag (by the code) set, and these two
				// say which half dropped it.
				(item.IsGhosted ? $" GHOST(label op {_to[r].LabelOpacity:0})" : string.Empty));
		}

		return $"[menu-view] {Name} highlight={Menu.HighlightIndex + 1}/{Menu.Visible.Count} " +
			$"rows={RowsDrawn} no-glyphs={LabelsWithoutGlyphs} blank={blank} " +
			$"theme={(Theme is null ? "MISSING" : "bound")} " +
			$"template={(RowTemplate is null ? "MISSING" : "cloned")} lift={_labelLift:0.#} | " +
			string.Join("  ", parts);
	}
}
