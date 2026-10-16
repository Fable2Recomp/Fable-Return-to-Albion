namespace Refii.Tests;

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Gdb;

[TestSuite]
public class GdbPlacementModelResolutionTests
{
    private const uint HashParent = GdbView.HashParent;
    private const uint HashStaticMeshComponent = GdbHash.StaticMeshComponent; // 0x29CF50D1
    private const uint HashModelFile = GdbHash.ModelFile; // 0x0C17DB4E
    private const uint HashRemoveComponent = 0x9B41D00A;

    private const uint QuestItemsBaseId = 0x1A75622B;
    private const uint WineBottleArchetypeId = 0xA2BE8411;
    private const uint WineBottleInstanceId = 0x531CA99D;

    private const uint BaseCompId = 0xC9397216;
    private const uint WineCompId = 0xAAFE166D;

    private const uint Model1Hash = 0x8BD9C5B9;
    private const uint WineBottleModelHash = 0xA3FAFF94;

    [TestCase]
    public void TestChildOverridesParentStaticMeshComponent_AncestorNotCollected()
    {
        var db = new GdbPatchDatabase();

        // 1. Ancestor Component C_base with ModelFile = Model1
        var compBase = new GdbPatchEntry(BaseCompId, 0x01);
        compBase.Properties.Add(new GdbPatchProperty(HashModelFile, GdbFieldType.String, Model1Hash));
        db.Entries.Add(compBase);

        // 2. Ancestor record (QuestItemsBaseId) with StaticMeshComponent = BaseCompId
        var questBase = new GdbPatchEntry(QuestItemsBaseId, 0x01);
        questBase.Properties.Add(new GdbPatchProperty(HashStaticMeshComponent, GdbFieldType.Reference, BaseCompId));
        db.Entries.Add(questBase);

        // 3. Child Component C_wine with ModelFile = WineBottleModel
        var compWine = new GdbPatchEntry(WineCompId, 0x01);
        compWine.Properties.Add(new GdbPatchProperty(HashModelFile, GdbFieldType.String, WineBottleModelHash));
        db.Entries.Add(compWine);

        // 4. Wine bottle archetype with parent = QuestItemsBaseId and StaticMeshComponent = WineCompId
        var wineArch = new GdbPatchEntry(WineBottleArchetypeId, 0x01);
        wineArch.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, QuestItemsBaseId));
        wineArch.Properties.Add(new GdbPatchProperty(HashStaticMeshComponent, GdbFieldType.Reference, WineCompId));
        db.Entries.Add(wineArch);

        // 5. Wine bottle instance with parent = WineBottleArchetypeId and no direct components
        var wineInst = new GdbPatchEntry(WineBottleInstanceId, 0x01);
        wineInst.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, WineBottleArchetypeId));
        db.Entries.Add(wineInst);

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out string error)).IsTrue();

        AssertThat(view.TryLookup(WineBottleInstanceId, out int instIdx)).IsTrue();
        List<uint> models = Engine_GdbPlacements.CollectModelPathHashes(view, instIdx);

        // Must collect ONLY the overriding wine bottle model, NOT the ancestor's model1!
        AssertThat(models.Count).IsEqual(1);
        AssertThat(models[0]).IsEqual(WineBottleModelHash);
        AssertThat(models.Contains(Model1Hash)).IsFalse();
    }

    [TestCase]
    public void TestChildInheritsParentStaticMeshComponent_WhenNoOverride()
    {
        var db = new GdbPatchDatabase();

        // 1. Component with ModelFile
        var comp = new GdbPatchEntry(WineCompId, 0x01);
        comp.Properties.Add(new GdbPatchProperty(HashModelFile, GdbFieldType.String, WineBottleModelHash));
        db.Entries.Add(comp);

        // 2. Parent with StaticMeshComponent
        var parent = new GdbPatchEntry(WineBottleArchetypeId, 0x01);
        parent.Properties.Add(new GdbPatchProperty(HashStaticMeshComponent, GdbFieldType.Reference, WineCompId));
        db.Entries.Add(parent);

        // 3. Child with parent set, no direct appearance
        var child = new GdbPatchEntry(WineBottleInstanceId, 0x01);
        child.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, WineBottleArchetypeId));
        db.Entries.Add(child);

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out string error)).IsTrue();

        AssertThat(view.TryLookup(WineBottleInstanceId, out int childIdx)).IsTrue();
        List<uint> models = Engine_GdbPlacements.CollectModelPathHashes(view, childIdx);

        AssertThat(models.Count).IsEqual(1);
        AssertThat(models[0]).IsEqual(WineBottleModelHash);
    }

    [TestCase]
    public void TestRemoveComponent_StripsInheritedAppearance()
    {
        var db = new GdbPatchDatabase();

        // 1. Component with ModelFile
        var comp = new GdbPatchEntry(WineCompId, 0x01);
        comp.Properties.Add(new GdbPatchProperty(HashModelFile, GdbFieldType.String, WineBottleModelHash));
        db.Entries.Add(comp);

        // 2. Parent with StaticMeshComponent
        var parent = new GdbPatchEntry(WineBottleArchetypeId, 0x01);
        parent.Properties.Add(new GdbPatchProperty(HashStaticMeshComponent, GdbFieldType.Reference, WineCompId));
        db.Entries.Add(parent);

        // 3. Child with RemoveComponent = StaticMeshComponent
        var child = new GdbPatchEntry(WineBottleInstanceId, 0x01);
        child.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, WineBottleArchetypeId));
        child.Properties.Add(new GdbPatchProperty(HashRemoveComponent, GdbFieldType.String, HashStaticMeshComponent));
        db.Entries.Add(child);

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out string error)).IsTrue();

        AssertThat(view.TryLookup(WineBottleInstanceId, out int childIdx)).IsTrue();
        List<uint> models = Engine_GdbPlacements.CollectModelPathHashes(view, childIdx);

        AssertThat(models.Count).IsEqual(0);
    }

    [TestCase]
    public void TestGlobalsGdb_WineBottles_DoNotLeakModel1()
    {
        string path = @"F:\Documents\programming\refii\Godot\build\x86_64\assets\game\data\Globals\globals.gdb";
        if (!File.Exists(path))
        {
            return;
        }

        byte[] bytes = File.ReadAllBytes(path);
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out string error)).IsTrue();

        // 1. Check 0xA2BE8411 (parent of QC055_BottleNoNote 0x531CA99D)
        AssertThat(view.TryLookup(WineBottleArchetypeId, out int bottle1Idx)).IsTrue();
        List<uint> bottle1Models = Engine_GdbPlacements.CollectModelPathHashes(view, bottle1Idx);
        AssertThat(bottle1Models.Count).IsEqual(1);
        AssertThat(bottle1Models[0]).IsEqual(WineBottleModelHash);
        AssertThat(bottle1Models.Contains(Model1Hash)).IsFalse();

        // 2. Check 0x7CF24F3B (parent of QC055_Bottle 0x5E5A29C4)
        uint bottle2ArchetypeId = 0x7CF24F3B;
        uint bottle2ModelHash = 0x7D892008;
        AssertThat(view.TryLookup(bottle2ArchetypeId, out int bottle2Idx)).IsTrue();
        List<uint> bottle2Models = Engine_GdbPlacements.CollectModelPathHashes(view, bottle2Idx);
        AssertThat(bottle2Models.Count).IsEqual(1);
        AssertThat(bottle2Models[0]).IsEqual(bottle2ModelHash);
        AssertThat(bottle2Models.Contains(Model1Hash)).IsFalse();
        AssertThat(bottle2Models.Contains(WineBottleModelHash)).IsFalse();

        // 3. Check 0x1A75622B (Quest & Story Items Base itself still retains its own direct model)
        AssertThat(view.TryLookup(QuestItemsBaseId, out int questBaseIdx)).IsTrue();
        List<uint> questBaseModels = Engine_GdbPlacements.CollectModelPathHashes(view, questBaseIdx);
        AssertThat(questBaseModels.Count).IsEqual(1);
        AssertThat(questBaseModels[0]).IsEqual(Model1Hash);
    }
}
