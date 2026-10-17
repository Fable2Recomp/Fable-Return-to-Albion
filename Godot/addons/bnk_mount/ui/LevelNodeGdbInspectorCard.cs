#if TOOLS
using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Assets.UI;

/// <summary>
/// Inspector card injected by <see cref="EntityInspectorPlugin"/> when selecting any 3D level node:
/// <list type="bullet">
/// <item><description>1:1 GDB Record nodes (or their child meshes): displays record/parent hashes, links to Mod Studio, and allows editing <c>.save</c> layer &amp; load state (<c>AlwaysOn</c>, <c>InitiallyOn</c>, <c>InitiallyOff</c>).</description></item>
/// <item><description><c>.save</c> Layer Container nodes: allows changing the layer's load state and setting it as the active target layer for new placements.</description></item>
/// <item><description>Pre-baked <c>StaticEnvironment</c> (<c>.engine_level</c>) nodes: reverse-matches the <c>.mdl</c> path to GDB archetypes so users can jump to the archetype in Mod Studio or spawn a 1:1 editable GDB placement.</description></item>
/// </list>
/// </summary>
public partial class LevelNodeGdbInspectorCard : VBoxContainer
{
    private static readonly GdbSaveLoadState[] LoadStateOptions =
    {
        GdbSaveLoadState.AlwaysOn,
        GdbSaveLoadState.InitiallyOn,
        GdbSaveLoadState.InitiallyOff,
    };

    public static bool CanInspect(GodotObject? godotObject)
    {
        if (godotObject is not Node node)
        {
            return false;
        }

        if (LevelRecordMetadata.FindOwningRecordNode(node) is not null)
        {
            return true;
        }

        if (node.HasMeta(LevelRecordMetadata.IsLayerContainerMeta) &&
            node.GetMeta(LevelRecordMetadata.IsLayerContainerMeta).AsBool())
        {
            return true;
        }

        if (node.HasMeta(LevelRecordMetadata.ModelPathMeta))
        {
            return true;
        }

        return false;
    }

    public LevelNodeGdbInspectorCard(Node selectedNode)
    {
        AddThemeConstantOverride("separation", 6);

        if (selectedNode.HasMeta(LevelRecordMetadata.IsLayerContainerMeta) &&
            selectedNode.GetMeta(LevelRecordMetadata.IsLayerContainerMeta).AsBool() &&
            selectedNode is Node3D layerNode)
        {
            BuildLayerContainerSection(layerNode);
            return;
        }

        Node3D? recordNode = LevelRecordMetadata.FindOwningRecordNode(selectedNode);
        if (recordNode is not null)
        {
            BuildRecordNodeSection(recordNode, selectedNode);
            return;
        }

        if (selectedNode.HasMeta(LevelRecordMetadata.ModelPathMeta))
        {
            BuildStaticModelArchetypeSection(selectedNode);
        }
    }

    private void BuildLayerContainerSection(Node3D layerContainer)
    {
        string layerName = layerContainer.HasMeta(LevelRecordMetadata.LayerNameMeta)
            ? layerContainer.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString()
            : layerContainer.Name.ToString();
        string layerTag = layerContainer.HasMeta(LevelRecordMetadata.LayerTagMeta)
            ? layerContainer.GetMeta(LevelRecordMetadata.LayerTagMeta).AsString()
            : string.Empty;
        GdbSaveLoadState currentLoad = layerContainer.HasMeta(LevelRecordMetadata.LayerLoadStateMeta)
            ? GdbSaveLoadStateExtensions.ParseLoadState(layerContainer.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString())
            : GdbSaveLoadState.AlwaysOn;

        AddChild(CreateHeader($"🗂️ Level Save Layer: {layerName}"));

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 4);
        AddKeyValueRow(grid, "Layer Name:", layerName);
        if (!string.IsNullOrEmpty(layerTag))
        {
            AddKeyValueRow(grid, "XML Tag:", layerTag);
        }
        AddKeyValueRow(grid, "Direct Entities:", layerContainer.GetChildCount().ToString());
        AddChild(grid);

        var loadRow = new HBoxContainer();
        loadRow.AddThemeConstantOverride("separation", 8);
        loadRow.AddChild(new Label
        {
            Text = "Load State:",
            Modulate = new Color(0.75f, 0.82f, 0.92f),
        });

        var loadDropdown = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        for (int i = 0; i < LoadStateOptions.Length; i++)
        {
            GdbSaveLoadState state = LoadStateOptions[i];
            loadDropdown.AddItem(FormatLoadStateLabel(state), i);
            if (state == currentLoad)
            {
                loadDropdown.Selected = i;
            }
        }

        loadDropdown.ItemSelected += idx =>
        {
            if (idx < 0 || idx >= LoadStateOptions.Length) return;
            GdbSaveLoadState newState = LoadStateOptions[idx];
            ApplyLayerContainerLoadState(layerContainer, layerName, newState);
        };
        loadRow.AddChild(loadDropdown);
        AddChild(loadRow);

        var setTargetBtn = new Button
        {
            Text = $"📌 Set '{layerName}' as Target Layer for New Placements",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        setTargetBtn.Pressed += () =>
        {
            EditorModValueService.ActiveTargetSaveLayer = layerName;
            GD.Print($"[level_edit] Active target .save layer set to '{layerName}'.");
        };
        AddChild(setTargetBtn);
    }

    private void BuildRecordNodeSection(Node3D recordNode, Node selectedNode)
    {
        Node3D? levelRoot = LevelRecordMetadata.FindLevelRootNode(recordNode);
        uint recordHash = LevelRecordMetadata.GetRecordHash(recordNode);
        uint parentHash = LevelRecordMetadata.GetParentHash(recordNode);
        string entityName = recordNode.HasMeta(LevelRecordMetadata.EntityNameMeta)
            ? recordNode.GetMeta(LevelRecordMetadata.EntityNameMeta).AsString()
            : recordNode.Name.ToString();
        string layerName = LevelRecordMetadata.GetLayerName(recordNode);
        GdbSaveLoadState loadState = LevelRecordMetadata.GetLoadState(recordNode);
        string gdbSource = recordNode.HasMeta(LevelRecordMetadata.SourceDbMeta)
            ? recordNode.GetMeta(LevelRecordMetadata.SourceDbMeta).AsString()
            : string.Empty;
        bool isNewPlacement = recordNode.HasMeta(LevelRecordMetadata.IsNewPlacementMeta) &&
                              recordNode.GetMeta(LevelRecordMetadata.IsNewPlacementMeta).AsBool();

        // Check for duplicate node in the scene:
        if (!isNewPlacement && levelRoot is not null)
        {
            var seenNodes = new List<Node3D>();
            FindNodesWithRecordHash(levelRoot, recordHash, seenNodes);
            if (seenNodes.Count > 1)
            {
                uint newHash = LevelGdbPatchCompiler.GenerateUniqueRecordHash(recordHash, recordNode.Name.ToString());
                recordNode.SetMeta(LevelRecordMetadata.RecordHashMeta, (long)newHash);
                recordNode.SetMeta(LevelRecordMetadata.ParentHashMeta, (long)recordHash);
                recordNode.SetMeta(LevelRecordMetadata.IsNewPlacementMeta, true);
                recordNode.RemoveMeta(LevelRecordMetadata.BaseTransformMeta);

                string model = LevelRecordMetadata.FindModelPath(recordNode);
                if (!string.IsNullOrEmpty(model))
                {
                    recordNode.SetMeta(LevelRecordMetadata.ModelPathMeta, model);
                }

                string rawName = StripHashSuffix(recordNode.Name.ToString());
                recordNode.Name = $"{rawName} [0x{newHash:X8}]";

                recordHash = newHash;
                isNewPlacement = true;
            }
        }

        bool hasBaseTransform = LevelRecordMetadata.TryGetBaseTransform(recordNode, out Transform3D baseTransform);
        Transform3D currentRegion = LevelRecordMetadata.ComputeRegionTransform(recordNode, levelRoot);
        bool isMoved = !isNewPlacement && hasBaseTransform && !GdbTransformMath.AreTransformsEquivalent(currentRegion, baseTransform);

        string headerSuffix = isNewPlacement ? " [NEW PLACEMENT]" : string.Empty;
        AddChild(CreateHeader($"🎯 GDB Level Record{headerSuffix}"));

        // Status Banner:
        var statusBox = new HBoxContainer();
        statusBox.AddThemeConstantOverride("separation", 6);
        var statusIcon = new Label();
        statusIcon.AddThemeFontSizeOverride("font_size", 12);
        var statusText = new Label();
        statusText.AddThemeFontSizeOverride("font_size", 12);

        bool isCulled = LevelRecordMetadata.IsAbyssCulled(recordNode);

        if (isCulled)
        {
            statusIcon.Text = "🗑️";
            statusText.Text = "Status: Deleted (Abyss-Culled)";
            statusText.Modulate = new Color(1.0f, 0.45f, 0.45f);
        }
        else if (isNewPlacement)
        {
            statusIcon.Text = "🟢";
            statusText.Text = "Status: New Placement (Added)";
            statusText.Modulate = new Color(0.45f, 0.95f, 0.55f);
        }
        else if (isMoved)
        {
            statusIcon.Text = "🟡";
            statusText.Text = "Status: Moved from Vanilla Transform";
            statusText.Modulate = new Color(1.0f, 0.85f, 0.35f);
        }
        else
        {
            statusIcon.Text = "⚪";
            statusText.Text = "Status: Unmodified (Vanilla)";
            statusText.Modulate = new Color(0.70f, 0.76f, 0.85f);
        }
        statusBox.AddChild(statusIcon);
        statusBox.AddChild(statusText);
        AddChild(statusBox);

        if (!ReferenceEquals(recordNode, selectedNode))
        {
            var subMeshHint = new Label
            {
                Text = $"Selected child mesh of '{recordNode.Name}'",
                Modulate = new Color(0.65f, 0.78f, 0.95f),
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            subMeshHint.AddThemeFontSizeOverride("font_size", 11);
            AddChild(subMeshHint);

            var selectRootBtn = new Button
            {
                Text = "⬆️ Select Owning Record Node",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            selectRootBtn.Pressed += () =>
            {
                if (!GodotObject.IsInstanceValid(recordNode)) return;
                var sel = EditorInterface.Singleton.GetSelection();
                sel.Clear();
                sel.AddNode(recordNode);
                EditorInterface.Singleton.InspectObject(recordNode);
            };
            AddChild(selectRootBtn);
        }

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 4);

        AddKeyValueRow(grid, "Entity Name:", entityName);
        AddKeyValueRow(grid, "Instance Hash:", $"0x{recordHash:X8}");
        if (parentHash != 0)
        {
            string parentName = Engine_GameGdb.Database?.NameOf(parentHash) ?? string.Empty;
            string parentDisplay = !string.IsNullOrEmpty(parentName)
                ? $"{parentName} (0x{parentHash:X8})"
                : $"0x{parentHash:X8}";
            AddKeyValueRow(grid, "Archetype / Parent:", parentDisplay);
        }
        if (!string.IsNullOrEmpty(gdbSource))
        {
            AddKeyValueRow(grid, "Source GDB:", gdbSource);
        }
        string resolvedModel = LevelRecordMetadata.FindModelPath(recordNode);
        if (!string.IsNullOrEmpty(resolvedModel))
        {
            AddKeyValueRow(grid, "Model (.mdl):", resolvedModel);
        }
        AddChild(grid);

        var btnRow = new VBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 4);

        string? levelGdbPath = levelRoot is not null && levelRoot.HasMeta(LevelRecordMetadata.LevelGdbPathMeta)
            ? levelRoot.GetMeta(LevelRecordMetadata.LevelGdbPathMeta).AsString()
            : null;

        var openInstanceBtn = new Button
        {
            Text = $"🔍 Open Instance (0x{recordHash:X8}) in Mod Studio",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        openInstanceBtn.Pressed += () => EditorModValueService.OpenRecordInModStudio(recordHash, levelGdbPath);
        btnRow.AddChild(openInstanceBtn);

        if (parentHash != 0)
        {
            var openParentBtn = new Button
            {
                Text = $"🧬 Open Parent Archetype (0x{parentHash:X8}) in Mod Studio",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            openParentBtn.Pressed += () => EditorModValueService.OpenRecordInModStudio(parentHash, levelGdbPath);
            btnRow.AddChild(openParentBtn);
        }

        if (isMoved || isCulled)
        {
            var resetBtn = new Button
            {
                Text = isCulled ? "↺ Restore Deleted Object" : "↺ Reset Transform to Vanilla",
                TooltipText = isCulled
                    ? "Restore this deleted object back to its vanilla coordinates and purge any .tu override records"
                    : "Restore this object's region position and rotation to its baseline vanilla coordinates and purge any .tu override records",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            resetBtn.Pressed += () =>
            {
                if (!GodotObject.IsInstanceValid(recordNode)) return;
                LevelRecordMetadata.ApplyRegionTransform(recordNode, baseTransform, levelRoot);
                recordNode.Visible = true;
                if (levelRoot is not null)
                {
                    EditorModValueService.RemoveRecordTuOverride(levelRoot, recordHash);
                }
                GD.Print($"[level_edit] Restored transform of '{entityName}' [0x{recordHash:X8}] to vanilla.");
                EditorInterface.Singleton.InspectObject(recordNode);
            };
            btnRow.AddChild(resetBtn);
        }

        AddChild(btnRow);

        AddChild(new HSeparator());

        var saveTitle = new Label
        {
            Text = "Level .save Layer & Load State",
            Modulate = new Color(0.85f, 0.90f, 1.0f),
        };
        saveTitle.AddThemeFontSizeOverride("font_size", 12);
        AddChild(saveTitle);

        List<string> knownLayers = CollectKnownLayerNames(levelRoot, layerName);

        var layerRow = new HBoxContainer();
        layerRow.AddThemeConstantOverride("separation", 6);
        layerRow.AddChild(new Label
        {
            Text = "Layer:",
            CustomMinimumSize = new Vector2(72, 0),
            Modulate = new Color(0.75f, 0.80f, 0.88f),
        });

        var layerDropdown = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        for (int i = 0; i < knownLayers.Count; i++)
        {
            layerDropdown.AddItem(knownLayers[i], i);
            if (string.Equals(knownLayers[i], layerName, StringComparison.OrdinalIgnoreCase))
            {
                layerDropdown.Selected = i;
            }
        }
        layerRow.AddChild(layerDropdown);
        AddChild(layerRow);

        var customLayerRow = new HBoxContainer();
        customLayerRow.AddThemeConstantOverride("separation", 6);
        var customLayerInput = new LineEdit
        {
            PlaceholderText = "Or type new layer name...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        customLayerRow.AddChild(customLayerInput);
        var moveLayerBtn = new Button
        {
            Text = "Move",
        };
        customLayerRow.AddChild(moveLayerBtn);
        AddChild(customLayerRow);

        var stateRow = new HBoxContainer();
        stateRow.AddThemeConstantOverride("separation", 6);
        stateRow.AddChild(new Label
        {
            Text = "Load State:",
            CustomMinimumSize = new Vector2(72, 0),
            Modulate = new Color(0.75f, 0.80f, 0.88f),
        });

        var stateDropdown = new OptionButton
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        for (int i = 0; i < LoadStateOptions.Length; i++)
        {
            GdbSaveLoadState st = LoadStateOptions[i];
            stateDropdown.AddItem(FormatLoadStateLabel(st), i);
            if (st == loadState)
            {
                stateDropdown.Selected = i;
            }
        }
        stateRow.AddChild(stateDropdown);
        AddChild(stateRow);

        layerDropdown.ItemSelected += idx =>
        {
            if (idx < 0 || idx >= knownLayers.Count) return;
            string chosenLayer = knownLayers[(int)idx];
            MoveRecordNodeToLayer(recordNode, levelRoot, chosenLayer);
        };

        moveLayerBtn.Pressed += () =>
        {
            string typed = customLayerInput.Text.Trim();
            if (string.IsNullOrEmpty(typed)) return;
            MoveRecordNodeToLayer(recordNode, levelRoot, typed);
            customLayerInput.Text = string.Empty;
        };

        stateDropdown.ItemSelected += idx =>
        {
            if (idx < 0 || idx >= LoadStateOptions.Length) return;
            GdbSaveLoadState newState = LoadStateOptions[(int)idx];
            recordNode.SetMeta(LevelRecordMetadata.LayerLoadStateMeta, newState.ToXmlAttribute());
            Engine_LevelProps.ApplyEditorLoadStateVisuals(
                recordNode,
                newState,
                Engine_LevelProps.EditorGhostInitiallyOff);
        };
    }

    private void BuildStaticModelArchetypeSection(Node node)
    {
        string modelPath = node.GetMeta(LevelRecordMetadata.ModelPathMeta).AsString();
        AddChild(CreateHeader("🏛️ Static Environment Mesh (.engine_level)"));

        var info = new Label
        {
            Text = $"Model: {modelPath}\nPre-baked static level geometry (no per-instance GDB record). Matching GDB archetypes referencing this model:",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Modulate = new Color(0.75f, 0.80f, 0.88f),
        };
        info.AddThemeFontSizeOverride("font_size", 11);
        AddChild(info);

        List<(uint RecordHash, string DisplayName)> matches =
            EditorModValueService.ReverseLookupArchetypesByModelPath(modelPath);

        if (matches.Count == 0)
        {
            var noneLbl = new Label
            {
                Text = "No GDB archetype records reference this .mdl directly.",
                Modulate = new Color(0.65f, 0.65f, 0.65f),
            };
            noneLbl.AddThemeFontSizeOverride("font_size", 11);
            AddChild(noneLbl);

            var placeRawBtn = new Button
            {
                Text = "➕ Place New 1:1 Editable Copy in Scene",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            placeRawBtn.Pressed += () =>
            {
                Vector3? pos = (node as Node3D)?.GlobalPosition;
                EditorModValueService.PlaceRecordInActive3DScene(
                    0,
                    Engine_LevelProps.NameOf(modelPath),
                    targetLayerName: null,
                    worldPosition: pos,
                    explicitModelPath: modelPath,
                    out _);
            };
            AddChild(placeRawBtn);
            return;
        }

        int shown = Math.Min(matches.Count, 8);
        for (int i = 0; i < shown; i++)
        {
            var (archHash, archName) = matches[i];
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);

            var openBtn = new Button
            {
                Text = $"🔍 {archName} [0x{archHash:X8}]",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                Alignment = HorizontalAlignment.Left,
                TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            };
            uint capturedHash = archHash;
            string capturedName = archName;
            openBtn.Pressed += () => EditorModValueService.OpenRecordInModStudio(capturedHash);
            row.AddChild(openBtn);

            var placeBtn = new Button
            {
                Text = "➕ Place",
                TooltipText = $"Spawn a new 1:1 editable GDB placement of '{archName}'",
            };
            placeBtn.Pressed += () =>
            {
                Vector3? pos = (node as Node3D)?.GlobalPosition;
                EditorModValueService.PlaceRecordInActive3DScene(
                    capturedHash,
                    capturedName,
                    targetLayerName: null,
                    worldPosition: pos,
                    explicitModelPath: modelPath,
                    out _);
            };
            row.AddChild(placeBtn);
            AddChild(row);
        }
    }

    public static void ApplyLayerContainerLoadState(
        Node3D layerContainer,
        string layerName,
        GdbSaveLoadState newState)
    {
        if (!GodotObject.IsInstanceValid(layerContainer))
        {
            return;
        }

        Node3D? levelRoot = LevelRecordMetadata.FindLevelRootNode(layerContainer);
        if (levelRoot is not null)
        {
            foreach (Node3D container in FindLayerContainersByName(levelRoot, layerName))
            {
                UpdateSingleLayerContainerLoadState(container, newState);
            }
        }
        else
        {
            UpdateSingleLayerContainerLoadState(layerContainer, newState);
        }
    }

    private static void UpdateSingleLayerContainerLoadState(Node3D container, GdbSaveLoadState newState)
    {
        string loadAttr = newState.ToXmlAttribute();
        container.SetMeta(LevelRecordMetadata.LayerLoadStateMeta, loadAttr);

        foreach (Node child in container.GetChildren())
        {
            if (child is Node3D child3D && LevelRecordMetadata.IsRecordNode(child3D))
            {
                child3D.SetMeta(LevelRecordMetadata.LayerLoadStateMeta, loadAttr);
                Engine_LevelProps.ApplyEditorLoadStateVisuals(
                    child3D,
                    newState,
                    Engine_LevelProps.EditorGhostInitiallyOff);
            }
        }
    }

    private static void MoveRecordNodeToLayer(Node3D recordNode, Node3D? levelRoot, string targetLayerName)
    {
        if (!GodotObject.IsInstanceValid(recordNode) || string.IsNullOrWhiteSpace(targetLayerName))
        {
            return;
        }

        string cleanLayer = targetLayerName.Trim();
        string layerTag = cleanLayer.Equals("DefaultLayer", StringComparison.OrdinalIgnoreCase)
            ? "id_5A4CBED4"
            : $"id_{Engine_GameHash.Fnv1(cleanLayer):X8}";

        recordNode.SetMeta(LevelRecordMetadata.LayerNameMeta, cleanLayer);
        recordNode.SetMeta(LevelRecordMetadata.LayerTagMeta, layerTag);

        if (levelRoot is null)
        {
            return;
        }

        Node? currentParent = recordNode.GetParent();
        Node? layersRoot = currentParent?.GetParent();
        if (layersRoot is Node3D layersRoot3D &&
            layersRoot3D.Name.ToString().Equals("Layers", StringComparison.OrdinalIgnoreCase))
        {
            Transform3D preservedGlobal = recordNode.IsInsideTree() ? recordNode.GlobalTransform : recordNode.Transform;
            string safeNodeName = cleanLayer.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
            Node3D? targetContainer = layersRoot3D.GetNodeOrNull<Node3D>(safeNodeName);
            GdbSaveLoadState targetState = GdbSaveLoadState.AlwaysOn;

            if (targetContainer is null)
            {
                targetContainer = new Node3D { Name = safeNodeName };
                LevelRecordMetadata.StampLayerContainer(targetContainer, cleanLayer, layerTag, targetState);
                layersRoot3D.AddChild(targetContainer);
                targetContainer.Owner = recordNode.Owner ?? levelRoot;
            }
            else if (targetContainer.HasMeta(LevelRecordMetadata.LayerLoadStateMeta))
            {
                targetState = GdbSaveLoadStateExtensions.ParseLoadState(
                    targetContainer.GetMeta(LevelRecordMetadata.LayerLoadStateMeta).AsString());
            }

            if (!ReferenceEquals(currentParent, targetContainer))
            {
                Node? owner = recordNode.Owner;
                currentParent?.RemoveChild(recordNode);
                targetContainer.AddChild(recordNode);
                SetOwnerRecursive(recordNode, owner);
                if (recordNode.IsInsideTree())
                {
                    recordNode.GlobalTransform = preservedGlobal;
                }
            }

            recordNode.SetMeta(LevelRecordMetadata.LayerLoadStateMeta, targetState.ToXmlAttribute());
            Engine_LevelProps.ApplyEditorLoadStateVisuals(
                recordNode,
                targetState,
                Engine_LevelProps.EditorGhostInitiallyOff);
        }
    }

    private static void SetOwnerRecursive(Node node, Node? owner)
    {
        if (owner is null) return;
        node.Owner = owner;
        foreach (Node child in node.GetChildren())
        {
            SetOwnerRecursive(child, owner);
        }
    }

    public static List<string> CollectKnownLayerNames(Node3D? levelRoot, string? ensureIncluded = null)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DefaultLayer" };
        if (!string.IsNullOrWhiteSpace(ensureIncluded))
        {
            set.Add(ensureIncluded!);
        }

        if (levelRoot is not null)
        {
            foreach (Node3D container in EnumerateAllLayerContainers(levelRoot))
            {
                string name = container.HasMeta(LevelRecordMetadata.LayerNameMeta)
                    ? container.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString()
                    : container.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    set.Add(name);
                }
            }
        }

        var list = new List<string>(set);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static IEnumerable<Node3D> EnumerateAllLayerContainers(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node current = stack.Pop();
            if (current is Node3D n3d &&
                n3d.HasMeta(LevelRecordMetadata.IsLayerContainerMeta) &&
                n3d.GetMeta(LevelRecordMetadata.IsLayerContainerMeta).AsBool())
            {
                yield return n3d;
            }

            int count = current.GetChildCount();
            for (int i = count - 1; i >= 0; i--)
            {
                stack.Push(current.GetChild(i));
            }
        }
    }

    private static IEnumerable<Node3D> FindLayerContainersByName(Node root, string layerName)
    {
        foreach (Node3D container in EnumerateAllLayerContainers(root))
        {
            string name = container.HasMeta(LevelRecordMetadata.LayerNameMeta)
                ? container.GetMeta(LevelRecordMetadata.LayerNameMeta).AsString()
                : container.Name.ToString();
            if (string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase))
            {
                yield return container;
            }
        }
    }

    private static string FormatLoadStateLabel(GdbSaveLoadState state) => state switch
    {
        GdbSaveLoadState.AlwaysOn => "AlwaysOn (Permanent)",
        GdbSaveLoadState.InitiallyOn => "InitiallyOn (Default Active, Script-Toggleable)",
        GdbSaveLoadState.InitiallyOff => "InitiallyOff (Hidden Until Quest/Script Activates)",
        _ => state.ToString(),
    };

    private static Label CreateHeader(string text)
    {
        var label = new Label
        {
            Text = text,
            Modulate = new Color(0.45f, 0.78f, 1.0f),
        };
        label.AddThemeFontSizeOverride("font_size", 14);
        return label;
    }

    private static void AddKeyValueRow(GridContainer grid, string key, string value)
    {
        var kLbl = new Label
        {
            Text = key,
            Modulate = new Color(1f, 1f, 1f, 0.65f),
        };
        kLbl.AddThemeFontSizeOverride("font_size", 11);
        grid.AddChild(kLbl);

        var vLbl = new Label
        {
            Text = value,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        vLbl.AddThemeFontSizeOverride("font_size", 11);
        grid.AddChild(vLbl);
    }

    private static string StripHashSuffix(string nodeName)
    {
        int bracket = nodeName.LastIndexOf(" [0x", StringComparison.OrdinalIgnoreCase);
        return bracket > 0 ? nodeName[..bracket].Trim() : nodeName.Trim();
    }

    private static void FindNodesWithRecordHash(Node root, uint targetHash, List<Node3D> results)
    {
        if (root is Node3D n3d && LevelRecordMetadata.GetRecordHash(n3d) == targetHash)
        {
            results.Add(n3d);
        }
        int count = root.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            FindNodesWithRecordHash(root.GetChild(i), targetHash, results);
        }
    }
}
#endif
