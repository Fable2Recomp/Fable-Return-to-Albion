using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The quick wheel's Lua library, ported: `menu/menulib.lua`, `menuhelpers.lua`, `menucloning.lua`,
/// `menuemphasisandghosting.lua` and `menustateandconstants.lua`, the five chunks' shared `g_Menu`.
/// That is the radial WheelNode/Orb/Beam world; the `expandablemenu/` package's `g_Menu` is the
/// list and is ported elsewhere. Every function is an operation over the Gameface element API, so
/// the port is parameterised on <see cref="IHost"/> as the Lua was.
///
/// Animation shapes are delegated to <see cref="Engine_UiWheelAnimation"/>, constants alias
/// <see cref="Engine_UiWheelConstants"/>, the size tables are <see cref="Engine_UiWheelMetrics"/>'s
/// parameter — `OrbConst`, `FrameConst`, `ExpandConst` and `FolderConst` are read by these chunks
/// and assigned by none of them. Shipped quirks are reproduced, not corrected, and cited at their
/// sites; one lives elsewhere: `AnimateMaximisedToHighlighted` computes a `math.sqrt` and discards it.
/// </summary>
public static class Engine_UiWheelLib
{
	// ===== menustateandconstants.lua =====

	/// <summary>
	/// `g_Menu.ItemConst` — represented by <see cref="Engine_UiWheelConstants.ItemType"/>,
	/// value-for-value: NODE 0, FOLDER 1, EXPRESSION_BUTTON 2, MENU_BUTTON 3,
	/// DOG_COMMAND_BUTTON 4, INVENTORY_ITEM_BUTTON 5, QUEST_ITEM_BUTTON 6, SUGGESTION_BUTTON 7,
	/// SPELLS_BUTTON 8, LEARN_ABILITY_ITEM 20, LEARN_SPELL_ITEM 21, DISCARD_ABILITY_ITEM 22,
	/// DISCARD_SPELL_ITEM 23, BUY_INVENTORY_ITEM 30, SELL_INVENTORY_ITEM 31,
	/// WORLD_MAP_REGION_ITEM 41, WORLD_MAP_QUESTS_ITEM 42, WORLD_MAP_JOBS_ITEM 43,
	/// WORLD_MAP_SALES_ITEM 44, WORLD_MAP_FAMILY_ITEM 45, REGION_STOP_ITEM 50, NAVIGATION 201,
	/// BUTTON_REMOVE 202, BUTTON_SET 203.
	/// </summary>
	public const string MenuItemTypeAttribute = "MenuItemType";

	/// <summary>`g_Menu.Timing.ANIMATE_TIME` = 0.225.</summary>
	public const float AnimateTime = Engine_UiWheelConstants.AnimateTime;

	/// <summary>`g_Menu.Timing.OPACITY_FADE_SPEED` = `1 / ANIMATE_TIME`.</summary>
	public const float OpacityFadeSpeed = Engine_UiWheelConstants.OpacityFadeSpeed;

	/// <summary>`g_Menu.Const.IMAGE_OPACITY_MAX` = 100, also `menulib.lua`'s private upvalue copy.</summary>
	public const float ImageOpacityMax = Engine_UiWheelConstants.ImageOpacityMax;

	public const float ImageOpacityMin = Engine_UiWheelConstants.ImageOpacityMin;

	public const float ImageOpacityDelta = Engine_UiWheelConstants.ImageOpacityDelta;

	/// <summary>`g_Menu.Const.RING_TINT_ALPHA` = 88 — declared, read by none of these chunks.</summary>
	public const float RingTintAlpha = Engine_UiWheelConstants.RingTintAlpha;

	/// <summary>
	/// The title-opacity band — 90 at rest, 100 at the top, delta 10, and NOT the image band.
	/// `menulib.dis main pc4..6`; bound to the six animations that read them at pc16..17, pc20..23,
	/// pc26..29, pc38..45 and pc54..55.
	/// </summary>
	public const float TitleOpacityRest = Engine_UiWheelConstants.TitleOpacityRest;

	public const float TitleOpacityTop = Engine_UiWheelConstants.TitleOpacityTop;

	public const float TitleOpacityDelta = Engine_UiWheelConstants.TitleOpacityDelta;

	/// <summary>
	/// The Rim's and the orb material's diffuse, and the scale every `TintingAmount` write is
	/// multiplied by — `menulib.dis main pc7..11`, bound as `main.12`'s upvalues at pc60..64.
	/// </summary>
	public const float OrbDiffuseR = Engine_UiWheelConstants.OrbDiffuseR;

	public const float OrbDiffuseG = Engine_UiWheelConstants.OrbDiffuseG;

	public const float OrbDiffuseB = Engine_UiWheelConstants.OrbDiffuseB;

	public const float OrbTintScale = Engine_UiWheelConstants.OrbTintScale;

	/// <summary>`STATE_NORM_TO_HIGH` 0 .. `STATE_RESET` 8 — represented by
	/// <see cref="Engine_UiWheelConstants.AnimationState"/>, value-for-value.</summary>
	public const int StateCount = 9;

	/// <summary>
	/// One `g_Menu` item table. `Element` is the Gameface element handle; the rest is script-side.
	/// </summary>
	public sealed class Item
	{
		public object? Element;

		/// <summary>`ParentItem` — the containing item, nil on the root.</summary>
		public Item? ParentItem;

		/// <summary>
		/// `parent` — a second, different link from `ParentItem`, walked only by the two level
		/// transitions' depth loop (`menulib.dis main.4 pc92..101`, `main.5 pc134..143`), which
		/// compares it against `g_Menu.RootItem` itself, not the root's element. No chunk in `menu/`
		/// assigns it.
		/// </summary>
		public Item? Parent;

		/// <summary>`IsOpen` — whether the children draw; level-changing animations read it into `child_opacity`.</summary>
		public bool IsOpen;

		/// <summary>`WasSpawned` — a runtime-spawned item never receives template art.</summary>
		public bool WasSpawned;

		/// <summary>`NumPhysicalItems` — how many children a folder's frame must cover.</summary>
		public int NumPhysicalItems;

		/// <summary>`RecordIDLow` / `RecordIDHigh` — the guest's 64-bit record id, halved.</summary>
		public int RecordIDLow;

		public int RecordIDHigh;

		/// <summary>`Emphasis` / `Ghosting` — the two flags that fight over `textColor`.</summary>
		public bool Emphasis;

		public bool Ghosting;

		/// <summary>`OrbMaterialElement` — the cloned orb's material child, captured at clone time.</summary>
		public object? OrbMaterialElement;
	}

	/// <summary>
	/// `g_Menu.State`. `SpawnedChildren` is read by `GetFirstItemWithName` and owned by the spawn
	/// handler in the package's main chunk.
	/// </summary>
	public sealed class MenuState
	{
		public readonly List<Item> MenuItems = new();

		public readonly List<Item> SpawnedItems = new();

		public readonly Dictionary<Item, List<Item>> SpawnedChildren = new();

		public float TimeElapsed;

		public float TimeSinceNav;

		/// <summary>`getElement("MenuItemTemplate", getElement("Scene.Layer"))`.</summary>
		public object? MenuItemTemplate;

		/// <summary>`g_Menu.RootItem` / `g_Menu.CurrentItem` — set by the package's main chunk,
		/// read here.</summary>
		public Item? RootItem;

		public Item? CurrentItem;
	}

	/// <summary>
	/// `g_Menu.FolderConst` — the full set these chunks read (`MIN_WIDTH`, `MAX_WIDTH`,
	/// `MAX_HEIGHT`, `ITEM_GAP`, `OVERLAP`, `ITEM_START_POS_Y`, `MAX_DISPLAYED_CHILDREN`) and none
	/// assigns; a parameter. <see cref="Engine_UiMenuCloning.FolderMetrics"/> carries three of them.
	/// </summary>
	public readonly record struct FolderMetrics(
		float MinWidth,
		float MaxWidth,
		float MaxHeight,
		float ItemGap,
		float Overlap,
		float ItemStartPosY,
		int MaxDisplayedChildren)
	{
		public static readonly FolderMetrics Unknown = default;
	}

	/// <summary>
	/// The Gameface API surface these chunks call, one member per Lua global. The last five are
	/// defined outside these chunks (`g_Menu:SetTitle`, `SetMinimisedFolderName`,
	/// `SetMaximisedFolderName` in the package's main chunk; `g_Theme:*`;
	/// `GUI.FLAG_WheelGraphIsAtRoot`) and are routed out rather than reimplemented.
	/// </summary>
	public interface IHost
	{
		/// <summary>`getElement(name, parent)` — a direct child by name.</summary>
		object? GetElement(string name, object? parent);

		/// <summary>`GetTableWithChildByIndex(parent, i)` — 1-based, as the Lua calls it.</summary>
		object? GetChildByIndex(object element, int index);

		/// <summary>`GetTableWithFirstChild(parent).element`.</summary>
		object? GetFirstChild(object element);

		bool HasAttribute(object element, string name);

		void SetAttribute(object element, string name, object value);

		string? GetAttributeString(object element, string name);

		float GetAttributeFloat(object element, string name);

		/// <summary>`getAttribute(element, "active")` — the only boolean these chunks read.</summary>
		bool GetAttributeBool(object element, string name);

		/// <summary>`clone(source, newParent)` — deep copy, nil on failure.</summary>
		object? Clone(object source, object newParent);

		/// <summary>`calculateBoundingBox(element, min, max)`.</summary>
		bool CalculateBoundingBox(object element,
			out float minX, out float minY, out float maxX, out float maxY);

		/// <summary>`GUI:GetText(tag)` — the Babel lookup.</summary>
		string GetText(string tag);

		object GetMenuItemDefaultTextColor();

		object GetMenuItemEmphasisedTextColor();

		object GetMenuItemGhostedTextColor();

		void SetTitle(Item item, string title);

		void SetMinimisedFolderName(Item item);

		void SetMaximisedFolderName(Item item);

		/// <summary>`GUI.FLAG_WheelGraphIsAtRoot` — the flag C++ polls.</summary>
		void SetWheelGraphIsAtRoot(bool atRoot);

		/// <summary>`AK.fetchObjects(g_Menu, "KeyLightComponent")` — `menustateandconstants.dis main pc156..160`.</summary>
		void FetchObjects(string componentName);
	}

	/// <summary>
	/// The tail of `menustateandconstants.lua`, in shipped order (`main pc133..169`):
	/// `ItemsToAnimate` and `ItemsToFade` hang off `g_Menu` itself (pc136..141, no `State`
	/// indirection) and so live on <see cref="Engine_UiWheelMenu"/>, while `MenuItems` and
	/// `SpawnedItems` go under `State` (pc143..149); pc156..160 is
	/// `AK.fetchObjects(g_Menu, "KeyLightComponent")`; pc163..169 is
	/// `State.MenuItemTemplate = getElement("MenuItemTemplate", getElement("Scene.Layer"))`.
	/// </summary>
	public static MenuState InitState(IHost host)
	{
		var state = new MenuState();
		host.FetchObjects("KeyLightComponent");
		object? layer = host.GetElement("Scene.Layer", null);
		state.MenuItemTemplate = host.GetElement("MenuItemTemplate", layer);
		return state;
	}

	// ===== menuhelpers.lua =====

	/// <summary>`IsAFolder(item)` — `getAttribute(item.element, "MenuItemType") == ItemConst.FOLDER`, never cached.</summary>
	public static bool IsAFolder(IHost host, Item item) =>
		item.Element is { } el &&
		(int)host.GetAttributeFloat(el, MenuItemTypeAttribute) ==
			(int)Engine_UiWheelConstants.ItemType.Folder;

	/// <summary>
	/// `IsAButton(item)` (`menuhelpers.dis main.5`) — a six-way `or` over `MenuItemType`, in the
	/// order the chunk tests them: EXPRESSION_BUTTON, MENU_BUTTON, DOG_COMMAND_BUTTON,
	/// INVENTORY_ITEM_BUTTON, QUEST_ITEM_BUTTON, SUGGESTION_BUTTON. A folder, a node and every type
	/// above 8 — `SPELLS_BUTTON` and the world map's rows included — answer false.
	/// </summary>
	public static bool IsAButton(int menuItemType) =>
		menuItemType == (int)Engine_UiWheelConstants.ItemType.ExpressionButton ||
		menuItemType == (int)Engine_UiWheelConstants.ItemType.MenuButton ||
		menuItemType == (int)Engine_UiWheelConstants.ItemType.DogCommandButton ||
		menuItemType == (int)Engine_UiWheelConstants.ItemType.InventoryItemButton ||
		menuItemType == (int)Engine_UiWheelConstants.ItemType.QuestItemButton ||
		menuItemType == (int)Engine_UiWheelConstants.ItemType.SuggestionButton;

	/// <summary>`IsAButton(item)` off the element's attribute, re-read every call as the chunk does.</summary>
	public static bool IsAButton(IHost host, Item item) =>
		item.Element is { } el &&
		IsAButton((int)host.GetAttributeFloat(el, MenuItemTypeAttribute));

	/// <summary>`IsContainedInAFolder(item)` — false on the root, else whether the parent is a
	/// folder. Root identity is by ELEMENT, `item.element == g_Menu.RootItem.element`.</summary>
	public static bool IsContainedInAFolder(MenuState state, IHost host, Item item)
	{
		if (state.RootItem is { Element: { } root } && ReferenceEquals(item.Element, root))
		{
			return false;
		}

		return item.ParentItem is { } parent && IsAFolder(host, parent);
	}

	/// <summary>`GetWidestChildWidth(item)` — a shipped stub: `return g_Menu.FolderConst.MIN_WIDTH`,
	/// argument ignored, no child measured.</summary>
	public static float GetWidestChildWidth(FolderMetrics folder, Item? item)
	{
		_ = item;
		return folder.MinWidth;
	}

	/// <summary>
	/// `GetTextWidth(item)` — the Title's rendered width less `FolderConst.OVERLAP`, clamped to
	/// the folder's MIN..MAX band. MAX_WIDTH is the failure answer when the icon is missing.
	/// </summary>
	public static float GetTextWidth(IHost host, FolderMetrics folder, Item item)
	{
		object? icon = item.Element is { } el ? host.GetElement("Icon", el) : null;
		if (icon is null)
		{
			return folder.MaxWidth;
		}

		if (icon is null)
		{
			// unreachable — the shipped chunk states this nil-check twice, verbatim.
			return folder.MaxWidth;
		}

		object? title = host.GetElement("Title", icon);
		if (title is null ||
			!host.CalculateBoundingBox(title, out float minX, out _, out float maxX, out _))
		{
			return folder.MaxWidth;
		}

		float width = (maxX - minX) - folder.Overlap;
		return System.Math.Min(System.Math.Max(width, folder.MinWidth), folder.MaxWidth);
	}

	/// <summary>`GetState()` — the current item's name into `g_Param.String1`, `"Unknown"` when
	/// none; this is how the guest reads the wheel's cursor.</summary>
	public static void GetState(MenuState state, IHost host, object? paramElement)
	{
		if (paramElement is null)
		{
			return;
		}

		if (state.CurrentItem is { Element: { } current })
		{
			host.SetAttribute(paramElement, "String1",
				host.GetAttributeString(current, "name") ?? string.Empty);
		}
		else
		{
			host.SetAttribute(paramElement, "String1", "Unknown");
		}
	}

	/// <summary>
	/// `GetIconElements(item)` — the item's art by name off the `Icon`. Six frame pieces only, the
	/// corners and the horizontal middles; `Left`, `Right` and `Middle` are `ScaleFolder`'s alone.
	/// </summary>
	public readonly record struct IconElements(
		object? Icon,
		object? Beam,
		object? Title,
		object? Frame,
		object? UpperLeft,
		object? UpperMiddle,
		object? UpperRight,
		object? LowerLeft,
		object? LowerMiddle,
		object? LowerRight,
		object? Background,
		object? Orb);

	public static IconElements GetIconElements(IHost host, Item item)
	{
		object? icon = item.Element is { } el ? host.GetElement("Icon", el) : null;
		if (icon is null)
		{
			return default;
		}

		object? frame = host.GetElement("Frame", icon);
		return new IconElements(
			Icon: icon,
			Beam: host.GetElement("Beam", icon),
			Title: host.GetElement("Title", icon),
			Frame: frame,
			UpperLeft: frame is null ? null : host.GetElement("UpperLeft", frame),
			UpperMiddle: frame is null ? null : host.GetElement("UpperMiddle", frame),
			UpperRight: frame is null ? null : host.GetElement("UpperRight", frame),
			LowerLeft: frame is null ? null : host.GetElement("LowerLeft", frame),
			LowerMiddle: frame is null ? null : host.GetElement("LowerMiddle", frame),
			LowerRight: frame is null ? null : host.GetElement("LowerRight", frame),
			Background: host.GetElement("Background", icon),
			Orb: host.GetElement("Orb", icon));
	}

	/// <summary>`ObserveIfAtRoot()` — `GUI.FLAG_WheelGraphIsAtRoot :=
	/// (CurrentItem.element == RootItem.element)`.</summary>
	public static void ObserveIfAtRoot(MenuState state, IHost host)
	{
		bool atRoot = state.CurrentItem is { Element: { } current } &&
			state.RootItem is { Element: { } root } &&
			ReferenceEquals(current, root);
		host.SetWheelGraphIsAtRoot(atRoot);
	}

	/// <summary>
	/// `StoreNameAndTitle(item, id)` — the name attribute, the Title's `TextID` (a table field in
	/// the Lua, an attribute here), and the localised string via `GUI:GetText`.
	/// </summary>
	public static void StoreNameAndTitle(IHost host, Item item, string id)
	{
		if (item.Element is not { } el)
		{
			return;
		}

		host.SetAttribute(el, "name", id);
		object? title = host.GetElement("Icon", el) is { } icon
			? host.GetElement("Title", icon)
			: null;
		if (title is not null)
		{
			host.SetAttribute(title, "TextID", id);
			host.SetAttribute(title, "textstring", host.GetText(id));
		}
	}

	/// <summary>`StoreImageResID(item, u, v)` — the atlas cell: `positionu` / `positionv` on the
	/// Image's first child's first child, the material scroll into the shared sheet.</summary>
	public static void StoreImageResID(IHost host, Item item, float u, float v)
	{
		object? image = item.Element is { } el && host.GetElement("Icon", el) is { } icon
			? host.GetElement("Image", icon)
			: null;
		object? material = image is not null && host.GetFirstChild(image) is { } first
			? host.GetFirstChild(first)
			: null;
		if (material is not null)
		{
			host.SetAttribute(material, "positionu", u);
			host.SetAttribute(material, "positionv", v);
		}
	}

	/// <summary>`StoreMenuItemID(item, low, high)`.</summary>
	public static void StoreMenuItemID(Item item, int low, int high)
	{
		item.RecordIDLow = low;
		item.RecordIDHigh = high;
	}

	/// <summary>
	/// `GetFirstItemWithName(name)` — linear over `State.MenuItems`, then each item's
	/// `State.SpawnedChildren`, first match by the `name` attribute; depth one only.
	/// </summary>
	public static Item? GetFirstItemWithName(MenuState state, IHost host, string name)
	{
		for (int i = 0; i < state.MenuItems.Count; ++i)
		{
			Item item = state.MenuItems[i];
			if (item.Element is { } el && string.Equals(
				host.GetAttributeString(el, "name"), name, System.StringComparison.Ordinal))
			{
				return item;
			}

			if (!state.SpawnedChildren.TryGetValue(item, out List<Item>? children))
			{
				continue;
			}

			for (int j = 0; j < children.Count; ++j)
			{
				if (children[j].Element is { } childEl && string.Equals(
					host.GetAttributeString(childEl, "name"), name,
					System.StringComparison.Ordinal))
				{
					return children[j];
				}
			}
		}

		return null;
	}

	// ===== menulib.lua =====

	/// <summary>`SetRootTitle()` — the root orb's title from `g_Param.String1`, through
	/// `g_Menu:SetTitle` (main chunk).</summary>
	public static void SetRootTitle(MenuState state, IHost host, object paramElement)
	{
		if (state.RootItem is { } root)
		{
			host.SetTitle(root, host.GetAttributeString(paramElement, "String1") ?? string.Empty);
		}
	}

	/// <summary>
	/// How deep an item sits under the root, 0 for a direct child — the loop both level transitions
	/// run before writing the root pivot. `menulib.dis main.4 pc92..101`, identical at
	/// `main.5 pc134..143`:
	/// <code>
	/// pc92..93   walk = item ; depth = 0
	/// pc94..97   while walk['parent'] ~= g_Menu.RootItem do
	/// pc99..101    walk = walk['parent'] ; depth = depth + 1
	/// </code>
	///
	/// The key is `'parent'`, not `'ParentItem'` (see <see cref="Item.Parent"/>), and the test is
	/// against the root ITEM TABLE, not `RootItem.element`. Nothing in `menu/` assigns `parent`; a
	/// null link ends the walk here rather than throwing.
	/// </summary>
	public static int Depth(MenuState state, Item item)
	{
		int depth = 0;
		Item? walk = item;
		while (walk?.Parent is { } parent && !ReferenceEquals(parent, state.RootItem))
		{
			++depth;
			walk = parent;
		}

		return depth;
	}

	/// <summary>
	/// The nine animation states with their side-effects — the field lerps are
	/// <see cref="Engine_UiWheelAnimation"/>'s; this layers the folder renames, `ScaleFolder`, the
	/// parent-Highlight re-park, the arrow snap and the root-pivot writes.
	///
	/// The two root-pivot writes are asymmetric as shipped, in sign and in terms:
	/// `AnimateHighlightedToMaximised` (`menulib.dis main.5 pc150..165`) writes
	/// `depth * HIERARCHY_GAP + HIERARCHY_GAP * t` positive; `AnimateMaximisedToHighlighted`
	/// (`main.4 pc108..128`) forms `HIERARCHY_GAP + depth * HIERARCHY_GAP` — pc108..110 is an
	/// explicit third `ExpandConst` fetch — subtracts `HIERARCHY_GAP * t` (pc116..121) and negates
	/// it (pc122 `UNM`). Both write `pivot.x` 0 (`main.5 pc144..149`, `main.4 pc102..107`) and both
	/// hand `setAttribute` the ITEM TABLE; ported through `RootItem.Element`, the form that can run.
	/// </summary>
	public static Engine_UiWheelConstants.AnimationInfo Animate(
		MenuState state, IHost host, Item item,
		Engine_UiWheelConstants.AnimationState animState, float t,
		Engine_UiWheelMetrics m, FolderMetrics folder,
		float positionX = 0f, float positionY = 0f)
	{
		switch (animState)
		{
			case Engine_UiWheelConstants.AnimationState.NormalToHighlighted:
				return Engine_UiWheelAnimation.NormalToHighlighted(t, m, item.IsOpen);

			case Engine_UiWheelConstants.AnimationState.HighlightedToNormal:
				return Engine_UiWheelAnimation.HighlightedToNormal(t, m, item.IsOpen);

			case Engine_UiWheelConstants.AnimationState.Pressed:
				return Engine_UiWheelAnimation.Pressed(t, m);

			case Engine_UiWheelConstants.AnimationState.HighlightedToMaximised:
			{
				if (IsAFolder(host, item))
				{
					host.SetMaximisedFolderName(item);
				}

				Engine_UiWheelConstants.AnimationInfo info =
					Engine_UiWheelAnimation.HighlightedToMaximised(t, m, positionX, positionY);

				// The parent's Highlight is parked on this item's row, MINUS the item's own
				// pivot — `menulib.dis main.5 pc88..101`:
				//
				//   setAttribute(getElement('Highlight', item.ParentItem.element),
				//                'position.y',
				//                getAttibute(item, 'position.y')
				//                  - getAttribute(item.element, 'pivot.y'))
				//
				// `getAttibute` is misspelt in the shipped chunk (pc88) and so a nil global, and
				// its first argument is the item table rather than `item.element`; the branch
				// raises whenever a parent has a Highlight. Ported as meant — the only form that
				// can run.
				if (item.ParentItem is { Element: { } parentEl } &&
					item.Element is { } el &&
					host.GetElement("Highlight", parentEl) is { } highlight)
				{
					host.SetAttribute(highlight, "position.y",
						host.GetAttributeFloat(el, "position.y") -
							host.GetAttributeFloat(el, "pivot.y"));
				}

				if (IsAFolder(host, item))
				{
					ScaleFolder(host, item, t, folder);

					// `if percent_complete > 0.94999998807907104` — the snap, not a fade.
					info = t > 0.95f
						? info with { ArrowOpacity = 100f, ChildOpacity = 100f }
						: info with { ArrowOpacity = 0f, ChildOpacity = 0f };
				}

				WriteRootPivot(state, host,
					(Depth(state, item) * m.HierarchyGap) + (m.HierarchyGap * t));
				return info;
			}

			case Engine_UiWheelConstants.AnimationState.MaximisedToHighlighted:
			{
				if (IsAFolder(host, item))
				{
					host.SetMinimisedFolderName(item);
				}

				Engine_UiWheelConstants.AnimationInfo info =
					Engine_UiWheelAnimation.MaximisedToHighlighted(t, m, positionX, positionY);

				WriteRootPivot(state, host,
					-((m.HierarchyGap + (Depth(state, item) * m.HierarchyGap)) -
						(m.HierarchyGap * t)));
				return info;
			}

			case Engine_UiWheelConstants.AnimationState.MaximisedToParent:
				return Engine_UiWheelAnimation.MaximisedToParent(t, m, positionX, positionY);

			case Engine_UiWheelConstants.AnimationState.ParentToMaximised:
				return Engine_UiWheelAnimation.ParentToMaximised(t, m, positionX, positionY);

			case Engine_UiWheelConstants.AnimationState.RootIntro:
				return Engine_UiWheelAnimation.RootIntro(t, m);

			default:
			{
				// `AnimateReset` — parked pose, plus the folder side-effects.
				if (IsAFolder(host, item))
				{
					host.SetMinimisedFolderName(item);
					ScaleFolder(host, item, 0f, folder);
				}

				return Engine_UiWheelAnimation.Reset(t, m) with
				{
					TitleOpacity = TitleOpacityRest,
					ChildOpacity = item.IsOpen ? 100f : 0f,
					ManageChildren = true,
				};
			}
		}
	}

	private static void WriteRootPivot(MenuState state, IHost host, float pivotY)
	{
		if (state.RootItem is { Element: { } root })
		{
			host.SetAttribute(root, "pivot.x", 0f);
			host.SetAttribute(root, "pivot.y", pivotY);
		}
	}

	/// <summary>
	/// `ScaleFolder(item, percent)` — stretches the folder's nine-slice frame open around its
	/// children. `menulib.dis main.11`, every term. The function zeroes its own `percent` at pc0
	/// before reading it, so the frame is written at its closed size every frame and only the
	/// animation's `frame_scale` moves.
	///
	/// <code>
	/// pc0        percent = 0
	/// pc1..12    icon = getElement('Icon', item.element)  ; frame = getElement('Frame', icon)
	///            return unless both
	/// pc14..25   width  = GetWidestChildWidth(item.ParentItem) / 100
	///                   + percent * (FolderConst.MAX_WIDTH / 100 - that)
	/// pc34..38   UpperMiddle.scale.x  = width
	/// pc39..43   UpperRight.position.x = width * 100
	/// pc56..69   h = FolderConst.ITEM_GAP * item.NumPhysicalItems + 16
	///            if FolderConst.MAX_HEIGHT &lt; h then h = FolderConst.MAX_HEIGHT
	/// pc70..71   height = percent * (h / 100)
	/// pc72..76   Left.scale.y   = height
	/// pc77..86   Middle.scale.x = width ; Middle.scale.y = height
	/// pc87..91   Right.scale.y  = height
	/// pc92..99   Right.position.x = getAttribute(UpperRight, 'position.x')
	/// pc112..113 lowerY = -height * 100
	/// pc114..123 LowerLeft.position.y = lowerY ; LowerMiddle.position.y = lowerY
	/// pc124..128 LowerMiddle.scale.x = width
	/// pc129..138 LowerRight.position.x = width * 100 ; LowerRight.position.y = lowerY
	/// </code>
	///
	/// Scales are the value over 100, positions the value times 100 (pc42, pc113, pc132): the
	/// frame's authored cell is 100 units. `Right.position.x` is read back off `UpperRight` rather
	/// than recomputed (pc95..98).
	/// </summary>
	public static void ScaleFolder(IHost host, Item item, float percent, FolderMetrics folder)
	{
		// pc0 — the argument is overwritten before it is read. Shipped.
		percent = 0f;

		object? icon = item.Element is { } el ? host.GetElement("Icon", el) : null;
		if (icon is null)
		{
			return;
		}

		object? frame = host.GetElement("Frame", icon);
		if (frame is null)
		{
			return;
		}

		// pc14..18 — `GetWidestChildWidth(item.ParentItem)`, the stub, over 100; a nil parent goes
		// straight through as the shipped call does.
		float widestChild = GetWidestChildWidth(folder, item.ParentItem) / 100f;
		float width = widestChild + (percent * ((folder.MaxWidth / 100f) - widestChild));

		object? upperRight = host.GetElement("UpperRight", frame);
		Set(host, host.GetElement("UpperMiddle", frame), "scale.x", width);
		Set(host, upperRight, "position.x", width * 100f);

		float span = (folder.ItemGap * item.NumPhysicalItems) + 16f;
		if (folder.MaxHeight < span)
		{
			span = folder.MaxHeight;
		}

		float height = percent * (span / 100f);

		object? middle = host.GetElement("Middle", frame);
		object? right = host.GetElement("Right", frame);
		Set(host, host.GetElement("Left", frame), "scale.y", height);
		Set(host, middle, "scale.x", width);
		Set(host, middle, "scale.y", height);
		Set(host, right, "scale.y", height);
		if (right is not null && upperRight is not null)
		{
			host.SetAttribute(right, "position.x",
				host.GetAttributeFloat(upperRight, "position.x"));
		}

		float lowerY = -height * 100f;
		object? lowerMiddle = host.GetElement("LowerMiddle", frame);
		object? lowerRight = host.GetElement("LowerRight", frame);
		Set(host, host.GetElement("LowerLeft", frame), "position.y", lowerY);
		Set(host, lowerMiddle, "position.y", lowerY);
		Set(host, lowerMiddle, "scale.x", width);
		Set(host, lowerRight, "position.x", width * 100f);
		Set(host, lowerRight, "position.y", lowerY);
	}

	private static void Set(IHost host, object? element, string name, float value)
	{
		if (element is not null)
		{
			host.SetAttribute(element, name, value);
		}
	}

	/// <summary>
	/// `MenuApplyAnimInfoToImageAndTitleAndFrameAndOrb(item, info)` — a global in the Lua, not a
	/// `g_Menu` method: writes one <see cref="Engine_UiWheelConstants.AnimationInfo"/> onto an
	/// item's art. `menulib.dis main.12`.
	///
	/// `active` is saved (pc4..7), forced true so the element API answers for the children, and
	/// written back (pc215..219) — an item parked inactive stays inactive. `detail_map_type` is
	/// forced to 0 in the CALLER'S record (pc77) inside the nine-cell loop, before pc78..82 reads
	/// it back for the Rim's `DetailMapType`; hence `ref`. The Image takes `orb_scale * 0.75`
	/// (pc24..31), the Background 0.9 (pc166, pc172), the Orb the full value (pc183, pc188).
	/// The Rim material (pc86..100) and the captured orb material (pc193..207) take diffuse
	/// 255, 153, 0 from `main.12`'s upvalues 0..2; only the Inside is white (pc126, pc131, pc136).
	/// Every `TintingAmount` write is scaled by upvalue 3, `102 / 255` = 0.4.
	/// </summary>
	public static void MenuApplyAnimInfoToImageAndTitleAndFrameAndOrb(
		IHost host, Item item, ref Engine_UiWheelConstants.AnimationInfo info)
	{
		if (item.Element is not { } el)
		{
			return;
		}

		// pc4..12 — save, then force.
		bool wasActive = host.GetAttributeBool(el, "active");
		host.SetAttribute(el, "active", true);

		object? icon = host.GetElement("Icon", el);
		if (icon is not null)
		{
			ApplyToIcon(host, item, icon, ref info);
		}

		// pc215..219 — the saved value goes back, on every exit path the chunk has.
		host.SetAttribute(el, "active", wasActive);
	}

	private static void ApplyToIcon(
		IHost host, Item item, object icon, ref Engine_UiWheelConstants.AnimationInfo info)
	{
		if (host.GetElement("Image", icon) is { } image)
		{
			float imageScale = info.OrbScale * 0.75f;
			host.SetAttribute(image, "scale.x", imageScale);
			host.SetAttribute(image, "scale.y", imageScale);
			host.SetAttribute(image, "opacity", info.ImageOpacity);
		}

		if (host.GetElement("Title", icon) is { } title)
		{
			host.SetAttribute(title, "opacity", info.TitleOpacity);
		}

		if (host.GetElement("Frame", icon) is { } frame)
		{
			host.SetAttribute(frame, "opacity", info.FolderOpacity);
			for (int i = 1; i <= 9; ++i)
			{
				object? cell = host.GetChildByIndex(frame, i);
				if (cell is null)
				{
					continue;
				}

				if (host.GetElement("Rim", cell) is { } rim)
				{
					// pc77 — the caller's record is overwritten, then read back at pc81.
					info = info with { DetailMapType = 0 };
					host.SetAttribute(rim, "DetailMapType", info.DetailMapType);
					if (host.GetFirstChild(rim) is { } rimMaterial)
					{
						host.SetAttribute(rimMaterial, "diffuse.r", OrbDiffuseR);
						host.SetAttribute(rimMaterial, "diffuse.g", OrbDiffuseG);
						host.SetAttribute(rimMaterial, "diffuse.b", OrbDiffuseB);
						if (host.HasAttribute(rimMaterial, "TintingAmount"))
						{
							host.SetAttribute(rimMaterial, "TintingAmount",
								info.OrbTint * OrbTintScale);
						}
					}
				}

				if (host.GetElement("Inside", cell) is { } inside &&
					host.GetFirstChild(inside) is { } insideMaterial)
				{
					host.SetAttribute(insideMaterial, "diffuse.r", 255f);
					host.SetAttribute(insideMaterial, "diffuse.g", 255f);
					host.SetAttribute(insideMaterial, "diffuse.b", 255f);
					if (host.HasAttribute(insideMaterial, "TintingAmount"))
					{
						host.SetAttribute(insideMaterial, "TintingAmount",
							info.InsideTint * OrbTintScale);
					}
				}
			}

			MenuApplyScaleToFrame(host, frame, info.FrameScale);
		}

		if (host.GetElement("Background", icon) is { } background)
		{
			// `* 0.89999997615814209` — the background sits just inside the orb.
			float backgroundScale = info.OrbScale * 0.9f;
			host.SetAttribute(background, "scale.x", backgroundScale);
			host.SetAttribute(background, "scale.y", backgroundScale);
		}

		if (host.GetElement("Orb", icon) is { } orb)
		{
			host.SetAttribute(orb, "scale.x", info.OrbScale);
			host.SetAttribute(orb, "scale.y", info.OrbScale);
			if (item.OrbMaterialElement is { } orbMaterial)
			{
				host.SetAttribute(orbMaterial, "diffuse.r", OrbDiffuseR);
				host.SetAttribute(orbMaterial, "diffuse.g", OrbDiffuseG);
				host.SetAttribute(orbMaterial, "diffuse.b", OrbDiffuseB);
				host.SetAttribute(orbMaterial, "TintingAmount", info.OrbTint * OrbTintScale);
			}
		}
	}

	/// <summary>
	/// `MenuApplyScaleToFrame(frame, s)` — a global in the Lua. Corners scale on both axes, the
	/// horizontal middles on `y` alone and the vertical sides on `x` alone: each edge piece's
	/// stretched axis belongs to the nine-slice layout. `Middle` is not touched.
	/// </summary>
	public static void MenuApplyScaleToFrame(IHost host, object frame, float scale)
	{
		if (host.GetElement("UpperLeft", frame) is { } upperLeft)
		{
			host.SetAttribute(upperLeft, "scale.x", scale);
			host.SetAttribute(upperLeft, "scale.y", scale);
		}

		if (host.GetElement("UpperMiddle", frame) is { } upperMiddle)
		{
			host.SetAttribute(upperMiddle, "scale.y", scale);
		}

		if (host.GetElement("UpperRight", frame) is { } upperRight)
		{
			host.SetAttribute(upperRight, "scale.x", scale);
			host.SetAttribute(upperRight, "scale.y", scale);
		}

		if (host.GetElement("Left", frame) is { } left)
		{
			host.SetAttribute(left, "scale.x", scale);
		}

		if (host.GetElement("Right", frame) is { } right)
		{
			host.SetAttribute(right, "scale.x", scale);
		}

		if (host.GetElement("LowerLeft", frame) is { } lowerLeft)
		{
			host.SetAttribute(lowerLeft, "scale.x", scale);
			host.SetAttribute(lowerLeft, "scale.y", scale);
		}

		if (host.GetElement("LowerMiddle", frame) is { } lowerMiddle)
		{
			host.SetAttribute(lowerMiddle, "scale.y", scale);
		}

		if (host.GetElement("LowerRight", frame) is { } lowerRight)
		{
			host.SetAttribute(lowerRight, "scale.x", scale);
			host.SetAttribute(lowerRight, "scale.y", scale);
		}
	}

	// ===== menucloning.lua =====

	/// <summary>`CloneAllMenuItemElementsFromTemplate()` — every item in `State.MenuItems`.</summary>
	public static void CloneAllMenuItemElementsFromTemplate(MenuState state, IHost host)
	{
		for (int i = 0; i < state.MenuItems.Count; ++i)
		{
			CloneMenuItemElementsFromTemplate(state, host, state.MenuItems[i]);
		}
	}

	/// <summary>
	/// `CloneMenuItemElementsFromTemplate(item)` — dresses one item from `MenuItemTemplate`, in the
	/// shipped order: a spawned item and the template itself are skipped; the template's `Icon`,
	/// `Background` and `Orb` are required (assert-and-return), `Frame`, `Beam`, `Highlight`,
	/// `UpArrows`, `DownArrows` optional; the TEMPLATE's tints are cleared — the orb's first child
	/// and, per nine-slice cell, the first child's first child and the second child's first child —
	/// on every call, once per item dressed; then, gated on the template icon having an `Image`,
	/// `Background` and `Orb` are cloned into the item's `Icon` and the orb clone's first child is
	/// captured as <see cref="Item.OrbMaterialElement"/> (one clone only,
	/// `menucloning.dis main.1 pc177..191`); `Frame` into the icon; `Beam` into the icon for every
	/// item but the root; `Highlight` onto the ITEM, one level above the icon; and a folder also
	/// receives `UpArrows` and `DownArrows` on the item. The element-API form of
	/// <see cref="Engine_UiMenuCloning"/>'s UiNode-tree pass.
	/// </summary>
	public static void CloneMenuItemElementsFromTemplate(MenuState state, IHost host, Item item)
	{
		if (item.WasSpawned || item.Element is not { } el)
		{
			return;
		}

		if (state.MenuItemTemplate is not { } template)
		{
			// `assert("Failed to find MenuItemTemplate")` — a message, not a check, then return.
			return;
		}

		if (ReferenceEquals(template, el))
		{
			return;
		}

		object? fromIcon = host.GetElement("Icon", template);
		if (fromIcon is null)
		{
			return;
		}

		object? fromBackground = host.GetElement("Background", fromIcon);
		if (fromBackground is null)
		{
			return;
		}

		object? fromOrb = host.GetElement("Orb", fromIcon);
		if (fromOrb is null)
		{
			return;
		}

		object? fromFrame = host.GetElement("Frame", fromIcon);
		object? fromBeam = host.GetElement("Beam", fromIcon);
		object? fromHighlight = host.GetElement("Highlight", template);
		object? fromUpArrows = host.GetElement("UpArrows", template);
		object? fromDownArrows = host.GetElement("DownArrows", template);

		if (host.GetChildByIndex(fromOrb, 1) is { } orbChild &&
			host.HasAttribute(orbChild, "TintingAmount"))
		{
			host.SetAttribute(orbChild, "TintingAmount", 0f);
		}

		if (fromFrame is not null)
		{
			for (int i = 1; i <= 9; ++i)
			{
				object? cell = host.GetChildByIndex(fromFrame, i);
				if (cell is null)
				{
					continue;
				}

				if (host.GetFirstChild(cell) is { } first &&
					host.GetFirstChild(first) is { } firstMaterial &&
					host.HasAttribute(firstMaterial, "TintingAmount"))
				{
					host.SetAttribute(firstMaterial, "TintingAmount", 0f);
				}

				if (host.GetChildByIndex(cell, 2) is { } second &&
					host.GetFirstChild(second) is { } secondMaterial &&
					host.HasAttribute(secondMaterial, "TintingAmount"))
				{
					host.SetAttribute(secondMaterial, "TintingAmount", 0f);
				}
			}
		}

		object? toIcon = host.GetElement("Icon", el);
		if (toIcon is null)
		{
			return;
		}

		if (host.GetElement("Image", fromIcon) is not null)
		{
			if (host.Clone(fromBackground, toIcon) is null)
			{
				return;
			}

			object? orbClone = host.Clone(fromOrb, toIcon);
			if (orbClone is null)
			{
				return;
			}

			item.OrbMaterialElement = host.GetFirstChild(orbClone);
		}

		if (fromFrame is not null && host.Clone(fromFrame, toIcon) is null)
		{
			return;
		}

		bool isRoot = state.RootItem is { Element: { } root } && ReferenceEquals(el, root);
		if (!isRoot && fromBeam is not null)
		{
			host.Clone(fromBeam, toIcon);
		}

		if (fromHighlight is not null)
		{
			host.Clone(fromHighlight, el);
		}

		if (IsAFolder(host, item))
		{
			if (fromUpArrows is not null)
			{
				host.Clone(fromUpArrows, el);
			}

			if (fromDownArrows is not null)
			{
				host.Clone(fromDownArrows, el);
			}
		}
	}

	/// <summary>
	/// `PlaceTemplateElementsAtDefaultPositions()` — moves the template's pieces onto the
	/// offsets every copy inherits, EXACTLY:
	///
	/// <code>
	/// Icon        (0, 0)          Background  (0, 0)
	/// Orb         (0, 0, -1)      Frame       (5, 0)
	/// Image       (0, 0, 0)       Highlight   (0, 0), first child diffuse.a = 103
	/// UpArrows    (110, ITEM_START_POS_Y - 8)
	/// DownArrows  (110, (ITEM_START_POS_Y - 8) - HIERARCHY_GAP * (MAX_DISPLAYED_CHILDREN - 1))
	/// Beam        (0, 0), scale.y = 0
	/// </code>
	///
	/// The orb's z of -1 puts it behind the icon art; the beam is parked flat until an animation
	/// raises it. The arrows' x of 110 and offset of 8 come from the bytecode constant pool (the
	/// decompile mangles the x register). A missing `Image` aborts after the frame is placed.
	/// </summary>
	public static void PlaceTemplateElementsAtDefaultPositions(
		MenuState state, IHost host, Engine_UiWheelMetrics m, FolderMetrics folder)
	{
		if (state.MenuItemTemplate is not { } template)
		{
			return;
		}

		var templateItem = new Item { Element = template };
		IconElements parts = GetIconElements(host, templateItem);
		if (parts.Icon is null)
		{
			return;
		}

		Set(host, parts.Icon, "position.x", 0f);
		Set(host, parts.Icon, "position.y", 0f);
		Set(host, parts.Background, "position.x", 0f);
		Set(host, parts.Background, "position.y", 0f);
		Set(host, parts.Orb, "position.x", 0f);
		Set(host, parts.Orb, "position.y", 0f);
		Set(host, parts.Orb, "position.z", -1f);
		Set(host, parts.Frame, "position.x", 5f);
		Set(host, parts.Frame, "position.y", 0f);

		object? image = host.GetElement("Image", parts.Icon);
		if (image is null)
		{
			// `assert("Failed to find the 'from' WheelNode's Image")`, then return — the
			// highlight, arrows and beam below are NOT placed on a template with no image.
			return;
		}

		host.SetAttribute(image, "position.x", 0f);
		host.SetAttribute(image, "position.y", 0f);
		host.SetAttribute(image, "position.z", 0f);

		if (host.GetElement("Highlight", template) is { } highlight)
		{
			host.SetAttribute(highlight, "position.x", 0f);
			host.SetAttribute(highlight, "position.y", 0f);

			// Two levels down, not one (`menucloning.dis main.2 pc97..107` nests the call):
			// the Highlight's first child is the quad, whose first child carries the alpha.
			if (host.GetFirstChild(highlight) is { } highlightQuad &&
				host.GetFirstChild(highlightQuad) is { } highlightMaterial)
			{
				host.SetAttribute(highlightMaterial, "diffuse.a", 103f);
			}
		}

		float arrowY = folder.ItemStartPosY - 8f;
		if (host.GetElement("UpArrows", template) is { } upArrows)
		{
			host.SetAttribute(upArrows, "position.x", 110f);
			host.SetAttribute(upArrows, "position.y", arrowY);
		}

		if (host.GetElement("DownArrows", template) is { } downArrows)
		{
			host.SetAttribute(downArrows, "position.x", 110f);
			host.SetAttribute(downArrows, "position.y",
				arrowY - (m.HierarchyGap * (folder.MaxDisplayedChildren - 1)));
		}

		if (host.GetElement("Beam", parts.Icon) is { } beam)
		{
			host.SetAttribute(beam, "position.x", 0f);
			host.SetAttribute(beam, "position.y", 0f);
			host.SetAttribute(beam, "scale.y", 0f);
		}
	}

	// ===== menuemphasisandghosting.lua =====

	/// <summary>
	/// `SetItemEmphasis()` — off `g_Param`: `String1` names the item, `Float1` is the switch, 0
	/// clearing to the default colour. Only the Title's `textColor` changes — no scale, no tint.
	/// </summary>
	public static void SetItemEmphasis(MenuState state, IHost host, object paramElement)
	{
		float value = host.GetAttributeFloat(paramElement, "Float1");
		Item? item = GetFirstItemWithName(state, host,
			host.GetAttributeString(paramElement, "String1") ?? string.Empty);
		if (item?.Element is not { } el)
		{
			return;
		}

		object? title = host.GetElement("Icon", el) is { } icon
			? host.GetElement("Title", icon)
			: null;
		if (value == 0f)
		{
			item.Emphasis = false;
			if (title is not null)
			{
				host.SetAttribute(title, "textColor", host.GetMenuItemDefaultTextColor());
			}
		}
		else
		{
			item.Emphasis = true;
			if (title is not null)
			{
				host.SetAttribute(title, "textColor", host.GetMenuItemEmphasisedTextColor());
			}
		}
	}

	/// <summary>
	/// `SetItemGhosting()` — <see cref="SetItemEmphasis"/>'s shape with the ghosted colour. Both
	/// write the same `textColor` channel and neither consults the other's flag, so clearing
	/// ghosting restores DEFAULT on an item still flagged emphasised; last writer wins.
	/// </summary>
	public static void SetItemGhosting(MenuState state, IHost host, object paramElement)
	{
		float value = host.GetAttributeFloat(paramElement, "Float1");
		Item? item = GetFirstItemWithName(state, host,
			host.GetAttributeString(paramElement, "String1") ?? string.Empty);
		if (item?.Element is not { } el)
		{
			return;
		}

		object? title = host.GetElement("Icon", el) is { } icon
			? host.GetElement("Title", icon)
			: null;
		if (value == 0f)
		{
			item.Ghosting = false;
			if (title is not null)
			{
				host.SetAttribute(title, "textColor", host.GetMenuItemDefaultTextColor());
			}
		}
		else
		{
			item.Ghosting = true;
			if (title is not null)
			{
				host.SetAttribute(title, "textColor", host.GetMenuItemGhostedTextColor());
			}
		}
	}
}
