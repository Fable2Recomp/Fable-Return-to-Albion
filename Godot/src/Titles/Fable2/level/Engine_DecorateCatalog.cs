using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Titles.Fable2;

using Refii.Level;

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
        byte[]? shipped = Read(target);

        // THE ANSWER IS KEPT ON DISK. Building it looks at all of a million records several times,
        // seconds on a phone at every boot, and it changes only when the data under it does.
        //
        // IT IS NOT SKIPPED WHEN THE MODE IS OFF. The setting only stops new placing: a save's layout
        // is restored and kept without it (the script's restore, GetSaveTable), and a host's placed
        // furniture reaches a joiner as a record id in the create message, whatever the joiner's own
        // setting. Every machine has to hold these records.
        byte[] key = CacheKey(blobs, models, shipped);
        string cache = System.IO.Path.Combine(Engine_Title.Fable2.CacheDirectory, "decorate", "catalog.bin");
        if (!TryReadCache(cache, key, models, out Engine_DecorateCatalog? catalog, out byte[] bytes, out string lua))
        {
            var patch = GdbPatchBuilder.LoadOrCreatePatchDatabase(shipped);
            catalog = Build(GdbMulti.Build(blobs), models, patch);
            bytes = GdbWriter.Serialize(patch);
            lua = catalog.LuaCatalog();
            WriteCache(cache, key, catalog, bytes, lua);
        }

        Current = catalog;
        mounted.AddVirtualFile(target, target, bytes.Length, () => bytes, "Refii");
        Engine_DecorateRuntime.Attach(mounted, lua);
    }

    /// <summary>Raise when <see cref="Build"/> or the file below changes in a way the build stamp would not show.</summary>
    private const int CacheFormat = 2;

    /// <summary>Everything <see cref="Build"/> reads, and the build of the code that reads it.</summary>
    internal static byte[] CacheKey(IReadOnlyList<byte[]?> blobs, IReadOnlyDictionary<uint, string> models, byte[]? shipped)
    {
        using var hash = Engine_Hash.CreateIncrementalSha256();
        void AddNumber(long value)
        {
            Span<byte> number = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, value);
            hash.AppendData(number);
        }

        void AddBytes(byte[]? bytes)
        {
            AddNumber(bytes?.LongLength ?? -1);
            hash.AppendData(bytes);
        }

        AddNumber(CacheFormat);
        AddBytes(typeof(Engine_DecorateCatalog).Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        AddNumber(blobs.Count);
        foreach (byte[]? blob in blobs) AddBytes(blob);
        AddBytes(shipped);
        AddNumber(models.Count);
        foreach (var model in models)
        {
            AddNumber(model.Key);
            AddBytes(Encoding.UTF8.GetBytes(model.Value));
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// The patch and Lua from <paramref name="path"/>, when made from the same input and whole: a
    /// damaged patch would be different game data. The catalog read holds what the mode's screens
    /// read (<see cref="Items"/>, <see cref="Models"/>); the tables behind the Lua are not made.
    /// </summary>
    internal static bool TryReadCache(
        string path, byte[] key, IReadOnlyDictionary<uint, string> models,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Engine_DecorateCatalog? catalog, out byte[] patch, out string lua)
    {
        catalog = null;
        patch = Array.Empty<byte>();
        lua = string.Empty;
        try
        {
            if (!System.IO.File.Exists(path))
            {
                return false;
            }

            byte[] file = System.IO.File.ReadAllBytes(path);
            int body = key.Length + CacheDigestLength;
            if (file.Length < body || !file.AsSpan(0, key.Length).SequenceEqual(key) ||
                !file.AsSpan(key.Length, CacheDigestLength).SequenceEqual(Engine_Hash.Sha256(file.AsSpan(body))))
            {
                return false;
            }

            using var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(file, body, file.Length - body));
            var read = new Engine_DecorateCatalog();
            foreach (var model in models) read.Models[model.Key] = model.Value;
            byte[] readPatch = reader.ReadBytes(reader.ReadInt32());
            string readLua = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));
            for (int i = reader.ReadInt32(); i > 0; i--)
            {
                var item = new Item(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadString());
                read.Items.Add(item.Card, item);
            }

            catalog = read;
            patch = readPatch;
            lua = readLua;
            return true;
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException or OverflowException or FormatException)
        {
            // Unreadable: built again and written over.
            return false;
        }
    }

    private const int CacheDigestLength = 32;

    /// <summary>Not being able to keep it only means the next boot builds it too.</summary>
    internal static void WriteCache(string path, byte[] key, Engine_DecorateCatalog catalog, byte[] patch, string lua)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var body = new System.IO.MemoryStream();
            using (var writer = new System.IO.BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(patch.Length);
                writer.Write(patch);
                byte[] text = Encoding.UTF8.GetBytes(lua);
                writer.Write(text.Length);
                writer.Write(text);
                writer.Write(catalog.Items.Count);
                foreach (Item item in catalog.Items.Values)
                {
                    writer.Write(item.Card);
                    writer.Write(item.Definition);
                    writer.Write(item.Type);
                    writer.Write(item.Level);
                    writer.Write(item.Model);
                }
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            using (System.IO.FileStream file = System.IO.File.Create(temporary))
            {
                file.Write(key);
                file.Write(Engine_Hash.Sha256(body.GetBuffer().AsSpan(0, (int)body.Length)));
                file.Write(body.GetBuffer(), 0, (int)body.Length);
            }

            System.IO.File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            try
            {
                System.IO.File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is System.IO.IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
