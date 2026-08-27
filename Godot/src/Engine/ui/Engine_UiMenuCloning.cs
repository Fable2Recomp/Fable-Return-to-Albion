using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Ports <c>menucloning.lua</c>: copies the wheel menu's item art off
/// <c>MenuItemTemplate</c> into every item.
///
/// Wheel items are authored as bare containers holding only an <c>Icon</c>. The background,
/// orb, nine-slice frame and beam exist once per presentation, under the template, and
/// <c>g_Menu:CloneMenuItemElementsFromTemplate</c> copies them into each item at runtime.
/// <c>PlaceTemplateElementsAtDefaultPositions</c> runs first so the default offsets are
/// carried into every copy.
///
/// Only <c>Icon</c>, <c>Background</c> and <c>Orb</c> are asserted by the Lua; <c>Frame</c>,
/// <c>Beam</c>, <c>Highlight</c>, <c>UpArrows</c> and <c>DownArrows</c> are nil-guarded and
/// skipped when absent. quickmainmenu's template has one child, its <c>Icon</c>, with no
/// <c>Highlight</c> and no arrows.
///
/// Element parentage and the folder test come from <c>menuhelpers.lua</c>:
/// <c>GetIconElements</c> (main.6) and <c>IsAFolder</c> (main.4).
/// </summary>
public static class Engine_UiMenuCloning
{
	/// <summary>
	/// The source item, reached by <c>menustateandconstants.lua</c> as
	/// <c>getElement("MenuItemTemplate", getElement("Scene.Layer"))</c>.
	/// </summary>
	public const string TemplateName = "MenuItemTemplate";

	/// <summary>The layer the template hangs under.</summary>
	public const string LayerName = "Layer";

	/// <summary><c>g_Menu.RootItem</c>. Found under <c>Layer/Ring</c>.</summary>
	public const string RootItemName = "Root";

	/// <summary>The item's art container, and the destination every piece is copied into.</summary>
	public const string IconName = "Icon";

	/// <summary>
	/// Pieces that live under the <c>Icon</c>. <c>GetIconElements</c> resolves
	/// <c>Beam</c>, <c>Title</c>, <c>Frame</c>, <c>Background</c> and <c>Orb</c> against the
	/// icon, not against the item.
	/// </summary>
	public const string BackgroundName = "Background";

	public const string OrbName = "Orb";
	public const string FrameName = "Frame";
	public const string BeamName = "Beam";

	/// <summary>
	/// Gates the background and orb copies: the Lua clones neither when
	/// <c>getElement('Image', icon)</c> is nil.
	/// </summary>
	public const string ImageName = "Image";

	/// <summary>Pieces that live under the item, one level up from the Icon.</summary>
	public const string HighlightName = "Highlight";

	public const string UpArrowsName = "UpArrows";
	public const string DownArrowsName = "DownArrows";

	/// <summary>
	/// The attribute <c>IsAFolder</c> reads:
	/// <c>getAttribute(item.element, 'MenuItemType') == g_Menu.ItemConst.FOLDER</c>.
	/// </summary>
	public const string MenuItemTypeAttribute = "MenuItemType";

	/// <summary><c>g_Menu.ItemConst.FOLDER</c>. NODE is 0, the button kinds are 2 and up.</summary>
	public const int FolderItemType = 1;

	/// <summary>
	/// Cells the frame's tint pass walks — <c>for i = 1, 9</c> over
	/// <c>GetTableWithChildByIndex(frame, i)</c>, the nine-slice.
	/// </summary>
	public const int FrameCells = 9;

	/// <summary>
	/// Constants the folder arrows are placed with.
	///
	/// <c>menucloning.lua</c> reads <c>g_Menu.FolderConst.ITEM_START_POS_Y</c>,
	/// <c>FolderConst.MAX_DISPLAYED_CHILDREN</c> and <c>ExpandConst.HIERARCHY_GAP</c>, but no
	/// extracted script or wheel presentation declares them. Arrow placement is skipped when
	/// this is null and counted in the report rather than placed from invented values.
	/// </summary>
	public readonly record struct FolderMetrics(
		float ItemStartPosY, float HierarchyGap, int MaxDisplayedChildren);

	/// <summary>What one run did.</summary>
	/// <param name="Items">Items reached from the root.</param>
	/// <param name="Dressed">Items that received at least one copied piece.</param>
	/// <param name="Cloned">Nodes created in total, across every item.</param>
	/// <param name="Folders">Items whose <c>MenuItemType</c> states FOLDER.</param>
	/// <param name="ArrowsUnplaced">
	/// Arrow elements left at their authored position because no <see cref="FolderMetrics"/>
	/// was supplied.
	/// </param>
	/// <param name="TintsCleared">
	/// <c>TintingAmount</c> clears applied across the frame's nine cells — the guest's own
	/// pre-clone reset, which this pass makes rather than merely counts.
	/// </param>
	/// <param name="TintWritesDropped">
	/// Writes the guest makes and we still do not — currently only <c>diffuse.a = 103</c> on the
	/// Highlight's material, which needs a colour-alpha lane the quad does not carry. See
	/// <see cref="Apply"/>.
	/// </param>
	public readonly record struct Report(
		bool TemplateFound, int Items, int Dressed, int Cloned,
		int Folders, int ArrowsUnplaced, int TintWritesDropped, int TintsCleared);

	/// <summary>
	/// Places the template and copies it into every item. Returns what it touched.
	///
	/// Clones are appended to <paramref name="screen"/>'s node array as well as to the parent's
	/// child list, so passes that sweep <c>Nodes</c> rather than walking from the roots see them
	/// — the frame engine among them, since a cloned <c>Frame</c> is a nine-slice needing
	/// layout. Their ordinals stay -1 and the array only grows at the end, so indexing live
	/// guest state by ordinal is unaffected.
	///
	/// The Lua's <c>TintingAmount</c> clear across the frame's nine cells is applied, hoisted to
	/// the parent quad as <see cref="Engine_UiScreen.ApplyLiveTint"/> hoists the live one. Its
	/// <c>diffuse.a</c> = 103 on the highlight is only counted: that is a material colour alpha
	/// and <see cref="UiQuad"/> carries no fourth lane; the wheel animation drives it separately
	/// as <c>highlight_opacity</c>.
	///
	/// Never throws.
	/// </summary>
	/// <param name="rootItem">
	/// The item at the centre of the wheel. Defaults to the authored <c>Root</c>; pass one to
	/// follow a wheel that has re-rooted at runtime.
	/// </param>
	public static Report Apply(Engine_UiScreen screen, FolderMetrics? metrics = null,
		UiNode? rootItem = null)
	{
		if (FindTemplate(screen) is not { } template)
		{
			return default;
		}

		var created = new List<UiNode>();
		var report = new Report(TemplateFound: true, 0, 0, 0, 0, 0, 0, 0);

		report = PlaceTemplateElementsAtDefaultPositions(template, metrics, report);

		// The items are a nested graph under `Ring/Root`, not siblings of the template — the
		// template is a child of the layer parked off-screen (y -1000 on quickmainmenu, -600 on
		// spellsmenu). The nesting is `ParentItem`; `Root` is `g_Menu.RootItem`.
		rootItem ??= FindRootItem(screen);
		if (rootItem is null)
		{
			return report;
		}

		UiNode? templateIcon = FindChild(template, IconName);
		report = WalkItems(rootItem, template, templateIcon, rootItem, created, report);

		if (created.Count > 0)
		{
			var grown = new UiNode[screen.Nodes.Length + created.Count];
			screen.Nodes.CopyTo(grown, 0);
			created.CopyTo(grown, screen.Nodes.Length);
			screen.Nodes = grown;
		}

		return report;
	}

	/// <summary>
	/// Dresses one item, then descends into the items nested under it.
	///
	/// An item is a node with an <c>Icon</c> child. That separates the two kinds of child an
	/// item has: spellsmenu's <c>Root</c> carries eight spells and its own <c>Icon</c>, and the
	/// icon is not an item because its children are <c>Title</c> and <c>Image</c>.
	/// </summary>
	private static Report WalkItems(UiNode item, UiNode template, UiNode? templateIcon,
		UiNode rootItem, List<UiNode> created, Report report)
	{
		report = report with { Items = report.Items + 1 };
		report = CloneInto(item, template, templateIcon, rootItem, created, report);

		// Snapshot: CloneInto has just added children, and a clone must not be walked as an item.
		int count = item.Children.Count;
		for (int i = 0; i < count; ++i)
		{
			UiNode child = item.Children[i];
			if (child.IsClone || FindChild(child, IconName) is null)
			{
				continue;
			}

			report = WalkItems(child, template, templateIcon, rootItem, created, report);
		}

		return report;
	}

	/// <summary>
	/// The wheel's root item, <c>g_Menu.RootItem</c> as the file authors it.
	///
	/// Scoped under the layer rather than matched anywhere in the screen, because <c>Root</c> is
	/// generic enough that another widget could use it.
	/// </summary>
	public static UiNode? FindRootItem(Engine_UiScreen screen)
	{
		foreach (UiNode node in screen.Nodes)
		{
			if (string.Equals(node.Name, LayerName, System.StringComparison.Ordinal) &&
				FindDescendant(node, RootItemName) is { } root &&
				FindChild(root, IconName) is not null)
			{
				return root;
			}
		}

		return null;
	}

	/// <summary>
	/// Copies the template's pieces into one item, in the Lua's order: background, orb and frame
	/// into the Icon, then the beam, then the highlight and arrows onto the item.
	/// </summary>
	private static Report CloneInto(UiNode item, UiNode template, UiNode? templateIcon,
		UiNode rootItem, List<UiNode> created, Report report)
	{
		if (templateIcon is null || FindChild(item, IconName) is not { } destination)
		{
			return report;
		}

		int before = created.Count;

		// Background and orb are gated on the template Icon having an Image; frame and beam are
		// tested separately and not covered by it.
		if (FindChild(templateIcon, ImageName) is not null)
		{
			CloneTo(FindChild(templateIcon, BackgroundName), destination, created);
			CloneTo(FindChild(templateIcon, OrbName), destination, created);
		}

		CloneTo(FindChild(templateIcon, FrameName), destination, created);

		// The beam is the spoke drawn back toward the centre, so the root item does not get one.
		if (!ReferenceEquals(item, rootItem))
		{
			CloneTo(FindChild(templateIcon, BeamName), destination, created);
		}

		CloneTo(FindChild(template, HighlightName), item, created);

		// Arrows are the folder's "more children above / below" pair.
		if (IsAFolder(item))
		{
			report = report with { Folders = report.Folders + 1 };
			CloneTo(FindChild(template, UpArrowsName), item, created);
			CloneTo(FindChild(template, DownArrowsName), item, created);
		}

		int made = created.Count - before;
		return made == 0
			? report
			: report with { Dressed = report.Dressed + 1, Cloned = report.Cloned + made };
	}

	/// <summary>
	/// Moves the template's pieces onto the offsets every copy inherits. The orb's z of -1 puts
	/// it behind the icon art; the frame's x of 5 is the only non-zero offset.
	/// </summary>
	private static Report PlaceTemplateElementsAtDefaultPositions(UiNode template,
		FolderMetrics? metrics, Report report)
	{
		if (FindChild(template, IconName) is not { } icon)
		{
			return report;
		}

		Place(icon, 0f, 0f);
		Place(FindChild(icon, BackgroundName), 0f, 0f);
		Place(FindChild(icon, OrbName), 0f, 0f, -1f);
		Place(FindChild(icon, FrameName), 5f, 0f);
		Place(FindChild(icon, ImageName), 0f, 0f, 0f);

		if (FindChild(template, HighlightName) is { } highlight)
		{
			Place(highlight, 0f, 0f);

			// Two levels down — `menucloning.dis main.2 pc97..107`:
			// `setAttribute(GetTableWithFirstChild(
			//      GetTableWithFirstChild(highlight).element).element, 'diffuse.a', 103)`.
			// Highlight's first child is the quad, whose first child carries the alpha.
			if (FirstChild(FirstChild(highlight)) is not null)
			{
				report = report with { TintWritesDropped = report.TintWritesDropped + 1 };
			}
		}

		// The Lua zeroes `scale.y` alone and leaves x and z at 1; a read-modify-write of a whole
		// scale vector would take the other two axes with it.
		if (FindChild(icon, BeamName) is { } beam)
		{
			Place(beam, 0f, 0f);
			beam.ScaleY = 0f;
		}

		report = PlaceArrows(template, metrics, report);

		// The nine-slice tint clear, which has nowhere to land — see Apply.
		if (FindChild(icon, FrameName) is { } frame)
		{
			report = report with
			{
				TintsCleared = report.TintsCleared + CountFrameTintWrites(frame),
			};
		}

		return report;
	}

	/// <summary>
	/// Places the folder arrows. Both sit at the same x; the down arrow drops by one full page
	/// of children, marking the bottom of the displayed range rather than of the list.
	/// </summary>
	private static Report PlaceArrows(UiNode template, FolderMetrics? metrics, Report report)
	{
		UiNode? up = FindChild(template, UpArrowsName);
		UiNode? down = FindChild(template, DownArrowsName);
		if (up is null && down is null)
		{
			return report;
		}

		if (metrics is not { } m)
		{
			int unplaced = (up is null ? 0 : 1) + (down is null ? 0 : 1);
			return report with { ArrowsUnplaced = report.ArrowsUnplaced + unplaced };
		}

		// 110 and 8 are the Lua's own literals, not derived from the constant tables.
		const float ArrowPosX = 110f;
		const float ArrowOffsetY = 8f;

		float top = m.ItemStartPosY - ArrowOffsetY;
		Place(up, ArrowPosX, top);
		Place(down, ArrowPosX, top - m.HierarchyGap * (m.MaxDisplayedChildren - 1));
		return report;
	}

	/// <summary>
	/// Clears <c>TintingAmount</c> across the frame's nine cells, and reports how many landed.
	///
	/// Each cell is tested down two branches — its first child's first child, and its second
	/// child's first child — and the Lua's <c>hasAttribute</c> means only a descendant carrying
	/// the attribute is written.
	///
	/// The clear is `menucloning.dis main.1 pc87..145`, run on the TEMPLATE before it is copied;
	/// skipping it copies the template's last weight into every item's frame cells. The
	/// attribute sits on a material descendant and the quad is on its PARENT, so the write
	/// hoists as <see cref="Engine_UiScreen.ApplyLiveTint"/> hoists the guest's live one.
	///
	/// The colour is untouched — the Lua writes only the weight, and `rgb -= (1 - tint) * amount`
	/// means a zero weight already neutralises the authored colour.
	/// </summary>
	private static int CountFrameTintWrites(UiNode frame)
	{
		int writes = 0;
		for (int i = 0; i < FrameCells && i < frame.Children.Count; ++i)
		{
			UiNode cell = frame.Children[i];
			writes += TintWritesUnder(FirstChild(cell));
			writes += TintWritesUnder(cell.Children.Count > 1 ? cell.Children[1] : null);
		}

		return writes;
	}

	private static int TintWritesUnder(UiNode? branch)
	{
		if (FirstChild(branch) is not { } target || !HasAttribute(target, "TintingAmount"))
		{
			return 0;
		}

		// The quad is on the parent; the element carrying the attribute never has one.
		UiQuad? quad = target.Quad ?? target.Parent?.Quad;
		if (quad is null)
		{
			return 0;
		}

		quad.TintAmount = 0f;
		return 1;
	}

	private static UiNode? FirstChild(UiNode? node) =>
		node is { Children.Count: > 0 } ? node.Children[0] : null;

	/// <summary>
	/// <c>getAttribute(item.element, 'MenuItemType') == ItemConst.FOLDER</c>.
	///
	/// Read off the file. The wheel's spawn handlers can write <c>MenuItemType</c> at runtime
	/// and the host reads back no general attribute, so an item promoted to a folder at runtime
	/// is not caught here and shows up as missing arrows.
	/// </summary>
	public static bool IsAFolder(UiNode item) =>
		item.Element is not null &&
		item.Element.TryGetAttribute(MenuItemTypeAttribute, out var a) &&
		a.Type is BgfValueType.Int1 or BgfValueType.Int2 &&
		a.AsInt() == FolderItemType;

	private static bool HasAttribute(UiNode node, string name) =>
		node.Element is not null && node.Element.TryGetAttribute(name, out _);

	/// <summary>
	/// The template, found under the presentation's layer as
	/// <c>menustateandconstants.lua</c> scopes it, falling back to a whole-screen match because
	/// 15 of the 52 shipped screens carry no authored names to navigate by.
	/// </summary>
	public static UiNode? FindTemplate(Engine_UiScreen screen)
	{
		foreach (UiNode node in screen.Nodes)
		{
			if (string.Equals(node.Name, LayerName, System.StringComparison.Ordinal) &&
				FindDescendant(node, TemplateName) is { } scoped)
			{
				return scoped;
			}
		}

		foreach (UiNode node in screen.Nodes)
		{
			if (string.Equals(node.Name, TemplateName, System.StringComparison.Ordinal))
			{
				return node;
			}
		}

		return null;
	}

	/// <summary>A direct child by name, which is what <c>getElement(name, parent)</c> resolves.</summary>
	private static UiNode? FindChild(UiNode? parent, string name)
	{
		if (parent is null)
		{
			return null;
		}

		foreach (UiNode child in parent.Children)
		{
			// A clone carries its source's name, so without this a second pass would find the
			// copy made last time.
			if (!child.IsClone &&
				string.Equals(child.Name, name, System.StringComparison.Ordinal))
			{
				return child;
			}
		}

		return null;
	}

	private static UiNode? FindDescendant(UiNode root, string name)
	{
		foreach (UiNode child in root.Children)
		{
			if (string.Equals(child.Name, name, System.StringComparison.Ordinal))
			{
				return child;
			}

			if (FindDescendant(child, name) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private static void Place(UiNode? node, float x, float y)
	{
		if (node is not null)
		{
			node.PositionX = x;
			node.PositionY = y;
		}
	}

	private static void Place(UiNode? node, float x, float y, float z)
	{
		if (node is not null)
		{
			node.PositionX = x;
			node.PositionY = y;
			node.PositionZ = z;
		}
	}

	private static void CloneTo(UiNode? source, UiNode destination, List<UiNode> created)
	{
		if (source is not null)
		{
			CopySubtree(source, destination, created);
		}
	}

	/// <summary>
	/// Deep-copies a subtree under a new parent, as the engine's <c>clone()</c> does.
	///
	/// The quad and text are copied rather than shared so a later per-element write lands on one
	/// item. <see cref="UiNode.Element"/> is shared deliberately — it is read-only here, and
	/// sharing it lets an attribute lookup on a clone answer with the template's value.
	/// </summary>
	private static UiNode CopySubtree(UiNode source, UiNode parent, List<UiNode> created)
	{
		var copy = new UiNode
		{
			Ordinal = -1,
			Name = source.Name,
			HasAuthoredName = source.HasAuthoredName,
			Type = source.Type,
			ObjectId = source.ObjectId,
			Element = source.Element,
			Parent = parent,
			IsClone = true,
			CloneSourceOrdinal = source.Ordinal >= 0 ? source.Ordinal : source.CloneSourceOrdinal,
			PositionX = source.PositionX,
			PositionY = source.PositionY,
			PositionZ = source.PositionZ,
			RotationX = source.RotationX,
			RotationY = source.RotationY,
			RotationZ = source.RotationZ,
			ScaleX = source.ScaleX,
			ScaleY = source.ScaleY,
			ScaleZ = source.ScaleZ,
			PivotX = source.PivotX,
			PivotY = source.PivotY,
			PivotZ = source.PivotZ,
			Opacity = source.Opacity,
			Visible = source.Visible,
		};

		if (source.Quad is { } quad)
		{
			copy.Quad = new UiQuad
			{
				TexturePath = quad.TexturePath,
				TextureHash = quad.TextureHash,
				UMin = quad.UMin, UMax = quad.UMax, VMin = quad.VMin, VMax = quad.VMax,
				R = quad.R, G = quad.G, B = quad.B,
				Source = quad.Source,
			};
		}

		if (source.Text is { } text)
		{
			copy.Text = new UiText
			{
				Tag = text.Tag,
				Live = text.Live,
				Display = text.Display,
				Localised = text.Localised,
				Size = text.Size,
				HorzAlign = text.HorzAlign,
				VertAlign = text.VertAlign,
				WrapMode = text.WrapMode,
				BoxWidth = text.BoxWidth,
				BoxHeight = text.BoxHeight,
				R = text.R, G = text.G, B = text.B,
				Source = text.Source,
			};
		}

		parent.Children.Add(copy);
		created.Add(copy);

		// Snapshot the count so the source list is not re-entered as it grows.
		int count = source.Children.Count;
		for (int i = 0; i < count; ++i)
		{
			CopySubtree(source.Children[i], copy, created);
		}

		return copy;
	}

	/// <summary>
	/// One line describing the run, for the build report. Distinguishes no wheel, no items, and
	/// items found with nothing to copy, which a bare count cannot.
	/// </summary>
	public static string Describe(Report report)
	{
		if (!report.TemplateFound)
		{
			return "no MenuItemTemplate on this screen";
		}

		if (report.Items == 0)
		{
			return $"MenuItemTemplate found but no '{RootItemName}' item under the layer";
		}

		string line = report.Cloned == 0
			? $"{report.Items} item(s) found, nothing cloned — the template carries no pieces"
			: $"{report.Dressed} of {report.Items} item(s) dressed, {report.Cloned} node(s) cloned";

		if (report.Folders > 0)
		{
			line += $", {report.Folders} folder(s)";
		}

		if (report.ArrowsUnplaced > 0)
		{
			line += $", {report.ArrowsUnplaced} arrow(s) left at the authored position " +
				"(no FolderConst supplied)";
		}

		if (report.TintsCleared > 0)
		{
			line += $", {report.TintsCleared} frame tint(s) cleared";
		}

		if (report.TintWritesDropped > 0)
		{
			line += $", {report.TintWritesDropped} tint write(s) not applied";
		}

		return line;
	}
}
