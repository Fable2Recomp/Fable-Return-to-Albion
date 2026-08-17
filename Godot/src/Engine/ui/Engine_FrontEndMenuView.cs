using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Ui;

/// <summary>
/// Draws an <see cref="Engine_FrontEndMenu"/> the way Fable 2's expandable menu draws itself.
///
/// THE LAYOUT IS NOT A DESIGN CHOICE. Every number below is read out of the shipped Lua in
/// <c>scripts/gameface/expandablemenu/</c>, disassembled with <c>tools/lua51dis.py</c>. The
/// model owns what the menu CONTAINS; this owns where it goes, and the original states that
/// plainly enough that inventing it would be the wrong call twice over.
///
/// WHAT THE SCRIPTS SAY, and where:
///
/// <list type="bullet">
/// <item><b>Twelve fixed slots, not a pitch.</b> <c>expandablemenuformatting.lua</c> builds
/// <c>g_MenuPositionSlots</c> as twelve literal <c>{position={x,y,z}, opacity_scale_index}</c>
/// records and then rebases every one by <c>x + 345, y - 185</c> in a loop at chunk load. The
/// column is not straight: x runs -345, -323, -303, -289 … -289, -295, -303, -323, -345, so
/// the list BOWS toward the reader around the highlight and falls away at both ends. A
/// constant row pitch reproduces none of that.</item>
///
/// <item><b>The highlight does not move; the list does.</b> <c>HIGHLIGHT_INDEX = 4</c>, and
/// <c>forceReset</c> assigns <c>VisibleIndex = DisplayIndex + 3 - CurrentHighlightIdx + 1</c>
/// — i.e. slot = displayIndex - highlight + 4. The selected entry is pinned to slot 4 with at
/// most two entries above it, and everything else slides underneath. <c>onScrollUp</c> and
/// <c>onScrollDown</c> exist precisely to animate that slide.</item>
///
/// <item><b>Slots 1 and 12 are gutters.</b> Their <c>opacity_scale_index</c> is 1, and
/// <c>g_MenuOpacityScaleSlots[1]</c> is <c>{scale = 0.65, opacity = 0}</c> — invisible. Slots
/// 2 and 11 are 50% at 0.75 scale, 3 and 10 are 75% at 0.85, and 4..9 are full. So ten slots
/// carry content (matching <c>g_Menu.Const.NUM_VISIBLE = 10</c> in
/// <c>expandablemenustateandconstants.lua</c>) and the two ends are where rows fade in and
/// out of existence.</item>
///
/// <item><b>Depth is an indent, and a heading is a root entry.</b>
/// <c>GetMenuItemTabOffset</c> returns <c>-65 - 5*(depth-1)</c>, plus 24 when
/// <c>IsHeading</c>; <c>IsHeading(item)</c> is <c>item.ParentItem == g_Menu.MenuRoot</c>, and
/// <c>GetMenuItemDepth</c> counts parent links, so a root entry is depth 1. A root row
/// therefore sits at slot.x - 41 and an expanded child at slot.x - 70. The label moves too:
/// <c>SetAttributesToVisibleIndex</c> puts <c>Icon.Title</c> at position.x -54 for a heading
/// and -30 for a child.</item>
///
/// <item><b>Headings are slightly larger than their children.</b> <c>onInitialize</c> sets
/// <c>HEADER_SCALE = 1.025</c> and <c>DEFAULT_SCALE = 0.925</c>, and
/// <c>GetMenuItemScaleDelta</c> returns that minus one, ADDED to the slot's own scale. So a
/// root row in slot 4 draws at 1.025 and a submenu row at 0.925 — the submenu visibly recedes
/// under its parent.</item>
///
/// <item><b>The text colour.</b> <c>SetGhosting</c> writes textcolor (255, 224, 128) at
/// opacity 100 for a live entry. A ghosted heading keeps the colour at opacity 40; a ghosted
/// child goes black at 90. Point size is 18 in every style record, which is exactly the
/// GUI_Normal bucket boundary in <see cref="Engine_UiFontLibrary.SelectFor"/>.</item>
/// </list>
///
/// WHAT THIS DOES NOT REPRODUCE, and why:
///
/// THE SLIDE IS REPRODUCED, and it is read rather than chosen. <c>ANIMATE_TIME</c> is 0.15,
/// set as a global at the top of <c>expandablemenuformatting.lua</c> beside
/// <c>HIGHLIGHT_INDEX = 4</c> and <c>NUM_VIS_SLOTS = 12</c>; the interpolation in
/// <c>UpdateAnimations</c> is a plain clamped lerp — <c>t = (now - start_time) / duration</c>,
/// clamped at 1, then <c>start_val + (end_val - start_val) * t</c> — with no easing curve, so a
/// straight interpolation is faithful here rather than a simplification. What moves is the
/// COMPONENT, not the slot: <c>AnimatePropertiesToIndex(comp, index)</c> takes a component to a
/// slot index, which is why the rows below are keyed by ENTRY and change slot, rather than
/// keyed by slot and changing text.
///
/// <list type="bullet">
/// <item>The <b>spawn and removal</b> animation. <c>ANIMATE_IN_TIME</c> and
/// <c>ANIMATE_OUT_TIME</c> are both 0.08 in <c>expandablemenustateandconstants.lua</c> and
/// belong to an item entering or leaving the LIST, which is a different event from one changing
/// slot. An entry arriving in the window here simply slides in from one slot beyond the
/// edge.</item>
/// <item><b>Truncation.</b> Each style carries a <c>truncate</c> width (200/210/240/305) and
/// the engine clips a long label to it. <see cref="Engine_UiTextMesh"/> has no truncate mode —
/// wrap mode 0 is a single line it does not fold — so an over-long label overhangs here.</item>
/// <item><b>Ghosting</b>, because <see cref="Engine_FrontEndMenu.Item"/> has no ghosted flag.
/// Its model drops an unavailable entry entirely rather than showing it greyed, which is the
/// front end's own behaviour — the guest's spawn conditions decide whether "Load Game" EXISTS.
/// The ghost colours are kept as constants so the flag can be wired without re-reading the
/// scripts.</item>
/// </list>
///
/// THE FRAME ART comes from the presentation, not from here — and it is NOT cloned at
/// runtime. <c>expandablemenucloning.lua</c> is an empty chunk (one RETURN), unlike the wheel
/// menu's <c>menucloning.lua</c> which really does clone from a template. Instead
/// <c>g_MenuFormatter:onInitialize</c> collects components the presentation already ships:
/// <c>getChildren(getElement('Buttons', self.parent))</c>, keeping each child whose
/// <c>getElementType</c> is <c>component</c>, and stores them as <c>MenuItems</c> — one per
/// slot, which is why there are twelve. Each carries <c>Tab.InnerBrown</c> (root rows) or
/// <c>Tab.InnerRed</c> (submenu rows) plus an <c>Icon.Title</c> text element; see
/// <c>UpdateTabInnerTexture</c>, which picks between them on <c>IsHeading</c>.
///
/// So <see cref="RowTemplate"/> takes one of those built nodes and this duplicates it per
/// slot, which reaches the same picture from a Godot tree that has no component instancing.
/// Leave it null and rows are text only — honest about having no art, rather than inventing a
/// tab that would then have to be un-invented.
/// </summary>
public partial class Engine_FrontEndMenuView : Node3D
{
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
	public static readonly Slot[] Slots =
	{
		new(0f, 85f, 1),
		new(22f, 69f, 2),
		new(42f, 15f, 3),
		new(56f, -45f, 4),
		new(56f, -106f, 4),
		new(56f, -164f, 4),
		new(56f, -222f, 4),
		new(56f, -280f, 4),
		new(50f, -338f, 4),
		new(42f, -396f, 3),
		new(22f, -454f, 2),
		new(0f, -475f, 1),
	};

	/// <summary>
	/// <c>g_MenuOpacityScaleSlots</c>. Entries 1 and 5 are identical; only 1 is reachable from
	/// the slot table as shipped, 5 being what <c>FadeOutTopItemEarly</c> and
	/// <c>FadeOutBottomItemEarly</c> retarget slots 2 and 11 to at the ends of a long list.
	/// </summary>
	public static readonly Style[] Styles =
	{
		new(0.65f, 0f, 18, 200f),
		new(0.75f, 50f, 18, 210f),
		new(0.85f, 75f, 18, 240f),
		new(1.00f, 100f, 18, 305f),
		new(0.65f, 0f, 18, 200f),
	};

	/// <summary>The slot the highlighted entry is pinned to — <c>HIGHLIGHT_INDEX</c>, 1-based.</summary>
	public const int HighlightSlot = 4;

	/// <summary>How many entries the window may hold — <c>g_Menu.Const.NUM_VISIBLE</c>.</summary>
	public const int WindowSize = 10;

	/// <summary><c>GetMenuItemTabOffset</c>: the base, the per-depth step, and the heading bonus.</summary>
	public const float TabOffset = -65f;
	public const float TabOffsetPerDepth = -5f;
	public const float HeadingTabBonus = 24f;

	/// <summary><c>Icon.Title</c>'s position.x, from <c>SetAttributesToVisibleIndex</c>.</summary>
	public const float HeadingLabelX = -54f;
	public const float ChildLabelX = -30f;

	/// <summary><c>HEADER_SCALE</c> / <c>DEFAULT_SCALE</c> from <c>g_MenuFormatter:onInitialize</c>.</summary>
	public const float HeaderScale = 1.025f;
	public const float DefaultScale = 0.925f;

	/// <summary>The live entry colour written by <c>SetGhosting</c> — (255, 224, 128).</summary>
	public static readonly Color LabelColour = new(1f, 224f / 255f, 128f / 255f);

	/// <summary>A ghosted ROOT entry: same colour, opacity 40. Unused until the model has the flag.</summary>
	public const float GhostedHeadingOpacity = 40f;

	/// <summary>A ghosted CHILD entry: black at opacity 90.</summary>
	public static readonly Color GhostedChildColour = new(0f, 0f, 0f);
	public const float GhostedChildOpacity = 90f;

	/// <summary>
	/// Where the menu component sits on the screen.
	///
	/// The slot table above is component-local because of the load-time rebase; undoing it puts
	/// the rows back in the authored 1280x720 canvas of <c>frontendmainmenu</c>, which is where
	/// a standalone view wants them. Mounted UNDER a real ExpandableMenu node, set this to zero
	/// — the parent already supplies it.
	/// </summary>
	public static readonly Vector3 CanvasAnchor = new(-345f, 185f, 0f);

	/// <summary>
	/// Row pitch through the full-size band, derived from the slot table rather than assumed:
	/// slots 5..8 are 58 apart. Only used to size the stand-in cursor.
	/// </summary>
	public static float RowPitch => Slots[4].Y - Slots[5].Y;

	/// <summary>
	/// The shipped look this draws with — fonts, strings and the text material.
	///
	/// One property rather than the three it used to hold separately, because every one of
	/// them is the same question: what does the GAME look like. A screen that sources two of
	/// the three and invents the third is the failure mode worth designing out.
	///
	/// Without it a row still takes its position from the slot table, so the layout is
	/// checkable with no game data at all — the labels are simply absent, and
	/// <see cref="LabelsWithoutGlyphs"/> says so.
	/// </summary>
	public Engine_UiTheme? Theme { get; set; }

	/// <summary>
	/// A built row component to duplicate per slot, or null for text-only rows.
	///
	/// The original does not duplicate — it reuses the twelve components the presentation
	/// already ships. Duplicating one of them gets to the same picture from a tree that has no
	/// component instancing; see the class remarks.
	/// </summary>
	public Node3D? RowTemplate { get; set; }

	/// <summary>
	/// The highlight cursor to place at <see cref="HighlightSlot"/>, or null for the stand-in.
	///
	/// The scripts never move this: <c>UpdateCursorColor</c> only sets the opacity of
	/// <c>Group.green_top</c> and <c>Group.green_top_translucent</c> inside the
	/// <c>MenuHighlight</c> element, and its position is authored in the <c>.bgf</c>. So the
	/// slot-4 placement used for the stand-in is inferred from the pinning rule, not read.
	/// </summary>
	public Node3D? CursorTemplate { get; set; }

	/// <summary>The model being drawn. Null until <see cref="Bind"/>.</summary>
	public Engine_FrontEndMenu? Menu { get; private set; }

	/// <summary>Rows currently on screen, for a caller that wants to assert on the layout.</summary>
	public int RowsDrawn { get; private set; }

	/// <summary>Labels that produced no glyphs. Non-zero with a non-zero <see cref="RowsDrawn"/> means no font.</summary>
	public int LabelsWithoutGlyphs { get; private set; }

	/// <summary>
	/// How long a row takes to reach its new slot — <c>ANIMATE_TIME</c>, set as a global at the
	/// top of <c>expandablemenuformatting.lua</c> alongside <c>HIGHLIGHT_INDEX = 4</c> and
	/// <c>NUM_VIS_SLOTS = 12</c>.
	///
	/// NOT the same number as <c>ANIMATE_IN_TIME</c> / <c>ANIMATE_OUT_TIME</c>, which are both
	/// 0.08 in <c>expandablemenustateandconstants.lua</c> and belong to an item being spawned
	/// into or removed from the list rather than to one changing slot.
	/// </summary>
	public const double AnimateTime = 0.15;

	/// <summary>Where a row is: the four properties a slide interpolates.</summary>
	private readonly struct RowPose
	{
		public readonly float X;
		public readonly float Y;
		public readonly float Scale;

		/// <summary>Authored 0..100, as the slot table states it.</summary>
		public readonly float Opacity;

		public RowPose(float x, float y, float scale, float opacity)
		{
			X = x;
			Y = y;
			Scale = scale;
			Opacity = opacity;
		}

		/// <summary>
		/// A plain clamped lerp, which is what the engine does — <c>UpdateAnimations</c>
		/// computes <c>t = (now - start_time) / duration</c>, clamps it at 1 and writes
		/// <c>start_val + (end_val - start_val) * t</c>. There is no easing curve to miss.
		/// </summary>
		public static RowPose Lerp(RowPose from, RowPose to, float t) => new(
			from.X + ((to.X - from.X) * t),
			from.Y + ((to.Y - from.Y) * t),
			from.Scale + ((to.Scale - from.Scale) * t),
			from.Opacity + ((to.Opacity - from.Opacity) * t));
	}

	private readonly Node3D[] _rows = new Node3D[Slots.Length];
	private readonly MeshInstance3D[] _labels = new MeshInstance3D[Slots.Length];
	private readonly string[] _labelText = new string[Slots.Length];
	private readonly int[] _labelSize = new int[Slots.Length];

	/// <summary>Which entry each row node is currently drawing. Null means the node is free.</summary>
	private readonly Engine_FrontEndMenu.Item?[] _rowItem =
		new Engine_FrontEndMenu.Item?[Slots.Length];

	private readonly RowPose[] _from = new RowPose[Slots.Length];
	private readonly RowPose[] _to = new RowPose[Slots.Length];

	private double _elapsed;
	private bool _animating;

	private Node3D? _cursor;

	public override void _Ready()
	{
		Position = CanvasAnchor;
		BuildRows();
		Refresh();
	}

	/// <summary>Points the view at a model and lays it out. Safe before <c>_Ready</c>.</summary>
	public void Bind(Engine_FrontEndMenu menu)
	{
		Menu = menu;
		if (_rows[0] is not null)
		{
			Refresh();
		}
	}

	/// <summary>
	/// The menus this one was opened from, innermost last. Empty at the root.
	///
	/// A stack rather than a parent pointer on the model, because the same model can be
	/// reachable from more than one place — a "Controls" page opened from both the front end
	/// and the pause menu should return to whichever one opened it, and a parent pointer
	/// would send it to whichever opened it last.
	/// </summary>
	private readonly Stack<Engine_FrontEndMenu> _opened = new();

	/// <summary>How deep into submenus the view currently is. 0 at the root menu.</summary>
	public int Depth => _opened.Count;

	/// <summary>
	/// Advances the highlight, on whichever model is currently BOUND.
	///
	/// Through the view rather than straight to a model the caller is holding, because once
	/// submenus exist those are two different objects: a caller that kept a reference to the
	/// root menu and navigated it would move the selection on a list that is not on screen,
	/// and the visible one would sit frozen. Returns true when the highlight moved.
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
	/// Activates the highlighted entry, opening a submenu if that is what it carries.
	///
	/// The single entry point for "the player pressed A", so the game and the harness cannot
	/// disagree about what that means. Returns the NAME to dispatch to the guest, or null when
	/// the press was fully handled here — an expand, a collapse, a submenu opening, a SCREEN
	/// opening, or a host-only entry the guest was never shipped with.
	///
	/// A screen is the one of those this view cannot finish on its own: it is not a list, so
	/// there is nothing here that could draw it. The name is latched into
	/// <see cref="TakeOpenedScreen"/> for whoever owns it, and the menu stays exactly where it
	/// is — which is also what the original does, since its gender card is drawn OVER the menu
	/// rather than in place of it.
	/// </summary>
	public string? Activate()
	{
		if (Menu is null)
		{
			return null;
		}

		// The model first: it owns expand/collapse and decides whether the press is the
		// guest's business. Only then is the submenu considered, because an entry with BOTH
		// children and a submenu is a modelling mistake and expanding is the shipped
		// behaviour — following the original where the two disagree.
		Engine_FrontEndMenu.Item? item = Menu.Highlighted;
		string? dispatch = Menu.Activate();

		if (dispatch is null && item?.Opens is { } screen)
		{
			// A refused entry opens nothing. The model latched the reason as a text tag and the
			// owner takes it — see Engine_FrontEndMenu.NewGameRefusal.
			if (Menu.Warning is null)
			{
				_openedScreen = screen;
			}

			return null;
		}

		if (dispatch is null && item?.Submenu is { } submenu && item.Children.Count == 0)
		{
			_opened.Push(Menu);
			Bind(submenu);
			return null;
		}

		Refresh();
		return dispatch;
	}

	/// <summary>
	/// The screen the last <see cref="Activate"/> asked for, cleared by reading it.
	///
	/// Latched rather than returned, because <see cref="Activate"/> already has one return
	/// value with a settled meaning — the name to dispatch — and a caller that ignores this
	/// gets a menu entry that does nothing rather than a wrong screen.
	/// </summary>
	public string? TakeOpenedScreen()
	{
		string? screen = _openedScreen;
		_openedScreen = null;
		return screen;
	}

	private string? _openedScreen;

	/// <summary>
	/// Backs out one level: collapses an expanded entry, else closes a submenu.
	///
	/// Returns false only when there is nothing left to back out of — at the root menu with
	/// nothing expanded — which is the caller's signal that B means "leave this screen"
	/// rather than "go up".
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

		if (_opened.Count == 0)
		{
			return false;
		}

		Bind(_opened.Pop());
		return true;
	}

	/// <summary>
	/// Builds twelve persistent row nodes once.
	///
	/// Persistent rather than rebuilt per frame because a slot's identity is what the original
	/// animates — <c>onScrollDown</c> moves the SAME component from one slot to the next — so a
	/// view that discards and recreates nodes has nowhere to hang the tween when it is added.
	/// </summary>
	private void BuildRows()
	{
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
				// supplies the position, so the clone contributes art and nothing else.
				frame.Position = Vector3.Zero;
				row.AddChild(frame);

				// ART AND NOTHING ELSE, which this did not previously deliver. A shipped
				// `MenuItemButton` carries its own `Icon.Title` text element, and the artist
				// left the authored placeholder in it — so every cloned row drew
				// "DEFAULT_TEXT_ID" underneath the label this view had just laid out. It goes
				// unnoticed in the running game only because the guest's live state happens to
				// leave that element invisible, which is not a thing to depend on: the view
				// owns the label, so the clone must not bring one.
				StripText(frame);

				// EACH ROW NEEDS ITS OWN MATERIALS. Node.Duplicate copies the node but SHARES
				// its sub-resources, so all twelve clones and the shipped original hold one
				// StandardMaterial3D per quad. Fading a row would fade every row and the
				// presentation's own component with it — and a row's opacity is per-slot (the
				// two gutters are 0), so they cannot share.
				OwnMaterials(frame);
			}

			var label = new MeshInstance3D { Name = "Label" };
			row.AddChild(label);
			_labels[i] = label;
			_labelText[i] = string.Empty;
		}

		BuildCursor();
	}

	/// <summary>
	/// Removes the TEXT a cloned row component brings with it.
	///
	/// Keyed on the element TYPE the scene builder recorded, not on the name: the scripts call
	/// the element `Icon.Title`, but a row is being taken for its frame and its star, and any
	/// text inside it is a label this view is about to draw itself.
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

	/// <summary>
	/// Gives every mesh under a cloned row a private copy of its material, and records the
	/// authored alpha so a later fade is relative to the artist's value rather than to 1.
	/// </summary>
	private static void OwnMaterials(Node node)
	{
		if (node is MeshInstance3D mesh && Engine_UiSceneBuilder.OwnQuadMaterial(mesh))
		{
			mesh.SetMeta(AuthoredAlphaMeta, Engine_UiSceneBuilder.QuadOpacity(mesh));
		}

		foreach (Node child in node.GetChildren())
		{
			OwnMaterials(child);
		}
	}

	/// <summary>Where a row quad's authored alpha is kept, so fading never compounds.</summary>
	private const string AuthoredAlphaMeta = "authored_alpha";

	/// <summary>
	/// The two alternative tab faces a row component ships with — brown for a root entry, red
	/// for a submenu one, chosen by <see cref="SetTabFace"/>.
	/// </summary>
	public const string HeadingTabFace = "InnerBrown";
	public const string ChildTabFace = "InnerRed";

	/// <summary>
	/// Picks the tab face this entry's depth calls for, the way the scripts pick it.
	///
	/// <c>UpdateTabInnerTextureMenu</c> is two <c>setAttribute(group, 'opacity', 0|100)</c>
	/// calls — on the GROUPS, never on their quads — so this re-runs exactly that. Without it
	/// every row wears the file's default face, which is the red one, and the root entries look
	/// like submenu rows.
	///
	/// It is NOT the same as forcing the quads opaque, which is what I tried first: the brown
	/// group carries a full-bleed backing plate whose authored alpha is only zero because the
	/// GROUP is, and lifting the leaves directly puts that plate on screen at full strength.
	/// </summary>
	private void SetTabFace(int row, bool heading)
	{
		if (_rows[row].GetNodeOrNull<Node3D>("Frame") is not { } frame)
		{
			return;
		}

		Face(HeadingTabFace, heading ? 1f : 0f);
		Face(ChildTabFace, heading ? 0f : 1f);

		void Face(string name, float opacity)
		{
			if (Engine_UiTheme.FindByName(frame, name) is Node3D group)
			{
				Recompute(group, Inherited(group) * opacity);
			}
		}

		// The group's own factor is REPLACED; everything below it keeps the artist's. That is
		// the whole of what setAttribute on a group does.
		static void Recompute(Node node, float accumulated)
		{
			if (node is MeshInstance3D mesh)
			{
				// Written to the fade baseline rather than to the material, because the slot
				// opacity is applied on top of it every frame — see ApplyPoses. Setting the
				// material here would last exactly until the next frame.
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
	private static void FadeArt(Node node, float opacity)
	{
		if (node is MeshInstance3D mesh && mesh.HasMeta(AuthoredAlphaMeta))
		{
			float authored = (float)mesh.GetMeta(AuthoredAlphaMeta);
			Engine_UiSceneBuilder.SetQuadOpacity(mesh, authored * opacity);
		}

		foreach (Node child in node.GetChildren())
		{
			FadeArt(child, opacity);
		}
	}

	private void BuildCursor()
	{
		if (CursorTemplate is not null)
		{
			_cursor = (Node3D)CursorTemplate.Duplicate();
			_cursor.Name = "Cursor";
		}
		else
		{
			// A STAND-IN, not the shipped cursor. Sized off the table rather than by eye —
			// the widest style's truncate width by the full-band row pitch — so it marks the
			// slot the highlight is pinned to without pretending to be art.
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
		// Behind the label in the painter's sense: the canvas camera is orthographic looking
		// down -Z, so a smaller Z is further away.
		_cursor.Position = new Vector3(slot.X + TabOffset + HeadingTabBonus, slot.Y, -1f);
		AddChild(_cursor);
	}

	/// <summary>
	/// Lays the visible window out across the slots. Call after any change to the model.
	/// </summary>
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

			if (_cursor is not null)
			{
				_cursor.Visible = false;
			}
			return;
		}

		if (_cursor is not null)
		{
			_cursor.Visible = true;
		}

		(int first, int last) = Window(Menu.HighlightIndex + 1, Menu.Visible.Count);
		int highlight = Menu.HighlightIndex + 1;

		// WHICH SLOT EACH ENTRY IS HEADED FOR. Computed before anything is assigned, because a
		// row is matched to the node ALREADY DRAWING THAT ENTRY — that is what makes a scroll a
		// movement rather than a repaint.
		var wanted = new int[_rows.Length];
		var entry = new Engine_FrontEndMenu.Item?[_rows.Length];
		int count = 0;

		for (int display = first; display <= last && count < _rows.Length; display++)
		{
			int slot = display - highlight + HighlightSlot;
			if (slot < 1 || slot > Slots.Length)
			{
				// Cannot happen for a window this rule produced; skipping rather than
				// clamping keeps a future off-by-one visible as a missing row instead of
				// two entries stacked in one slot.
				continue;
			}

			wanted[count] = slot;
			entry[count] = Menu.Visible[display - 1];
			++count;
		}

		// Each entry keeps the node it was already on. The original does the same thing from
		// the other end — `AnimatePropertiesToIndex(comp, index)` moves a COMPONENT to a slot,
		// so the component is the persistent thing and the slot is what changes. A slot-keyed
		// view would have to retext every node on every press, and would have nothing whose
		// position could be tweened.
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
			}
		}

		for (int i = 0; i < count; ++i)
		{
			int row = assigned[i];
			if (row < 0)
			{
				continue;
			}

			bool wasDrawn = _rowItem[row] is not null && _rows[row].Visible;

			// AN ENTRY ARRIVING STARTS ONE SLOT FURTHER OUT, which is what "the list slides"
			// means: the window moved by one, so an entry entering it was, a moment ago, just
			// past the edge. Slots 1 and 12 are the zero-opacity gutters, so an entry sliding in
			// from beyond them fades up on its way rather than appearing at full strength.
			_from[row] = wasDrawn
				? _to[row]
				: PoseFor(System.Math.Clamp(wanted[i] + (wanted[i] <= HighlightSlot ? -1 : 1),
					1, Slots.Length), entry[i]!);

			_to[row] = PoseFor(wanted[i], entry[i]!);

			if (!ReferenceEquals(_rowItem[row], entry[i]))
			{
				_rowItem[row] = entry[i];
				SetLabel(row, entry[i]!);
			}

			_rows[row].Visible = true;
			RowsDrawn++;
		}

		// One clock for every row, because they all move together: the whole column slides by
		// one slot on a press. `ANIMATE_TIME` is 0.15, set as a global in
		// `expandablemenuformatting.lua`, and the interpolation is a plain clamped lerp with no
		// easing — `UpdateAnimations` computes `t = (now - start) / duration`, clamps it at 1
		// and writes `start + (end - start) * t`, which is the whole of it.
		_elapsed = 0.0;
		_animating = true;
		ApplyPoses(0f);
	}

	/// <summary>
	/// Advances the slide. Rows move only here, so the tween runs at the display rate rather
	/// than at whatever rate the caller happens to poll navigation.
	/// </summary>
	public override void _Process(double delta)
	{
		if (!_animating)
		{
			return;
		}

		_elapsed += delta;
		float t = AnimateTime <= 0.0 ? 1f : (float)(_elapsed / AnimateTime);
		if (t >= 1f)
		{
			t = 1f;
			_animating = false;
		}

		ApplyPoses(t);
	}

	/// <summary>Writes every visible row at a point on its own from/to interpolation.</summary>
	private void ApplyPoses(float t)
	{
		for (int r = 0; r < _rows.Length; ++r)
		{
			if (_rowItem[r] is null || !_rows[r].Visible)
			{
				continue;
			}

			RowPose pose = RowPose.Lerp(_from[r], _to[r], t);

			_rows[r].Position = new Vector3(pose.X, pose.Y, 0f);
			_rows[r].Scale = new Vector3(pose.Scale, pose.Scale, 1f);

			var tint = new Color(LabelColour.R, LabelColour.G, LabelColour.B, pose.Opacity / 100f);
			Engine_UiTheme.Retint(_labels[r], tint);

			// The frame fades with the label. The scripts set opacity on the row COMPONENT, so
			// the tab and its text carry one value — and without this the two gutter slots
			// would slide a fully opaque tab in under a label that had already faded to
			// nothing.
			if (_rows[r].GetNodeOrNull<Node3D>("Frame") is { } frame)
			{
				FadeArt(frame, pose.Opacity / 100f);
			}
		}
	}

	/// <summary>Where a given entry sits when it is resting in a given slot.</summary>
	private static RowPose PoseFor(int slot, Engine_FrontEndMenu.Item item)
	{
		Slot placement = Slots[slot - 1];
		Style style = Styles[placement.StyleIndex - 1];

		// A root entry is a heading; depth counts parent links, so the model's two levels are
		// depth 1 and 2. Deeper nesting would need the model to expose a real chain.
		bool heading = item.Parent is null;
		int depth = heading ? 1 : 2;

		float tab = TabOffset + (TabOffsetPerDepth * (depth - 1)) + (heading ? HeadingTabBonus : 0f);
		float scale = style.Scale + ((heading ? HeaderScale : DefaultScale) - 1f);

		return new RowPose(placement.X + tab, placement.Y, scale, style.Opacity);
	}

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
	/// The second term is what keeps the window from running past slot 11: while the highlight
	/// is still in the first two entries it has fewer than two rows above it, and the cap
	/// shrinks by exactly that shortfall.
	/// </summary>
	public static (int First, int Last) Window(int highlight, int total)
	{
		if (total <= 0)
		{
			return (1, 0);
		}

		int first = Math.Max(highlight - 2, 1);
		int span = total - first + 1;
		int cap = WindowSize - (2 - (highlight - first));
		int count = Math.Min(span, cap);
		return (first, first + count - 1);
	}

	/// <summary>
	/// Puts an entry's LABEL on a row node. Called only when the row changes which entry it
	/// draws, which a scroll does not — the entry keeps its node and moves with it.
	///
	/// The glyph mesh is the one thing here that costs anything, and this is what keeps it off
	/// the per-frame path: the position, scale and opacity that change every frame of a slide
	/// are a transform and a material write, and the text is not one of them.
	/// </summary>
	private void SetLabel(int row, Engine_FrontEndMenu.Item item)
	{
		bool heading = item.Parent is null;
		SetTabFace(row, heading);

		MeshInstance3D label = _labels[row];
		label.Position = new Vector3(heading ? HeadingLabelX : ChildLabelX, 0f, 0f);

		string display = Resolve(item.TextTag);

		// The point size is the same in every style record — 18 across all five — so a row
		// changing slot never re-lays its glyphs. Taken from the table rather than written as
		// 18 so a style that ever differs is honoured instead of silently ignored.
		int size = Styles[3].FontSize;

		if (_labelText[row] == display && _labelSize[row] == size)
		{
			return;
		}

		_labelText[row] = display;
		_labelSize[row] = size;

		// Built opaque; ApplyPoses carries the slot's own opacity every frame. Engine_UiSceneBuilder
		// deliberately forces authored text alpha to 1 because the engine overwrites the .fac's
		// own alpha, and the slot opacity is a different value — the scripts set it on the row
		// COMPONENT, and the two gutters are 0.
		var opaque = new Color(LabelColour.R, LabelColour.G, LabelColour.B, 1f);

		// Left-aligned and vertically centred on the slot. The row's x offsets are negative and
		// its truncate width positive, so the string runs rightward from the anchor — but
		// Icon.Title's own authored alignment is in the .fac rather than in the scripts, so this
		// is the one placement value here that is inferred.
		//
		// Wrap mode 0 is the engine's single-line, truncate-don't-fold mode. The truncate width
		// itself is not applied; see the class remarks.
		(ArrayMesh? mesh, Texture2D? atlas) = Theme?.Glyphs(
			display, size, opaque,
			horizontalAlign: 0, verticalAlign: 1,
			boxWidth: Styles[3].Truncate, boxHeight: 0f, wrapMode: 0) ?? (null, null);

		label.Mesh = mesh;
		label.MaterialOverride = mesh is null
			? null
			: Engine_UiSceneBuilder.TextMaterial(opaque, atlas);

		if (mesh is null)
		{
			LabelsWithoutGlyphs++;
		}
	}

	/// <summary>
	/// A tag to its display string, through the theme's book.
	///
	/// An unresolved tag is shown VERBATIM rather than blanked, which is what the engine does
	/// too — <see cref="Engine_UiScreen"/> falls back the same way, and it is how a missing
	/// entry announces itself instead of leaving an empty row.
	/// </summary>
	public string Resolve(string tag) => Theme?.Resolve(tag) ?? tag;

	/// <summary>
	/// Finds a row component to clone inside a built presentation.
	///
	/// The names are the scripts', not ours: <c>g_MenuFormatter:onInitialize</c> reads
	/// <c>getElement('Buttons', self.parent)</c> and keeps every child whose
	/// <c>getElementType</c> is <c>component</c>. <see cref="Engine_UiSceneBuilder"/> records
	/// the element type as metadata, so the same test runs here.
	///
	/// Returns null when the group is absent, which is not a failure — it means this
	/// presentation is not the one carrying the menu.
	/// </summary>
	public static Node3D? FindRowTemplate(Node root) =>
		Engine_UiTheme.FindByName(root, RowGroupName) is { } group ? RowTemplateIn(group) : null;

	/// <summary>
	/// The group the shipped row components hang off — <c>getElement('Buttons', self.parent)</c>.
	/// Named rather than inlined because the game also needs to HIDE this group: Godot draws
	/// the rows now, and leaving the guest's twelve components in the tree puts two menus on
	/// one screen.
	/// </summary>
	public const string RowGroupName = "Buttons";

	/// <summary>
	/// A row component to clone, taken from an already-located group.
	///
	/// Split from <see cref="FindRowTemplate"/> so a caller that reached the group through the
	/// theme's catalogue does not have to search the tree for it a second time.
	/// </summary>
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

	/// <summary>A one-line description of what is on screen, for a check tool's log.</summary>
	public string Describe()
	{
		if (Menu is null)
		{
			return "[menu-view] no model bound";
		}

		(int first, int last) = Window(Menu.HighlightIndex + 1, Menu.Visible.Count);
		var parts = new List<string>();
		for (int display = first; display <= last; display++)
		{
			int slot = display - Menu.HighlightIndex - 1 + HighlightSlot;
			Engine_FrontEndMenu.Item item = Menu.Visible[display - 1];
			string mark = display == Menu.HighlightIndex + 1 ? ">" : " ";
			parts.Add($"{mark}slot{slot}:{item.Name}");
		}

		return $"[menu-view] highlight={Menu.HighlightIndex + 1}/{Menu.Visible.Count} " +
			$"window={first}..{last} rows={RowsDrawn} no-glyphs={LabelsWithoutGlyphs} | " +
			string.Join("  ", parts);
	}
}
