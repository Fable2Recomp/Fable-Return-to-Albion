#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// Primary host panel for the Fable 2 Mod Studio, managing the top workspace header,
/// active mod switcher, domain navigation rail, and sub-view hosting.
/// Designed for full responsiveness as a first-class Godot main screen workspace.
/// </summary>
public partial class ModdingHubPanel : MarginContainer
{
    public static ModdingHubPanel? Instance { get; private set; }

    private VBoxContainer? _mainVBox;
    private Label? _titleLabel;
    private HBoxContainer? _leftBox;
    private HBoxContainer? _centerBox;
    private HBoxContainer? _rightBox;
    private OptionButton? _modSelectDropdown;
    private HBoxContainer? _expandedActions;
    private MenuButton? _hamburgerMenu;
    private Button? _newModButton;
    private Button? _editMetaButton;
    private Button? _creatorFolderButton;
    private Button? _deleteModButton;
    private Button? _refreshButton;

    private const int MenuActionNewMod = 1;
    private const int MenuActionEditMeta = 2;
    private const int MenuActionCreatorFolder = 3;
    private const int MenuActionRefresh = 4;
    private const int MenuActionDeleteMod = 5;

    // Navigation Tab Buttons
    private HBoxContainer? _navBar;
    private readonly List<Button> _navButtons = new();
    private Control? _contentContainer;

    // Views
    private ModdingHubHomeView? _homeView;
    private ModdingGuideView? _moddingGuideView;
    private FxLabView? _fxLabView;
    private PolymorphicItemGraphView? _itemGraphView;

    private readonly List<Control> _views = new();
    private int _currentTabIndex = 0;

    // Dialogs
    private CreateModDialog? _createModDialog;
    private ModMetadataDialog? _metadataDialog;
    private ConfirmationDialog? _deleteConfirmDialog;

    public ModdingHubPanel()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;

        AddThemeConstantOverride("margin_left", 8);
        AddThemeConstantOverride("margin_top", 8);
        AddThemeConstantOverride("margin_right", 8);
        AddThemeConstantOverride("margin_bottom", 8);

        _mainVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        _mainVBox.AddThemeConstantOverride("separation", 8);
        AddChild(_mainVBox);

        BuildHeader();
        BuildNavigationRail();
        BuildContentArea();
        BuildDialogs();

        Resized += UpdateResponsiveLayout;
        RefreshModList();
    }

    public override void _Ready()
    {
        base._Ready();
        UpdateResponsiveLayout();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        Instance = this;
        EditorModExporter.ActiveModChanged += OnExternalActiveModChanged;
        EditorModExporter.ModContentChanged += OnExternalModContentChanged;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        EditorModExporter.ActiveModChanged -= OnExternalActiveModChanged;
        EditorModExporter.ModContentChanged -= OnExternalModContentChanged;
        base._ExitTree();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationWMWindowFocusIn)
        {
            RefreshActiveMod();
        }
    }

    private void OnExternalActiveModChanged(string newMod)
    {
        RefreshModList();
    }

    private void OnExternalModContentChanged(string modifiedMod)
    {
        string currentActive = EditorModExporter.ActiveModName;
        if (string.IsNullOrEmpty(modifiedMod) || modifiedMod.Equals(currentActive, StringComparison.OrdinalIgnoreCase))
        {
            RefreshActiveMod();
        }
    }

    private void UpdateResponsiveLayout()
    {
        float width = Size.X;
        bool isCompact = width < 720;

        if (_expandedActions is not null)
        {
            _expandedActions.Visible = !isCompact;
        }

        if (_hamburgerMenu is not null)
        {
            _hamburgerMenu.Visible = isCompact;
        }

        if (_titleLabel is not null)
        {
            _titleLabel.Visible = width >= 500;
        }

        if (_leftBox is not null)
        {
            _leftBox.Visible = width >= 500;
        }

        if (_rightBox is not null)
        {
            _rightBox.Visible = width >= 500;
        }
    }

    private void BuildHeader()
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 10);
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // Left section: Title branding
        _leftBox = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Begin
        };
        _titleLabel = new Label
        {
            Text = "🛠️ FABLE 2 MOD STUDIO",
            Modulate = new Color(0.40f, 0.70f, 1.0f)
        };
        _titleLabel.AddThemeFontSizeOverride("font_size", 15);
        _leftBox.AddChild(_titleLabel);
        header.AddChild(_leftBox);

        // Center section: Active mod selection and quick actions
        _centerBox = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter
        };
        _centerBox.AddThemeConstantOverride("separation", 8);

        var modLabel = new Label
        {
            Text = "Active Mod:",
            Modulate = new Color(0.8f, 0.8f, 0.8f)
        };
        _centerBox.AddChild(modLabel);

        _modSelectDropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(140, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin
        };
        _modSelectDropdown.ItemSelected += OnModSelected;
        _centerBox.AddChild(_modSelectDropdown);

        // Expanded actions (fanned out in wide view)
        _expandedActions = new HBoxContainer();
        _expandedActions.AddThemeConstantOverride("separation", 6);

        _newModButton = new Button
        {
            Text = "+ New Mod",
            TooltipText = "Create a brand new mod package and workspace"
        };
        _newModButton.Pressed += () => _createModDialog?.Open();
        _expandedActions.AddChild(_newModButton);

        _editMetaButton = new Button
        {
            Text = "✎ Metadata",
            TooltipText = "Edit active mod author, version, and description in package header"
        };
        _editMetaButton.Pressed += () =>
        {
            string active = EditorModExporter.ActiveModName;
            if (!string.IsNullOrWhiteSpace(active))
            {
                _metadataDialog?.OpenForMod(active);
            }
        };
        _expandedActions.AddChild(_editMetaButton);

        _creatorFolderButton = new Button
        {
            Text = "📁 Creator",
            TooltipText = "Open assets/creator/<ActiveMod> in File Explorer"
        };
        _creatorFolderButton.Pressed += OnOpenCreatorFolder;
        _expandedActions.AddChild(_creatorFolderButton);

        _refreshButton = new Button
        {
            Text = "🔄",
            TooltipText = "Refresh active mod content"
        };
        _refreshButton.Pressed += () =>
        {
            RefreshModList();
            RefreshActiveMod();
        };
        _expandedActions.AddChild(_refreshButton);

        var separator = new VSeparator();
        separator.AddThemeConstantOverride("separation", 10);
        _expandedActions.AddChild(separator);

        _deleteModButton = new Button
        {
            Text = "🗑️ Delete",
            TooltipText = "Permanently delete active mod package and creator assets from disk"
        };
        ModStudioUiStyles.ApplyRedButtonStyle(_deleteModButton);
        _deleteModButton.Pressed += OnDeleteModPressed;
        _expandedActions.AddChild(_deleteModButton);

        _centerBox.AddChild(_expandedActions);

        // Hamburger menu button (collapsed fallback for narrow main screen)
        _hamburgerMenu = new MenuButton
        {
            Text = "☰",
            TooltipText = "Mod actions and options",
            Visible = false
        };
        _hamburgerMenu.CustomMinimumSize = new Vector2(28, 26);
        var popup = _hamburgerMenu.GetPopup();
        popup.AddItem("+ New Mod", MenuActionNewMod);
        popup.AddItem("✎ Edit Metadata", MenuActionEditMeta);
        popup.AddItem("📁 Open Creator Folder", MenuActionCreatorFolder);
        popup.AddItem("🔄 Refresh Content", MenuActionRefresh);
        popup.AddSeparator();
        popup.AddItem("🗑️ Delete Mod...", MenuActionDeleteMod);
        popup.IdPressed += OnHamburgerMenuIdPressed;
        _centerBox.AddChild(_hamburgerMenu);

        header.AddChild(_centerBox);

        // Right section: Balancer spacer to ensure centerBox remains centered
        _rightBox = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.End
        };
        header.AddChild(_rightBox);

        _mainVBox?.AddChild(header);
        _mainVBox?.AddChild(new HSeparator());
    }

    private void OnHamburgerMenuIdPressed(long id)
    {
        string active = EditorModExporter.ActiveModName;
        switch (id)
        {
            case MenuActionNewMod:
                _createModDialog?.Open();
                break;
            case MenuActionEditMeta:
                if (!string.IsNullOrWhiteSpace(active)) _metadataDialog?.OpenForMod(active);
                break;
            case MenuActionCreatorFolder:
                OnOpenCreatorFolder();
                break;
            case MenuActionRefresh:
                RefreshModList();
                break;
            case MenuActionDeleteMod:
                OnDeleteModPressed();
                break;
        }
    }

    private void BuildNavigationRail()
    {
        _navBar = new HBoxContainer();
        _navBar.AddThemeConstantOverride("separation", 6);
        _navBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        AddNavButton("🏠 Home", 0);
        AddNavButton("📖 Modding Guide", 1);
        AddNavButton("🗂️ GDB Hierarchy & Components", 2);
        AddNavButton("✨ FX Lab", 3);

        var navScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 32)
        };
        navScroll.AddChild(_navBar);
        _mainVBox?.AddChild(navScroll);
    }

    private void AddNavButton(string title, int index)
    {
        var btn = new Button
        {
            Text = title,
            ToggleMode = true,
            ButtonGroup = new ButtonGroup()
        };
        btn.Pressed += () => SwitchTab(index);
        _navButtons.Add(btn);
        _navBar?.AddChild(btn);
    }

    private void BuildContentArea()
    {
        _contentContainer = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.13f, 0.16f),
            BorderColor = new Color(0.20f, 0.23f, 0.28f),
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
            ContentMarginTop = 12,
            ContentMarginBottom = 12
        };
        _contentContainer.AddThemeStyleboxOverride("panel", panelStyle);

        // Tab 0: Home View
        _homeView = new ModdingHubHomeView();
        _views.Add(_homeView);
        _contentContainer.AddChild(_homeView);

        // Tab 1: Interactive Modding Guide & Socket Reference View
        _moddingGuideView = new ModdingGuideView();
        _views.Add(_moddingGuideView);
        _contentContainer.AddChild(_moddingGuideView);

        // Tab 2: Unified GDB Hierarchy & Component Editor View
        _itemGraphView = new PolymorphicItemGraphView();
        _views.Add(_itemGraphView);
        _contentContainer.AddChild(_itemGraphView);

        // Tab 3: the FX Lab — the active mod's fx/recipes.json against a live preview.
        _fxLabView = new FxLabView();
        _views.Add(_fxLabView);
        _contentContainer.AddChild(_fxLabView);

        _mainVBox?.AddChild(_contentContainer);
        SwitchTab(0);
    }

    public void OpenGdbDomain(string domainKey)
    {
        SwitchTab(2);
        _itemGraphView?.SelectDomain(domainKey);
    }

    public void OpenGdbNode(uint recordHash)
    {
        SwitchTab(2);
        _itemGraphView?.SelectNodeByHash(recordHash);
    }

    private void BuildDialogs()
    {
        _createModDialog = new CreateModDialog();
        _createModDialog.ModCreated += name =>
        {
            RefreshModList();
            SelectModByName(name);
        };
        AddChild(_createModDialog);

        _metadataDialog = new ModMetadataDialog();
        _metadataDialog.MetadataUpdated += () => RefreshActiveMod();
        AddChild(_metadataDialog);

        _deleteConfirmDialog = new ConfirmationDialog
        {
            Title = "Delete Mod Package",
            OkButtonText = "Delete Permanently"
        };
        _deleteConfirmDialog.Confirmed += OnDeleteConfirmed;
        ModStudioUiStyles.ApplyRedButtonStyle(_deleteConfirmDialog.GetOkButton());
        AddChild(_deleteConfirmDialog);
    }

    private void OnDeleteModPressed()
    {
        string active = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(active)) return;

        if (_deleteConfirmDialog is not null)
        {
            _deleteConfirmDialog.DialogText =
                $"Are you sure you want to permanently delete mod '{active}'?\n\n" +
                $"This will remove:\n" +
                $"• assets/mods/{active}.f2mod\n" +
                $"• res://creator/{active}/\n\n" +
                $"This action cannot be undone.";
            _deleteConfirmDialog.PopupCentered();
        }
    }

    private void OnDeleteConfirmed()
    {
        string active = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(active)) return;

        if (!EditorModExporter.DeleteMod(active, out string error))
        {
            GD.PushWarning($"[mod_manager] Failed to delete mod '{active}': {error}");
        }

        RefreshModList();
    }

    public void SwitchTab(int index)
    {
        _currentTabIndex = index;

        for (int i = 0; i < _navButtons.Count; i++)
        {
            _navButtons[i].ButtonPressed = (i == index);
        }

        for (int i = 0; i < _views.Count; i++)
        {
            _views[i].Visible = (i == index);
        }
    }

    /// <summary>
    /// Reloads the list of discovered mods from disk and selects the active mod.
    /// Handles zero-mod states gracefully by displaying '(No Active Mod)' and disabling mod-specific actions.
    /// </summary>
    public void RefreshModList()
    {
        if (_modSelectDropdown is null) return;

        _modSelectDropdown.Clear();
        List<string> names = EditorModExporter.DiscoverModNames();
        string currentActive = EditorModExporter.ActiveModName;

        if (names.Count == 0)
        {
            _modSelectDropdown.AddItem("(No Active Mod)");
            _modSelectDropdown.SetItemDisabled(0, true);
            _modSelectDropdown.Select(0);

            if (_editMetaButton is not null) _editMetaButton.Disabled = true;
            if (_creatorFolderButton is not null) _creatorFolderButton.Disabled = true;
            if (_deleteModButton is not null) _deleteModButton.Disabled = true;
            UpdateHamburgerMenuState(false);

            RefreshActiveMod();
            return;
        }

        if (_editMetaButton is not null) _editMetaButton.Disabled = false;
        if (_creatorFolderButton is not null) _creatorFolderButton.Disabled = false;
        if (_deleteModButton is not null) _deleteModButton.Disabled = false;
        UpdateHamburgerMenuState(true);

        int selectedIndex = 0;
        for (int i = 0; i < names.Count; i++)
        {
            _modSelectDropdown.AddItem(names[i]);
            if (names[i].Equals(currentActive, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }

        _modSelectDropdown.Select(selectedIndex);
        if (!names[selectedIndex].Equals(EditorModExporter.ActiveModName, StringComparison.OrdinalIgnoreCase))
        {
            EditorModExporter.ActiveModName = names[selectedIndex];
        }

        RefreshActiveMod();
    }

    private void UpdateHamburgerMenuState(bool hasMod)
    {
        var popup = _hamburgerMenu?.GetPopup();
        if (popup is null) return;
        popup.SetItemDisabled(popup.GetItemIndex(MenuActionEditMeta), !hasMod);
        popup.SetItemDisabled(popup.GetItemIndex(MenuActionCreatorFolder), !hasMod);
        popup.SetItemDisabled(popup.GetItemIndex(MenuActionDeleteMod), !hasMod);
    }

    private void SelectModByName(string modName)
    {
        if (_modSelectDropdown is null) return;
        for (int i = 0; i < _modSelectDropdown.ItemCount; i++)
        {
            if (_modSelectDropdown.GetItemText(i).Equals(modName, StringComparison.OrdinalIgnoreCase))
            {
                _modSelectDropdown.Select(i);
                if (!modName.Equals(EditorModExporter.ActiveModName, StringComparison.OrdinalIgnoreCase))
                {
                    EditorModExporter.ActiveModName = modName;
                }
                if (_editMetaButton is not null) _editMetaButton.Disabled = false;
                if (_creatorFolderButton is not null) _creatorFolderButton.Disabled = false;
                if (_deleteModButton is not null) _deleteModButton.Disabled = false;
                UpdateHamburgerMenuState(true);
                RefreshActiveMod();
                return;
            }
        }
    }

    private void OnModSelected(long index)
    {
        if (_modSelectDropdown is null || _modSelectDropdown.ItemCount == 0) return;
        string chosen = _modSelectDropdown.GetItemText((int)index);
        if (chosen.Equals("(No Active Mod)", StringComparison.OrdinalIgnoreCase)) return;
        if (!chosen.Equals(EditorModExporter.ActiveModName, StringComparison.OrdinalIgnoreCase))
        {
            EditorModExporter.ActiveModName = chosen;
        }
        RefreshActiveMod();
    }

    private void RefreshActiveMod()
    {
        string active = EditorModExporter.ActiveModName;
        _homeView?.Refresh(active);
        _itemGraphView?.RefreshGraph();
    }

    private void OnOpenCreatorFolder()
    {
        string active = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(active)) return;
        string creatorDir = EditorModExporter.GetCreatorDirectory(active);
        if (string.IsNullOrEmpty(creatorDir)) return;
        Directory.CreateDirectory(creatorDir);
        BnkMountPlugin.Instance?.SyncActiveModCreatorFavorite(active);
        BnkMountPlugin.Instance?.RefreshAndNavigateCreatorFile(creatorDir);
        EditorModExporter.RevealInExplorer(creatorDir);
    }
}
#endif
