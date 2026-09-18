using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Hero;

public enum HeroMorphKind
{
    Strong,
    Fat,
    Tall,
    Will,
    Young,
    Old,
    GoodPure,
    Impure,
    EvilPure,
    EvilImpure,
}

public sealed class HeroMorphWeights
{
    public float Strong { get; set; }

    public float Fat { get; set; }

    public float Young { get; set; }

    public float Old { get; set; }

    public float Impure { get; set; }

    public float EvilPure { get; set; }

    public float EvilImpure { get; set; }

    public float Tall { get; set; }

    public float Will { get; set; }

    public float GoodPure { get; set; }

    public float this[HeroMorphKind kind] => kind switch
    {
        HeroMorphKind.Strong => Strong,
        HeroMorphKind.Fat => Fat,
        HeroMorphKind.Young => Young,
        HeroMorphKind.Old => Old,
        HeroMorphKind.Impure => Impure,
        HeroMorphKind.EvilPure => EvilPure,
        HeroMorphKind.EvilImpure => EvilImpure,
        HeroMorphKind.Tall => Tall,
        HeroMorphKind.Will => Will,
        HeroMorphKind.GoodPure => GoodPure,
        _ => 0.0f,
    };

    public bool Any
    {
        get
        {
            foreach (HeroMorphKind kind in Enum.GetValues<HeroMorphKind>())
            {
                if (this[kind] > 0.0001f)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

public static class Engine_HeroMorph
{
    public static string TokenOf(HeroMorphKind kind) => kind switch
    {
        HeroMorphKind.Strong => "morph_strong",
        HeroMorphKind.Fat => "morph_fat",
        HeroMorphKind.Young => "morph_young",
        HeroMorphKind.Old => "morph_old",
        HeroMorphKind.Impure => "morph_impure",
        HeroMorphKind.EvilPure => "morph_evilpure",
        HeroMorphKind.EvilImpure => "morph_evilimpure",
        HeroMorphKind.GoodPure => "morph_goodpure",
        _ => string.Empty,
    };

    public static bool TopologyMatches(ArrayMesh neutral, ArrayMesh target)
    {
        int surfaces = neutral.GetSurfaceCount();
        if (surfaces == 0 || target.GetSurfaceCount() != surfaces)
        {
            return false;
        }

        for (int s = 0; s < surfaces; s++)
        {
            int a = neutral.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex]
                .AsVector3Array().Length;
            int b = target.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex]
                .AsVector3Array().Length;

            if (a == 0 || a != b)
            {
                return false;
            }
        }

        return true;
    }

    public static ArrayMesh? Blend(
        string neutralPath,
        IReadOnlyDictionary<HeroMorphKind, string> targets,
        HeroMorphWeights weights,
        Func<string, ArrayMesh?> resolve)
    {
        ArrayMesh? neutral = resolve(neutralPath);
        if (neutral is null || !weights.Any)
        {
            return neutral;
        }

        int surfaces = neutral.GetSurfaceCount();
        var arrays = new Godot.Collections.Array[surfaces];
        var positions = new Vector3[surfaces][];
        var normals = new Vector3[surfaces][];
        var origin = new Vector3[surfaces][];
        var originNormals = new Vector3[surfaces][];

        for (int s = 0; s < surfaces; s++)
        {
            arrays[s] = neutral.SurfaceGetArrays(s);
            positions[s] = arrays[s][(int)Mesh.ArrayType.Vertex].AsVector3Array();
            origin[s] = (Vector3[])positions[s].Clone();

            normals[s] = arrays[s][(int)Mesh.ArrayType.Normal].VariantType ==
                Variant.Type.PackedVector3Array
                ? arrays[s][(int)Mesh.ArrayType.Normal].AsVector3Array()
                : Array.Empty<Vector3>();

            originNormals[s] = (Vector3[])normals[s].Clone();
        }

        bool blended = false;

        foreach (HeroMorphKind kind in Enum.GetValues<HeroMorphKind>())
        {
            float weight = Engine_HeroMorphState.VertexWeight(weights, kind);
            if (weight <= 0.0001f || !targets.TryGetValue(kind, out string? path))
            {
                continue;
            }

            ArrayMesh? target = resolve(path);
            if (target is null || target.GetSurfaceCount() != surfaces)
            {
                continue;
            }

            for (int s = 0; s < surfaces; s++)
            {
                Godot.Collections.Array other = target.SurfaceGetArrays(s);
                Vector3[] targetPositions =
                    other[(int)Mesh.ArrayType.Vertex].AsVector3Array();

                if (targetPositions.Length != origin[s].Length)
                {
                    continue;
                }

                for (int v = 0; v < targetPositions.Length; v++)
                {
                    positions[s][v] += (targetPositions[v] - origin[s][v]) * weight;
                }

                if (normals[s].Length == 0 ||
                    other[(int)Mesh.ArrayType.Normal].VariantType !=
                        Variant.Type.PackedVector3Array)
                {
                    blended = true;
                    continue;
                }

                Vector3[] targetNormals = other[(int)Mesh.ArrayType.Normal].AsVector3Array();
                if (targetNormals.Length != originNormals[s].Length)
                {
                    continue;
                }

                for (int v = 0; v < targetNormals.Length; v++)
                {
                    normals[s][v] += (targetNormals[v] - originNormals[s][v]) * weight;
                }

                blended = true;
            }
        }

        if (!blended)
        {
            return neutral;
        }

        var result = new ArrayMesh();

        for (int s = 0; s < surfaces; s++)
        {
            arrays[s][(int)Mesh.ArrayType.Vertex] = positions[s];

            if (normals[s].Length != 0)
            {
                for (int v = 0; v < normals[s].Length; v++)
                {
                    normals[s][v] = normals[s][v].LengthSquared() > 1e-12f
                        ? normals[s][v].Normalized()
                        : originNormals[s][v];
                }

                arrays[s][(int)Mesh.ArrayType.Normal] = normals[s];
            }

            result.AddSurfaceFromArrays(neutral.SurfaceGetPrimitiveType(s), arrays[s], null,
                Refii.Models.Engine_MdlMesh.SurfaceLods(neutral, s));
            result.SurfaceSetName(s, neutral.SurfaceGetName(s));
            result.SurfaceSetMaterial(s, neutral.SurfaceGetMaterial(s));
        }

        foreach (StringName meta in neutral.GetMetaList())
        {
            result.SetMeta(meta, neutral.GetMeta(meta));
        }

        return result;
    }
}
