#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// Unified expandable GDB node hierarchy tree and component stack inspector for the Modding Studio Items tab.
/// Rooted at <c>0x46F79080</c> (World Entity Root), allowing inspection and editing of any node's directly
/// attached components, walked-up inherited components, 1-click component overrides, custom component attachment,
/// and branching new child nodes from any point in the tree.
/// </summary>
public partial class PolymorphicItemGraphView : MarginContainer
{
    private PolymorphicItemGraph _graph = null!;

    // Left Pane — Search, Filter & Hierarchy Tree Controls:
    private LineEdit? _searchInput;
    private OptionButton? _domainDropdown;
    private CheckBox? _modifiedOnlyCheck;
    private Tree? _hierarchyTree;
    private Label? _treeSummaryLabel;
    private readonly Dictionary<uint, TreeItem> _treeItemsByHash = new();
    private bool _suppressTreeSelectionEvent;

    // Right Pane — Node Header & Component Stack Inspector Controls:
    private HBoxContainer? _breadcrumbBar;
    private PanelContainer? _inspectorIconBox;
    private TextureRect? _inspectorIconRect;
    private Label? _inspectorTitle;
    private Label? _inspectorSubtitle;
    private Label? _inspectorDesc;
    private Button? _inspect3DBtn;
    private Button? _placeInSceneBtn;
    private Button? _createChildNodeBtn;
    private Button? _deleteCustomNodeBtn;
    private Button? _openFullEditorBtn;
    private VBoxContainer? _componentStackContainer;
    private OptionButton? _attachComponentDropdown;
    private LineEdit? _customComponentNameInput;
    private Button? _attachComponentBtn;
    private Button? _saveModBtn;
    private Button? _revertPropsBtn;
    private Label? _saveStatusLabel;

    // Asset File Dialog & Expanded Surface Material Panels state:
    private FileDialog? _assetFileDialog;
    private Action<string>? _pendingAssetFileCallback;
    private readonly HashSet<(uint CompHash, uint PropHash)> _expandedMaterialPanels = new();

    // Create Child Node Dialog:
    private ConfirmationDialog? _createNodeDialog;
    private Label? _createParentInfoLabel;
    private LineEdit? _createDisplayNameInput;
    private LineEdit? _createRecordNameInput;
    private LineEdit? _createDescriptionInput;
    private Label? _createDialogErrorLabel;

    // Add Custom Property Dialog:
    private ConfirmationDialog? _addPropertyDialog;
    private uint _addPropertyTargetCompHash;
    private Label? _addPropertyTargetLabel;
    private LineEdit? _addPropertyNameInput;
    private OptionButton? _addPropertyTypeDropdown;
    private LineEdit? _addPropertyValueInput;
    private Label? _addPropertyErrorLabel;

    // Modal Loading / Progress Dialog & GMD Socket Inspector Dialog:
    private Window? _loadingDialog;
    private Label? _loadingTitleLabel;
    private Label? _loadingDetailLabel;
    private AcceptDialog? _gmdSocketsDialog;
    private VBoxContainer? _gmdSocketsContentBox;

    // Cache for loaded item icon textures:
    private readonly Dictionary<string, Texture2D?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Key, string Label)[] DomainOptions =
    {
        ("All", "All GDB Entities"),
        ("Weapons", "Weapons"),
        ("Clothing", "Clothing & Armor"),
        ("Potions", "Potions & Consumables"),
        ("Augments", "Augments"),
        ("Gifts", "Gifts, Trophies & Titles"),
        ("Books", "Books & Documents"),
        ("Furniture & Props", "Furniture, Chests & Props"),
        ("Creatures & NPCs", "Creatures, NPCs & Villagers"),
        ("Buildings & Shops", "Buildings, Houses & Shops"),
        ("Loot & Generators", "Loot Tables & Generators"),
        ("Visual FX & Audio", "Visual FX & Sound Emitters"),
        ("World Markers", "World Markers & Triggers"),
    };

    public PolymorphicItemGraphView()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_top", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_bottom", 8);

        BuildGraphModel();
        BuildLayout();
        BuildCreateNodeDialog();
        BuildAddPropertyDialog();

        PopulateTree();
        UpdateInspector();
    }

    /// <summary>
    /// Rebuilds the GDB hierarchy graph from the active mod and refreshes both the tree and component inspector.
    /// </summary>
    public void RefreshGraph()
    {
        uint selectedHash = _graph?.SelectedNode?.RecordHash ?? Engine_RecordNameCatalog.WorldEntityRootHash;
        ReloadGraphAndReselect(selectedHash);
    }

    /// <summary>
    /// Selects a top-level GDB domain filter programmatically (e.g. from Modding Studio navigation).
    /// </summary>
    public void SelectDomain(string domainKey)
    {
        int foundIdx = 0;
        for (int i = 0; i < DomainOptions.Length; i++)
        {
            if (DomainOptions[i].Key.Equals(domainKey, StringComparison.OrdinalIgnoreCase))
            {
                foundIdx = i;
                break;
            }
        }

        if (_domainDropdown is not null)
        {
            _domainDropdown.Selected = foundIdx;
        }

        _graph.SetDomainFilter(DomainOptions[foundIdx].Key);
        PopulateTree();
        UpdateInspector();
    }

    /// <summary>
    /// Selects a specific GDB node by record hash (switching domain filter to All if needed).
    /// </summary>
    public void SelectNodeByHash(uint recordHash)
    {
        if (recordHash == 0) return;
        ReloadGraphAndReselect(recordHash);
        ItemGraphNode? found = _graph.FindNodeByHash(recordHash);
        if (found is not null)
        {
            if (!PolymorphicItemGraph.MatchesDomain(found, _graph.DomainFilter))
            {
                SelectDomain("All");
            }
            _graph.SelectNode(found);
            PopulateTree();
            UpdateInspector();
        }
    }

    private void BuildGraphModel()
    {
        if (_graph is not null)
        {
            _graph.GraphNavigated -= OnGraphNavigated;
        }

        GdbMulti? multi = EditorModValueService.GetEffectiveBaseDatabase();
        _graph = PolymorphicItemGraph.Build(multi);
        _graph.GraphNavigated += OnGraphNavigated;
    }

    public override void _ExitTree()
    {
        if (_graph is not null)
        {
            _graph.GraphNavigated -= OnGraphNavigated;
        }
        base._ExitTree();
    }

    private void BuildLayout()
    {
        var split = new HSplitContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SplitOffsets = new[] { 390 }
        };
        split.AddThemeConstantOverride("separation", 10);
        AddChild(split);

        var leftPane = BuildTreePane();
        split.AddChild(leftPane);

        var rightPane = BuildInspectorPane();
        split.AddChild(rightPane);
    }

    private Control BuildTreePane()
    {
        var panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(320, 0),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.11f, 0.12f, 0.15f),
            BorderColor = new Color(0.22f, 0.25f, 0.30f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 6);

        var headerRow = new HBoxContainer();
        headerRow.AddThemeConstantOverride("separation", 6);

        var headerLabel = new Label
        {
            Text = "GDB HIERARCHY TREE",
            Modulate = new Color(0.65f, 0.72f, 0.85f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        headerLabel.AddThemeFontSizeOverride("font_size", 11);
        headerRow.AddChild(headerLabel);

        var expandTopBtn = new Button
        {
            Text = "Expand Branches",
            TooltipText = "Expand main category branches"
        };
        expandTopBtn.AddThemeFontSizeOverride("font_size", 10);
        expandTopBtn.Pressed += () => SetTreeCollapsedRecursive(_hierarchyTree?.GetRoot(), false, maxDepth: 2);
        headerRow.AddChild(expandTopBtn);

        var collapseBtn = new Button
        {
            Text = "Collapse All",
            TooltipText = "Collapse all branches back to the top root"
        };
        collapseBtn.AddThemeFontSizeOverride("font_size", 10);
        collapseBtn.Pressed += () => SetTreeCollapsedRecursive(_hierarchyTree?.GetRoot(), true, maxDepth: 1);
        headerRow.AddChild(collapseBtn);

        vbox.AddChild(headerRow);

        // Search and Filter Controls:
        _searchInput = new LineEdit
        {
            PlaceholderText = "Search by name, internal ID, or 0xHash...",
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _searchInput.AddThemeFontSizeOverride("font_size", 12);
        _searchInput.TextChanged += _ => PopulateTree();
        vbox.AddChild(_searchInput);

        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 8);

        var domainLabel = new Label
        {
            Text = "Domain:",
            Modulate = new Color(0.7f, 0.75f, 0.82f)
        };
        domainLabel.AddThemeFontSizeOverride("font_size", 11);
        filterRow.AddChild(domainLabel);

        _domainDropdown = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _domainDropdown.AddThemeFontSizeOverride("font_size", 11);
        for (int i = 0; i < DomainOptions.Length; i++)
        {
            _domainDropdown.AddItem(DomainOptions[i].Label, i);
        }
        _domainDropdown.ItemSelected += idx =>
        {
            if (idx >= 0 && idx < DomainOptions.Length)
            {
                _graph.SetDomainFilter(DomainOptions[idx].Key);
                PopulateTree();
            }
        };
        filterRow.AddChild(_domainDropdown);

        _modifiedOnlyCheck = new CheckBox
        {
            Text = "Modified Only",
            TooltipText = "Show only custom or mod-overridden GDB nodes"
        };
        _modifiedOnlyCheck.AddThemeFontSizeOverride("font_size", 11);
        _modifiedOnlyCheck.Toggled += _ => PopulateTree();
        filterRow.AddChild(_modifiedOnlyCheck);

        vbox.AddChild(filterRow);

        // Expandable Tree Control:
        _hierarchyTree = new Tree
        {
            Columns = 2,
            ColumnTitlesVisible = false,
            HideRoot = false,
            SelectMode = Tree.SelectModeEnum.Single,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _hierarchyTree.SetColumnExpand(0, true);
        _hierarchyTree.SetColumnExpandRatio(0, 3);
        _hierarchyTree.SetColumnExpand(1, false);
        _hierarchyTree.SetColumnCustomMinimumWidth(1, 105);
        _hierarchyTree.ItemSelected += OnTreeItemSelected;
        vbox.AddChild(_hierarchyTree);

        _treeSummaryLabel = new Label
        {
            Text = "",
            Modulate = new Color(0.55f, 0.60f, 0.70f)
        };
        _treeSummaryLabel.AddThemeFontSizeOverride("font_size", 10);
        vbox.AddChild(_treeSummaryLabel);

        panel.AddChild(vbox);
        return panel;
    }

    private Control BuildInspectorPane()
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.14f, 0.18f),
            BorderColor = new Color(0.22f, 0.25f, 0.30f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        vbox.AddThemeConstantOverride("separation", 6);

        // 1. Ancestry Breadcrumb Bar:
        var breadcrumbScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 24)
        };
        _breadcrumbBar = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _breadcrumbBar.AddThemeConstantOverride("separation", 4);
        breadcrumbScroll.AddChild(_breadcrumbBar);
        vbox.AddChild(breadcrumbScroll);

        vbox.AddChild(new HSeparator());

        // 2. Selected Node Header & Actions:
        var headerHBox = new HBoxContainer();
        headerHBox.AddThemeConstantOverride("separation", 10);

        _inspectorIconBox = new PanelContainer
        {
            CustomMinimumSize = new Vector2(40, 40),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            Visible = false
        };
        var iconBoxStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.10f, 0.13f),
            BorderColor = new Color(0.25f, 0.28f, 0.35f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4
        };
        _inspectorIconBox.AddThemeStyleboxOverride("panel", iconBoxStyle);

        _inspectorIconRect = new TextureRect
        {
            CustomMinimumSize = new Vector2(34, 34),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        _inspectorIconBox.AddChild(_inspectorIconRect);
        headerHBox.AddChild(_inspectorIconBox);

        var titleVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        titleVBox.AddThemeConstantOverride("separation", 2);

        _inspectorTitle = new Label
        {
            Text = "World Entity Root",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _inspectorTitle.AddThemeFontSizeOverride("font_size", 15);
        titleVBox.AddChild(_inspectorTitle);

        _inspectorSubtitle = new Label
        {
            Text = "Record: 0x46F79080",
            Modulate = new Color(0.65f, 0.72f, 0.82f),
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        _inspectorSubtitle.AddThemeFontSizeOverride("font_size", 11);
        titleVBox.AddChild(_inspectorSubtitle);

        headerHBox.AddChild(titleVBox);

        var headerActions = new HBoxContainer
        {
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        headerActions.AddThemeConstantOverride("separation", 6);

        _inspect3DBtn = new Button
        {
            Text = "🔍 Inspect in 3D",
            TooltipText = "Open or update this GDB record's 3D model in the Godot 3D viewport",
            Visible = false,
            Modulate = new Color(0.65f, 0.92f, 1.0f)
        };
        _inspect3DBtn.AddThemeFontSizeOverride("font_size", 11);
        _inspect3DBtn.Pressed += OnInspectIn3DPressed;
        headerActions.AddChild(_inspect3DBtn);

        _placeInSceneBtn = new Button
        {
            Text = "📍 Place in 3D Scene",
            TooltipText = "Spawn a 1:1 instance of this GDB record into the active 3D LevelScene under the current target .save layer",
            Visible = false,
            Modulate = new Color(0.55f, 1.0f, 0.72f)
        };
        _placeInSceneBtn.AddThemeFontSizeOverride("font_size", 11);
        _placeInSceneBtn.Pressed += OnPlaceIn3DScenePressed;
        headerActions.AddChild(_placeInSceneBtn);

        _createChildNodeBtn = new Button
        {
            Text = "+ Create Child Node",
            TooltipText = "Create a new child GDB node inheriting all components from this node"
        };
        _createChildNodeBtn.AddThemeFontSizeOverride("font_size", 11);
        _createChildNodeBtn.Pressed += OnCreateChildNodePressed;
        headerActions.AddChild(_createChildNodeBtn);

        _deleteCustomNodeBtn = new Button
        {
            Text = "✕ Delete Custom Node",
            TooltipText = "Delete this mod-created custom node and its custom component records from the active mod",
            Visible = false,
            Modulate = new Color(1.0f, 0.65f, 0.65f)
        };
        _deleteCustomNodeBtn.AddThemeFontSizeOverride("font_size", 11);
        _deleteCustomNodeBtn.Pressed += OnDeleteCustomNodePressed;
        headerActions.AddChild(_deleteCustomNodeBtn);

        _openFullEditorBtn = new Button
        {
            Text = "Open Full Item Editor",
            TooltipText = "Open the full item editor dialog for this item/weapon",
            Visible = false
        };
        _openFullEditorBtn.AddThemeFontSizeOverride("font_size", 11);
        _openFullEditorBtn.Pressed += OnOpenFullEditorPressed;
        headerActions.AddChild(_openFullEditorBtn);

        headerHBox.AddChild(headerActions);
        vbox.AddChild(headerHBox);

        _inspectorDesc = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.72f, 0.75f, 0.80f)
        };
        _inspectorDesc.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_inspectorDesc);

        // 3. Attach Component Toolbar:
        var attachBar = new HBoxContainer();
        attachBar.AddThemeConstantOverride("separation", 6);

        var attachLabel = new Label
        {
            Text = "Attach Component:",
            Modulate = new Color(0.75f, 0.80f, 0.88f)
        };
        attachLabel.AddThemeFontSizeOverride("font_size", 11);
        attachBar.AddChild(attachLabel);

        _attachComponentDropdown = new OptionButton
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _attachComponentDropdown.AddThemeFontSizeOverride("font_size", 11);
        _attachComponentDropdown.ItemSelected += OnAttachDropdownSelected;
        attachBar.AddChild(_attachComponentDropdown);

        _customComponentNameInput = new LineEdit
        {
            PlaceholderText = "CustomSlotName (e.g. MyLuaComponent)",
            CustomMinimumSize = new Vector2(200, 0),
            Visible = false
        };
        _customComponentNameInput.AddThemeFontSizeOverride("font_size", 11);
        _customComponentNameInput.TextSubmitted += _ => OnAttachComponentPressed();
        attachBar.AddChild(_customComponentNameInput);

        _attachComponentBtn = new Button
        {
            Text = "+ Attach to Node",
            TooltipText = "Attach the selected component slot directly onto this node in the active mod"
        };
        _attachComponentBtn.AddThemeFontSizeOverride("font_size", 11);
        _attachComponentBtn.Pressed += OnAttachComponentPressed;
        attachBar.AddChild(_attachComponentBtn);

        vbox.AddChild(attachBar);
        vbox.AddChild(new HSeparator());

        // 4. Scrollable Component Stack (2-column Grid for Direct & Inherited Component Cards):
        var stackScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };

        _componentStackContainer = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _componentStackContainer.AddThemeConstantOverride("separation", 8);
        stackScroll.AddChild(_componentStackContainer);
        vbox.AddChild(stackScroll);

        vbox.AddChild(new HSeparator());

        // 5. Color Legend Row:
        var legendRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center
        };
        legendRow.AddThemeConstantOverride("separation", 14);

        var legendPrefix = new Label
        {
            Text = "Property Colors:",
            Modulate = new Color(0.58f, 0.64f, 0.72f)
        };
        legendPrefix.AddThemeFontSizeOverride("font_size", 10);
        legendRow.AddChild(legendPrefix);

        var blueLegend = new Label
        {
            Text = "Blue = Vanilla Override on Node",
            Modulate = new Color(0.55f, 0.86f, 1.0f)
        };
        blueLegend.AddThemeFontSizeOverride("font_size", 10);
        legendRow.AddChild(blueLegend);

        var goldLegend = new Label
        {
            Text = "Gold = Modded (Active Mod)",
            Modulate = new Color(1.0f, 0.85f, 0.40f)
        };
        goldLegend.AddThemeFontSizeOverride("font_size", 10);
        legendRow.AddChild(goldLegend);

        var greyLegend = new Label
        {
            Text = "Grey = Walked Up from Parent",
            Modulate = new Color(0.64f, 0.70f, 0.78f)
        };
        greyLegend.AddThemeFontSizeOverride("font_size", 10);
        legendRow.AddChild(greyLegend);

        vbox.AddChild(legendRow);

        // 6. Save & Revert Footer Bar:
        var footerRow = new HBoxContainer();
        footerRow.AddThemeConstantOverride("separation", 8);

        _saveModBtn = new Button
        {
            Text = "Save Node & Component Changes to Mod",
            TooltipText = $"Write modified component properties and localized text tags to the active mod ({Refii.Core.Engine_Title.Active.ModExtension})",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _saveModBtn.Pressed += OnSaveModPressed;
        footerRow.AddChild(_saveModBtn);

        _revertPropsBtn = new Button
        {
            Text = "↺ Reset Unsaved Edits",
            TooltipText = "Revert unsaved property changes back to baseline values"
        };
        _revertPropsBtn.Pressed += OnRevertPropsPressed;
        footerRow.AddChild(_revertPropsBtn);

        vbox.AddChild(footerRow);

        _saveStatusLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            Modulate = new Color(0.5f, 0.9f, 0.6f)
        };
        _saveStatusLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_saveStatusLabel);

        panel.AddChild(vbox);
        return panel;
    }

    private void PopulateTree()
    {
        if (_hierarchyTree is null) return;

        _suppressTreeSelectionEvent = true;
        _hierarchyTree.Clear();
        _treeItemsByHash.Clear();

        string query = _searchInput?.Text.Trim() ?? string.Empty;
        bool hasQuery = !string.IsNullOrWhiteSpace(query);
        bool modOnly = _modifiedOnlyCheck?.ButtonPressed ?? false;
        string domain = _graph.DomainFilter;

        int matchedCount = 0;

        bool ShouldIncludeSubtree(ItemGraphNode node)
        {
            bool domainOk = PolymorphicItemGraph.MatchesDomain(node, domain);
            if (!domainOk) return false;

            bool selfMatches = MatchesSearchAndModFilter(node, query, modOnly);
            if (selfMatches) return true;

            foreach (var child in node.Children)
            {
                if (ShouldIncludeSubtree(child)) return true;
            }
            return false;
        }

        void BuildTreeRecursive(ItemGraphNode node, TreeItem? parentItem, int depth)
        {
            if (node != _graph.Root && !ShouldIncludeSubtree(node))
            {
                return;
            }

            TreeItem item = _hierarchyTree.CreateItem(parentItem);
            item.SetMetadata(0, node.RecordHash);
            _treeItemsByHash[node.RecordHash] = item;
            matchedCount++;

            string badge = node.IsCustomModNode
                ? " [NEW]"
                : (node.HasModOverrides ? " [MOD]" : string.Empty);
            item.SetText(0, node.Label + badge);

            Texture2D? icon = ResolveItemIcon(node);
            if (icon is not null)
            {
                item.SetIcon(0, icon);
                item.SetIconMaxWidth(0, 18);
            }

            if (node.IsCustomModNode)
            {
                item.SetCustomColor(0, new Color(0.45f, 0.95f, 0.68f));
            }
            else if (node.HasModOverrides)
            {
                item.SetCustomColor(0, new Color(1.0f, 0.85f, 0.40f));
            }
            else if (node.Children.Count > 0)
            {
                item.SetCustomColor(0, new Color(0.82f, 0.88f, 0.96f));
            }

            string metaText = node.Children.Count > 0
                ? $"0x{node.RecordHash:X8} ({node.Children.Count})"
                : $"0x{node.RecordHash:X8}";
            item.SetText(1, metaText);
            item.SetTextAlignment(1, HorizontalAlignment.Right);
            item.SetCustomColor(1, new Color(0.50f, 0.56f, 0.65f));

            foreach (var child in node.Children)
            {
                BuildTreeRecursive(child, item, depth + 1);
            }

            // Start collapsed by default (only root at depth == 0 is uncollapsed so top-level branches are visible and collapsed),
            // unless searching, filtering by Modified Only, or expanding ancestors along a non-root ActivePath:
            bool expandForActiveSelection = _graph.SelectedNode != _graph.Root &&
                                            _graph.ActivePath.Contains(node) &&
                                            node != _graph.SelectedNode;
            if (hasQuery || modOnly || depth == 0 || expandForActiveSelection)
            {
                item.Collapsed = false;
            }
            else
            {
                item.Collapsed = true;
            }
        }

        BuildTreeRecursive(_graph.Root, null, 0);

        if (_treeItemsByHash.TryGetValue(_graph.SelectedNode.RecordHash, out TreeItem? selectedItem))
        {
            selectedItem.Select(0);
            _hierarchyTree.ScrollToItem(selectedItem);
        }

        if (_treeSummaryLabel is not null)
        {
            _treeSummaryLabel.Text = $"Showing {matchedCount:N0} GDB node(s) rooted at 0x{_graph.Root.RecordHash:X8}";
        }

        _suppressTreeSelectionEvent = false;
    }

    private static bool MatchesSearchAndModFilter(ItemGraphNode node, string query, bool modOnly)
    {
        if (modOnly && !node.HasModOverrides)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (node.Label.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(node.InternalName) && node.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if ($"0x{node.RecordHash:X8}".Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        if (node.RecordHash.ToString("X8", CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void SetTreeCollapsedRecursive(TreeItem? item, bool collapsed, int maxDepth, int currentDepth = 0)
    {
        if (item is null) return;
        if (currentDepth >= maxDepth)
        {
            item.Collapsed = collapsed;
        }
        else if (!collapsed)
        {
            item.Collapsed = false;
        }

        TreeItem? child = item.GetFirstChild();
        while (child is not null)
        {
            SetTreeCollapsedRecursive(child, collapsed, maxDepth, currentDepth + 1);
            child = child.GetNext();
        }
    }

    private void OnTreeItemSelected()
    {
        if (_suppressTreeSelectionEvent || _hierarchyTree is null) return;
        TreeItem? selected = _hierarchyTree.GetSelected();
        if (selected is null) return;

        Variant meta = selected.GetMetadata(0);
        uint hash = (uint)(long)meta;
        ItemGraphNode? target = _graph.FindNodeByHash(hash);
        if (target is not null && target != _graph.SelectedNode)
        {
            _graph.SelectNode(target);
        }
    }

    private void OnGraphNavigated()
    {
        if (_hierarchyTree is not null && _treeItemsByHash.TryGetValue(_graph.SelectedNode.RecordHash, out TreeItem? item))
        {
            _suppressTreeSelectionEvent = true;
            TreeItem? p = item.GetParent();
            while (p is not null)
            {
                p.Collapsed = false;
                p = p.GetParent();
            }
            item.Select(0);
            _hierarchyTree.ScrollToItem(item);
            _suppressTreeSelectionEvent = false;
        }

        UpdateInspector();
    }

    private Texture2D? ResolveItemIcon(ItemGraphNode node)
    {
        if (node is null) return null;
        if (node.IconTexture is not null) return node.IconTexture;

        string iconPath = !string.IsNullOrWhiteSpace(node.IconTexPath)
            ? node.IconTexPath
            : (node.Detail?.IconTex ?? string.Empty);

        if (string.IsNullOrWhiteSpace(iconPath)) return null;

        if (_iconCache.TryGetValue(iconPath, out Texture2D? cached))
        {
            return cached;
        }

        Texture2D? resolved = BnkMountPlugin.GetEquipmentIcon(iconPath);
        _iconCache[iconPath] = resolved;
        if (resolved is not null)
        {
            node.IconTexture = resolved;
        }
        return resolved;
    }

    private void UpdateInspector()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null) return;

        UpdateBreadcrumbs();

        Texture2D? inspIcon = ResolveItemIcon(node);
        if (_inspectorIconBox is not null && _inspectorIconRect is not null)
        {
            if (inspIcon is not null)
            {
                _inspectorIconRect.Texture = inspIcon;
                _inspectorIconBox.Visible = true;
            }
            else
            {
                _inspectorIconBox.Visible = false;
            }
        }

        if (_inspectorTitle is not null)
        {
            string badge = node.IsCustomModNode ? "  [NEW MOD NODE]" : (node.HasModOverrides ? "  [MODIFIED]" : "");
            _inspectorTitle.Text = node.Label + badge;
        }

        if (_inspectorSubtitle is not null)
        {
            string internalPart = !string.IsNullOrWhiteSpace(node.InternalName) ? $"  |  ID: {node.InternalName}" : "";
            string parentPart = node.ParentHash != 0 ? $"  |  Parent: 0x{node.ParentHash:X8}" : "  |  Top Root";
            _inspectorSubtitle.Text = $"Record: 0x{node.RecordHash:X8}  |  Domain: {node.Category}{internalPart}{parentPart}  |  Children: {node.Children.Count}";
        }

        if (_inspectorDesc is not null)
        {
            _inspectorDesc.Text = !string.IsNullOrWhiteSpace(node.Description)
                ? node.Description
                : $"GDB node in {node.Category}.";
        }

        if (_openFullEditorBtn is not null)
        {
            _openFullEditorBtn.Visible = node.Detail is not null;
        }

        if (_inspect3DBtn is not null)
        {
            string? effectiveModel = ResolveEffectiveModelPathForNode(node);
            _inspect3DBtn.Visible = !string.IsNullOrWhiteSpace(effectiveModel);
            if (!string.IsNullOrWhiteSpace(effectiveModel))
            {
                _inspect3DBtn.TooltipText = $"Inspect '{effectiveModel}' in Godot's 3D viewport";
            }
        }

        if (_placeInSceneBtn is not null)
        {
            _placeInSceneBtn.Visible = node.RecordHash != 0 && node.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash;
        }

        if (_deleteCustomNodeBtn is not null)
        {
            _deleteCustomNodeBtn.Visible = node.IsCustomModNode;
        }

        if (_createChildNodeBtn is not null)
        {
            _createChildNodeBtn.Visible = node.RecordHash != 0;
        }

        if (_saveStatusLabel is not null)
        {
            _saveStatusLabel.Text = "";
        }

        PopulateAttachComponentDropdown(node);
        PopulateComponentStack(node);
    }

    private static string? ResolveEffectiveModelPathForNode(ItemGraphNode? node)
    {
        if (node is null || node.RecordHash == 0) return null;

        foreach (var comp in node.DirectComponents)
        {
            foreach (var prop in comp.Properties)
            {
                if (prop.AssetKind == AssetFieldKind.Model &&
                    !string.IsNullOrWhiteSpace(prop.CurrentValue) &&
                    !prop.CurrentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return prop.CurrentValue;
                }
            }
        }

        foreach (var comp in node.InheritedComponents)
        {
            if (comp.IsStrippedByNode) continue;
            foreach (var prop in comp.Properties)
            {
                if (prop.AssetKind == AssetFieldKind.Model &&
                    !string.IsNullOrWhiteSpace(prop.CurrentValue) &&
                    !prop.CurrentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return prop.CurrentValue;
                }
            }
        }

        return EditorModValueService.ResolveNodeEffectiveModelPath(node.RecordHash);
    }

    private void OnInspectIn3DPressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || node.RecordHash == 0) return;

        string? modelPath = ResolveEffectiveModelPathForNode(node);
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            ShowStatus($"Record '{node.Label}' does not have a resolved 3D model (.mdl).", isError: true);
            return;
        }

        if (BnkMountPlugin.Instance is not null &&
            BnkMountPlugin.Instance.InspectGdbRecordIn3D(node.RecordHash, modelPath, switchTo3DView: true))
        {
            ShowStatus($"Opened '{node.Label}' ({modelPath}) in 3D view.", isError: false);
        }
        else
        {
            ShowStatus($"Could not open '{modelPath}' in 3D view.", isError: true);
        }
    }

    private void OnPlaceIn3DScenePressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || node.RecordHash == 0) return;

        string? modelPath = ResolveEffectiveModelPathForNode(node);
        Node3D? spawned = EditorModValueService.PlaceRecordInActive3DScene(
            node.RecordHash,
            !string.IsNullOrWhiteSpace(node.InternalName) ? node.InternalName : node.Label,
            targetLayerName: null,
            worldPosition: null,
            explicitModelPath: modelPath,
            out string err);

        if (spawned is not null)
        {
            ShowStatus($"Placed '{spawned.Name}' into the active 3D level scene (layer '{EditorModValueService.ActiveTargetSaveLayer}').", isError: false);
        }
        else
        {
            ShowStatus($"Could not place in 3D scene: {err}", isError: true);
        }
    }

    private void UpdateBreadcrumbs()
    {
        if (_breadcrumbBar is null) return;

        foreach (Node child in _breadcrumbBar.GetChildren())
        {
            _breadcrumbBar.RemoveChild(child);
            child.QueueFree();
        }

        for (int i = 0; i < _graph.ActivePath.Count; i++)
        {
            ItemGraphNode n = _graph.ActivePath[i];

            if (i > 0)
            {
                var sep = new Label
                {
                    Text = "›",
                    Modulate = new Color(0.45f, 0.50f, 0.60f)
                };
                _breadcrumbBar.AddChild(sep);
            }

            var btn = new Button
            {
                Text = $"{n.Label} (0x{n.RecordHash:X8})",
                Flat = true
            };
            btn.AddThemeFontSizeOverride("font_size", 11);

            Texture2D? bIcon = ResolveItemIcon(n);
            if (bIcon is not null)
            {
                btn.Icon = bIcon;
                btn.ExpandIcon = true;
            }

            ItemGraphNode target = n;
            btn.Pressed += () => _graph.StepToAncestor(target);

            if (target == _graph.SelectedNode)
            {
                btn.Modulate = new Color(1.0f, 0.85f, 0.40f);
            }

            _breadcrumbBar.AddChild(btn);
        }
    }

    private void PopulateAttachComponentDropdown(ItemGraphNode node)
    {
        if (_attachComponentDropdown is null) return;
        _attachComponentDropdown.Clear();

        var directSlotHashes = new HashSet<uint>(node.DirectComponents.Select(c => c.SlotHash));
        int idx = 0;
        foreach (ComponentSlotSchema schema in Engine_RecordNameCatalog.AllComponentSchemas)
        {
            if (directSlotHashes.Contains(schema.SlotHash))
            {
                continue;
            }

            _attachComponentDropdown.AddItem($"{schema.Category}: {schema.DisplayName} ({schema.SlotName})", idx);
            _attachComponentDropdown.SetItemMetadata(idx, schema.SlotName);
            idx++;
        }

        _attachComponentDropdown.AddItem("Custom Component Slot (Lua / Mod)...", idx);
        _attachComponentDropdown.SetItemMetadata(idx, "__CUSTOM__");

        OnAttachDropdownSelected(_attachComponentDropdown.Selected);
    }

    private void OnAttachDropdownSelected(long index)
    {
        if (_attachComponentDropdown is null || _customComponentNameInput is null) return;
        if (index < 0 || index >= _attachComponentDropdown.ItemCount)
        {
            _customComponentNameInput.Visible = false;
            return;
        }

        string meta = _attachComponentDropdown.GetItemMetadata((int)index).AsString();
        _customComponentNameInput.Visible = (meta == "__CUSTOM__");
    }

    private void PopulateComponentStack(ItemGraphNode node)
    {
        if (_componentStackContainer is null) return;

        foreach (Node child in _componentStackContainer.GetChildren())
        {
            _componentStackContainer.RemoveChild(child);
            child.QueueFree();
        }

        // SECTION 1: Directly Attached / Overridden Components on This Node (2-column Grid)
        var directHeader = new Label
        {
            Text = $"ATTACHED & OVERRIDDEN COMPONENTS ON THIS NODE ({node.DirectComponents.Count})",
            Modulate = new Color(0.55f, 0.82f, 1.0f)
        };
        directHeader.AddThemeFontSizeOverride("font_size", 11);
        _componentStackContainer.AddChild(directHeader);

        if (node.DirectComponents.Count == 0)
        {
            var emptyDirect = new Label
            {
                Text = "No components are directly attached on this node record. It uses its inherited components below.",
                Modulate = new Color(0.55f, 0.60f, 0.68f)
            };
            emptyDirect.AddThemeFontSizeOverride("font_size", 11);
            _componentStackContainer.AddChild(emptyDirect);
        }
        else
        {
            var directGrid = new GridContainer
            {
                Columns = 2,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            directGrid.AddThemeConstantOverride("h_separation", 10);
            directGrid.AddThemeConstantOverride("v_separation", 10);

            foreach (NodeComponentSlot slot in node.DirectComponents)
            {
                directGrid.AddChild(BuildDirectComponentCard(node, slot));
            }
            _componentStackContainer.AddChild(directGrid);
        }

        // Direct Node Scalar Fields (if any exist outside component pointers, e.g. custom Lua fields or unit tests):
        var directNodeScalars = node.Properties.Where(p => p.TargetRecordHash == node.RecordHash).ToList();
        if (directNodeScalars.Count > 0)
        {
            _componentStackContainer.AddChild(BuildDirectNodeScalarsCard(node, directNodeScalars));
        }

        _componentStackContainer.AddChild(new HSeparator());

        // SECTION 2: Inherited Components (Walked Up from Ancestors) (2-column Grid)
        var inheritedHeader = new Label
        {
            Text = $"INHERITED COMPONENTS (WALKED UP FROM ANCESTORS) ({node.InheritedComponents.Count})",
            Modulate = new Color(0.75f, 0.80f, 0.88f)
        };
        inheritedHeader.AddThemeFontSizeOverride("font_size", 11);
        _componentStackContainer.AddChild(inheritedHeader);

        if (node.InheritedComponents.Count == 0)
        {
            var emptyInh = new Label
            {
                Text = "No additional components inherited from ancestors.",
                Modulate = new Color(0.55f, 0.60f, 0.68f)
            };
            emptyInh.AddThemeFontSizeOverride("font_size", 11);
            _componentStackContainer.AddChild(emptyInh);
        }
        else
        {
            var inheritedGrid = new GridContainer
            {
                Columns = 2,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            inheritedGrid.AddThemeConstantOverride("h_separation", 10);
            inheritedGrid.AddThemeConstantOverride("v_separation", 8);

            foreach (NodeComponentSlot slot in node.InheritedComponents)
            {
                inheritedGrid.AddChild(BuildInheritedComponentCard(node, slot));
            }
            _componentStackContainer.AddChild(inheritedGrid);
        }
    }

    private Control BuildDirectComponentCard(ItemGraphNode node, NodeComponentSlot slot)
    {
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.Fill
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.12f, 0.16f),
            BorderColor = slot.IsCustomOrModOverriddenSlot || slot.Properties.Any(p => p.IsModified)
                ? new Color(0.75f, 0.62f, 0.25f)
                : new Color(0.26f, 0.36f, 0.48f),
            BorderWidthLeft = 2,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        card.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);

        // Card Header Row:
        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);

        var titleLabel = new Label
        {
            Text = slot.DisplayName,
            TooltipText = $"{slot.SlotName} | Component Record: 0x{slot.ComponentRecordHash:X8}\n{slot.Summary}",
            Modulate = new Color(0.88f, 0.94f, 1.0f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 12);
        topRow.AddChild(titleLabel);

        var hashLabel = new Label
        {
            Text = $"0x{slot.ComponentRecordHash:X8}",
            Modulate = new Color(0.50f, 0.58f, 0.68f)
        };
        hashLabel.AddThemeFontSizeOverride("font_size", 10);
        topRow.AddChild(hashLabel);

        var addPropBtn = new Button
        {
            Text = "+ Prop",
            TooltipText = "Add a custom property to this component record"
        };
        addPropBtn.AddThemeFontSizeOverride("font_size", 10);
        uint capturedCompHash = slot.ComponentRecordHash;
        string capturedSlotName = slot.SlotName;
        addPropBtn.Pressed += () => OpenAddPropertyDialog(capturedCompHash, capturedSlotName);
        topRow.AddChild(addPropBtn);

        if (slot.IsCustomOrModOverriddenSlot || slot.Properties.Any(p => p.IsModified))
        {
            string removeBtnText = slot.OverriddenAncestorNodeHash != 0 ? "↺ Inherit" : "✕ Remove";
            string removeTooltip = slot.OverriddenAncestorNodeHash != 0
                ? "Remove this component override from the active mod so the node reverts to its inherited ancestor component"
                : "Detach/revert this component from the node in the active mod";

            var removeCompBtn = new Button
            {
                Text = removeBtnText,
                TooltipText = removeTooltip,
                Modulate = new Color(1.0f, 0.72f, 0.65f)
            };
            removeCompBtn.AddThemeFontSizeOverride("font_size", 10);
            NodeComponentSlot capturedSlot = slot;
            removeCompBtn.Pressed += () => OnRemoveOrRevertDirectComponent(node, capturedSlot);
            topRow.AddChild(removeCompBtn);
        }

        vbox.AddChild(topRow);

        // Override Lineage Notice (if this component overrides an ancestor node's component):
        bool overridesParent = slot.OverriddenAncestorNodeHash != 0 || slot.ParentComponentRecordHash != 0;

        if (slot.OverriddenAncestorNodeHash != 0)
        {
            var overrideRow = new HBoxContainer();
            overrideRow.AddThemeConstantOverride("separation", 4);

            var ovLabel = new Label
            {
                Text = "Overrides:",
                Modulate = new Color(0.95f, 0.78f, 0.40f)
            };
            ovLabel.AddThemeFontSizeOverride("font_size", 10);
            overrideRow.AddChild(ovLabel);

            var ancBtn = new Button
            {
                Text = $"{slot.OverriddenAncestorNodeLabel} (0x{slot.OverriddenAncestorNodeHash:X8})",
                Flat = true
            };
            ancBtn.AddThemeFontSizeOverride("font_size", 10);
            uint ancHash = slot.OverriddenAncestorNodeHash;
            ancBtn.Pressed += () =>
            {
                var ancNode = _graph.FindNodeByHash(ancHash);
                if (ancNode is not null) _graph.SelectNode(ancNode);
            };
            overrideRow.AddChild(ancBtn);
            vbox.AddChild(overrideRow);
        }

        // Editable Properties List:
        if (slot.Properties.Count == 0)
        {
            var noProps = new Label
            {
                Text = "(No properties defined — click '+ Prop' to add)",
                Modulate = new Color(0.50f, 0.55f, 0.62f)
            };
            noProps.AddThemeFontSizeOverride("font_size", 10);
            vbox.AddChild(noProps);
        }
        else
        {
            var propList = new VBoxContainer();
            propList.AddThemeConstantOverride("separation", 3);
            foreach (ModItemProperty prop in slot.Properties)
            {
                propList.AddChild(BuildEditablePropertyRow(node, prop, overridesParent));
            }
            vbox.AddChild(propList);
        }

        card.AddChild(vbox);
        return card;
    }

    private Control BuildDirectNodeScalarsCard(ItemGraphNode node, List<ModItemProperty> directProps)
    {
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.12f, 0.16f),
            BorderColor = new Color(0.26f, 0.36f, 0.48f),
            BorderWidthLeft = 2,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        card.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);

        var title = new Label
        {
            Text = $"Direct Node Record Fields (0x{node.RecordHash:X8})",
            Modulate = new Color(0.88f, 0.94f, 1.0f)
        };
        title.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(title);

        var grid = new GridContainer
        {
            Columns = 2,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 4);

        foreach (ModItemProperty prop in directProps)
        {
            grid.AddChild(BuildEditablePropertyRow(node, prop, false));
        }

        vbox.AddChild(grid);
        card.AddChild(vbox);
        return card;
    }

    private static Color GetPropertyLabelColor(ModItemProperty prop, bool componentOverridesParent)
    {
        if (prop.IsModified)
            return new Color(1.0f, 0.85f, 0.40f);
        if (componentOverridesParent)
        {
            if (prop.IsDefinedOnComponent)
                return new Color(0.55f, 0.86f, 1.0f);
            if (prop.IsWalkedUpFromParent)
                return new Color(0.64f, 0.70f, 0.78f);
        }
        return new Color(0.86f, 0.89f, 0.94f);
    }

    private static string BuildPropertyTooltip(ModItemProperty prop, bool componentOverridesParent)
    {
        string baseInfo = $"Field: {prop.PropertyName} (0x{prop.PropertyHash:X8}) | Type: {prop.Type} | Baseline: {prop.VanillaValue}";
        if (!componentOverridesParent)
            return baseInfo;

        if (prop.IsDefinedOnComponent)
        {
            string parentInfo = !string.IsNullOrEmpty(prop.ParentComponentValue)
                ? $"Overrides parent value: {prop.ParentComponentValue}"
                : "Defined directly on this component record";
            return $"{baseInfo} | {parentInfo}";
        }

        if (prop.IsWalkedUpFromParent)
        {
            string sourceInfo = prop.DeclaringHash != 0
                ? $"Walked up from parent component (0x{prop.DeclaringHash:X8})"
                : "Not overridden on this component (inherits parent/default)";
            return $"{baseInfo} | {sourceInfo}";
        }

        return baseInfo;
    }

    private Control BuildEditablePropertyRow(ItemGraphNode node, ModItemProperty prop, bool componentOverridesParent = false)
    {
        if (prop.AssetKind != AssetFieldKind.None)
        {
            return BuildAssetPropertyRow(node, prop, componentOverridesParent);
        }

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 4);

        var nameLabel = new Label
        {
            Text = prop.DisplayLabel,
            TooltipText = BuildPropertyTooltip(prop, componentOverridesParent),
            CustomMinimumSize = new Vector2(105, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        nameLabel.Modulate = GetPropertyLabelColor(prop, componentOverridesParent);
        row.AddChild(nameLabel);

        ModItemProperty capturedProp = prop;
        bool capturedOverridesParent = componentOverridesParent;

        if (prop.Type == GdbFieldType.Bool)
        {
            bool isChecked = prop.CurrentValue.Equals("true", StringComparison.OrdinalIgnoreCase) || prop.CurrentValue == "1";
            var check = new CheckBox
            {
                ButtonPressed = isChecked,
                Text = isChecked ? "True" : "False",
                CustomMinimumSize = new Vector2(68, 0)
            };
            check.AddThemeFontSizeOverride("font_size", 10);
            check.Toggled += pressed =>
            {
                string newVal = pressed ? "true" : "false";
                check.Text = pressed ? "True" : "False";
                SyncPropertyEdit(node, capturedProp, newVal, nameLabel, capturedOverridesParent);
            };
            row.AddChild(check);

            var resetBtn = new Button
            {
                Text = "↺",
                TooltipText = $"Reset to baseline ({prop.VanillaValue})",
                CustomMinimumSize = new Vector2(24, 22)
            };
            resetBtn.AddThemeFontSizeOverride("font_size", 11);
            resetBtn.Pressed += () =>
            {
                bool vPressed = capturedProp.VanillaValue.Equals("true", StringComparison.OrdinalIgnoreCase) || capturedProp.VanillaValue == "1";
                check.ButtonPressed = vPressed;
                check.Text = vPressed ? "True" : "False";
                SyncPropertyEdit(node, capturedProp, capturedProp.VanillaValue, nameLabel, capturedOverridesParent);
            };
            row.AddChild(resetBtn);
        }
        else
        {
            bool isTextOrString = prop.Type == GdbFieldType.String || prop.IsLocalizedTextTag;
            var lineEdit = new LineEdit
            {
                Text = prop.CurrentValue,
                TooltipText = prop.CurrentValue,
                CustomMinimumSize = new Vector2(isTextOrString ? 120 : 72, 0),
                SizeFlagsHorizontal = isTextOrString ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill,
                SizeFlagsStretchRatio = isTextOrString ? 1.15f : 0.0f
            };
            lineEdit.AddThemeFontSizeOverride("font_size", 11);
            lineEdit.TextChanged += newText =>
            {
                lineEdit.TooltipText = newText;
                SyncPropertyEdit(node, capturedProp, newText, nameLabel, capturedOverridesParent);
            };
            row.AddChild(lineEdit);

            var resetBtn = new Button
            {
                Text = "↺",
                TooltipText = $"Reset to baseline ({prop.VanillaValue})",
                CustomMinimumSize = new Vector2(24, 22)
            };
            resetBtn.AddThemeFontSizeOverride("font_size", 11);
            resetBtn.Pressed += () =>
            {
                lineEdit.Text = capturedProp.VanillaValue;
                lineEdit.TooltipText = capturedProp.VanillaValue;
                SyncPropertyEdit(node, capturedProp, capturedProp.VanillaValue, nameLabel, capturedOverridesParent);
            };
            row.AddChild(resetBtn);
        }

        return row;
    }

    private Control BuildAssetPropertyRow(ItemGraphNode node, ModItemProperty prop, bool componentOverridesParent)
    {
        var outerVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        outerVBox.AddThemeConstantOverride("separation", 3);

        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        row.AddThemeConstantOverride("separation", 4);

        string allowedExts = string.Join(", ", EditorModValueService.GetAllowedExtensionsForAssetKind(prop.AssetKind));
        var nameLabel = new Label
        {
            Text = prop.DisplayLabel,
            TooltipText = $"{BuildPropertyTooltip(prop, componentOverridesParent)}\nDrag & Drop ({allowedExts}) from FileSystem dock or click Link...",
            CustomMinimumSize = new Vector2(95, 0),
            SizeFlagsHorizontal = Control.SizeFlags.Fill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        nameLabel.Modulate = GetPropertyLabelColor(prop, componentOverridesParent);
        row.AddChild(nameLabel);

        ModItemProperty capturedProp = prop;
        bool capturedOverridesParent = componentOverridesParent;

        var dropEdit = new AssetDropLineEdit(prop.AssetKind)
        {
            Text = prop.CurrentValue,
            PlaceholderText = prop.AssetKind == AssetFieldKind.Model
                ? "Drop .glb / .mdl here..."
                : "Drop .png / .dds / .tex here...",
            TooltipText = $"{prop.CurrentValue}\nDrag & drop ({allowedExts}) from res://creator/ in FileSystem dock to convert & link automatically.",
            CustomMinimumSize = new Vector2(130, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.25f
        };
        dropEdit.AddThemeFontSizeOverride("font_size", 11);
        dropEdit.TextChanged += newText =>
        {
            dropEdit.TooltipText = newText;
            SyncPropertyEdit(node, capturedProp, newText, nameLabel, capturedOverridesParent);
        };
        dropEdit.AssetDropped += droppedPath =>
        {
            HandleLinkAssetToProperty(node, capturedProp, droppedPath);
        };
        dropEdit.InvalidAssetDropped += invalidPath =>
        {
            ShowStatus($"Unsupported format '{System.IO.Path.GetExtension(invalidPath)}' for {capturedProp.PropertyName}. Allowed: {allowedExts}", isError: true);
        };
        row.AddChild(dropEdit);

        bool canExport = !string.IsNullOrWhiteSpace(prop.CurrentValue) &&
                         !prop.CurrentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

        var exportBtn = new Button
        {
            Text = "📤",
            TooltipText = prop.AssetKind == AssetFieldKind.Model
                ? $"Export '{prop.CurrentValue}' (.glb + surface .png textures) into res://creator/{EditorModExporter.ActiveModName}/"
                : $"Export '{prop.CurrentValue}' (.png) into res://creator/{EditorModExporter.ActiveModName}/",
            Disabled = !canExport,
            CustomMinimumSize = new Vector2(26, 22)
        };
        exportBtn.AddThemeFontSizeOverride("font_size", 10);
        exportBtn.Pressed += () => HandleExportAssetProperty(node, capturedProp);
        row.AddChild(exportBtn);

        var linkBtn = new Button
        {
            Text = "📁",
            TooltipText = $"Browse res://creator/{EditorModExporter.ActiveModName}/ to convert and link an asset ({allowedExts})",
            CustomMinimumSize = new Vector2(26, 22)
        };
        linkBtn.AddThemeFontSizeOverride("font_size", 10);
        linkBtn.Pressed += () =>
        {
            OpenAssetFileDialog(capturedProp.AssetKind, selectedPath =>
            {
                HandleLinkAssetToProperty(node, capturedProp, selectedPath);
            });
        };
        row.AddChild(linkBtn);

        if (prop.AssetKind == AssetFieldKind.Model)
        {
            var socketsBtn = new Button
            {
                Text = "🎯",
                TooltipText = "Inspect .mdl.gmd attachment sockets (hand grip, projectile muzzle, target points) & copy socket names for Blender",
                Disabled = !canExport,
                CustomMinimumSize = new Vector2(26, 22)
            };
            socketsBtn.AddThemeFontSizeOverride("font_size", 10);
            socketsBtn.Pressed += () => OpenGmdSocketsInspectorDialog(node, capturedProp);
            row.AddChild(socketsBtn);
        }

        var resetBtn = new Button
        {
            Text = "↺",
            TooltipText = $"Reset to baseline ({prop.VanillaValue})",
            CustomMinimumSize = new Vector2(24, 22)
        };
        resetBtn.AddThemeFontSizeOverride("font_size", 11);
        resetBtn.Pressed += () =>
        {
            dropEdit.Text = capturedProp.VanillaValue;
            dropEdit.TooltipText = capturedProp.VanillaValue;
            SyncPropertyEdit(node, capturedProp, capturedProp.VanillaValue, nameLabel, capturedOverridesParent);
        };
        row.AddChild(resetBtn);

        outerVBox.AddChild(row);

        // If this is a Model property that resolves to a .mdl path, offer expandable Surface Material Texture slots:
        if (prop.AssetKind == AssetFieldKind.Model && canExport &&
            prop.CurrentValue.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            List<ModModelMaterialSlot> matSlots = EditorModValueService.GetModelMaterialSlots(
                prop.CurrentValue, prop.VanillaValue, EditorModExporter.ActiveModName, BnkMountPlugin.Instance?.FileSystem);

            if (matSlots.Count > 0)
            {
                outerVBox.AddChild(BuildModelMaterialSlotsSection(node, capturedProp, matSlots));
            }
        }

        return outerVBox;
    }

    private Control BuildModelMaterialSlotsSection(
        ItemGraphNode node,
        ModItemProperty modelProp,
        List<ModModelMaterialSlot> matSlots)
    {
        var key = (modelProp.TargetRecordHash, modelProp.PropertyHash);
        bool anyModified = matSlots.Any(s => s.IsModified);
        bool isExpanded = _expandedMaterialPanels.Contains(key) || anyModified;

        var sectionVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        sectionVBox.AddThemeConstantOverride("separation", 2);

        var toggleBtn = new Button
        {
            Text = $"{(isExpanded ? "▾" : "▸")} 🎨 Surface Material Textures ({matSlots.Count} slot{(matSlots.Count == 1 ? "" : "s")})",
            TooltipText = "Expand to export or drag-and-drop replacement textures (.png / .dds / .tex) onto individual material slots of this 3D model",
            Alignment = HorizontalAlignment.Left,
            Flat = true,
            Modulate = anyModified ? new Color(1.0f, 0.85f, 0.40f) : new Color(0.65f, 0.80f, 0.96f)
        };
        toggleBtn.AddThemeFontSizeOverride("font_size", 10);

        var slotsPanel = new PanelContainer
        {
            Visible = isExpanded,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var slotsStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.12f),
            BorderColor = new Color(0.20f, 0.25f, 0.34f),
            BorderWidthLeft = 2,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 3,
            CornerRadiusBottomRight = 3,
            CornerRadiusTopLeft = 3,
            CornerRadiusTopRight = 3,
            ContentMarginLeft = 8,
            ContentMarginRight = 6,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        slotsPanel.AddThemeStyleboxOverride("panel", slotsStyle);

        var slotsVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        slotsVBox.AddThemeConstantOverride("separation", 3);

        foreach (ModModelMaterialSlot slot in matSlots)
        {
            ModModelMaterialSlot capturedSlot = slot;
            var slotRow = new HBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            slotRow.AddThemeConstantOverride("separation", 4);

            var slotLabel = new Label
            {
                Text = $"Mat {slot.FlatMaterialIndex} · {slot.SlotName}",
                TooltipText = $"{slot.MeshName}, Material {slot.SubMaterialIndex} ({slot.SlotName})\nCurrent: {slot.CurrentTexturePath}\nBaseline: {slot.VanillaTexturePath}",
                CustomMinimumSize = new Vector2(92, 0),
                Modulate = slot.SlotIndex == 5
                    ? (slot.IsModified ? new Color(0.35f, 0.90f, 1.0f) : new Color(0.55f, 0.70f, 0.85f))
                    : slot.SlotIndex == 4
                        ? (slot.IsModified ? new Color(1.0f, 0.70f, 0.35f) : new Color(0.85f, 0.72f, 0.55f))
                        : (slot.IsModified ? new Color(1.0f, 0.85f, 0.40f) : new Color(0.74f, 0.80f, 0.88f)),
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
            };
            slotLabel.AddThemeFontSizeOverride("font_size", 10);
            slotRow.AddChild(slotLabel);

            var texDropEdit = new AssetDropLineEdit(AssetFieldKind.Texture)
            {
                Text = slot.CurrentTexturePath,
                Editable = false,
                PlaceholderText = slot.SlotIndex switch
                {
                    5 => "Drop .png (_orm.png) here for PBR...",
                    4 => "Drop .png for Emission / Glow...",
                    _ => "Drop .png / .dds / .tex here...",
                },
                TooltipText = slot.SlotIndex switch
                {
                    5 => $"{slot.CurrentTexturePath}\nDrag & drop an authored PBR ORM texture (_orm.png) here to bind Occlusion, Roughness, and Metallic.",
                    4 => $"{slot.CurrentTexturePath}\nDrag & drop an emissive texture (.png, .dds, .tex) here for glowing runes, lights, neon, or magical effects.",
                    _ => $"{slot.CurrentTexturePath}\nDrag & drop a .png, .dds, or .tex file here to convert and rebind this surface material slot.",
                },
                CustomMinimumSize = new Vector2(110, 0),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            texDropEdit.AddThemeFontSizeOverride("font_size", 10);
            texDropEdit.AssetDropped += droppedPath =>
            {
                HandleLinkSurfaceTextureToSlot(node, modelProp, capturedSlot, droppedPath);
            };
            texDropEdit.InvalidAssetDropped += invalidPath =>
            {
                ShowStatus($"Unsupported texture format '{System.IO.Path.GetExtension(invalidPath)}'. Drop a .png, .dds, or .tex file.", isError: true);
            };
            slotRow.AddChild(texDropEdit);

            var exportTexBtn = new Button
            {
                Text = "📤",
                TooltipText = $"Export '{slot.CurrentTexturePath}' as .png into res://creator/{EditorModExporter.ActiveModName}/",
                Disabled = string.IsNullOrWhiteSpace(slot.CurrentTexturePath),
                CustomMinimumSize = new Vector2(24, 20)
            };
            exportTexBtn.AddThemeFontSizeOverride("font_size", 9);
            exportTexBtn.Pressed += () =>
            {
                ExportSingleTextureToCreator(node, capturedSlot.CurrentTexturePath, exportPbr: false);
            };
            slotRow.AddChild(exportTexBtn);

            var linkTexBtn = new Button
            {
                Text = "📁",
                TooltipText = $"Select a .png, .dds, or .tex file to link to Mat {slot.FlatMaterialIndex} {slot.SlotName}",
                CustomMinimumSize = new Vector2(24, 20)
            };
            linkTexBtn.AddThemeFontSizeOverride("font_size", 9);
            linkTexBtn.Pressed += () =>
            {
                OpenAssetFileDialog(AssetFieldKind.Texture, selectedPath =>
                {
                    HandleLinkSurfaceTextureToSlot(node, modelProp, capturedSlot, selectedPath);
                });
            };
            slotRow.AddChild(linkTexBtn);

            if (slot.IsModified && (!string.IsNullOrWhiteSpace(slot.VanillaTexturePath) || slot.SlotIndex is 4 or 5))
            {
                var revertSlotBtn = new Button
                {
                    Text = "↺",
                    TooltipText = slot.SlotIndex switch
                    {
                        5 => "Remove authored PBR ORM texture",
                        4 => string.IsNullOrWhiteSpace(slot.VanillaTexturePath)
                            ? "Remove authored emission texture"
                            : $"Revert {slot.SlotName} to baseline ({slot.VanillaTexturePath})",
                        _ => $"Revert {slot.SlotName} to baseline ({slot.VanillaTexturePath})",
                    },
                    CustomMinimumSize = new Vector2(22, 20)
                };
                revertSlotBtn.AddThemeFontSizeOverride("font_size", 10);
                revertSlotBtn.Pressed += () =>
                {
                    HandleLinkSurfaceTextureToSlot(node, modelProp, capturedSlot, capturedSlot.VanillaTexturePath);
                };
                slotRow.AddChild(revertSlotBtn);
            }

            slotsVBox.AddChild(slotRow);
        }

        slotsPanel.AddChild(slotsVBox);

        toggleBtn.Pressed += () =>
        {
            bool nextState = !slotsPanel.Visible;
            slotsPanel.Visible = nextState;
            if (nextState)
                _expandedMaterialPanels.Add(key);
            else
                _expandedMaterialPanels.Remove(key);
            toggleBtn.Text = $"{(nextState ? "▾" : "▸")} 🎨 Surface Material Textures ({matSlots.Count} slot{(matSlots.Count == 1 ? "" : "s")})";
        };

        sectionVBox.AddChild(toggleBtn);
        sectionVBox.AddChild(slotsPanel);
        return sectionVBox;
    }

    private void HandleExportAssetProperty(ItemGraphNode node, ModItemProperty prop)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        string subFolder = !string.IsNullOrWhiteSpace(node.InternalName) ? node.InternalName : node.Label;

        if (prop.AssetKind == AssetFieldKind.Model)
        {
            RunWithLoadingDialog(
                "📤 Exporting 3D Model...",
                $"Converting '{System.IO.Path.GetFileName(prop.CurrentValue.Replace('\\', '/'))}' (.glb + sockets) into res://creator/{activeMod}/...",
                () =>
                {
                    if (EditorModExporter.ExportModelToCreatorDir(
                            prop.CurrentValue,
                            subFolder,
                            BnkMountPlugin.Instance?.FileSystem,
                            out string exportedGlb,
                            out List<string> exportedTextures,
                            out string err))
                    {
                        BnkMountPlugin.Instance?.SyncActiveModCreatorFavorite(activeMod);
                        BnkMountPlugin.Instance?.RefreshAndNavigateCreatorFile(exportedGlb);
                        ShowStatus(
                            $"Exported '{System.IO.Path.GetFileName(exportedGlb)}' (+sockets) to res://creator/{activeMod}/.",
                            isError: false);
                    }
                    else
                    {
                        ShowStatus($"Model export failed: {err}", isError: true);
                    }
                });
        }
        else
        {
            ExportSingleTextureToCreator(node, prop.CurrentValue, exportPbr: false);
        }
    }

    private void ExportSingleTextureToCreator(ItemGraphNode node, string textureVirtualPath, bool exportPbr)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        string subFolder = !string.IsNullOrWhiteSpace(node.InternalName) ? node.InternalName : node.Label;
        RunWithLoadingDialog(
            "📤 Exporting Texture...",
            $"Decoding '{System.IO.Path.GetFileName(textureVirtualPath.Replace('\\', '/'))}' to uncompressed .png in res://creator/{activeMod}/...",
            () =>
            {
                if (EditorModExporter.ExportTextureToCreatorDir(
                        textureVirtualPath,
                        subFolder,
                        BnkMountPlugin.Instance?.FileSystem,
                        exportPbr,
                        out string exportedPng,
                        out string err))
                {
                    BnkMountPlugin.Instance?.SyncActiveModCreatorFavorite(activeMod);
                    BnkMountPlugin.Instance?.RefreshAndNavigateCreatorFile(exportedPng);
                    ShowStatus(
                        $"Exported '{System.IO.Path.GetFileName(exportedPng)}' to res://creator/{activeMod}/.",
                        isError: false);
                }
                else
                {
                    ShowStatus($"Texture export failed: {err}", isError: true);
                }
            });
    }

    private void HandleLinkAssetToProperty(ItemGraphNode node, ModItemProperty prop, string sourceFilePath)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        string fileName = System.IO.Path.GetFileName(sourceFilePath);
        RunWithLoadingDialog(
            prop.AssetKind == AssetFieldKind.Model
                ? "📥 Compiling & Linking 3D Model..."
                : "📥 Compiling & Linking Texture...",
            $"Converting '{fileName}' and packaging into '{activeMod}{Refii.Core.Engine_Title.Active.ModExtension}'...",
            () =>
            {
                var compSlot = node.DirectComponents.FirstOrDefault(c => c.ComponentRecordHash == prop.TargetRecordHash);
                IReadOnlyList<ModItemProperty>? compProps = compSlot?.Properties;

                if (!EditorModValueService.LinkAssetToComponentProperty(
                        node.RecordHash,
                        node.Label,
                        prop,
                        compProps,
                        sourceFilePath,
                        activeMod,
                        out string newVirtualPath,
                        out string err,
                        BnkMountPlugin.Instance?.FileSystem))
                {
                    ShowStatus($"Asset link failed: {err}", isError: true);
                    return;
                }

                if (prop.AssetKind == AssetFieldKind.Model)
                {
                    _expandedMaterialPanels.Add((prop.TargetRecordHash, prop.PropertyHash));
                }

                ReloadGraphAndReselect(node.RecordHash);
                ShowStatus(
                    $"Linked '{fileName}' -> {prop.PropertyName} ({newVirtualPath}) in '{activeMod}{Refii.Core.Engine_Title.Active.ModExtension}'.",
                    isError: false);
            });
    }

    private void HandleLinkSurfaceTextureToSlot(
        ItemGraphNode node,
        ModItemProperty modelProp,
        ModModelMaterialSlot slot,
        string sourceFilePath)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        string fileName = System.IO.Path.GetFileName(sourceFilePath.Replace('\\', '/'));
        RunWithLoadingDialog(
            "🎨 Updating Surface Material Slot...",
            $"Linking '{fileName}' to Mat {slot.FlatMaterialIndex} · {slot.SlotName} in '{activeMod}{Refii.Core.Engine_Title.Active.ModExtension}'...",
            () =>
            {
                var compSlot = node.DirectComponents.FirstOrDefault(c => c.ComponentRecordHash == modelProp.TargetRecordHash);
                IReadOnlyList<ModItemProperty>? compProps = compSlot?.Properties;

                if (!EditorModValueService.LinkSurfaceTextureToModelSlot(
                        node.RecordHash,
                        node.Label,
                        modelProp,
                        compProps,
                        slot.FlatMaterialIndex,
                        slot.SlotIndex,
                        slot.SlotName,
                        sourceFilePath,
                        activeMod,
                        out string newTexPath,
                        out string newModelPath,
                        out string err,
                        BnkMountPlugin.Instance?.FileSystem))
                {
                    ShowStatus($"Surface texture link failed: {err}", isError: true);
                    return;
                }

                _expandedMaterialPanels.Add((modelProp.TargetRecordHash, modelProp.PropertyHash));
                ReloadGraphAndReselect(node.RecordHash);
                ShowStatus(
                    $"Linked '{fileName}' -> Mat {slot.FlatMaterialIndex} {slot.SlotName} ({newTexPath}) on '{newModelPath}'.",
                    isError: false);
            });
    }

    private void OpenAssetFileDialog(AssetFieldKind kind, Action<string> onFileSelected)
    {
        _pendingAssetFileCallback = onFileSelected;

        if (_assetFileDialog is null || !GodotObject.IsInstanceValid(_assetFileDialog))
        {
            _assetFileDialog = new FileDialog
            {
                Access = FileDialog.AccessEnum.Filesystem,
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Size = new Vector2I(760, 520)
            };
            _assetFileDialog.FileSelected += path =>
            {
                _pendingAssetFileCallback?.Invoke(path);
            };
            AddChild(_assetFileDialog);
        }

        _assetFileDialog.ClearFilters();
        switch (kind)
        {
            case AssetFieldKind.Model:
                _assetFileDialog.Title = "Link 3D Model (.glb, .gltf, .mdl)";
                _assetFileDialog.AddFilter("*.glb, *.gltf, *.mdl ; 3D Models (.glb, .gltf, .mdl)");
                break;
            case AssetFieldKind.Texture:
                _assetFileDialog.Title = "Link Texture (.png, .dds, .tex)";
                _assetFileDialog.AddFilter("*.png, *.dds, *.tex ; Textures (.png, .dds, .tex)");
                break;
            default:
                _assetFileDialog.Title = "Link Asset (.glb, .gltf, .mdl, .png, .dds, .tex)";
                _assetFileDialog.AddFilter("*.glb, *.gltf, *.mdl, *.png, *.dds, *.tex ; Supported Mod Assets");
                break;
        }

        string creatorDir = EditorModExporter.EnsureActiveModWorkspace();
        if (!string.IsNullOrEmpty(creatorDir) && System.IO.Directory.Exists(creatorDir))
        {
            _assetFileDialog.CurrentDir = creatorDir;
        }

        _assetFileDialog.PopupCentered(new Vector2I(760, 520));
    }

    private static void SyncPropertyEdit(ItemGraphNode node, ModItemProperty prop, string newValue, Label nameLabel, bool componentOverridesParent = false)
    {
        prop.CurrentValue = newValue;
        prop.IsModified = !EditorModValueService.AreValuesEqual(prop.Type, prop.CurrentValue, prop.VanillaValue);
        nameLabel.Modulate = GetPropertyLabelColor(prop, componentOverridesParent);

        // Also keep node.Properties in sync if a matching entry exists:
        var nodeProp = node.Properties.FirstOrDefault(p =>
            p.TargetRecordHash == prop.TargetRecordHash && p.PropertyHash == prop.PropertyHash);
        if (nodeProp is not null && !ReferenceEquals(nodeProp, prop))
        {
            nodeProp.CurrentValue = prop.CurrentValue;
            nodeProp.IsModified = prop.IsModified;
        }
    }

    private Control BuildInheritedComponentCard(ItemGraphNode node, NodeComponentSlot slot)
    {
        var card = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.Fill
        };
        var style = new StyleBoxFlat
        {
            BgColor = slot.IsStrippedByNode ? new Color(0.14f, 0.09f, 0.10f) : new Color(0.09f, 0.10f, 0.13f),
            BorderColor = slot.IsStrippedByNode ? new Color(0.55f, 0.25f, 0.28f) : new Color(0.20f, 0.22f, 0.28f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        card.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 3);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);

        string titleText = slot.IsStrippedByNode
            ? $"{slot.DisplayName} [STRIPPED]"
            : slot.DisplayName;

        var titleLabel = new Label
        {
            Text = titleText,
            TooltipText = $"{slot.SlotName} (0x{slot.ComponentRecordHash:X8}) inherited from {slot.SourceNodeLabel}",
            Modulate = slot.IsStrippedByNode ? new Color(0.95f, 0.55f, 0.55f) : new Color(0.78f, 0.83f, 0.90f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        titleLabel.AddThemeFontSizeOverride("font_size", 11);
        topRow.AddChild(titleLabel);

        string capturedSlotName = slot.SlotName;
        uint capturedParentCompHash = slot.ComponentRecordHash;

        if (!slot.IsStrippedByNode)
        {
            var overrideBtn = new Button
            {
                Text = "Override",
                TooltipText = "Create a child component record on this node so you can customize its properties (including Name/Description or stats) without affecting siblings"
            };
            overrideBtn.AddThemeFontSizeOverride("font_size", 10);
            overrideBtn.Pressed += () => OnOverrideInheritedComponent(node, capturedSlotName, capturedParentCompHash);
            topRow.AddChild(overrideBtn);
        }

        var stripToggleBtn = new Button
        {
            Text = slot.IsStrippedByNode ? "↺ Restore" : "✕ Strip",
            TooltipText = slot.IsStrippedByNode
                ? "Restore this inherited component by removing RemoveComponent (0x9B41D00A) from this node"
                : "Strip this inherited component from this node using native RemoveComponent (0x9B41D00A)"
        };
        stripToggleBtn.AddThemeFontSizeOverride("font_size", 10);
        bool targetStripState = !slot.IsStrippedByNode;
        stripToggleBtn.Pressed += () => OnToggleStripInheritedComponent(node, capturedSlotName, targetStripState);
        topRow.AddChild(stripToggleBtn);

        vbox.AddChild(topRow);

        var fromRow = new HBoxContainer();
        fromRow.AddThemeConstantOverride("separation", 4);
        var sourceBtn = new Button
        {
            Text = $"From: {slot.SourceNodeLabel} (0x{slot.SourceNodeHash:X8})",
            TooltipText = "Click to inspect the ancestor node that provides this component",
            Flat = true
        };
        sourceBtn.AddThemeFontSizeOverride("font_size", 10);
        sourceBtn.Modulate = new Color(0.55f, 0.78f, 0.95f);
        uint sourceHash = slot.SourceNodeHash;
        sourceBtn.Pressed += () =>
        {
            var anc = _graph.FindNodeByHash(sourceHash);
            if (anc is not null) _graph.SelectNode(anc);
        };
        fromRow.AddChild(sourceBtn);
        vbox.AddChild(fromRow);

        if (slot.Properties.Count > 0 && !slot.IsStrippedByNode)
        {
            var nonEmptyProps = slot.Properties
                .Where(p => !string.IsNullOrWhiteSpace(p.CurrentValue))
                .ToList();
            var summaryParts = nonEmptyProps
                .Take(8)
                .Select(p => $"{p.PropertyName}: {p.CurrentValue}");
            string previewText = string.Join("  |  ", summaryParts);
            if (nonEmptyProps.Count > 8)
            {
                previewText += $"  (+{nonEmptyProps.Count - 8})";
            }

            var previewLabel = new Label
            {
                Text = previewText,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.55f, 0.60f, 0.68f)
            };
            previewLabel.AddThemeFontSizeOverride("font_size", 10);
            vbox.AddChild(previewLabel);
        }

        card.AddChild(vbox);
        return card;
    }

    private void OnRemoveOrRevertDirectComponent(ItemGraphNode node, NodeComponentSlot slot)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected.", isError: true);
            return;
        }

        if (!EditorModValueService.RemoveOrRevertDirectComponent(
                node.RecordHash, slot.SlotHash, slot.ComponentRecordHash, activeMod, out string err))
        {
            ShowStatus($"Failed to remove/revert component: {err}", isError: true);
            return;
        }

        EditorModValueService.PruneUnreferencedModAssets(activeMod);
        ReloadGraphAndReselect(node.RecordHash);
        ShowStatus($"Removed/reverted '{slot.SlotName}' on '{node.Label}'.", isError: false);
    }

    private void OnToggleStripInheritedComponent(ItemGraphNode node, string slotName, bool stripped)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        if (!EditorModValueService.SetInheritedComponentStripped(
                node.RecordHash, slotName, stripped, activeMod, out string err))
        {
            ShowStatus($"Failed to update RemoveComponent: {err}", isError: true);
            return;
        }

        ReloadGraphAndReselect(node.RecordHash);
        ShowStatus(
            stripped
                ? $"Stripped inherited '{slotName}' from '{node.Label}' via RemoveComponent (0x9B41D00A)."
                : $"Restored inherited '{slotName}' on '{node.Label}'.",
            isError: false);
    }

    private void OnDeleteCustomNodePressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || !node.IsCustomModNode) return;

        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected.", isError: true);
            return;
        }

        uint parentHash = node.ParentHash != 0 ? node.ParentHash : Engine_RecordNameCatalog.WorldEntityRootHash;
        string deletedLabel = node.Label;
        uint deletedHash = node.RecordHash;

        if (!EditorModValueService.DeleteCustomNode(deletedHash, activeMod, out string err))
        {
            ShowStatus($"Failed to delete custom node: {err}", isError: true);
            return;
        }

        EditorModValueService.PruneUnreferencedModAssets(activeMod);
        ReloadGraphAndReselect(parentHash);
        ShowStatus($"Deleted custom node '{deletedLabel}' [0x{deletedHash:X8}] from mod '{activeMod}'.", isError: false);
    }

    private void OnOverrideInheritedComponent(ItemGraphNode node, string slotName, uint parentCompHash)
    {
        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        if (!EditorModValueService.AttachOrOverrideComponent(
                node.RecordHash, slotName, parentCompHash, activeMod, out uint newCompHash, out string err))
        {
            ShowStatus($"Override failed: {err}", isError: true);
            return;
        }

        ReloadGraphAndReselect(node.RecordHash);
        ShowStatus($"Overrode '{slotName}' [0x{newCompHash:X8}] on '{node.Label}'.", isError: false);
    }

    private void OnAttachComponentPressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || node.RecordHash == 0 || _attachComponentDropdown is null) return;

        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected. Create or select a mod first.", isError: true);
            return;
        }

        int sel = _attachComponentDropdown.Selected;
        if (sel < 0 || sel >= _attachComponentDropdown.ItemCount) return;

        string meta = _attachComponentDropdown.GetItemMetadata(sel).AsString();
        string slotName = meta == "__CUSTOM__"
            ? (_customComponentNameInput?.Text.Trim() ?? string.Empty)
            : meta;

        if (string.IsNullOrWhiteSpace(slotName))
        {
            ShowStatus("Enter a component slot name to attach.", isError: true);
            return;
        }

        // Check if this node already inherits this component from an ancestor so we can link parentComponentRecordHash:
        uint slotHash = Engine_GameHash.Fnv1(slotName);
        NodeComponentSlot? inherited = node.InheritedComponents.FirstOrDefault(c => c.SlotHash == slotHash);
        uint? parentCompHash = inherited?.ComponentRecordHash;

        if (!EditorModValueService.AttachOrOverrideComponent(
                node.RecordHash, slotName, parentCompHash, activeMod, out uint newCompHash, out string err))
        {
            ShowStatus($"Attach failed: {err}", isError: true);
            return;
        }

        if (_customComponentNameInput is not null)
        {
            _customComponentNameInput.Text = "";
        }

        ReloadGraphAndReselect(node.RecordHash);
        ShowStatus($"Attached '{slotName}' [0x{newCompHash:X8}] to '{node.Label}'.", isError: false);
    }

    private string _defaultAutoRecordName = string.Empty;

    private void OnSaveModPressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || node.RecordHash == 0) return;

        string activeMod = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(activeMod))
        {
            ShowStatus("No active mod selected.", isError: true);
            return;
        }

        RunWithLoadingDialog(
            "💾 Saving Mod Overrides...",
            $"Writing GDB component overrides and pruning unused assets in '{activeMod}{Refii.Core.Engine_Title.Active.ModExtension}'...",
            () =>
            {
                try
                {
                    // If the user typed a custom component name in the Attach Component bar and clicked Save directly, attach it first:
                    if (_attachComponentDropdown is not null && _customComponentNameInput is not null && _customComponentNameInput.Visible)
                    {
                        string pendingCustomSlot = _customComponentNameInput.Text.Trim();
                        if (!string.IsNullOrWhiteSpace(pendingCustomSlot))
                        {
                            uint slotHash = Engine_GameHash.Fnv1(pendingCustomSlot);
                            NodeComponentSlot? inherited = node.InheritedComponents.FirstOrDefault(c => c.SlotHash == slotHash);
                            if (EditorModValueService.AttachOrOverrideComponent(
                                    node.RecordHash, pendingCustomSlot, inherited?.ComponentRecordHash, activeMod, out _, out _))
                            {
                                _customComponentNameInput.Text = "";
                            }
                        }
                    }

                    var allDirectProps = new List<ModItemProperty>();
                    foreach (var comp in node.DirectComponents)
                    {
                        allDirectProps.AddRange(comp.Properties);
                    }
                    foreach (var directScalar in node.Properties.Where(p => p.TargetRecordHash == node.RecordHash))
                    {
                        allDirectProps.Add(directScalar);
                    }

                    EditorModValueService.SaveNodeComponentOverrides(node.RecordHash, allDirectProps, activeMod, node.Label);
                    EditorModValueService.PruneUnreferencedModAssets(activeMod);
                    ReloadGraphAndReselect(node.RecordHash);
                    ShowStatus($"Saved changes for '{node.Label}' [0x{node.RecordHash:X8}] to mod '{activeMod}'.", isError: false);
                }
                catch (Exception ex)
                {
                    ShowStatus($"Save failed: {ex.Message}", isError: true);
                }
            });
    }

    private void OnRevertPropsPressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null) return;

        foreach (var comp in node.DirectComponents)
        {
            foreach (var p in comp.Properties)
            {
                p.CurrentValue = p.VanillaValue;
                p.IsModified = false;
            }
        }

        foreach (var p in node.Properties)
        {
            p.CurrentValue = p.VanillaValue;
            p.IsModified = false;
        }

        PopulateComponentStack(node);
        ShowStatus("Reverted unsaved edits.", isError: false);
    }

    private void OnOpenFullEditorPressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node?.Detail is null) return;

        uint currentHash = node.RecordHash;
        var dlg = new ModItemEditorDialog();
        dlg.TreeExited += () => ReloadGraphAndReselect(currentHash);
        AddChild(dlg);
        dlg.OpenForItem(node.Detail);
    }

    private void BuildCreateNodeDialog()
    {
        _createNodeDialog = new ConfirmationDialog
        {
            Title = "Create Child Node",
            WrapControls = false,
            MinSize = new Vector2I(520, 340),
            MaxSize = new Vector2I(680, 440),
            Size = new Vector2I(560, 370),
            OkButtonText = "Create Child Node"
        };

        var vbox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(520, 0)
        };
        vbox.AddThemeConstantOverride("separation", 8);

        _createParentInfoLabel = new Label
        {
            Text = "Parent Node:",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            Modulate = new Color(0.70f, 0.80f, 0.95f)
        };
        _createParentInfoLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_createParentInfoLabel);

        var hintLabel = new Label
        {
            Text = "Inherits all components from its parent chain. Leave Display Name blank to inherit the parent's display name.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.60f, 0.65f, 0.72f)
        };
        hintLabel.AddThemeFontSizeOverride("font_size", 10);
        vbox.AddChild(hintLabel);

        var nameLabel = new Label { Text = "Display Name (Optional — leave blank to inherit parent name):" };
        nameLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(nameLabel);

        _createDisplayNameInput = new LineEdit { PlaceholderText = "Optional — e.g. Dragonbreath Flintlock Rifle" };
        _createDisplayNameInput.TextChanged += text =>
        {
            if (_createRecordNameInput is not null)
            {
                string currentRec = _createRecordNameInput.Text.Trim();
                bool isAutoOrEmpty = string.IsNullOrEmpty(currentRec) ||
                                     currentRec.Equals(_defaultAutoRecordName, StringComparison.OrdinalIgnoreCase) ||
                                     currentRec.StartsWith("ObjectInventory_", StringComparison.OrdinalIgnoreCase);
                string sanitized = text.Replace(" ", "").Replace("'", "").Replace("-", "");
                if (isAutoOrEmpty)
                {
                    _createRecordNameInput.Text = !string.IsNullOrEmpty(sanitized)
                        ? "ObjectInventory_" + sanitized
                        : _defaultAutoRecordName;
                }
            }
        };
        vbox.AddChild(_createDisplayNameInput);

        var recLabel = new Label { Text = "Internal GDB Record Name:" };
        recLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(recLabel);

        _createRecordNameInput = new LineEdit { PlaceholderText = "e.g. ObjectInventory_CustomNode" };
        vbox.AddChild(_createRecordNameInput);

        var descLabel = new Label { Text = "Description (Optional):" };
        descLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(descLabel);

        _createDescriptionInput = new LineEdit { PlaceholderText = "Optional in-game description text" };
        vbox.AddChild(_createDescriptionInput);

        _createDialogErrorLabel = new Label
        {
            Text = "",
            Modulate = new Color(1.0f, 0.45f, 0.45f)
        };
        _createDialogErrorLabel.AddThemeFontSizeOverride("font_size", 10);
        vbox.AddChild(_createDialogErrorLabel);

        _createNodeDialog.AddChild(vbox);
        _createNodeDialog.Confirmed += OnConfirmCreateChildNode;
        AddChild(_createNodeDialog);
    }

    private void OnCreateChildNodePressed()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || node.RecordHash == 0) return;

        string parentPrefix = !string.IsNullOrWhiteSpace(node.InternalName)
            ? node.InternalName
            : $"ObjectInventory_Node_{node.RecordHash:X8}";
        int suffix = node.Children.Count + 1;
        string candidate;
        do
        {
            candidate = $"{parentPrefix}_Child_{suffix}";
            suffix++;
        } while (_graph.FindNodeByHash(Engine_GameHash.Fnv1(candidate)) is not null);
        _defaultAutoRecordName = candidate;

        if (_createParentInfoLabel is not null)
        {
            _createParentInfoLabel.Text = $"Branching child off: {node.Label} (0x{node.RecordHash:X8})";
        }
        if (_createDisplayNameInput is not null) _createDisplayNameInput.Text = "";
        if (_createRecordNameInput is not null)
        {
            _createRecordNameInput.Text = _defaultAutoRecordName;
            _createRecordNameInput.PlaceholderText = _defaultAutoRecordName;
        }
        if (_createDescriptionInput is not null) _createDescriptionInput.Text = "";
        if (_createDialogErrorLabel is not null) _createDialogErrorLabel.Text = "";

        if (_createNodeDialog is not null)
        {
            _createNodeDialog.Size = new Vector2I(560, 370);
            _createNodeDialog.PopupCentered(new Vector2I(560, 370));
        }
    }

    private void OnConfirmCreateChildNode()
    {
        ItemGraphNode node = _graph.SelectedNode;
        if (node is null || node.RecordHash == 0) return;

        string displayName = _createDisplayNameInput?.Text.Trim() ?? string.Empty;
        string recordName = _createRecordNameInput?.Text.Trim() ?? string.Empty;
        string description = _createDescriptionInput?.Text.Trim() ?? string.Empty;

        string activeMod = EditorModExporter.ActiveModName;
        if (!EditorModValueService.CreateChildNode(
                node.RecordHash, recordName, displayName, description, activeMod, out uint newHash, out string err))
        {
            ShowStatus($"Failed to create child node: {err}", isError: true);
            return;
        }

        ReloadGraphAndReselect(newHash);
        ItemGraphNode? createdNode = _graph.FindNodeByHash(newHash);
        string shownLabel = createdNode?.Label ?? (!string.IsNullOrWhiteSpace(displayName) ? displayName : recordName);
        ShowStatus($"Created child node '{shownLabel}' [0x{newHash:X8}] under '{node.Label}'.", isError: false);
    }

    private void BuildAddPropertyDialog()
    {
        _addPropertyDialog = new ConfirmationDialog
        {
            Title = "Add Component Property",
            WrapControls = false,
            MinSize = new Vector2I(460, 290),
            MaxSize = new Vector2I(580, 380),
            Size = new Vector2I(480, 310),
            OkButtonText = "Add Property"
        };

        var vbox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(440, 0)
        };
        vbox.AddThemeConstantOverride("separation", 7);

        _addPropertyTargetLabel = new Label
        {
            Text = "Component:",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            Modulate = new Color(0.70f, 0.80f, 0.95f)
        };
        _addPropertyTargetLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_addPropertyTargetLabel);

        var nameLbl = new Label { Text = "Property Name (hashed via FNV-1a):" };
        nameLbl.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(nameLbl);

        _addPropertyNameInput = new LineEdit { PlaceholderText = "e.g. DamageMultiplier or CustomLuaPower" };
        vbox.AddChild(_addPropertyNameInput);

        var typeLbl = new Label { Text = "Field Type:" };
        typeLbl.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(typeLbl);

        _addPropertyTypeDropdown = new OptionButton();
        _addPropertyTypeDropdown.AddItem("Float", (int)GdbFieldType.Float);
        _addPropertyTypeDropdown.AddItem("Int", (int)GdbFieldType.Int);
        _addPropertyTypeDropdown.AddItem("Bool", (int)GdbFieldType.Bool);
        _addPropertyTypeDropdown.AddItem("Enum", (int)GdbFieldType.Enum);
        _addPropertyTypeDropdown.AddItem("String / Symbol", (int)GdbFieldType.String);
        _addPropertyTypeDropdown.AddItem("Asset / Resource Reference (.mdl / .tex)", (int)GdbFieldType.ResourceReference);
        vbox.AddChild(_addPropertyTypeDropdown);

        var valLbl = new Label { Text = "Initial Value:" };
        valLbl.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(valLbl);

        _addPropertyValueInput = new LineEdit { Text = "0" };
        vbox.AddChild(_addPropertyValueInput);

        _addPropertyErrorLabel = new Label
        {
            Text = "",
            Modulate = new Color(1.0f, 0.45f, 0.45f)
        };
        _addPropertyErrorLabel.AddThemeFontSizeOverride("font_size", 10);
        vbox.AddChild(_addPropertyErrorLabel);

        _addPropertyDialog.AddChild(vbox);
        _addPropertyDialog.Confirmed += OnConfirmAddProperty;
        AddChild(_addPropertyDialog);
    }

    private void OpenAddPropertyDialog(uint compRecordHash, string slotName)
    {
        _addPropertyTargetCompHash = compRecordHash;
        if (_addPropertyTargetLabel is not null)
        {
            _addPropertyTargetLabel.Text = $"Target Component: {slotName} (0x{compRecordHash:X8})";
        }
        if (_addPropertyNameInput is not null) _addPropertyNameInput.Text = "";
        if (_addPropertyValueInput is not null) _addPropertyValueInput.Text = "0";
        if (_addPropertyErrorLabel is not null) _addPropertyErrorLabel.Text = "";

        if (_addPropertyDialog is not null)
        {
            _addPropertyDialog.Size = new Vector2I(480, 310);
            _addPropertyDialog.PopupCentered(new Vector2I(480, 310));
        }
    }

    private void OnConfirmAddProperty()
    {
        if (_addPropertyTargetCompHash == 0) return;

        string propName = _addPropertyNameInput?.Text.Trim() ?? string.Empty;
        string propVal = _addPropertyValueInput?.Text.Trim() ?? "0";
        int selIdx = _addPropertyTypeDropdown?.Selected ?? 0;
        GdbFieldType fieldType = _addPropertyTypeDropdown is not null && selIdx >= 0
            ? (GdbFieldType)_addPropertyTypeDropdown.GetItemId(selIdx)
            : GdbFieldType.Float;

        string activeMod = EditorModExporter.ActiveModName;
        uint currentNodeHash = _graph.SelectedNode.RecordHash;

        if (!EditorModValueService.AddOrUpdateComponentProperty(
                _addPropertyTargetCompHash, propName, fieldType, propVal, activeMod, out string err))
        {
            ShowStatus($"Failed to add property: {err}", isError: true);
            return;
        }

        ReloadGraphAndReselect(currentNodeHash);
        ShowStatus($"Added property '{propName}' to component 0x{_addPropertyTargetCompHash:X8}.", isError: false);
    }

    private void EnsureLoadingDialog()
    {
        if (_loadingDialog is not null && GodotObject.IsInstanceValid(_loadingDialog))
        {
            return;
        }

        _loadingDialog = new Window
        {
            Title = "Refii Mod Studio",
            Exclusive = true,
            Transient = true,
            Unresizable = true,
            Borderless = false,
            Visible = false,
            Size = new Vector2I(480, 145),
            MinSize = new Vector2I(460, 135)
        };

        var bgPanel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        bgPanel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.09f, 0.11f, 0.15f),
            BorderColor = new Color(0.28f, 0.56f, 0.90f),
            BorderWidthLeft = 2,
            BorderWidthRight = 2,
            BorderWidthTop = 2,
            BorderWidthBottom = 2,
            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 16,
            ContentMarginBottom = 16
        };
        bgPanel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter
        };
        vbox.AddThemeConstantOverride("separation", 8);

        _loadingTitleLabel = new Label
        {
            Text = "⏳ Working...",
            Modulate = new Color(0.92f, 0.96f, 1.0f)
        };
        _loadingTitleLabel.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(_loadingTitleLabel);

        _loadingDetailLabel = new Label
        {
            Text = "Please wait while Mod Studio processes your assets...",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.68f, 0.76f, 0.88f)
        };
        _loadingDetailLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_loadingDetailLabel);

        var progressBar = new ProgressBar
        {
            Indeterminate = true,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 14)
        };
        vbox.AddChild(progressBar);

        bgPanel.AddChild(vbox);
        _loadingDialog.AddChild(bgPanel);
        AddChild(_loadingDialog);
    }

    private async void RunWithLoadingDialog(string title, string detail, Action work)
    {
        if (!IsInsideTree() ||
            string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase))
        {
            work();
            return;
        }

        EnsureLoadingDialog();
        if (_loadingTitleLabel is not null) _loadingTitleLabel.Text = title;
        if (_loadingDetailLabel is not null) _loadingDetailLabel.Text = detail;
        ShowStatus(title, isError: false);

        _loadingDialog?.PopupCentered(new Vector2I(480, 145));

        // Allow two editor frames so the modal dialog renders visibly on screen before synchronous I/O begins
        SceneTree? tree = GetTree();
        if (tree is not null)
        {
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        try
        {
            work();
        }
        finally
        {
            if (_loadingDialog is not null && GodotObject.IsInstanceValid(_loadingDialog))
            {
                _loadingDialog.Hide();
            }
        }
    }

    private void OpenGmdSocketsInspectorDialog(ItemGraphNode node, ModItemProperty modelProp)
    {
        if (_gmdSocketsDialog is null || !GodotObject.IsInstanceValid(_gmdSocketsDialog))
        {
            _gmdSocketsDialog = new AcceptDialog
            {
                Title = "🎯 Model Attachment Sockets (.mdl.gmd)",
                WrapControls = false,
                Size = new Vector2I(760, 560),
                MinSize = new Vector2I(680, 440),
                OkButtonText = "Close"
            };

            var scroll = new ScrollContainer
            {
                CustomMinimumSize = new Vector2(720, 480),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical = Control.SizeFlags.ExpandFill
            };

            _gmdSocketsContentBox = new VBoxContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _gmdSocketsContentBox.AddThemeConstantOverride("separation", 8);
            scroll.AddChild(_gmdSocketsContentBox);
            _gmdSocketsDialog.AddChild(scroll);
            AddChild(_gmdSocketsDialog);
        }

        if (_gmdSocketsContentBox is null) return;
        foreach (Node child in _gmdSocketsContentBox.GetChildren())
        {
            child.QueueFree();
        }

        string activeMod = EditorModExporter.ActiveModName;
        string normalizedModel = (modelProp.CurrentValue ?? string.Empty).Replace('\\', '/').TrimStart('/');
        string gmdPath = normalizedModel + ".gmd";

        byte[]? gmdBytes = EditorModValueService.ReadVirtualAssetBytes(
            gmdPath, activeMod, includeActiveModPackage: true, BnkMountPlugin.Instance?.FileSystem);
        if (gmdBytes is null || gmdBytes.Length == 0)
        {
            string vanillaModel = (modelProp.VanillaValue ?? string.Empty).Replace('\\', '/').TrimStart('/');
            if (!string.IsNullOrWhiteSpace(vanillaModel))
            {
                gmdBytes = EditorModValueService.ReadVirtualAssetBytes(
                    vanillaModel + ".gmd", activeMod, includeActiveModPackage: false, BnkMountPlugin.Instance?.FileSystem);
            }
        }

        var gmd = gmdBytes is not null && gmdBytes.Length > 0
            ? Refii.Models.Engine_GmdSkeleton.TryParse(gmdBytes)
            : null;

        var headerLbl = new Label
        {
            Text = $"Model: {normalizedModel}\nSidecar: {gmdPath} ({(gmd is not null ? $"{gmd.Attachments.Length} socket(s), {gmd.Names.Length} bone(s)" : "No .mdl.gmd sidecar found")})",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.85f, 0.92f, 1.0f)
        };
        headerLbl.AddThemeFontSizeOverride("font_size", 12);
        _gmdSocketsContentBox.AddChild(headerLbl);

        var infoLbl = new Label
        {
            Text = "💡 When you click 📤 Export on this model, these sockets are exported into the .glb under a 'GmdDummies' Empty hierarchy. " +
                   "You can move/rotate them in Blender (or use res://modding_tools/refii_blender_tools.py) and drag the .glb back to update the .mdl.gmd sidecar!",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.65f, 0.78f, 0.92f)
        };
        infoLbl.AddThemeFontSizeOverride("font_size", 11);
        _gmdSocketsContentBox.AddChild(infoLbl);

        var activeTitle = new Label
        {
            Text = "Active Sockets on This Model:",
            Modulate = new Color(0.45f, 0.92f, 0.65f)
        };
        activeTitle.AddThemeFontSizeOverride("font_size", 12);
        _gmdSocketsContentBox.AddChild(activeTitle);

        if (gmd is not null && gmd.Attachments.Length > 0)
        {
            foreach (var att in gmd.Attachments)
            {
                Transform3D gltfXform = gmd.ComposeAttachment(att);
                Vector3 pos = gltfXform.Origin;

                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);

                var nameEdit = new LineEdit
                {
                    Text = att.Name,
                    Editable = false,
                    CustomMinimumSize = new Vector2(240, 0),
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
                };
                nameEdit.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(nameEdit);

                var coordLbl = new Label
                {
                    Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "glTF Pos: ({0:F3}, {1:F3}, {2:F3}) · Bone #{3}",
                        pos.X, pos.Y, pos.Z, att.Node),
                    Modulate = new Color(0.72f, 0.78f, 0.86f)
                };
                coordLbl.AddThemeFontSizeOverride("font_size", 10);
                row.AddChild(coordLbl);

                string capturedName = att.Name;
                var copyBtn = new Button
                {
                    Text = "📋 Copy Name",
                    CustomMinimumSize = new Vector2(95, 22)
                };
                copyBtn.AddThemeFontSizeOverride("font_size", 10);
                copyBtn.Pressed += () =>
                {
                    DisplayServer.ClipboardSet(capturedName);
                    ShowStatus($"Copied socket name '{capturedName}' to clipboard.", isError: false);
                };
                row.AddChild(copyBtn);

                _gmdSocketsContentBox.AddChild(row);
            }
        }
        else
        {
            var emptyLbl = new Label
            {
                Text = "No attachment sockets found in this model's .mdl.gmd sidecar.",
                Modulate = new Color(0.65f, 0.68f, 0.74f)
            };
            emptyLbl.AddThemeFontSizeOverride("font_size", 11);
            _gmdSocketsContentBox.AddChild(emptyLbl);
        }

        _gmdSocketsContentBox.AddChild(new HSeparator());

        var refTitle = new Label
        {
            Text = "Standard Fable II Attachment Socket Catalog (Click 📋 Copy to use in Blender):",
            Modulate = new Color(0.95f, 0.80f, 0.42f)
        };
        refTitle.AddThemeFontSizeOverride("font_size", 12);
        _gmdSocketsContentBox.AddChild(refTitle);

        foreach (var entry in Refii.Models.Engine_GmdWriter.StandardSockets)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var catLbl = new Label
            {
                Text = $"[{entry.Category}]",
                CustomMinimumSize = new Vector2(105, 0),
                Modulate = new Color(0.58f, 0.78f, 0.96f)
            };
            catLbl.AddThemeFontSizeOverride("font_size", 10);
            row.AddChild(catLbl);

            var nameLbl = new Label
            {
                Text = entry.Name,
                CustomMinimumSize = new Vector2(215, 0),
                Modulate = new Color(0.92f, 0.95f, 1.0f)
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(nameLbl);

            var descLbl = new Label
            {
                Text = entry.Description,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                Modulate = new Color(0.68f, 0.73f, 0.80f)
            };
            descLbl.AddThemeFontSizeOverride("font_size", 10);
            row.AddChild(descLbl);

            string capturedSocket = entry.Name;
            var copyBtn = new Button
            {
                Text = "📋 Copy",
                CustomMinimumSize = new Vector2(68, 22)
            };
            copyBtn.AddThemeFontSizeOverride("font_size", 10);
            copyBtn.Pressed += () =>
            {
                DisplayServer.ClipboardSet(capturedSocket);
                ShowStatus($"Copied '{capturedSocket}' to clipboard.", isError: false);
            };
            row.AddChild(copyBtn);

            _gmdSocketsContentBox.AddChild(row);
        }

        _gmdSocketsDialog.PopupCentered(new Vector2I(760, 560));
    }

    private void ReloadGraphAndReselect(uint targetNodeHash)
    {
        string prevDomain = _graph.DomainFilter;
        BuildGraphModel();
        _graph.SetDomainFilter(prevDomain);

        ItemGraphNode? found = _graph.FindNodeByHash(targetNodeHash);
        if (found is not null)
        {
            _graph.SelectNode(found);
        }

        PopulateTree();
        UpdateInspector();
    }

    private void ShowStatus(string message, bool isError)
    {
        if (_saveStatusLabel is null) return;
        _saveStatusLabel.Text = message;
        _saveStatusLabel.Modulate = isError ? new Color(1.0f, 0.45f, 0.45f) : new Color(0.45f, 0.92f, 0.55f);
    }

    /// <summary>
    /// Specialized <see cref="LineEdit"/> that accepts file drag-and-drop payloads from Godot's <c>FileSystemDock</c>
    /// (e.g. <c>res://creator/&lt;ActiveMod&gt;/...</c>), validates allowed asset file extensions, and renders
    /// a prominent interactive drop-zone badge so creators immediately recognize it as a drag-and-drop target.
    /// </summary>
    public sealed partial class AssetDropLineEdit : LineEdit
    {
        private readonly AssetFieldKind _allowedKind;
        private bool _isHovered;
        private bool _isDragHovering;

        /// <summary>Raised when a valid asset file is dropped onto the control.</summary>
        public event Action<string>? AssetDropped;

        /// <summary>Raised when a file with an unsupported extension is dropped.</summary>
        public event Action<string>? InvalidAssetDropped;

        public AssetDropLineEdit() : this(AssetFieldKind.Model)
        {
        }

        public AssetDropLineEdit(AssetFieldKind allowedKind)
        {
            _allowedKind = allowedKind;
            ApplyDropZoneStyling(highlighted: false);

            MouseEntered += () =>
            {
                _isHovered = true;
                ApplyDropZoneStyling(highlighted: true);
                QueueRedraw();
            };
            MouseExited += () =>
            {
                _isHovered = false;
                _isDragHovering = false;
                ApplyDropZoneStyling(highlighted: false);
                QueueRedraw();
            };
        }

        private void ApplyDropZoneStyling(bool highlighted)
        {
            bool isModel = _allowedKind == AssetFieldKind.Model;
            Color accent = isModel
                ? (highlighted ? new Color(0.32f, 0.85f, 1.0f) : new Color(0.24f, 0.62f, 0.92f))
                : (highlighted ? new Color(0.75f, 0.58f, 1.0f) : new Color(0.52f, 0.42f, 0.85f));
            Color bg = isModel
                ? (highlighted ? new Color(0.10f, 0.19f, 0.28f) : new Color(0.07f, 0.13f, 0.20f))
                : (highlighted ? new Color(0.15f, 0.14f, 0.26f) : new Color(0.10f, 0.10f, 0.18f));

            var box = new StyleBoxFlat
            {
                BgColor = bg,
                BorderColor = accent,
                BorderWidthLeft = 3,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusTopLeft = 4,
                CornerRadiusBottomLeft = 4,
                CornerRadiusTopRight = 4,
                CornerRadiusBottomRight = 4,
                ContentMarginLeft = 7,
                ContentMarginRight = 78,
                ContentMarginTop = 3,
                ContentMarginBottom = 3
            };

            AddThemeStyleboxOverride("normal", box);
            AddThemeStyleboxOverride("read_only", box);

            var focusBox = (StyleBoxFlat)box.Duplicate();
            focusBox.BorderColor = new Color(0.38f, 0.95f, 0.72f);
            focusBox.BorderWidthTop = 1;
            focusBox.BorderWidthRight = 1;
            focusBox.BorderWidthBottom = 1;
            AddThemeStyleboxOverride("focus", focusBox);
        }

        public override void _Notification(int what)
        {
            base._Notification(what);
            if (what == NotificationDragEnd)
            {
                if (_isDragHovering)
                {
                    _isDragHovering = false;
                    ApplyDropZoneStyling(highlighted: _isHovered);
                    QueueRedraw();
                }
            }
        }

        public override void _Draw()
        {
            base._Draw();

            Font? font = GetThemeDefaultFont();
            if (font is null) return;

            bool isModel = _allowedKind == AssetFieldKind.Model;
            string badgeText = _isDragHovering
                ? "📥 DROP NOW"
                : isModel
                    ? "📥 DROP .GLB"
                    : "📥 DROP .PNG";

            int fontSize = 9;
            Vector2 textSize = font.GetStringSize(badgeText, HorizontalAlignment.Left, -1, fontSize);
            float padX = 5f;
            float padY = 2f;
            float badgeW = textSize.X + padX * 2f;
            float badgeH = Mathf.Min(Size.Y - 4f, textSize.Y + padY * 2f);
            float badgeX = Size.X - badgeW - 4f;
            float badgeY = (Size.Y - badgeH) * 0.5f;

            if (badgeX < 36f)
            {
                return;
            }

            Color pillBg = _isDragHovering
                ? new Color(0.16f, 0.62f, 0.42f, 0.95f)
                : isModel
                    ? new Color(0.14f, 0.36f, 0.56f, 0.92f)
                    : new Color(0.28f, 0.22f, 0.52f, 0.92f);
            Color pillBorder = _isDragHovering
                ? new Color(0.40f, 1.0f, 0.72f, 1.0f)
                : isModel
                    ? new Color(0.36f, 0.75f, 1.0f, 0.85f)
                    : new Color(0.68f, 0.55f, 0.98f, 0.85f);

            var rect = new Rect2(badgeX, badgeY, badgeW, badgeH);
            DrawRect(rect, pillBg, filled: true);
            DrawRect(rect, pillBorder, filled: false, width: 1.0f);

            float baselineY = badgeY + (badgeH + textSize.Y * 0.68f) * 0.5f - 1f;
            DrawString(
                font,
                new Vector2(badgeX + padX, baselineY),
                badgeText,
                HorizontalAlignment.Left,
                -1,
                fontSize,
                new Color(0.94f, 0.98f, 1.0f));
        }

        public static bool CanAcceptDroppedPath(AssetFieldKind allowedKind, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return EditorModValueService.IsAllowedAssetExtension(allowedKind, path);
        }

        public override bool _CanDropData(Vector2 atPosition, Variant data)
        {
            string? path = ExtractFirstDroppedFilePath(data);
            bool ok = CanAcceptDroppedPath(_allowedKind, path);
            if (ok != _isDragHovering)
            {
                _isDragHovering = ok;
                ApplyDropZoneStyling(highlighted: ok || _isHovered);
                QueueRedraw();
            }
            return ok;
        }

        public override void _DropData(Vector2 atPosition, Variant data)
        {
            _isDragHovering = false;
            ApplyDropZoneStyling(highlighted: _isHovered);
            QueueRedraw();

            string? path = ExtractFirstDroppedFilePath(data);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!CanAcceptDroppedPath(_allowedKind, path))
            {
                InvalidAssetDropped?.Invoke(path);
                return;
            }

            AssetDropped?.Invoke(path);
        }

        public static string? ExtractFirstDroppedFilePath(Variant data)
        {
            if (data.VariantType == Variant.Type.Dictionary)
            {
                Godot.Collections.Dictionary dict = data.AsGodotDictionary();
                if (dict.TryGetValue("files", out Variant filesVar))
                {
                    if (filesVar.VariantType == Variant.Type.PackedStringArray)
                    {
                        string[] files = filesVar.AsStringArray();
                        if (files is not null && files.Length > 0 && !string.IsNullOrWhiteSpace(files[0]))
                        {
                            return files[0];
                        }
                    }
                    else if (filesVar.VariantType == Variant.Type.Array)
                    {
                        Godot.Collections.Array arr = filesVar.AsGodotArray();
                        if (arr.Count > 0)
                        {
                            string s = arr[0].AsString();
                            if (!string.IsNullOrWhiteSpace(s))
                            {
                                return s;
                            }
                        }
                    }
                }

                if (dict.TryGetValue("path", out Variant pathVar))
                {
                    string p = pathVar.AsString();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        return p;
                    }
                }
            }
            else if (data.VariantType == Variant.Type.String)
            {
                string s = data.AsString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            return null;
        }
    }
}
#endif
