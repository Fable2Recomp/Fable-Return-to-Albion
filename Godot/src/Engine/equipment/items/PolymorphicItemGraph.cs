#if TOOLS
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using Refii.Core;
using Refii.EditorTools;
using Refii.Gdb;

namespace Refii.Equipment;

/// <summary>
/// Represents a single component slot attached directly to or inherited by an <see cref="ItemGraphNode"/>.
/// </summary>
public sealed class NodeComponentSlot
{
    /// <summary>FNV-1a hash of the component slot field on the node record (e.g. 0x2C34431E for WeaponComponent).</summary>
    public uint SlotHash { get; set; }

    /// <summary>Internal GDB slot name (e.g. "WeaponComponent", "ShopItemComponent", or a custom Lua component name).</summary>
    public string SlotName { get; set; } = string.Empty;

    /// <summary>Human-readable component title (e.g. "Weapon Component").</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Functional grouping category (e.g. "Combat & Weapons", "Core Item", "Appearance & Visuals").</summary>
    public string Category { get; set; } = "Component";

    /// <summary>Short description of what this component controls.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Record hash of the component instance pointed to by this slot.</summary>
    public uint ComponentRecordHash { get; set; }

    /// <summary>Internal GDB record name of the component instance if known.</summary>
    public string ComponentRecordName { get; set; } = string.Empty;

    /// <summary>Parent component record hash if this component record inherits from an ancestor component record.</summary>
    public uint ParentComponentRecordHash { get; set; }

    /// <summary>True when attached directly on the inspected node; false when inherited via ancestor walk-up.</summary>
    public bool IsDirectOnNode { get; set; }

    /// <summary>Record hash of the node that attaches this component (either the current node or an ancestor node).</summary>
    public uint SourceNodeHash { get; set; }

    /// <summary>Display label of the node that attaches this component.</summary>
    public string SourceNodeLabel { get; set; } = string.Empty;

    /// <summary>If <see cref="IsDirectOnNode"/> is true and this slot overrides an ancestor's component, the ancestor's node hash.</summary>
    public uint OverriddenAncestorNodeHash { get; set; }

    /// <summary>Display label of <see cref="OverriddenAncestorNodeHash"/> if overriding an ancestor component.</summary>
    public string OverriddenAncestorNodeLabel { get; set; } = string.Empty;

    /// <summary>True when this direct component slot was newly attached or overridden by the active mod.</summary>
    public bool IsCustomOrModOverriddenSlot { get; set; }

    /// <summary>True when this inherited component slot is stripped on the current node via RemoveComponent (0x9B41D00A).</summary>
    public bool IsStrippedByNode { get; set; }

    /// <summary>All editable scalar and string/tag properties discovered on this component record (including inherited component fields).</summary>
    public List<ModItemProperty> Properties { get; } = new();

    public NodeComponentSlot CloneAsInherited()
    {
        var copy = new NodeComponentSlot
        {
            SlotHash = SlotHash,
            SlotName = SlotName,
            DisplayName = DisplayName,
            Category = Category,
            Summary = Summary,
            ComponentRecordHash = ComponentRecordHash,
            ComponentRecordName = ComponentRecordName,
            ParentComponentRecordHash = ParentComponentRecordHash,
            IsDirectOnNode = false,
            SourceNodeHash = SourceNodeHash,
            SourceNodeLabel = SourceNodeLabel,
            OverriddenAncestorNodeHash = 0,
            OverriddenAncestorNodeLabel = string.Empty,
            IsCustomOrModOverriddenSlot = false,
            IsStrippedByNode = false,
        };

        foreach (var p in Properties)
        {
            copy.Properties.Add(new ModItemProperty
            {
                PropertyName = p.PropertyName,
                DisplayLabel = p.DisplayLabel,
                CategoryId = p.CategoryId,
                CategoryDisplayName = p.CategoryDisplayName,
                PropertyHash = p.PropertyHash,
                TargetRecordHash = p.TargetRecordHash,
                DeclaringHash = p.DeclaringHash != 0 ? p.DeclaringHash : p.TargetRecordHash,
                Type = p.Type,
                VanillaValue = p.CurrentValue,
                CurrentValue = p.CurrentValue,
                IsModified = false,
                IsLocalizedTextTag = p.IsLocalizedTextTag,
                BabelTagKey = p.BabelTagKey,
                IsDefinedOnComponent = false,
                IsWalkedUpFromParent = true,
                ParentComponentValue = p.CurrentValue,
            });
        }

        return copy;
    }
}

/// <summary>
/// Represents a single GDB node in the unified GDB hierarchy tree.
/// Every node in Fable II's GDB is structurally identical: a record with an optional <c>parent</c> pointer
/// and a set of attached component pointers. Any node can have child nodes created from it and components attached or overridden.
/// </summary>
public sealed class ItemGraphNode
{
    /// <summary>Unique identifier for this node.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Raw GDB internal record name if present in the symbol/alias table.</summary>
    public string InternalName { get; set; } = string.Empty;

    /// <summary>Optional 2D game UI texture for items with an <c>IconGraphic</c>.</summary>
    public Texture2D? IconTexture { get; set; }

    /// <summary>Virtual texture path for the item's <c>IconGraphic</c> if present.</summary>
    public string IconTexPath { get; set; } = string.Empty;

    /// <summary>Numerical GDB record hash (top root is <c>0x46F79080</c> World Entity Root).</summary>
    public uint RecordHash { get; set; }

    /// <summary>Parent GDB record hash (0 for the top root node).</summary>
    public uint ParentHash { get; set; }

    /// <summary>Organizational GDB domain category ("Weapons", "Clothing", "Potions", "Augments", "Gifts", "Books", "Furniture & Props", "Creatures & NPCs", "Buildings & Shops", "Loot & Generators", "Visual FX & Audio", "World Markers", or "All").</summary>
    public string Category { get; set; } = "All";

    /// <summary>Descriptive summary or lore text.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Attached item detail if resolved by <see cref="Engine_ItemDetails"/>.</summary>
    public ItemDetail? Detail { get; set; }

    /// <summary>True when this node record does not exist in the base game GDB and was created by the active mod.</summary>
    public bool IsCustomModNode { get; set; }

    /// <summary>True when this node record or one of its direct component records exists in the active mod's .gdb.tu.</summary>
    public bool IsModModifiedNode { get; set; }

    /// <summary>Components explicitly attached on this node's GDB record.</summary>
    public List<NodeComponentSlot> DirectComponents { get; } = new();

    /// <summary>Components inherited by walking up this node's ancestor chain (for slots not overridden on this node).</summary>
    public List<NodeComponentSlot> InheritedComponents { get; } = new();

    /// <summary>Direct scalar fields authored on the node record itself plus all properties across <see cref="DirectComponents"/>.</summary>
    public List<ModItemProperty> Properties { get; } = new();

    /// <summary>True if this node is custom-created or has any modified property or component override in the active mod.</summary>
    public bool HasModOverrides =>
        IsCustomModNode ||
        IsModModifiedNode ||
        Properties.Any(p => p.IsModified) ||
        DirectComponents.Any(c => c.IsCustomOrModOverriddenSlot || c.Properties.Any(p => p.IsModified));

    /// <summary>Parent node in the hierarchy tree.</summary>
    public ItemGraphNode? Parent { get; set; }

    /// <summary>Direct child nodes inheriting from this node.</summary>
    public List<ItemGraphNode> Children { get; } = new();

    /// <summary>
    /// Adds a child node and links parent reference.
    /// </summary>
    public void AddChild(ItemGraphNode child)
    {
        child.Parent = this;
        if (child.ParentHash == 0 && RecordHash != 0)
        {
            child.ParentHash = RecordHash;
        }
        Children.Add(child);
    }

    /// <summary>
    /// Finds an effective component slot (either directly attached or inherited from an ancestor) by slot hash.
    /// </summary>
    public NodeComponentSlot? FindEffectiveComponent(uint slotHash) =>
        DirectComponents.FirstOrDefault(c => c.SlotHash == slotHash) ??
        InheritedComponents.FirstOrDefault(c => c.SlotHash == slotHash && !c.IsStrippedByNode);
}

/// <summary>
/// Headless model managing the unified GDB entity & component hierarchy rooted at <c>0x46F79080</c> (World Entity Root).
/// </summary>
public sealed class PolymorphicItemGraph
{
    public static readonly string[] AllDomains =
    {
        "All",
        "Weapons",
        "Clothing",
        "Potions",
        "Augments",
        "Gifts",
        "Books",
        "Furniture & Props",
        "Creatures & NPCs",
        "Buildings & Shops",
        "Loot & Generators",
        "Visual FX & Audio",
        "World Markers",
    };

    /// <summary>The top root node of the hierarchy (<c>0x46F79080</c>: World Entity Root).</summary>
    public ItemGraphNode Root { get; }

    /// <summary>The current active ancestry path from <see cref="Root"/> to <see cref="SelectedNode"/>.</summary>
    public List<ItemGraphNode> ActivePath { get; } = new();

    /// <summary>The node currently selected for inspection.</summary>
    public ItemGraphNode SelectedNode { get; private set; }

    /// <summary>Active organizational domain filter.</summary>
    public string DomainFilter { get; private set; } = "All";

    /// <summary>Event raised whenever selection or filtering changes.</summary>
    public event Action? GraphNavigated;

    public PolymorphicItemGraph(ItemGraphNode root)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        ActivePath.Add(Root);
        SelectedNode = Root;
    }

    /// <summary>
    /// Sets the organizational domain filter.
    /// </summary>
    public void SetDomainFilter(string domain)
    {
        DomainFilter = !string.IsNullOrWhiteSpace(domain) ? domain : "All";
        if (!MatchesDomain(SelectedNode, DomainFilter))
        {
            SelectedNode = Root;
            RebuildActivePath(Root);
        }
        GraphNavigated?.Invoke();
    }

    /// <summary>
    /// Returns the children of a node filtered by the active domain filter.
    /// </summary>
    public List<ItemGraphNode> GetVisibleChildren(ItemGraphNode node)
    {
        if (node is null) return new List<ItemGraphNode>();
        if (string.Equals(DomainFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            return node.Children;
        }

        return node.Children.Where(c => MatchesDomain(c, DomainFilter)).ToList();
    }

    public static bool MatchesDomain(ItemGraphNode node, string domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(node.Category, domain, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((domain.Equals("Other", StringComparison.OrdinalIgnoreCase) ||
             domain.Equals("Furniture", StringComparison.OrdinalIgnoreCase) ||
             domain.Equals("Furniture & Props", StringComparison.OrdinalIgnoreCase)) &&
            (node.Category.Equals("Other", StringComparison.OrdinalIgnoreCase) ||
             node.Category.Equals("Furniture", StringComparison.OrdinalIgnoreCase) ||
             node.Category.Equals("Furniture & Props", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (MatchesDomain(child, domain)) return true;
        }

        return false;
    }

    /// <summary>
    /// Selects a node in the hierarchy and updates <see cref="ActivePath"/>.
    /// </summary>
    public void SelectNode(ItemGraphNode node)
    {
        if (node is null) return;
        SelectedNode = node;
        RebuildActivePath(node);
        GraphNavigated?.Invoke();
    }

    /// <summary>
    /// Steps back to an ancestor node in the active path.
    /// </summary>
    public void StepToAncestor(ItemGraphNode ancestor)
    {
        if (ancestor is null) return;
        SelectNode(ancestor);
    }

    /// <summary>
    /// Resets selection back to <see cref="Root"/>.
    /// </summary>
    public void ResetToRoot()
    {
        SelectNode(Root);
    }

    private void RebuildActivePath(ItemGraphNode target)
    {
        var lineage = new List<ItemGraphNode>();
        ItemGraphNode? curr = target;
        var guard = new HashSet<ItemGraphNode>();
        while (curr is not null && guard.Add(curr))
        {
            lineage.Insert(0, curr);
            curr = curr.Parent;
        }

        ActivePath.Clear();
        ActivePath.AddRange(lineage);
    }

    /// <summary>
    /// Finds a node anywhere in the hierarchy by its GDB record hash.
    /// </summary>
    public ItemGraphNode? FindNodeByHash(uint recordHash)
    {
        if (recordHash == 0) return null;
        return FindNodeRecursive(Root, recordHash);
    }

    private static ItemGraphNode? FindNodeRecursive(ItemGraphNode current, uint hash)
    {
        if (current.RecordHash == hash) return current;
        foreach (var child in current.Children)
        {
            var found = FindNodeRecursive(child, hash);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Constructs a populated GDB hierarchy graph dynamically from mounted GDB databases,
    /// or falls back to a canonical hierarchy rooted at <c>0x46F79080</c> if databases are offline.
    /// </summary>
    public static PolymorphicItemGraph Build(GdbMulti? multi = null)
    {
        if (multi is not null)
        {
            try
            {
                var dynamicRoot = BuildDynamicGraph(multi);
                if (dynamicRoot.Children.Count > 0)
                {
                    return new PolymorphicItemGraph(dynamicRoot);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[PolymorphicItemGraph] Dynamic GDB graph build failed: {ex.Message}. Falling back to canonical skeleton.");
            }
        }

        return new PolymorphicItemGraph(BuildFallbackGraph());
    }

    /// <summary>
    /// Dynamically builds the complete GDB entity hierarchy from mounted GDB databases starting at
    /// <c>0x46F79080</c> (World Entity Root) and resolves all direct and inherited components.
    /// Synchronously overlays the active mod's <c>.gdb.tu</c> and <c>book.babel.csv</c> so custom child nodes
    /// and property edits are reflected immediately.
    /// </summary>
    private static ItemGraphNode BuildDynamicGraph(GdbMulti inputMulti)
    {
        GdbMulti baseMulti = (ReferenceEquals(inputMulti, Engine_GameGdb.Database) && Engine_GameGdb.BaseDatabase is not null)
            ? Engine_GameGdb.BaseDatabase
            : inputMulti;

        string activeMod = EditorModExporter.ActiveModName;
        GdbMulti multi = EditorModValueService.BuildActiveModGdbMulti(
            activeMod,
            out var activeBabelOverrides,
            out _,
            out var activeTuRecords,
            out var activeTuEntryMap,
            baseMulti) ?? baseMulti;

        var details = Engine_ItemDetails.Build(multi);
        if (details.Count == 0 && !multi.TryLookup(Engine_RecordNameCatalog.WorldEntityRootHash, out _))
        {
            return BuildFallbackGraph();
        }

        var nodeMap = new Dictionary<uint, ItemGraphNode>();

        // Ensure the top root node 0x46F79080 (World Entity Root) exists:
        var topRoot = GetOrCreateNode(Engine_RecordNameCatalog.WorldEntityRootHash, 0, multi, activeBabelOverrides, nodeMap);
        topRoot.Category = "All";

        var seenDefHashes = new HashSet<uint>();

        foreach (ItemDetail d in details)
        {
            uint defHash = d.DefinitionHash != 0 ? d.DefinitionHash : d.RecordHash;
            if (defHash == 0 || !seenDefHashes.Add(defHash)) continue;

            string displayName = Engine_ItemNames.VanillaDisplayNameOf(d);
            if (string.IsNullOrWhiteSpace(displayName) &&
                string.IsNullOrWhiteSpace(d.Label) &&
                d.NameTag == 0 &&
                d.InternalName.Length == 0 &&
                !Engine_RecordNameCatalog.TryGetCuratedNodeName(defHash, out _))
            {
                continue;
            }

            string domain = ClassifyDomain(d);

            // Walk HashParent upward to discover all parent records in this node's ancestry:
            var chain = new List<uint>();
            uint curr = defHash;
            var visited = new HashSet<uint>();

            while (curr != 0 && curr != GdbHash.Null && visited.Add(curr))
            {
                uint parentHash = 0;
                if (multi.TryLookup(curr, out GdbCursor c) &&
                    c.View.TryFindLocalRaw(c.Record, GdbView.HashParent, 6, out uint pH, out _) &&
                    pH != 0 && pH != GdbHash.Null)
                {
                    parentHash = pH;
                }
                else if (d.RecordHash != defHash && curr == defHash &&
                         multi.TryLookup(d.RecordHash, out GdbCursor rc) &&
                         rc.View.TryFindLocalRaw(rc.Record, GdbView.HashParent, 6, out uint rpH, out _) &&
                         rpH != 0 && rpH != GdbHash.Null)
                {
                    parentHash = rpH;
                }

                if (parentHash != 0 && parentHash != GdbHash.Null)
                {
                    chain.Add(parentHash);
                    curr = parentHash;
                }
                else
                {
                    break;
                }
            }

            // Link ancestor nodes from highest ancestor down to defHash:
            ItemGraphNode parentNode = topRoot;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                uint ancHash = chain[i];
                uint ancParentHash = i < chain.Count - 1 ? chain[i + 1] : topRoot.RecordHash;
                ItemGraphNode ancNode = GetOrCreateNode(ancHash, ancParentHash, multi, activeBabelOverrides, nodeMap);

                if (string.IsNullOrEmpty(ancNode.Category) || ancNode.Category == "All")
                {
                    ancNode.Category = domain;
                }

                if (ancNode != topRoot && ancNode.Parent is null && !parentNode.Children.Contains(ancNode))
                {
                    parentNode.AddChild(ancNode);
                }

                parentNode = ancNode;
            }

            // Create or enrich the node for defHash itself:
            uint immediateParentHash = chain.Count > 0 ? chain[0] : topRoot.RecordHash;
            ItemGraphNode itemNode = GetOrCreateNode(defHash, immediateParentHash, multi, activeBabelOverrides, nodeMap);
            ApplyDetailToNode(itemNode, d, domain, multi, activeBabelOverrides);

            if (itemNode != topRoot && itemNode.Parent != parentNode)
            {
                itemNode.Parent?.Children.Remove(itemNode);
                if (!parentNode.Children.Contains(itemNode))
                {
                    parentNode.AddChild(itemNode);
                }
            }
        }

        // Discover all other GDB entity nodes (creatures, buildings, shops, props, loot tables, markers, custom mod nodes, and weapon/item variants)
        // that descend from 0x46F79080 or any node in nodeMap:
        DiscoverAdditionalChildNodes(multi, activeBabelOverrides, nodeMap);

        // Populate direct and inherited component stacks across the entire tree:
        PopulateComponentStacksRecursive(topRoot, multi, baseMulti, activeTuRecords, activeTuEntryMap, activeBabelOverrides);

        // Enrich nodes with inherited ItemDetails (so weapon/item variants get "Open Full Item Editor"),
        // synthesize descriptive names for unnamed child variants, and classify all GDB entity domains:
        EnrichNodesPostPassRecursive(topRoot, multi, activeBabelOverrides);

        // Propagate branch categories upward and sort children:
        PropagateDomainCategories(topRoot);
        SortHierarchyChildren(topRoot);

        return topRoot;
    }

    private static ItemGraphNode GetOrCreateNode(
        uint recordHash,
        uint parentHash,
        GdbMulti multi,
        Dictionary<string, string> activeBabelOverrides,
        Dictionary<uint, ItemGraphNode> nodeMap)
    {
        if (nodeMap.TryGetValue(recordHash, out ItemGraphNode? existing))
        {
            if (existing.ParentHash == 0 && parentHash != 0 && recordHash != Engine_RecordNameCatalog.WorldEntityRootHash)
            {
                existing.ParentHash = parentHash;
            }
            return existing;
        }

        string rawName = multi.NameOf(recordHash);
        string label = ResolveNodeDisplayName(recordHash, rawName, null, multi, activeBabelOverrides);

        var node = new ItemGraphNode
        {
            Id = $"Node_{recordHash:X8}",
            RecordHash = recordHash,
            ParentHash = recordHash == Engine_RecordNameCatalog.WorldEntityRootHash ? 0 : parentHash,
            InternalName = rawName,
            Label = label,
            Category = "All",
            Description = !string.IsNullOrWhiteSpace(rawName)
                ? $"GDB record {rawName} (0x{recordHash:X8})."
                : $"GDB record 0x{recordHash:X8}."
        };

        nodeMap[recordHash] = node;
        return node;
    }

    private static void ApplyDetailToNode(
        ItemGraphNode node,
        ItemDetail d,
        string domain,
        GdbMulti multi,
        Dictionary<string, string> activeBabelOverrides)
    {
        node.Detail = d;
        node.Category = domain;

        if (string.IsNullOrWhiteSpace(node.InternalName) && !string.IsNullOrWhiteSpace(d.InternalName))
        {
            node.InternalName = d.InternalName;
        }

        if (!string.IsNullOrWhiteSpace(d.IconTex))
        {
            node.IconTexPath = d.IconTex;
        }

        node.Label = ResolveNodeDisplayName(node.RecordHash, node.InternalName, d, multi, activeBabelOverrides);

        if (EditorModValueService.TryLookupActiveBabelOverride(activeBabelOverrides, d.DescTagName, d.DescTag, out string babelDesc) &&
            !string.IsNullOrWhiteSpace(babelDesc))
        {
            node.Description = babelDesc;
            return;
        }

        string desc = Engine_ItemNames.VanillaDescriptionOf(d);
        if (!string.IsNullOrWhiteSpace(desc) && !Engine_RecordNameCatalog.IsGenericPlaceholderName(desc))
        {
            node.Description = desc;
        }
        else if (!string.IsNullOrWhiteSpace(d.DescTagName))
        {
            node.Description = d.DescTagName;
        }
    }

    private static string ResolveNodeDisplayName(
        uint recordHash,
        string rawName,
        ItemDetail? detail,
        GdbMulti multi,
        Dictionary<string, string> activeBabelOverrides)
    {
        // 1. Check active mod's Babel overrides first if detail has NameTag:
        if (detail is not null &&
            EditorModValueService.TryLookupActiveBabelOverride(activeBabelOverrides, detail.NameTagName, detail.NameTag, out string modBabelName) &&
            !string.IsNullOrWhiteSpace(modBabelName) &&
            !Engine_RecordNameCatalog.IsGenericPlaceholderName(modBabelName))
        {
            return modBabelName;
        }

        // 2. Check NameTag on the record or its InventoryItemComponent in activeBabelOverrides or vanilla text bank:
        if (recordHash != 0 && multi.TryLookup(recordHash, out GdbCursor cursor) && cursor.Valid)
        {
            uint nameTag = 0;
            if (cursor.View.TryFindLocalRaw(cursor.Record, Engine_ItemDetails.NameTag, -1, out uint directTag, out _) && directTag != 0)
            {
                nameTag = directTag;
            }
            else if (cursor.View.TryFindLocalRaw(cursor.Record, Engine_ItemDetails.InventoryItemComponent, 6, out uint invComp, out _) &&
                     invComp != 0 && multi.TryLookup(invComp, out GdbCursor invCursor) && invCursor.Valid)
            {
                invCursor.View.TryFindLocalRaw(invCursor.Record, Engine_ItemDetails.NameTag, -1, out nameTag, out _);
            }

            if (nameTag != 0)
            {
                string tagKey = multi.NameOf(nameTag);
                if (EditorModValueService.TryLookupActiveBabelOverride(activeBabelOverrides, tagKey, nameTag, out string ovText) &&
                    !string.IsNullOrWhiteSpace(ovText) &&
                    !Engine_RecordNameCatalog.IsGenericPlaceholderName(ovText))
                {
                    return ovText;
                }

                if (Engine_BnkTextBankLoader.TryLookupVanilla(nameTag, out string locText) &&
                    !Engine_RecordNameCatalog.IsGenericPlaceholderName(locText))
                {
                    return locText;
                }
            }
        }

        // 3. Check if detail has a vanilla localized name (which also consults Engine_RecordNameCatalog for base nodes):
        if (detail is not null)
        {
            string displayName = Engine_ItemNames.VanillaDisplayNameOf(detail);
            if (!string.IsNullOrWhiteSpace(displayName) && !Engine_RecordNameCatalog.IsGenericPlaceholderName(displayName))
            {
                return displayName;
            }
        }

        // 4. Check curated node catalog directly by hash:
        if (Engine_RecordNameCatalog.TryGetCuratedNodeName(recordHash, out string curated))
        {
            return curated;
        }

        // 5. Fall back to prettified GDB record name or detail label:
        if (!string.IsNullOrWhiteSpace(rawName))
        {
            string pretty = Engine_ItemLabels.Prettify(rawName);
            if (!Engine_RecordNameCatalog.IsGenericPlaceholderName(pretty))
            {
                return pretty;
            }
            return rawName;
        }

        if (detail is not null && !string.IsNullOrWhiteSpace(detail.Label) && !Engine_RecordNameCatalog.IsGenericPlaceholderName(detail.Label))
        {
            return detail.Label;
        }

        return $"Node 0x{recordHash:X8}";
    }

    private static void DiscoverAdditionalChildNodes(
        GdbMulti multi,
        Dictionary<string, string> activeBabelOverrides,
        Dictionary<uint, ItemGraphNode> nodeMap)
    {
        // Collect all component target record hashes so we don't mistake a component sub-record for an entity node:
        var componentTargetHashes = new HashSet<uint>();
        foreach (GdbView view in multi.Views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                foreach ((uint fieldHash, GdbFieldType type, uint raw) in view.RawFields(i))
                {
                    if (fieldHash != GdbView.HashParent &&
                        type == GdbFieldType.Reference &&
                        raw != 0 && raw != GdbHash.Null)
                    {
                        componentTargetHashes.Add(raw);
                    }
                }
            }
        }

        // Iteratively attach any non-component record whose parent is in nodeMap:
        bool addedAny = true;
        int pass = 0;
        while (addedAny && pass++ < 10)
        {
            addedAny = false;
            foreach (GdbView view in multi.Views)
            {
                for (int i = 0; i < view.RecordCount; i++)
                {
                    uint recHash = view.RecordHash(i);
                    if (recHash == 0 || nodeMap.ContainsKey(recHash) || componentTargetHashes.Contains(recHash))
                    {
                        continue;
                    }

                    if (view.TryFindLocalRaw(i, GdbView.HashParent, 6, out uint parentHash, out _) &&
                        parentHash != 0 && parentHash != GdbHash.Null &&
                        nodeMap.TryGetValue(parentHash, out ItemGraphNode? parentNode))
                    {
                        var childNode = GetOrCreateNode(recHash, parentHash, multi, activeBabelOverrides, nodeMap);
                        childNode.Category = parentNode.Category;
                        parentNode.AddChild(childNode);
                        addedAny = true;
                    }
                }
            }
        }
    }

    private static void PopulateComponentStacksRecursive(
        ItemGraphNode node,
        GdbMulti multi,
        GdbMulti baseMulti,
        HashSet<uint> activeTuRecords,
        IReadOnlyDictionary<uint, Dictionary<uint, uint>> activeTuEntryMap,
        IReadOnlyDictionary<string, string>? activeBabelOverrides)
    {
        node.DirectComponents.Clear();
        node.InheritedComponents.Clear();
        node.Properties.Clear();

        node.IsCustomModNode = node.RecordHash != 0 &&
                               node.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash &&
                               !baseMulti.TryLookup(node.RecordHash, out _);
        node.IsModModifiedNode = false;

        var removedSlotHashes = new HashSet<uint>();
        activeTuEntryMap.TryGetValue(node.RecordHash, out Dictionary<uint, uint>? nodeTuProps);

        if (node.RecordHash != 0 && multi.TryLookup(node.RecordHash, out GdbCursor cursor) && cursor.Valid)
        {
            GdbCursor baseNodeCursor = default;
            bool hasBaseNode = !node.IsCustomModNode && baseMulti.TryLookup(node.RecordHash, out baseNodeCursor) && baseNodeCursor.Valid;

            // 1. Discover direct scalar fields authored on the node record itself:
            var directNodeProps = EditorModValueService.DiscoverDirectNodeScalarProperties(node.RecordHash, multi, activeTuEntryMap);
            node.Properties.AddRange(directNodeProps);
            if (directNodeProps.Any(p => p.IsModified))
            {
                node.IsModModifiedNode = true;
            }

            // 2. Discover directly attached component slots and RemoveComponent directives on this node:
            foreach ((uint fieldHash, GdbFieldType type, uint raw) in cursor.View.RawFields(cursor.Record))
            {
                if (fieldHash == GdbView.HashParent || raw == 0 || raw == GdbHash.Null)
                {
                    continue;
                }

                bool isLocalInBase = hasBaseNode && baseNodeCursor.View.TryFindLocalRaw(baseNodeCursor.Record, fieldHash, -1, out _, out _);
                bool isLocalInTu = nodeTuProps is not null && nodeTuProps.ContainsKey(fieldHash);

                // Skip fields that were only flattened onto this base node by SynthesizeCompleteDatabase:
                if (hasBaseNode && !isLocalInBase && !isLocalInTu)
                {
                    continue;
                }

                // Check if this is RemoveComponent (0x9B41D00A or legacy 0xAD0D6E07):
                if (fieldHash == Engine_RecordNameCatalog.RemoveComponentFieldHash || fieldHash == 0xAD0D6E07)
                {
                    removedSlotHashes.Add(raw);
                    if (isLocalInTu)
                    {
                        node.IsModModifiedNode = true;
                    }
                    continue;
                }

                if (type != GdbFieldType.Reference)
                {
                    continue;
                }

                bool hasCatalogSchema = Engine_RecordNameCatalog.TryGetComponentSchema(fieldHash, out ComponentSlotSchema? schema);
                string slotName = hasCatalogSchema && schema is not null
                    ? schema.SlotName
                    : multi.NameOf(fieldHash);

                // Verify that the target is a valid record in GDB, a custom TU component record, or a known component slot:
                bool targetIsRecord = (multi.TryLookup(raw, out GdbCursor compCursor) && compCursor.Valid) || activeTuRecords.Contains(raw);
                if (!hasCatalogSchema && !targetIsRecord)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(slotName))
                {
                    slotName = $"Component_0x{fieldHash:X8}";
                }

                string displayName = hasCatalogSchema && schema is not null
                    ? schema.DisplayName
                    : EditorModValueService.PrettifyPropertyName(slotName);

                string category = hasCatalogSchema && schema is not null
                    ? schema.Category
                    : "Custom / Mod Component";

                string summary = hasCatalogSchema && schema is not null
                    ? schema.Summary
                    : "Attached GDB component record.";

                uint parentCompHash = 0;
                if (compCursor.Valid &&
                    compCursor.View.TryFindLocalRaw(compCursor.Record, GdbView.HashParent, 6, out uint pComp, out _) &&
                    pComp != 0 && pComp != GdbHash.Null)
                {
                    parentCompHash = pComp;
                }

                bool isCustomOrModSlot = node.IsCustomModNode || isLocalInTu || !baseMulti.TryLookup(raw, out _);

                var slot = new NodeComponentSlot
                {
                    SlotHash = fieldHash,
                    SlotName = slotName,
                    DisplayName = displayName,
                    Category = category,
                    Summary = summary,
                    ComponentRecordHash = raw,
                    ComponentRecordName = multi.NameOf(raw),
                    ParentComponentRecordHash = parentCompHash,
                    IsDirectOnNode = true,
                    SourceNodeHash = node.RecordHash,
                    SourceNodeLabel = node.Label,
                    IsCustomOrModOverriddenSlot = isCustomOrModSlot,
                };

                // Check if an ancestor node had this same component slot:
                if (node.Parent is not null)
                {
                    NodeComponentSlot? ancestorSlot = node.Parent.FindEffectiveComponent(fieldHash);
                    if (ancestorSlot is not null)
                    {
                        slot.OverriddenAncestorNodeHash = ancestorSlot.SourceNodeHash;
                        slot.OverriddenAncestorNodeLabel = ancestorSlot.SourceNodeLabel;
                    }
                }

                var compProps = EditorModValueService.DiscoverComponentProperties(
                    raw, slotName, displayName, multi, activeBabelOverrides, activeTuEntryMap);
                slot.Properties.AddRange(compProps);
                node.DirectComponents.Add(slot);

                if (isCustomOrModSlot || compProps.Any(p => p.IsModified))
                {
                    node.IsModModifiedNode = true;
                }

                foreach (var p in compProps)
                {
                    if (!node.Properties.Any(existing => existing.PropertyName.Equals(p.PropertyName, StringComparison.OrdinalIgnoreCase)))
                    {
                        node.Properties.Add(p);
                    }
                }

                // Resolve IconTexPath from InventoryItemComponent if not yet set on the node:
                if (string.IsNullOrEmpty(node.IconTexPath) && fieldHash == Engine_ItemDetails.InventoryItemComponent && compCursor.Valid)
                {
                    if (multi.TryFindInherited(compCursor, Engine_ItemDetails.IconGraphic, -1, out _, out uint iconHash, out _) && iconHash != 0)
                    {
                        string iconName = multi.NameOf(iconHash);
                        if (!string.IsNullOrWhiteSpace(iconName))
                        {
                            node.IconTexPath = iconName;
                        }
                    }
                }
            }
        }

        node.DirectComponents.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

        // 3. Populate InheritedComponents by walking from Parent's DirectComponents + InheritedComponents:
        if (node.Parent is not null)
        {
            var directSlotHashes = new HashSet<uint>(node.DirectComponents.Select(c => c.SlotHash));

            foreach (var parentDirect in node.Parent.DirectComponents)
            {
                if (!directSlotHashes.Contains(parentDirect.SlotHash))
                {
                    var cloned = parentDirect.CloneAsInherited();
                    cloned.IsStrippedByNode = removedSlotHashes.Contains(parentDirect.SlotHash);
                    node.InheritedComponents.Add(cloned);
                }
            }

            foreach (var parentInherited in node.Parent.InheritedComponents)
            {
                if (!directSlotHashes.Contains(parentInherited.SlotHash) &&
                    !node.InheritedComponents.Any(c => c.SlotHash == parentInherited.SlotHash))
                {
                    var cloned = parentInherited.CloneAsInherited();
                    cloned.IsStrippedByNode = parentInherited.IsStrippedByNode || removedSlotHashes.Contains(parentInherited.SlotHash);
                    node.InheritedComponents.Add(cloned);
                }
            }

            node.InheritedComponents.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(node.IconTexPath) && !string.IsNullOrEmpty(node.Parent.IconTexPath))
            {
                node.IconTexPath = node.Parent.IconTexPath;
            }
        }

        // Recurse down to children:
        foreach (var child in node.Children)
        {
            PopulateComponentStacksRecursive(child, multi, baseMulti, activeTuRecords, activeTuEntryMap, activeBabelOverrides);
        }
    }

    /// <summary>
    /// Enriches all nodes in the tree after components have been resolved:
    /// 1. Populates <see cref="ItemGraphNode.Detail"/> via <see cref="Engine_ItemDetails.BuildOne"/> for any node that has or inherits
    ///    <c>InventoryItemComponent</c> or <c>WeaponComponent</c> (enabling "Open Full Item Editor" on all weapon/item variants).
    /// 2. Resolves localized names from component properties or synthesizes descriptive names for unnamed child variants.
    /// 3. Classifies every node into its GDB entity domain.
    /// </summary>
    private static void EnrichNodesPostPassRecursive(
        ItemGraphNode node,
        GdbMulti multi,
        Dictionary<string, string> activeBabelOverrides)
    {
        // 1. Ensure Detail is populated for any node with direct or inherited InventoryItemComponent or WeaponComponent:
        if (node.Detail is null && node.RecordHash != 0)
        {
            bool hasInvOrWeapon =
                node.FindEffectiveComponent(Engine_ItemDetails.InventoryItemComponent) is not null ||
                node.FindEffectiveComponent(Engine_ItemDetails.WeaponComponent) is not null;

            if (hasInvOrWeapon)
            {
                ItemDetail? builtDetail = Engine_ItemDetails.BuildOne(multi, node.RecordHash);
                if (builtDetail is not null)
                {
                    node.Detail = builtDetail;
                    if (string.IsNullOrWhiteSpace(node.IconTexPath) && !string.IsNullOrWhiteSpace(builtDetail.IconTex))
                    {
                        node.IconTexPath = builtDetail.IconTex;
                    }
                }
            }
        }

        // 2. If this node has a modified or direct localized NameTag/BuildingName/TitleTag property on one of its DirectComponents,
        //    or if its Label is still "Node 0x...", synthesize a descriptive human-readable name:
        bool isUnnamedFallback = node.Label.StartsWith("Node 0x", StringComparison.OrdinalIgnoreCase) ||
                                 Engine_RecordNameCatalog.IsGenericPlaceholderName(node.Label);

        bool resolvedDirectName = false;

        // Check direct component localized name tags first:
        foreach (NodeComponentSlot comp in node.DirectComponents)
        {
            foreach (ModItemProperty prop in comp.Properties)
            {
                if (!prop.IsLocalizedTextTag || string.IsNullOrWhiteSpace(prop.CurrentValue) ||
                    Engine_RecordNameCatalog.IsGenericPlaceholderName(prop.CurrentValue))
                {
                    continue;
                }

                if (prop.PropertyName.Equals("NameTag", StringComparison.OrdinalIgnoreCase) ||
                    prop.PropertyName.Equals("BuildingName", StringComparison.OrdinalIgnoreCase) ||
                    prop.PropertyName.Equals("TitleTag", StringComparison.OrdinalIgnoreCase) ||
                    prop.PropertyName.Equals("NameTagNeutral", StringComparison.OrdinalIgnoreCase) ||
                    prop.PropertyName.Equals("NameTagMale", StringComparison.OrdinalIgnoreCase))
                {
                    // Apply if modified by active mod, custom component, or node is currently unnamed:
                    if (prop.IsModified || comp.IsCustomOrModOverriddenSlot || isUnnamedFallback)
                    {
                        node.Label = prop.CurrentValue;
                        isUnnamedFallback = false;
                        resolvedDirectName = true;
                        break;
                    }
                }
            }

            foreach (ModItemProperty prop in comp.Properties)
            {
                if (prop.IsLocalizedTextTag &&
                    (prop.PropertyName.Equals("DescriptionTag", StringComparison.OrdinalIgnoreCase) ||
                     prop.PropertyName.Equals("TextTag", StringComparison.OrdinalIgnoreCase) ||
                     prop.PropertyName.Equals("BuildingAnecdotes", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(prop.CurrentValue) &&
                    !Engine_RecordNameCatalog.IsGenericPlaceholderName(prop.CurrentValue))
                {
                    if (string.IsNullOrWhiteSpace(node.Description) ||
                        node.Description.StartsWith("GDB record", StringComparison.OrdinalIgnoreCase))
                    {
                        node.Description = prop.CurrentValue;
                    }
                }
            }
        }

        // Check inherited components for localized NameTag and DescriptionTag when not defined directly on this node:
        bool isCustomChildWithoutDirectInv = node.IsCustomModNode &&
            !resolvedDirectName &&
            !node.DirectComponents.Any(c => c.SlotHash == Engine_ItemDetails.InventoryItemComponent);

        foreach (NodeComponentSlot inhComp in node.InheritedComponents)
        {
            if (inhComp.IsStrippedByNode)
            {
                continue;
            }

            if (isCustomChildWithoutDirectInv)
            {
                foreach (ModItemProperty prop in inhComp.Properties)
                {
                    if (!prop.IsLocalizedTextTag || string.IsNullOrWhiteSpace(prop.CurrentValue) ||
                        Engine_RecordNameCatalog.IsGenericPlaceholderName(prop.CurrentValue))
                    {
                        continue;
                    }

                    if (prop.PropertyName.Equals("NameTag", StringComparison.OrdinalIgnoreCase) ||
                        prop.PropertyName.Equals("BuildingName", StringComparison.OrdinalIgnoreCase) ||
                        prop.PropertyName.Equals("TitleTag", StringComparison.OrdinalIgnoreCase) ||
                        prop.PropertyName.Equals("NameTagNeutral", StringComparison.OrdinalIgnoreCase) ||
                        prop.PropertyName.Equals("NameTagMale", StringComparison.OrdinalIgnoreCase))
                    {
                        bool isRawTagKey = prop.CurrentValue.StartsWith("INV_ITEM_", StringComparison.OrdinalIgnoreCase) ||
                                           prop.CurrentValue.StartsWith("TEXT_", StringComparison.OrdinalIgnoreCase);
                        if (isRawTagKey &&
                            node.Parent is not null &&
                            node.Parent.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash &&
                            !node.Parent.Label.StartsWith("Node 0x", StringComparison.OrdinalIgnoreCase))
                        {
                            node.Label = node.Parent.Label;
                        }
                        else
                        {
                            node.Label = prop.CurrentValue;
                        }

                        isUnnamedFallback = false;
                        isCustomChildWithoutDirectInv = false;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(node.Description) ||
                node.Description.StartsWith("GDB record", StringComparison.OrdinalIgnoreCase))
            {
                foreach (ModItemProperty prop in inhComp.Properties)
                {
                    if (prop.IsLocalizedTextTag &&
                        (prop.PropertyName.Equals("DescriptionTag", StringComparison.OrdinalIgnoreCase) ||
                         prop.PropertyName.Equals("TextTag", StringComparison.OrdinalIgnoreCase) ||
                         prop.PropertyName.Equals("BuildingAnecdotes", StringComparison.OrdinalIgnoreCase)) &&
                        !string.IsNullOrWhiteSpace(prop.CurrentValue) &&
                        !Engine_RecordNameCatalog.IsGenericPlaceholderName(prop.CurrentValue))
                    {
                        node.Description = prop.CurrentValue;
                        break;
                    }
                }
            }
        }

        if (isCustomChildWithoutDirectInv &&
            node.Parent is not null &&
            node.Parent.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash &&
            !node.Parent.Label.StartsWith("Node 0x", StringComparison.OrdinalIgnoreCase) &&
            !Engine_RecordNameCatalog.IsGenericPlaceholderName(node.Parent.Label))
        {
            node.Label = node.Parent.Label;
            isUnnamedFallback = false;
        }

        if ((string.IsNullOrWhiteSpace(node.Description) ||
             node.Description.StartsWith("GDB record", StringComparison.OrdinalIgnoreCase)) &&
            node.Parent is not null &&
            node.Parent.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash &&
            !string.IsNullOrWhiteSpace(node.Parent.Description) &&
            !node.Parent.Description.StartsWith("GDB record", StringComparison.OrdinalIgnoreCase))
        {
            node.Description = node.Parent.Description;
        }

        if (isUnnamedFallback)
        {
            node.Label = SynthesizeVariantNodeName(node);
        }

        // Update SourceNodeLabel on direct components so children inheriting from this node see the enriched name:
        foreach (var directComp in node.DirectComponents)
        {
            directComp.SourceNodeLabel = node.Label;
        }
        foreach (var child in node.Children)
        {
            foreach (var inh in child.InheritedComponents)
            {
                if (inh.SourceNodeHash == node.RecordHash)
                {
                    inh.SourceNodeLabel = node.Label;
                }
            }
            foreach (var dir in child.DirectComponents)
            {
                if (dir.OverriddenAncestorNodeHash == node.RecordHash)
                {
                    dir.OverriddenAncestorNodeLabel = node.Label;
                }
            }
        }

        // 3. Classify node domain:
        node.Category = ClassifyNodeDomain(node);

        foreach (var child in node.Children)
        {
            EnrichNodesPostPassRecursive(child, multi, activeBabelOverrides);
        }
    }

    private static string SynthesizeVariantNodeName(ItemGraphNode node)
    {
        if (Engine_RecordNameCatalog.TryGetCuratedNodeName(node.RecordHash, out string curated))
        {
            return curated;
        }

        string baseName = string.Empty;
        if (node.Parent is not null &&
            node.Parent.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash &&
            !node.Parent.Label.StartsWith("Node 0x", StringComparison.OrdinalIgnoreCase))
        {
            baseName = node.Parent.Label;
            if (baseName.EndsWith(" Base", StringComparison.OrdinalIgnoreCase))
            {
                baseName = baseName[..^5].Trim();
            }
        }

        var qualifiers = new List<string>();

        foreach (var comp in node.DirectComponents)
        {
            if (comp.SlotName.Equals("AugmentableComponent", StringComparison.OrdinalIgnoreCase))
            {
                var slotsProp = comp.Properties.FirstOrDefault(p => p.PropertyName.Equals("NumberOfSlots", StringComparison.OrdinalIgnoreCase));
                if (slotsProp is not null && !string.IsNullOrWhiteSpace(slotsProp.CurrentValue))
                {
                    string plural = slotsProp.CurrentValue == "1" ? "1 Augment Slot" : $"{slotsProp.CurrentValue} Augment Slots";
                    qualifiers.Add(plural);
                    continue;
                }
            }

            if (comp.SlotName.Equals("CarriedComponent", StringComparison.OrdinalIgnoreCase))
            {
                var slotProp = comp.Properties.FirstOrDefault(p => p.PropertyName.Equals("DefaultCarrySlot", StringComparison.OrdinalIgnoreCase));
                if (slotProp is not null && !string.IsNullOrWhiteSpace(slotProp.CurrentValue) && !slotProp.CurrentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    qualifiers.Add(slotProp.CurrentValue);
                    continue;
                }
            }

            if (comp.SlotName.Equals("GraphicAppearanceStaticMeshComponent", StringComparison.OrdinalIgnoreCase) ||
                comp.SlotName.Equals("GraphicAppearanceComponent", StringComparison.OrdinalIgnoreCase))
            {
                var modelProp = comp.Properties.FirstOrDefault(p => p.PropertyName.Equals("ModelFile", StringComparison.OrdinalIgnoreCase));
                if (modelProp is not null && !string.IsNullOrWhiteSpace(modelProp.CurrentValue) && !modelProp.CurrentValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    qualifiers.Add(Engine_ItemLabels.Prettify(modelProp.CurrentValue));
                    continue;
                }
            }

            if (comp.SlotName.Equals("ShopItemComponent", StringComparison.OrdinalIgnoreCase))
            {
                var priceProp = comp.Properties.FirstOrDefault(p => p.PropertyName.Equals("BasePrice", StringComparison.OrdinalIgnoreCase));
                if (priceProp is not null && !string.IsNullOrWhiteSpace(priceProp.CurrentValue))
                {
                    qualifiers.Add($"Price {priceProp.CurrentValue}");
                    continue;
                }
            }

            string shortComp = comp.DisplayName.Replace(" Component", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(shortComp) && qualifiers.Count < 2)
            {
                qualifiers.Add(shortComp);
            }
        }

        if (qualifiers.Count == 0)
        {
            var stripped = node.InheritedComponents.Where(c => c.IsStrippedByNode).ToList();
            if (stripped.Count > 0)
            {
                qualifiers.Add("No " + stripped[0].DisplayName.Replace(" Component", "", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(baseName))
        {
            if (qualifiers.Count > 0)
            {
                return $"{baseName} ({string.Join(", ", qualifiers.Take(2))})";
            }
            return $"{baseName} Variant (0x{node.RecordHash:X8})";
        }

        if (qualifiers.Count > 0)
        {
            return $"{string.Join(" / ", qualifiers.Take(2))} (0x{node.RecordHash:X8})";
        }

        return $"Node 0x{node.RecordHash:X8}";
    }

    private static string ClassifyNodeDomain(ItemGraphNode node)
    {
        if (node.RecordHash == Engine_RecordNameCatalog.WorldEntityRootHash ||
            node.RecordHash == Engine_RecordNameCatalog.InteractiveWorldObjectBaseHash ||
            node.RecordHash == Engine_RecordNameCatalog.InventoryItemBaseHash)
        {
            return "All";
        }

        // Check lineage for known top-level branch roots:
        ItemGraphNode? walk = node;
        var guard = new HashSet<uint>();
        while (walk is not null && guard.Add(walk.RecordHash))
        {
            switch (walk.RecordHash)
            {
                case 0x815D2C8C: // Weapons Base
                    return "Weapons";
                case 0x8D7E8E6E: // Clothing & Armor Base
                    return "Clothing";
                case 0x48D0E450: // Potions Base
                    return "Potions";
                case 0x522858DA: // Augments Base
                    return "Augments";
                case 0xBA93334D: // Gifts, Trophies & Deeds Base
                case 0x16B7FD22: // Gifts & Food Base
                case 0x5AE7E572: // Trophies Base
                case 0xD2472BD5: // Hero Titles Base
                    return "Gifts";
                case 0xF234B26B: // Books & Documents Base
                    return "Books";
                case 0xDBA16BCA: // Furniture Base
                case 0x586E4A9F: // Interactive Props, Chests & Doors Base
                    return "Furniture & Props";
                case 0xCD69A0A8: // Creatures & NPCs Base
                    return "Creatures & NPCs";
                case 0xBC1E195A: // Buildings, Real Estate & Shops Base
                    return "Buildings & Shops";
                case 0x01136AFB: // Loot Tables & Random Item Generators Base
                    return "Loot & Generators";
                case 0x9A779DBF: // Visual Effects & Sound Emitters Base
                    return "Visual FX & Audio";
                case 0xFEBBC06F: // World Markers & Triggers Base
                    return "World Markers";
            }
            walk = walk.Parent;
        }

        // Check component slots:
        bool HasSlot(string name) =>
            node.DirectComponents.Any(c => c.SlotName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            node.InheritedComponents.Any(c => !c.IsStrippedByNode && c.SlotName.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (HasSlot("WeaponComponent") || HasSlot("FirearmComponent"))
        {
            return "Weapons";
        }
        if (HasSlot("CreatureComponent") || HasSlot("CreatureAIComponent") || HasSlot("VillageMemberComponent"))
        {
            return "Creatures & NPCs";
        }
        if (HasSlot("BuildingComponent") || HasSlot("ShopComponent") || HasSlot("RentableComponent") || HasSlot("BuyableHouseComponent"))
        {
            return "Buildings & Shops";
        }
        if (HasSlot("FurnitureUnlockComponent") || HasSlot("ChestComponent") || HasSlot("BedComponent") || HasSlot("DoorComponent") || HasSlot("SwitchComponent") || HasSlot("SmashableComponent"))
        {
            return "Furniture & Props";
        }
        if (HasSlot("CreatureGeneratorComponent"))
        {
            return "Loot & Generators";
        }
        if (HasSlot("SoundEmitterComponent") || HasSlot("ParticleEmitterComponent") || HasSlot("LightComponent"))
        {
            return "Visual FX & Audio";
        }
        if (HasSlot("MarkerComponent") || HasSlot("TriggerComponent") || HasSlot("CameraPointComponent"))
        {
            return "World Markers";
        }
        if (HasSlot("ReadableComponent"))
        {
            return "Books";
        }

        if (node.Detail is not null)
        {
            string itemDomain = ClassifyDomain(node.Detail);
            if (!itemDomain.Equals("Other", StringComparison.OrdinalIgnoreCase))
            {
                return itemDomain;
            }
        }

        return "Furniture & Props";
    }

    private static void PropagateDomainCategories(ItemGraphNode node)
    {
        foreach (var child in node.Children)
        {
            PropagateDomainCategories(child);
        }

        if (node.Children.Count > 0)
        {
            var childCategories = node.Children
                .Select(c => c.Category)
                .Where(c => !string.IsNullOrEmpty(c) && c != "All")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (childCategories.Count == 1 && node.RecordHash != Engine_RecordNameCatalog.WorldEntityRootHash)
            {
                node.Category = childCategories[0];
            }
            else if (childCategories.Count > 1)
            {
                node.Category = "All";
            }
        }
    }

    /// <summary>
    /// Classifies an item into one of the top-level organizational domains.
    /// </summary>
    public static string ClassifyDomain(ItemDetail d)
    {
        if (d.WeaponComponent != 0 ||
            d.Category == Engine_ItemCategories.Weapon ||
            d.WeaponType != 0 ||
            d.InternalName.StartsWith("Weapon_", StringComparison.OrdinalIgnoreCase) ||
            d.InternalName.StartsWith("ObjectInventoryWeapon", StringComparison.OrdinalIgnoreCase) ||
            d.NameTagName.StartsWith("INV_ITEM_WEAPON", StringComparison.OrdinalIgnoreCase))
        {
            return "Weapons";
        }

        if (d.Category is Engine_ItemCategories.Hat or Engine_ItemCategories.Coat
            or Engine_ItemCategories.Shirt or Engine_ItemCategories.Gloves
            or Engine_ItemCategories.Trousers or Engine_ItemCategories.Boots
            or Engine_ItemCategories.Accessory or Engine_ItemCategories.Outfit
            or Engine_ItemCategories.Hair or Engine_ItemCategories.Beard
            or Engine_ItemCategories.Moustache ||
            d.BodyAreasCovered != 0 || !string.IsNullOrEmpty(d.WornModelPath))
        {
            return "Clothing";
        }

        if (d.Category == Engine_ItemCategories.Potion ||
            d.DisplayName.Contains("Potion", StringComparison.OrdinalIgnoreCase) ||
            d.DisplayName.Contains("Elixir", StringComparison.OrdinalIgnoreCase) ||
            d.DisplayName.Contains("Phial", StringComparison.OrdinalIgnoreCase))
        {
            return "Potions";
        }

        if (d.Category == Engine_ItemCategories.Augment ||
            d.DisplayName.Contains("Augment", StringComparison.OrdinalIgnoreCase))
        {
            return "Augments";
        }

        if (d.Category == Engine_ItemCategories.Gift || d.Category == Engine_ItemCategories.Trophy)
        {
            return "Gifts";
        }

        if (d.Category == Engine_ItemCategories.Archaeology ||
            d.DisplayName.Contains("Book", StringComparison.OrdinalIgnoreCase) ||
            d.DisplayName.Contains("Document", StringComparison.OrdinalIgnoreCase))
        {
            return "Books";
        }

        return "Furniture & Props";
    }

    /// <summary>
    /// Constructs the offline / headless canonical hierarchy rooted at <c>0x46F79080</c> (World Entity Root)
    /// for unit tests and offline fallback.
    /// </summary>
    public static ItemGraphNode BuildFallbackGraph()
    {
        var worldRoot = CreateFallbackNode(
            Engine_RecordNameCatalog.WorldEntityRootHash,
            0,
            "World Entity Root",
            "All",
            "Top-level GDB entity root record (0x46F79080).");

        var interactiveBase = CreateFallbackNode(
            Engine_RecordNameCatalog.InteractiveWorldObjectBaseHash,
            worldRoot.RecordHash,
            "Interactive World Object Base",
            "All",
            "Base record for interactive world objects (0x6D8C4798).",
            ("ObjectComponent", Engine_GameHash.Fnv1("ObjectComponent"), new[] { ("Breakable", GdbFieldType.Bool, "false") }),
            ("GraphicAppearanceComponent", Engine_GameHash.Fnv1("GraphicAppearanceComponent"), new[] { ("Alpha", GdbFieldType.Float, "1") }));
        worldRoot.AddChild(interactiveBase);

        var inventoryBase = CreateFallbackNode(
            Engine_RecordNameCatalog.InventoryItemBaseHash,
            interactiveBase.RecordHash,
            "Inventory Item Base",
            "All",
            "Base record for inventory items (0xC9397216).",
            ("TargetedComponent", Engine_GameHash.Fnv1("TargetedComponent"), new[] { ("Targetable", GdbFieldType.Bool, "true") }),
            ("ShopItemComponent", 0xDEC45061, new[] { ("BasePrice", GdbFieldType.Int, "100") }),
            ("InventoryItemComponent", 0xC3318103, new[]
            {
                ("NameTag", GdbFieldType.String, ""),
                ("DescriptionTag", GdbFieldType.String, ""),
                ("Rating", GdbFieldType.Int, "1"),
            }));
        interactiveBase.AddChild(inventoryBase);

        // 1. Weapons branch (0x815D2C8C)
        var weaponsBase = CreateFallbackNode(
            0x815D2C8C,
            inventoryBase.RecordHash,
            "Weapons Base",
            "Weapons",
            "Base record for all melee and ranged weapons.",
            ("WeaponComponent", 0x2C34431E, new[] { ("DamageMultiplier", GdbFieldType.Float, "10"), ("AttackSpeedMultiplier", GdbFieldType.Float, "1") }),
            ("AugmentableComponent", 0x9DC93C5B, new[] { ("NumberOfSlots", GdbFieldType.Int, "0") }));
        inventoryBase.AddChild(weaponsBase);

        var lightMeleeBase = CreateFallbackNode(
            0x4B6A3899,
            weaponsBase.RecordHash,
            "Light Melee Weapons Base",
            "Weapons",
            "Base record for light melee weapons.");
        weaponsBase.AddChild(lightMeleeBase);

        var katanasBase = CreateFallbackNode(
            0x47245B15,
            lightMeleeBase.RecordHash,
            "Katanas Base",
            "Weapons",
            "Base record for katanas.",
            ("WeaponComponent", 0x2C34431E, new[] { ("DamageMultiplier", GdbFieldType.Float, "18"), ("AttackSpeedMultiplier", GdbFieldType.Float, "1.15") }));
        lightMeleeBase.AddChild(katanasBase);

        katanasBase.AddChild(CreateFallbackItemNode("Iron Katana", 0x3FAD4179, katanasBase.RecordHash, 600, 18.0f, 1.15f, 1, 2, "Weapons"));
        katanasBase.AddChild(CreateFallbackItemNode("Steel Katana", 0xB8C9899B, katanasBase.RecordHash, 2400, 29.0f, 1.15f, 2, 3, "Weapons"));
        var masterKatana = CreateFallbackItemNode("Master Katana", 0xA1BD29CD, katanasBase.RecordHash, 9600, 58.0f, 1.15f, 3, 4, "Weapons");
        katanasBase.AddChild(masterKatana);
        masterKatana.AddChild(CreateFallbackItemNode("The Daichi", 0x3966741D, masterKatana.RecordHash, 32000, 96.0f, 1.25f, 3, 5, "Weapons"));

        var longswordsBase = CreateFallbackNode(
            0xE7328E75,
            lightMeleeBase.RecordHash,
            "Longswords Base",
            "Weapons",
            "Base record for longswords.",
            ("WeaponComponent", 0x2C34431E, new[] { ("DamageMultiplier", GdbFieldType.Float, "16"), ("AttackSpeedMultiplier", GdbFieldType.Float, "1.0") }));
        lightMeleeBase.AddChild(longswordsBase);

        longswordsBase.AddChild(CreateFallbackItemNode("Iron Longsword", 0xBDFE364C, longswordsBase.RecordHash, 450, 16.0f, 1.0f, 1, 2, "Weapons"));
        longswordsBase.AddChild(CreateFallbackItemNode("Master Longsword", 0x22C02D90, longswordsBase.RecordHash, 7200, 53.0f, 1.0f, 3, 4, "Weapons"));
        longswordsBase.AddChild(CreateFallbackItemNode("Hal's Sword", 0x61C68648, longswordsBase.RecordHash, 15000, 70.0f, 1.1f, 4, 5, "Weapons"));

        var firearmsBase = CreateFallbackNode(
            0xDA0ECA94,
            weaponsBase.RecordHash,
            "Firearms & Bows Base",
            "Weapons",
            "Base record for ranged firearms and bows.",
            ("FirearmComponent", 0x0A644D42, new[] { ("ReloadTime", GdbFieldType.Float, "1.25"), ("BulletCapacity", GdbFieldType.Int, "1"), ("Range", GdbFieldType.Float, "35") }));
        weaponsBase.AddChild(firearmsBase);

        var pistolsBase = CreateFallbackNode(
            0x6EE3899C,
            firearmsBase.RecordHash,
            "Pistols & Bows Base",
            "Weapons",
            "Base record for pistols and bows.");
        firearmsBase.AddChild(pistolsBase);

        pistolsBase.AddChild(CreateFallbackItemNode("Toy Gun", 0x2FDDF213, pistolsBase.RecordHash, 15, 1.0f, 1.0f, 0, 1, "Weapons"));
        pistolsBase.AddChild(CreateFallbackItemNode("Rusty Flintlock Pistol", 0x3A153787, pistolsBase.RecordHash, 180, 18.0f, 1.0f, 0, 1, "Weapons"));
        pistolsBase.AddChild(CreateFallbackItemNode("Master Flintlock Pistol", 0xCCEDB63B, pistolsBase.RecordHash, 11000, 61.0f, 1.0f, 3, 4, "Weapons"));

        // 2. Clothing branch (0x8D7E8E6E)
        var clothingBase = CreateFallbackNode(
            0x8D7E8E6E,
            inventoryBase.RecordHash,
            "Clothing & Armor Base",
            "Clothing",
            "Base record for clothing and armor.",
            ("AppearanceModifierComponent", 0xD949A6F5, new[] { ("ClusterSortLayer", GdbFieldType.Int, "0") }));
        inventoryBase.AddChild(clothingBase);

        var coatsBase = CreateFallbackNode(0x4DC80022, clothingBase.RecordHash, "Coats & Robes Base", "Clothing", "Base record for coats and robes.");
        clothingBase.AddChild(coatsBase);
        coatsBase.AddChild(CreateFallbackItemNode("Noble Gent's Coat", 0x20000003, coatsBase.RecordHash, 1200, 0, 0, 0, 3, "Clothing"));

        var hatsBase = CreateFallbackNode(0xB7DC68E3, clothingBase.RecordHash, "Hats & Headwear Base", "Clothing", "Base record for hats and headwear.");
        clothingBase.AddChild(hatsBase);
        hatsBase.AddChild(CreateFallbackItemNode("Bandit Bandana", 0x20000005, hatsBase.RecordHash, 300, 0, 0, 0, 2, "Clothing"));

        // 3. Potions branch (0x48D0E450)
        var potionsBase = CreateFallbackNode(
            0x48D0E450,
            inventoryBase.RecordHash,
            "Potions Base",
            "Potions",
            "Base record for potions.",
            ("OnUseEffectComponent", 0xE3EFA160, new[] { ("InstantEffect", GdbFieldType.Bool, "true") }));
        inventoryBase.AddChild(potionsBase);
        potionsBase.AddChild(CreateFallbackItemNode("Standard Health Potion", 0x68DC87BD, potionsBase.RecordHash, 80, 0, 0, 0, 3, "Potions"));

        // 4. Augments branch (0x522858DA)
        var augmentsBase = CreateFallbackNode(
            0x522858DA,
            inventoryBase.RecordHash,
            "Augments Base",
            "Augments",
            "Base record for weapon augments.",
            ("AugmentableComponent", 0x9DC93C5B, new[] { ("NumberOfSlots", GdbFieldType.Int, "0") }));
        inventoryBase.AddChild(augmentsBase);
        augmentsBase.AddChild(CreateFallbackItemNode("Flame Augment", 0xF701AD8C, augmentsBase.RecordHash, 3000, 0, 0, 0, 3, "Augments"));

        // 5. Furniture branch directly under World Entity Root (0xDBA16BCA)
        var furnitureBase = CreateFallbackNode(
            0xDBA16BCA,
            worldRoot.RecordHash,
            "Furniture Base",
            "Furniture & Props",
            "Base record for household furniture.",
            ("FurnitureUnlockComponent", Engine_GameHash.Fnv1("FurnitureUnlockComponent"), new[] { ("FurnitureType", GdbFieldType.Enum, "0") }));
        worldRoot.AddChild(furnitureBase);

        // 6. Creatures & NPCs branch under Interactive World Object Base (0xCD69A0A8)
        var creaturesBase = CreateFallbackNode(
            0xCD69A0A8,
            interactiveBase.RecordHash,
            "Creatures & NPCs Base",
            "Creatures & NPCs",
            "Base record for all creatures, villagers, and enemies.",
            ("CreatureComponent", Engine_GameHash.Fnv1("CreatureComponent"), new[] { ("NameTag", GdbFieldType.String, ""), ("WalkSpeed", GdbFieldType.Float, "1.5"), ("RunSpeed", GdbFieldType.Float, "4.5") }),
            ("FactionComponent", Engine_GameHash.Fnv1("FactionComponent"), new[] { ("PrimaryFaction", GdbFieldType.Enum, "0") }));
        interactiveBase.AddChild(creaturesBase);

        // 7. Buildings, Real Estate & Shops branch under Interactive World Object Base (0xBC1E195A)
        var buildingsBase = CreateFallbackNode(
            0xBC1E195A,
            interactiveBase.RecordHash,
            "Buildings, Real Estate & Shops Base",
            "Buildings & Shops",
            "Base record for houses, shops, and buildings.",
            ("BuildingComponent", Engine_GameHash.Fnv1("BuildingComponent"), new[] { ("BuildingName", GdbFieldType.String, ""), ("BaseCost", GdbFieldType.Int, "2500") }));
        interactiveBase.AddChild(buildingsBase);

        PropagateFallbackInheritedComponents(worldRoot);
        SortHierarchyChildren(worldRoot);
        return worldRoot;
    }

    private static ItemGraphNode CreateFallbackNode(
        uint hash,
        uint parentHash,
        string label,
        string domain,
        string desc,
        params (string SlotName, uint SlotHash, (string PropName, GdbFieldType Type, string Val)[] Props)[] directComps)
    {
        var node = new ItemGraphNode
        {
            Id = $"Node_{hash:X8}",
            RecordHash = hash,
            ParentHash = parentHash,
            Label = label,
            Category = domain,
            Description = desc,
        };

        foreach (var (slotName, slotHash, props) in directComps)
        {
            Engine_RecordNameCatalog.TryGetComponentSchema(slotHash, out ComponentSlotSchema? schema);
            uint compRecordHash = Engine_GameHash.Fnv1($"{label}_{slotName}");
            var slot = new NodeComponentSlot
            {
                SlotHash = slotHash,
                SlotName = slotName,
                DisplayName = schema?.DisplayName ?? EditorModValueService.PrettifyPropertyName(slotName),
                Category = schema?.Category ?? "Component",
                Summary = schema?.Summary ?? string.Empty,
                ComponentRecordHash = compRecordHash,
                ComponentRecordName = $"{label}_{slotName}",
                IsDirectOnNode = true,
                SourceNodeHash = hash,
                SourceNodeLabel = label,
            };

            foreach (var (propName, type, val) in props)
            {
                var p = CreateFallbackProp(propName, EditorModValueService.PrettifyPropertyName(propName), slotName, compRecordHash, type, val);
                slot.Properties.Add(p);
                node.Properties.Add(p);
            }

            node.DirectComponents.Add(slot);
        }

        return node;
    }

    private static ItemGraphNode CreateFallbackItemNode(
        string label,
        uint hash,
        uint parentHash,
        int price,
        float damage,
        float speed,
        int slots,
        int rating,
        string domain)
    {
        string descText = $"GDB item record {label} (0x{hash:X8}).";
        var comps = new List<(string SlotName, uint SlotHash, (string PropName, GdbFieldType Type, string Val)[] Props)>
        {
            ("ShopItemComponent", 0xDEC45061, new[] { ("BasePrice", GdbFieldType.Int, price.ToString(CultureInfo.InvariantCulture)) }),
            ("InventoryItemComponent", 0xC3318103, new[]
            {
                ("NameTag", GdbFieldType.String, label),
                ("DescriptionTag", GdbFieldType.String, descText),
                ("Rating", GdbFieldType.Int, rating.ToString(CultureInfo.InvariantCulture)),
            }),
        };

        if (damage > 0)
        {
            comps.Add(("WeaponComponent", 0x2C34431E, new[]
            {
                ("DamageMultiplier", GdbFieldType.Float, damage.ToString("0.#####", CultureInfo.InvariantCulture)),
                ("AttackSpeedMultiplier", GdbFieldType.Float, speed.ToString("0.#####", CultureInfo.InvariantCulture)),
            }));
        }

        if (slots > 0)
        {
            comps.Add(("AugmentableComponent", 0x9DC93C5B, new[]
            {
                ("NumberOfSlots", GdbFieldType.Int, slots.ToString(CultureInfo.InvariantCulture)),
            }));
        }

        var node = CreateFallbackNode(hash, parentHash, label, domain, descText, comps.ToArray());
        node.Detail = new ItemDetail
        {
            RecordHash = hash,
            DefinitionHash = hash,
            Label = label,
            DisplayName = label,
            BasePrice = price,
            DamageMultiplier = damage,
            AttackSpeedMultiplier = speed,
            AugmentSlots = slots,
            Rating = rating,
        };
        return node;
    }

    private static void PropagateFallbackInheritedComponents(ItemGraphNode node)
    {
        if (node.Parent is not null)
        {
            var directHashes = new HashSet<uint>(node.DirectComponents.Select(c => c.SlotHash));
            foreach (var directSlot in node.DirectComponents)
            {
                var anc = node.Parent.FindEffectiveComponent(directSlot.SlotHash);
                if (anc is not null)
                {
                    directSlot.OverriddenAncestorNodeHash = anc.SourceNodeHash;
                    directSlot.OverriddenAncestorNodeLabel = anc.SourceNodeLabel;
                }
            }

            foreach (var pDirect in node.Parent.DirectComponents)
            {
                if (!directHashes.Contains(pDirect.SlotHash))
                {
                    node.InheritedComponents.Add(pDirect.CloneAsInherited());
                }
            }

            foreach (var pInh in node.Parent.InheritedComponents)
            {
                if (!directHashes.Contains(pInh.SlotHash) && !node.InheritedComponents.Any(c => c.SlotHash == pInh.SlotHash))
                {
                    node.InheritedComponents.Add(pInh.CloneAsInherited());
                }
            }
        }

        foreach (var child in node.Children)
        {
            PropagateFallbackInheritedComponents(child);
        }
    }

    private static ModItemProperty CreateFallbackProp(
        string name, string display, string cat, uint targetHash, GdbFieldType type, string val)
    {
        bool isLoc = type == GdbFieldType.String && Engine_RecordNameCatalog.IsLocalizedTextTagField(name);
        return new ModItemProperty
        {
            PropertyName = name,
            DisplayLabel = display,
            CategoryId = cat,
            CategoryDisplayName = cat,
            PropertyHash = Engine_GameHash.Fnv1(name),
            TargetRecordHash = targetHash,
            DeclaringHash = targetHash,
            Type = type,
            VanillaValue = val,
            CurrentValue = val,
            IsLocalizedTextTag = isLoc,
        };
    }

    private static void SortHierarchyChildren(ItemGraphNode node)
    {
        if (node.Children.Count > 1)
        {
            node.Children.Sort((a, b) =>
            {
                // Branch nodes with children sort before leaf nodes so expandable branches sit at the top of each group:
                bool aHasChildren = a.Children.Count > 0;
                bool bHasChildren = b.Children.Count > 0;
                if (aHasChildren != bHasChildren)
                {
                    return aHasChildren ? -1 : 1;
                }

                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
        }

        foreach (var child in node.Children)
        {
            SortHierarchyChildren(child);
        }
    }
}
#endif
