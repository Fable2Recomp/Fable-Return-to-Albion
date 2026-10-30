using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Level;

public sealed class Engine_DecorateCatalog
{
    public sealed record Item(uint Card, uint Definition, int Type, int Level, string Model);
    public const string Setting = "decorate_mode_enabled";
    public readonly Dictionary<uint, Item> Items = new();
    public readonly Dictionary<uint, string> Models = new();
    public readonly HashSet<uint> Furniture = new();
    public readonly Dictionary<uint, int> FurnitureTypes = new();
    public readonly Dictionary<uint, string[]> Movable = new();
    public static Engine_DecorateCatalog? Current { get; private set; }
    public static readonly string[] Types = { "Object", "Single bed", "Double bed", "Wardrobe", "Armchair",
        "Cupboard", "Drawers", "Dresser", "Sink", "Stove", "Dining table", "Bedside table", "Table", "Decoration" };
    private static readonly string[] Physics = { "PhysicsSimulationStaticComponent", "PhysicsSimulationKeyframedComponent", "PhysicsSimulationDynamicComponent" };
    private static readonly string[] FixedComponents = { "BuildingComponent", "DoorComponent", "BuildingSaleSignComponent",
        "LevelExitComponent", "CreatureComponent", "SwitchComponent", "TriggerComponent", "WorkplaceComponent",
        "FlameTrapComponent", "LightningTrapComponent", "VaultLineComponent", "ClimbableComponent" };
    private static readonly string[] StructureNames = { "door", "fireplace", "hearth", "chimney", "stair", "ceiling",
        "roof", "pillar", "column", "archway", "building", "window", "banister", "railing", "fence", "gate", "ladder" };
    private static uint H(string name) => Engine_GameHash.Fnv1(name);
    public static string DefinitionName(uint card) => $"RefiiDecorate_{card:X8}";
    public static string CardName(uint card) => $"RefiiDecorateCard_{card:X8}";
    public static string MovableName(uint definition, int level) => $"RefiiDecorateMoved_{definition:X8}_{level}";

    public static bool Field(GdbMulti db, GdbCursor cursor, string field, out uint value) =>
        db.TryFindInherited(cursor, H(field), -1, out _, out value, out _);

    public static bool Component(GdbMulti db, GdbCursor cursor, string field, out GdbCursor component)
    {
        component = default;
        return Field(db, cursor, field, out uint hash) && hash != 0 && hash != GdbHash.Null && db.TryLookup(hash, out component);
    }

    public static bool IsDecorationModel(string model)
    {
        string path = model.Replace('\\', '/').ToLowerInvariant();
        if (!path.EndsWith(".mdl", StringComparison.Ordinal) ||
            !path.Contains("/props/", StringComparison.Ordinal) && !path.Contains("/furniture/", StringComparison.Ordinal)) return false;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (string structure in StructureNames)
            if (name.Contains(structure, StringComparison.Ordinal)) return false;
        return !name.Contains("_wall_", StringComparison.Ordinal) && !name.Contains("_floor_", StringComparison.Ordinal);
    }

    public static Engine_DecorateCatalog Build(GdbMulti db, IReadOnlyDictionary<uint, string> models, GdbPatchDatabase patch)
    {
        var result = new Engine_DecorateCatalog();
        foreach (var model in models) result.Models[model.Key] = model.Value;
        var templates = new Dictionary<uint, GdbCursor>();
        var all = new Dictionary<uint, GdbCursor>();
        foreach (var view in db.Views)
            for (int i = 0; i < view.RecordCount; i++) all[view.RecordHash(i)] = new GdbCursor(view, i);
        foreach (var pair in all.OrderBy(p => p.Key))
        {
            if (!Component(db, pair.Value, "UpgradeableFurnitureComponent", out var furniture) ||
                !Field(db, furniture, "Type", out uint type) || type == 0 || type > 12 ||
                (Field(db, furniture, "NotActuallyForUpgrades", out uint fake) && fake != 0)) continue;
            result.Furniture.Add(pair.Key);
            result.FurnitureTypes[pair.Key] = (int)type;
            templates.TryAdd(type, pair.Value);
        }
        var allowed = new HashSet<uint>(new[] { "WhereaboutsComponent", "MapwhoComponent", "ObjectComponent",
            "GraphicAppearanceComponent", "GraphicAppearanceStaticMeshComponent", "GraphicAppearanceAnimatedMeshComponent",
            "PhysicsSimulationStaticComponent", "PhysicsSimulationKeyframedComponent", "PhysicsSimulationDynamicComponent",
            "OwnedEntityComponent", "TargetedComponent", "UpgradeableFurnitureComponent",
            "OnActionUseComponent", "BedComponent", "AnimationManagerComponent", "ActionManagerSimpleComponent",
            "SoundComponent", "NetworkComponent", "SpellNotHeatableComponent" }.Select(H));
        foreach (uint definition in result.Furniture.Order().ToArray())
        {
            var source = all[definition];
            Component(db, source, "UpgradeableFurnitureComponent", out var upgrade);
            bool threeLevels = Component(db, upgrade, "FurnitureLevels", out var levels) &&
                Field(db, levels, "Level_3", out _) && !Field(db, levels, "Level_4", out _);
            var names = new string[5];
            for (int level = 1; level <= names.Length; level++)
            {
                string name = MovableName(definition, level);
                names[level - 1] = name;
                string upgradeName = name + "_Upgrade";
                var component = new GdbPatchEntry(H(upgradeName), 1);
                foreach (var field in db.InheritedFields(upgrade))
                    if (field.Hash != H("BaseLevel")) component.Properties.Add(new(field.Hash, field.Type, field.Raw));
                component.Properties.Add(new(H("BaseLevel"), GdbFieldType.Int, threeLevels && level == 3 ? 5u : (uint)level));
                var entity = new GdbPatchEntry(H(name), 1);
                foreach (var field in db.InheritedFields(source))
                    if (allowed.Contains(field.Hash)) entity.Properties.Add(new(field.Hash, field.Type,
                        field.Hash == H("UpgradeableFurnitureComponent") ? component.Id : field.Raw));
                patch.Entries.Add(component);
                patch.Entries.Add(entity);
                patch.Names[component.Id] = upgradeName;
                patch.Names[entity.Id] = name;
                patch.Aliases.Add((entity.Id, entity.Id));
                result.Furniture.Add(entity.Id);
                result.FurnitureTypes[entity.Id] = result.FurnitureTypes[definition];
            }
            result.Movable[definition] = names;
        }
        foreach (var pair in all.OrderBy(p => p.Key))
        {
            if (result.Furniture.Contains(pair.Key) || !Component(db, pair.Value, "ObjectComponent", out _) ||
                !Component(db, pair.Value, "GraphicAppearanceStaticMeshComponent", out var appearance) ||
                !Field(db, appearance, "ModelFile", out uint model) || !models.TryGetValue(model, out string? path) ||
                !IsDecorationModel(path) || FixedComponents.Any(slot => Component(db, pair.Value, slot, out _))) continue;
            bool dynamic = Component(db, pair.Value, "PhysicsSimulationDynamicComponent", out _) ||
                Component(db, pair.Value, "PhysicsSimulationKeyframedComponent", out _);
            bool stateful = Component(db, pair.Value, "InventoryComponent", out _) ||
                Component(db, pair.Value, "ChestComponent", out _) || Component(db, pair.Value, "InventoryItemComponent", out _) ||
                Component(db, pair.Value, "CrateComponent", out _) || Component(db, pair.Value, "TrophyDisplayComponent", out _);
            if (stateful && !dynamic) continue;
            result.Furniture.Add(pair.Key);
            result.FurnitureTypes[pair.Key] = 13;
            if (dynamic) continue;
            string name = MovableName(pair.Key, 1);
            var entity = new GdbPatchEntry(H(name), 1);
            foreach (var field in db.InheritedFields(pair.Value))
                if (field.Hash != H("ManagedLocationComponent")) entity.Properties.Add(new(field.Hash, field.Type, field.Raw));
            patch.Entries.Add(entity);
            patch.Names[entity.Id] = name;
            patch.Aliases.Add((entity.Id, entity.Id));
            result.Movable[pair.Key] = new[] { name };
            result.Furniture.Add(entity.Id);
            result.FurnitureTypes[entity.Id] = 13;
        }
        foreach (var pair in all.OrderBy(p => p.Key))
        {
            if (!Component(db, pair.Value, "FurnitureUnlockComponent", out var unlock) ||
                !Component(db, pair.Value, "InventoryItemComponent", out var inventory) ||
                !Field(db, inventory, "Category", out uint category) || category != 15 ||
                !Field(db, unlock, "Type", out uint type) || !templates.TryGetValue(type, out var template) ||
                !Field(db, unlock, "Level", out uint level) || level is < 1 or > 5 ||
                !Field(db, inventory, "GUIScreenMesh", out uint mesh) || !models.TryGetValue(mesh, out string? model)) continue;
            string name = DefinitionName(pair.Key);
            var copied = new Dictionary<uint, uint>();
            GdbPatchEntry Copy(GdbCursor source, string targetName, bool root = false)
            {
                uint sourceId = source.View.RecordHash(source.Record);
                var entry = new GdbPatchEntry(H(targetName), 1);
                copied[sourceId] = entry.Id;
                patch.Entries.Add(entry);
                patch.Names[entry.Id] = targetName;
                foreach (var field in db.InheritedFields(source))
                {
                    if (root && !allowed.Contains(field.Hash)) continue;
                    uint value = field.Raw;
                    if (field.Type == GdbFieldType.Reference && value != 0 && value != GdbHash.Null && db.TryLookup(value, out var child))
                    {
                        if (!copied.TryGetValue(value, out uint mapped))
                        {
                            if (copied.Count > 256) throw new InvalidOperationException("Furniture definition contains too many dependencies.");
                            mapped = Copy(child, name + "_" + value.ToString("X8", CultureInfo.InvariantCulture)).Id;
                        }
                        value = mapped;
                    }
                    entry.Properties.Add(new GdbPatchProperty(field.Hash, field.Type, value));
                    string text = db.NameOf(field.Raw);
                    if (text.Length != 0) patch.Names[field.Raw] = text;
                }
                return entry;
            }
            var entity = Copy(template, name, true);
            patch.Aliases.Add((entity.Id, entity.Id));
            void SetComponent(string slot, string field, GdbFieldType fieldType, uint value)
            {
                var property = entity.Properties.FirstOrDefault(p => p.NameHash == H(slot));
                if (property is null) return;
                var component = patch.Entries.First(e => e.Id == property.Value);
                component.Properties.RemoveAll(p => p.NameHash == H(field));
                component.Properties.Add(new GdbPatchProperty(H(field), fieldType, value));
                patch.Names[H(field)] = field;
            }
            SetComponent("UpgradeableFurnitureComponent", "BaseLevel", GdbFieldType.Int, level);
            SetComponent("GraphicAppearanceStaticMeshComponent", "ModelFile", GdbFieldType.String, mesh);
            SetComponent("GraphicAppearanceAnimatedMeshComponent", "ModelFile", GdbFieldType.String, mesh);
            string collision = System.IO.Path.ChangeExtension(model, ".hkx");
            uint collisionHash = Engine_GameHash.ResourcePath(collision);
            if (!models.ContainsKey(collisionHash) && model.EndsWith("_static.mdl", StringComparison.OrdinalIgnoreCase))
            {
                collision = model[..^11] + ".hkx";
                collisionHash = Engine_GameHash.ResourcePath(collision);
            }
            if (models.ContainsKey(collisionHash))
            {
                foreach (string slot in Physics) SetComponent(slot, "PhysicsFile", GdbFieldType.String, collisionHash);
                patch.Names[collisionHash] = collision.Replace('/', '\\').ToLowerInvariant();
            }
            patch.Names[mesh] = model.Replace('/', '\\').ToLowerInvariant();
            if (!patch.Entries.Any(e => e.Id == pair.Key))
            {
                var cardEntry = new GdbPatchEntry(pair.Key, 1);
                foreach (var field in pair.Value.View.RawFields(pair.Value.Record))
                    cardEntry.Properties.Add(new GdbPatchProperty(field.Hash, field.Type, field.Raw));
                patch.Entries.Add(cardEntry);
            }
            string cardName = CardName(pair.Key);
            patch.Names[H(cardName)] = cardName;
            patch.Aliases.Add((H(cardName), pair.Key));
            result.Items.Add(pair.Key, new Item(pair.Key, entity.Id, (int)type, (int)level, model));
            result.Furniture.Add(entity.Id);
            result.FurnitureTypes[entity.Id] = (int)type;
        }
        return result;
    }

    public string LuaCatalog()
    {
        var text = new StringBuilder("RefiiDecorateCatalog = {\n");
        foreach (var item in Items.Values)
            text.AppendFormat(CultureInfo.InvariantCulture, "[\"0x{0:x8}\"]={{definition=\"{1}\",inventory=\"{2}\",kind={3}}},\n", item.Card, DefinitionName(item.Card), CardName(item.Card), item.Type);
        text.Append("}\nRefiiDecorateFurniture = {\n");
        foreach (uint definition in Furniture.Order())
            text.AppendFormat(CultureInfo.InvariantCulture, "[\"0x{0:x8}\"]={1},\n", definition, FurnitureTypes[definition]);
        text.Append("}\nRefiiDecorateMovable = {\n");
        foreach (var pair in Movable.OrderBy(p => p.Key))
        {
            text.AppendFormat(CultureInfo.InvariantCulture, "[\"0x{0:x8}\"]={{", pair.Key);
            foreach (string name in pair.Value) text.Append('"').Append(name).Append("\",");
            text.Append("},\n");
        }
        return text.Append("}\n").ToString();
    }

    public static void Attach(BnkFileSystem mounted)
    {
        byte[]? Read(string path) => mounted.TryGetFile(path, out var file) ? file.ReadAllBytes() : null;
        var blobs = Engine_GameGdb.LoadSupplementalBlobs(Read);
        var paths = mounted.EnumerateAllFiles().Select(f => f.VirtualPath).ToArray();
        foreach (string path in paths.Where(p => p.StartsWith("worlds/", StringComparison.OrdinalIgnoreCase) &&
            (p.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.EndsWith(".tu", StringComparison.OrdinalIgnoreCase))) blobs.Add(Read(path));
        var db = GdbMulti.Build(blobs);
        var models = new Dictionary<uint, string>();
        foreach (string path in paths.Where(p => p.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase)))
            models.TryAdd(Engine_GameHash.ResourcePath(path), path);
        const string target = "globals/globals.gdb.tu";
        var patch = GdbPatchBuilder.LoadOrCreatePatchDatabase(Read(target));
        Current = Build(db, models, patch);
        byte[] bytes = GdbWriter.Serialize(patch);
        mounted.AddVirtualFile(target, target, bytes.Length, () => bytes, "Refii");
        Engine_DecorateRuntime.Attach(mounted, Current.LuaCatalog());
    }
}
