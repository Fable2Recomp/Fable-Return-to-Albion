using System;
using GdUnit4;
using Godot;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Engine_HiddenClusters.Apply on the reconcile cadence: a prop's mesh with surface regions but no
/// part table hides nothing of its own, and what another node draws for it (a cloth body) stays
/// collapsed through every pass, with the mesh read once and no override rewritten while none changes.
/// </summary>
[TestSuite]
public sealed class HiddenClustersApplyTests
{
    private static ArrayMesh ThreeSurfaces()
    {
        var mesh = new ArrayMesh();
        for (int i = 0; i < 3; ++i)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }

        mesh.SetMeta(Refii.Models.Engine_MdlMesh.SurfaceRegionsMeta, new[] { "a", "b", "c" });
        return mesh;
    }

    [TestCase]
    [RequireGodotRuntime]
    public void APropWithoutAPartTableHidesNothingAndKeepsWhatIsDrawnElsewhere()
    {
        bool variants = Engine_BodyVariants.Enabled;
        Engine_BodyVariants.Enabled = false;
        ArrayMesh mesh = ThreeSurfaces();
        var node = new MeshInstance3D { Mesh = mesh };
        try
        {
            AssertThat(Engine_HiddenClusters.Apply(node, 0x1234u)).IsEqual(0);
            AssertThat(Engine_HiddenClusters.ClusterMask(node)).IsEqual(UInt128.Zero);
            for (int s = 0; s < 3; ++s)
            {
                AssertThat(node.GetSurfaceOverrideMaterial(s)).IsNull();
            }

            // A cloth body draws surface 1: collapsed here, and kept so by every later pass.
            Engine_HiddenClusters.SetClothSurfaces(node, UInt128.One << 1);
            for (int pass = 0; pass < 2; ++pass)
            {
                AssertThat(Engine_HiddenClusters.Apply(node, 0x1234u)).IsEqual(0);
                AssertThat(Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(1))).IsTrue();
                AssertThat(node.GetSurfaceOverrideMaterial(0)).IsNull();
                AssertThat(node.GetSurfaceOverrideMaterial(2)).IsNull();
                AssertThat(node.Mesh).IsSame(mesh);
            }

            // Let go, the surface draws its own material again, and stays so.
            Engine_HiddenClusters.SetClothSurfaces(node, UInt128.Zero);
            AssertThat(node.GetSurfaceOverrideMaterial(1)).IsNull();
            AssertThat(Engine_HiddenClusters.Apply(node, 0x1234u)).IsEqual(0);
            AssertThat(node.GetSurfaceOverrideMaterial(1)).IsNull();
        }
        finally
        {
            Engine_HiddenClusters.SetClothSurfaces(node, UInt128.Zero);
            node.Free();
            Engine_BodyVariants.Enabled = variants;
        }
    }
}
