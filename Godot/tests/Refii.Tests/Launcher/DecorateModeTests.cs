using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Godot;
using GdUnit4;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Level;
using Refii.Launcher.Core;
using Refii.Mods;
using Refii.Game;
using Refii.Ui;
using System.Reflection;
using static GdUnit4.Assertions;
using Refii.Titles.Fable2;

namespace Refii.Tests.Launcher;

[TestSuite]
public class DecorateModeTests
{
    private const uint Card = 100, Template = 200;
    private const string Model = "art/furniture/chair.mdl";
    private static uint H(string name) => Engine_GameHash.Fnv1(name);
    private static byte[] Fixture(uint category = 15, uint level = 3, bool fake = false, string physics = "PhysicsSimulationStaticComponent", int levelCount = 5)
    {
        var data = new GdbPatchDatabase();
        void Entry(uint id, params (string Field, GdbFieldType Type, uint Value)[] fields)
        {
            var entry = new GdbPatchEntry(id, 1);
            foreach (var f in fields) entry.Properties.Add(new GdbPatchProperty(H(f.Field), f.Type, f.Value));
            data.Entries.Add(entry);
        }
        Entry(Card, ("FurnitureUnlockComponent", GdbFieldType.Reference, 101), ("InventoryItemComponent", GdbFieldType.Reference, 102));
        Entry(101, ("Type", GdbFieldType.Enum, 3), ("Level", GdbFieldType.Int, level));
        Entry(102, ("Category", GdbFieldType.Enum, category), ("GUIScreenMesh", GdbFieldType.String, Engine_GameHash.ResourcePath(Model)));
        Entry(Template, ("UpgradeableFurnitureComponent", GdbFieldType.Reference, 201),
            ("GraphicAppearanceStaticMeshComponent", GdbFieldType.Reference, 202),
            (physics, GdbFieldType.Reference, 203),
            ("ManagedLocationComponent", GdbFieldType.Reference, 204));
        Entry(201, ("Type", GdbFieldType.Enum, 3), ("BaseLevel", GdbFieldType.Int, 1),
            ("FurnitureLevels", GdbFieldType.Reference, 205),
            ("NotActuallyForUpgrades", GdbFieldType.Bool, fake ? 1u : 0u));
        Entry(202, ("ModelFile", GdbFieldType.String, H("old-model")));
        Entry(203, ("PhysicsFile", GdbFieldType.String, H("chair-collision")));
        Entry(204, ("Radius", GdbFieldType.Float, BitConverter.SingleToUInt32Bits(5)));
        Entry(205, Enumerable.Range(1, levelCount).Select(i => ($"Level_{i}", GdbFieldType.Reference, (uint)(300 + i))).ToArray());
        for (int i = 1; i <= levelCount; i++) Entry((uint)(300 + i), ("Mesh", GdbFieldType.String, H($"grade-{i}-model")));
        return GdbWriter.Serialize(data);
    }
    private static Dictionary<uint, string> Models() => new() { [Engine_GameHash.ResourcePath(Model)] = Model };

    private static Vector3[] RoomFaces(float x, float z, float width, float depth, float bottom = 0, float top = 4)
    {
        var result = new List<Vector3>();
        foreach (float y in new[] { bottom, top })
        {
            Vector3 a = new(x, y, z), b = new(x + width, y, z), c = new(x + width, y, z + depth), d = new(x, y, z + depth);
            result.AddRange(new[] { a, b, c, a, c, d });
        }
        return result.ToArray();
    }

    [TestCase]
    public void InteriorUsesTheFloorPlanInsteadOfItsBoundingBox()
    {
        var interior = new Engine_DecorateInterior();
        AssertThat(interior.Contains(Vector3.One)).IsFalse();
        var faces = RoomFaces(0, 0, 2, 5).Concat(RoomFaces(2, 0, 3, 2)).ToArray();
        var pose = new Transform3D(new Basis(Vector3.Up, 0.8f), new Vector3(20, 2, -15));
        interior.Build(faces, pose);
        AssertThat(interior.Ready).IsTrue();
        foreach (var p in new[] { new Vector3(1, 0, 4), new Vector3(4, 2, 1), new Vector3(2, 3.8f, 1) })
            AssertThat(interior.Contains(pose * p)).IsTrue();
        foreach (var p in new[] { new Vector3(4, 2, 4), new Vector3(6, 2, 1), new Vector3(1, -1, 1), new Vector3(1, 5, 1) })
            AssertThat(interior.Contains(pose * p)).IsFalse();
        AssertThat(interior.Contains(new Vector3(float.NaN, 0, 0))).IsFalse();
        interior.Clear();
        AssertThat(interior.Ready).IsFalse();
    }

    [TestCase]
    public void PlacementChecksTheWholeObjectIncludingOverhangsAndConcaveGaps()
    {
        var interior = new Engine_DecorateInterior();
        interior.Build(RoomFaces(0, 0, 5, 5), Transform3D.Identity);
        var box = new Aabb(new Vector3(-0.5f, 0, -0.5f), Vector3.One);
        AssertThat(interior.Contains(box, new Transform3D(Basis.Identity, new Vector3(1, 0, 1)))).IsTrue();
        AssertThat(interior.Contains(box, new Transform3D(Basis.Identity, new Vector3(0.1f, 0, 1)))).IsFalse();
        AssertThat(interior.Contains(box, new Transform3D(Basis.Identity, new Vector3(1, 3.8f, 1)))).IsFalse();
        AssertThat(interior.Contains(box, new Transform3D(Basis.Identity, new Vector3(1, -0.2f, 1)))).IsFalse();
        var faces = RoomFaces(0, 0, 1, 5).Concat(RoomFaces(1, 0, 3, 1)).Concat(RoomFaces(4, 0, 1, 5)).ToArray();
        interior.Build(faces, Transform3D.Identity);
        AssertThat(interior.Contains(new Aabb(new Vector3(0.2f, 0, 0.2f), new Vector3(4.6f, 1, 4.6f)), Transform3D.Identity)).IsFalse();
        interior.Build(new[] { Vector3.Zero, Vector3.Right, Vector3.Back }, Transform3D.Identity);
        AssertThat(interior.Contains(new Vector3(0.1f, 0, 0.1f))).IsFalse();
    }

    [TestCase]
    public void DecorationsKeepTheirComponentsWhileStructuralFixturesStayFixed()
    {
        var source = new GdbPatchDatabase();
        var models = Models();
        uint next = 1000;
        uint Prop(string name, string? special = null, bool dynamic = false)
        {
            uint id = next; next += 10;
            string path = $"art/environment/props/{name}.mdl";
            uint model = Engine_GameHash.ResourcePath(path);
            models[model] = path;
            var entity = new GdbPatchEntry(id, 1);
            foreach (string slot in new[] { "ObjectComponent", "WhereaboutsComponent", "GraphicAppearanceStaticMeshComponent",
                dynamic ? "PhysicsSimulationDynamicComponent" : "PhysicsSimpleComponent", "ManagedLocationComponent", "GraphicAppearanceLightComponent" })
                entity.Properties.Add(new(H(slot), GdbFieldType.Reference, id + 1));
            if (special is not null) entity.Properties.Add(new(H(special), GdbFieldType.Reference, id + 1));
            var component = new GdbPatchEntry(id + 1, 1);
            component.Properties.Add(new(H("ModelFile"), GdbFieldType.String, model));
            source.Entries.Add(entity); source.Entries.Add(component);
            return id;
        }
        uint rug = Prop("esa_rug_worn"), lamp = Prop("bs_lamp"), vase = Prop("bs_vase", dynamic: true);
        uint inventory = Prop("esa_book", "InventoryItemComponent", true);
        var fixedIds = new[] { Prop("bs_door"), Prop("bs_fireplace"), Prop("bs_window"), Prop("bs_stairs"),
            Prop("fixture", "DoorComponent"), Prop("fixture2", "BuildingComponent"), Prop("fixture3", "WorkplaceComponent") };
        byte[] bytes = GdbWriter.Serialize(source);
        var patch = new GdbPatchDatabase();
        var catalog = Engine_DecorateCatalog.Build(GdbMulti.Build(new[] { bytes }), models, patch);
        foreach (uint id in new[] { rug, lamp, vase, inventory }) AssertThat(catalog.Furniture.Contains(id)).IsTrue();
        foreach (uint id in fixedIds) AssertThat(catalog.Furniture.Contains(id)).IsFalse();
        AssertThat(catalog.Movable.ContainsKey(vase)).IsFalse();
        AssertThat(catalog.Movable.ContainsKey(inventory)).IsFalse();
        var installed = GdbMulti.Build(new[] { bytes, GdbWriter.Serialize(patch) });
        foreach (uint id in new[] { rug, lamp })
        {
            AssertThat(catalog.Movable[id].Length).IsEqual(1);
            uint moved = H(catalog.Movable[id][0]);
            AssertThat(installed.TryLookup(moved, out var entity)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Component(installed, entity, "PhysicsSimpleComponent", out _)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Component(installed, entity, "GraphicAppearanceLightComponent", out _)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Component(installed, entity, "ManagedLocationComponent", out _)).IsFalse();
            AssertThat(patch.Aliases.Contains((moved, moved))).IsTrue();
            AssertThat(catalog.FurnitureTypes[moved]).IsEqual(13);
        }
        AssertThat(bytes).IsEqual(GdbWriter.Serialize(source));
    }

    [TestCase]
    public void CachedCatalogIsTheBuiltOne()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "refii_decorate_" + Guid.NewGuid().ToString("N"));
        string file = System.IO.Path.Combine(dir, "sub", "catalog.bin");
        try
        {
            byte[] gdb = Fixture();
            var models = Models();
            byte[]?[] blobs = { gdb, null };
            byte[] key = Engine_DecorateCatalog.CacheKey(blobs, models, null);
            var patch = new GdbPatchDatabase();
            Engine_DecorateCatalog built = Engine_DecorateCatalog.Build(GdbMulti.Build(blobs), models, patch);
            byte[] bytes = GdbWriter.Serialize(patch);
            AssertThat(Engine_DecorateCatalog.TryReadCache(file, key, models, out _, out _, out _)).IsFalse();

            Engine_DecorateCatalog.WriteCache(file, key, built, bytes, built.LuaCatalog());
            AssertThat(Engine_DecorateCatalog.TryReadCache(file, key, models, out Engine_DecorateCatalog? read, out byte[] readBytes, out string readLua)).IsTrue();
            AssertThat(readBytes).IsEqual(bytes);
            AssertThat(readLua).IsEqual(built.LuaCatalog());
            AssertThat(read!.Items.Values.ToArray()).IsEqual(built.Items.Values.ToArray());
            AssertThat(read.Models.Count).IsEqual(models.Count);

            // Any input that differs is a different answer.
            AssertThat(Engine_DecorateCatalog.CacheKey(blobs, models, null)).IsEqual(key);
            byte[] changed = (byte[])gdb.Clone();
            changed[^1] ^= 1;
            var otherModels = new Dictionary<uint, string>(models) { [1u] = "x.mdl" };
            foreach (byte[] other in new[]
            {
                Engine_DecorateCatalog.CacheKey(new byte[]?[] { changed, null }, models, null),
                Engine_DecorateCatalog.CacheKey(new byte[]?[] { null, gdb }, models, null),
                Engine_DecorateCatalog.CacheKey(blobs, otherModels, null),
                Engine_DecorateCatalog.CacheKey(blobs, models, new byte[] { 1 }),
            })
            {
                AssertThat(other.AsSpan().SequenceEqual(key)).IsFalse();
                AssertThat(Engine_DecorateCatalog.TryReadCache(file, other, models, out _, out _, out _)).IsFalse();
            }

            // A file cut short, or with any byte of its patch changed, reads as no cache at all: a
            // damaged patch must never reach the game as data.
            byte[] whole = System.IO.File.ReadAllBytes(file);
            System.IO.File.WriteAllBytes(file, whole[..(whole.Length / 2)]);
            AssertThat(Engine_DecorateCatalog.TryReadCache(file, key, models, out _, out _, out _)).IsFalse();
            byte[] damaged = (byte[])whole.Clone();
            damaged[key.Length + 32 + 8] ^= 1;
            System.IO.File.WriteAllBytes(file, damaged);
            AssertThat(Engine_DecorateCatalog.TryReadCache(file, key, models, out _, out _, out _)).IsFalse();
            System.IO.File.WriteAllBytes(file, whole);
            AssertThat(Engine_DecorateCatalog.TryReadCache(file, key, models, out _, out _, out _)).IsTrue();
        }
        finally
        {
            if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, true);
        }
    }

    [TestCase]
    public void FurnitureUsesItsInventoryCategoryAndRealUpgradeComponent()
    {
        foreach (var invalid in new[] { Fixture(1), Fixture(15, 0), Fixture(15, 6), Fixture(fake: true) })
        {
            var catalog = Engine_DecorateCatalog.Build(GdbMulti.Build(new[] { invalid }), Models(), new());
            AssertThat(catalog.Items.Count).IsEqual(0);
        }
        var db = GdbMulti.Build(new[] { Fixture() });
        AssertThat(Engine_DecorateCatalog.Build(db, new Dictionary<uint, string>(), new()).Items.Count).IsEqual(0);
        AssertThat(Engine_DecorateCatalog.Build(db, Models(), new()).Items[Card].Type).IsEqual(3);
    }

    [TestCase]
    public void CopiesFurnitureWithItsLevelAndCollisionWithoutChangingItsOriginal()
    {
        byte[] source = Fixture();
        var original = GdbMulti.Build(new[] { source });
        var patch = new GdbPatchDatabase();
        var catalog = Engine_DecorateCatalog.Build(original, Models(), patch);
        var installed = GdbMulti.Build(new[] { source, GdbWriter.Serialize(patch) });
        AssertThat(installed.TryLookup(catalog.Items[Card].Definition, out var entity)).IsTrue();
        AssertThat(Engine_DecorateCatalog.Component(installed, entity, "UpgradeableFurnitureComponent", out var upgrade)).IsTrue();
        AssertThat(Engine_DecorateCatalog.Field(installed, upgrade, "BaseLevel", out uint level)).IsTrue();
        AssertThat(level).IsEqual(3u);
        AssertThat(Engine_DecorateCatalog.Component(installed, entity, "PhysicsSimulationStaticComponent", out var physics)).IsTrue();
        AssertThat(Engine_DecorateCatalog.Field(installed, physics, "PhysicsFile", out uint collision)).IsTrue();
        AssertThat(collision).IsEqual(H("chair-collision"));
        AssertThat(Engine_DecorateCatalog.Component(installed, entity, "ManagedLocationComponent", out _)).IsFalse();
        AssertThat(installed.TryLookup(Template, out var unchanged)).IsTrue();
        AssertThat(Engine_DecorateCatalog.Component(installed, unchanged, "UpgradeableFurnitureComponent", out var unchangedUpgrade)).IsTrue();
        Engine_DecorateCatalog.Field(installed, unchangedUpgrade, "BaseLevel", out uint oldLevel);
        AssertThat(oldLevel).IsEqual(1u);
        AssertThat(installed.TryLookup(H(Engine_DecorateCatalog.CardName(Card)), out var card)).IsTrue();
        AssertThat(card.View.RecordHash(card.Record)).IsEqual(Card);
        AssertThat(source).IsEqual(Fixture());
    }

    [TestCase]
    public void RetainsEachPhysicsComponentAndUsesTheChosenModelsCollision()
    {
        foreach (string slot in new[] { "PhysicsSimulationStaticComponent", "PhysicsSimulationKeyframedComponent", "PhysicsSimulationDynamicComponent" })
        {
            byte[] source = Fixture(physics: slot);
            var patch = new GdbPatchDatabase();
            var resources = Models();
            uint collision = Engine_GameHash.ResourcePath("art/furniture/chair.hkx");
            resources[collision] = "art/furniture/chair.hkx";
            var catalog = Engine_DecorateCatalog.Build(GdbMulti.Build(new[] { source }), resources, patch);
            var installed = GdbMulti.Build(new[] { source, GdbWriter.Serialize(patch) });
            AssertThat(installed.TryLookup(catalog.Items[Card].Definition, out var entity)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Component(installed, entity, slot, out var physics)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Field(installed, physics, "PhysicsFile", out uint file)).IsTrue();
            AssertThat(file).IsEqual(collision);
        }
    }

    [TestCase]
    public void FixedFurnitureHasStandaloneDefinitionsForEveryExistingUpgradeLevel()
    {
        byte[] source = Fixture();
        var patch = new GdbPatchDatabase();
        var catalog = Engine_DecorateCatalog.Build(GdbMulti.Build(new[] { source }), Models(), patch);
        var installed = GdbMulti.Build(new[] { source, GdbWriter.Serialize(patch) });
        AssertThat(catalog.Movable[Template].Length).IsEqual(5);
        for (int level = 1; level <= 5; level++)
        {
            string name = catalog.Movable[Template][level - 1];
            AssertThat(name).IsEqual(Engine_DecorateCatalog.MovableName(Template, level));
            AssertThat(installed.TryLookup(H(name), out var entity)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Component(installed, entity, "UpgradeableFurnitureComponent", out var upgrade)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Field(installed, upgrade, "BaseLevel", out uint actual)).IsTrue();
            AssertThat(actual).IsEqual((uint)level);
            AssertThat(Engine_DecorateCatalog.Field(installed, upgrade, "Type", out uint type)).IsTrue();
            AssertThat(type).IsEqual(3u);
            AssertThat(Engine_DecorateCatalog.Component(installed, entity, "ManagedLocationComponent", out _)).IsFalse();
            AssertThat(Engine_DecorateCatalog.Field(installed, entity, "PhysicsSimulationStaticComponent", out uint physics)).IsTrue();
            AssertThat(physics).IsEqual(203u);
            AssertThat(Engine_DecorateCatalog.Field(installed, entity, "GraphicAppearanceStaticMeshComponent", out uint mesh)).IsTrue();
            AssertThat(mesh).IsEqual(202u);
            AssertThat(catalog.FurnitureTypes[H(name)]).IsEqual(3);
            AssertThat(catalog.Movable.ContainsKey(H(name))).IsFalse();
        }
        AssertThat(installed.TryLookup(Template, out var original)).IsTrue();
        AssertThat(Engine_DecorateCatalog.Component(installed, original, "UpgradeableFurnitureComponent", out var originalUpgrade)).IsTrue();
        Engine_DecorateCatalog.Field(installed, originalUpgrade, "BaseLevel", out uint unchanged);
        AssertThat(unchanged).IsEqual(1u);
        AssertThat(catalog.LuaCatalog().Contains("RefiiDecorateMovable", StringComparison.Ordinal)).IsTrue();
        AssertThat(catalog.LuaCatalog().Contains("RefiiDecorateMoved_000000C8_5", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void ThreeGradeFurniturePreservesItsHighestGradeThroughTheNativeMapping()
    {
        byte[] source = Fixture(levelCount: 3);
        var patch = new GdbPatchDatabase();
        var catalog = Engine_DecorateCatalog.Build(GdbMulti.Build(new[] { source }), Models(), patch);
        var installed = GdbMulti.Build(new[] { source, GdbWriter.Serialize(patch) });
        for (int index = 0; index < 3; index++)
        {
            AssertThat(installed.TryLookup(H(catalog.Movable[Template][index]), out var entity)).IsTrue();
            Engine_DecorateCatalog.Component(installed, entity, "UpgradeableFurnitureComponent", out var upgrade);
            Engine_DecorateCatalog.Field(installed, upgrade, "BaseLevel", out uint baseLevel);
            AssertThat(baseLevel).IsEqual(new uint[] { 1, 2, 5 }[index]);
            AssertThat(Engine_DecorateCatalog.Component(installed, upgrade, "FurnitureLevels", out var levels)).IsTrue();
            AssertThat(Engine_DecorateCatalog.Field(installed, levels, "Level_3", out uint highest)).IsTrue();
            AssertThat(highest).IsEqual(303u);
        }
    }

    [TestCase]
    public void FurnitureSpawnNamesResolveThroughGuestAliasTable()
    {
        using var mounted = new BnkFileSystem();
        byte[] source = Fixture();
        mounted.AddVirtualFile("globals/globals.gdb", "globals/globals.gdb", source.Length, () => source, "fixture");
        byte[] model = { 1, 2, 3 };
        mounted.AddVirtualFile(Model, Model, model.Length, () => model, "fixture");
        byte[] init = Encoding.UTF8.GetBytes("function Refii.on(name, fn) end\n");
        mounted.AddVirtualFile(ModLua.InitKey, ModLua.InitKey, init.Length, () => init, "fixture");
        Engine_DecorateCatalog.Attach(mounted);
        var catalog = Engine_DecorateCatalog.Current!;
        AssertThat(mounted.TryGetFile("globals/globals.gdb.tu", out var file)).IsTrue();
        AssertThat(GdbView.TryParse(file.ReadAllBytes(), out var guestFile, out _)).IsTrue();
        foreach (string name in catalog.Items.Values.Select(i => Engine_DecorateCatalog.DefinitionName(i.Card))
            .Concat(catalog.Movable.Values.SelectMany(names => names)))
        {
            uint hash = H(name);
            AssertThat(guestFile!.TryLookup(hash, out _)).IsTrue();
            AssertThat(guestFile.TryResolveAlias(hash, out uint entry)).OverrideFailureMessage($"Guest cannot spawn '{name}' by name").IsTrue();
            AssertThat(entry).IsEqual(hash);
            AssertThat(guestFile.TryLookup(entry, out int index)).IsTrue();
            AssertThat(guestFile.Fields(index).Any(field => field.Hash == H("UpgradeableFurnitureComponent"))).IsTrue();
        }
    }

    [TestCase]
    public void FreshInstallRebuildsStableDefinitionsAndLeavesOriginalScriptsUntouched()
    {
        uint? first = null;
        for (int install = 0; install < 2; install++)
        {
            using var mounted = new BnkFileSystem();
            void Add(string path, byte[] bytes) => mounted.AddVirtualFile(path, path, bytes.Length, () => bytes, "fixture");
            Add("globals/globals.gdb", Fixture());
            Add(Model, new byte[] { 1, 2, 3 });
            byte[] original = Encoding.UTF8.GetBytes("OriginalFurniture = true");
            Add("scripts/furniture.lua", original);
            Add(ModLua.InitKey, Encoding.UTF8.GetBytes("function Refii.on(name, fn) end\nExistingMod = true\n"));
            Engine_DecorateCatalog.Attach(mounted);
            var catalog = Engine_DecorateCatalog.Current!;
            AssertThat(catalog.Items.Count).IsEqual(1);
            first ??= catalog.Items[Card].Definition;
            AssertThat(catalog.Items[Card].Definition).IsEqual(first.Value);
            AssertThat(mounted.TryGetFile("scripts/furniture.lua", out var lua)).IsTrue();
            AssertThat(lua.ReadAllBytes()).IsEqual(original);
            AssertThat(mounted.TryGetFile(ModLua.InitKey, out var init)).IsTrue();
            string text = Encoding.UTF8.GetString(init.ReadAllBytes());
            AssertThat(text.Contains("ExistingMod = true", StringComparison.Ordinal)).IsTrue();
            AssertThat(text.Contains("RefiiDecorateCard_00000064", StringComparison.Ordinal)).IsTrue();
            AssertThat(text.Contains("RefiiDecorateMoved_000000C8_3", StringComparison.Ordinal)).IsTrue();
            AssertThat(mounted.TryGetFile("globals/globals.gdb.tu", out var tu)).IsTrue();
            AssertThat(GdbView.TryParse(tu.ReadAllBytes(), out _, out _)).IsTrue();
        }
    }

    [TestCase]
    public void DecorateIsAnOptInLauncherControl()
    {
        var setting = RuntimeCVarsGodot.Definitions.Single(d => d.Name == Engine_DecorateCatalog.Setting);
        AssertThat(setting.Category).IsEqual(RuntimeCVarsGodot.ControlsCategory);
        AssertThat(setting.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(setting.DefaultValue).IsEqual("false");
        AssertThat(RuntimeSettingsModel.IsAdvanced(setting)).IsFalse();
    }

    [TestCase]
    public void SharedFreeCameraMovesInViewSpaceAndBoundsLargeFrameSteps()
    {
        Vector3 position = Vector3.Zero;
        float yaw = 0, pitch = 0;
        Transform3D pose = Engine_FreeCamera.Step(ref position, ref yaw, ref pitch, new Vector3(1, 1, -1),
            Vector2.Zero, Vector2.Zero, 10, 2);
        AssertThat(Math.Abs(position.Length() - 0.1f) < 0.00001f).IsTrue();
        AssertThat(pose.Origin).IsEqual(position);
        Vector3 before = position;
        Engine_FreeCamera.Step(ref position, ref yaw, ref pitch, Vector3.One, Vector2.Zero, new Vector2(0, 100000), float.NaN, 2);
        AssertThat(position).IsEqual(before);
        AssertThat(pitch).IsEqual(-1.55f);
        position = Vector3.Zero; yaw = Mathf.Pi / 2; pitch = 0;
        Engine_FreeCamera.Step(ref position, ref yaw, ref pitch, Vector3.Forward, Vector2.Zero, Vector2.Zero, 0.05f, 2);
        AssertThat(position.X < -0.099f && Math.Abs(position.Z) < 0.0001f).IsTrue();
    }

    [TestCase]
    public void RoomDiscoverySelectsAndRestoresObjectsWithLazyNativeSearches()
    {
        string? tool = System.Environment.GetEnvironmentVariable("REFII_TEST_LUA");
        if (string.IsNullOrWhiteSpace(tool)) return;
        string source = """
        local events, values, entities = {}, {}, {}
        local writes, creates, destroyed, money = 0, 0, 0, 2
        local consume, failOwner, rejectMove, rejectStore = true, false, false, false
        local limbo, place = {}, "home"
        local vector = {}
        vector.__index = vector
        function vector:GetX() return self.x end
        function vector:GetY() return self.y end
        function vector:GetZ() return self.z end
        function vector.__add(a,b) return CVector3(a.x+b.x,a.y+b.y,a.z+b.z) end
        function vector.__mul(a,n) return CVector3(a.x*n,a.y*n,a.z*n) end
        function CVector3(x,y,z) return setmetatable({x=x,y=y,z=z},vector) end
        local function entity(id, definition, p)
            local e={id=id,definition=definition,p=p,f=CVector3(0,1,0),alive=true}
            function e:IsAlive() return self.alive end
            function e:GetPosition() return self.p end
            function e:GetName() return self.name or self.id end
            function e:SetName(name) self.name=name end
            function e:SetPosition(p) error('generic setter must not move physics furniture') end
            function e:Destroy() self.alive=false destroyed=destroyed+1 end
            entities[#entities+1]=e
            return e
        end
        local house=entity('0000000000000001','house',CVector3(0,0,0))
        local hero=entity('0000000000000002','hero',CVector3(0,0,0))
        local chair=entity('0000000000000003','old',CVector3(1,2,0))
        chair.inside=house chair.fixed=true
        function GetPlayerHero() return hero end
        function GetSaveTable() return {Limbo=limbo} end
        function LoadScriptSave(data) limbo=data.Limbo or {} end
        ScriptFunction={PutEntityInLimbo=function(e)
            if rejectStore then return end
            assert(e.alive and not limbo[e.name])
            limbo[e.name]=e e.alive=false
        end,CreateEntityFromLimbo=function(name,p)
            local e=limbo[name]
            if e then limbo[name]=nil e.alive=true e.p=p end
        end}
        Physics={TeleportToPosition=function(e,p) writes=writes+1 if not rejectMove and not e.fixed then e.p=p end end,GetFacingVector=function(e) return e.f end,SetFacingVector=function(e,f) e.f=f end}
        OwnedEntity={IsAvailable=function(e) return e.owner~=nil end,GetOwner=function(e) return e.owner end,
            SetOwner=function(e,o) if failOwner then error('owner failure') end e.owner=o end}
        UpgradeableFurniture={GetECType=function() return 1 end,GetCurrentLevel=function(e) return e.upgrade-1 end}
        Object={GetECType=function() return 2 end}
        SearchTools={StartNewSearch=function(kind) assert(kind=='object') return {populated=false} end,
            FilterWithEC=function(s,component) assert(component==Object.GetECType()) s.populated=true end,
            FilterWithName=function(s,name) s.name=name s.populated=true end,
            GetSearchResults=function(s)
                local found={}
                if not s.populated then return found end
                for _,e in ipairs(entities) do if e.alive and (not s.name or e.name==s.name) then found[#found+1]=e end end
                return found
            end}
        Debug={CreateEntityAtPosition=function(def,name,p)
            creates=creates+1
            local e=entity(string.format('%016x',100+creates),def,p)
            e.name=name e.upgrade=tonumber(string.match(def,"moved(%d)"))
            return e
        end}
        Inventory={RemoveItemOfType=function() if consume then money=money-1 end end}
        local function fromHandle(e)
            if type(e)~='number' then return e end
            for _,v in ipairs(entities) do if tonumber(v.id,16)==e then return v end end
        end
        Whereabouts={IsAvailable=function(e) return e.inside~=nil end,GetBuildingWeAreInsideOf=function(e) return e.inside end}
        Refii={on=function(name,fn) events[name]=fn end,publish=function(k,v) values[k]=v end,
            uid=function(e) return fromHandle(e).id end,handle=function(e) return tonumber(e.id,16) end,entity=fromHandle,
            hash=function(s) return s end,definition=function(e) return fromHandle(e).definition end,level_name=function() return place end,
            world_here=function() return true end,value=function() return 1 end,
            item_count=function() return money end,_end_redecorating=function() end,_error=function() end,
            _by_uid=function(id) for _,e in ipairs(entities) do if e.id==id and e.alive then return e end end end}
        Refii._decorate_rotation=function(handle,x,y,z,w)
            local e=fromHandle(handle)
            if x then e.q={x,y,z,w} return true end
            if e.q then return unpack(e.q) end
            local yaw=math.atan2(e.f.x,e.f.y)
            return 0,0,-math.sin(yaw/2),math.cos(yaw/2)
        end
        RefiiDecorateCatalog={['0x00000064']={definition='new',inventory='card',kind=1}}
        RefiiDecorateFurniture={old=1,new=1}
        RefiiDecorateMovable={old={'moved1'}}
        for i=1,5 do RefiiDecorateFurniture['moved'..i]=1 end

        local nextHouse=entity('0000000000000004','house2',CVector3(100,0,0))
        local outside=entity('0000000000000005','old',CVector3(100,0,0))
        outside.inside=nextHouse
        local fixture=entity('0000000000000006','door',CVector3(2,0,0))
        fixture.owner=house fixture.inside=house
        local vase=entity('0000000000000007','vase',CVector3(3,0,0))
        vase.inside=house
        RefiiDecorateFurniture.vase=13
        local roomTable=entity('0000000000000008','table',CVector3(4,0,0))
        roomTable.owner=house
        RefiiDecorateFurniture.table=3
        local dead=entity('0000000000000009','vase',CVector3(3,0,0))
        dead.inside=house dead.alive=false
        local originalCreate=Debug.CreateEntityAtPosition
        Debug.CreateEntityAtPosition=function(...) local e=originalCreate(...) e.inside=house return e end
        UpgradeableFurniture.GetCurrentLevel=function() error('decorations have no upgrade component') end
        """ + "\n" + Engine_DecorateRuntime.Script + "\n" + """
        local D=RefiiDecorate
        local function settle() events.tick() events.tick() end
        local function start() events.redecorating({player=1,on=1,building=house}) end
        start()
        assert(values['refii.decorate.count']==3, 'Expected the chair, vase and table, got '..tostring(values['refii.decorate.count']))
        assert(values['refii.decorate.building0']==1)
        assert(values['refii.decorate.room1.uid0']==3 and values['refii.decorate.room2.uid0']==7 and values['refii.decorate.room3.uid0']==8)
        D.Call('Select',3)
        assert(values['refii.decorate.selected']==1 and values['refii.decorate.uid0']==8)
        D.Call('Cancel')
        assert(values['refii.decorate.selected']==0 and roomTable.p.x==4)
        D.Call('Select',1)
        D.Call('Commit',2,3,1,0.5)
        assert(chair.alive)
        settle()
        assert(not chair.alive and fixture.alive and outside.alive)
        local saved=GetSaveTable()
        local moved=saved.RefiiDecorate.levels.home['1']
        assert(moved.sourceRecord=='old' and moved.upgrade==1 and moved.x==2)
        D.Call('Select',1)
        D.Call('Commit',4,5,1,0.3)
        settle()
        assert(vase.alive and vase.p.x==4)
        saved=GetSaveTable()
        assert(saved.RefiiDecorate.levels.home['2'].uid==vase.id)
        D.Call('End')
        entities[#entities].alive=false chair.alive=true vase.p=CVector3(3,0,0)
        LoadScriptSave(saved)
        events.level_loaded()
        for i=1,60 do events.tick() end
        assert(not chair.alive and vase.p.x==4)
        start()
        assert(values['refii.decorate.count']==3 and fixture.alive and outside.alive and roomTable.alive)
        D.Call('Select',1)
        vase.contents={rareItem=3}
        rejectStore=true
        D.Call('Store','0x00000123',1,0,0,0,1,0,0,0,1,0,0,0)
        settle()
        assert(vase.alive and values['refii.decorate.status']==3)
        rejectStore=false
        D.Call('Store','0x00000123',1,0,0,0,1,0,0,0,1,0,0,0)
        settle()
        assert(not vase.alive and values['refii.decorate.storedcount']==1 and values['refii.decorate.count']==2)
        local storageId=values['refii.decorate.stored1.id']
        saved=GetSaveTable()
        local stored=saved.RefiiDecorate.levels.home[tostring(storageId)]
        assert(stored.stored and stored.model=='0x00000123' and limbo[stored.name].contents.rareItem==3)
        LoadScriptSave(saved) events.level_loaded()
        for i=1,60 do events.tick() end
        start()
        assert(values['refii.decorate.storedcount']==1 and not vase.alive)
        D.Call('Take',storageId)
        assert(values['refii.decorate.selected']==1 and values['refii.decorate.stored']==storageId and not vase.alive)
        D.Call('Cancel')
        assert(stored.stored and not vase.alive)
        D.Call('Take',storageId)
        failOwner=true vase.owner=house
        D.Call('Commit',1,1,1,0,0.5,0.5,0.5,0.5)
        failOwner=false
        assert(stored.stored and not vase.alive and limbo[stored.name])
        D.Call('Take',storageId)
        D.Call('Commit',1,1,1,0,0.5,0.5,0.5,0.5)
        settle()
        assert(vase.alive and not stored.stored and not limbo[stored.name] and vase.contents.rareItem==3)
        assert(stored.qx==0.5 and stored.qy==0.5 and stored.qz==0.5 and stored.qw==0.5)
        saved=GetSaveTable()
        vase.q={0,0,0,1}
        LoadScriptSave(saved) events.level_loaded()
        for i=1,60 do events.tick() end
        assert(vase.q[1]==0.5 and vase.q[2]==0.5 and vase.q[3]==0.5 and vase.q[4]==0.5)
        start()
        D.Call('Select',1)
        D.Call('Store','0x00000123',1,0,0,0,1,0,0,0,1,0,0,0)
        settle()
        D.Call('End') place='otherhome' start()
        D.Call('Take',storageId) D.Call('Commit',2,2,1,0,0,0,0,1) settle()
        saved=GetSaveTable()
        assert(saved.RefiiDecorate.levels.home[tostring(storageId)].retired)
        assert(vase.alive and values['refii.decorate.storedcount']==0)
        local relocated
        for _,item in pairs(saved.RefiiDecorate.levels.otherhome) do relocated=item end
        assert(relocated and relocated.uid==vase.id and relocated.qw==1)
        print('PASS: storage, cancellation, failure rollback, contents, quaternion save/restore, cross-level inventory, ownerless decorations, native interior membership, fixtures and neighbouring homes excluded, static copy, dynamic instance preservation, grade-free save/restore')
        """;
        var start = new System.Diagnostics.ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-");
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(source);
        process.StandardInput.Close();
        bool finished = process.WaitForExit(10000);
        if (!finished) process.Kill(true);
        AssertThat(finished).OverrideFailureMessage("Decorate Lua lifecycle timed out").IsTrue();
        AssertThat(process.ExitCode).OverrideFailureMessage("Decorate Lua lifecycle: " + error.GetAwaiter().GetResult()).IsEqual(0);
        AssertThat(output.GetAwaiter().GetResult().Contains("PASS:", StringComparison.Ordinal)).IsTrue();
    }

    [TestCase]
    public void NewRuntimeCompilesWithTheGuestLuaCompiler()
    {
        if (!Refii.Lua.Engine_LuaDecompiler.Available) return;
        AssertThat(Refii.Lua.Engine_LuaDecompiler.TryCheckSource(Engine_DecorateRuntime.Script,
            "@refii/decorate", out string error)).OverrideFailureMessage("Decorate Lua: " + error).IsTrue();
    }

    [TestCase]
    public void FurniturePickingUsesTheDisplayedRotationScaleAndCameraRay()
    {
        var box = new Aabb(new Vector3(-0.5f, -0.5f, -0.5f), Vector3.One);
        var pose = new Transform3D(Basis.FromEuler(new Vector3(0, 0.7f, 0)).Scaled(new Vector3(2, 3, 0.4f)), new Vector3(4, 1, -8));
        Vector3 camera = new(0, 1, 0);
        AssertThat(Engine_DecoratePicking.Segment(box, pose, camera, pose.Origin - camera, 40, out var from, out var to)).IsTrue();
        AssertThat((pose * from).DistanceTo(camera) < 0.0001f).IsTrue();
        AssertThat(Math.Abs((pose * to).DistanceTo(camera) - 40) < 0.0001f).IsTrue();
        AssertThat(Engine_DecoratePicking.Segment(box, pose, camera, Vector3.Right, 40, out _, out _)).IsFalse();
        AssertThat(Engine_DecoratePicking.Segment(box, pose, camera, pose.Origin - camera, 2, out _, out _)).IsFalse();
        AssertThat(Engine_DecoratePicking.Segment(box, pose, pose.Origin, Vector3.Up, 40, out _, out _)).IsTrue();
    }

    [TestCase]
    public void FurniturePickingRejectsInvalidAndBehindCameraTargets()
    {
        var box = new Aabb(-Vector3.One, Vector3.One * 2);
        var behind = new Transform3D(Basis.Identity, new Vector3(0, 0, 6));
        AssertThat(Engine_DecoratePicking.Segment(box, behind, Vector3.Zero, Vector3.Forward, 40, out _, out _)).IsFalse();
        AssertThat(Engine_DecoratePicking.Segment(box, Transform3D.Identity, Vector3.Zero, Vector3.Zero, 40, out _, out _)).IsFalse();
        AssertThat(Engine_DecoratePicking.Segment(box, new Transform3D(new Basis(Vector3.Zero, Vector3.Zero, Vector3.Zero), Vector3.Zero),
            Vector3.Zero, Vector3.Forward, 40, out _, out _)).IsFalse();
        AssertThat(Engine_DecoratePicking.Segment(box, Transform3D.Identity, Vector3.Zero, Vector3.Forward, float.NaN, out _, out _)).IsFalse();
        AssertThat(Engine_DecoratePicking.Segment(box, Transform3D.Identity, new Vector3(float.NaN, 0, 0), Vector3.Forward, 40, out _, out _)).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void PickingHitsVisibleFacesAndReusesTheMeshAfterMovementAndCleanup()
    {
        using var box = new BoxMesh { Size = Vector3.One };
        using var picking = new Engine_DecoratePicking();
        var near = new Transform3D(Basis.Identity, new Vector3(0, 0, -5));
        var far = new Transform3D(Basis.Identity, new Vector3(0, 0, -9));
        AssertThat(picking.Hit(box, near, Vector3.Zero, Vector3.Forward, 40, out float distance)).IsTrue();
        AssertThat(Math.Abs(distance - 4.5f) < 0.001f).IsTrue();
        AssertThat(picking.Hit(box, far, Vector3.Zero, Vector3.Forward, distance, out _)).IsFalse();
        AssertThat(picking.Hit(box, new Transform3D(Basis.Identity, new Vector3(3, 0, -5)),
            Vector3.Zero, Vector3.Forward, 40, out _)).IsFalse();
        using var holes = new ArrayMesh();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-2, -1, 0), new Vector3(-2, 1, 0), new Vector3(-1, 0, 0),
            new Vector3(1, -1, 0), new Vector3(1, 1, 0), new Vector3(2, 0, 0) };
        holes.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        AssertThat(Engine_DecoratePicking.Segment(holes.GetAabb(), near, Vector3.Zero, Vector3.Forward, 40, out _, out _)).IsTrue();
        AssertThat(picking.Hit(holes, near, Vector3.Zero, Vector3.Forward, 40, out _)).IsFalse();
        picking.Dispose();
        AssertThat(picking.Hit(box, far, Vector3.Zero, Vector3.Forward, 40, out distance)).IsTrue();
        AssertThat(Math.Abs(distance - 8.5f) < 0.001f).IsTrue();
    }

    [TestCase]
    public void FreeCameraTurnsInPlaceAndMovesInTheDirectionItFaces()
    {
        Vector3 position = new(2, 3, 5);
        float yaw = 0, pitch = -0.6f;
        Vector3 start = position;
        Transform3D turned = Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
            Vector3.Zero, new Vector2(1, 1), Vector2.Zero, 0.05f, false, false);
        AssertThat(position).IsEqual(start);
        AssertThat(yaw < 0 && pitch < -0.6f).IsTrue();
        Transform3D moved = Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
            Vector3.Forward, Vector2.Zero, Vector2.Zero, 0.05f, false, false);
        Vector3 travel = moved.Origin - turned.Origin;
        AssertThat(travel.Dot(-moved.Basis.Z) > 0.149f).IsTrue();
        AssertThat(travel.Cross(moved.Basis.Z).Length() < 0.00001f).IsTrue();
        Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
            Vector3.Back, Vector2.Zero, Vector2.Zero, 0.05f, false, false);
        AssertThat(position.IsEqualApprox(start)).IsTrue();
    }

    [TestCase]
    public void FreeCameraStopsImmediatelyAndHasConsistentPrecisionAndFastSpeeds()
    {
        static Vector3 Travel(bool fine, bool fast, Vector3 move)
        {
            Vector3 position = Vector3.Zero;
            float yaw = 0, pitch = 0;
            for (int i = 0; i < 60; i++) Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
                move, Vector2.Zero, Vector2.Zero, 1f / 60, fine, fast);
            Vector3 before = position;
            Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
                Vector3.Zero, Vector2.Zero, Vector2.Zero, 1f / 60, fine, fast);
            AssertThat(position).IsEqual(before);
            return position;
        }
        float normal = Travel(false, false, Vector3.Forward).Length();
        AssertThat(normal).IsEqualApprox(3, 0.0001f);
        AssertThat(Travel(true, false, Vector3.Forward).Length()).IsEqualApprox(0.6f, 0.0001f);
        AssertThat(Travel(false, true, Vector3.Forward).Length()).IsEqualApprox(8, 0.0001f);
        AssertThat(Travel(true, true, Vector3.Forward).Length()).IsEqualApprox(0.6f, 0.0001f);
        AssertThat(Travel(false, false, new Vector3(1, 1, -1)).Length()).IsEqualApprox(normal, 0.0001f);
    }

    [TestCase]
    public void FreeCameraHeightIsWorldVerticalAndMovementIsFrameRateIndependent()
    {
        Vector3 position = new(3, 4, 5), start = position;
        float yaw = 0.8f, pitch = -0.7f;
        Transform3D raised = Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
            Vector3.Up, Vector2.Zero, Vector2.Zero, 0.05f, false, false);
        AssertThat(position.X).IsEqual(start.X); AssertThat(position.Z).IsEqual(start.Z);
        AssertThat(position.Y - start.Y).IsEqualApprox(0.15f, 0.00001f);
        Transform3D lowered = Engine_DecorateControls.Camera(ref position, ref yaw, ref pitch,
            Vector3.Down, Vector2.Zero, Vector2.Zero, 0.05f, false, false);
        AssertThat(position.IsEqualApprox(start)).IsTrue();
        AssertThat(lowered.Basis).IsEqual(raised.Basis);
        static Vector3 AtRate(int rate)
        {
            Vector3 p = Vector3.Zero;
            float y = 0, x = -0.5f;
            for (int i = 0; i < rate; i++) Engine_DecorateControls.Camera(ref p, ref y, ref x,
                Vector3.Right, Vector2.Zero, Vector2.Zero, 1f / rate, false, false);
            return p;
        }
        AssertThat(AtRate(30).DistanceTo(AtRate(120)) < 0.0001f).IsTrue();
    }

    [TestCase]
    public void HeldFurnitureFollowsCameraWithoutSnappingOrChangingItsGrabOffset()
    {
        var before = new Transform3D(Basis.FromEuler(new Vector3(-0.2f, 0.3f, 0)), new Vector3(2, 3, 5));
        Vector3 offset = new(0.4f, -0.7f, -4);
        Vector3 piece = before * offset;
        AssertThat(Engine_DecorateControls.Carry(before, before, piece, 0)).IsEqual(piece);
        var after = new Transform3D(Basis.FromEuler(new Vector3(0.2f, -0.5f, 0)), new Vector3(4, 5, 2));
        Vector3 moved = Engine_DecorateControls.Carry(before, after, piece, 0);
        AssertThat(moved.IsEqualApprox(after * offset)).IsTrue();
        AssertThat(Engine_DecorateControls.Carry(after, before, moved, 0).IsEqualApprox(piece)).IsTrue();
        AssertThat(Engine_DecorateControls.Carry(before, before, piece, 2).IsEqualApprox(before * (offset + Vector3.Forward * 2))).IsTrue();
    }

    [TestCase]
    public void RotationValuesAreRelativeToTheOriginalAngleAndAcceptPreciseInput()
    {
        float origin = Mathf.DegToRad(170);
        AssertThat(Engine_DecorateControls.RotationDegrees(origin, origin)).IsEqualApprox(0, 0.001f);
        AssertThat(Engine_DecorateControls.TryRotation("25.5", origin, out float yaw)).IsTrue();
        AssertThat(Engine_DecorateControls.RotationDegrees(yaw, origin)).IsEqualApprox(25.5f, 0.001f);
        AssertThat(Engine_DecorateControls.TryRotation("-90.1", origin, out yaw)).IsTrue();
        AssertThat(Engine_DecorateControls.RotationDegrees(yaw, origin)).IsEqualApprox(-90.1f, 0.001f);
        AssertThat(Engine_DecorateControls.TryRotation("360", origin, out yaw)).IsTrue();
        AssertThat(Engine_DecorateControls.RotationDegrees(yaw, origin)).IsEqualApprox(0, 0.001f);
        foreach (string text in new[] { "", "-", ".", "NaN", "Infinity", "1..2", "12,5", "1e40" })
            AssertThat(Engine_DecorateControls.TryRotation(text, origin, out _)).IsFalse();
        AssertThat(Engine_DecorateControls.TryRotation("0", float.NaN, out _)).IsFalse();
    }

    [TestCase]
    public void AllRotationAxesRoundTripThroughGuestCoordinates()
    {
        foreach (Vector3 angles in new[] { new Vector3(0.3f, 0.7f, -0.2f), new Vector3(-0.4f, 2.8f, 1.2f), new Vector3(0, 0.6f, 0) })
        {
            Quaternion guest = Engine_DecorateControls.GuestRotation(angles);
            Vector3 restored = Engine_DecorateControls.Angles(guest);
            AssertThat(Engine_DecorateControls.Pose(Vector3.Zero, restored).Basis.IsEqualApprox(
                Engine_DecorateControls.Pose(Vector3.Zero, angles).Basis)).IsTrue();
        }
        AssertThat(Engine_DecorateControls.Pose(Vector3.Zero, new Vector3(0, 0.6f, 0)).IsEqualApprox(
            Engine_DecorateControls.Pose(Vector3.Zero, 0.6f))).IsTrue();
        AssertThat(Engine_DecorateControls.DragPoint(new Vector3(0, 1, 3), new Vector3(0, 1, -3).Normalized(),
            Vector3.Zero, Vector3.Back, out Vector3 higher)).IsTrue();
        AssertThat(higher.Y).IsEqualApprox(2, 0.001f);
        AssertThat(higher.Z).IsEqualApprox(0, 0.001f);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void WallsStopAndSlideThePreviewWithObjectCollisionDisabled()
    {
        var root = new Node3D(); ((SceneTree)Engine.GetMainLoop()).Root.AddChild(root);
        var collision = new Engine_DecorateCollision();
        var interior = new Engine_DecorateInterior();
        var faces = new List<Vector3>(RoomFaces(-5, -5, 10, 10));
        faces.AddRange(new[] { new Vector3(1, 0, -4), new Vector3(1, 4, -4), new Vector3(1, 4, 4),
            new Vector3(1, 0, -4), new Vector3(1, 4, 4), new Vector3(1, 0, 4) });
        interior.Build(faces.ToArray(), Transform3D.Identity);
        try
        {
            using var mesh = new BoxMesh { Size = Vector3.One };
            var preview = new MeshInstance3D { Mesh = mesh }; root.AddChild(preview);
            Vector3 from = new(0, 1, 0), target = new(3, 1, 0);
            float amount = collision.LimitMovement(preview, Transform3D.Identity, false, interior,
                Transform3D.Identity, from, Vector3.Zero, target, Vector3.Zero, false, Array.Empty<long>());
            Vector3 at = from.Lerp(target, amount);
            AssertThat(at.X > 0.5f && at.X < 0.52f).IsTrue();
            Vector3 requested = at + new Vector3(0.4f, 0.5f, 0.5f);
            Vector3 slid = collision.Slide(preview, Transform3D.Identity, false, interior, Transform3D.Identity,
                at, Vector3.Zero, requested, false);
            AssertThat(slid.X < 0.52f).IsTrue();
            AssertThat(slid.Y).IsEqualApprox(requested.Y, 0.001f);
            AssertThat(slid.Z).IsEqualApprox(requested.Z, 0.001f);
            AssertThat(interior.CrossesSurface(mesh.GetAabb(), new Transform3D(Basis.Identity, slid))).IsFalse();
            AssertThat(interior.Surface(new Vector3(-2, 1, 0), new Vector3(3, 1, 0).Normalized(), 10, out Vector3 hit, out Vector3 normal)).IsTrue();
            AssertThat(hit.Y).IsEqualApprox(2, 0.001f);
            AssertThat(normal.X < -0.99f).IsTrue();
        }
        finally { root.Free(); collision.Clear(); }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void CollisionMovementStopsBeforeThinWallsAndAllowsMovingAway()
    {
        var root = new Node3D(); ((SceneTree)Engine.GetMainLoop()).Root.AddChild(root);
        var collision = new Engine_DecorateCollision();
        var interior = new Engine_DecorateInterior();
        interior.Build(RoomFaces(-5, -5, 10, 10), Transform3D.Identity);
        try
        {
            using var mesh = new BoxMesh { Size = Vector3.One };
            using var wallMesh = new BoxMesh { Size = new Vector3(0.02f, 4, 6) };
            Vector3 from = new(0, 1, 0), to = new(3, 1, 0);
            var preview = new MeshInstance3D { Mesh = mesh, Position = from }; root.AddChild(preview);
            var wall = new MeshInstance3D { Mesh = wallMesh, Position = new Vector3(1, 2, 0) }; root.AddChild(wall);
            long[] candidates = { (long)preview.GetInstanceId(), (long)wall.GetInstanceId() };
            float fraction = collision.LimitMovement(preview, Transform3D.Identity, false, interior,
                Transform3D.Identity, from, 0, to, 0, candidates);
            Vector3 stopped = from.Lerp(to, fraction);
            AssertThat(stopped.X > 0.48f && stopped.X < 0.51f).IsTrue();
            preview.Position = stopped;
            AssertThat(collision.Blocked(preview, Transform3D.Identity, false, candidates)).IsFalse();
            AssertThat(collision.LimitMovement(preview, Transform3D.Identity, false, interior,
                Transform3D.Identity, stopped, 0, from, 0, candidates)).IsEqual(1f);
            float again = collision.LimitMovement(preview, Transform3D.Identity, false, interior,
                Transform3D.Identity, stopped, 0, to, 0, candidates);
            AssertThat(stopped.Lerp(to, again).X - stopped.X < 0.005f).IsTrue();
            wall.Visible = false;
            Vector3 edge = new(4.4f, 1, 0);
            fraction = collision.LimitMovement(preview, Transform3D.Identity, false, interior,
                Transform3D.Identity, edge, 0, new Vector3(6, 1, 0), 0, candidates);
            AssertThat(edge.Lerp(new Vector3(6, 1, 0), fraction).X < 4.57f).IsTrue();
        }
        finally { root.Free(); collision.Clear(); }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void RotationStopsAtObstaclesAndPreservesTheModelsOffset()
    {
        var root = new Node3D(); ((SceneTree)Engine.GetMainLoop()).Root.AddChild(root);
        var collision = new Engine_DecorateCollision();
        var interior = new Engine_DecorateInterior();
        interior.Build(RoomFaces(-5, -5, 10, 10), Transform3D.Identity);
        try
        {
            using var mesh = new BoxMesh { Size = new Vector3(2, 1, 0.3f) };
            using var wallMesh = new BoxMesh { Size = new Vector3(6, 4, 0.02f) };
            var offset = new Transform3D(Basis.Identity, new Vector3(0.2f, 0.6f, 0));
            var preview = new MeshInstance3D { Mesh = mesh, Transform = offset }; root.AddChild(preview);
            var wall = new MeshInstance3D { Mesh = wallMesh, Position = new Vector3(0, 2, 0.9f) }; root.AddChild(wall);
            long[] candidates = { (long)preview.GetInstanceId(), (long)wall.GetInstanceId() };
            float fraction = collision.LimitMovement(preview, offset, true, interior, offset,
                Vector3.Zero, 0, Vector3.Zero, Mathf.Pi / 2, candidates);
            AssertThat(fraction > 0 && fraction < 1).IsTrue();
            preview.Transform = Engine_DecorateControls.Pose(Vector3.Zero, Mathf.Pi / 2 * fraction) * offset;
            AssertThat(collision.Blocked(preview, offset, true, candidates)).IsFalse();
            wall.Visible = false;
            AssertThat(collision.LimitMovement(preview, offset, true, interior, offset,
                Vector3.Zero, 0, Vector3.Zero, Mathf.Pi / 2, candidates)).IsEqual(1f);
        }
        finally { root.Free(); collision.Clear(); }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void CollisionEnabledPreviewStopsAtTheBoundaryButTheCameraCanKeepMoving()
    {
        var root = new Node3D(); ((SceneTree)Engine.GetMainLoop()).Root.AddChild(root);
        var decorate = new GameDecorate(); root.AddChild(decorate); decorate.SetProcess(false); decorate.SetProcessInput(false);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(GameDecorate).GetField(name, fields)!.SetValue(decorate, value);
        T Get<T>(string name) => (T)typeof(GameDecorate).GetField(name, fields)!.GetValue(decorate)!;
        void Call(string name, params object[] args) => typeof(GameDecorate).GetMethod(name, fields)!.Invoke(decorate, args);
        try
        {
            using var mesh = new BoxMesh { Size = Vector3.One };
            Vector3 position = new(4.3f, 1, 0);
            var pose = new Transform3D(Basis.Identity, position);
            Set("_previewCard", Card); Set("_piece", position); Set("_ghostSource", pose);
            Get<Engine_DecorateInterior>("_interior").Build(RoomFaces(-5, -5, 10, 10), Transform3D.Identity);
            Call("CreateGhost", mesh, pose, 1u); Call("UpdateGhost"); Call("ToggleCollision");
            AssertThat(Get<bool>("_acceptedPreview")).IsTrue();
            var camera = new Transform3D(Basis.Identity, Vector3.Right * 2);
            Call("CarrySelection", Transform3D.Identity, camera, 0f); Call("UpdateGhost");
            AssertThat(Get<Vector3>("_piece").X < 4.57f).IsTrue();
            AssertThat(Get<Vector3>("_piece").X > 4.3f).IsTrue();
            AssertThat(Get<bool>("_blocked")).IsFalse();
            AssertThat(((StandardMaterial3D)Get<MeshInstance3D>("_ghost").MaterialOverride).AlbedoColor.G).IsEqual(1f);
            Call("CarrySelection", camera, Transform3D.Identity, 0f); Call("UpdateGhost");
            AssertThat(Get<Vector3>("_piece").X < 4.3f).IsTrue();
            Call("ToggleCollision");
            Call("CarrySelection", Transform3D.Identity, camera, 0f); Call("UpdateGhost");
            AssertThat(Get<Vector3>("_piece").X < 4.57f).IsTrue();
            Set("_piece", new Vector3(0, 1, 0)); Set("_pendingMove", false); Set("_dirty", true);
            for (int i = 0; i < 5; i++) Call("UpdateGhost");
            Call("ToggleCollision");
            Set("_piece", new Vector3(4, 1, 0)); Set("_dirty", true); Call("UpdateGhost");
            AssertThat(Get<bool>("_pendingMove")).IsTrue();
            for (int i = 0; i < 4; i++) Call("UpdateGhost");
            AssertThat(Get<Vector3>("_piece").X).IsEqualApprox(4, 0.001f);
            AssertThat(Get<bool>("_pendingMove")).IsFalse();
        }
        finally { root.Free(); }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void NumericRotationEditingPreviewsCancelsAndConfirmsWithoutPlacing()
    {
        var root = new Node3D(); ((SceneTree)Engine.GetMainLoop()).Root.AddChild(root);
        var decorate = new GameDecorate(); root.AddChild(decorate); decorate.SetProcess(false); decorate.SetProcessInput(false);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(GameDecorate).GetField(name, fields)!.SetValue(decorate, value);
        T Get<T>(string name) => (T)typeof(GameDecorate).GetField(name, fields)!.GetValue(decorate)!;
        void Call(string name, params object[] args) => typeof(GameDecorate).GetMethod(name, fields)!.Invoke(decorate, args);
        bool keyboard = Engine_UiInputMode.KeyboardMouse;
        try
        {
            Engine_UiInputMode.KeyboardMouse = true;
            var art = new Node3D(); root.AddChild(art);
            art.AddChild(new MeshInstance3D { Name = "MsgFrame", Mesh = new QuadMesh { Size = new Vector2(430, 640) } });
            var theme = new Engine_UiTheme(); theme.Catalogue(art);
            var viewport = new SubViewport { Size = new Vector2I(800, 600), OwnWorld3D = true }; root.AddChild(viewport);
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = 720,
                Position = new Vector3(0, 0, 20000), Far = 40000, Current = true }; viewport.AddChild(camera);
            var ui = new Engine_UiDecorate(); viewport.AddChild(ui); ui.Build(theme);
            Set("_ui", ui); Set("_uiCamera", camera);
            using var mesh = new BoxMesh();
            Set("_previewCard", Card); Set("_rotationOrigin", 0.6f); Set("_rotation", 0.6f);
            var pose = Engine_DecorateControls.Pose(Vector3.Zero, 0.6f);
            Set("_ghostSource", pose); Call("CreateGhost", mesh, pose, 1u); Call("UpdateGhost");
            Call("Refresh");
            Call("ClickPanel", camera.UnprojectPosition(ui.GlobalPosition + new Vector3(0, 60, 10)));
            AssertThat(Get<bool>("_editingRotation")).IsTrue();
            foreach (char c in "25.5")
            {
                using var key = new InputEventKey { Pressed = true, Unicode = c };
                Call("TypeRotation", key);
            }
            AssertThat(Engine_DecorateControls.RotationDegrees(Get<float>("_rotation"), 0.6f)).IsEqualApprox(25.5f, 0.001f);
            AssertThat(Get<MeshInstance3D>("_ghost").GlobalTransform.IsEqualApprox(Engine_DecorateControls.Pose(Vector3.Zero, Get<float>("_rotation")))).IsTrue();
            Call("Back");
            AssertThat(Get<float>("_rotation")).IsEqualApprox(0.6f, 0.001f);
            AssertThat(Get<bool>("_editingRotation")).IsFalse();
            AssertThat(Get<bool>("_waiting")).IsFalse();
            Call("BeginRotationEdit"); Call("AdjustRotation", 0.1f); Call("Choose");
            AssertThat(Engine_DecorateControls.RotationDegrees(Get<float>("_rotation"), 0.6f)).IsEqualApprox(0.1f, 0.001f);
            AssertThat(Get<bool>("_editingRotation")).IsFalse();
            AssertThat(Get<bool>("_waiting")).IsFalse();
            AssertThat(Get<uint>("_previewCard")).IsEqual(Card);
            Call("BeginRotationEdit");
            Engine_UiInputMode.KeyboardMouse = false;
            Call("ProcessRotationInput", 0u);
            AssertThat(Engine_DecorateControls.RotationDegrees(Get<float>("_rotation"), 0.6f)).IsEqualApprox(0.1f, 0.001f);
            Engine_UiInputMode.KeyboardMouse = true;
            AssertThat(Get<bool>("_editingRotation")).IsFalse();
            AssertThat(Get<bool>("_waiting")).IsFalse();
            Call("BeginRotationEdit");
            using var invalid = new InputEventKey { Pressed = true, Unicode = '-' };
            Call("TypeRotation", invalid); Call("Choose");
            AssertThat(Get<bool>("_editingRotation")).IsTrue();
            Call("Back"); Call("Back");
        }
        finally { Engine_UiInputMode.KeyboardMouse = keyboard; root.Free(); }
    }

    [TestCase]
    public void HeldFurnitureDepthIsBoundedAndRotationDoesNotNeedCameraChords()
    {
        Vector3 far = Engine_DecorateControls.Carry(Transform3D.Identity, Transform3D.Identity, new Vector3(1, 2, -5), 100);
        Vector3 near = Engine_DecorateControls.Carry(Transform3D.Identity, Transform3D.Identity, far, -100);
        AssertThat(far).IsEqual(new Vector3(1, 2, -30));
        AssertThat(near).IsEqual(new Vector3(1, 2, -0.5f));
        AssertThat(Engine_DecorateControls.Rotation(true, false, 0.05f, false)).IsEqualApprox(-Mathf.Pi / 40, 0.00001f);
        AssertThat(Engine_DecorateControls.Rotation(false, true, 0.05f, false)).IsEqualApprox(Mathf.Pi / 40, 0.00001f);
        AssertThat(Engine_DecorateControls.Rotation(true, true, 0.05f, false)).IsEqual(0f);
        AssertThat(Engine_DecorateControls.Rotation(false, false, 0.05f, false)).IsEqual(0f);
        AssertThat(Engine_DecorateControls.Rotation(false, true, 0.05f, true)).IsEqualApprox(0.0125f, 0.00001f);
    }

    [TestCase]
    public void MousePlacementStaysOnItsPlaneAndRejectsRaysBehindTheCamera()
    {
        AssertThat(Engine_DecorateControls.PlanePoint(new Vector3(0, 4, 4), new Vector3(1, -1, -1).Normalized(), 1, out var point)).IsTrue();
        AssertThat(point).IsEqualApprox(new Vector3(3, 1, 1), new Vector3(0.0001f, 0.0001f, 0.0001f));
        AssertThat(Engine_DecorateControls.PlanePoint(Vector3.Up * 4, Vector3.Right, 0, out _)).IsFalse();
        AssertThat(Engine_DecorateControls.PlanePoint(Vector3.Up * 4, Vector3.Up, 0, out _)).IsFalse();
        AssertThat(Engine_DecorateControls.PlanePoint(Vector3.Up * 100, Vector3.Down, 0, out _)).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void FurnitureDragsWithoutJumpingAndDeselectKeepsDecorateOpen()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var viewport = new SubViewport { Size = new Vector2I(1280, 720), OwnWorld3D = true };
        tree.Root.AddChild(viewport);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags shared = BindingFlags.Static | BindingFlags.NonPublic;
        var decorate = new GameDecorate(); viewport.AddChild(decorate); decorate.SetProcess(false); decorate.SetProcessInput(false);
        bool previousInput = Engine_UiInputMode.KeyboardMouse;
        void Set(string name, object value) => typeof(GameDecorate).GetField(name, instance)!.SetValue(decorate, value);
        T Get<T>(string name) => (T)typeof(GameDecorate).GetField(name, instance)!.GetValue(decorate)!;
        void Call(string name, params object[] args) => typeof(GameDecorate).GetMethod(name, instance)!.Invoke(decorate, args);
        try
        {
            var camera = new Camera3D { Position = new Vector3(0, 4, 6), Current = true }; viewport.AddChild(camera);
            camera.LookAt(Vector3.Zero);
            Engine_UiInputMode.KeyboardMouse = true;
            Set("_size", new Vector2(1280, 720));
            Set("_camera", camera); Set("_room", true); Set("_piece", new Vector3(1, 0, 0));
            typeof(GameDecorate).GetField("<Active>k__BackingField", shared)!.SetValue(null, true);
            typeof(GameDecorate).GetField("_selectedUid", shared)!.SetValue(null, 42ul);
            Vector2 cursor = camera.UnprojectPosition(new Vector3(0.5f, 0, 0));
            Set("_cursor", cursor); Call("AnchorDrag"); Call("DragToCursor");
            AssertThat(Get<Vector3>("_piece").IsEqualApprox(new Vector3(1, 0, 0))).IsTrue();
            Set("_cursor", cursor + new Vector2(90, 0)); Call("DragToCursor");
            Vector3 moved = Get<Vector3>("_piece");
            AssertThat(moved.X > 1).IsTrue(); AssertThat(moved.Y).IsEqual(0f);
            camera.Position += Vector3.Right;
            Call("AnchorDrag"); Call("DragToCursor");
            AssertThat(Get<Vector3>("_piece").IsEqualApprox(moved)).IsTrue();
            Engine_UiInputMode.KeyboardMouse = false;
            Transform3D before = camera.GlobalTransform;
            camera.Position += Vector3.Up;
            Call("CarrySelection", before, camera.GlobalTransform, 0f);
            AssertThat(Get<Vector3>("_piece").IsEqualApprox(moved + Vector3.Up)).IsTrue();
            AssertThat(GameDecorate.Selected(42)).IsTrue();
            AssertThat(Get<bool>("_dirty")).IsTrue();
            moved = Get<Vector3>("_piece");
            Set("_waiting", true);
            Call("CarrySelection", camera.GlobalTransform, before, 5f);
            AssertThat(Get<Vector3>("_piece").IsEqualApprox(moved)).IsTrue();
            Set("_waiting", false);
            Engine_UiInputMode.KeyboardMouse = true;
            Call("AnchorDrag"); Call("DragToCursor");
            AssertThat(Get<Vector3>("_piece").IsEqualApprox(moved)).IsTrue();
            Call("UpdateCursor", true, true, cursor);
            AssertThat(Get<Vector2>("_cursor")).IsEqual(new Vector2(640, 360));
            AssertThat(Get<bool>("_dragReady")).IsFalse();
            Call("AnchorDrag");
            Call("UpdateCursor", true, false, cursor);
            AssertThat(Get<bool>("_dragReady")).IsFalse();
            Call("AnchorDrag"); Call("DragToCursor");
            AssertThat(Get<Vector3>("_piece").IsEqualApprox(moved)).IsTrue();
            Call("UpdateCursor", false, false, cursor);
            AssertThat(Get<Vector2>("_cursor")).IsEqual(new Vector2(640, 360));
            Call("Back");
            AssertThat(GameDecorate.Active).IsTrue(); AssertThat(GameDecorate.Selected(42)).IsFalse();
            AssertThat(Get<bool>("_dirty")).IsFalse(); AssertThat(Get<bool>("_cancelPending")).IsTrue();
            Set("_waiting", false); Set("_cancelPending", false);
            Call("Back"); AssertThat(GameDecorate.Active).IsTrue();
            Set("_aimIndex", 0); Call("Choose"); AssertThat(Get<bool>("_waiting")).IsTrue();
            Call("ReconcileSelection", 0ul, 0, 0u);
            AssertThat(Get<bool>("_waiting")).IsTrue();
            Call("Back"); AssertThat(GameDecorate.Active).IsTrue(); AssertThat(Get<bool>("_cancelPending")).IsTrue();
            Call("ReconcileSelection", 0ul, 0, 0u);
            AssertThat(Get<bool>("_cancelPending")).IsTrue();
            Call("ReconcileSelection", 42ul, 0, 0u);
            AssertThat(GameDecorate.Selected(42)).IsFalse(); AssertThat(Get<bool>("_waiting")).IsTrue();
            Call("ReconcileSelection", 0ul, 6, 0u);
            AssertThat(Get<bool>("_waiting")).IsFalse(); AssertThat(GameDecorate.Active).IsTrue();
            Set("_aimIndex", 0); Call("Choose"); Call("ReconcileSelection", 42ul, 0, 0u);
            AssertThat(GameDecorate.Selected(42)).IsTrue(); AssertThat(Get<bool>("_waiting")).IsFalse();
            using var mesh = new BoxMesh();
            Set("_ghostSource", Transform3D.Identity);
            Call("CreateGhost", mesh, Transform3D.Identity, 1u);
            Get<Engine_DecorateInterior>("_interior").Build(RoomFaces(-30, -30, 60, 60, -10, 30), Transform3D.Identity);
            Call("Choose"); AssertThat(Get<bool>("_waiting")).IsTrue();
            Call("ReconcileSelection", 0ul, 5, 0u);
            AssertThat(GameDecorate.Selected(42)).IsFalse(); AssertThat(GameDecorate.Active).IsTrue();
        }
        finally
        {
            Engine_UiInputMode.KeyboardMouse = previousInput;
            typeof(GameDecorate).GetField("<Active>k__BackingField", shared)!.SetValue(null, false);
            typeof(GameDecorate).GetField("_selectedUid", shared)!.SetValue(null, 0ul);
            viewport.Free();
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void PlacementCollisionAllowsContactAndRejectsWallsRotatedFurnitureAndEnclosure()
    {
        var collision = new Engine_DecorateCollision();
        using var box = new BoxMesh { Size = new Vector3(2, 2, 2) };
        using var wall = new BoxMesh { Size = new Vector3(0.2f, 5, 10) };
        using var floor = new PlaneMesh { Size = new Vector2(20, 20) };
        using var enclosing = new BoxMesh { Size = new Vector3(12, 12, 12) };
        Transform3D pose = new(new Basis(Vector3.Up, 0.7f), new Vector3(0, 1, 0));
        Aabb bounds = Engine_DecorateCollision.Inset(box.GetAabb(), pose);
        AssertThat(collision.Overlaps(bounds, pose, floor, Transform3D.Identity)).IsFalse();
        AssertThat(collision.Overlaps(bounds, pose, wall, Transform3D.Identity)).IsTrue();
        AssertThat(collision.Overlaps(bounds, pose, box,
            new Transform3D(new Basis(Vector3.Up, -0.4f), new Vector3(1.1f, 1, 0)))).IsTrue();
        AssertThat(collision.Overlaps(bounds, pose, box,
            new Transform3D(Basis.Identity, new Vector3(4, 1, 0)))).IsFalse();
        AssertThat(collision.Overlaps(bounds, pose, enclosing, Transform3D.Identity)).IsTrue();
        collision.Clear();
        AssertThat(collision.Overlaps(bounds, pose, wall, Transform3D.Identity)).IsTrue();
        pose.Origin = new Vector3(0, 0.7f, 0);
        AssertThat(collision.Overlaps(bounds, pose, floor, Transform3D.Identity)).IsTrue();
    }

    [TestCase]
    public void PlacementCollisionRejectsTrianglesOnlyWhenTheyCrossTheVolume()
    {
        var bounds = new Aabb(-Vector3.One, Vector3.One * 2);
        AssertThat(Engine_DecorateCollision.Triangle(bounds, new Vector3(0, -3, -3),
            new Vector3(0, 3, -3), new Vector3(0, 0, 3))).IsTrue();
        AssertThat(Engine_DecorateCollision.Triangle(bounds, new Vector3(2, -3, -3),
            new Vector3(2, 3, -3), new Vector3(2, 0, 3))).IsFalse();
        AssertThat(Engine_DecorateCollision.Triangle(bounds, new Vector3(0.8f, 2, 0),
            new Vector3(2, 0.8f, 0), new Vector3(2, 2, 0))).IsFalse();
    }

    [TestCase]
    [RequireGodotRuntime]
    public void PlacementChecksIgnoreTheSourceAndPreviewButBlockOtherVisibleCopies()
    {
        var root = new Node3D(); ((SceneTree)Engine.GetMainLoop()).Root.AddChild(root);
        using var mesh = new BoxMesh();
        var collision = new Engine_DecorateCollision();
        try
        {
            var source = new MeshInstance3D { Mesh = mesh }; root.AddChild(source);
            var preview = new MeshInstance3D { Mesh = mesh }; root.AddChild(preview);
            var other = new MeshInstance3D { Mesh = mesh, Position = Vector3.Right * 4 }; root.AddChild(other);
            long[] candidates = { (long)source.GetInstanceId(), (long)preview.GetInstanceId(), (long)other.GetInstanceId() };
            AssertThat(collision.Blocked(preview, source.GlobalTransform, true, candidates)).IsFalse();
            AssertThat(collision.Blocked(preview, source.GlobalTransform, false, candidates)).IsTrue();
            preview.Position = other.Position;
            AssertThat(collision.Blocked(preview, source.GlobalTransform, true, candidates)).IsTrue();
            other.Visible = false;
            AssertThat(collision.Blocked(preview, source.GlobalTransform, true, candidates)).IsFalse();
            other.Visible = true; other.CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly;
            AssertThat(collision.Blocked(preview, source.GlobalTransform, true, candidates)).IsFalse();
        }
        finally { root.Free(); collision.Clear(); }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void PickingHouseObjectsDoesNotDependOnInteriorReadiness()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var viewport = new SubViewport { Size = new Vector2I(800, 600), OwnWorld3D = true };
        tree.Root.AddChild(viewport);
        var camera = new Camera3D { Position = new Vector3(0, 0, 5), Current = true };
        var entities = new GameEntities();
        var decorate = new GameDecorate();
        viewport.AddChild(camera); viewport.AddChild(entities); viewport.AddChild(decorate);
        decorate.SetProcess(false); decorate.SetProcessInput(false);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags shared = BindingFlags.Static | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(GameDecorate).GetField(name, instance)!.SetValue(decorate, value);
        T Get<T>(string name) => (T)typeof(GameDecorate).GetField(name, instance)!.GetValue(decorate)!;
        void Call(string name, params object[] args) => typeof(GameDecorate).GetMethod(name, instance)!.Invoke(decorate, args);
        var addresses = (Dictionary<ulong, uint>)typeof(GameDecorate).GetField("RoomAddresses", shared)!.GetValue(null)!;
        var previousEntities = GameEntities.Current;
        bool previousDevice = Engine_UiInputMode.KeyboardMouse;
        try
        {
            GameEntities.Current = entities;
            using var mesh = new BoxMesh();
            var target = new MeshInstance3D { Mesh = mesh };
            entities.AddChild(target);
            var nodes = (Dictionary<uint, MeshInstance3D>)typeof(GameEntities).GetField("_nodes", instance)!.GetValue(entities)!;
            nodes.Add(0x90010000, target);
            addresses[42] = 0x90010000;
            Get<List<(ulong Uid, string Label)>>("_targets").Add((42, "Table"));
            Set("_camera", camera); Set("_room", true);
            foreach (bool keyboard in new[] { false, true })
            {
                Engine_UiInputMode.KeyboardMouse = keyboard;
                Get<Engine_DecorateInterior>("_interior").Clear();
                Call("UpdateAim", new Vector2(400, 300));
                AssertThat(Get<int>("_aimIndex")).IsEqual(0);
                Get<Engine_DecorateInterior>("_interior").Build(RoomFaces(50, 50, 4, 4), Transform3D.Identity);
                Call("UpdateAim", new Vector2(400, 300));
                AssertThat(Get<int>("_aimIndex")).IsEqual(0);
                Call("Choose");
                AssertThat(Get<bool>("_waiting")).IsTrue();
                Call("ReconcileSelection", 42ul, 0, 0u);
                Set("_ghostSource", Transform3D.Identity);
                Call("CreateGhost", mesh, Transform3D.Identity, 1u);
                Call("CheckCollision");
                AssertThat(Get<bool>("_blocked")).IsTrue();
                AssertThat(Get<bool>("_collisionEnabled")).IsFalse();
                Call("Back"); Call("ReconcileSelection", 0ul, 6, 0u);
            }
        }
        finally
        {
            GameEntities.Current = previousEntities;
            Engine_UiInputMode.KeyboardMouse = previousDevice;
            addresses.Clear();
            typeof(GameDecorate).GetField("_selectedUid", shared)!.SetValue(null, 0ul);
            typeof(GameDecorate).GetField("_aimUid", shared)!.SetValue(null, 0ul);
            viewport.Free();
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void GhostPreviewMovesWithoutTouchingTheSourceAndCancelsWithoutPlacement()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = new Node3D(); tree.Root.AddChild(root);
        var decorate = new GameDecorate(); root.AddChild(decorate); decorate.SetProcess(false); decorate.SetProcessInput(false);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags shared = BindingFlags.Static | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(GameDecorate).GetField(name, instance)!.SetValue(decorate, value);
        T Get<T>(string name) => (T)typeof(GameDecorate).GetField(name, instance)!.GetValue(decorate)!;
        void Call(string name, params object[] args) => typeof(GameDecorate).GetMethod(name, instance)!.Invoke(decorate, args);
        try
        {
            using var mesh = new BoxMesh();
            using var originalMaterial = new StandardMaterial3D { AlbedoColor = Colors.Brown };
            var source = new MeshInstance3D { Mesh = mesh, MaterialOverride = originalMaterial };
            root.AddChild(source);
            Vector3 position = new(2, 1, 3);
            var placement = new Transform3D(new Basis(Vector3.Up, -0.4f), position);
            var offset = new Transform3D(new Basis(Vector3.Up, 0.7f).Scaled(new Vector3(2, 3, 1)), new Vector3(0, 0.25f, 0));
            source.GlobalTransform = placement * offset;
            Transform3D original = source.GlobalTransform;
            Set("_ghostSource", placement); Set("_piece", position); Set("_rotation", 0.4f);
            typeof(GameDecorate).GetField("_selectedUid", shared)!.SetValue(null, 42ul);
            Call("CreateGhost", mesh, original, 1u); Call("UpdateGhost");
            MeshInstance3D ghost = Get<MeshInstance3D>("_ghost");
            AssertThat(ghost.GlobalTransform.IsEqualApprox(original)).IsTrue();
            AssertThat(ghost.Mesh == mesh).IsTrue();
            AssertThat(ghost.GetChildCount()).IsEqual(0);
            AssertThat(((StandardMaterial3D)ghost.MaterialOverride).AlbedoColor.A < 1).IsTrue();
            AssertThat(ghost.CastShadow).IsEqual(GeometryInstance3D.ShadowCastingSetting.Off);
            AssertThat(Get<bool>("_collisionEnabled")).IsFalse();
            var camera = new Transform3D(Basis.Identity, new Vector3(0, 1, 8));
            var movedCamera = new Transform3D(new Basis(Vector3.Up, 0.2f), new Vector3(1, 2, 8));
            Call("CarrySelection", camera, movedCamera, 0f);
            Set("_rotation", 0.9f); Call("UpdateGhost");
            Transform3D expected = new Transform3D(new Basis(Vector3.Up, -0.9f), Get<Vector3>("_piece")) * offset;
            AssertThat(ghost.GlobalTransform.IsEqualApprox(expected)).IsTrue();
            AssertThat(source.GlobalTransform.IsEqualApprox(original)).IsTrue();
            AssertThat(source.MaterialOverride == originalMaterial).IsTrue();
            Get<Engine_DecorateInterior>("_interior").Build(RoomFaces(-30, -30, 60, 60, -10, 30), Transform3D.Identity);
            Set("_blocked", true);
            Call("CheckCollision");
            AssertThat(Get<bool>("_blocked")).IsFalse();
            AssertThat(((StandardMaterial3D)ghost.MaterialOverride).AlbedoColor.G).IsEqual(1f);
            Call("Back");
            AssertThat(ghost.Visible).IsFalse();
            AssertThat(ghost.IsQueuedForDeletion()).IsTrue();
            AssertThat(source.GlobalTransform.IsEqualApprox(original)).IsTrue();
            AssertThat(GameDecorate.Selected(42)).IsFalse();
        }
        finally
        {
            typeof(GameDecorate).GetField("_selectedUid", shared)!.SetValue(null, 0ul);
            root.Free();
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void InventoryPreviewNeedsNoGuestEntityAndIsRemovedAfterConfirmOrCancel()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = new Node3D(); tree.Root.AddChild(root);
        var decorate = new GameDecorate(); root.AddChild(decorate); decorate.SetProcess(false); decorate.SetProcessInput(false);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(GameDecorate).GetField(name, fields)!.SetValue(decorate, value);
        T Get<T>(string name) => (T)typeof(GameDecorate).GetField(name, fields)!.GetValue(decorate)!;
        void Call(string name, params object[] args) => typeof(GameDecorate).GetMethod(name, fields)!.Invoke(decorate, args);
        try
        {
            using var mesh = new BoxMesh();
            Set("_awaitSelection", true);
            Call("ReconcileSelection", 0ul, 0, Card);
            AssertThat(Get<uint>("_previewCard")).IsEqual(Card);
            AssertThat(Get<bool>("_waiting")).IsFalse();
            Call("Choose"); AssertThat(Get<bool>("_waiting")).IsFalse();
            Set("_ghostSource", Transform3D.Identity);
            Call("CreateGhost", mesh, Transform3D.Identity, 1u);
            Call("CarrySelection", Transform3D.Identity, new Transform3D(Basis.Identity, Vector3.Right), 0f);
            Call("UpdateGhost");
            AssertThat(Get<Vector3>("_piece")).IsEqual(Vector3.Right);
            MeshInstance3D ghost = Get<MeshInstance3D>("_ghost");
            Call("Choose"); AssertThat(Get<bool>("_waiting")).IsFalse();
            AssertThat(Get<bool>("_blocked")).IsTrue();
            Get<Engine_DecorateInterior>("_interior").Build(RoomFaces(-10, -10, 20, 20, -1, 5), Transform3D.Identity);
            Call("Choose"); AssertThat(Get<bool>("_waiting")).IsTrue();
            Call("ReconcileSelection", 0ul, 4, Card);
            AssertThat(Get<bool>("_waiting")).IsFalse();
            AssertThat(ghost.Visible).IsTrue();
            Call("Choose"); Call("ReconcileSelection", 0ul, 5, 0u);
            AssertThat(ghost.Visible).IsFalse();
            AssertThat(Get<uint>("_previewCard")).IsEqual(0u);
            Set("_awaitSelection", true); Call("ReconcileSelection", 0ul, 0, Card);
            Call("CreateGhost", mesh, Transform3D.Identity, 1u);
            ghost = Get<MeshInstance3D>("_ghost");
            Call("Back");
            AssertThat(ghost.Visible).IsFalse();
            AssertThat(Get<uint>("_previewCard")).IsEqual(0u);
            Call("ReconcileSelection", 0ul, 0, Card);
            AssertThat(Get<uint>("_previewCard")).IsEqual(0u);
            Call("ReconcileSelection", 0ul, 6, 0u);
            AssertThat(Get<bool>("_waiting")).IsFalse();
        }
        finally { root.Free(); }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void HidingDecorateUiKeepsControllerCrosshairRenderedAcrossModeChanges()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = new Node3D();
        tree.Root.AddChild(root);
        bool keyboard = Engine_UiInputMode.KeyboardMouse;
        try
        {
            var art = new Node3D(); root.AddChild(art);
            art.AddChild(new MeshInstance3D { Name = "MsgFrame", Mesh = new QuadMesh { Size = new Vector2(430, 640) } });
            var theme = new Engine_UiTheme(); theme.Catalogue(art);
            var controls = new SubViewportContainer(); root.AddChild(controls);
            var viewport = new SubViewport { RenderTargetUpdateMode = SubViewport.UpdateMode.Always }; controls.AddChild(viewport);
            var ui = new Engine_UiDecorate(); viewport.AddChild(ui); ui.Build(theme);
            var reticle = new Node3D(); viewport.AddChild(reticle);
            var decorate = new GameDecorate(); root.AddChild(decorate); decorate.SetProcess(false); decorate.SetProcessInput(false);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            void Set(string name, object value) => typeof(GameDecorate).GetField(name, fields)!.SetValue(decorate, value);
            void Call(string name, params object[] values) => typeof(GameDecorate).GetMethod(name, fields)!.Invoke(decorate, values);
            Set("_ui", ui); Set("_container", controls); Set("_reticle", reticle); Set("_room", true);
            Engine_UiInputMode.KeyboardMouse = false;
            Call("Refresh");
            AssertThat(ui.PanelBounds.Size.Y).IsEqual(280f);
            AssertThat(ui.Hit(new Vector2(-100, -106))).IsEqual(Engine_UiDecorate.PageSize + 8);
            AssertThat(GameDecorate.KeyLabel("DPad")).IsEqual("K");
            Call("ToggleCollision");
            AssertThat((bool)typeof(GameDecorate).GetField("_collisionEnabled", fields)!.GetValue(decorate)!).IsTrue();
            Call("ToggleCollision");
            AssertThat((bool)typeof(GameDecorate).GetField("_collisionEnabled", fields)!.GetValue(decorate)!).IsFalse();
            AssertThat(GameDecorate.KeyLabel("X")).IsEqual("Tab");
            AssertThat(GameDecorate.KeyLabel("Back")).IsEqual("H");
            Call("UpdateReticle"); AssertThat(reticle.Visible).IsTrue();
            Call("SetUiHidden", true);
            AssertThat(controls.Visible).IsTrue(); AssertThat(ui.Visible).IsFalse();
            AssertThat(reticle.IsVisibleInTree()).IsTrue();
            AssertThat(viewport.RenderTargetUpdateMode).IsEqual(SubViewport.UpdateMode.Always);
            Call("SetUiHidden", false);
            AssertThat(controls.Visible).IsTrue(); AssertThat(ui.Visible).IsTrue(); AssertThat(reticle.Visible).IsTrue();
            AssertThat(viewport.RenderTargetUpdateMode).IsEqual(SubViewport.UpdateMode.Always);
            Call("SetUiHidden", true); Call("ToggleHelp");
            AssertThat(controls.Visible).IsTrue(); AssertThat(ui.Visible).IsTrue(); AssertThat(ui.HelpOpen).IsTrue(); AssertThat(reticle.Visible).IsFalse();
            Call("SetUiHidden", true);
            AssertThat(ui.HelpOpen).IsFalse(); AssertThat(ui.Visible).IsFalse(); AssertThat(reticle.IsVisibleInTree()).IsTrue();
            Set("_room", false);
            Call("UpdateReticle"); AssertThat(reticle.IsVisibleInTree()).IsTrue();
            Call("SetUiHidden", false); AssertThat(reticle.Visible).IsFalse();
            Call("SetUiHidden", true); AssertThat(reticle.IsVisibleInTree()).IsTrue();
            Engine_UiInputMode.KeyboardMouse = true;
            Call("UpdateReticle"); AssertThat(reticle.Visible).IsFalse(); AssertThat(ui.Visible).IsFalse();
            Set("_room", true);
            Call("SetUiHidden", false); AssertThat(reticle.Visible).IsFalse();
            AssertThat(GameDecorate.KeyLabel("RS")).IsEqual("U");
            AssertThat(ui.PanelBounds.Size.Y).IsEqual(280f);
            AssertThat(ui.Hit(new Vector2(50, -64))).IsEqual(Engine_UiDecorate.PageSize + 6);
            AssertThat(ui.Hit(new Vector2(50, -22))).IsEqual(Engine_UiDecorate.PageSize + 7);
            ui.ShowRows(new[] { "Chair", "Table" }, 1, false, false);
            AssertThat(ui.PanelBounds.Size.Y).IsEqual(640f);
            AssertThat(ui.Hit(new Vector2(-100, 139))).IsEqual(1);
            ui.ShowRows(new[] { "Chair", "Table" }, 1, false, true);
            AssertThat(ui.PanelBounds.Size.Y).IsEqual(400f);
            AssertThat(ui.Hit(new Vector2(-126, 60))).IsEqual(Engine_UiDecorate.PageSize + 10);
            AssertThat(ui.Hit(new Vector2(0, 60))).IsEqual(Engine_UiDecorate.PageSize + 11);
            AssertThat(ui.Hit(new Vector2(126, 60))).IsEqual(Engine_UiDecorate.PageSize + 12);
            AssertThat(ui.Position.Y - ui.PanelBounds.Size.Y / 2).IsEqual(-340f);
            ui.ShowRotation(25.5f); ui.ShowRotation("-90.1", true);
            AssertThat(ui.Hit(new Vector2(-100, 139))).IsEqual(-1);
            AssertThat(ui.Hit(new Vector2(50, 20))).IsEqual(Engine_UiDecorate.PageSize + 1);
        }
        finally { Engine_UiInputMode.KeyboardMouse = keyboard; root.Free(); }
    }
}
