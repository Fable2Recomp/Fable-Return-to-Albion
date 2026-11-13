using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_compress_mesh_attributes`: model surfaces that are not skinned are added with Godot's
/// <see cref="Mesh.ArrayFormat.FlagCompressAttributes"/>, which packs a vertex's position into
/// 16 bits an axis within the surface's box, its normal and tangent into one 4-byte axis plus a
/// 16-bit angle, and each UV channel into 16 bits an axis. A vertex with both UV channels goes
/// from 36 bytes (12 position, 8 normal and tangent, 8 + 8 UVs) to 20; the transfer channels in
/// CUSTOM0..3 and the colour are not touched. Hooked from <see cref="Refii.Models.Engine_MdlSurfaceArrays.Add(ArrayMesh, Mesh.PrimitiveType, Godot.Collections.Array, Godot.Collections.Dictionary?, float[]?)"/>.
/// </summary>
/// <remarks>
/// <para>WHAT GODOT 4.7 NEEDS (servers/rendering/rendering_server.cpp,
/// `mesh_create_surface_data_from_arrays` and `_surface_set_data`). 3D positions; a normal array
/// whenever there is a tangent array (it refuses tangents without normals). The UVs: when every
/// component of UV and UV2 is in [0, 1] they are stored as unorm16 of the value itself; otherwise
/// each axis is scaled by twice the largest magnitude of that channel and stored as unorm16 of
/// <c>uv / scale + 0.5</c>, and the shader multiplies back (`uv_scale`). The encoder tests that
/// scale with `is_zero_approx` and the shader with an exact `!= vec4(0.0)`, so UVs that leave
/// [0, 1] by less than about 1e-5 would decode wrongly: such a surface is not compressed.</para>
/// <para>WHAT THIS ADDS FOR THE PICTURE. Nothing Godot requires, only precision. UVs: a step is
/// <c>2 * max|uv| / 65535</c>, so a channel that tiles past <see cref="UvLimit"/> repeats stays
/// float (at 8, a step is a quarter texel of a 2048 texture). Positions: a step is the box's side
/// / 65535, so a surface longer than <see cref="ExtentLimit"/> on any side stays float (at 32 m a
/// vertex moves at most a quarter millimetre; two surfaces meeting at a seam each round to their
/// own box, which is where a crack would show). NaN or infinity anywhere stays float.</para>
/// <para>WHAT IS NOT COMPRESSED. Skinned surfaces (bones or weights): characters keep their float
/// arrays. Anything that is not a triangle list. Anything that is not added through the hook —
/// shadow proxies, caster cells, cloth, terrain, effects — whose builders write vertex regions in
/// the uncompressed layout or check its strides.</para>
/// <para>WHAT READS THE ARRAYS LATER IS UNAFFECTED. Every reader of a model surface's arrays
/// (composites, body variants, caster merges, shadow proxies, baked lighting) goes through
/// <see cref="Refii.Models.Engine_MdlSurfaceArrays.Of"/>, which hands out the float arrays the builder
/// recorded, not Godot's copy. After <see cref="Refii.Models.Engine_MdlSurfaceArrays.Forget"/> a reader
/// reads back from the GPU, and Godot decodes a compressed surface to floats there
/// (`_get_array_from_surface`), within the steps above. LOD index lists are passed beside the
/// arrays and stored as they are, whatever the vertex format, and the generated ones
/// (Engine_MdlAutoLod) are built from the float arrays before the surface is added.</para>
/// </remarks>
public static class AndroidMeshCompression
{
    /// <summary>The refii.toml setting. Off unless set: it is being measured.</summary>
    public const string Key = "android_compress_mesh_attributes";

    /// <summary>The largest UV magnitude, on either channel, a compressed surface may carry.</summary>
    public const float UvLimit = 8f;

    /// <summary>The longest side, in metres, of the box of a compressed surface's positions.</summary>
    public const float ExtentLimit = 32f;

    /// <summary>Below this magnitude Godot's encoder and shader disagree about a scaled channel.</summary>
    private const float SmallestUvScale = 1e-4f;

    /// <summary>Why a surface was or was not compressed.</summary>
    public enum Verdict
    {
        Compressed,
        NotTriangles,
        Skinned,
        NoPositions,
        TangentWithoutNormal,
        NotFinite,
        TooLarge,
        UvTiled,
        UvTiny,
    }

    /// <summary>One UV channel's range, as Godot's encoder measures it (from zero, so an absent value counts as 0).</summary>
    public readonly record struct UvRange(bool Present, Vector2 Min, Vector2 Max, bool Finite)
    {
        public static readonly UvRange None = new(false, Vector2.Zero, Vector2.Zero, true);

        /// <summary>Every component in [0, 1]: Godot stores the value itself.</summary>
        public bool InUnit => !Present || (Min.X >= 0f && Min.Y >= 0f && Max.X <= 1f && Max.Y <= 1f);

        /// <summary>The largest magnitude of any component: half the scale Godot stores the channel at.</summary>
        public float Magnitude => Present
            ? MathF.Max(MathF.Max(MathF.Abs(Min.X), MathF.Abs(Max.X)), MathF.Max(MathF.Abs(Min.Y), MathF.Abs(Max.Y)))
            : 0f;
    }

    /// <summary>What the decision reads from a surface.</summary>
    public readonly record struct Shape(
        bool Triangles, bool Skinned, bool Positions, bool Normals, bool Tangents,
        float LongestSide, bool PositionsFinite, UvRange Uv, UvRange Uv2);

    private static bool? _enabled;
    private static readonly int[] Counts = new int[Enum.GetValues<Verdict>().Length];
    private static long _savedBytes;

    /// <summary>The launch setting, on a phone only; read once.</summary>
    public static bool Enabled => _enabled ??= ReadEnabled();

    private static bool ReadEnabled()
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            return Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), Key, false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The compression flag for a model surface about to be added, or 0: always 0 unless the
    /// setting is on. Counts the verdict for <see cref="Stats"/>.
    /// </summary>
    public static Mesh.ArrayFormat FlagFor(Mesh.PrimitiveType primitive, Godot.Collections.Array arrays)
    {
        if (!Enabled)
        {
            return 0;
        }

        Verdict verdict = Judge(primitive, arrays, out int vertices, out int savedPerVertex);
        Interlocked.Increment(ref Counts[(int)verdict]);
        if (verdict != Verdict.Compressed)
        {
            return 0;
        }

        Interlocked.Add(ref _savedBytes, (long)vertices * savedPerVertex);
        return Mesh.ArrayFormat.FlagCompressAttributes;
    }

    /// <summary>The verdict on a surface's arrays, reading only what the decision needs.</summary>
    /// <param name="vertices">The surface's vertex count, or 0 when it was not read.</param>
    /// <param name="savedPerVertex">Bytes a vertex saves on the GPU when compressed.</param>
    public static Verdict Judge(
        Mesh.PrimitiveType primitive, Godot.Collections.Array arrays, out int vertices, out int savedPerVertex)
    {
        vertices = 0;
        savedPerVertex = 0;
        bool triangles = primitive == Mesh.PrimitiveType.Triangles;
        bool skinned = Has(arrays, Mesh.ArrayType.Bones) || Has(arrays, Mesh.ArrayType.Weights);
        bool positions = Is(arrays, Mesh.ArrayType.Vertex, Variant.Type.PackedVector3Array);
        bool normals = Is(arrays, Mesh.ArrayType.Normal, Variant.Type.PackedVector3Array);
        bool tangents = Has(arrays, Mesh.ArrayType.Tangent);

        // The cheap checks first: a skinned or odd surface reads no array at all.
        var shape = new Shape(triangles, skinned, positions, normals, tangents, 0f, true, UvRange.None, UvRange.None);
        Verdict early = Decide(shape);
        if (early != Verdict.Compressed)
        {
            return early;
        }

        Vector3[] points = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        vertices = points.Length;
        (float longest, bool finite) = Extent(points);
        UvRange uv = Is(arrays, Mesh.ArrayType.TexUV, Variant.Type.PackedVector2Array)
            ? Measure(arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array())
            : UvRange.None;
        UvRange uv2 = Is(arrays, Mesh.ArrayType.TexUV2, Variant.Type.PackedVector2Array)
            ? Measure(arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array())
            : UvRange.None;

        // Position 12 to 8; normal and tangent (Godot adds a tangent to any normal) 8 to 4; each UV 8 to 4.
        savedPerVertex = 4 + (normals ? 4 : 0) + (uv.Present ? 4 : 0) + (uv2.Present ? 4 : 0);
        return Decide(shape with { LongestSide = longest, PositionsFinite = finite, Uv = uv, Uv2 = uv2 });
    }

    /// <summary>The decision on what was read: Godot's own conditions, then the precision ones.</summary>
    public static Verdict Decide(in Shape shape)
    {
        if (!shape.Triangles)
        {
            return Verdict.NotTriangles;
        }

        if (shape.Skinned)
        {
            return Verdict.Skinned;
        }

        if (!shape.Positions)
        {
            return Verdict.NoPositions;
        }

        if (shape.Tangents && !shape.Normals)
        {
            return Verdict.TangentWithoutNormal;
        }

        if (!shape.PositionsFinite || !shape.Uv.Finite || !shape.Uv2.Finite || !float.IsFinite(shape.LongestSide))
        {
            return Verdict.NotFinite;
        }

        if (shape.LongestSide > ExtentLimit)
        {
            return Verdict.TooLarge;
        }

        // Both channels in [0, 1]: stored as they are.
        if (shape.Uv.InUnit && shape.Uv2.InUnit)
        {
            return Verdict.Compressed;
        }

        // Otherwise both are scaled, each by its own largest magnitude.
        float magnitude = MathF.Max(shape.Uv.Magnitude, shape.Uv2.Magnitude);
        if (magnitude > UvLimit)
        {
            return Verdict.UvTiled;
        }

        return magnitude * 2f < SmallestUvScale ? Verdict.UvTiny : Verdict.Compressed;
    }

    /// <summary>A UV channel's range from zero, as Godot's encoder takes it.</summary>
    public static UvRange Measure(ReadOnlySpan<Vector2> uvs)
    {
        Vector2 min = Vector2.Zero, max = Vector2.Zero;
        bool finite = true;
        foreach (Vector2 uv in uvs)
        {
            finite &= float.IsFinite(uv.X) && float.IsFinite(uv.Y);
            min = min.Min(uv);
            max = max.Max(uv);
        }

        return new UvRange(true, min, max, finite);
    }

    /// <summary>The longest side of the positions' box, and whether every position is finite.</summary>
    public static (float Longest, bool Finite) Extent(ReadOnlySpan<Vector3> points)
    {
        if (points.IsEmpty)
        {
            return (0f, true);
        }

        Vector3 min = points[0], max = points[0];
        bool finite = true;
        foreach (Vector3 p in points)
        {
            finite &= float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
            min = min.Min(p);
            max = max.Max(p);
        }

        Vector3 size = max - min;
        return (MathF.Max(size.X, MathF.Max(size.Y, size.Z)), finite);
    }

    /// <summary>Surfaces by verdict and the GPU bytes saved since start, for the debug channel's stats.</summary>
    public static Dictionary<string, object?> Stats()
    {
        var stats = new Dictionary<string, object?> { ["enabled"] = Enabled };
        foreach (Verdict verdict in Enum.GetValues<Verdict>())
        {
            stats[verdict.ToString()] = Volatile.Read(ref Counts[(int)verdict]);
        }

        stats["savedMB"] = Math.Round(Interlocked.Read(ref _savedBytes) / 1048576.0, 1);
        return stats;
    }

    private static bool Has(Godot.Collections.Array arrays, Mesh.ArrayType slot) =>
        arrays.Count > (int)slot && arrays[(int)slot].VariantType != Variant.Type.Nil;

    private static bool Is(Godot.Collections.Array arrays, Mesh.ArrayType slot, Variant.Type type) =>
        arrays.Count > (int)slot && arrays[(int)slot].VariantType == type;
}
