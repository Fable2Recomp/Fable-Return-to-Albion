#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Refii.EditorTools;
using Refii.Entities;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Assets.UI;

/// <summary>
/// 3D Viewport toolbar mounted in <see cref="EditorPlugin.CustomControlContainer.SpatialEditorMenu"/>
/// when editing a Fable 2 level scene. Provides:
/// <list type="bullet">
/// <item><description>Scenario switcher (<c>defaultscenario</c>, <c>epilogue</c>, etc.).</description></item>
/// <item><description><c>.save</c> Layers &amp; Load State manager popover (<c>AlwaysOn</c>, <c>InitiallyOn</c>, <c>InitiallyOff</c> visibility, load-state editing, and Ghost mode toggle).</description></item>
/// <item><description>Active target layer selector for new 3D placements.</description></item>
/// <item><description>Category overlay toggles (Props, Static Environment, NPCs, Village Markers, Spawners, Splines/Vaults, Triggers, Kynapse NavGraph).</description></item>
/// <item><description>1-click <b>💾 Save Level to Mod</b> compilation into <c>&lt;level&gt;.gdb.tu</c> and <c>&lt;level&gt;.save.tu</c>.</description></item>
/// </list>
/// </summary>
public partial class LevelPerspectiveToolbar : HBoxContainer
{
    private OptionButton? _scenarioDropdown;
    private Button? _layersBtn;
    private PopupPanel? _layersPopup;
    private VBoxContainer? _layersListVBox;
    private CheckButton? _ghostInitiallyOffToggle;
    private Button? _deletedItemsBtn;
    private PopupPanel? _deletedPopup;
    private VBoxContainer? _deletedListVBox;
    private OptionButton? _targetLayerDropdown;
    private MenuButton? _overlaysMenu;
    private Button? _saveLevelBtn;
    private Button? _revertBtn;
    private Label? _statusLabel;

    private bool _suppressSignals;

    private static readonly (string Label, string NodePath)[] OverlayCategories =
    {
        ("Props (1:1 GDB / .save)", "Props/Layers"),
        ("Static Environment (.engine_level)", "Props/StaticEnvironment"),
        ("Entities & NPCs", "Entities"),
        ("Markers: NPC & Village", "Markers/Category_NPC_Village"),
        ("Markers: Combat Spawners", "Markers/Category_Spawners"),
        ("Markers: Patrols, Splines & Vaults", "Markers/Category_Splines_Vaults"),
        ("Markers: Triggers & Cameras", "Markers/Category_Triggers"),
        ("Kynapse AI NavGraph (.pdl)", Engine_KynapseGraph.NodeName),
    };

    public LevelPerspectiveToolbar()
    {
        AddThemeConstantOverride("separation", 6);
        BuildUi();
        Visible = false;
    }

    private void BuildUi()
    {
        AddChild(new VSeparator());

        var badge = new Label
        {
            Text = "🗺️ Level:",
            Modulate = new Color(0.50f, 0.82f, 1.0f),
        };
        badge.AddThemeFontSizeOverride("font_size", 12);
        AddChild(badge);

        // 1. Scenario Dropdown:
        _scenarioDropdown = new OptionButton
        {
            TooltipText = "Switch active Fable 2 Scenario (.list / .gdb / .save)",
            CustomMinimumSize = new Vector2(130, 0),
        };
        _scenarioDropdown.AddThemeFontSizeOverride("font_size", 11);
        _scenarioDropdown.ItemSelected += OnScenarioSelected;
        AddChild(_scenarioDropdown);

        // 2. Layers & Load States Popover Button:
        _layersBtn = new Button
        {
            Text = "🗂️ Save Layers",
            TooltipText = "Inspect & toggle .save layers (AlwaysOn, InitiallyOn, InitiallyOff) and Ghost mode",
        };
        _layersBtn.AddThemeFontSizeOverride("font_size", 11);
        _layersBtn.Pressed += OnLayersButtonPressed;
        AddChild(_layersBtn);

        BuildLayersPopup();

        // 2b. Deleted Items Popover Button:
        _deletedItemsBtn = new Button
        {
            Text = "🗑️ Deleted (0)",
            TooltipText = "View and restore deleted vanilla level entities",
        };
        _deletedItemsBtn.AddThemeFontSizeOverride("font_size", 11);
        _deletedItemsBtn.Pressed += OnDeletedItemsButtonPressed;
        AddChild(_deletedItemsBtn);

        BuildDeletedPopup();

        // 3. Target Layer Selector:
        var targetLbl = new Label
        {
            Text = "Target:",
            Modulate = new Color(0.72f, 0.78f, 0.86f),
        };
        targetLbl.AddThemeFontSizeOverride("font_size", 11);
        AddChild(targetLbl);

        _targetLayerDropdown = new OptionButton
        {
            TooltipText = "Target .save layer where newly placed objects will be added",
            CustomMinimumSize = new Vector2(120, 0),
        };
        _targetLayerDropdown.AddThemeFontSizeOverride("font_size", 11);
        _targetLayerDropdown.ItemSelected += OnTargetLayerSelected;
        AddChild(_targetLayerDropdown);

        // 4. Category Overlays MenuButton:
        _overlaysMenu = new MenuButton
        {
            Text = "👁️ Overlays",
            TooltipText = "Toggle visibility of level categories, markers, and AI navgraph",
            Flat = false,
        };
        _overlaysMenu.AddThemeFontSizeOverride("font_size", 11);
        PopupMenu overlayPopup = _overlaysMenu.GetPopup();
        overlayPopup.HideOnCheckableItemSelection = false;
        for (int i = 0; i < OverlayCategories.Length; i++)
        {
            overlayPopup.AddCheckItem(OverlayCategories[i].Label, i);
            overlayPopup.SetItemChecked(i, true);
        }
        overlayPopup.IdPressed += OnOverlayMenuIdPressed;
        _overlaysMenu.AboutToPopup += SyncOverlayMenuChecks;
        AddChild(_overlaysMenu);

        // 5. Save Level to Mod Button:
        _saveLevelBtn = new Button
        {
            Text = "💾 Save Level to Mod",
            TooltipText = "Compile moved, added, deleted, and layer-edited GDB records into <level>.gdb.tu and <level>.save.tu in the active .f2mod package",
        };
        _saveLevelBtn.AddThemeFontSizeOverride("font_size", 11);
        _saveLevelBtn.Pressed += OnSaveLevelPressed;
        AddChild(_saveLevelBtn);

        // 5b. Revert to GDB Button:
        _revertBtn = new Button
        {
            Text = "🔄 Revert to GDB",
            TooltipText = "Revert all objects back to their original GDB transforms, restore deleted items, and remove unsaved new placements",
        };
        _revertBtn.AddThemeFontSizeOverride("font_size", 11);
        _revertBtn.Pressed += OnRevertLevelPressed;
        AddChild(_revertBtn);

        _statusLabel = new Label
        {
            Text = "",
            Modulate = new Color(0.55f, 0.92f, 0.62f),
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 11);
        AddChild(_statusLabel);
    }

    private void BuildLayersPopup()
    {
        _layersPopup = new PopupPanel
        {
            Size = new Vector2I(520, 420),
        };

        var outerVBox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(500, 390),
        };
        outerVBox.AddThemeConstantOverride("separation", 6);

        var topRow = new HBoxContainer();
        topRow.AddThemeConstantOverride("separation", 6);

        var defaultStateBtn = new Button
        {
            Text = "🎮 Default Game State (On Only)",
            TooltipText = "Show AlwaysOn + InitiallyOn layers; hide InitiallyOff layers",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        defaultStateBtn.AddThemeFontSizeOverride("font_size", 11);
        defaultStateBtn.Pressed += () => ApplyLayerVisibilityPreset(showInitiallyOff: false);
        topRow.AddChild(defaultStateBtn);

        var showAllBtn = new Button
        {
            Text = "👁️ Show All Layers",
            TooltipText = "Make all layers visible in the 3D Editor (including InitiallyOff)",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        showAllBtn.AddThemeFontSizeOverride("font_size", 11);
        showAllBtn.Pressed += () => ApplyLayerVisibilityPreset(showInitiallyOff: true);
        topRow.AddChild(showAllBtn);

        outerVBox.AddChild(topRow);

        _ghostInitiallyOffToggle = new CheckButton
        {
            Text = "👻 Ghost / Translucent Mode for InitiallyOff Objects",
            ButtonPressed = Engine_LevelProps.EditorGhostInitiallyOff,
            TooltipText = "Render InitiallyOff objects at 45% opacity so you can distinguish inactive quest/script props at a glance",
        };
        _ghostInitiallyOffToggle.AddThemeFontSizeOverride("font_size", 11);
        _ghostInitiallyOffToggle.Toggled += OnGhostToggleChanged;
        outerVBox.AddChild(_ghostInitiallyOffToggle);

        outerVBox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 290),
        };

        _layersListVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _layersListVBox.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_layersListVBox);
        outerVBox.AddChild(scroll);

        _layersPopup.AddChild(outerVBox);
        AddChild(_layersPopup);
    }

    private void BuildDeletedPopup()
    {
        _deletedPopup = new PopupPanel
        {
            Size = new Vector2I(520, 380),
        };

        var outerVBox = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(500, 350),
        };
        outerVBox.AddThemeConstantOverride("separation", 6);

        var title = new Label
        {
            Text = "🗑️ Deleted Vanilla Entities (Removed from Scene)",
            Modulate = new Color(1.0f, 0.75f, 0.45f),
        };
        title.AddThemeFontSizeOverride("font_size", 13);
        outerVBox.AddChild(title);

        var hint = new Label
        {
            Text = "Entities below are in the vanilla .save but have been deleted in the 3D scene. When you save, they will be written as removals to <level>.save.tu. Click 'Restore' to re-add any entity back to its vanilla coordinates.",
            Modulate = new Color(0.70f, 0.75f, 0.85f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        outerVBox.AddChild(hint);

        outerVBox.AddChild(new HSeparator());

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 240),
        };

        _deletedListVBox = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _deletedListVBox.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_deletedListVBox);
        outerVBox.AddChild(scroll);

        _deletedPopup.AddChild(outerVBox);
        AddChild(_deletedPopup);
    }

    private void OnDeletedItemsButtonPressed()
    {
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null || _deletedPopup is null || _deletedListVBox is null) return;

        PopulateDeletedList(levelRoot);

        Vector2 mousePos = DisplayServer.MouseGetPosition();
        _deletedPopup.Position = new Vector2I((int)mousePos.X - 250, (int)mousePos.Y + 10);
        _deletedPopup.Popup();
    }

    private void PopulateDeletedList(Node3D levelRoot)
    {
        if (_deletedListVBox is null) return;
        foreach (Node child in _deletedListVBox.GetChildren())
        {
            child.QueueFree();
        }

        List<GdbSaveEntityEntry> deleted = EditorModValueService.GetDeletedEntities(levelRoot);
        UpdateDeletedButtonText(deleted.Count);

        if (deleted.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text = "✓ No deleted entities. All vanilla .save entities are present in the scene.",
                Modulate = new Color(0.6f, 0.9f, 0.6f),
            };
            emptyLabel.AddThemeFontSizeOverride("font_size", 11);
            _deletedListVBox.AddChild(emptyLabel);
            return;
        }

        foreach (GdbSaveEntityEntry entry in deleted)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            var infoLbl = new Label
            {
                Text = $"{entry.EntityName} [0x{entry.RecordHash:X8}]  (Layer: {entry.LayerName})",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            infoLbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(infoLbl);

            var restoreBtn = new Button
            {
                Text = "↺ Restore",
                TooltipText = $"Restore '{entry.EntityName}' to layer '{entry.LayerName}' at its vanilla coordinates",
            };
            restoreBtn.AddThemeFontSizeOverride("font_size", 11);

            uint capturedHash = entry.RecordHash;
            restoreBtn.Pressed += () =>
            {
                EditorModValueService.RestoreDeletedEntity(levelRoot, capturedHash, out string _);
                PopulateDeletedList(levelRoot);
            };
            row.AddChild(restoreBtn);

            _deletedListVBox.AddChild(row);
        }
    }

    private void UpdateDeletedButtonText(int count)
    {
        if (_deletedItemsBtn is null) return;
        _deletedItemsBtn.Text = $"🗑️ Deleted ({count})";
        if (count > 0)
        {
            _deletedItemsBtn.Modulate = new Color(1.0f, 0.75f, 0.40f);
        }
        else
        {
            _deletedItemsBtn.Modulate = Colors.White;
        }
    }

    /// <summary>
    /// Refreshes the toolbar state for the given edited scene root. Hides the toolbar if the scene is not a Fable 2 level.
    /// </summary>
    public void RefreshForScene(Node? editedRoot)
    {
        Node3D? levelRoot = LevelRecordMetadata.FindLevelRootNode(editedRoot);
        if (levelRoot is null)
        {
            Visible = false;
            return;
        }

        Visible = true;
        _suppressSignals = true;

        try
        {
            if (levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta))
            {
                EditorModValueService.ActiveLevelGdbSubPath =
                    levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString();
            }

            // Populate Scenarios:
            if (_scenarioDropdown is not null)
            {
                _scenarioDropdown.Clear();
                string activeScenario = levelRoot.HasMeta(LevelRecordMetadata.ScenarioNameMeta)
                    ? levelRoot.GetMeta(LevelRecordMetadata.ScenarioNameMeta).AsString()
                    : Engine_LevelManifest.DefaultScenario;
                string[] scenarios = levelRoot.HasMeta(LevelRecordMetadata.AvailableScenariosMeta)
                    ? levelRoot.GetMeta(LevelRecordMetadata.AvailableScenariosMeta).AsStringArray()
                    : new[] { activeScenario };

                if (scenarios.Length == 0)
                {
                    scenarios = new[] { Engine_LevelManifest.DefaultScenario };
                }

                int selectedIdx = 0;
                for (int i = 0; i < scenarios.Length; i++)
                {
                    _scenarioDropdown.AddItem(scenarios[i], i);
                    if (string.Equals(scenarios[i], activeScenario, StringComparison.OrdinalIgnoreCase))
                    {
                        selectedIdx = i;
                    }
                }
                _scenarioDropdown.Selected = selectedIdx;
                _scenarioDropdown.Disabled = scenarios.Length <= 1;
            }

            // Populate Target Layers:
            if (_targetLayerDropdown is not null)
            {
                _targetLayerDropdown.Clear();
                List<string> layers = LevelNodeGdbInspectorCard.CollectKnownLayerNames(
                    levelRoot, EditorModValueService.ActiveTargetSaveLayer);
                int selIdx = 0;
                for (int i = 0; i < layers.Count; i++)
                {
                    _targetLayerDropdown.AddItem(layers[i], i);
                    if (string.Equals(layers[i], EditorModValueService.ActiveTargetSaveLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        selIdx = i;
                    }
                }
                _targetLayerDropdown.Selected = selIdx;
            }

            if (_layersBtn is not null)
            {
                int layerCount = CollectLayerGroups(levelRoot).Count;
                _layersBtn.Text = $"🗂️ Save Layers ({layerCount})";
            }

            if (_deletedItemsBtn is not null)
            {
                UpdateDeletedButtonText(EditorModValueService.GetDeletedEntities(levelRoot).Count);
            }

            if (_statusLabel is not null)
            {
                _statusLabel.Text = "";
            }
        }
        finally
        {
            _suppressSignals = false;
        }
    }

    private void OnScenarioSelected(long index)
    {
        if (_suppressSignals || _scenarioDropdown is null) return;
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        int idx = (int)index;
        if (idx < 0 || idx >= _scenarioDropdown.ItemCount) return;

        string chosenScenario = _scenarioDropdown.GetItemText(idx);
        string levelName = levelRoot.HasMeta(LevelRecordMetadata.LevelNameMeta)
            ? levelRoot.GetMeta(LevelRecordMetadata.LevelNameMeta).AsString()
            : levelRoot.Name.ToString();

        BnkMountPlugin.Instance?.RebuildLevelWithScenario(levelName, chosenScenario);
    }

    private void OnTargetLayerSelected(long index)
    {
        if (_suppressSignals || _targetLayerDropdown is null) return;
        int idx = (int)index;
        if (idx < 0 || idx >= _targetLayerDropdown.ItemCount) return;
        EditorModValueService.ActiveTargetSaveLayer = _targetLayerDropdown.GetItemText(idx);
    }

    private void OnLayersButtonPressed()
    {
        if (_layersPopup is null || _layersBtn is null) return;
        PopulateLayersPopupList();

        Vector2I btnPos = (Vector2I)_layersBtn.GetScreenPosition();
        Vector2I popupPos = new(btnPos.X, btnPos.Y + (int)_layersBtn.Size.Y + 4);
        _layersPopup.Position = popupPos;
        _layersPopup.Popup();
    }

    private void PopulateLayersPopupList()
    {
        if (_layersListVBox is null) return;
        foreach (Node child in _layersListVBox.GetChildren())
        {
            child.QueueFree();
        }

        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        var groups = CollectLayerGroups(levelRoot);
        foreach (var group in groups)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            bool isVisible = group.Containers.Exists(c => c.Visible);
            var visCheck = new CheckBox
            {
                ButtonPressed = isVisible,
                TooltipText = $"Show/hide layer '{group.LayerName}' in the 3D viewport",
            };
            List<Node3D> capturedContainers = group.Containers;
            visCheck.Toggled += pressed =>
            {
                foreach (Node3D c in capturedContainers)
                {
                    if (GodotObject.IsInstanceValid(c))
                    {
                        c.Visible = pressed;
                    }
                }
            };
            row.AddChild(visCheck);

            var nameLbl = new Label
            {
                Text = $"{group.LayerName} ({group.EntityCount})",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
                Modulate = GetLoadStateColor(group.LoadState),
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(nameLbl);

            var stateDropdown = new OptionButton
            {
                CustomMinimumSize = new Vector2(130, 0),
            };
            stateDropdown.AddThemeFontSizeOverride("font_size", 10);
            GdbSaveLoadState[] states =
            {
                GdbSaveLoadState.AlwaysOn,
                GdbSaveLoadState.InitiallyOn,
                GdbSaveLoadState.InitiallyOff,
            };
            for (int i = 0; i < states.Length; i++)
            {
                stateDropdown.AddItem(states[i].ToXmlAttribute(), i);
                if (states[i] == group.LoadState)
                {
                    stateDropdown.Selected = i;
                }
            }

            string capturedName = group.LayerName;
            Node3D firstContainer = group.Containers[0];
            stateDropdown.ItemSelected += idx =>
            {
                if (idx < 0 || idx >= states.Length) return;
                GdbSaveLoadState chosen = states[(int)idx];
                LevelNodeGdbInspectorCard.ApplyLayerContainerLoadState(firstContainer, capturedName, chosen);
                nameLbl.Modulate = GetLoadStateColor(chosen);
            };
            row.AddChild(stateDropdown);

            _layersListVBox.AddChild(row);
        }
    }

    private void ApplyLayerVisibilityPreset(bool showInitiallyOff)
    {
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        foreach (Node3D container in LevelNodeGdbInspectorCard.EnumerateAllLayerContainers(levelRoot))
        {
            GdbSaveLoadState state = container.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
                ? GdbSaveLoadStateExtensions.ParseLoadState(container.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString())
                : GdbSaveLoadState.AlwaysOn;

            container.Visible = showInitiallyOff || state != GdbSaveLoadState.InitiallyOff;
        }

        PopulateLayersPopupList();
    }

    private void OnGhostToggleChanged(bool pressed)
    {
        Engine_LevelProps.EditorGhostInitiallyOff = pressed;
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        foreach (Node3D container in LevelNodeGdbInspectorCard.EnumerateAllLayerContainers(levelRoot))
        {
            GdbSaveLoadState state = container.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
                ? GdbSaveLoadStateExtensions.ParseLoadState(container.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString())
                : GdbSaveLoadState.AlwaysOn;

            foreach (Node child in container.GetChildren())
            {
                if (child is Node3D child3D && LevelRecordMetadata.IsRecordNode(child3D))
                {
                    Engine_LevelProps.ApplyEditorLoadStateVisuals(child3D, state, pressed);
                }
            }
        }
    }

    private void SyncOverlayMenuChecks()
    {
        if (_overlaysMenu is null) return;
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        PopupMenu popup = _overlaysMenu.GetPopup();
        for (int i = 0; i < OverlayCategories.Length; i++)
        {
            Node3D? target = levelRoot.GetNodeOrNull<Node3D>(OverlayCategories[i].NodePath);
            popup.SetItemDisabled(i, target is null);
            if (target is not null)
            {
                popup.SetItemChecked(i, target.Visible);
            }
        }
    }

    private void OnOverlayMenuIdPressed(long id)
    {
        if (_overlaysMenu is null) return;
        int idx = (int)id;
        if (idx < 0 || idx >= OverlayCategories.Length) return;

        Node3D? levelRoot = GetActiveLevelRoot();
        Node3D? target = levelRoot?.GetNodeOrNull<Node3D>(OverlayCategories[idx].NodePath);
        if (target is null) return;

        target.Visible = !target.Visible;
        _overlaysMenu.GetPopup().SetItemChecked(idx, target.Visible);
    }

    private void OnSaveLevelPressed()
    {
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        string activeMod = EditorModExporter.ActiveModName;
        if (EditorModValueService.SaveActiveLevelSceneToMod(
                levelRoot,
                activeMod,
                out LevelPatchCompileResult result,
                out string error))
        {
            string summary =
                $"✓ Saved [{activeMod}]: {result.ModifiedTransformCount} moved, " +
                $"{result.NewPlacementCount} added, {result.DeletedRecordCount} removed, " +
                $"{result.LayerStateOverrideCount} layer state(s)";
            if (_statusLabel is not null)
            {
                _statusLabel.Text = summary;
                _statusLabel.Modulate = new Color(0.50f, 0.95f, 0.62f);
            }
            if (_deletedItemsBtn is not null)
            {
                UpdateDeletedButtonText(EditorModValueService.GetDeletedEntities(levelRoot).Count);
            }
            GD.Print($"[level_edit] {summary}");
        }
        else
        {
            if (_statusLabel is not null)
            {
                _statusLabel.Text = $"⚠️ {error}";
                _statusLabel.Modulate = new Color(1.0f, 0.48f, 0.48f);
            }
            GD.PushWarning($"[level_edit] Failed to save level to mod: {error}");
        }
    }

    private void OnRevertLevelPressed()
    {
        Node3D? levelRoot = GetActiveLevelRoot();
        if (levelRoot is null) return;

        EditorModValueService.RevertLevelSceneToGdbTransforms(levelRoot);
        UpdateDeletedButtonText(EditorModValueService.GetDeletedEntities(levelRoot).Count);
        if (_statusLabel is not null)
        {
            _statusLabel.Text = "↺ Reverted scene to GDB transforms.";
            _statusLabel.Modulate = new Color(0.70f, 0.85f, 1.0f);
        }
        GD.Print("[level_edit] Reverted level scene to GDB baseline transforms.");
    }

    private static Node3D? GetActiveLevelRoot()
    {
        try
        {
            return LevelRecordMetadata.FindLevelRootNode(EditorInterface.Singleton.GetEditedSceneRoot());
        }
        catch
        {
            return null;
        }
    }

    private sealed class LayerGroupInfo
    {
        public string LayerName { get; init; } = string.Empty;
        public GdbSaveLoadState LoadState { get; set; }
        public int EntityCount { get; set; }
        public List<Node3D> Containers { get; } = new();
    }

    private static List<LayerGroupInfo> CollectLayerGroups(Node3D levelRoot)
    {
        var map = new Dictionary<string, LayerGroupInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (Node3D container in LevelNodeGdbInspectorCard.EnumerateAllLayerContainers(levelRoot))
        {
            string name = container.HasMeta(LevelRecordMetadata.LayerNameMeta)
                ? container.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString()
                : container.Name.ToString();
            GdbSaveLoadState state = container.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
                ? GdbSaveLoadStateExtensions.ParseLoadState(container.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString())
                : GdbSaveLoadState.AlwaysOn;

            if (!map.TryGetValue(name, out LayerGroupInfo? info))
            {
                info = new LayerGroupInfo
                {
                    LayerName = name,
                    LoadState = state,
                };
                map[name] = info;
            }

            info.Containers.Add(container);
            info.EntityCount += container.GetChildCount();
        }

        var list = new List<LayerGroupInfo>(map.Values);
        list.Sort((a, b) => string.Compare(a.LayerName, b.LayerName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static Color GetLoadStateColor(GdbSaveLoadState state) => state switch
    {
        GdbSaveLoadState.AlwaysOn => new Color(0.55f, 0.95f, 0.65f),
        GdbSaveLoadState.InitiallyOn => new Color(0.55f, 0.82f, 1.0f),
        GdbSaveLoadState.InitiallyOff => new Color(1.0f, 0.72f, 0.42f),
        _ => Colors.White,
    };
}
#endif
