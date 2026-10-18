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
/// Dedicated dock panel for Fable 2 Mod Studio, specifically designed for narrow vertical
/// sidebar layouts (DockSlot.RightUl, 200px - 400px wide).
/// Provides zero horizontal clamping, compact action menu, icon domain navigation,
/// and live reactive synchronization with mod content changes.
/// </summary>
public partial class ModStudioDockPanel : MarginContainer
{
    private OptionButton? _modSelectDropdown;
    private MenuButton? _hamburgerMenu;

    private const int MenuActionNewMod = 1;
    private const int MenuActionEditMeta = 2;
    private const int MenuActionCreatorFolder = 3;
    private const int MenuActionRefresh = 4;
    private const int MenuActionDeleteMod = 5;
    private const int MenuActionShare = 6;

    // Home view (dock version presents the central home dashboard)
    private ModdingHubHomeView? _homeView;

    // Dialogs
    private CreateModDialog? _createModDialog;
    private ModMetadataDialog? _metadataDialog;
    private ConfirmationDialog? _deleteConfirmDialog;

    public ModStudioDockPanel()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        SetAnchorsPreset(Control.LayoutPreset.FullRect);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;

        AddThemeConstantOverride("margin_left", 4);
        AddThemeConstantOverride("margin_top", 4);
        AddThemeConstantOverride("margin_right", 4);
        AddThemeConstantOverride("margin_bottom", 4);

        var mainVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        mainVBox.AddThemeConstantOverride("separation", 6);
        AddChild(mainVBox);

        BuildHeader(mainVBox);
        BuildContentArea(mainVBox);
        BuildDialogs();

        RefreshModList();
    }

    public override void _EnterTree()
    {
        base._EnterTree();
        EditorModExporter.ActiveModChanged += OnExternalActiveModChanged;
        EditorModExporter.ModContentChanged += OnExternalModContentChanged;
    }

    public override void _ExitTree()
    {
        EditorModExporter.ActiveModChanged -= OnExternalActiveModChanged;
        EditorModExporter.ModContentChanged -= OnExternalModContentChanged;
        base._ExitTree();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        if (what == NotificationWMWindowFocusIn)
        {
            // Auto-refresh when returning to Godot from an external DCC tool or file edit:
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

    private void BuildHeader(VBoxContainer parent)
    {
        var header = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        header.AddThemeConstantOverride("separation", 6);

        var modLabel = new Label
        {
            Text = "Mod:",
            Modulate = new Color(0.75f, 0.78f, 0.85f)
        };
        modLabel.AddThemeFontSizeOverride("font_size", 12);
        header.AddChild(modLabel);

        _modSelectDropdown = new OptionButton
        {
            CustomMinimumSize = new Vector2(80, 26),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        _modSelectDropdown.ItemSelected += OnModSelected;
        header.AddChild(_modSelectDropdown);

        _hamburgerMenu = new MenuButton
        {
            Text = "☰",
            TooltipText = "Mod Actions & Tools",
            CustomMinimumSize = new Vector2(28, 26)
        };
        var popup = _hamburgerMenu.GetPopup();
        popup.AddItem("+ New Mod", MenuActionNewMod);
        popup.AddItem("✎ Edit Metadata", MenuActionEditMeta);
        popup.AddItem("📁 Open Creator Folder", MenuActionCreatorFolder);
        popup.AddItem("🔄 Refresh Content", MenuActionRefresh);
        popup.AddItem("📦 Share package…", MenuActionShare);
        popup.AddSeparator();
        popup.AddItem("🗑️ Delete Mod...", MenuActionDeleteMod);
        popup.IdPressed += OnHamburgerMenuIdPressed;
        header.AddChild(_hamburgerMenu);

        parent.AddChild(header);
        parent.AddChild(new HSeparator());
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
                RefreshActiveMod();
                break;
            case MenuActionDeleteMod:
                OnDeleteModPressed();
                break;
            case MenuActionShare:
                OnSharePressed();
                break;
        }
    }

    /// <summary>
    /// Asks for a folder and writes the active mod there as one .f2mod a player installs from the
    /// launcher (<see cref="RefiiModApi.SharePackage"/>), which opens the folder; a refusal is shown.
    /// </summary>
    private void OnSharePressed()
    {
        string active = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(active)) return;

        var picker = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = $"Share '{active}' — choose a folder for {active}{ModWorkspace.PackageExtension}",
            CurrentDir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            Size = new Vector2I(760, 520),
        };
        picker.DirSelected += dir =>
        {
            string error = new RefiiModApi().SharePackage(dir);
            if (error.Length == 0) return;

            var refusal = new AcceptDialog
            {
                Title = "Share package",
                DialogText = $"'{active}' was not shared: {error}",
                DialogAutowrap = true,
                MinSize = new Vector2I(420, 0),
            };
            refusal.VisibilityChanged += () =>
            {
                if (!refusal.Visible) refusal.QueueFree();
            };
            AddChild(refusal);
            refusal.PopupCentered();
        };
        picker.VisibilityChanged += () =>
        {
            if (!picker.Visible) picker.QueueFree();
        };
        AddChild(picker);
        picker.PopupCentered();
    }

    private void BuildContentArea(VBoxContainer parent)
    {
        var contentContainer = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.12f, 0.13f, 0.16f),
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        contentContainer.AddThemeStyleboxOverride("panel", panelStyle);

        _homeView = new ModdingHubHomeView();
        contentContainer.AddChild(_homeView);

        parent.AddChild(contentContainer);
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
                $"• assets/creator/{active}/\n\n" +
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
        RefreshActiveMod();
    }

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
            UpdateHamburgerMenuState(false);
            RefreshActiveMod();
            return;
        }

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
        popup.SetItemDisabled(popup.GetItemIndex(MenuActionShare), !hasMod);
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
    }

    private void OnOpenCreatorFolder()
    {
        string active = EditorModExporter.ActiveModName;
        if (string.IsNullOrWhiteSpace(active)) return;
        string creatorDir = Path.Combine(Engine_AppRoot.AssetsDirectory, "creator", active);
        Directory.CreateDirectory(creatorDir);
        EditorModExporter.RevealInExplorer(creatorDir);
    }
}
#endif
