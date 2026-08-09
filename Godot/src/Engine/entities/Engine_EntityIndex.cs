using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;
using Refii.Models;

namespace Refii.Entities;

public static class Engine_EntityIndex
{
    public const string Folder = "entities";

    public const string Extension = ".entity";

    private static Func<string, byte[]?>? _resolver;

    private static GdbMulti? _multi;

    private static List<EntityEntry>? _entries;

    private static Dictionary<string, EntityEntry>? _byFileName;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        _resolver = resolver;
        Invalidate();
    }

    public static void Invalidate()
    {
        _multi = null;
        _entries = null;
        _byFileName = null;
    }

    public static IReadOnlyList<EntityEntry> Entries
    {
        get
        {
            Build();
            return _entries ?? new List<EntityEntry>();
        }
    }

    public static GdbMulti? Database
    {
        get
        {
            Build();
            return _multi;
        }
    }

    public static string FileNameOf(EntityEntry entry)
    {
        var text = new System.Text.StringBuilder(entry.Name.Length + 12);
        foreach (char c in entry.Name)
        {
            text.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' ? c : '_');
        }

        string name = text.ToString().Trim();
        if (name.Length == 0)
        {
            name = "entity";
        }

        return $"{name} [{entry.EntityHash:X8}]{Extension}";
    }

    public static string VirtualPathOf(EntityEntry entry) =>
        $"{Folder}/{FileNameOf(entry)}";

    public static bool TryGet(string virtualPath, out EntityEntry entry)
    {
        entry = null!;
        Build();

        if (_byFileName is null)
        {
            return false;
        }

        string path = virtualPath.Replace('\\', '/');
        int slash = path.LastIndexOf('/');
        string leaf = slash < 0 ? path : path[(slash + 1)..];

        return _byFileName.TryGetValue(leaf, out entry!);
    }

    private static void Build()
    {
        if (_entries is not null || _resolver is null)
        {
            return;
        }

        var blobs = new List<byte[]?>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            blobs.Add(_resolver(path) ?? _resolver("data/" + path));
        }

        _multi = GdbMulti.Build(blobs);
        _entries = Engine_EntityCatalog.Build(_multi);

        var map = new Dictionary<string, EntityEntry>();
        foreach (EntityEntry entry in _entries)
        {
            map[FileNameOf(entry)] = entry;
        }

        _byFileName = map;

        GD.Print($"[entities] indexed {_entries.Count} entit(ies) " +
            $"from {_multi.Views.Count} database(s), {_multi.RecordCount:N0} records");
    }

    public static Engine_EntityResource? BuildResource(string virtualPath)
    {
        if (!TryGet(virtualPath, out EntityEntry entry) || Database is null)
        {
            return null;
        }

        GdbMulti multi = Database;
        var resource = new Engine_EntityResource
        {
            EntityName = entry.Name,
            EntityHash = $"0x{entry.EntityHash:X8}",
            NameTag = entry.NameTagHash == 0 ? string.Empty : multi.NameOf(entry.NameTagHash),
        };

        foreach (uint modelHash in entry.ModelHashes)
        {
            string? path = Engine_PropModelIndex.Resolve(modelHash, Array.Empty<string>());
            if (path is null)
            {
                resource.MissingModels.Add($"0x{modelHash:X8}");
                continue;
            }

            resource.ModelPaths.Add(path);
            AppendSurfaces(resource, path);
        }

        FillGdbFields(multi, entry, resource);
        return resource;
    }

    private static void AppendSurfaces(Engine_EntityResource resource, string modelPath)
    {
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(modelPath);
        if (bytes is null)
        {
            return;
        }

        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(bytes, modelPath, out _, out _);
        if (mesh is null)
        {
            return;
        }

        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            resource.AddSurfaceFromArrays(
                mesh.SurfaceGetPrimitiveType(surface), mesh.SurfaceGetArrays(surface));

            int added = resource.GetSurfaceCount() - 1;
            resource.SurfaceSetName(added, mesh.SurfaceGetName(surface));
            resource.SurfaceSetMaterial(added, mesh.SurfaceGetMaterial(surface));
        }
    }

    private static void FillGdbFields(
        GdbMulti multi, EntityEntry entry, Engine_EntityResource resource)
    {
        if (!multi.TryLookup(entry.EntityHash, out GdbCursor cursor))
        {
            return;
        }

        CollectFields(multi, cursor, resource.GdbFields, resource.Components);

        if (multi.TryLookup(entry.CreatureComponent, out GdbCursor creature))
        {
            CollectFields(multi, creature, resource.CreatureFields, null);
        }

        EntityGameplay? gameplay = Engine_EntityGameplay.Extract(multi, entry.EntityHash);
        if (gameplay is null)
        {
            return;
        }

        resource.Faction = gameplay.FactionName;
        resource.CombatProfile = gameplay.CombatProfileName;

        resource.Stats.AddRange(gameplay.CoreFields);
        resource.Combat.AddRange(gameplay.CombatFields);
    }

    private static void CollectFields(
        GdbMulti multi,
        GdbCursor start,
        List<KeyValuePair<string, string>> fields,
        List<string>? components)
    {
        var seen = new HashSet<string>();

        GdbCursor current = start;
        var visited = new HashSet<uint>();

        for (int depth = 0; depth < 64 && current.Valid; depth++)
        {
            foreach (GdbField field in current.View.Fields(current.Record))
            {
                if (field.Hash == GdbView.HashParent)
                {
                    continue;
                }

                string name = field.Name.Length != 0
                    ? field.Name
                    : $"0x{field.Hash:X8}";

                if (seen.Add(name))
                {
                    fields.Add(new KeyValuePair<string, string>(name, field.Describe()));
                }

                if (components is not null && field.IsReference &&
                    name.EndsWith("Component", StringComparison.OrdinalIgnoreCase) &&
                    !components.Contains(name))
                {
                    components.Add(name);
                }
            }

            if (!current.View.TryFindLocalRaw(
                    current.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || !visited.Add(parent) ||
                !multi.TryLookup(parent, out current))
            {
                return;
            }
        }
    }
}
