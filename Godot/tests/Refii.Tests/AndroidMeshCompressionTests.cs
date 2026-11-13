using System;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;
using Verdict = Refii.AndroidRuntime.AndroidMeshCompression.Verdict;

namespace Refii.Tests;

/// <summary>`android_compress_mesh_attributes` (AndroidMeshCompression): which model surfaces take Godot's attribute compression.</summary>
[TestSuite]
public sealed class AndroidMeshCompressionTests
{
    private static readonly AndroidMeshCompression.UvRange Unit =
        new(true, Vector2.Zero, Vector2.One, true);

    private static AndroidMeshCompression.Shape Prop(
        float longest = 4f, AndroidMeshCompression.UvRange? uv = null, AndroidMeshCompression.UvRange? uv2 = null) =>
        new(true, false, true, true, true, longest, true, uv ?? Unit, uv2 ?? AndroidMeshCompression.UvRange.None);

    /// <summary>A small static prop with UVs in [0, 1] is compressed; skinned, non-triangle and tangent-only surfaces are not.</summary>
    [TestCase]
    public void OnlyStaticTriangleSurfacesGodotAcceptsAreCompressed()
    {
        AssertThat(AndroidMeshCompression.Decide(Prop())).IsEqual(Verdict.Compressed);
        AssertThat(AndroidMeshCompression.Decide(Prop() with { Skinned = true })).IsEqual(Verdict.Skinned);
        AssertThat(AndroidMeshCompression.Decide(Prop() with { Triangles = false })).IsEqual(Verdict.NotTriangles);
        AssertThat(AndroidMeshCompression.Decide(Prop() with { Positions = false })).IsEqual(Verdict.NoPositions);
        // Godot refuses tangents without normals under compression; neither is fine.
        AssertThat(AndroidMeshCompression.Decide(Prop() with { Normals = false })).IsEqual(Verdict.TangentWithoutNormal);
        AssertThat(AndroidMeshCompression.Decide(Prop() with { Normals = false, Tangents = false })).IsEqual(Verdict.Compressed);
    }

    /// <summary>Positions round to the box's side / 65535: a surface past the limit stays float, and so does a non-finite one.</summary>
    [TestCase]
    public void LargeOrNonFiniteSurfacesStayFloat()
    {
        AssertThat(AndroidMeshCompression.Decide(Prop(AndroidMeshCompression.ExtentLimit))).IsEqual(Verdict.Compressed);
        AssertThat(AndroidMeshCompression.Decide(Prop(AndroidMeshCompression.ExtentLimit + 0.01f))).IsEqual(Verdict.TooLarge);
        AssertThat(AndroidMeshCompression.Decide(Prop(float.NaN))).IsEqual(Verdict.NotFinite);
        AssertThat(AndroidMeshCompression.Decide(Prop() with { PositionsFinite = false })).IsEqual(Verdict.NotFinite);
        AssertThat(AndroidMeshCompression.Decide(Prop(uv: Unit with { Finite = false }))).IsEqual(Verdict.NotFinite);
    }

    /// <summary>UVs outside [0, 1] are scaled by Godot; within ±8 they are compressed, tiled further or barely nonzero they are not.</summary>
    [TestCase]
    public void TiledUvsAreCompressedOnlyWithinTheLimit()
    {
        var tiled = new AndroidMeshCompression.UvRange(true, new Vector2(-2f, 0f), new Vector2(4f, 3f), true);
        AssertThat(AndroidMeshCompression.Decide(Prop(uv: tiled))).IsEqual(Verdict.Compressed);

        var edge = new AndroidMeshCompression.UvRange(true, Vector2.Zero, new Vector2(AndroidMeshCompression.UvLimit, 1f), true);
        AssertThat(AndroidMeshCompression.Decide(Prop(uv: edge))).IsEqual(Verdict.Compressed);

        var far = new AndroidMeshCompression.UvRange(true, new Vector2(-8.5f, 0f), new Vector2(1f, 1f), true);
        AssertThat(AndroidMeshCompression.Decide(Prop(uv: far))).IsEqual(Verdict.UvTiled);

        // UV in [0, 1] but the lightmap channel tiled: both are scaled, and the lightmap's range decides.
        AssertThat(AndroidMeshCompression.Decide(Prop(uv2: far))).IsEqual(Verdict.UvTiled);
        AssertThat(AndroidMeshCompression.Decide(Prop(uv2: tiled))).IsEqual(Verdict.Compressed);

        // Out of [0, 1] by less than Godot's zero test: its encoder and shader would disagree.
        var tiny = new AndroidMeshCompression.UvRange(true, new Vector2(-1e-6f, 0f), new Vector2(1e-6f, 0f), true);
        AssertThat(AndroidMeshCompression.Decide(Prop(uv: tiny))).IsEqual(Verdict.UvTiny);
    }

    /// <summary>The ranges are taken from zero, as Godot's encoder takes them.</summary>
    [TestCase]
    public void RangesAreMeasuredFromZero()
    {
        AndroidMeshCompression.UvRange inside = AndroidMeshCompression.Measure(new[] { new Vector2(0.25f, 0.5f), new Vector2(0.75f, 1f) });
        AssertThat(inside.Min).IsEqual(Vector2.Zero);
        AssertThat(inside.InUnit).IsTrue();

        AndroidMeshCompression.UvRange outside = AndroidMeshCompression.Measure(new[] { new Vector2(-3f, 0.5f), new Vector2(0.5f, 6f) });
        AssertThat(outside.InUnit).IsFalse();
        AssertThat(outside.Magnitude).IsEqual(6f);
        AssertThat(AndroidMeshCompression.Measure(new[] { new Vector2(float.NaN, 0f) }).Finite).IsFalse();

        (float longest, bool finite) = AndroidMeshCompression.Extent(new[] { new Vector3(-1f, 0f, 2f), new Vector3(3f, 0.5f, 2f) });
        AssertThat(longest).IsEqual(4f);
        AssertThat(finite).IsTrue();
    }

    /// <summary>The verdict on real arrays: a skinned surface reads no array; a tiled one stays float.</summary>
    [TestCase, RequireGodotRuntime]
    public void JudgesGodotArrays()
    {
        Godot.Collections.Array prop = Quad(new Vector2(1f, 1f));
        AssertThat(AndroidMeshCompression.Judge(Mesh.PrimitiveType.Triangles, prop, out int vertices, out int saved))
            .IsEqual(Verdict.Compressed);
        AssertThat(vertices).IsEqual(4);
        AssertThat(saved).IsEqual(12); // position 4, normal and tangent 4, one UV 4

        AssertThat(AndroidMeshCompression.Judge(Mesh.PrimitiveType.Triangles, Quad(new Vector2(12f, 1f)), out _, out _))
            .IsEqual(Verdict.UvTiled);

        Godot.Collections.Array skinned = Quad(new Vector2(1f, 1f));
        skinned[(int)Mesh.ArrayType.Bones] = new int[16];
        skinned[(int)Mesh.ArrayType.Weights] = new float[16];
        AssertThat(AndroidMeshCompression.Judge(Mesh.PrimitiveType.Triangles, skinned, out int read, out _))
            .IsEqual(Verdict.Skinned);
        AssertThat(read).IsEqual(0);

        // Off the phone (and with the setting off) the hook adds nothing.
        AssertThat((long)AndroidMeshCompression.FlagFor(Mesh.PrimitiveType.Triangles, prop)).IsEqual(0L);
    }

    /// <summary>
    /// A compressed surface keeps its LOD level and decodes to its positions and tiled UVs within
    /// Godot's steps (headless Godot keeps the surface data in its dummy mesh storage).
    /// </summary>
    [TestCase, RequireGodotRuntime]
    public void CompressedSurfaceKeepsItsLevelsAndDecodes()
    {
        Godot.Collections.Array arrays = Quad(new Vector2(6f, -3f));
        AssertThat(AndroidMeshCompression.Judge(Mesh.PrimitiveType.Triangles, arrays, out _, out _)).IsEqual(Verdict.Compressed);

        var lods = new Godot.Collections.Dictionary { [0.5f] = new[] { 0, 1, 2 } };
        var mesh = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays, null, lods,
            Mesh.ArrayFormat.FlagCompressAttributes);

        AssertThat(mesh.GetSurfaceCount()).IsEqual(1);
        AssertThat((long)(mesh.SurfaceGetFormat(0) & Mesh.ArrayFormat.FlagCompressAttributes)).IsNotEqual(0L);
        Godot.Collections.Dictionary surface = RenderingServer.MeshGetSurface(mesh.GetRid(), 0);
        AssertThat(surface["lods"].AsGodotArray().Count).IsEqual(1);

        Godot.Collections.Array back = mesh.SurfaceGetArrays(0);
        Vector3[] positions = back[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector2[] uvs = back[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        Vector3[] sourcePositions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector2[] sourceUvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        for (int i = 0; i < sourcePositions.Length; i++)
        {
            // A step is the box's side (10 m) / 65535; a UV step 2 * 6 / 65535.
            AssertThat(positions[i].DistanceTo(sourcePositions[i]) < 10f / 65535f * 2f).IsTrue();
            AssertThat(MathF.Abs(uvs[i].X - sourceUvs[i].X) < 12f / 65535f).IsTrue();
            AssertThat(MathF.Abs(uvs[i].Y - sourceUvs[i].Y) < 12f / 65535f).IsTrue();
        }
    }

    /// <summary>A 10 m quad facing up, its UVs running from 0 to <paramref name="uvFar"/>.</summary>
    private static Godot.Collections.Array Quad(Vector2 uvFar)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[]
        {
            new Vector3(0f, 0f, 0f), new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 10f), new Vector3(0f, 0f, 10f),
        };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };
        arrays[(int)Mesh.ArrayType.Tangent] = new float[] { 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1 };
        arrays[(int)Mesh.ArrayType.TexUV] = new[]
        {
            Vector2.Zero, new Vector2(uvFar.X, 0f), uvFar, new Vector2(0f, uvFar.Y),
        };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 1, 0, 3, 2 };
        return arrays;
    }
}
