using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Hero;

/// <summary>
/// What the guest's morph weights are computed FROM: the CECStats percentages (Tan +0x34,
/// Fat +0x38, Height +0x3C, Strength +0x40, Skill +0x44, Will +0x48), morality and purity
/// normalised over their ranges, and CECAge's years.
/// </summary>
public enum HeroMorphInput
{
    Tan,
    Fat,
    Height,
    Strength,
    Skill,
    Will,
    Morality,
    Purity,
    Age,
}

public sealed class HeroMorphStats
{
    public float Tan { get; set; } = 0.5f;

    public float Fat { get; set; }

    public float Height { get; set; }

    public float Strength { get; set; }

    public float Skill { get; set; }

    public float Will { get; set; }

    public float Morality { get; set; } = 0.5f;

    public float Purity { get; set; } = 0.5f;

    public float Age { get; set; } = 30.0f;

    public float this[HeroMorphInput input]
    {
        get => input switch
        {
            HeroMorphInput.Tan => Tan,
            HeroMorphInput.Fat => Fat,
            HeroMorphInput.Height => Height,
            HeroMorphInput.Strength => Strength,
            HeroMorphInput.Skill => Skill,
            HeroMorphInput.Will => Will,
            HeroMorphInput.Morality => Morality,
            HeroMorphInput.Purity => Purity,
            HeroMorphInput.Age => Age,
            _ => 0.0f,
        };
        set
        {
            switch (input)
            {
                case HeroMorphInput.Tan: Tan = value; break;
                case HeroMorphInput.Fat: Fat = value; break;
                case HeroMorphInput.Height: Height = value; break;
                case HeroMorphInput.Strength: Strength = value; break;
                case HeroMorphInput.Skill: Skill = value; break;
                case HeroMorphInput.Will: Will = value; break;
                case HeroMorphInput.Morality: Morality = value; break;
                case HeroMorphInput.Purity: Purity = value; break;
                case HeroMorphInput.Age: Age = value; break;
            }
        }
    }
}

public static class Engine_HeroMorph
{
    /// <summary>The part's own mesh blended with every listed target at its guest weight.</summary>
    public static ArrayMesh? Blend(
        string neutralPath,
        IReadOnlyList<(uint Type, string Path)> targets,
        HeroMorphStats stats,
        Func<string, ArrayMesh?> resolve)
    {
        ArrayMesh? neutral = resolve(neutralPath);
        if (neutral is null)
        {
            return null;
        }

        var listed = new List<(ArrayMesh Target, float Weight)>();

        foreach ((uint type, string path) in targets)
        {
            float weight = Engine_HeroMorphState.Weight(stats, type);
            if (weight > WeightFloor && resolve(path) is ArrayMesh target)
            {
                listed.Add((target, weight));
            }
        }

        return Blend(neutral, listed);
    }

    /// <summary>The guest drops a target at or below this weight (<c>sub_8220DE88</c>, 0x8220e2a0).</summary>
    public const float WeightFloor = 0.0001f;

    /// <summary>
    /// The guest's blend: <c>sum(w_i * v_i)</c> with the part's own model at <c>1 - sum</c>,
    /// positions and normals, normals renormalised. A target whose mesh count differs leaves
    /// the whole part unmorphed (<c>sub_82AB12F8</c>); a mesh whose vertex count differs is
    /// left as the part's own.
    /// </summary>
    public static ArrayMesh Blend(
        ArrayMesh neutral, IReadOnlyList<(ArrayMesh Target, float Weight)> targets)
    {
        int surfaces = neutral.GetSurfaceCount();
        if (surfaces == 0 || targets.Count == 0)
        {
            return neutral;
        }

        foreach ((ArrayMesh target, _) in targets)
        {
            if (target.GetSurfaceCount() != surfaces)
            {
                return neutral;
            }
        }

        var result = new ArrayMesh();
        bool blended = false;

        for (int s = 0; s < surfaces; s++)
        {
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(neutral, s);
            Vector3[] origin = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] originNormals =
                arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.PackedVector3Array
                    ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array()
                    : Array.Empty<Vector3>();

            var others = new List<(Vector3[] Positions, Vector3[] Normals, float Weight)>();
            bool matches = origin.Length != 0;

            foreach ((ArrayMesh target, float weight) in targets)
            {
                Godot.Collections.Array other = Refii.Models.Engine_MdlSurfaceArrays.Of(target, s);
                Vector3[] positions = other[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                if (positions.Length != origin.Length)
                {
                    matches = false;
                    break;
                }

                Vector3[] normals =
                    other[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.PackedVector3Array
                        ? other[(int)Mesh.ArrayType.Normal].AsVector3Array()
                        : Array.Empty<Vector3>();

                others.Add((positions, normals, weight));
            }

            if (matches)
            {
                var positions = (Vector3[])origin.Clone();
                var normals = (Vector3[])originNormals.Clone();
                bool normalsMatch = normals.Length != 0;

                foreach ((Vector3[] targetPositions, Vector3[] targetNormals, float weight) in others)
                {
                    for (int v = 0; v < positions.Length; v++)
                    {
                        positions[v] += (targetPositions[v] - origin[v]) * weight;
                    }

                    if (targetNormals.Length != originNormals.Length)
                    {
                        normalsMatch = false;
                    }
                }

                if (normalsMatch)
                {
                    foreach ((_, Vector3[] targetNormals, float weight) in others)
                    {
                        for (int v = 0; v < normals.Length; v++)
                        {
                            normals[v] += (targetNormals[v] - originNormals[v]) * weight;
                        }
                    }

                    for (int v = 0; v < normals.Length; v++)
                    {
                        normals[v] = normals[v].LengthSquared() > 1e-12f
                            ? normals[v].Normalized()
                            : originNormals[v];
                    }

                    arrays[(int)Mesh.ArrayType.Normal] = normals;
                }

                arrays[(int)Mesh.ArrayType.Vertex] = positions;
                blended = true;
            }

            Refii.Models.Engine_MdlSurfaceArrays.Add(result, neutral.SurfaceGetPrimitiveType(s), arrays,
                Refii.Models.Engine_MdlMesh.SurfaceLods(neutral, s));
            result.SurfaceSetName(s, neutral.SurfaceGetName(s));
            result.SurfaceSetMaterial(s, neutral.SurfaceGetMaterial(s));
        }

        if (!blended)
        {
            return neutral;
        }

        foreach (StringName meta in neutral.GetMetaList())
        {
            result.SetMeta(meta, neutral.GetMeta(meta));
        }

        return result;
    }
}
