using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Models;

namespace Refii.Level;

public sealed class PropBuildStats
{
    public int SaveEntities { get; set; }

    public int Placements { get; set; }

    public int Skeletal { get; set; }

    public int Unnamed { get; set; }

    public int NoModelHash { get; set; }

    public int UnresolvedModel { get; set; }

    public int RejectedCharacter { get; set; }

    public int DuplicateRecord { get; set; }

    public int DuplicateTransform { get; set; }

    public int SupplementalHits { get; set; }

    public int Instances { get; set; }

    public int Blocks { get; set; }

    public int EngineLevelBlocks { get; set; }

    public int EngineLevelInstances { get; set; }

    public int EngineLevelUnresolved { get; set; }

    public int EngineLevelType21Blocks { get; set; }

    public string EngineLevelError { get; set; } = string.Empty;

    public override string ToString() =>
        $"save={SaveEntities} placements={Placements} instances={Instances} " +
        $"blocks={Blocks} supplemental={SupplementalHits} " +
        $"skipped(skeletal={Skeletal} unnamed={Unnamed} nomodel={NoModelHash} " +
        $"unresolved={UnresolvedModel} character={RejectedCharacter} " +
        $"duprecord={DuplicateRecord} duptransform={DuplicateTransform}) " +
        $"engine_level(blocks={EngineLevelBlocks} inst={EngineLevelInstances} " +
        $"unresolved={EngineLevelUnresolved} type21={EngineLevelType21Blocks})" +
        (EngineLevelError.Length == 0 ? string.Empty : $" [{EngineLevelError}]");
}

public static class Engine_LevelProps
{
    public const string NodeName = "Props";

    public const float ModelReach = 64.0f;

    public static readonly string[] SupplementalGdbPaths =
    {
        "globals/globals.gdb",
        "speechaction/speechaction.gdb",
        "interactivecutscenes/interactivecutscenes.gdb",
        "entity/entity.gdb",
        "environmentthemes/environmentthemes.gdb",
    };

    public static Node3D? Build(
        LevelEntry level, Func<string, byte[]?> read, out PropBuildStats stats)
    {
        stats = new PropBuildStats();

        byte[]? gdbBytes = Engine_LevelGdb.TryRead(level, read, out string gdbPath);
        if (gdbBytes is null)
        {
            return null;
        }

        if (!GdbView.TryParse(gdbBytes, out GdbView view, out string why))
        {
            GD.Print($"[props] {level}: level gdb unreadable ({why})");
            return null;
        }

        string savePath = Engine_LevelGdb.SiblingWithExtension(gdbPath, ".save");
        List<KeyValuePair<uint, string>> saveMap = Engine_GdbSaveMap.Parse(read(savePath));
        stats.SaveEntities = saveMap.Count;

        List<GdbPlacement> placements = Engine_GdbPlacements.Parse(view, saveMap);
        AppendSupplementalPlacements(read, saveMap, placements, stats);
        stats.Placements = placements.Count;

        var blocks = new Dictionary<string, PropBlockBuild>();
        var recordKeys = new HashSet<string>();
        var transformKeys = new HashSet<string>();
        var order = new List<string>();

        AppendEngineLevelBlocks(
            read, Engine_LevelGdb.SiblingWithExtension(gdbPath, ".engine_level"),
            blocks, order, transformKeys, stats);

        foreach (GdbPlacement placement in placements)
        {
            if (placement.SkeletonFileHash != 0 || placement.RetargetSkeletonFileHash != 0)
            {
                stats.Skeletal++;
                continue;
            }

            if (placement.ModelPathHash == 0)
            {
                if (placement.EntityName.Length == 0)
                {
                    stats.Unnamed++;
                }
                else
                {
                    stats.NoModelHash++;
                }

                continue;
            }

            string? modelPath = Engine_PropModelIndex.Resolve(
                placement.ModelPathHash, Array.Empty<string>());

            if (modelPath is null)
            {
                stats.UnresolvedModel++;
                continue;
            }

            if (IsCharacterModel(modelPath))
            {
                stats.RejectedCharacter++;
                continue;
            }

            if (!recordKeys.Add(RecordKey(placement, modelPath)))
            {
                stats.DuplicateRecord++;
                continue;
            }

            Transform3D transform = BuildTransform(placement);
            if (!transformKeys.Add(TransformKey(transform, modelPath)))
            {
                stats.DuplicateTransform++;
                continue;
            }

            string key = "prop|" + modelPath;
            if (!blocks.TryGetValue(key, out PropBlockBuild? block))
            {
                block = new PropBlockBuild(modelPath, $"prop {NameOf(modelPath)}");
                blocks[key] = block;
                order.Add(key);
            }

            block.Instances.Add(transform);
            stats.Instances++;
        }

        if (stats.Instances == 0)
        {
            return null;
        }

        var container = new Engine_PropStreamer { Name = NodeName };

        foreach (string key in order)
        {
            PropBlockBuild block = blocks[key];

            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                InstanceCount = block.Instances.Count,
            };

            var bounds = new Aabb(block.Instances[0].Origin, Vector3.Zero);
            for (int i = 0; i < block.Instances.Count; i++)
            {
                multiMesh.SetInstanceTransform(i, block.Instances[i]);
                bounds = bounds.Expand(block.Instances[i].Origin);
            }

            multiMesh.CustomAabb = bounds.Grow(ModelReach);

            var node = new MultiMeshInstance3D
            {
                Name = UniqueName(container, block.Label),
                Multimesh = multiMesh,
            };

            node.SetMeta(Engine_PropStreamer.ModelPathMeta, block.ModelPath);
            container.AddChild(node);

            stats.Blocks++;
        }

        if (container.GetChildCount() == 0)
        {
            container.QueueFree();
            return null;
        }

        return container;
    }

    private sealed class PropBlockBuild
    {
        public PropBlockBuild(string modelPath, string label)
        {
            ModelPath = modelPath;
            Label = label;
        }

        public string ModelPath { get; }

        public string Label { get; }

        public List<Transform3D> Instances { get; } = new();
    }

    private static void AppendEngineLevelBlocks(
        Func<string, byte[]?> read,
        string enginePath,
        Dictionary<string, PropBlockBuild> blocks,
        List<string> order,
        HashSet<string> transformKeys,
        PropBuildStats stats)
    {
        byte[]? bytes = read(enginePath);
        if (bytes is null)
        {
            return;
        }

        EngineLevelFile file = Engine_EngineLevelFile.Parse(bytes);
        stats.EngineLevelType21Blocks = file.Type21Blocks;

        if (!file.Ok)
        {
            stats.EngineLevelError = file.Error;
            return;
        }

        if (file.StoppedAtType != 0)
        {
            stats.EngineLevelError = $"stopped at unknown type {file.StoppedAtType}";
        }

        foreach (EngineLevelBlock block in file.PropBlocks)
        {
            string? modelPath = Engine_PropModelIndex.ResolveByPath(block.ModelPath);
            if (modelPath is null)
            {
                stats.EngineLevelUnresolved += block.Instances.Count;
                continue;
            }

            string key = $"engine|{stats.EngineLevelBlocks}|{modelPath}";
            var build = new PropBlockBuild(
                modelPath, $"engine_level {NameOf(modelPath)}");

            int emitted = 0;
            foreach (EngineLevelInstance instance in block.Instances)
            {
                Transform3D transform = BuildEngineLevelTransform(instance.Values);
                if (!transformKeys.Add(TransformKey(transform, modelPath)))
                {
                    continue;
                }

                build.Instances.Add(transform);
                emitted++;
            }

            if (emitted == 0)
            {
                continue;
            }

            blocks[key] = build;
            order.Add(key);

            stats.EngineLevelInstances += emitted;
            stats.Instances += emitted;
            stats.EngineLevelBlocks++;
        }
    }

    public static Transform3D BuildEngineLevelTransform(float[] values)
    {
        float sx = values[9] == 0f ? 1f : values[9];
        float sy = values[10] == 0f ? sx : values[10];
        float sz = values[11] == 0f ? sx : values[11];

        return new Transform3D(
            YawScaleBasis(values[6], values[7], sx, sy, sz),
            new Vector3(values[0], values[2], values[1]));
    }

    private static void AppendSupplementalPlacements(
        Func<string, byte[]?> read,
        List<KeyValuePair<uint, string>> saveMap,
        List<GdbPlacement> placements,
        PropBuildStats stats)
    {
        if (saveMap.Count == 0)
        {
            return;
        }

        var views = new List<GdbView>();
        foreach (string path in SupplementalGdbPaths)
        {
            byte[]? bytes = read(path) ?? read("data/" + path);
            if (bytes is not null && GdbView.TryParse(bytes, out GdbView view, out _))
            {
                views.Add(view);
            }
        }

        if (views.Count == 0)
        {
            return;
        }

        var placed = new HashSet<uint>();
        foreach (GdbPlacement placement in placements)
        {
            if (placement.Hash != 0)
            {
                placed.Add(placement.Hash);
            }
        }

        var appended = new List<GdbPlacement>();

        foreach (KeyValuePair<uint, string> entity in saveMap)
        {
            if (entity.Key == 0 || placed.Contains(entity.Key))
            {
                continue;
            }

            foreach (GdbView view in views)
            {
                if (!Engine_GdbPlacements.TryLookupPlacement(
                        view, entity.Key, entity.Value, true, out GdbPlacement placement) ||
                    placement.ModelPathHashes.Count == 0)
                {
                    continue;
                }

                placed.Add(entity.Key);
                stats.SupplementalHits++;

                if (placement.ModelPathHashes.Count > 1)
                {
                    foreach (uint hash in placement.ModelPathHashes)
                    {
                        GdbPlacement part = placement.Clone();
                        part.ModelPathHash = hash;
                        part.ModelPathHashes = new List<uint> { hash };
                        appended.Add(part);
                    }
                }
                else
                {
                    appended.Add(placement);
                }

                break;
            }
        }

        placements.AddRange(appended);
    }

    public static Transform3D BuildTransform(GdbPlacement placement)
    {
        float scale = float.IsFinite(placement.Scale) &&
                      placement.Scale > 0.01f && placement.Scale < 100f
            ? placement.Scale
            : 1f;

        Basis basis = placement.HasRotation
            ? RotationBasis(placement.RotX, placement.RotY, placement.RotZ, scale)
            : YawBasis(placement.Yaw, scale);

        return new Transform3D(basis, new Vector3(placement.X, placement.Z, placement.Y));
    }

    public static Basis RotationBasis(float rx, float ry, float rz, float scale)
    {
        if (!float.IsFinite(rx))
        {
            rx = 0f;
        }

        if (!float.IsFinite(ry))
        {
            ry = 0f;
        }

        if (!float.IsFinite(rz))
        {
            rz = 0f;
        }

        float sx = MathF.Sin(rx);
        float cx = MathF.Cos(rx);
        float sy = MathF.Sin(ry);
        float cy = MathF.Cos(ry);
        float sz = MathF.Sin(rz);
        float cz = MathF.Cos(rz);

        var game = new float[9];
        game[0] = cy * cx;
        game[1] = sx;
        game[2] = -sy * cx;
        game[3] = (sy * sz) - (cy * cz * sx);
        game[4] = cz * cx;
        game[5] = (sy * cz * sx) + (cy * sz);
        game[6] = (cy * sz * sx) + (sy * cz);
        game[7] = -sz * cx;
        game[8] = (cy * cz) - (sy * sz * sx);

        var map = new[] { 0, 2, 1 };
        var m = new float[9];
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
            {
                m[(row * 3) + col] = game[(map[row] * 3) + map[col]] * scale;
            }
        }

        return ToGodotBasis(m);
    }

    public static Basis YawBasis(float yaw, float scale)
    {
        float s = MathF.Sin(yaw);
        float c = MathF.Cos(yaw);

        if (!float.IsFinite(s) || !float.IsFinite(c))
        {
            s = 0f;
            c = 1f;
        }

        return YawScaleBasis(s, c, scale, scale, scale);
    }

    public static Basis YawScaleBasis(float s, float c, float sx, float sy, float sz)
    {
        if (!float.IsFinite(s) || !float.IsFinite(c))
        {
            s = 0f;
            c = 1f;
        }

        return ToGodotBasis(new[]
        {
            c * sx, 0f, s * sy,
            0f, sz, 0f,
            -s * sx, 0f, c * sy,
        });
    }

    private static Basis ToGodotBasis(float[] m) => new(
        new Vector3(m[0], m[3], m[6]),
        new Vector3(m[1], m[4], m[7]),
        new Vector3(-m[2], -m[5], -m[8]));

    public static bool IsCharacterModel(string modelPath)
    {
        string path = modelPath.Replace('\\', '/').ToLowerInvariant();
        return path.StartsWith("art/characters/", StringComparison.Ordinal) ||
               path.Contains("/art/characters/", StringComparison.Ordinal) ||
               path.Contains("/characters/heros/", StringComparison.Ordinal);
    }

    public static string LeafOf(string path)
    {
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? path : path[(slash + 1)..];
    }

    public static string NameOf(string path)
    {
        string leaf = LeafOf(path);
        int dot = leaf.LastIndexOf('.');
        return dot <= 0 ? leaf : leaf[..dot];
    }

    private static string RecordKey(GdbPlacement placement, string modelPath) =>
        $"{modelPath.ToLowerInvariant()}|{placement.Hash:x}|{placement.ParentHash:x}|" +
        $"{placement.EntityName}|{Quantise(placement.X)},{Quantise(placement.Y)}," +
        $"{Quantise(placement.Z)}";

    private static string TransformKey(Transform3D transform, string modelPath)
    {
        Basis basis = transform.Basis;
        Vector3 origin = transform.Origin;

        return $"{modelPath.ToLowerInvariant()}|" +
               $"{Quantise(origin.X)}|{Quantise(origin.Y)}|{Quantise(origin.Z)}|" +
               $"{Quantise(basis.X.X)}|{Quantise(basis.X.Y)}|{Quantise(basis.X.Z)}|" +
               $"{Quantise(basis.Y.X)}|{Quantise(basis.Y.Y)}|{Quantise(basis.Y.Z)}|" +
               $"{Quantise(basis.Z.X)}|{Quantise(basis.Z.Y)}|{Quantise(basis.Z.Z)}";
    }

    private static long Quantise(float value) =>
        float.IsFinite(value) ? (long)MathF.Round(value * 100f) : 0L;

    private static string UniqueName(Node parent, string name)
    {
        if (parent.GetNodeOrNull(name) is null)
        {
            return name;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (parent.GetNodeOrNull(candidate) is null)
            {
                return candidate;
            }
        }
    }
}
