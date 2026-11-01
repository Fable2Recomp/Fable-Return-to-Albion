using System;
using Godot;

namespace Refii.Lighting;

public struct ProbeSample
{
    public Vector4 R;
    public Vector4 G;
    public Vector4 B;
}

public sealed class LightProbeField
{
    public const float DistanceEpsilon = 9.9999997e-6f;

    public const float ShL0Basis = 0.28209481f;

    public Vector3 Origin { get; set; }

    public Vector3 BoundsSize { get; set; }

    public Vector3 CellExtent { get; set; }

    public int DimX { get; set; }

    public int DimY { get; set; }

    public int DimZ { get; set; }

    public int MaxInternalDepth { get; set; }

    public int ShBands { get; set; }

    public int[] RootPairKeys { get; set; } = Array.Empty<int>();

    public int[] RootPairIds { get; set; } = Array.Empty<int>();

    public uint[] RootLevelStart { get; set; } = Array.Empty<uint>();

    public uint[] LevelOffsets { get; set; } = Array.Empty<uint>();

    public uint[] RootNodeStart { get; set; } = Array.Empty<uint>();

    public short[] NodeDescriptors { get; set; } = Array.Empty<short>();

    public uint[] RootLeafStart { get; set; } = Array.Empty<uint>();

    public byte[] Leaves { get; set; } = Array.Empty<byte>();

    public Vector3[][] Banks { get; set; } = { Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector3>() };

    /// <summary>Fable 3's probe set (.flpb): where each probe sits, in the guest's axes.</summary>
    public Vector3[] ProbePositions { get; set; } = Array.Empty<Vector3>();

    /// <summary>
    /// Fable 3's per-probe order-3 SH, <see cref="ShCoefficients"/> RGB coefficients per probe in
    /// <c>D3DXSHEvalDirection</c> order — the order sub_82CC2360 evaluates them in (it calls the
    /// order-3 evaluator, sub_82EC3568, and weights coefficient i by basis value i).
    /// </summary>
    public Vector3[] ProbeCoefficients { get; set; } = Array.Empty<Vector3>();

    public const int ShCoefficients = 9;

    /// <summary>
    /// Fable 3's cell around each probe: cell i answers with probe i. Each cell is the planes
    /// (xyz normal, w offset) a point must be on the non-negative side of to lie in it.
    /// </summary>
    public Vector4[][] CellPlanes { get; set; } = Array.Empty<Vector4[]>();

    /// <summary>
    /// Fable 3's neighbour list per cell (the .flpb link list, cell+20..+24 once loaded): the
    /// cells whose probes blend with the containing cell's. A negative index is no neighbour.
    /// </summary>
    public int[][] CellLinks { get; set; } = Array.Empty<int[]>();

    public Vector3 CellBoundsMin { get; set; }

    public Vector3 CellBoundsMax { get; set; }

    /// <summary>
    /// The title samples its cells only with probes and MORE than one cell (sub_82CC2588: the
    /// count of 176-byte probes non-zero, then `(cells end - begin) >> 5 > 1`).
    /// </summary>
    public bool HasCells => ProbeCoefficients.Length > 0 && CellPlanes.Length > 1;

    public bool HasData => HasCells ||
        (RootPairKeys.Length > 0 && Banks.Length == 4 && Banks[0].Length > 0 &&
        DimX > 0 && DimY > 0 && DimZ > 0);

    public ProbeSample Sample(Vector3 world)
    {
        var sample = new ProbeSample();
        if (!HasData)
        {
            return sample;
        }

        if (HasCells)
        {
            return SampleCells(world);
        }

        float cellX = CellExtent.X * 32f;
        float cellY = CellExtent.Y * 32f;
        float cellZ = CellExtent.Z * 32f;
        if (cellX <= 0f || cellY <= 0f || cellZ <= 0f)
        {
            return sample;
        }

        int cx = Clamp((int)MathF.Floor((world.X - Origin.X) / cellX), DimX);
        int cy = Clamp((int)MathF.Floor((world.Y - Origin.Y) / cellY), DimY);
        int cz = Clamp((int)MathF.Floor((world.Z - Origin.Z) / cellZ), DimZ);

        int key = cx + (DimX * cy) + (DimX * DimY * cz);

        int root = FindRoot(key);
        if (root < 0)
        {
            return sample;
        }

        var mins = new int[] { 0, 0, 0 };
        var maxs = new int[] { 32, 32, 32 };
        Descend(world, cx, cy, cz, root, 0, mins, maxs, 0, ref sample);
        return sample;
    }

    public Color AmbientAt(Vector3 world, out float energy)
    {
        ProbeSample s = Sample(world);
        float r = MathF.Max(s.R.X * ShL0Basis, 0f);
        float g = MathF.Max(s.G.X * ShL0Basis, 0f);
        float b = MathF.Max(s.B.X * ShL0Basis, 0f);
        float m = MathF.Max(MathF.Max(r, g), b);

        if (m <= 1e-4f)
        {
            energy = 0f;
            return new Color(0f, 0f, 0f);
        }

        energy = MathF.Min(m, 16f);
        return new Color(
            Math.Clamp(r / m, 0f, 1f),
            Math.Clamp(g / m, 0f, 1f),
            Math.Clamp(b / m, 0f, 1f));
    }

    private void Descend(
        Vector3 p, int cx, int cy, int cz, int root, int depth,
        int[] mins, int[] maxs, int selector, ref ProbeSample sample)
    {
        long descIndex = RootNodeStart[root] + selector;
        if (descIndex < 0 || descIndex >= NodeDescriptors.Length)
        {
            return;
        }

        short desc = NodeDescriptors[descIndex];

        if (desc >= 0)
        {
            Vector3 a = CornerWorld(cx, cy, cz, mins[0], mins[1], mins[2]);
            Vector3 b = CornerWorld(cx, cy, cz, maxs[0], maxs[1], maxs[2]);
            Vector3 half = (b - a) * 0.5f;

            int octant =
                (p.X >= a.X + half.X ? 1 : 0) |
                (p.Y >= a.Y + half.Y ? 2 : 0) |
                (p.Z >= a.Z + half.Z ? 4 : 0);

            var nextMin = new int[] { mins[0], mins[1], mins[2] };
            var nextMax = new int[] { maxs[0], maxs[1], maxs[2] };
            int hx = (maxs[0] - mins[0]) >> 1;
            int hy = (maxs[1] - mins[1]) >> 1;
            int hz = (maxs[2] - mins[2]) >> 1;

            if ((octant & 1) != 0) nextMin[0] += hx; else nextMax[0] = mins[0] + hx;
            if ((octant & 2) != 0) nextMin[1] += hy; else nextMax[1] = mins[1] + hy;
            if ((octant & 4) != 0) nextMin[2] += hz; else nextMax[2] = mins[2] + hz;

            long levelIndex = RootLevelStart[root] + depth;
            if (levelIndex < 0 || levelIndex >= LevelOffsets.Length)
            {
                return;
            }

            int next = (int)LevelOffsets[levelIndex] + desc + octant;
            Descend(p, cx, cy, cz, root, depth + 1, nextMin, nextMax, next, ref sample);
            return;
        }

        long leafIndex = (long)RootLeafStart[root] - desc - 1;
        long leafByte = leafIndex * 32;
        if (leafIndex < 0 || leafByte + 32 > Leaves.Length)
        {
            return;
        }

        var accR = new float[4];
        var accG = new float[4];
        var accB = new float[4];
        float weightSum = 0f;

        for (int corner = 0; corner < 8; corner++)
        {
            int lx = (corner & 1) != 0 ? maxs[0] : mins[0];
            int ly = (corner & 2) != 0 ? maxs[1] : mins[1];
            int lz = (corner & 4) != 0 ? maxs[2] : mins[2];

            Vector3 cornerWorld = CornerWorld(cx, cy, cz, lx, ly, lz);
            Vector3 d = cornerWorld - p;
            float d2 = (d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z);
            float w = 1f / MathF.Max(DistanceEpsilon, d2 * d2);

            for (int j = 0; j < 4; j++)
            {
                byte idx = Leaves[leafByte + (corner * 4) + j];
                Vector3[] bank = Banks[j];
                if (idx >= bank.Length)
                {
                    continue;
                }

                Vector3 rgb = bank[idx];
                accR[j] += w * rgb.X;
                accG[j] += w * rgb.Y;
                accB[j] += w * rgb.Z;
            }

            weightSum += w;
        }

        if (weightSum <= 0f)
        {
            return;
        }

        float inv = 1f / weightSum;
        sample.R = new Vector4(accR[0] * inv, accR[1] * inv, accR[2] * inv, accR[3] * inv);
        sample.G = new Vector4(accG[0] * inv, accG[1] * inv, accG[2] * inv, accG[3] * inv);
        sample.B = new Vector4(accB[0] * inv, accB[1] * inv, accB[2] * inv, accB[3] * inv);
    }

    /// <summary>
    /// Fable 3's lookup: the containing cell's probe blended with its linked neighbours' by
    /// inverse square distance.
    /// </summary>
    /// <remarks>
    /// THE CELL. sub_821C1A20 with no cached cell: outside the half-open box of every cell vertex
    /// (<c>p &lt; min</c> or <c>p &gt;= max</c> on any axis) answers nothing; otherwise the cells are
    /// tried in file order and a cell holds the point when no plane gives
    /// <c>dot(plane, (p, 1)) &lt; 0</c> (sub_821C19B0, 0x821c19ec..0x821c19fc); a cell with no
    /// faces holds nothing. The title also keeps the last cell per object and tries it and its
    /// neighbours first — that only finds the same cell sooner, so it is not repeated here.
    ///
    /// THE BLEND. sub_821C22B0 (0x821c231c..0x821c247c): no containing cell writes nothing; else
    /// the containing cell's probe at weight <c>1 / |p - probe.pos|^2</c> (position at record+144,
    /// 0x821c237c..0x821c2394) plus every neighbour the cell links to at cell+20..+24, indices
    /// &gt;= 0 only (0x821c23e4..0x821c2430), at the same rule, divided by the summed weight
    /// (sub_821C18D8 at 0x821c246c; sub_821C24A0 scales a record, sub_82CC1C50 adds 9 float4).
    /// The first four coefficients are the L0/L1 the sample carries, in the order the Fable 2 banks
    /// already use.
    /// </remarks>
    private ProbeSample SampleCells(Vector3 p)
    {
        var sample = new ProbeSample();
        if (p.X < CellBoundsMin.X || p.X >= CellBoundsMax.X ||
            p.Y < CellBoundsMin.Y || p.Y >= CellBoundsMax.Y ||
            p.Z < CellBoundsMin.Z || p.Z >= CellBoundsMax.Z)
        {
            return sample;
        }

        int containing = -1;
        for (int cell = 0; cell < CellPlanes.Length; cell++)
        {
            Vector4[] planes = CellPlanes[cell];
            if (planes.Length == 0)
            {
                continue;
            }

            bool inside = true;
            foreach (Vector4 plane in planes)
            {
                if ((plane.X * p.X) + (plane.Y * p.Y) + (plane.Z * p.Z) + plane.W < 0f)
                {
                    inside = false;
                    break;
                }
            }

            if (inside)
            {
                containing = cell;
                break;
            }
        }

        if (containing < 0)
        {
            return sample;
        }

        var r = Vector4.Zero;
        var g = Vector4.Zero;
        var b = Vector4.Zero;
        float summed = 0f;
        AccumulateProbe(containing, p, ref r, ref g, ref b, ref summed);
        if (containing < CellLinks.Length)
        {
            foreach (int neighbour in CellLinks[containing])
            {
                if (neighbour >= 0 && neighbour != containing)
                {
                    AccumulateProbe(neighbour, p, ref r, ref g, ref b, ref summed);
                }
            }
        }

        if (summed <= 0f)
        {
            return sample;
        }

        float inv = 1f / summed;
        sample.R = r * inv;
        sample.G = g * inv;
        sample.B = b * inv;
        return sample;
    }

    /// <summary>One probe's L0/L1 at the guest's inverse-square-distance weight (sub_821C22B0).</summary>
    private void AccumulateProbe(int probe, Vector3 p, ref Vector4 r, ref Vector4 g, ref Vector4 b, ref float summed)
    {
        int first = probe * ShCoefficients;
        if (probe < 0 || probe >= ProbePositions.Length || first + 3 >= ProbeCoefficients.Length)
        {
            return;
        }

        Vector3 d = ProbePositions[probe] - p;
        float w = 1f / MathF.Max((d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z), DistanceEpsilon);
        Vector3 c0 = ProbeCoefficients[first];
        Vector3 c1 = ProbeCoefficients[first + 1];
        Vector3 c2 = ProbeCoefficients[first + 2];
        Vector3 c3 = ProbeCoefficients[first + 3];
        r += new Vector4(c0.X, c1.X, c2.X, c3.X) * w;
        g += new Vector4(c0.Y, c1.Y, c2.Y, c3.Y) * w;
        b += new Vector4(c0.Z, c1.Z, c2.Z, c3.Z) * w;
        summed += w;
    }

    private Vector3 CornerWorld(int cx, int cy, int cz, int lx, int ly, int lz) =>
        new(
            Origin.X + (CellExtent.X * ((32 * cx) + lx)),
            Origin.Y + (CellExtent.Y * ((32 * cy) + ly)),
            Origin.Z + (CellExtent.Z * ((32 * cz) + lz)));

    private int FindRoot(int key)
    {
        int lo = 0;
        int hi = RootPairKeys.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (RootPairKeys[mid] < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (lo >= RootPairKeys.Length || RootPairKeys[lo] != key)
        {
            return -1;
        }

        int root = RootPairIds[lo];
        return root >= 0 && root < RootNodeStart.Length ? root : -1;
    }

    private static int Clamp(int value, int dim)
    {
        if (value < 0)
        {
            return 0;
        }

        return value >= dim ? dim - 1 : value;
    }
}

public static class Engine_LightProbeField
{
    public static bool TryParse(byte[] bytes, out LightProbeField field, out string error)
    {
        field = new LightProbeField();
        error = string.Empty;

        var reader = new BeReader(bytes);

        if (!ReadVec3(reader, out Vector3 origin) ||
            !ReadVec3(reader, out Vector3 boundsSize) ||
            !ReadVec3(reader, out Vector3 cellExtent))
        {
            error = "truncated reading lattice header";
            return false;
        }

        field.Origin = origin;
        field.BoundsSize = boundsSize;
        field.CellExtent = cellExtent;

        if (!reader.U32(out uint dimX) || !reader.U32(out uint dimY) || !reader.U32(out uint dimZ) ||
            !reader.U32(out uint maxDepth) || !reader.U32(out uint shBands))
        {
            error = "truncated reading dims";
            return false;
        }

        field.DimX = (int)dimX;
        field.DimY = (int)dimY;
        field.DimZ = (int)dimZ;
        field.MaxInternalDepth = (int)maxDepth;
        field.ShBands = (int)shBands;

        if (!reader.U32(out uint rootCount))
        {
            error = "truncated reading root count";
            return false;
        }

        var keys = new int[rootCount];
        var ids = new int[rootCount];
        for (uint i = 0; i < rootCount; i++)
        {
            if (!reader.U32(out uint keyRaw) || !reader.U32(out uint idRaw))
            {
                error = $"truncated root pair {i}";
                return false;
            }

            keys[i] = unchecked((int)keyRaw);
            ids[i] = unchecked((int)idRaw);
        }

        SortPairs(keys, ids);
        field.RootPairKeys = keys;
        field.RootPairIds = ids;

        if (!reader.U32Array(rootCount, out uint[] rootLevelStart))
        {
            error = "truncated reading root level starts";
            return false;
        }

        field.RootLevelStart = rootLevelStart;

        if (!reader.U32(out uint levelCount) || !reader.U32Array(levelCount, out uint[] levelOffsets))
        {
            error = "truncated reading level offsets";
            return false;
        }

        field.LevelOffsets = levelOffsets;

        if (!reader.U32Array(rootCount, out uint[] rootNodeStart))
        {
            error = "truncated reading root node starts";
            return false;
        }

        field.RootNodeStart = rootNodeStart;

        if (!reader.U32(out uint nodeCount) || !ReadS16Array(reader, nodeCount, out short[] descriptors))
        {
            error = "truncated reading node descriptors";
            return false;
        }

        field.NodeDescriptors = descriptors;

        if (!reader.U32Array(rootCount, out uint[] rootLeafStart))
        {
            error = "truncated reading root leaf starts";
            return false;
        }

        field.RootLeafStart = rootLeafStart;

        if (!reader.U32(out uint leafCount) || !reader.Bytes(leafCount * 32, out byte[] leaves))
        {
            error = "truncated reading leaves";
            return false;
        }

        field.Leaves = leaves;

        var banks = new Vector3[4][];
        for (int bank = 0; bank < 4; bank++)
        {
            if (!reader.U32(out uint entryCount))
            {
                error = $"truncated reading bank {bank} count";
                return false;
            }

            var entries = new Vector3[entryCount];
            for (uint j = 0; j < entryCount; j++)
            {
                if (!ReadVec3(reader, out Vector3 rgb))
                {
                    error = $"truncated bank {bank} entry {j}";
                    return false;
                }

                entries[j] = rgb;
            }

            banks[bank] = entries;
        }

        field.Banks = banks;
        return true;
    }

    public const uint ProbeSetVersion = 2;

    /// <summary>
    /// Fable 3's probe set, <c>&lt;scenario&gt;.flpb</c>, into the same field: a list of SH probes
    /// and one convex cell per probe.
    /// </summary>
    /// <remarks>
    /// Big-endian, as sub_82CC31C8 and sub_82DA8800 read it (Fable 3 IDB):
    ///
    ///     u32 version (2 only), u32 probeCount
    ///     probeCount x { f32[3] position, 9 x f32[3] SH }   each f32[3] via sub_82DA5680; the
    ///                                                      position to probe+144, the SH to +0
    ///     u32 cellCount
    ///     cellCount x { u32 faceCount,
    ///                   faceCount x { f32[4] plane, u32 n, n x f32[3] vertex },   sub_82DA58E0
    ///                   u32 linkCount, linkCount x u32 neighbour }
    ///
    /// A neighbour is a cell index, or -1..-6 where the face lies on the outer box. The box itself
    /// is the bounds of every face vertex, gathered once the cells are read (sub_82DA8D70).
    /// </remarks>
    public static bool TryParseProbeSet(byte[] bytes, out LightProbeField field, out string error)
    {
        field = new LightProbeField();
        error = string.Empty;

        var reader = new BeReader(bytes);
        if (!reader.U32(out uint version) || version != ProbeSetVersion)
        {
            error = $"unsupported probe set version (accepts {ProbeSetVersion})";
            return false;
        }

        const int probeBytes = 4 * 3 * (1 + LightProbeField.ShCoefficients);
        if (!reader.U32(out uint probeCount) ||
            (long)probeCount * probeBytes > bytes.Length - reader.Position)
        {
            error = "truncated reading probes";
            return false;
        }

        var positions = new Vector3[probeCount];
        var coefficients = new Vector3[probeCount * LightProbeField.ShCoefficients];
        for (uint i = 0; i < probeCount; i++)
        {
            ReadVec3(reader, out positions[i]);
            for (int c = 0; c < LightProbeField.ShCoefficients; c++)
            {
                ReadVec3(reader, out coefficients[(i * LightProbeField.ShCoefficients) + c]);
            }
        }

        if (!reader.U32(out uint cellCount) || (long)cellCount * 8 > bytes.Length - reader.Position)
        {
            error = "truncated reading cell count";
            return false;
        }

        var cells = new Vector4[cellCount][];
        var links = new int[cellCount][];
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        for (uint i = 0; i < cellCount; i++)
        {
            if (!reader.U32(out uint faceCount) || (long)faceCount * 20 > bytes.Length - reader.Position)
            {
                error = $"truncated cell {i} face count";
                return false;
            }

            var planes = new Vector4[faceCount];
            for (uint f = 0; f < faceCount; f++)
            {
                if (!ReadVec3(reader, out Vector3 normal) || !ReadF32(reader, out float offset) ||
                    !reader.U32(out uint vertexCount) ||
                    (long)vertexCount * 12 > bytes.Length - reader.Position)
                {
                    error = $"truncated cell {i} face {f}";
                    return false;
                }

                planes[f] = new Vector4(normal.X, normal.Y, normal.Z, offset);
                for (uint v = 0; v < vertexCount; v++)
                {
                    ReadVec3(reader, out Vector3 vertex);
                    min = new Vector3(MathF.Min(min.X, vertex.X), MathF.Min(min.Y, vertex.Y), MathF.Min(min.Z, vertex.Z));
                    max = new Vector3(MathF.Max(max.X, vertex.X), MathF.Max(max.Y, vertex.Y), MathF.Max(max.Z, vertex.Z));
                }
            }

            if (!reader.U32(out uint linkCount) || (long)linkCount * 4 > bytes.Length - reader.Position)
            {
                error = $"truncated cell {i} neighbours";
                return false;
            }

            // The neighbours the guest blends with (sub_821C22B0, 0x821c23e4..0x821c2430): signed
            // cell indices, a negative one no neighbour.
            var neighbours = new int[linkCount];
            for (uint n = 0; n < linkCount; n++)
            {
                reader.U32(out uint raw);
                neighbours[n] = unchecked((int)raw);
            }

            cells[i] = planes;
            links[i] = neighbours;
        }

        field.ProbePositions = positions;
        field.ProbeCoefficients = coefficients;
        field.CellPlanes = cells;
        field.CellLinks = links;
        field.CellBoundsMin = min;
        field.CellBoundsMax = max;
        return true;
    }

    private static bool ReadVec3(BeReader reader, out Vector3 value)
    {
        value = Vector3.Zero;
        if (!ReadF32(reader, out float x) || !ReadF32(reader, out float y) || !ReadF32(reader, out float z))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool ReadF32(BeReader reader, out float value)
    {
        value = 0f;
        if (!reader.U32(out uint raw))
        {
            return false;
        }

        value = BitConverter.Int32BitsToSingle(unchecked((int)raw));
        return true;
    }

    private static bool ReadS16Array(BeReader reader, uint count, out short[] values)
    {
        values = Array.Empty<short>();
        var result = new short[count];
        for (uint i = 0; i < count; i++)
        {
            if (!reader.U16(out ushort raw))
            {
                return false;
            }

            result[i] = unchecked((short)raw);
        }

        values = result;
        return true;
    }

    private static void SortPairs(int[] keys, int[] ids)
    {
        Array.Sort(keys, ids);
    }
}
