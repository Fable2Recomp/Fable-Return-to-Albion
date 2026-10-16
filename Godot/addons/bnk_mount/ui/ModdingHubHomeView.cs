#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Equipment;
using Refii.Gdb;
using Refii.Mods;

namespace Refii.Assets.UI;

/// <summary>
/// Homepage dashboard view for the Fable 2 Mod Studio dock/window.
/// Displays isolated active mod metrics, conflict status, and an interactive content traversal tree.
/// </summary>
public partial class ModdingHubHomeView : VBoxContainer
{
    private string _activeMod = string.Empty;
    private ModContentSummary? _currentSummary;

    // Metric Labels
    private Label? _recordsMetricLabel;
    private Label? _babelMetricLabel;
    private Label? _scriptsMetricLabel;
    private Label? _creatorMetricLabel;

    // Conflict Status Banner
    private PanelContainer? _conflictPanel;
    private Label? _conflictLabel;

    // Search and Tree
    private LineEdit? _filterEdit;
    private Tree? _traversalTree;
    private Button? _editButton;
    private Button? _revertButton;

    public ModdingHubHomeView()
    {
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 10);

        BuildSummaryCards();
        BuildConflictBanner();
        BuildTraversalTreeSection();
    }

    private void BuildSummaryCards()
    {
        var grid = new HFlowContainer();
        grid.AddThemeConstantOverride("h_separation", 8);
        grid.AddThemeConstantOverride("v_separation", 6);
        grid.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        grid.AddChild(CreateMetricCard("🗡️", "GDB Records", out _recordsMetricLabel));
        grid.AddChild(CreateMetricCard("💬", "Babel Strings", out _babelMetricLabel));
        grid.AddChild(CreateMetricCard("📜", "Lua Scripts", out _scriptsMetricLabel));
        grid.AddChild(CreateMetricCard("📁", "Creator Assets", out _creatorMetricLabel));

        AddChild(grid);
    }

    private static PanelContainer CreateMetricCard(string icon, string labelText, out Label valueLabel)
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(75, 0)
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.16f, 0.18f, 0.22f),
            BorderColor = new Color(0.26f, 0.30f, 0.36f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 10,
            ContentMarginRight = 10,
            ContentMarginTop = 8,
            ContentMarginBottom = 8
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 2);

        var headerRow = new HBoxContainer();
        var iconLabel = new Label { Text = icon };
        headerRow.AddChild(iconLabel);

        var title = new Label
        {
            Text = labelText,
            Modulate = new Color(0.72f, 0.75f, 0.80f)
        };
        title.AddThemeFontSizeOverride("font_size", 12);
        headerRow.AddChild(title);
        vbox.AddChild(headerRow);

        valueLabel = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        valueLabel.AddThemeFontSizeOverride("font_size", 18);
        vbox.AddChild(valueLabel);

        panel.AddChild(vbox);
        return panel;
    }

    private void BuildConflictBanner()
    {
        _conflictPanel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.20f, 0.15f),
            BorderColor = new Color(0.25f, 0.45f, 0.30f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        _conflictPanel.AddThemeStyleboxOverride("panel", style);

        _conflictLabel = new Label
        {
            Text = "✓ No Conflicts with active load order",
            Modulate = new Color(0.55f, 0.90f, 0.60f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _conflictLabel.AddThemeFontSizeOverride("font_size", 12);
        _conflictPanel.AddChild(_conflictLabel);

        AddChild(_conflictPanel);
    }

    private void BuildTraversalTreeSection()
    {
        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        header.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var title = new Label
        {
            Text = "Active Mod Content Traversal",
            Modulate = new Color(0.9f, 0.92f, 0.96f),
            ClipText = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        title.AddThemeFontSizeOverride("font_size", 14);
        header.AddChild(title);

        _filterEdit = new LineEdit
        {
            PlaceholderText = "Filter mod contents...",
            ClearButtonEnabled = true,
            CustomMinimumSize = new Vector2(90, 26)
        };
        _filterEdit.TextChanged += _ => RefreshTree();
        header.AddChild(_filterEdit);

        AddChild(header);

        _traversalTree = new Tree
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Columns = 3,
            ColumnTitlesVisible = true,
            HideRoot = true
        };
        _traversalTree.SetColumnTitle(0, "Item / Resource");
        _traversalTree.SetColumnTitle(1, "Identifier");
        _traversalTree.SetColumnTitle(2, "Modification Summary");
        _traversalTree.SetColumnExpand(0, true);
        _traversalTree.SetColumnExpandRatio(0, 3);
        _traversalTree.SetColumnClipContent(0, true);
        _traversalTree.SetColumnExpand(1, true);
        _traversalTree.SetColumnExpandRatio(1, 2);
        _traversalTree.SetColumnCustomMinimumWidth(1, 160);
        _traversalTree.SetColumnClipContent(1, true);
        _traversalTree.SetColumnExpand(2, true);
        _traversalTree.SetColumnExpandRatio(2, 4);
        _traversalTree.SetColumnClipContent(2, true);

        _traversalTree.ItemActivated += OnTreeItemActivated;
        _traversalTree.ItemSelected += OnTreeItemSelected;

        AddChild(_traversalTree);

        var actionRow = new HFlowContainer();
        actionRow.AddThemeConstantOverride("h_separation", 8);
        actionRow.AddThemeConstantOverride("v_separation", 4);
        actionRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        _editButton = new Button
        {
            Text = "✎ Edit Selected",
            Disabled = true
        };
        _editButton.Pressed += OnEditPressed;
        actionRow.AddChild(_editButton);

        _revertButton = new Button
        {
            Text = "↺ Revert Selected",
            Disabled = true,
            TooltipText = "Purge this override from the active mod package"
        };
        _revertButton.Pressed += OnRevertPressed;
        actionRow.AddChild(_revertButton);

        AddChild(actionRow);
    }

    /// <summary>
    /// Refreshes the homepage data for the specified active mod with strict isolation.
    /// </summary>
    /// <param name="modName">Name of the active mod.</param>
    public void Refresh(string modName)
    {
        _activeMod = modName;
        if (string.IsNullOrEmpty(modName))
        {
            _currentSummary = new ModContentSummary
            {
                ModName = string.Empty,
                ConflictStatusText = "No active mod selected"
            };

            if (_recordsMetricLabel is not null) _recordsMetricLabel.Text = "0";
            if (_babelMetricLabel is not null) _babelMetricLabel.Text = "0";
            if (_scriptsMetricLabel is not null) _scriptsMetricLabel.Text = "0";
            if (_creatorMetricLabel is not null) _creatorMetricLabel.Text = "0";

            if (_conflictLabel is not null)
            {
                _conflictLabel.Text = "No active mod selected. Click '+ New Mod' above to create a mod.";
                _conflictLabel.Modulate = new Color(0.7f, 0.7f, 0.7f);
            }

            if (_conflictPanel is not null)
            {
                var emptyStyle = new StyleBoxFlat
                {
                    BgColor = new Color(0.14f, 0.16f, 0.18f),
                    BorderColor = new Color(0.24f, 0.28f, 0.32f),
                    BorderWidthLeft = 1,
                    BorderWidthTop = 1,
                    BorderWidthRight = 1,
                    BorderWidthBottom = 1,
                    CornerRadiusBottomLeft = 4,
                    CornerRadiusBottomRight = 4,
                    CornerRadiusTopLeft = 4,
                    CornerRadiusTopRight = 4,
                    ContentMarginLeft = 12,
                    ContentMarginRight = 12,
                    ContentMarginTop = 6,
                    ContentMarginBottom = 6
                };
                _conflictPanel.AddThemeStyleboxOverride("panel", emptyStyle);
            }

            RefreshTree();
            return;
        }

        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        _currentSummary = ModContentScanner.ScanMod(modsRoot, modName);

        // Update metrics
        if (_recordsMetricLabel is not null) _recordsMetricLabel.Text = _currentSummary.PatchedRecordCount.ToString();
        if (_babelMetricLabel is not null) _babelMetricLabel.Text = _currentSummary.LocalizedStringCount.ToString();
        if (_scriptsMetricLabel is not null) _scriptsMetricLabel.Text = _currentSummary.ScriptCount.ToString();
        if (_creatorMetricLabel is not null) _creatorMetricLabel.Text = _currentSummary.CreatorAssetCount.ToString();

        // Update conflict banner
        if (_conflictLabel is not null)
        {
            _conflictLabel.Text = _currentSummary.ConflictStatusText;
            _conflictLabel.Modulate = _currentSummary.HasConflicts
                ? new Color(1.0f, 0.65f, 0.35f)
                : new Color(0.55f, 0.90f, 0.60f);
        }

        if (_conflictPanel is not null)
        {
            var style = new StyleBoxFlat
            {
                BgColor = _currentSummary.HasConflicts ? new Color(0.24f, 0.16f, 0.12f) : new Color(0.13f, 0.20f, 0.15f),
                BorderColor = _currentSummary.HasConflicts ? new Color(0.55f, 0.35f, 0.20f) : new Color(0.25f, 0.45f, 0.30f),
                BorderWidthLeft = 1,
                BorderWidthTop = 1,
                BorderWidthRight = 1,
                BorderWidthBottom = 1,
                CornerRadiusBottomLeft = 4,
                CornerRadiusBottomRight = 4,
                CornerRadiusTopLeft = 4,
                CornerRadiusTopRight = 4,
                ContentMarginLeft = 12,
                ContentMarginRight = 12,
                ContentMarginTop = 6,
                ContentMarginBottom = 6
            };
            _conflictPanel.AddThemeStyleboxOverride("panel", style);
        }

        RefreshTree();
    }

    private void RefreshTree()
    {
        if (_traversalTree is null || _currentSummary is null)
        {
            return;
        }

        _traversalTree.Clear();
        TreeItem root = _traversalTree.CreateItem();

        if (string.IsNullOrEmpty(_activeMod))
        {
            TreeItem empty = _traversalTree.CreateItem(root);
            empty.SetText(0, "No active mod selected.");
            empty.SetText(1, "Click '+ New Mod' above");
            empty.SetText(2, "Create a .f2mod package to start authoring overrides.");
            empty.SetSelectable(0, false);
            empty.SetSelectable(1, false);
            empty.SetSelectable(2, false);
            UpdateActionButtons();
            return;
        }

        if (_currentSummary.Entries.Count == 0)
        {
            TreeItem empty = _traversalTree.CreateItem(root);
            empty.SetText(0, $"'{_activeMod}' has 0 overrides");
            empty.SetText(1, "Empty mod package");
            empty.SetText(2, "Use the workspaces below to edit weapons, items, characters, or text.");
            empty.SetSelectable(0, false);
            empty.SetSelectable(1, false);
            empty.SetSelectable(2, false);
            UpdateActionButtons();
            return;
        }

        string filter = _filterEdit?.Text.Trim() ?? string.Empty;

        var categories = new Dictionary<string, List<ModAuthoredEntry>>();

        foreach (ModAuthoredEntry entry in _currentSummary.Entries)
        {
            if (filter.Length > 0 &&
                !entry.Label.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !entry.PathOrKey.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !entry.Details.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!categories.TryGetValue(entry.Category, out var list))
            {
                list = new List<ModAuthoredEntry>();
                categories[entry.Category] = list;
            }
            list.Add(entry);
        }

        foreach (var (categoryName, entries) in categories)
        {
            TreeItem catItem = _traversalTree.CreateItem(root);
            catItem.SetText(0, $"{categoryName} ({entries.Count})");
            catItem.SetSelectable(0, false);
            catItem.SetSelectable(1, false);
            catItem.SetSelectable(2, false);

            foreach (ModAuthoredEntry e in entries)
            {
                TreeItem item = _traversalTree.CreateItem(catItem);
                item.SetText(0, e.Label);
                item.SetText(1, e.Subtitle);
                item.SetText(2, e.Details);
                item.SetMetadata(0, e.PathOrKey);
                item.SetMetadata(1, (int)e.Kind);
                item.SetMetadata(2, e.ContainerSubPath);
            }
        }

        UpdateActionButtons();
    }

    private void OnTreeItemSelected() => UpdateActionButtons();

    private void UpdateActionButtons()
    {
        TreeItem? selected = _traversalTree?.GetSelected();
        bool isValid = selected is not null && selected.GetParent() != _traversalTree?.GetRoot();
        if (_editButton is not null) _editButton.Disabled = !isValid;
        if (_revertButton is not null) _revertButton.Disabled = !isValid;
    }

    private void OnTreeItemActivated() => OnEditPressed();

    private void OnEditPressed()
    {
        TreeItem? selected = _traversalTree?.GetSelected();
        if (selected is null) return;

        var kind = (ModAuthoredKind)selected.GetMetadata(1).AsInt32();
        string key = selected.GetMetadata(0).AsString();

        if (kind == ModAuthoredKind.GdbRecord)
        {
            if (uint.TryParse(key.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint hash))
            {
                OpenItemEditorForRecord(hash);
            }
        }
        else if (kind == ModAuthoredKind.CreatorAsset)
        {
            string creatorDir = System.IO.Path.Combine(Engine_AppRoot.AssetsDirectory, "creator", _activeMod, key);
            EditorModExporter.RevealInExplorer(creatorDir);
        }
    }

    private void OpenItemEditorForRecord(uint recordHash)
    {
        GdbMulti? baseMulti = Engine_GameGdb.BaseDatabase ?? Engine_GameGdb.Database;
        if (baseMulti is null) return;

        GdbMulti multi = EditorModValueService.BuildActiveModGdbMulti(baseMulti, _activeMod);
        ItemDetail? detail = Engine_ItemDetails.BuildOne(multi, recordHash);
        if (detail is not null)
        {
            var dlg = new ModItemEditorDialog();
            AddChild(dlg);
            dlg.OpenForItem(detail);
            return;
        }

        ModdingHubPanel.Instance?.OpenGdbNode(recordHash);
    }

    private void OnRevertPressed()
    {
        TreeItem? selected = _traversalTree?.GetSelected();
        if (selected is null) return;

        var kind = (ModAuthoredKind)selected.GetMetadata(1).AsInt32();
        string key = selected.GetMetadata(0).AsString();
        string subPath = selected.GetMetadata(2).AsString();

        string modsRoot = Engine_GameAssets.ModsRoot;
        if (string.IsNullOrEmpty(modsRoot))
        {
            modsRoot = ModWorkspace.GetDefaultModsRoot(Engine_AppRoot.AssetsDirectory);
        }

        bool success = false;
        string error = string.Empty;

        if (kind == ModAuthoredKind.GdbRecord)
        {
            if (uint.TryParse(key.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out uint hash))
            {
                success = ModContentScanner.RevertGdbRecord(modsRoot, _activeMod, subPath, hash, out error);
            }
        }
        else if (kind == ModAuthoredKind.BabelString)
        {
            success = ModContentScanner.RevertBabelString(modsRoot, _activeMod, key, out error);
        }

        if (success)
        {
            GD.Print($"[mod_studio] Reverted {key} from {_activeMod}");
            BnkMountPlugin.Instance?.ReloadModOverlays();
            Refresh(_activeMod);
            EditorModExporter.NotifyModContentChanged(_activeMod);
        }
        else if (!string.IsNullOrEmpty(error))
        {
            GD.PushWarning($"[mod_studio] Failed to revert {key}: {error}");
        }
    }
}
#endif
