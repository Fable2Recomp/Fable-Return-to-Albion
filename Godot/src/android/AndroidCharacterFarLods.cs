using System;
using System.Collections.Generic;
using Refii.Core;
using Refii.Models;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_character_far_lod_cm`: two much coarser levels after a character surface's own, made
/// by clustering its vertices on a grid of this many centimetres and 2.5 times that. 0 is off.
/// </summary>
/// <remarks>
/// <para>WHY. A townsperson's coarsest shipped level is still 2,500 to 4,500 triangles (head
/// 600-1,000, torso 700, legs 530, hat 380, ears and nose 200), and on Low (mesh_lod_pixels 4) every
/// villager past about 10 m already draws it: at 20 m on a 40% render of an S10e that body is about
/// 28 x 8 pixels, some 15 triangles a pixel, and the Adreno 640 pays its binning and its vertex
/// outputs per triangle and vertex, not per pixel. Nothing coarser exists: meshoptimizer
/// (`mesh_auto_lod`, `mesh_auto_lod_far`) keeps every open border locked, and garment parts are all
/// border, so even its 20% level of a villager is 2,200 triangles.</para>
/// <para>HOW. Vertex clustering, the far-level method that ignores borders: every vertex the
/// coarsest level uses falls in a grid cell, and a cell's vertices that also share a UV cell (an
/// eighth of the texture each way) become one new vertex at the CELL's mean position, so the two
/// sides of a seam still meet; it takes the UV, tangent, skin weights and source number of the
/// member nearest that mean and the members' mean normal. A triangle whose corners land in fewer
/// than three cells is dropped, as is a repeat. The new vertices are appended to the surface as a
/// shipped skinned level's own are (Engine_MdlLods), so the bones move them like any other.</para>
/// <para>KEYED BY THE CELL, in metres, as an error the shared `mesh_lod_pixels` rule switches by (a
/// 5 cm cell on Low at about 6.4 m, the 12.5 cm one at about 16 m), and at least 1.25 times the key
/// before it so a shipped level keeps its distance. One key for every surface of a part, so the
/// body's surface merge (Engine_BodyVariants) still joins what it joined. A level that saves under
/// 30% of the one before is not kept. Characters only (models with skinned parts), not cloth.
/// A surface that ships no level takes `mesh_auto_lod_skinned`'s generated ones first, when that is
/// on. Applies to models built after the next launch; nothing on the desktop.</para>
/// <para>MEASURED OFFLINE (the globals banks, Engine_MdlMerge + Engine_MdlLods as the game builds
/// them, at 5 cm): a thin townsman 7,820 triangles at LOD 0, 2,505 from 10 m on; with the far levels
/// 1,950 at 12 m, 1,266 at 16 m, 425 from 17 m. Townsfolk past 17 m keep 13-17% of their triangles,
/// children (no shipped level) and the dog drop from 6,100-6,400 to 1,800 past 6.4 m and to 380-650
/// past 16 m. The new vertices add 7-25% to a composite's skinned vertex count.</para>
/// </remarks>
public static class AndroidCharacterFarLods
{
    /// <summary>The refii.toml key: the first cell in centimetres; 0 (off) unless set.</summary>
    public const string Key = "android_character_far_lod_cm";

    /// <summary>The second level's cell against the first's.</summary>
    public const float SecondCellRatio = 2.5f;

    /// <summary>UV cells across the texture each way: vertices in different ones stay apart, so a triangle never spans two UV islands.</summary>
    public const int UvCells = 8;

    /// <summary>A far level keys at least this many times the level before it.</summary>
    public const float KeyStep = 1.25f;

    /// <summary>The smallest and largest first cell, centimetres.</summary>
    public const float MinCentimetres = 1f, MaxCentimetres = 50f;

    private static float _centimetres = -1f;
    private static int _levels;

    /// <summary>Levels added since start-up, for the load log and `charcensus`.</summary>
    public static int LevelsAdded => System.Threading.Volatile.Read(ref _levels);

    /// <summary>The first cell the configuration asks for, centimetres: 0 off, else clamped. Pure.</summary>
    public static float Wanted(IReadOnlyDictionary<string, string> values)
    {
        float cm = Engine_RefiiConfig.Float(values, Key, 0f);
        return cm <= 0f ? 0f : Math.Clamp(cm, MinCentimetres, MaxCentimetres);
    }

    /// <summary>The cells, metres, finest first, for a first cell of <paramref name="centimetres"/>. Pure.</summary>
    public static float[] CellsFor(float centimetres) =>
        centimetres <= 0f ? Array.Empty<float>() : new[] { centimetres / 100f, centimetres / 100f * SecondCellRatio };

    /// <summary>This process's first cell, centimetres; 0 off the phone or unset. Read once.</summary>
    public static float Centimetres
    {
        get
        {
            if (_centimetres < 0f)
            {
                float cm = 0f;
                try
                {
                    cm = Engine_Platform.IsAndroid ? Wanted(Engine_RefiiConfig.Read(out _)) : 0f;
                }
                catch (Exception)
                {
                    cm = 0f;
                }

                _centimetres = cm;
            }

            return _centimetres;
        }
    }

    /// <summary>
    /// The model's far levels onto its surfaces, after its shipped ones are attached
    /// (Engine_MdlMesh). Nothing unless the setting is on and the model has skinned parts.
    /// </summary>
    public static void Append(MdlInfo info, List<MdlGeometry> surfaces)
    {
        float cm = Centimetres;
        if (cm <= 0f || info.SkinnedCount == 0 || Engine_ClassicMaterial.Active)
        {
            return;
        }

        float[] cells = CellsFor(cm);
        foreach (MdlGeometry geo in surfaces)
        {
            // A surface that ships none takes `mesh_auto_lod_skinned`'s levels first, here: once it
            // has far ones, Engine_MdlMesh would no longer ask Engine_MdlExtraLods for any.
            if (geo.Lods.Count == 0 && geo.HasBones && info.MovementMode != 2 && Engine_MdlAutoLod.Skinned)
            {
                Generated(geo);
            }

            System.Threading.Interlocked.Add(ref _levels, Append(geo, cells));
        }
    }

    /// <summary>The generated levels (Engine_MdlAutoLod) onto a surface's own list, as shipped ones would be.</summary>
    private static void Generated(MdlGeometry geo)
    {
        int vertexCount = geo.VertexCount;
        if (geo.VerticesIn is not null || geo.Positions.Length < vertexCount * 3 || geo.Normals.Length < vertexCount * 3)
        {
            return;
        }

        var vertices = new Godot.Vector3[vertexCount];
        var normals = new Godot.Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            vertices[v] = new Godot.Vector3(geo.Positions[v * 3], geo.Positions[v * 3 + 1], geo.Positions[v * 3 + 2]);
            normals[v] = new Godot.Vector3(geo.Normals[v * 3], geo.Normals[v * 3 + 1], geo.Normals[v * 3 + 2]);
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Godot.Mesh.ArrayType.Normal] = normals;
        arrays[(int)Godot.Mesh.ArrayType.Index] = Array.ConvertAll(geo.Indices, i => (int)i);
        if (Engine_MdlAutoLod.Generate(vertices, geo.Indices.Length, arrays) is not { } levels)
        {
            return;
        }

        var found = new List<(float Key, uint[] Indices)>();
        foreach (KeyValuePair<Godot.Variant, Godot.Variant> level in levels.Lods)
        {
            found.Add((level.Key.AsSingle(), Array.ConvertAll(level.Value.AsInt32Array(), i => (uint)i)));
        }

        found.Sort((a, b) => a.Key.CompareTo(b.Key));
        geo.Lods.AddRange(found);
    }

    /// <summary>Adds the far levels for <paramref name="cells"/> to one surface; returns how many were kept.</summary>
    internal static int Append(MdlGeometry geo, float[] cells)
    {
        if (!geo.HasBones || geo.ClothRecord >= 0 || geo.VerticesIn is not null || geo.Indices.Length < 3)
        {
            return 0;
        }

        int vertexCount = geo.VertexCount;
        if (geo.Positions.Length < vertexCount * 3 || geo.Uvs.Length < vertexCount * 2 ||
            geo.BoneIds.Length < vertexCount * 4 || geo.BoneWeights.Length < vertexCount * 4)
        {
            return 0;
        }

        // The coarsest level there is: the clustering only needs the cells it covers.
        uint[] source = geo.Lods.Count != 0 ? geo.Lods[^1].Indices : geo.Indices;
        float lastKey = geo.Lods.Count != 0 ? geo.Lods[^1].EdgeLength : 0f;
        int previous = source.Length;
        int kept = 0;
        foreach (float cell in cells)
        {
            Clustered level = Cluster(geo.Positions, geo.Normals, geo.Uvs, source, vertexCount, cell);
            if (level.Indices.Length < 3 || level.Indices.Length > previous * 7 / 10)
            {
                continue;
            }

            int baseVertex = geo.VertexCount;
            AppendVertices(geo, level);
            var indices = new uint[level.Indices.Length];
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = (uint)(baseVertex + level.Indices[i]);
            }

            float key = MathF.Max(cell, lastKey * KeyStep);
            geo.Lods.Add((key, indices));
            lastKey = key;
            previous = indices.Length;
            kept++;
        }

        return kept;
    }

    /// <summary>A clustered level: its new vertices (each from a representative surface vertex) and its triangles over them.</summary>
    public sealed class Clustered
    {
        /// <summary>Per new vertex: the surface vertex it copies everything but the position and normal from.</summary>
        public int[] Representatives = Array.Empty<int>();

        /// <summary>Per new vertex, xyz.</summary>
        public float[] Positions = Array.Empty<float>();

        /// <summary>Per new vertex, xyz, unit length.</summary>
        public float[] Normals = Array.Empty<float>();

        /// <summary>Triangles over the new vertices, in the source's winding.</summary>
        public int[] Indices = Array.Empty<int>();
    }

    /// <summary>
    /// The clustered level of <paramref name="indices"/> on a grid of <paramref name="cell"/> metres.
    /// Pure.
    /// </summary>
    /// <param name="positions">The surface's positions, xyz per vertex.</param>
    /// <param name="normals">Its normals, xyz per vertex, or empty.</param>
    /// <param name="uvs">Its UVs, two per vertex.</param>
    /// <param name="indices">The triangles to simplify, over the first <paramref name="vertexCount"/> vertices.</param>
    public static Clustered Cluster(
        ReadOnlySpan<float> positions, ReadOnlySpan<float> normals, ReadOnlySpan<float> uvs,
        ReadOnlySpan<uint> indices, int vertexCount, float cell)
    {
        var result = new Clustered();
        if (!(cell > 0f) || indices.Length < 3)
        {
            return result;
        }

        bool hasNormals = normals.Length >= vertexCount * 3;
        float inverse = 1f / cell;
        var cellOf = new Dictionary<(int, int, int), int>();
        var keyOf = new Dictionary<(int Cell, int U, int V), int>();
        var vertexCluster = new int[vertexCount];
        var vertexCell = new int[vertexCount];
        Array.Fill(vertexCluster, -1);
        var cellSums = new List<(double X, double Y, double Z, int N)>();
        var clusterCell = new List<int>();
        var clusterNormal = new List<(float X, float Y, float Z)>();
        var clusterRep = new List<int>();
        var clusterRepDistance = new List<float>();

        int end = indices.Length - (indices.Length % 3);
        for (int i = 0; i < end; i++)
        {
            int v = (int)indices[i];
            if ((uint)v >= (uint)vertexCount || vertexCluster[v] >= 0)
            {
                continue;
            }

            float x = positions[v * 3], y = positions[v * 3 + 1], z = positions[v * 3 + 2];
            var at = ((int)MathF.Floor(x * inverse), (int)MathF.Floor(y * inverse), (int)MathF.Floor(z * inverse));
            if (!cellOf.TryGetValue(at, out int c))
            {
                c = cellSums.Count;
                cellOf[at] = c;
                cellSums.Add((0, 0, 0, 0));
            }

            (double sx, double sy, double sz, int n) = cellSums[c];
            cellSums[c] = (sx + x, sy + y, sz + z, n + 1);
            vertexCell[v] = c;

            float u = uvs[v * 2], w = uvs[v * 2 + 1];
            var key = (c, UvCell(u), UvCell(w));
            if (!keyOf.TryGetValue(key, out int k))
            {
                k = clusterCell.Count;
                keyOf[key] = k;
                clusterCell.Add(c);
                clusterNormal.Add((0f, 0f, 0f));
                clusterRep.Add(v);
                clusterRepDistance.Add(float.MaxValue);
            }

            vertexCluster[v] = k;
            if (hasNormals)
            {
                (float nx, float ny, float nz) = clusterNormal[k];
                clusterNormal[k] = (nx + normals[v * 3], ny + normals[v * 3 + 1], nz + normals[v * 3 + 2]);
            }
        }

        // Each cluster's representative: its member nearest the cell's mean.
        for (int v = 0; v < vertexCount; v++)
        {
            int k = vertexCluster[v];
            if (k < 0)
            {
                continue;
            }

            (double sx, double sy, double sz, int n) = cellSums[vertexCell[v]];
            float dx = positions[v * 3] - (float)(sx / n), dy = positions[v * 3 + 1] - (float)(sy / n), dz = positions[v * 3 + 2] - (float)(sz / n);
            float d = dx * dx + dy * dy + dz * dz;
            if (d < clusterRepDistance[k])
            {
                clusterRepDistance[k] = d;
                clusterRep[k] = v;
            }
        }

        var triangles = new HashSet<(int, int, int)>();
        var output = new List<int>();
        var newIndex = new int[clusterCell.Count];
        Array.Fill(newIndex, -1);
        var used = new List<int>();
        for (int i = 0; i < end; i += 3)
        {
            int a = (int)indices[i], b = (int)indices[i + 1], c = (int)indices[i + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
            {
                continue;
            }

            int ka = vertexCluster[a], kb = vertexCluster[b], kc = vertexCluster[c];
            int ca = clusterCell[ka], cb = clusterCell[kb], cc = clusterCell[kc];
            if (ca == cb || cb == cc || ca == cc)
            {
                continue;
            }

            // One rotation per winding, so a repeat is found whichever corner it starts at.
            (int, int, int) triangle = ka < kb && ka < kc ? (ka, kb, kc) : kb < kc ? (kb, kc, ka) : (kc, ka, kb);
            if (!triangles.Add(triangle))
            {
                continue;
            }

            foreach (int k in new[] { ka, kb, kc })
            {
                if (newIndex[k] < 0)
                {
                    newIndex[k] = used.Count;
                    used.Add(k);
                }

                output.Add(newIndex[k]);
            }
        }

        result.Indices = output.ToArray();
        result.Representatives = new int[used.Count];
        result.Positions = new float[used.Count * 3];
        result.Normals = new float[used.Count * 3];
        for (int n = 0; n < used.Count; n++)
        {
            int k = used[n];
            int rep = clusterRep[k];
            result.Representatives[n] = rep;
            (double sx, double sy, double sz, int count) = cellSums[clusterCell[k]];
            result.Positions[n * 3] = (float)(sx / count);
            result.Positions[n * 3 + 1] = (float)(sy / count);
            result.Positions[n * 3 + 2] = (float)(sz / count);
            (float nx, float ny, float nz) = clusterNormal[k];
            float length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 1e-6f)
            {
                (nx, ny, nz) = (nx / length, ny / length, nz / length);
            }
            else if (hasNormals)
            {
                (nx, ny, nz) = (normals[rep * 3], normals[rep * 3 + 1], normals[rep * 3 + 2]);
            }
            else
            {
                (nx, ny, nz) = (0f, 1f, 0f);
            }

            result.Normals[n * 3] = nx;
            result.Normals[n * 3 + 1] = ny;
            result.Normals[n * 3 + 2] = nz;
        }

        return result;
    }

    private static int UvCell(float value) =>
        float.IsFinite(value) ? (int)Math.Clamp(MathF.Floor(value * UvCells), -1_000_000f, 1_000_000f) : 0;

    /// <summary>The level's new vertices onto the surface's arrays, every array the surface carries per vertex.</summary>
    private static void AppendVertices(MdlGeometry geo, Clustered level)
    {
        int baseCount = geo.VertexCount;
        int[] reps = level.Representatives;

        geo.Positions = Grow(geo.Positions, 3, baseCount, level.Positions);
        if (geo.Normals.Length >= baseCount * 3)
        {
            geo.Normals = Grow(geo.Normals, 3, baseCount, level.Normals);
        }

        geo.Uvs = Copy(geo.Uvs, 2, baseCount, reps);
        if (geo.Tangents.Length >= baseCount * 4)
        {
            geo.Tangents = Copy(geo.Tangents, 4, baseCount, reps);
        }

        geo.BoneIds = Copy(geo.BoneIds, 4, baseCount, reps);
        geo.BoneWeights = Copy(geo.BoneWeights, 4, baseCount, reps);

        // The source map keeps its length equal to the vertex count, or it is not a map at all.
        if (geo.SourceVertices.Length == baseCount)
        {
            geo.SourceVertices = Copy(geo.SourceVertices, 1, baseCount, reps);
        }
    }

    private static float[] Grow(float[] head, int width, int baseCount, float[] tail)
    {
        var result = new float[baseCount * width + tail.Length];
        Array.Copy(head, result, Math.Min(head.Length, baseCount * width));
        Array.Copy(tail, 0, result, baseCount * width, tail.Length);
        return result;
    }

    private static T[] Copy<T>(T[] head, int width, int baseCount, int[] reps)
    {
        var result = new T[(baseCount + reps.Length) * width];
        Array.Copy(head, result, Math.Min(head.Length, baseCount * width));
        for (int n = 0; n < reps.Length; n++)
        {
            Array.Copy(head, reps[n] * width, result, (baseCount + n) * width, width);
        }

        return result;
    }
}
