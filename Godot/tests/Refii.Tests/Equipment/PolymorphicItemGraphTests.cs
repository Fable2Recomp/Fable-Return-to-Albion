namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Assets.UI;
using Refii.Equipment;
using Refii.Gdb;

[TestSuite]
public class PolymorphicItemGraphTests
{
    [TestCase]
    public void TestGraphBuildsFromTopWorldEntityRoot()
    {
        var graph = PolymorphicItemGraph.Build();

        AssertThat(graph.Root).IsNotNull();
        AssertThat(graph.Root.RecordHash).IsEqual(Engine_RecordNameCatalog.WorldEntityRootHash);
        AssertThat(graph.Root.Label).IsEqual("World Entity Root");
        AssertThat(graph.ActivePath.Count).IsEqual(1);
        AssertThat(graph.SelectedNode).IsEqual(graph.Root);

        // Verify linear ancestry chain from World Entity Root -> Interactive World Object Base -> Inventory Item Base:
        AssertThat(graph.Root.Children.Count).IsGreater(0);
        ItemGraphNode interactiveBase = graph.Root.Children.First(c => c.RecordHash == Engine_RecordNameCatalog.InteractiveWorldObjectBaseHash);
        AssertThat(interactiveBase.Label).IsEqual("Interactive World Object Base");

        ItemGraphNode inventoryItemBase = interactiveBase.Children.First(c => c.RecordHash == Engine_RecordNameCatalog.InventoryItemBaseHash);
        AssertThat(inventoryItemBase.Label).IsEqual("Inventory Item Base");

        // Check major branches under Inventory Item Base:
        var branchLabels = inventoryItemBase.Children.Select(c => c.Label).ToList();
        AssertThat(branchLabels).Contains("Weapons Base");
        AssertThat(branchLabels).Contains("Clothing & Armor Base");
        AssertThat(branchLabels).Contains("Potions Base");
        AssertThat(branchLabels).Contains("Augments Base");

        // Furniture Base branches off World Entity Root (0x46F79080):
        var rootChildLabels = graph.Root.Children.Select(c => c.Label).ToList();
        AssertThat(rootChildLabels).Contains("Furniture Base");
    }

    [TestCase]
    public void TestAncestryPathAndStepToAncestor()
    {
        var graph = PolymorphicItemGraph.Build();

        ItemGraphNode? katanaBase = graph.FindNodeByHash(0x47245B15u);
        AssertThat(katanaBase).IsNotNull();
        AssertThat(katanaBase!.Label).IsEqual("Katanas Base");

        ItemGraphNode masterKatana = katanaBase.Children.First(c => c.Label == "Master Katana");
        graph.SelectNode(masterKatana);

        // Path should be: World Entity Root -> Interactive World Object Base -> Inventory Item Base -> Weapons Base -> Light Melee Weapons Base -> Katanas Base -> Master Katana
        AssertThat(graph.SelectedNode).IsEqual(masterKatana);
        AssertThat(graph.ActivePath.Count).IsEqual(7);
        AssertThat(graph.ActivePath[0].RecordHash).IsEqual(0x46F79080u);
        AssertThat(graph.ActivePath[1].RecordHash).IsEqual(0x6D8C4798u);
        AssertThat(graph.ActivePath[2].RecordHash).IsEqual(0xC9397216u);
        AssertThat(graph.ActivePath[3].RecordHash).IsEqual(0x815D2C8Cu);
        AssertThat(graph.ActivePath[4].RecordHash).IsEqual(0x4B6A3899u);
        AssertThat(graph.ActivePath[5].RecordHash).IsEqual(0x47245B15u);
        AssertThat(graph.ActivePath[6].RecordHash).IsEqual(0xA1BD29CDu);

        // Step back to Light Melee Weapons Base:
        ItemGraphNode meleeBase = graph.ActivePath[4];
        graph.StepToAncestor(meleeBase);
        AssertThat(graph.ActivePath.Count).IsEqual(5);
        AssertThat(graph.SelectedNode).IsEqual(meleeBase);

        var childLabels = meleeBase.Children.Select(c => c.Label).ToList();
        AssertThat(childLabels).Contains("Katanas Base");
        AssertThat(childLabels).Contains("Longswords Base");

        // Reset to root:
        graph.ResetToRoot();
        AssertThat(graph.ActivePath.Count).IsEqual(1);
        AssertThat(graph.SelectedNode).IsEqual(graph.Root);
    }

    [TestCase]
    public void TestComponentStackInheritanceAndOverrides()
    {
        var graph = PolymorphicItemGraph.Build();

        ItemGraphNode? masterKatana = graph.FindNodeByHash(0xA1BD29CDu);
        AssertThat(masterKatana).IsNotNull();

        // Master Katana directly defines InventoryItemComponent, WeaponComponent, ShopItemComponent:
        var directSlotNames = masterKatana!.DirectComponents.Select(c => c.SlotName).ToList();
        AssertThat(directSlotNames).Contains("InventoryItemComponent");
        AssertThat(directSlotNames).Contains("WeaponComponent");
        AssertThat(directSlotNames).Contains("ShopItemComponent");

        // WeaponComponent on Master Katana overrides the ancestor WeaponComponent:
        var weaponSlot = masterKatana.DirectComponents.First(c => c.SlotName == "WeaponComponent");
        AssertThat(weaponSlot.IsDirectOnNode).IsTrue();
        AssertThat(weaponSlot.OverriddenAncestorNodeHash).IsNotEqual(0u);

        // TargetedComponent and ObjectComponent are inherited by walking up ancestors:
        var inheritedSlotNames = masterKatana.InheritedComponents.Select(c => c.SlotName).ToList();
        AssertThat(inheritedSlotNames).Contains("TargetedComponent");
        AssertThat(inheritedSlotNames).Contains("ObjectComponent");

        // The Daichi is a child node of Master Katana and inherits from it:
        ItemGraphNode? daichi = masterKatana.Children.FirstOrDefault(c => c.RecordHash == 0x3966741Du);
        AssertThat(daichi).IsNotNull();
        AssertThat(daichi!.Label).IsEqual("The Daichi");
        AssertThat(daichi.Parent).IsEqual(masterKatana);

        // Toy Gun inherits FirearmComponent from Firearms & Bows Base (0xDA0ECA94):
        ItemGraphNode? toyGun = graph.FindNodeByHash(0x2FDDF213u);
        AssertThat(toyGun).IsNotNull();
        AssertThat(toyGun!.Label).IsEqual("Toy Gun");
        var firearmInherited = toyGun.InheritedComponents.FirstOrDefault(c => c.SlotName == "FirearmComponent");
        AssertThat(firearmInherited).IsNotNull();
        AssertThat(firearmInherited!.SourceNodeHash).IsEqual(0xDA0ECA94u);
    }

    [TestCase]
    public void TestPropertyModificationAndValues()
    {
        var graph = PolymorphicItemGraph.Build();

        ItemGraphNode? masterKatana = graph.FindNodeByHash(0xA1BD29CDu);
        AssertThat(masterKatana).IsNotNull();

        var priceProp = masterKatana!.Properties.Find(p => p.PropertyName == "BasePrice");
        AssertThat(priceProp).IsNotNull();
        AssertThat(priceProp!.VanillaValue).IsEqual("9600");
        AssertThat(priceProp.IsModified).IsFalse();

        // Edit price:
        priceProp.CurrentValue = "15000";
        priceProp.IsModified = !string.Equals(priceProp.CurrentValue, priceProp.VanillaValue, StringComparison.Ordinal);
        AssertThat(priceProp.IsModified).IsTrue();

        // Revert:
        priceProp.CurrentValue = priceProp.VanillaValue;
        priceProp.IsModified = false;
        AssertThat(priceProp.IsModified).IsFalse();
    }

    [TestCase]
    public void TestDomainFilteringFromTopRoot()
    {
        var graph = PolymorphicItemGraph.Build();

        // 1. Initial All filter:
        AssertThat(graph.DomainFilter).IsEqual("All");
        AssertThat(graph.GetVisibleChildren(graph.Root).Count).IsEqual(2);

        ItemGraphNode invBase = graph.FindNodeByHash(Engine_RecordNameCatalog.InventoryItemBaseHash)!;
        AssertThat(graph.GetVisibleChildren(invBase).Count).IsEqual(4);

        // 2. Filter to Weapons:
        graph.SetDomainFilter("Weapons");
        AssertThat(graph.DomainFilter).IsEqual("Weapons");
        var visibleUnderInvBase = graph.GetVisibleChildren(invBase);
        AssertThat(visibleUnderInvBase.Count).IsEqual(1);
        AssertThat(visibleUnderInvBase[0].Label).IsEqual("Weapons Base");

        // 3. Filter to Clothing:
        graph.SetDomainFilter("Clothing");
        var clothingUnderInvBase = graph.GetVisibleChildren(invBase);
        AssertThat(clothingUnderInvBase.Count).IsEqual(1);
        AssertThat(clothingUnderInvBase[0].Label).IsEqual("Clothing & Armor Base");

        // 4. Reset to All:
        graph.SetDomainFilter("All");
        AssertThat(graph.GetVisibleChildren(invBase).Count).IsEqual(4);

        // 5. Filter to Creatures & NPCs and Buildings & Shops:
        ItemGraphNode interactiveBase = graph.FindNodeByHash(Engine_RecordNameCatalog.InteractiveWorldObjectBaseHash)!;
        graph.SetDomainFilter("Creatures & NPCs");
        var creaturesUnderInteractive = graph.GetVisibleChildren(interactiveBase);
        AssertThat(creaturesUnderInteractive.Count).IsEqual(1);
        AssertThat(creaturesUnderInteractive[0].Label).IsEqual("Creatures & NPCs Base");

        graph.SetDomainFilter("Buildings & Shops");
        var buildingsUnderInteractive = graph.GetVisibleChildren(interactiveBase);
        AssertThat(buildingsUnderInteractive.Count).IsEqual(1);
        AssertThat(buildingsUnderInteractive[0].Label).IsEqual("Buildings, Real Estate & Shops Base");
    }

    [TestCase]
    public void TestInventoryItemComponentExposesEditableNameAndDescriptionTags()
    {
        var graph = PolymorphicItemGraph.Build();

        ItemGraphNode? masterKatana = graph.FindNodeByHash(0xA1BD29CDu);
        AssertThat(masterKatana).IsNotNull();

        NodeComponentSlot? invComp = masterKatana!.DirectComponents.FirstOrDefault(c => c.SlotName == "InventoryItemComponent");
        AssertThat(invComp).IsNotNull();

        var nameProp = invComp!.Properties.FirstOrDefault(p => p.PropertyName == "NameTag");
        var descProp = invComp.Properties.FirstOrDefault(p => p.PropertyName == "DescriptionTag");
        AssertThat(nameProp).IsNotNull();
        AssertThat(descProp).IsNotNull();
        AssertThat(nameProp!.IsLocalizedTextTag).IsTrue();
        AssertThat(descProp!.IsLocalizedTextTag).IsTrue();
        AssertThat(nameProp.CurrentValue).IsEqual("Master Katana");
    }

    [TestCase]
    public void TestSharedRecordNameCatalogAndItemNamesIntegration()
    {
        // Curated base nodes resolve cleanly in Engine_RecordNameCatalog:
        AssertThat(Engine_RecordNameCatalog.TryGetCuratedNodeName(0x46F79080u, out string rootName)).IsTrue();
        AssertThat(rootName).IsEqual("World Entity Root");

        AssertThat(Engine_RecordNameCatalog.TryGetCuratedNodeName(0xC9397216u, out string invBaseName)).IsTrue();
        AssertThat(invBaseName).IsEqual("Inventory Item Base");

        AssertThat(Engine_RecordNameCatalog.TryGetCuratedNodeName(0x8C52C0C8u, out string riflesBaseName)).IsTrue();
        AssertThat(riflesBaseName).IsEqual("Flintlock Rifles Base");

        AssertThat(Engine_RecordNameCatalog.TryGetCuratedNodeName(0xDBA16BCAu, out string furnBaseName)).IsTrue();
        AssertThat(furnBaseName).IsEqual("Furniture Base");

        AssertThat(Engine_RecordNameCatalog.TryGetCuratedNodeName(0xCD69A0A8u, out string creaturesBaseName)).IsTrue();
        AssertThat(creaturesBaseName).IsEqual("Creatures & NPCs Base");

        // Placeholder text filter catches "Gui Needs Text" and "Baseitem":
        AssertThat(Engine_RecordNameCatalog.IsGenericPlaceholderName("Gui Needs Text")).IsTrue();
        AssertThat(Engine_RecordNameCatalog.IsGenericPlaceholderName("Baseitem")).IsTrue();
        AssertThat(Engine_RecordNameCatalog.IsGenericPlaceholderName("Master Katana")).IsFalse();

        // RemoveComponent is not in the attachable component schema list, and its field hash is 0x9B41D00A:
        AssertThat(Engine_RecordNameCatalog.RemoveComponentFieldHash).IsEqual(0x9B41D00Au);
        AssertThat(Engine_RecordNameCatalog.AllComponentSchemas.Any(s => s.SlotName == "RemoveComponent")).IsFalse();
    }

    [TestCase]
    public void TestDynamicGraphDiscoversChildNodesComponentsAndBuildWithOverlays()
    {
        uint rootId = Engine_RecordNameCatalog.WorldEntityRootHash;
        uint archId = 0x47245B15u; // Katanas Base
        uint archWeaponCompId = 0x47245B16u;
        uint newItemId = 0x99887766u;
        uint newShopCompId = 0x99887767u;
        uint newInvCompId = 0x99887768u;

        uint weaponSlotHash = Refii.Core.Engine_GameHash.Fnv1("WeaponComponent");
        uint shopSlotHash = Refii.Core.Engine_GameHash.Fnv1("ShopItemComponent");
        uint invSlotHash = Refii.Core.Engine_GameHash.Fnv1("InventoryItemComponent");
        uint dmgPropHash = Refii.Core.Engine_GameHash.Fnv1("DamageMultiplier");
        uint pricePropHash = Refii.Core.Engine_GameHash.Fnv1("BasePrice");

        var baseDb = new GdbPatchDatabase();

        // Root entry:
        var rootEntry = new GdbPatchEntry(rootId, 0x01);
        baseDb.Entries.Add(rootEntry);

        // Katanas Base node + WeaponComponent sub-record:
        var archEntry = new GdbPatchEntry(archId, 0x01);
        archEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, rootId));
        archEntry.Properties.Add(new GdbPatchProperty(weaponSlotHash, GdbFieldType.Reference, archWeaponCompId));
        baseDb.Entries.Add(archEntry);

        var archWeaponComp = new GdbPatchEntry(archWeaponCompId, 0x01);
        archWeaponComp.Properties.Add(new GdbPatchProperty(dmgPropHash, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(1.2f)));
        baseDb.Entries.Add(archWeaponComp);

        // Custom Katana child node + InventoryItemComponent + ShopItemComponent:
        uint nameTag = Refii.Core.Engine_GameHash.Fnv1("INV_ITEM_WEAPON_CUSTOM_KATANA_NAME");
        var itemEntry = new GdbPatchEntry(newItemId, 0x01);
        itemEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, archId));
        itemEntry.Properties.Add(new GdbPatchProperty(invSlotHash, GdbFieldType.Reference, newInvCompId));
        itemEntry.Properties.Add(new GdbPatchProperty(shopSlotHash, GdbFieldType.Reference, newShopCompId));
        baseDb.Entries.Add(itemEntry);

        var invComp = new GdbPatchEntry(newInvCompId, 0x01);
        invComp.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.NameTag, GdbFieldType.String, nameTag));
        baseDb.Entries.Add(invComp);

        var shopComp = new GdbPatchEntry(newShopCompId, 0x01);
        shopComp.Properties.Add(new GdbPatchProperty(pricePropHash, GdbFieldType.Int, 3500u));
        baseDb.Entries.Add(shopComp);

        baseDb.Names[newItemId] = "Weapon_CustomKatana";
        baseDb.Names[nameTag] = "INV_ITEM_WEAPON_CUSTOM_KATANA_NAME";
        baseDb.Names[Engine_ItemDetails.NameTag] = "NameTag";
        baseDb.Names[weaponSlotHash] = "WeaponComponent";
        baseDb.Names[shopSlotHash] = "ShopItemComponent";
        baseDb.Names[invSlotHash] = "InventoryItemComponent";
        baseDb.Names[dmgPropHash] = "DamageMultiplier";
        baseDb.Names[pricePropHash] = "BasePrice";

        byte[] gdbBytes = GdbWriter.Serialize(baseDb);
        var baseMulti = GdbMulti.Build(new[] { gdbBytes });

        // Layer a Title Update (.gdb.tu) overlay adding a child variant node that strips ShopItemComponent:
        uint overlayChildId = 0x77665544u;
        var tuDb = new GdbPatchDatabase();
        var overlayChildEntry = new GdbPatchEntry(overlayChildId, 0x01);
        overlayChildEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, newItemId));
        overlayChildEntry.Properties.Add(new GdbPatchProperty(Engine_RecordNameCatalog.RemoveComponentFieldHash, GdbFieldType.String, shopSlotHash));
        tuDb.Entries.Add(overlayChildEntry);
        tuDb.Names[overlayChildId] = "Weapon_CustomKatana_NoShop";
        tuDb.Names[Engine_RecordNameCatalog.RemoveComponentFieldHash] = "RemoveComponent";

        var overlaidMulti = GdbMulti.BuildWithOverlays(baseMulti, new[] { GdbWriter.Serialize(tuDb) });
        var graph = PolymorphicItemGraph.Build(overlaidMulti);

        AssertThat(graph.Root).IsNotNull();
        AssertThat(graph.Root.RecordHash).IsEqual(rootId);

        ItemGraphNode? customKatana = graph.FindNodeByHash(newItemId);
        AssertThat(customKatana).IsNotNull();
        AssertThat(customKatana!.Parent).IsNotNull();
        AssertThat(customKatana.Parent!.RecordHash).IsEqual(archId);
        AssertThat(customKatana.Parent.Label).IsEqual("Katanas Base");

        // Verify DirectComponents on Custom Katana has ShopItemComponent (BasePrice = 3500) and InventoryItemComponent (NameTag):
        var directShop = customKatana.DirectComponents.FirstOrDefault(c => c.SlotName == "ShopItemComponent");
        AssertThat(directShop).IsNotNull();
        AssertThat(directShop!.Properties.Any(p => p.PropertyName == "BasePrice" && p.CurrentValue == "3500")).IsTrue();

        var directInv = customKatana.DirectComponents.FirstOrDefault(c => c.SlotName == "InventoryItemComponent");
        AssertThat(directInv).IsNotNull();
        AssertThat(directInv!.Properties.Any(p => p.PropertyName == "NameTag" && p.IsLocalizedTextTag)).IsTrue();

        // Verify InheritedComponents on Custom Katana walks up to Katanas Base's WeaponComponent:
        var inheritedWeapon = customKatana.InheritedComponents.FirstOrDefault(c => c.SlotName == "WeaponComponent");
        AssertThat(inheritedWeapon).IsNotNull();
        AssertThat(inheritedWeapon!.SourceNodeHash).IsEqual(archId);
        AssertThat(inheritedWeapon.Properties.Any(p => p.PropertyName == "DamageMultiplier")).IsTrue();

        // Verify overlaid child node was discovered and has ShopItemComponent marked as IsStrippedByNode:
        ItemGraphNode? overlaidChild = graph.FindNodeByHash(overlayChildId);
        AssertThat(overlaidChild).IsNotNull();
        AssertThat(overlaidChild!.Detail).IsNotNull(); // Inherits InventoryItemComponent + WeaponComponent so Detail is populated!
        var strippedShop = overlaidChild.InheritedComponents.FirstOrDefault(c => c.SlotName == "ShopItemComponent");
        AssertThat(strippedShop).IsNotNull();
        AssertThat(strippedShop!.IsStrippedByNode).IsTrue();
    }

    [TestCase]
    public void TestRealGlobalsTreeStructureAndComponentStacks()
    {
        string path = @"F:/Documents/programming/refii/Godot/build/x86_64/assets/game/data/Globals/globals.gdb";
        if (!System.IO.File.Exists(path)) return;

        byte[] bytes = System.IO.File.ReadAllBytes(path);
        var multi = GdbMulti.Build(new[] { bytes });
        var graph = PolymorphicItemGraph.Build(multi);

        // 1. Root is 0x46F79080 (World Entity Root):
        AssertThat(graph.Root).IsNotNull();
        AssertThat(graph.Root.RecordHash).IsEqual(0x46F79080u);
        AssertThat(graph.Root.Label).IsEqual("World Entity Root");

        var allNodes = new List<ItemGraphNode>();
        void CollectNodes(ItemGraphNode n)
        {
            allNodes.Add(n);
            foreach (var child in n.Children) CollectNodes(child);
        }
        CollectNodes(graph.Root);

        AssertThat(allNodes.Count).IsGreater(500);

        // 2. No record hash appears more than once in the tree:
        var hashes = allNodes.Where(n => n.RecordHash != 0).Select(n => n.RecordHash).ToList();
        var duplicates = hashes.GroupBy(h => h).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        AssertThat(duplicates.Count).IsEqual(0);

        // 3. No node has placeholder names ("Gui Needs Text", "Baseitem", "Archetype 0x"):
        foreach (var node in allNodes)
        {
            AssertThat(node.Label.Equals("Gui Needs Text", StringComparison.OrdinalIgnoreCase)).IsFalse();
            AssertThat(node.Label.Equals("Baseitem", StringComparison.OrdinalIgnoreCase)).IsFalse();
            AssertThat(node.Label.StartsWith("Archetype 0x", StringComparison.OrdinalIgnoreCase)).IsFalse();
        }

        // 4. Verify Inventory Item Base (0xC9397216) has its core direct components:
        var invItemBase = graph.FindNodeByHash(0xC9397216u);
        AssertThat(invItemBase).IsNotNull();
        AssertThat(invItemBase!.Label).IsEqual("Inventory Item Base");
        var invBaseSlots = invItemBase.DirectComponents.Select(c => c.SlotName).ToList();
        AssertThat(invBaseSlots).Contains("InventoryItemComponent");
        AssertThat(invBaseSlots).Contains("TargetedComponent");

        // 5. Verify Flintlock Rifles Base (0x8C52C0C8) has WeaponComponent + FirearmComponent:
        var flintlockRiflesBase = graph.FindNodeByHash(0x8C52C0C8u);
        AssertThat(flintlockRiflesBase).IsNotNull();
        AssertThat(flintlockRiflesBase!.Label).IsEqual("Flintlock Rifles Base");
        var rifleSlots = flintlockRiflesBase.DirectComponents.Select(c => c.SlotName).ToList();
        AssertThat(rifleSlots).Contains("WeaponComponent");
        AssertThat(rifleSlots).Contains("FirearmComponent");

        // 6. Verify weapon variants 0x0C888863 (Iron Mace Socketed Variant) and 0x75EAFACE (NPC Carried Weapon)
        //    have readable names and populated ItemDetail (for "Open Full Item Editor"):
        var socketedIronMace = graph.FindNodeByHash(0x0C888863u);
        AssertThat(socketedIronMace).IsNotNull();
        AssertThat(socketedIronMace!.Label.StartsWith("Node 0x", StringComparison.OrdinalIgnoreCase)).IsFalse();
        AssertThat(socketedIronMace.Detail).IsNotNull();

        var npcCarriedWeapon = graph.FindNodeByHash(0x75EAFACEu);
        AssertThat(npcCarriedWeapon).IsNotNull();
        AssertThat(npcCarriedWeapon!.Label).IsEqual("NPC Carried Weapon (Shadow_R_Heel)");
        AssertThat(npcCarriedWeapon.Detail).IsNotNull();
        var strippedTargeted = npcCarriedWeapon.InheritedComponents.FirstOrDefault(c => c.SlotName == "TargetedComponent");
        AssertThat(strippedTargeted).IsNotNull();
        AssertThat(strippedTargeted!.IsStrippedByNode).IsTrue();

        // 7. Verify Furniture Base (0xDBA16BCA) and Non-Item GDB Branches (Creatures & NPCs 0xCD69A0A8, Buildings 0xBC1E195A):
        var furnitureBase = graph.FindNodeByHash(0xDBA16BCAu);
        AssertThat(furnitureBase).IsNotNull();
        AssertThat(furnitureBase!.Label).IsEqual("Furniture Base");
        AssertThat(furnitureBase.Children.Count).IsGreater(0);

        var creaturesBase = graph.FindNodeByHash(0xCD69A0A8u);
        AssertThat(creaturesBase).IsNotNull();
        AssertThat(creaturesBase!.Label).IsEqual("Creatures & NPCs Base");
        AssertThat(creaturesBase.Category).IsEqual("Creatures & NPCs");

        var buildingsBase = graph.FindNodeByHash(0xBC1E195Au);
        AssertThat(buildingsBase).IsNotNull();
        AssertThat(buildingsBase!.Label).IsEqual("Buildings, Real Estate & Shops Base");
        AssertThat(buildingsBase.Category).IsEqual("Buildings & Shops");
    }

    [TestCase]
    public void TestGdbPatchBuilderParseRawValueHandlesSymbolicStringsAndHex()
    {
        uint compHash = 0x11223344u;
        uint soundTagField = Refii.Core.Engine_GameHash.Fnv1("SoundEventTag");
        uint nameTagField = Engine_ItemDetails.NameTag;
        uint expectedSoundHash = Refii.Core.Engine_GameHash.Fnv1("EVENT_WEAPON_KATANA_SWING");

        var entry = GdbPatchBuilder.BuildPromotedEntry(
            null,
            compHash,
            new[]
            {
                (soundTagField, GdbFieldType.String, "EVENT_WEAPON_KATANA_SWING"),
                (nameTagField, GdbFieldType.String, "0xDEADBEEF"),
            });

        var soundProp = entry.Properties.FirstOrDefault(p => p.NameHash == soundTagField);
        var nameProp = entry.Properties.FirstOrDefault(p => p.NameHash == nameTagField);
        AssertThat(soundProp).IsNotNull();
        AssertThat(soundProp!.Value).IsEqual(expectedSoundHash);
        AssertThat(nameProp).IsNotNull();
        AssertThat(nameProp!.Value).IsEqual(0xDEADBEEFu);
    }

    [TestCase]
    public void TestBaseItemRenameDoesNotMarkChildVariantsAsModded()
    {
        uint baseInvCompId = 0xAAA00001u;
        uint variantInvCompId = 0xBBB00002u;
        uint nameTagHash = Refii.Core.Engine_GameHash.Fnv1("INV_ITEM_KATANA_IRON_NAME");
        uint ratingHash = Refii.Core.Engine_GameHash.Fnv1("Rating");

        var baseDb = new GdbPatchDatabase();

        var baseInvComp = new GdbPatchEntry(baseInvCompId, 0x01);
        baseInvComp.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.NameTag, GdbFieldType.String, nameTagHash));
        baseInvComp.Properties.Add(new GdbPatchProperty(ratingHash, GdbFieldType.Int, 2u));
        baseDb.Entries.Add(baseInvComp);

        // Variant InventoryItemComponent inherits NameTag from baseInvCompId and only overrides Rating:
        var variantInvComp = new GdbPatchEntry(variantInvCompId, 0x01);
        variantInvComp.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, baseInvCompId));
        variantInvComp.Properties.Add(new GdbPatchProperty(ratingHash, GdbFieldType.Int, 3u));
        baseDb.Entries.Add(variantInvComp);

        baseDb.Names[Engine_ItemDetails.NameTag] = "NameTag";
        baseDb.Names[nameTagHash] = "INV_ITEM_KATANA_IRON_NAME";
        baseDb.Names[ratingHash] = "Rating";

        var multi = GdbMulti.Build(new[] { GdbWriter.Serialize(baseDb) });
        var babelOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["INV_ITEM_KATANA_IRON_NAME"] = "Renamed Iron Katana"
        };
        var emptyTuMap = new Dictionary<uint, Dictionary<uint, uint>>();

        // 1. On the base item's InventoryItemComponent, NameTag is declared locally and modified in Babel:
        var baseProps = Refii.EditorTools.EditorModValueService.DiscoverComponentProperties(
            baseInvCompId, "InventoryItemComponent", "Inventory Item Component", multi, babelOverrides, emptyTuMap);
        var baseNameProp = baseProps.First(p => p.PropertyName == "NameTag");
        AssertThat(baseNameProp.CurrentValue).IsEqual("Renamed Iron Katana");
        AssertThat(baseNameProp.IsModified).IsTrue();

        // 2. On the child variant's InventoryItemComponent, NameTag is inherited from baseInvCompId,
        //    so it reflects the inherited name but MUST NOT be marked as modified on the variant:
        var variantProps = Refii.EditorTools.EditorModValueService.DiscoverComponentProperties(
            variantInvCompId, "InventoryItemComponent", "Inventory Item Component", multi, babelOverrides, emptyTuMap);
        var variantNameProp = variantProps.First(p => p.PropertyName == "NameTag");
        AssertThat(variantNameProp.CurrentValue).IsEqual("Renamed Iron Katana");
        AssertThat(variantNameProp.IsModified).IsFalse();
    }

    [TestCase]
    public void TestCustomChildNodeComponentEditingBabelTagsAndFullItemEditorInheritance()
    {
        string tempRoot = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, "refii_gdb_test_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempRoot);

        string? prevModsRoot = Refii.EditorTools.EditorModValueService.ModsRootOverride;
        string prevActiveMod = Refii.EditorTools.EditorModExporter.ActiveModName;

        try
        {
            // Construct a synthetic multi-view BaseDatabase where entity/entity.gdb holds our test items:
            uint rootId = Engine_RecordNameCatalog.WorldEntityRootHash;
            uint katanaId = 0x55443322u;
            uint katanaInvCompId = 0x55443323u;
            uint katanaWeaponCompId = 0x55443324u;

            uint invSlotHash = Engine_ItemDetails.InventoryItemComponent;
            uint weaponSlotHash = Engine_ItemDetails.WeaponComponent;
            uint dmgHash = Refii.Core.Engine_GameHash.Fnv1("Damage");
            uint soundEventHash = Refii.Core.Engine_GameHash.Fnv1("SoundEventTag");
            uint soundValHash = Refii.Core.Engine_GameHash.Fnv1("EVENT_KATANA_SWING");
            uint iconValHash = Refii.Core.Engine_GameHash.Fnv1("gui/icons/weapons/katana_iron.tex");
            uint baseNameTagHash = Refii.Core.Engine_GameHash.Fnv1("INV_ITEM_KATANA_IRON_NAME");
            uint baseDescTagHash = Refii.Core.Engine_GameHash.Fnv1("INV_ITEM_KATANA_IRON_DESC");

            var emptyDb = new GdbPatchDatabase();
            byte[] emptyGdbBytes = GdbWriter.Serialize(emptyDb);

            var entityDb = new GdbPatchDatabase();
            entityDb.Entries.Add(new GdbPatchEntry(rootId, 0x01));

            var katanaEntry = new GdbPatchEntry(katanaId, 0x01);
            katanaEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, rootId));
            katanaEntry.Properties.Add(new GdbPatchProperty(invSlotHash, GdbFieldType.Reference, katanaInvCompId));
            katanaEntry.Properties.Add(new GdbPatchProperty(weaponSlotHash, GdbFieldType.Reference, katanaWeaponCompId));
            entityDb.Entries.Add(katanaEntry);

            var invEntry = new GdbPatchEntry(katanaInvCompId, 0x01);
            invEntry.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.NameTag, GdbFieldType.String, baseNameTagHash));
            invEntry.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.DescTag, GdbFieldType.String, baseDescTagHash));
            invEntry.Properties.Add(new GdbPatchProperty(Engine_ItemDetails.IconGraphic, GdbFieldType.String, iconValHash));
            entityDb.Entries.Add(invEntry);

            var weapEntry = new GdbPatchEntry(katanaWeaponCompId, 0x01);
            weapEntry.Properties.Add(new GdbPatchProperty(dmgHash, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(16.0f)));
            weapEntry.Properties.Add(new GdbPatchProperty(soundEventHash, GdbFieldType.String, soundValHash));
            entityDb.Entries.Add(weapEntry);

            entityDb.Names[katanaId] = "ObjectInventoryWeaponMeL_KatanaIron";
            entityDb.Names[katanaInvCompId] = "ObjectInventoryWeaponMeL_KatanaIron_InventoryItemComponent";
            entityDb.Names[katanaWeaponCompId] = "ObjectInventoryWeaponMeL_KatanaIron_WeaponComponent";
            entityDb.Names[invSlotHash] = "InventoryItemComponent";
            entityDb.Names[weaponSlotHash] = "WeaponComponent";
            entityDb.Names[Engine_ItemDetails.NameTag] = "NameTag";
            entityDb.Names[Engine_ItemDetails.DescTag] = "DescriptionTag";
            entityDb.Names[Engine_ItemDetails.IconGraphic] = "IconGraphic";
            entityDb.Names[baseNameTagHash] = "INV_ITEM_KATANA_IRON_NAME";
            entityDb.Names[baseDescTagHash] = "INV_ITEM_KATANA_IRON_DESC";
            entityDb.Names[iconValHash] = "gui/icons/weapons/katana_iron.tex";
            entityDb.Names[dmgHash] = "Damage";
            entityDb.Names[soundEventHash] = "SoundEventTag";
            entityDb.Names[soundValHash] = "EVENT_KATANA_SWING";

            byte[] entityGdbBytes = GdbWriter.Serialize(entityDb);

            Func<string, byte[]?> resolver = vpath =>
            {
                if (vpath.Equals("entity/entity.gdb", StringComparison.OrdinalIgnoreCase))
                {
                    return entityGdbBytes;
                }
                if (vpath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
                {
                    return emptyGdbBytes;
                }
                return null;
            };
            Engine_GameGdb.SetResolver(resolver);
            Engine_GameGdb.SetBaseResolver(resolver);

            Refii.EditorTools.EditorModValueService.ModsRootOverride = tempRoot;
            string modName = "TestHierarchyMod";
            Refii.EditorTools.EditorModExporter.ActiveModName = modName;

            // 1. Create a child node with BOTH recordName and displayName blank (inheriting name from parent):
            bool okUnnamed = Refii.EditorTools.EditorModValueService.CreateChildNode(
                katanaId, "", "", "", modName, out uint autoChildHash, out string autoErr);
            AssertThat(okUnnamed).IsTrue();
            AssertThat(autoErr).IsEmpty();
            AssertThat(autoChildHash).IsNotEqual(0u);

            // 2. Create child node "ObjectInventory_11" with display name "11":
            bool ok11 = Refii.EditorTools.EditorModValueService.CreateChildNode(
                katanaId, "ObjectInventory_11", "11", "", modName, out uint child11Hash, out string err11);
            AssertThat(ok11).IsTrue();
            AssertThat(err11).IsEmpty();

            // 3. Attach a custom Lua component ("MyLuaBehaviorComponent") and add a custom property:
            bool okLua = Refii.EditorTools.EditorModValueService.AttachOrOverrideComponent(
                child11Hash, "MyLuaBehaviorComponent", null, modName, out uint luaCompHash, out string luaErr);
            AssertThat(okLua).IsTrue();
            AssertThat(luaErr).IsEmpty();

            bool okProp = Refii.EditorTools.EditorModValueService.AddOrUpdateComponentProperty(
                luaCompHash, "LuaPowerBonus", GdbFieldType.Float, "2.5", modName, out string propErr);
            AssertThat(okProp).IsTrue();
            AssertThat(propErr).IsEmpty();

            // 4. Override WeaponComponent on ObjectInventory_11 (inheriting from katanaWeaponCompId):
            bool okWeap = Refii.EditorTools.EditorModValueService.AttachOrOverrideComponent(
                child11Hash, "WeaponComponent", katanaWeaponCompId, modName, out uint childWeapCompHash, out string weapErr);
            AssertThat(okWeap).IsTrue();
            AssertThat(weapErr).IsEmpty();

            // 5. Build graph and inspect both autoChildHash (created without a Display Name) and ObjectInventory_11:
            var graph = PolymorphicItemGraph.Build(Engine_GameGdb.BaseDatabase);

            // Verify autoChildHash (unnamed child node) automatically inherits InventoryItemComponent NameTag & DescriptionTag from its parent:
            ItemGraphNode? unnamedChildNode = graph.FindNodeByHash(autoChildHash);
            AssertThat(unnamedChildNode).IsNotNull();
            AssertThat(unnamedChildNode!.DirectComponents.Any(c => c.SlotName == "InventoryItemComponent")).IsFalse();
            var inheritedInvSlot = unnamedChildNode.InheritedComponents.FirstOrDefault(c => c.SlotName == "InventoryItemComponent");
            AssertThat(inheritedInvSlot).IsNotNull();
            AssertThat(unnamedChildNode.Label).IsEqual(unnamedChildNode.Parent!.Label);
            AssertThat(unnamedChildNode.Description).IsEqual("INV_ITEM_KATANA_IRON_DESC");

            ItemGraphNode? node11 = graph.FindNodeByHash(child11Hash);
            AssertThat(node11).IsNotNull();
            AssertThat(node11!.Label).IsEqual("11");

            // Verify MyLuaBehaviorComponent is present in DirectComponents with LuaPowerBonus = 2.5:
            var luaSlot = node11.DirectComponents.FirstOrDefault(c => c.SlotName == "MyLuaBehaviorComponent");
            AssertThat(luaSlot).IsNotNull();
            AssertThat(luaSlot!.Properties.Any(p => p.PropertyName == "LuaPowerBonus" && p.CurrentValue == "2.5")).IsTrue();

            // Verify WeaponComponent on ObjectInventory_11 inherits Damage (16) and SoundEventTag (EVENT_KATANA_SWING):
            var weapSlot = node11.DirectComponents.FirstOrDefault(c => c.SlotName == "WeaponComponent");
            AssertThat(weapSlot).IsNotNull();
            var dmgProp = weapSlot!.Properties.First(p => p.PropertyName == "Damage");
            var soundProp = weapSlot.Properties.First(p => p.PropertyName == "SoundEventTag");
            AssertThat(dmgProp.CurrentValue).IsEqual("16");
            AssertThat(dmgProp.IsModified).IsFalse();
            AssertThat(dmgProp.IsDefinedOnComponent).IsFalse();
            AssertThat(dmgProp.IsWalkedUpFromParent).IsTrue();
            AssertThat(soundProp.CurrentValue).IsEqual("EVENT_KATANA_SWING");
            AssertThat(soundProp.IsModified).IsFalse();
            AssertThat(soundProp.IsDefinedOnComponent).IsFalse();
            AssertThat(soundProp.IsWalkedUpFromParent).IsTrue();

            // 6. Edit ONLY Damage -> "50" and NameTag -> "Dragon Katana", then SaveNodeComponentOverrides:
            dmgProp.CurrentValue = "50";
            var invSlot = node11.DirectComponents.First(c => c.SlotName == "InventoryItemComponent");
            var nameProp = invSlot.Properties.First(p => p.PropertyName == "NameTag");
            nameProp.CurrentValue = "Dragon Katana";

            var allDirectProps = node11.DirectComponents.SelectMany(c => c.Properties).ToList();
            Refii.EditorTools.EditorModValueService.SaveNodeComponentOverrides(child11Hash, allDirectProps, modName, node11.Label);

            // 7. Rebuild graph and verify:
            //    - Label updated to "Dragon Katana"
            //    - Babel CSV has INV_ITEM_OBJECTINVENTORY_11_NAME = "Dragon Katana" and NO dead MOD_NODE_*_NAMETAG key
            //    - SoundEventTag and IconGraphic are intact and NOT marked modified!
            //    - Damage is marked IsDefinedOnComponent = true with ParentComponentValue = "16", while SoundEventTag is IsWalkedUpFromParent = true!
            var babelAfter = Refii.EditorTools.EditorModValueService.LoadActiveModBabelOverrides(modName);
            AssertThat(babelAfter.ContainsKey("INV_ITEM_OBJECTINVENTORY_11_NAME")).IsTrue();
            AssertThat(babelAfter["INV_ITEM_OBJECTINVENTORY_11_NAME"]).IsEqual("Dragon Katana");
            AssertThat(babelAfter.Keys.Any(k => k.StartsWith("MOD_NODE_", StringComparison.OrdinalIgnoreCase))).IsFalse();

            var graphAfter = PolymorphicItemGraph.Build(Engine_GameGdb.BaseDatabase);
            ItemGraphNode? node11After = graphAfter.FindNodeByHash(child11Hash);
            AssertThat(node11After).IsNotNull();
            AssertThat(node11After!.Label).IsEqual("Dragon Katana");

            var weapSlotAfter = node11After.DirectComponents.First(c => c.SlotName == "WeaponComponent");
            var dmgAfter = weapSlotAfter.Properties.First(p => p.PropertyName == "Damage");
            var soundAfter = weapSlotAfter.Properties.First(p => p.PropertyName == "SoundEventTag");
            AssertThat(dmgAfter.CurrentValue).IsEqual("50");
            AssertThat(dmgAfter.IsModified).IsTrue();
            AssertThat(dmgAfter.IsDefinedOnComponent).IsTrue();
            AssertThat(dmgAfter.IsWalkedUpFromParent).IsFalse();
            AssertThat(dmgAfter.ParentComponentValue).IsEqual("16");
            AssertThat(soundAfter.CurrentValue).IsEqual("EVENT_KATANA_SWING");
            AssertThat(soundAfter.IsModified).IsFalse();
            AssertThat(soundAfter.IsDefinedOnComponent).IsFalse();
            AssertThat(soundAfter.IsWalkedUpFromParent).IsTrue();

            var invSlotAfter = node11After.DirectComponents.First(c => c.SlotName == "InventoryItemComponent");
            var iconAfter = invSlotAfter.Properties.First(p => p.PropertyName == "IconGraphic");
            AssertThat(iconAfter.CurrentValue).IsEqual("gui/icons/weapons/katana_iron.tex");
            AssertThat(iconAfter.IsModified).IsFalse();
            AssertThat(iconAfter.IsDefinedOnComponent).IsFalse();
            AssertThat(iconAfter.IsWalkedUpFromParent).IsTrue();

            // 8. Verify "Open Full Item Editor" (GetEffectiveValues) on the custom child node discovers all inherited properties:
            AssertThat(node11After.Detail).IsNotNull();
            var fullEditorValues = Refii.EditorTools.EditorModValueService.GetEffectiveValues(node11After.Detail!, modName);
            AssertThat(fullEditorValues.DisplayName).IsEqual("Dragon Katana");
            AssertThat(fullEditorValues.Properties.Any(p => p.PropertyName == "Damage" && p.CurrentValue == "50" && p.IsModified)).IsTrue();

            // 9. Verify renaming the unnamed child node via Full Item Editor (SaveItemOverrides) promotes a dedicated child InventoryItemComponent
            //    without overwriting the parent's INV_ITEM_KATANA_IRON_NAME tag:
            ItemGraphNode? unnamedAfter = graphAfter.FindNodeByHash(autoChildHash);
            AssertThat(unnamedAfter).IsNotNull();
            AssertThat(unnamedAfter!.Detail).IsNotNull();
            var unnamedValues = Refii.EditorTools.EditorModValueService.GetEffectiveValues(unnamedAfter.Detail!, modName);
            unnamedValues.DisplayName = "Shadow Katana";
            Refii.EditorTools.EditorModValueService.SaveItemOverrides(unnamedAfter.Detail!, unnamedValues, modName);

            var babelAfterRename = Refii.EditorTools.EditorModValueService.LoadActiveModBabelOverrides(modName);
            AssertThat(babelAfterRename.ContainsKey("INV_ITEM_KATANA_IRON_NAME")).IsFalse();
            var graphAfterRename = PolymorphicItemGraph.Build(Engine_GameGdb.BaseDatabase);
            ItemGraphNode? renamedNode = graphAfterRename.FindNodeByHash(autoChildHash);
            AssertThat(renamedNode).IsNotNull();
            AssertThat(renamedNode!.Label).IsEqual("Shadow Katana");
            AssertThat(renamedNode.DirectComponents.Any(c => c.SlotName == "InventoryItemComponent")).IsTrue();
        }
        finally
        {
            Refii.EditorTools.EditorModValueService.ModsRootOverride = prevModsRoot;
            Refii.EditorTools.EditorModExporter.ActiveModName = prevActiveMod;
            Engine_GameGdb.SetResolver(null);
            Engine_GameGdb.SetBaseResolver(null);
            try { System.IO.Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static byte[] BuildSyntheticMdl(
        string meshName,
        string diffuseTex,
        string specularTex,
        string normalTex,
        int lodCount = 2)
    {
        static void WriteU32BE(System.IO.Stream s, uint val)
        {
            s.WriteByte((byte)(val >> 24));
            s.WriteByte((byte)(val >> 16));
            s.WriteByte((byte)(val >> 8));
            s.WriteByte((byte)val);
        }

        static void WriteStrz(System.IO.Stream s, string text)
        {
            byte[] ascii = System.Text.Encoding.ASCII.GetBytes(text ?? string.Empty);
            s.Write(ascii, 0, ascii.Length);
            s.WriteByte(0);
        }

        byte[] BuildLodPayload()
        {
            using var ms = new System.IO.MemoryStream();
            ms.Write(new byte[32]); // 32-byte LOD prologue
            WriteU32BE(ms, 0u); // bc = 0
            WriteU32BE(ms, 0u); // btc = 0
            ms.Write(new byte[40]); // 40-byte counts header
            WriteU32BE(ms, 1u); // mc = 1
            WriteU32BE(ms, 1u); // rigid = 1
            WriteU32BE(ms, 0u); // skinned = 0
            WriteU32BE(ms, 0u); // ex52 = 0
            WriteU32BE(ms, 0u); // ex240 = 0
            ms.WriteByte(0);    // section flag = 0
            WriteU32BE(ms, 0u); // tagc = 0
            for (int i = 0; i < 5; i++) WriteU32BE(ms, 0u); // shdesc (sh4 = 0)
            WriteU32BE(ms, 0u); // fc = 0
            WriteU32BE(ms, 0u); // hrc = 0

            // Mesh header 0:
            WriteU32BE(ms, 0u); // sel = 0
            WriteStrz(ms, meshName);
            ms.Write(new byte[45]); // 45-byte scalar block
            WriteU32BE(ms, 1u); // matc = 1

            // Material 0:
            WriteStrz(ms, diffuseTex);  // slot 0: Diffuse
            WriteStrz(ms, specularTex); // slot 1: Specular
            WriteStrz(ms, normalTex);   // slot 2: Normal
            WriteStrz(ms, "");          // slot 3: EnvironmentMap
            WriteStrz(ms, "");          // slot 4: SelfIllumination
            for (int k = 0; k < 4; k++) WriteStrz(ms, ""); // 4 shader params
            ms.Write(new byte[8]); // 2 material floats

            // Rigid record 0:
            WriteStrz(ms, meshName);
            ms.WriteByte(0);
            WriteU32BE(ms, 0u); // partIndex
            WriteU32BE(ms, 0u); // idx1 (meshIdx)
            WriteU32BE(ms, 0u);
            WriteU32BE(ms, 3u); // idxCount = 3
            WriteU32BE(ms, 3u); // vtxCount = 3
            ms.Write(new byte[40]); // bounds + sphere
            WriteU32BE(ms, 1u); // subc = 1

            // Submesh 0:
            WriteU32BE(ms, 0xFFFFFFFFu); // marker
            WriteU32BE(ms, 0u); // matIdx = 0
            ms.WriteByte(0);    // primType = 0
            WriteU32BE(ms, 1u); // faceCount = 1
            WriteU32BE(ms, 0u); // startIdx = 0
            ms.Write(new byte[24]); // 6 floats bounding box

            ms.Write(new byte[3 * 20]); // 3 rigid vertices (20 bytes each)
            ms.Write(new byte[3 * 2]);  // 3 indices (2 bytes each)
            WriteU32BE(ms, 0u); // ext1 = 0

            return ms.ToArray();
        }

        byte[] lodPayload = BuildLodPayload();
        using var fileStream = new System.IO.MemoryStream();

        byte[] header = new byte[104];
        byte[] magic = System.Text.Encoding.ASCII.GetBytes("MeshFile");
        Array.Copy(magic, 0, header, 0, 8);
        // version = 36 at +8, hdrSize = 88 at +12:
        header[11] = 36;
        header[15] = 88;
        // lodCount at 0x38:
        header[0x3B] = (byte)lodCount;
        for (int i = 0; i < lodCount; i++)
        {
            uint sz = (uint)lodPayload.Length;
            int off = 0x3C + (i * 4);
            header[off] = (byte)(sz >> 24);
            header[off + 1] = (byte)(sz >> 16);
            header[off + 2] = (byte)(sz >> 8);
            header[off + 3] = (byte)sz;
        }

        fileStream.Write(header, 0, header.Length);
        for (int i = 0; i < lodCount; i++)
        {
            fileStream.Write(lodPayload, 0, lodPayload.Length);
        }

        return fileStream.ToArray();
    }

    [TestCase]
    public void TestToyGunModelHashResolutionAndAssetPathHashEncoding()
    {
        // Toy Gun record (0x15FE8FE2) has GraphicAppearanceStaticMeshComponent (0x6090FB1C)
        // whose ModelFile (0xA0B3B05C) stores the PathHash of its .mdl virtual path.
        const string toyGunMdlPath = @"art\weapons\ranged\objectweaponrifletoy.mdl";
        uint expectedPathHash = Refii.Level.Engine_PropModelIndex.PathHash(toyGunMdlPath);

        // Register the path in Engine_PropModelIndex and verify PathHash resolution:
        Refii.Level.Engine_PropModelIndex.RegisterPath("art/weapons/ranged/ObjectWeaponRifleToy.mdl");
        string? resolvedToyGunPath = Refii.Level.Engine_PropModelIndex.Resolve(expectedPathHash, Array.Empty<string>());
        AssertThat(resolvedToyGunPath).IsNotNull();
        AssertThat(resolvedToyGunPath!.Replace('\\', '/').ToLowerInvariant())
            .IsEqual("art/weapons/ranged/objectweaponrifletoy.mdl");

        // Verify GdbPatchBuilder.HashStringOrAssetPath uses PathHash (lowercase + '/' -> '\') for virtual asset paths:
        string modModelForwardSlash = "mods/my_mod/models/toy_gun/modelfile.mdl";
        uint encodedHash = GdbPatchBuilder.HashStringOrAssetPath(modModelForwardSlash);
        uint expectedModPathHash = Refii.Level.Engine_PropModelIndex.PathHash(@"mods\my_mod\models\toy_gun\modelfile.mdl");
        AssertThat(encodedHash).IsEqual(expectedModPathHash);

        // Verify non-asset strings still use case-preserving Engine_GameHash.Fnv1:
        const string tagString = "INV_ITEM_TOY_GUN_NAME";
        AssertThat(GdbPatchBuilder.HashStringOrAssetPath(tagString))
            .IsEqual(Refii.Core.Engine_GameHash.Fnv1(tagString));
    }

    [TestCase]
    public void TestMdlMaterialTextureExtractionAndMultiLodRewriting()
    {
        byte[] baseMdl = BuildSyntheticMdl(
            "ToyGunMesh",
            @"art\weapons\ranged\toy_gun_d.tex",
            @"art\weapons\ranged\toy_gun_s.tex",
            @"art\weapons\ranged\toy_gun_n.tex",
            lodCount: 2);

        var extracted = Refii.Models.Engine_MdlWriter.ExtractMaterials(baseMdl);
        AssertThat(extracted.Count).IsEqual(1);
        AssertThat(extracted[0].MeshName).IsEqual("ToyGunMesh");
        AssertThat(extracted[0].Material.Diffuse).IsEqual(@"art\weapons\ranged\toy_gun_d.tex");
        AssertThat(extracted[0].Material.Specular).IsEqual(@"art\weapons\ranged\toy_gun_s.tex");
        AssertThat(extracted[0].Material.Normal).IsEqual(@"art\weapons\ranged\toy_gun_n.tex");

        string newDiffuse = @"mods\my_mod\textures\toy_gun\mat0_diffuse_custom_long_name.tex";
        string newNormal = @"mods\my_mod\textures\toy_gun\n.tex";
        var patches = new Dictionary<int, Refii.Models.MdlMaterialTexturePatch>
        {
            [0] = new Refii.Models.MdlMaterialTexturePatch
            {
                Diffuse = newDiffuse,
                Normal = newNormal,
            }
        };

        bool ok = Refii.Models.Engine_MdlWriter.TryRewriteMaterialTextures(
            baseMdl, patches, out byte[] rewrittenMdl, out string err);
        AssertThat(ok).IsTrue();
        AssertThat(err).IsEmpty();

        // Parse rewritten .mdl and verify both LOD 0 and LOD 1 materials were updated and header sizes match:
        var parsedInfo = new Refii.Models.MdlInfo();
        AssertThat(Refii.Models.Engine_MdlEngine.TryParse(rewrittenMdl, parsedInfo)).IsTrue();
        AssertThat(parsedInfo.Meshes.Count).IsEqual(1);
        AssertThat(parsedInfo.Meshes[0].Materials[0].Diffuse).IsEqual(newDiffuse);
        AssertThat(parsedInfo.Meshes[0].Materials[0].Normal).IsEqual(newNormal);
        AssertThat(parsedInfo.Meshes[0].Materials[0].Specular).IsEqual(@"art\weapons\ranged\toy_gun_s.tex");

        AssertThat(parsedInfo.Lods.Count).IsEqual(1);
        AssertThat(parsedInfo.Lods[0].Meshes[0].Materials[0].Diffuse).IsEqual(newDiffuse);
        AssertThat(parsedInfo.Lods[0].Meshes[0].Materials[0].Normal).IsEqual(newNormal);
    }

    [TestCase]
    public void TestEndToEndAssetLinkingSurfaceTexturePatchingAndOrphanPruning()
    {
        string tempRoot = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, "refii_asset_link_test_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempRoot);

        string? prevModsRoot = Refii.EditorTools.EditorModValueService.ModsRootOverride;
        string prevActiveMod = Refii.EditorTools.EditorModExporter.ActiveModName;

        try
        {
            // 1. Verify AssetDropLineEdit.CanAcceptDroppedPath validation rules:
            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Model, "res://creator/MyMod/toy_gun/rifle.glb")).IsTrue();
            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Model, "res://creator/MyMod/toy_gun/rifle.mdl")).IsTrue();
            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Model, "res://creator/MyMod/toy_gun/albedo.png")).IsFalse();

            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Texture, "res://creator/MyMod/toy_gun/albedo.png")).IsTrue();
            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Texture, "res://creator/MyMod/toy_gun/albedo.dds")).IsTrue();
            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Texture, "res://creator/MyMod/toy_gun/albedo.tex")).IsTrue();
            AssertThat(PolymorphicItemGraphView.AssetDropLineEdit.CanAcceptDroppedPath(
                Refii.EditorTools.AssetFieldKind.Texture, "res://creator/MyMod/toy_gun/rifle.glb")).IsFalse();

            // 2. Build a synthetic GDB with Toy Gun (0x15FE8FE2) and GraphicAppearanceStaticMeshComponent (0x6090FB1C):
            uint rootId = Engine_RecordNameCatalog.WorldEntityRootHash;
            uint toyGunId = 0x15FE8FE2u;
            uint staticMeshCompId = 0x6090FB1Cu;
            uint staticMeshSlotHash = Refii.Core.Engine_GameHash.Fnv1("GraphicAppearanceStaticMeshComponent");
            uint modelFileFieldHash = Refii.Core.Engine_GameHash.Fnv1("ModelFile");

            string vanillaMdlPath = @"art\weapons\ranged\toy_gun_vanilla.mdl";
            uint vanillaMdlHash = Refii.Level.Engine_PropModelIndex.PathHash(vanillaMdlPath);
            Refii.Level.Engine_PropModelIndex.RegisterPath(vanillaMdlPath);

            byte[] vanillaMdlBytes = BuildSyntheticMdl(
                "ToyGunVanilla",
                @"art\weapons\ranged\toy_gun_d.tex",
                @"art\weapons\ranged\toy_gun_s.tex",
                @"art\weapons\ranged\toy_gun_n.tex",
                lodCount: 1);

            byte[] vanillaGmdBytes = new byte[] { 0x47, 0x4D, 0x44, 0x30, 1, 2, 3, 4, 5, 6, 7, 8 };
            var fs = new Refii.Bnk.BnkFileSystem();
            fs.AddVirtualFile(
                vanillaMdlPath.Replace('\\', '/'),
                "base",
                vanillaMdlBytes.Length,
                () => vanillaMdlBytes);
            fs.AddVirtualFile(
                vanillaMdlPath.Replace('\\', '/') + ".gmd",
                "base",
                vanillaGmdBytes.Length,
                () => vanillaGmdBytes);

            var emptyDb = new GdbPatchDatabase();
            byte[] emptyGdbBytes = GdbWriter.Serialize(emptyDb);

            var entityDb = new GdbPatchDatabase();
            entityDb.Entries.Add(new GdbPatchEntry(rootId, 0x01));

            var toyGunEntry = new GdbPatchEntry(toyGunId, 0x01);
            toyGunEntry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, rootId));
            toyGunEntry.Properties.Add(new GdbPatchProperty(staticMeshSlotHash, GdbFieldType.Reference, staticMeshCompId));
            entityDb.Entries.Add(toyGunEntry);

            var compEntry = new GdbPatchEntry(staticMeshCompId, 0x01);
            compEntry.Properties.Add(new GdbPatchProperty(modelFileFieldHash, GdbFieldType.String, vanillaMdlHash));
            entityDb.Entries.Add(compEntry);

            entityDb.Names[toyGunId] = "ObjectInventoryWeaponRifle_ToyGun";
            entityDb.Names[staticMeshCompId] = "ToyGun_StaticMeshComp";
            entityDb.Names[staticMeshSlotHash] = "GraphicAppearanceStaticMeshComponent";
            entityDb.Names[modelFileFieldHash] = "ModelFile";
            byte[] entityGdbBytes = GdbWriter.Serialize(entityDb);

            Func<string, byte[]?> resolver = vpath =>
            {
                if (vpath.Equals("entity/entity.gdb", StringComparison.OrdinalIgnoreCase))
                    return entityGdbBytes;
                if (vpath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
                    return emptyGdbBytes;
                return null;
            };
            Engine_GameGdb.SetResolver(resolver);
            Engine_GameGdb.SetBaseResolver(resolver);

            Refii.EditorTools.EditorModValueService.ModsRootOverride = tempRoot;
            string modName = "ToyGunAssetMod";
            Refii.EditorTools.EditorModExporter.ActiveModName = modName;

            // 3. Build graph and verify 0x6090FB1C's ModelFile resolves to vanillaMdlPath and is classified as AssetFieldKind.Model:
            var graph = PolymorphicItemGraph.Build(Engine_GameGdb.BaseDatabase);
            ItemGraphNode? toyGunNode = graph.FindNodeByHash(toyGunId);
            AssertThat(toyGunNode).IsNotNull();

            var meshComp = toyGunNode!.DirectComponents.FirstOrDefault(c => c.SlotName == "GraphicAppearanceStaticMeshComponent");
            AssertThat(meshComp).IsNotNull();
            var modelProp = meshComp!.Properties.FirstOrDefault(p => p.PropertyName == "ModelFile");
            AssertThat(modelProp).IsNotNull();
            AssertThat(modelProp!.AssetKind).IsEqual(Refii.EditorTools.AssetFieldKind.Model);
            AssertThat(modelProp.CurrentValue).IsEqual(vanillaMdlPath);

            // Verify ResolveNodeEffectiveModelPath finds vanillaMdlPath for ToyGun:
            string? resolvedEffectiveModel = Refii.EditorTools.EditorModValueService.ResolveNodeEffectiveModelPath(toyGunId);
            AssertThat(resolvedEffectiveModel).IsEqual(vanillaMdlPath);

            // 4. Link a custom .mdl file onto ModelFile via LinkAssetToComponentProperty:
            string customMdlDiskPath = System.IO.Path.Combine(tempRoot, "custom_mesh.mdl");
            byte[] customMdlBytes = BuildSyntheticMdl(
                "CustomToyGunMesh",
                @"art\weapons\ranged\toy_gun_d.tex",
                @"art\weapons\ranged\toy_gun_s.tex",
                @"art\weapons\ranged\toy_gun_n.tex",
                lodCount: 1);
            System.IO.File.WriteAllBytes(customMdlDiskPath, customMdlBytes);

            bool linkedModelOk = Refii.EditorTools.EditorModValueService.LinkAssetToComponentProperty(
                toyGunId,
                toyGunNode.Label,
                modelProp,
                meshComp.Properties,
                customMdlDiskPath,
                modName,
                out string linkedModelVPath,
                out string linkModelErr,
                fs);
            AssertThat(linkedModelOk).IsTrue();
            AssertThat(linkModelErr).IsEmpty();
            AssertThat(linkedModelVPath).StartsWith(@"mods\toygunassetmod\models\");
            AssertThat(linkedModelVPath).EndsWith(@"\modelfile.mdl");

            // Verify the companion .mdl.gmd sidecar was registered in AssetHashIndex for guest grip dummy resolution:
            uint gmdGuestHash = Refii.Core.Engine_GameHash.Fnv1(linkedModelVPath + ".gmd");
            AssertThat(Refii.Bnk.AssetHashIndex.PathForHash(gmdGuestHash))
                .IsEqual(linkedModelVPath.Replace('\\', '/') + ".gmd");

            // 5. Link a replacement surface texture (.png + companion _orm.png) onto slot 0 (Diffuse) via LinkSurfaceTextureToModelSlot:
            string customAlbedoDiskPath = System.IO.Path.Combine(tempRoot, "toy_gun_albedo.png");
            string customOrmDiskPath = System.IO.Path.Combine(tempRoot, "toy_gun_albedo_orm.png");
            System.IO.File.WriteAllBytes(customAlbedoDiskPath, new byte[] { 1, 2, 3, 4 });
            System.IO.File.WriteAllBytes(customOrmDiskPath, new byte[] { 9, 8, 7, 6 });

            bool linkedTexOk = Refii.EditorTools.EditorModValueService.LinkSurfaceTextureToModelSlot(
                toyGunId,
                toyGunNode.Label,
                modelProp,
                meshComp.Properties,
                flatMaterialIndex: 0,
                slotIndex: 0,
                slotName: "Diffuse",
                sourceFilePath: customAlbedoDiskPath,
                activeMod: modName,
                out string linkedTexVPath,
                out string patchedModelVPath,
                out string linkTexErr,
                filesystem: fs);
            AssertThat(linkedTexOk).IsTrue();
            AssertThat(linkTexErr).IsEmpty();
            AssertThat(linkedTexVPath).StartsWith(@"mods\toygunassetmod\textures\");
            AssertThat(linkedTexVPath).EndsWith(@"mat0_diffuse.tex");
            AssertThat(patchedModelVPath).IsEqual(linkedModelVPath);

            // Verify GetModelMaterialSlots reflects the modified Diffuse slot and preserves VanillaTexturePath:
            var slotsAfter = Refii.EditorTools.EditorModValueService.GetModelMaterialSlots(
                patchedModelVPath, vanillaMdlPath, modName, fs);
            AssertThat(slotsAfter.Count).IsGreaterEqual(3);
            var diffuseSlot = slotsAfter.First(s => s.FlatMaterialIndex == 0 && s.SlotName == "Diffuse");
            AssertThat(diffuseSlot.CurrentTexturePath).IsEqual(linkedTexVPath);
            AssertThat(diffuseSlot.VanillaTexturePath).IsEqual(@"art\weapons\ranged\toy_gun_d.tex");
            AssertThat(diffuseSlot.IsModified).IsTrue();

            // Verify the 3 mods/... entries (.mdl, .mdl.gmd, .tex) exist in the .f2mod package while referenced:
            string pkgPath = System.IO.Path.Combine(tempRoot, modName + ".f2mod");
            int prunedWhileLive = Refii.EditorTools.EditorModValueService.PruneUnreferencedModAssets(modName);
            AssertThat(prunedWhileLive).IsEqual(0);
            AssertThat(Refii.Mods.F2ModPackage.TryOpen(pkgPath, out var pkgBeforeRevert, out _)).IsTrue();
            using (pkgBeforeRevert)
            {
                int modAssetCountBefore = pkgBeforeRevert!.Entries.Count(
                    e => e.Path.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
                AssertThat(modAssetCountBefore).IsEqual(3);
                AssertThat(pkgBeforeRevert.Entries.Any(
                    e => e.Path.Replace('\\', '/').EndsWith(".mdl.gmd", StringComparison.OrdinalIgnoreCase))).IsTrue();
            }

            // 6. Revert the ModelFile property back to vanilla and save, then verify SaveNodeComponentOverrides automatically pruned the 4 orphaned mods/... files:
            modelProp.CurrentValue = modelProp.VanillaValue;
            modelProp.IsModified = false;
            Refii.EditorTools.EditorModValueService.SaveNodeComponentOverrides(
                toyGunId, meshComp.Properties, modName, toyGunNode.Label);

            AssertThat(Refii.Mods.F2ModPackage.TryOpen(pkgPath, out var pkgAfterRevert, out _)).IsTrue();
            using (pkgAfterRevert)
            {
                int modAssetCountAfter = pkgAfterRevert!.Entries.Count(
                    e => e.Path.Replace('\\', '/').StartsWith("mods/", StringComparison.OrdinalIgnoreCase));
                AssertThat(modAssetCountAfter).IsEqual(0);
                AssertThat(Refii.Mods.F2ModPackage.CalculateZombieBytes(pkgPath)).IsEqual(0UL);
            }
        }
        finally
        {
            Refii.EditorTools.EditorModValueService.ModsRootOverride = prevModsRoot;
            Refii.EditorTools.EditorModExporter.ActiveModName = prevActiveMod;
            Engine_GameGdb.SetResolver(null);
            Engine_GameGdb.SetBaseResolver(null);
            try { System.IO.Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [TestCase]
    public void TestCreatorWorkspaceResPathAndScannerIntegration()
    {
        string testModName = "CreatorResTestMod_" + Guid.NewGuid().ToString("N")[..8];
        string tempModsDir = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, "refii_creator_res_test_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempModsDir);

        string creatorDir = Refii.EditorTools.EditorModExporter.GetCreatorDirectory(testModName);
        string creatorResPath = Refii.EditorTools.EditorModExporter.GetCreatorResPath(testModName);

        try
        {
            AssertThat(creatorResPath).IsEqual($"res://creator/{testModName}/");
            AssertThat(creatorDir).IsEqual(System.IO.Path.Combine(Refii.Core.Engine_AppRoot.SafeGetProjectDir(), "creator", testModName));

            System.IO.Directory.CreateDirectory(creatorDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(creatorDir, "toy_gun.glb"), "dummy glb");
            System.IO.File.WriteAllText(System.IO.Path.Combine(creatorDir, "toy_gun_d.png"), "dummy png");

            var summary = Refii.Mods.ModContentScanner.ScanMod(tempModsDir, testModName);
            AssertThat(summary.CreatorAssetCount).IsEqual(2);
            AssertThat(summary.Entries.Any(e => e.Kind == Refii.Mods.ModAuthoredKind.CreatorAsset && e.Label == "toy_gun.glb")).IsTrue();
            AssertThat(summary.Entries.Any(e => e.Kind == Refii.Mods.ModAuthoredKind.CreatorAsset && e.Label == "toy_gun_d.png")).IsTrue();
        }
        finally
        {
            try { if (System.IO.Directory.Exists(creatorDir)) System.IO.Directory.Delete(creatorDir, true); } catch { }
            try { if (System.IO.Directory.Exists(tempModsDir)) System.IO.Directory.Delete(tempModsDir, true); } catch { }
        }
    }

    [TestCase]
    public void TestGlbEmbeddedTextureExtractionUpdatesMdlMaterialTable()
    {
        byte[] baseMdl = BuildSyntheticMdl(
            "ToyGunMesh",
            @"art\weapons\ranged\toy_gun_d.tex",
            @"art\weapons\ranged\toy_gun_s.tex",
            @"art\weapons\ranged\toy_gun_n.tex",
            lodCount: 2);

        byte[] dummyAlbedoPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        byte[] dummyNormalPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 6, 7, 8 };

        string gltfJson =
            "{" +
            "\"asset\":{\"version\":\"2.0\"}," +
            "\"bufferViews\":[" +
            "{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + dummyAlbedoPng.Length + "}," +
            "{\"buffer\":0,\"byteOffset\":" + dummyAlbedoPng.Length + ",\"byteLength\":" + dummyNormalPng.Length + "}" +
            "]," +
            "\"images\":[" +
            "{\"bufferView\":0,\"mimeType\":\"image/png\"}," +
            "{\"bufferView\":1,\"mimeType\":\"image/png\"}" +
            "]," +
            "\"textures\":[" +
            "{\"source\":0}," +
            "{\"source\":1}" +
            "]," +
            "\"materials\":[" +
            "{" +
            "\"name\":\"ToyGun_Mat_0\"," +
            "\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}," +
            "\"normalTexture\":{\"index\":1}" +
            "}" +
            "]," +
            "\"meshes\":[{\"primitives\":[{\"material\":0}]}]" +
            "}";

        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(gltfJson);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int jsonChunkLen = jsonBytes.Length + jsonPad;

        int binLen = dummyAlbedoPng.Length + dummyNormalPng.Length;
        int binPad = (4 - (binLen % 4)) % 4;
        int binChunkLen = binLen + binPad;

        int totalLen = 12 + 8 + jsonChunkLen + 8 + binChunkLen;
        byte[] glbBytes = new byte[totalLen];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(0, 4), 0x46546C67u); // "glTF"
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(4, 4), 2u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(8, 4), (uint)totalLen);

        // Chunk 0: JSON
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(12, 4), (uint)jsonChunkLen);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(16, 4), 0x4E4F534Au); // "JSON"
        Array.Copy(jsonBytes, 0, glbBytes, 20, jsonBytes.Length);
        for (int i = 0; i < jsonPad; i++)
        {
            glbBytes[20 + jsonBytes.Length + i] = (byte)' ';
        }

        // Chunk 1: BIN
        int binChunkPos = 20 + jsonChunkLen;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos, 4), (uint)binChunkLen);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos + 4, 4), 0x004E4942u); // "BIN\0"
        Array.Copy(dummyAlbedoPng, 0, glbBytes, binChunkPos + 8, dummyAlbedoPng.Length);
        Array.Copy(dummyNormalPng, 0, glbBytes, binChunkPos + 8 + dummyAlbedoPng.Length, dummyNormalPng.Length);

        var packageAdditions = new List<(string Path, byte[] Bytes)>();
        bool extractedOk = Refii.EditorTools.EditorModValueService.TryExtractAndApplyGltfEmbeddedTextures(
            glbBytes,
            baseDir: null,
            compiledMdl: baseMdl,
            modSlug: "toygunassetmod",
            nodeSlug: "toy_gun",
            packageAdditions: packageAdditions,
            out byte[] patchedMdl);

        AssertThat(extractedOk).IsTrue();
        AssertThat(packageAdditions.Count).IsEqual(2);
        AssertThat(packageAdditions.Any(p => p.Path == "mods/toygunassetmod/textures/toy_gun/mat0_diffuse.tex")).IsTrue();
        AssertThat(packageAdditions.Any(p => p.Path == "mods/toygunassetmod/textures/toy_gun/mat0_normal.tex")).IsTrue();

        var materialsAfter = Refii.Models.Engine_MdlWriter.ExtractMaterials(patchedMdl);
        AssertThat(materialsAfter.Count).IsEqual(1);
        AssertThat(materialsAfter[0].Material.Diffuse).IsEqual(@"mods\toygunassetmod\textures\toy_gun\mat0_diffuse.tex");
        AssertThat(materialsAfter[0].Material.Normal).IsEqual(@"mods\toygunassetmod\textures\toy_gun\mat0_normal.tex");
        AssertThat(materialsAfter[0].Material.Specular).IsEqual(@"art\weapons\ranged\toy_gun_s.tex");
    }

    [TestCase]
    public void TestGmdBinaryRoundTripAndGlbDummyExtraction()
    {
        var initialAttachments = new List<Refii.Models.GmdAttachment>
        {
            new(
                "Weapon.Weapon.Hold",
                new Transform3D(Basis.Identity, new Vector3(0.01f, -0.04f, -0.02f)),
                -1),
            new(
                "Weapon.Weapon.ProjectileEmitter",
                new Transform3D(Basis.Identity, new Vector3(0.0f, 0.38f, 0.06f)),
                -1),
            new(
                "Weapon.Weapon.Sheath",
                new Transform3D(Basis.Identity, new Vector3(0.0f, 0.14f, 0.03f)),
                -1),
        };

        byte[] gmdBytes = Refii.Models.Engine_GmdWriter.Write(null, initialAttachments);
        AssertThat(gmdBytes.Length).IsGreater(48);

        var parsed = Refii.Models.Engine_GmdSkeleton.TryParse(gmdBytes);
        AssertThat(parsed).IsNotNull();
        AssertThat(parsed!.Attachments.Length).IsEqual(3);
        AssertThat(parsed.Attachments[0].Name).IsEqual("Weapon.Weapon.Hold");
        AssertThat(parsed.Attachments[1].Name).IsEqual("Weapon.Weapon.ProjectileEmitter");
        AssertThat(parsed.Attachments[2].Name).IsEqual("Weapon.Weapon.Sheath");
        AssertThat(parsed.Attachments[0].Node).IsEqual(-1);
        AssertThat(Mathf.IsEqualApprox(parsed.Attachments[1].Local.Origin.Y, 0.38f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(parsed.Attachments[1].Local.Origin.Z, 0.06f, 1e-4f)).IsTrue();

        // Simulate a Blender-exported .glb containing GmdDummies where:
        // - Weapon_Weapon_Hold.001 was moved in glTF (+Y Up) space to (0.02, 0.05, -0.10)
        // - Weapon.Weapon.ProjectileEmitter was moved via extras.gmd_dummy_name in glTF space to (0.0, 0.12, -0.55)
        // - Weapon.FX.Particle.Effect_1 (without trailing dot) is resolved to canonical "Weapon.FX.Particle.Effect_1."
        // - Weapon.Weapon.Sheath was omitted in Blender, so it should be preserved from templateGmd!
        string gltfWithDummiesJson =
            "{" +
            "\"asset\":{\"version\":\"2.0\"}," +
            "\"scenes\":[{\"nodes\":[0]}]," +
            "\"nodes\":[" +
            "{\"name\":\"GmdDummies\",\"children\":[1,2,3]}," +
            "{\"name\":\"Weapon_Weapon_Hold.001\",\"translation\":[0.02,0.05,-0.10]}," +
            "{\"name\":\"MuzzleEmpty\",\"translation\":[0.0,0.12,-0.55],\"extras\":{\"gmd_dummy_name\":\"Weapon.Weapon.ProjectileEmitter\",\"gmd_parent_node\":0}}," +
            "{\"name\":\"Weapon.FX.Particle.Effect_1\",\"translation\":[-0.11,0.0,-0.57]}" +
            "]" +
            "}";

        byte[] gltfJsonBytes = System.Text.Encoding.UTF8.GetBytes(gltfWithDummiesJson);
        bool extracted = Refii.Models.Engine_GmdWriter.TryExtractGmdFromGltf(
            gltfJsonBytes,
            parsed,
            out byte[] rebuiltGmdBytes,
            out List<Refii.Models.GmdAttachment> mergedAttachments);

        AssertThat(extracted).IsTrue();
        AssertThat(rebuiltGmdBytes.Length).IsGreater(48);
        AssertThat(mergedAttachments.Count).IsEqual(4);

        var reparsed = Refii.Models.Engine_GmdSkeleton.TryParse(rebuiltGmdBytes);
        AssertThat(reparsed).IsNotNull();
        AssertThat(reparsed!.Attachments.Length).IsEqual(4);

        // Verify glTF (+Y Up) -> GameMesh (Z-Up) -> TryParse (Godot +Y Up) round-trips the exact glTF coordinates:
        var holdAtt = reparsed.Attachments.First(a => a.Name == "Weapon.Weapon.Hold");
        AssertThat(holdAtt.Node).IsEqual(-1);
        AssertThat(Mathf.IsEqualApprox(holdAtt.Local.Origin.X, 0.02f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(holdAtt.Local.Origin.Y, 0.05f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(holdAtt.Local.Origin.Z, -0.10f, 1e-4f)).IsTrue();

        // Also verify ToGameSpace produces the expected Z-Up coordinates (X, -Z, Y):
        Transform3D holdGame = Refii.Models.Engine_GmdSkeleton.ToGameSpace(holdAtt.Local);
        AssertThat(Mathf.IsEqualApprox(holdGame.Origin.X, 0.02f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(holdGame.Origin.Y, 0.10f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(holdGame.Origin.Z, 0.05f, 1e-4f)).IsTrue();

        var muzzleAtt = reparsed.Attachments.First(a => a.Name == "Weapon.Weapon.ProjectileEmitter");
        // Since templateGmd has 0 skeleton bones, gmd_parent_node=0 is safely clamped to -1:
        AssertThat(muzzleAtt.Node).IsEqual(-1);
        AssertThat(Mathf.IsEqualApprox(muzzleAtt.Local.Origin.X, 0.0f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(muzzleAtt.Local.Origin.Y, 0.12f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(muzzleAtt.Local.Origin.Z, -0.55f, 1e-4f)).IsTrue();

        // Trailing dot preserved on Weapon.FX.Particle.Effect_1.:
        var fxAtt = reparsed.Attachments.First(a => a.Name == "Weapon.FX.Particle.Effect_1.");
        AssertThat(Mathf.IsEqualApprox(fxAtt.Local.Origin.X, -0.11f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(fxAtt.Local.Origin.Z, -0.57f, 1e-4f)).IsTrue();

        // Untouched socket from template preserved:
        var sheathAtt = reparsed.Attachments.First(a => a.Name == "Weapon.Weapon.Sheath");
        AssertThat(Mathf.IsEqualApprox(sheathAtt.Local.Origin.Y, 0.14f, 1e-4f)).IsTrue();
        AssertThat(Mathf.IsEqualApprox(sheathAtt.Local.Origin.Z, 0.03f, 1e-4f)).IsTrue();
    }

    [TestCase]
    public void TestGlbGmdDummyPatchRestoresDottedNamesAndExtras()
    {
        var attachments = new List<Refii.Models.GmdAttachment>
        {
            new("Weapon.Weapon.Hold", Transform3D.Identity, -1),
            new("Weapon.Weapon.ProjectileEmitter", new Transform3D(Basis.Identity, new Vector3(0f, 0.35f, 0.05f)), -1),
            new("Weapon.FX.Particle.Effect_1.", Transform3D.Identity, -1),
        };
        var gmd = Refii.Models.Engine_GmdSkeleton.TryParse(Refii.Models.Engine_GmdWriter.Write(null, attachments))!;

        string sanitizedGlbJson =
            "{" +
            "\"asset\":{\"version\":\"2.0\"}," +
            "\"nodes\":[" +
            "{\"name\":\"GmdDummies\",\"children\":[1,2,3]}," +
            "{\"name\":\"Weapon_Weapon_Hold\"}," +
            "{\"name\":\"Weapon_Weapon_ProjectileEmitter\"}," +
            "{\"name\":\"Weapon_FX_Particle_Effect_1_\"}" +
            "]" +
            "}";
        byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(sanitizedGlbJson);
        int pad = (4 - (jsonBytes.Length % 4)) % 4;
        int chunkLen = jsonBytes.Length + pad;
        byte[] glb = new byte[20 + chunkLen];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(0, 4), 0x46546C67u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4, 4), 2u);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8, 4), (uint)glb.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12, 4), (uint)chunkLen);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16, 4), 0x4E4F534Au);
        Array.Copy(jsonBytes, 0, glb, 20, jsonBytes.Length);
        for (int i = 0; i < pad; i++) glb[20 + jsonBytes.Length + i] = (byte)' ';

        byte[] patchedGlb = Refii.Models.Engine_GmdWriter.PatchGlbGmdDummyNamesAndExtras(glb, gmd);
        int patchedJsonLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(patchedGlb.AsSpan(12, 4));
        string patchedJson = System.Text.Encoding.UTF8.GetString(patchedGlb, 20, patchedJsonLen);

        AssertThat(patchedJson.Contains("\"name\":\"Weapon.Weapon.Hold\"")).IsTrue();
        AssertThat(patchedJson.Contains("\"gmd_dummy_name\":\"Weapon.Weapon.Hold\"")).IsTrue();
        AssertThat(patchedJson.Contains("\"name\":\"Weapon.Weapon.ProjectileEmitter\"")).IsTrue();
        AssertThat(patchedJson.Contains("\"gmd_dummy_name\":\"Weapon.Weapon.ProjectileEmitter\"")).IsTrue();
        AssertThat(patchedJson.Contains("\"name\":\"Weapon.FX.Particle.Effect_1.\"")).IsTrue();
        AssertThat(patchedJson.Contains("\"gmd_dummy_name\":\"Weapon.FX.Particle.Effect_1.\"")).IsTrue();
    }
}

