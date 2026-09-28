#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Assets;
using Refii.Equipment;
using Refii.Gdb;

namespace Refii.EditorTools;

/// <summary>
/// Modal dialog allowing modders to edit genuine GDB properties and Babel localization text for an item
/// directly from the Godot editor, organized into clean component tabs (General & Lore, Shop & Pricing,
/// Inventory & Rules, Combat & Weapon, Augmentations, Appearance, Item Definition).
/// </summary>
public partial class ModItemEditorDialog : ConfirmationDialog
{
    private ItemDetail? _currentDetail;
    private ItemModValues? _currentValues;

    // Header labels:
    private Label? _headerLabel;
    private Label? _subHeaderLabel;
    private Label? _modBadgeLabel;

    // Search and tab navigation controls:
    private LineEdit? _searchEdit;
    private Button? _prevTabBtn;
    private Button? _nextTabBtn;
    private Label? _tabNavLabel;
    private TabContainer? _tabContainer;

    // General & Lore controls:
    private Label? _nameLabel;
    private LineEdit? _nameEdit;
    private Button? _resetNameBtn;
    private Label? _descLabel;
    private TextEdit? _descEdit;
    private Button? _resetDescBtn;

    // Action button:
    private Button? _revertButton;

    // Dynamic row tracking:
    private readonly List<PropRow> _rowControls = new();
    private readonly List<TabInfo> _dynamicTabs = new();

    /// <summary>
    /// Metadata and control handles for a single editable property row in a category tab.
    /// </summary>
    private sealed class PropRow
    {
        public ModItemProperty Property { get; set; } = null!;
        public Label LabelNode { get; set; } = null!;
        public Control EditorControl { get; set; } = null!;
        public Button ResetButton { get; set; } = null!;
        public Func<string> GetValue { get; set; } = null!;
        public Action<string> SetValue { get; set; } = null!;
        public int TabIndex { get; set; }
    }

    /// <summary>
    /// Metadata for a dynamic category tab generated for an item component.
    /// </summary>
    private sealed class TabInfo
    {
        public string CategoryId { get; set; } = string.Empty;
        public string BaseTitle { get; set; } = string.Empty;
        public int TabIndex { get; set; }
        public Control TabControl { get; set; } = null!;
    }

    public ModItemEditorDialog()
    {
        Title = "Edit Item in Mod";
        Size = new Vector2I(780, 720);
        MinSize = new Vector2I(680, 560);
        Unresizable = false;
        OkButtonText = "Save to Mod";

        var rootBox = new VBoxContainer();
        rootBox.AddThemeConstantOverride("separation", 10);
        rootBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rootBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // 1. Header with Name, Record Hash, and Active Mod Badge:
        var headerBox = new VBoxContainer();
        _headerLabel = new Label
        {
            Text = "Item Name",
        };
        _headerLabel.AddThemeFontSizeOverride("font_size", 16);

        _subHeaderLabel = new Label
        {
            Text = "Record Hash: 0x00000000",
            Modulate = new Color(0.7f, 0.7f, 0.7f),
        };

        var modRow = new HBoxContainer();
        _modBadgeLabel = new Label
        {
            Text = "Active Mod: MyMod",
            Modulate = new Color(0.4f, 0.85f, 0.5f),
        };
        modRow.AddChild(_modBadgeLabel);

        headerBox.AddChild(_headerLabel);
        headerBox.AddChild(_subHeaderLabel);
        headerBox.AddChild(modRow);
        rootBox.AddChild(headerBox);

        rootBox.AddChild(new HSeparator());

        // 2. Search Bar for filtering across all tabs and properties:
        var searchBox = new HBoxContainer();
        searchBox.AddChild(new Label
        {
            Text = "Filter:",
            Modulate = new Color(0.85f, 0.7f, 0.4f),
        });

        _searchEdit = new LineEdit
        {
            PlaceholderText = "Search properties across all tabs...",
            ClearButtonEnabled = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _searchEdit.TextChanged += OnSearchTextChanged;
        searchBox.AddChild(_searchEdit);
        rootBox.AddChild(searchBox);

        // 3. Tab Navigation Bar with Arrows on Each Side:
        var navRow = new HBoxContainer();
        navRow.AddThemeConstantOverride("separation", 10);
        navRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        _prevTabBtn = new Button
        {
            Text = "◀",
            TooltipText = "Previous Category Tab",
            CustomMinimumSize = new Vector2(36, 30),
        };
        _prevTabBtn.Pressed += () =>
        {
            if (_tabContainer is not null && _tabContainer.CurrentTab > 0)
            {
                _tabContainer.CurrentTab--;
                UpdateTabNavVisuals();
            }
        };
        navRow.AddChild(_prevTabBtn);

        _tabNavLabel = new Label
        {
            Text = "General & Lore",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Modulate = new Color(0.85f, 0.88f, 0.95f),
        };
        navRow.AddChild(_tabNavLabel);

        _nextTabBtn = new Button
        {
            Text = "▶",
            TooltipText = "Next Category Tab",
            CustomMinimumSize = new Vector2(36, 30),
        };
        _nextTabBtn.Pressed += () =>
        {
            if (_tabContainer is not null && _tabContainer.CurrentTab < _tabContainer.GetTabCount() - 1)
            {
                _tabContainer.CurrentTab++;
                UpdateTabNavVisuals();
            }
        };
        navRow.AddChild(_nextTabBtn);

        rootBox.AddChild(navRow);

        // 4. TabContainer with Category Tabs and Distinct Tab Styling:
        _tabContainer = new TabContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        ApplyTabStyles(_tabContainer);
        _tabContainer.TabChanged += OnTabChanged;

        // Tab 0: General & Lore
        var generalTab = CreateGeneralLoreTab();
        _tabContainer.AddChild(generalTab);
        _tabContainer.SetTabTitle(0, "General & Lore");

        rootBox.AddChild(_tabContainer);

        rootBox.AddChild(new HSeparator());

        // 5. Action Row with Revert Button:
        var actionRow = new HBoxContainer();
        _revertButton = new Button
        {
            Text = "↺ Revert All to Defaults",
            CustomMinimumSize = new Vector2(180, 32),
        };
        _revertButton.Pressed += OnRevertPressed;
        actionRow.AddChild(_revertButton);

        var spacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        actionRow.AddChild(spacer);

        rootBox.AddChild(actionRow);

        AddChild(rootBox);

        Confirmed += OnConfirmed;
    }

    /// <summary>
    /// Configures distinct, clean tab styles with centered tabs and accent borders.
    /// </summary>
    private static void ApplyTabStyles(TabContainer tc)
    {
        tc.TabAlignment = TabBar.AlignmentMode.Center;

        // Selected active tab:
        var selBox = new StyleBoxFlat
        {
            BgColor = new Color(0.24f, 0.28f, 0.35f),
            BorderColor = new Color(0.40f, 0.70f, 1.0f),
            BorderWidthTop = 3,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };

        // Inactive unselected tabs:
        var unselBox = new StyleBoxFlat
        {
            BgColor = new Color(0.14f, 0.16f, 0.19f),
            BorderColor = new Color(0.25f, 0.28f, 0.33f),
            BorderWidthTop = 1,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };

        // Hovered tab:
        var hovBox = new StyleBoxFlat
        {
            BgColor = new Color(0.20f, 0.23f, 0.28f),
            BorderColor = new Color(0.35f, 0.45f, 0.55f),
            BorderWidthTop = 2,
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 0,
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            ContentMarginLeft = 16,
            ContentMarginRight = 16,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };

        // Panel content background:
        var panelBox = new StyleBoxFlat
        {
            BgColor = new Color(0.18f, 0.20f, 0.24f),
            BorderColor = new Color(0.28f, 0.32f, 0.38f),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            BorderWidthTop = 1,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };

        tc.AddThemeStyleboxOverride("tab_selected", selBox);
        tc.AddThemeStyleboxOverride("tab_unselected", unselBox);
        tc.AddThemeStyleboxOverride("tab_hovered", hovBox);
        tc.AddThemeStyleboxOverride("panel", panelBox);

        tc.AddThemeColorOverride("font_selected_color", Colors.White);
        tc.AddThemeColorOverride("font_unselected_color", new Color(0.72f, 0.75f, 0.80f));
        tc.AddThemeColorOverride("font_hovered_color", new Color(0.92f, 0.95f, 1.0f));
    }

    private void OnTabChanged(long tabIndex)
    {
        UpdateTabNavVisuals();
    }

    /// <summary>
    /// Updates the left and right tab navigation buttons and centered category indicator label.
    /// </summary>
    private void UpdateTabNavVisuals()
    {
        if (_tabContainer is null) return;
        int current = _tabContainer.CurrentTab;
        int count = _tabContainer.GetTabCount();

        if (_prevTabBtn is not null)
        {
            _prevTabBtn.Disabled = current <= 0;
        }

        if (_nextTabBtn is not null)
        {
            _nextTabBtn.Disabled = current >= count - 1;
        }

        if (_tabNavLabel is not null && count > 0)
        {
            string tabTitle = _tabContainer.GetTabTitle(current);
            _tabNavLabel.Text = $"Category {current + 1} of {count}:  {tabTitle}";
        }
    }

    /// <summary>
    /// Creates the persistent first tab for Display Name and Lore Description.
    /// </summary>
    private Control CreateGeneralLoreTab()
    {
        var margin = new MarginContainer
        {
            Name = "General & Lore",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);

        var vbox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddThemeConstantOverride("separation", 10);

        _nameLabel = new Label { Text = "Display Name (In-Game Name):" };
        vbox.AddChild(_nameLabel);

        var nameRow = new HBoxContainer();
        _nameEdit = new LineEdit
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _nameEdit.TextChanged += _ =>
        {
            UpdateGeneralLoreVisuals();
            UpdateTabTitles();
            UpdateRevertButtonState();
        };
        nameRow.AddChild(_nameEdit);

        _resetNameBtn = new Button
        {
            Text = "↺",
            TooltipText = "Reset to default name",
            CustomMinimumSize = new Vector2(28, 0),
        };
        _resetNameBtn.Pressed += () =>
        {
            if (_currentValues is not null && _nameEdit is not null)
            {
                _nameEdit.Text = _currentValues.VanillaDisplayName;
                UpdateGeneralLoreVisuals();
                UpdateTabTitles();
                UpdateRevertButtonState();
            }
        };
        nameRow.AddChild(_resetNameBtn);
        vbox.AddChild(nameRow);

        _descLabel = new Label { Text = "Description (Lore / In-Game Tooltip):" };
        vbox.AddChild(_descLabel);

        _descEdit = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 160),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _descEdit.TextChanged += () =>
        {
            UpdateGeneralLoreVisuals();
            UpdateTabTitles();
            UpdateRevertButtonState();
        };
        vbox.AddChild(_descEdit);

        var descActionRow = new HBoxContainer();
        var descSpacer = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        descActionRow.AddChild(descSpacer);

        _resetDescBtn = new Button
        {
            Text = "↺ Reset Description",
            TooltipText = "Reset description to vanilla lore",
        };
        _resetDescBtn.Pressed += () =>
        {
            if (_currentValues is not null && _descEdit is not null)
            {
                _descEdit.Text = _currentValues.VanillaDescription;
                UpdateGeneralLoreVisuals();
                UpdateTabTitles();
                UpdateRevertButtonState();
            }
        };
        descActionRow.AddChild(_resetDescBtn);
        vbox.AddChild(descActionRow);

        var infoLabel = new Label
        {
            Text = "ℹ Name and Description changes are saved as clean UTF-8 text overlays in english.babel.csv.",
            Modulate = new Color(0.6f, 0.6f, 0.6f),
        };
        infoLabel.AddThemeFontSizeOverride("font_size", 12);
        vbox.AddChild(infoLabel);

        margin.AddChild(vbox);
        return margin;
    }

    /// <summary>
    /// Creates a scrollable grid container for an item component category tab.
    /// </summary>
    private static (ScrollContainer Scroll, GridContainer Grid) CreateCategoryTabControl(string title)
    {
        var scroll = new ScrollContainer
        {
            Name = title,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
        };

        var margin = new MarginContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        margin.AddThemeConstantOverride("margin_left", 12);
        margin.AddThemeConstantOverride("margin_right", 12);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);

        var grid = new GridContainer
        {
            Columns = 3,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 8);

        margin.AddChild(grid);
        scroll.AddChild(margin);
        return (scroll, grid);
    }

    /// <summary>
    /// Opens the dialog populated with genuine schema properties and effective values for the selected item.
    /// </summary>
    public void OpenForItem(ItemDetail detail)
    {
        _currentDetail = detail;
        string activeMod = EditorModExporter.ActiveModName;

        ItemModValues values = EditorModValueService.GetEffectiveValues(detail, activeMod);
        _currentValues = values;

        PopulateForm(values, activeMod);
        PopupCentered();
    }

    /// <summary>
    /// Populates all dialog fields and constructs category tabs based on the item's discovered components.
    /// </summary>
    private void PopulateForm(ItemModValues values, string? activeMod = null)
    {
        if (_currentDetail is null)
        {
            return;
        }

        string mod = !string.IsNullOrWhiteSpace(activeMod) ? activeMod : EditorModExporter.ActiveModName;

        if (_headerLabel is not null)
        {
            _headerLabel.Text = _currentDetail.Label;
        }

        if (_subHeaderLabel is not null)
        {
            string internalName = !string.IsNullOrEmpty(_currentDetail.InternalName)
                ? $"  ({_currentDetail.InternalName})"
                : string.Empty;
            _subHeaderLabel.Text = $"Record: 0x{_currentDetail.RecordHash:X8}{internalName}";
        }

        if (_modBadgeLabel is not null)
        {
            _modBadgeLabel.Text = $"Active Mod: {mod}" + (values.HasModOverrides ? " [MODIFIED OVERLAY]" : " [VANILLA DEFAULTS]");
            _modBadgeLabel.Modulate = values.HasModOverrides ? new Color(1.0f, 0.85f, 0.35f) : new Color(0.4f, 0.85f, 0.5f);
        }

        if (_searchEdit is not null)
        {
            _searchEdit.Text = string.Empty;
        }

        // 1. Populate General & Lore fields:
        if (_nameEdit is not null)
        {
            _nameEdit.Text = values.DisplayName;
        }
        if (_descEdit is not null)
        {
            _descEdit.Text = values.Description;
        }
        if (_resetNameBtn is not null)
        {
            _resetNameBtn.TooltipText = $"Reset to default ({values.VanillaDisplayName})";
        }
        if (_resetDescBtn is not null)
        {
            _resetDescBtn.TooltipText = "Reset to default vanilla description";
        }

        UpdateGeneralLoreVisuals();

        // 2. Clear old dynamic category tabs (keeping Tab 0: General & Lore):
        _rowControls.Clear();
        _dynamicTabs.Clear();

        if (_tabContainer is not null)
        {
            while (_tabContainer.GetChildCount() > 1)
            {
                Node child = _tabContainer.GetChild(1);
                _tabContainer.RemoveChild(child);
                child.QueueFree();
            }

            // Logical presentation ordering for component categories:
            var preferredOrder = new[]
            {
                "Shop",
                "Inventory",
                "Combat",
                "Augments",
                "Appearance",
                "Definition",
            };

            var categoryGroups = new Dictionary<string, (string DisplayName, List<ModItemProperty> Props)>(StringComparer.OrdinalIgnoreCase);

            foreach (ModItemProperty prop in values.Properties)
            {
                string catId = !string.IsNullOrEmpty(prop.CategoryId) ? prop.CategoryId : "Definition";
                string catName = !string.IsNullOrEmpty(prop.CategoryDisplayName) ? prop.CategoryDisplayName : "Item Definition";

                if (!categoryGroups.TryGetValue(catId, out var group))
                {
                    group = (catName, new List<ModItemProperty>());
                    categoryGroups[catId] = group;
                }
                group.Props.Add(prop);
            }

            // Order categories by preferredOrder followed by any novel discovered categories:
            var orderedCategoryIds = new List<string>();
            foreach (string catId in preferredOrder)
            {
                if (categoryGroups.ContainsKey(catId))
                {
                    orderedCategoryIds.Add(catId);
                }
            }
            foreach (string catId in categoryGroups.Keys)
            {
                if (!orderedCategoryIds.Exists(c => c.Equals(catId, StringComparison.OrdinalIgnoreCase)))
                {
                    orderedCategoryIds.Add(catId);
                }
            }

            // Build tab for each populated category:
            foreach (string catId in orderedCategoryIds)
            {
                var group = categoryGroups[catId];
                if (group.Props.Count == 0)
                {
                    continue;
                }

                int tabIndex = _tabContainer.GetChildCount();
                var (tabScroll, grid) = CreateCategoryTabControl(group.DisplayName);
                _tabContainer.AddChild(tabScroll);
                _tabContainer.SetTabTitle(tabIndex, group.DisplayName);

                var tabInfo = new TabInfo
                {
                    CategoryId = catId,
                    BaseTitle = group.DisplayName,
                    TabIndex = tabIndex,
                    TabControl = tabScroll,
                };
                _dynamicTabs.Add(tabInfo);

                foreach (ModItemProperty prop in group.Props)
                {
                    BuildPropertyRow(grid, prop, tabIndex);
                }
            }

            _tabContainer.CurrentTab = 0;
            UpdateTabTitles();
        }

        UpdateTabNavVisuals();
        UpdateRevertButtonState();
    }

    /// <summary>
    /// Builds a row inside a category tab for a specific discovered GDB property.
    /// </summary>
    private void BuildPropertyRow(GridContainer grid, ModItemProperty prop, int tabIndex)
    {
        var label = new Label
        {
            Text = prop.DisplayLabel + ":",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };

        Control editorControl;
        Func<string> getValue;
        Action<string> setValue;

        if (prop.Type == GdbFieldType.Bool)
        {
            var check = new CheckBox
            {
                Text = "Enabled",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            check.ButtonPressed = prop.CurrentValue.Equals("true", StringComparison.OrdinalIgnoreCase) || prop.CurrentValue == "1";
            getValue = () => check.ButtonPressed ? "true" : "false";
            setValue = v => check.ButtonPressed = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
            editorControl = check;
        }
        else if (prop.Type == GdbFieldType.Float)
        {
            var spin = new SpinBox
            {
                MinValue = -1000000.0,
                MaxValue = 1000000.0,
                Step = 0.05,
                AllowGreater = true,
                AllowLesser = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };

            if (prop.PropertyName.Contains("Multiplier", StringComparison.OrdinalIgnoreCase))
            {
                spin.Suffix = "x";
            }
            else if (prop.PropertyName.Contains("Time", StringComparison.OrdinalIgnoreCase))
            {
                spin.Suffix = " s";
            }
            else if (prop.PropertyName.Contains("Range", StringComparison.OrdinalIgnoreCase))
            {
                spin.Suffix = " m";
            }

            if (float.TryParse(prop.CurrentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float fVal))
            {
                spin.Value = fVal;
            }
            getValue = () => spin.Value.ToString("0.#####", CultureInfo.InvariantCulture);
            setValue = v =>
            {
                if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    spin.Value = f;
                }
            };
            editorControl = spin;
        }
        else if (prop.Type == GdbFieldType.Int)
        {
            if (prop.PropertyName.Equals("Rating", StringComparison.OrdinalIgnoreCase))
            {
                var opt = new OptionButton
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                opt.AddItem("1 Star (Tier 1)", 1);
                opt.AddItem("2 Stars (Tier 2)", 2);
                opt.AddItem("3 Stars (Tier 3)", 3);
                opt.AddItem("4 Stars (Tier 4)", 4);
                opt.AddItem("5 Stars (Tier 5)", 5);
                int.TryParse(prop.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rVal);
                opt.Select(Math.Clamp(rVal - 1, 0, 4));
                getValue = () => (opt.Selected + 1).ToString(CultureInfo.InvariantCulture);
                setValue = v =>
                {
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
                    {
                        opt.Select(Math.Clamp(r - 1, 0, 4));
                    }
                };
                editorControl = opt;
            }
            else if (prop.PropertyName.Equals("NumberOfSlots", StringComparison.OrdinalIgnoreCase))
            {
                var spin = new SpinBox
                {
                    MinValue = 0,
                    MaxValue = 8,
                    Step = 1,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                int.TryParse(prop.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sVal);
                spin.Value = sVal;
                getValue = () => ((int)spin.Value).ToString(CultureInfo.InvariantCulture);
                setValue = v =>
                {
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
                    {
                        spin.Value = s;
                    }
                };
                editorControl = spin;
            }
            else if (prop.PropertyName.Equals("BasePrice", StringComparison.OrdinalIgnoreCase))
            {
                var spin = new SpinBox
                {
                    MinValue = 0,
                    MaxValue = 100000000,
                    Step = 10,
                    AllowGreater = true,
                    Suffix = " Gold",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                int.TryParse(prop.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pVal);
                spin.Value = pVal;
                getValue = () => ((int)spin.Value).ToString(CultureInfo.InvariantCulture);
                setValue = v =>
                {
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p))
                    {
                        spin.Value = p;
                    }
                };
                editorControl = spin;
            }
            else
            {
                var spin = new SpinBox
                {
                    MinValue = -1000000,
                    MaxValue = 100000000,
                    Step = 1,
                    AllowGreater = true,
                    AllowLesser = true,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                int.TryParse(prop.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal);
                spin.Value = iVal;
                getValue = () => ((int)spin.Value).ToString(CultureInfo.InvariantCulture);
                setValue = v =>
                {
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv))
                    {
                        spin.Value = iv;
                    }
                };
                editorControl = spin;
            }
        }
        else
        {
            var spin = new SpinBox
            {
                MinValue = 0,
                MaxValue = 100000000,
                Step = 1,
                AllowGreater = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            uint.TryParse(prop.CurrentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint eVal);
            spin.Value = eVal;
            getValue = () => ((uint)spin.Value).ToString(CultureInfo.InvariantCulture);
            setValue = v =>
            {
                if (uint.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint ev))
                {
                    spin.Value = ev;
                }
            };
            editorControl = spin;
        }

        var resetBtn = new Button
        {
            Text = "↺",
            TooltipText = $"Reset to default ({prop.VanillaValue})",
            CustomMinimumSize = new Vector2(28, 0),
        };

        var row = new PropRow
        {
            Property = prop,
            LabelNode = label,
            EditorControl = editorControl,
            ResetButton = resetBtn,
            GetValue = getValue,
            SetValue = setValue,
            TabIndex = tabIndex,
        };

        resetBtn.Pressed += () =>
        {
            setValue(prop.VanillaValue);
            prop.CurrentValue = prop.VanillaValue;
            prop.IsModified = false;
            UpdateRowVisual(row);
            UpdateTabTitles();
            UpdateRevertButtonState();
        };

        // Connect value change signals to update visuals:
        if (editorControl is SpinBox sb)
        {
            sb.ValueChanged += _ =>
            {
                prop.CurrentValue = getValue();
                prop.IsModified = !string.Equals(prop.CurrentValue, prop.VanillaValue, StringComparison.Ordinal);
                UpdateRowVisual(row);
                UpdateTabTitles();
                UpdateRevertButtonState();
            };
        }
        else if (editorControl is CheckBox cb)
        {
            cb.Toggled += _ =>
            {
                prop.CurrentValue = getValue();
                prop.IsModified = !string.Equals(prop.CurrentValue, prop.VanillaValue, StringComparison.Ordinal);
                UpdateRowVisual(row);
                UpdateTabTitles();
                UpdateRevertButtonState();
            };
        }
        else if (editorControl is OptionButton ob)
        {
            ob.ItemSelected += _ =>
            {
                prop.CurrentValue = getValue();
                prop.IsModified = !string.Equals(prop.CurrentValue, prop.VanillaValue, StringComparison.Ordinal);
                UpdateRowVisual(row);
                UpdateTabTitles();
                UpdateRevertButtonState();
            };
        }

        UpdateRowVisual(row);

        _rowControls.Add(row);
        grid.AddChild(label);
        grid.AddChild(editorControl);
        grid.AddChild(resetBtn);
    }

    /// <summary>
    /// Highlights modified rows in gold and enables their per-property reset button.
    /// </summary>
    private static void UpdateRowVisual(PropRow row)
    {
        if (row.Property.IsModified)
        {
            row.LabelNode.Modulate = new Color(1.0f, 0.85f, 0.4f);
            row.ResetButton.Disabled = false;
            row.ResetButton.Modulate = Colors.White;
        }
        else
        {
            row.LabelNode.Modulate = Colors.White;
            row.ResetButton.Disabled = true;
            row.ResetButton.Modulate = new Color(0.5f, 0.5f, 0.5f, 0.6f);
        }
    }

    /// <summary>
    /// Highlights modified General & Lore fields and manages their reset buttons.
    /// </summary>
    private void UpdateGeneralLoreVisuals()
    {
        if (_currentValues is null)
        {
            return;
        }

        bool nameMod = !string.Equals(_nameEdit?.Text.Trim(), _currentValues.VanillaDisplayName, StringComparison.Ordinal);
        if (_nameLabel is not null)
        {
            _nameLabel.Modulate = nameMod ? new Color(1.0f, 0.85f, 0.4f) : Colors.White;
        }
        if (_resetNameBtn is not null)
        {
            _resetNameBtn.Disabled = !nameMod;
            _resetNameBtn.Modulate = nameMod ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.6f);
        }

        bool descMod = !string.Equals(_descEdit?.Text.Trim(), _currentValues.VanillaDescription, StringComparison.Ordinal);
        if (_descLabel is not null)
        {
            _descLabel.Modulate = descMod ? new Color(1.0f, 0.85f, 0.4f) : Colors.White;
        }
        if (_resetDescBtn is not null)
        {
            _resetDescBtn.Disabled = !descMod;
            _resetDescBtn.Modulate = descMod ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.6f);
        }
    }

    /// <summary>
    /// Updates tab titles with modified indicators (•) and match counts when searching.
    /// </summary>
    private void UpdateTabTitles()
    {
        if (_tabContainer is null || _currentValues is null)
        {
            return;
        }

        string query = _searchEdit?.Text.Trim() ?? string.Empty;
        bool isSearching = !string.IsNullOrEmpty(query);

        // Tab 0: General & Lore
        bool generalMod = !string.Equals(_nameEdit?.Text.Trim(), _currentValues.VanillaDisplayName, StringComparison.Ordinal) ||
                          !string.Equals(_descEdit?.Text.Trim(), _currentValues.VanillaDescription, StringComparison.Ordinal);
        string genTitle = "General & Lore" + (generalMod ? " •" : string.Empty);
        if (isSearching)
        {
            bool nameMatch = "display name".Contains(query, StringComparison.OrdinalIgnoreCase) || (_nameEdit?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            bool descMatch = "description".Contains(query, StringComparison.OrdinalIgnoreCase) || "lore".Contains(query, StringComparison.OrdinalIgnoreCase) || (_descEdit?.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
            genTitle += (nameMatch || descMatch) ? " (✓)" : " (0)";
        }
        _tabContainer.SetTabTitle(0, genTitle);

        // Dynamic category tabs:
        foreach (var tabInfo in _dynamicTabs)
        {
            bool hasModInTab = false;
            int matchCount = 0;

            foreach (var row in _rowControls)
            {
                if (row.TabIndex != tabInfo.TabIndex)
                {
                    continue;
                }
                if (row.Property.IsModified)
                {
                    hasModInTab = true;
                }
                if (isSearching)
                {
                    bool match = row.Property.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 row.Property.PropertyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 row.Property.CategoryDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        matchCount++;
                    }
                }
            }

            string title = tabInfo.BaseTitle + (hasModInTab ? " •" : string.Empty);
            if (isSearching)
            {
                title += $" ({matchCount})";
            }
            _tabContainer.SetTabTitle(tabInfo.TabIndex, title);
        }

        UpdateTabNavVisuals();
    }

    /// <summary>
    /// Filters properties across all tabs based on search query, updating row visibility and tab counts.
    /// </summary>
    private void OnSearchTextChanged(string query)
    {
        string trimmed = query.Trim();
        bool isSearching = !string.IsNullOrEmpty(trimmed);

        int firstMatchingTabIndex = -1;

        bool nameMatch = !isSearching || "display name".Contains(trimmed, StringComparison.OrdinalIgnoreCase) || (_nameEdit?.Text.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false);
        bool descMatch = !isSearching || "description".Contains(trimmed, StringComparison.OrdinalIgnoreCase) || "lore".Contains(trimmed, StringComparison.OrdinalIgnoreCase) || (_descEdit?.Text.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ?? false);
        bool generalMatch = nameMatch || descMatch;
        if (generalMatch && firstMatchingTabIndex == -1)
        {
            firstMatchingTabIndex = 0;
        }

        var tabMatchCounts = new Dictionary<int, int>();
        foreach (var tab in _dynamicTabs)
        {
            tabMatchCounts[tab.TabIndex] = 0;
        }

        foreach (PropRow row in _rowControls)
        {
            bool match = !isSearching ||
                         row.Property.DisplayLabel.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                         row.Property.PropertyName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                         row.Property.CategoryDisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase);

            row.LabelNode.Visible = match;
            row.EditorControl.Visible = match;
            row.ResetButton.Visible = match;

            if (match)
            {
                tabMatchCounts[row.TabIndex] = tabMatchCounts.GetValueOrDefault(row.TabIndex) + 1;
                if (firstMatchingTabIndex == -1)
                {
                    firstMatchingTabIndex = row.TabIndex;
                }
            }
        }

        UpdateTabTitles();

        // Switch to the first tab that has matches if the current tab has none:
        if (isSearching && _tabContainer is not null)
        {
            int current = _tabContainer.CurrentTab;
            bool currentHasMatches = (current == 0 && generalMatch) || tabMatchCounts.GetValueOrDefault(current, 0) > 0;
            if (!currentHasMatches && firstMatchingTabIndex != -1)
            {
                _tabContainer.CurrentTab = firstMatchingTabIndex;
                UpdateTabNavVisuals();
            }
        }
    }

    /// <summary>
    /// Enables or disables the global revert button with clean contrast and informative tooltips.
    /// </summary>
    private void UpdateRevertButtonState()
    {
        if (_revertButton is null)
        {
            return;
        }

        bool hasAnyMod = HasAnyModifications();
        _revertButton.Disabled = !hasAnyMod;

        if (hasAnyMod)
        {
            _revertButton.AddThemeColorOverride("font_color", new Color(1.0f, 0.45f, 0.45f));
            _revertButton.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.65f, 0.65f));
            _revertButton.TooltipText = "Click to revert all active mod overrides for this item to vanilla game defaults.";
        }
        else
        {
            _revertButton.RemoveThemeColorOverride("font_color");
            _revertButton.RemoveThemeColorOverride("font_hover_color");
            _revertButton.TooltipText = "Item is currently using vanilla defaults (no active overrides to revert).";
        }
    }

    private bool HasAnyModifications()
    {
        if (_currentValues is null)
        {
            return false;
        }

        if (_currentValues.HasModOverrides)
        {
            return true;
        }

        if (_nameEdit is not null && !string.Equals(_nameEdit.Text.Trim(), _currentValues.VanillaDisplayName, StringComparison.Ordinal))
        {
            return true;
        }

        if (_descEdit is not null && !string.Equals(_descEdit.Text.Trim(), _currentValues.VanillaDescription, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (PropRow row in _rowControls)
        {
            if (row.Property.IsModified)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Saves all changes across all tabs to the active mod's overlay CSVs.
    /// </summary>
    private void OnConfirmed()
    {
        if (_currentDetail is null || _currentValues is null)
        {
            return;
        }

        _currentValues.DisplayName = _nameEdit?.Text.Trim() ?? string.Empty;
        _currentValues.Description = _descEdit?.Text.Trim() ?? string.Empty;

        foreach (PropRow row in _rowControls)
        {
            row.Property.CurrentValue = row.GetValue();
            row.Property.IsModified = !string.Equals(row.Property.CurrentValue, row.Property.VanillaValue, StringComparison.Ordinal);
        }

        EditorModValueService.SaveItemOverrides(_currentDetail, _currentValues);
    }

    /// <summary>
    /// Reverts all mod overrides for this item and re-populates the dialog with vanilla defaults.
    /// </summary>
    private void OnRevertPressed()
    {
        if (_currentDetail is null)
        {
            return;
        }

        EditorModValueService.RevertItemOverrides(_currentDetail);
        ItemModValues vanilla = EditorModValueService.GetVanillaValues(_currentDetail);
        _currentValues = vanilla;
        PopulateForm(vanilla, EditorModExporter.ActiveModName);
    }
}
#endif
