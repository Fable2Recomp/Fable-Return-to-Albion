using System;
using GdUnit4;
using Godot;
using Refii.Entities;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Engine_HiddenClusters on the reconcile cadence for a prop WITH a part table (every market prop):
/// the node's record answers the cloth, displacement and variant entries it copies, a pass asked as
/// the last one writes nothing, and whatever moves in between (a cloth body, a displacement part, the
/// variants lever, another writer of the node's mesh) is presented exactly as before.
/// </summary>
[TestSuite]
public sealed class HiddenClustersReconcileTests
{
    private static ArrayMesh ThreeSurfacesWithPartTable()
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
        mesh.SetMeta(Refii.Models.Engine_MdlComposite.SurfacePartsMetaName, new[] { 0, 0, 0 });
        mesh.SetMeta(Refii.Models.Engine_MdlComposite.PartHashesMetaName, new long[] { 0x1234 });
        return mesh;
    }

    [TestCase]
    [RequireGodotRuntime]
    public void APropWithAPartTableKeepsItsVariantThroughEveryPassAndAnyMeshWrittenOverIt()
    {
        bool variants = Engine_BodyVariants.Enabled;
        Engine_BodyVariants.Enabled = true;
        ArrayMesh mesh = ThreeSurfacesWithPartTable();
        var node = new MeshInstance3D { Mesh = mesh };
        UInt128 one = UInt128.One << 1;
        try
        {
            // Nothing hidden: the source, twice over.
            for (int pass = 0; pass < 2; ++pass)
            {
                AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
                AssertThat(node.Mesh).IsSame(mesh);
                AssertThat(Engine_HiddenClusters.CollapsedMask(node)).IsEqual(UInt128.Zero);
                AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(node)).IsEqual(UInt128.Zero);
            }

            // A cloth body draws surface 1: the variant without it, kept by every later pass.
            Engine_HiddenClusters.SetClothSurfaces(node, one);
            Mesh variant = node.Mesh;
            AssertThat(variant).IsNotSame(mesh);
            AssertThat(variant.GetSurfaceCount()).IsEqual(2);
            AssertThat(Engine_BodyVariants.SourceOf(node)).IsSame(mesh);
            for (int pass = 0; pass < 2; ++pass)
            {
                AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
                AssertThat(node.Mesh).IsSame(variant);
                AssertThat(Engine_HiddenClusters.CollapsedMask(node)).IsEqual(one);
                // A cloth body that casts nothing of its own leaves its surfaces in the shadow.
                AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(node)).IsEqual(UInt128.Zero);
            }

            // Another writer puts the source back: the next pass shows the same variant again.
            node.Mesh = mesh;
            AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
            AssertThat(node.Mesh).IsSame(variant);

            // Casting its own shadow, the cloth's surfaces leave the shadow too.
            Engine_HiddenClusters.SetClothSurfaces(node, one, castsShadow: true);
            AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(node)).IsEqual(one);
            AssertThat(node.Mesh).IsSame(variant);

            // A displacement part draws surface 2 as well: a second variant, in both masks.
            UInt128 two = UInt128.One << 2;
            Engine_HiddenClusters.SetDisplacedSurfaces(node, two);
            AssertThat(Engine_HiddenClusters.CollapsedMask(node)).IsEqual(one | two);
            AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(node)).IsEqual(one | two);
            Mesh both = node.Mesh;
            AssertThat(both.GetSurfaceCount()).IsEqual(1);
            AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
            AssertThat(node.Mesh).IsSame(both);

            // The variants lever off: the source, with the collapse override on both surfaces.
            Engine_BodyVariants.Enabled = false;
            AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
            AssertThat(node.Mesh).IsSame(mesh);
            AssertThat(node.GetSurfaceOverrideMaterial(0)).IsNull();
            AssertThat(Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(1))).IsTrue();
            AssertThat(Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(2))).IsTrue();
            AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
            AssertThat(Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(1))).IsTrue();

            // And back on: the overrides come off before the variant goes on.
            Engine_BodyVariants.Enabled = true;
            AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
            AssertThat(node.Mesh).IsSame(both);
            AssertThat(node.GetSurfaceOverrideMaterial(1)).IsNull();

            // Both let go: the source again, nothing collapsed, and so it stays.
            Engine_HiddenClusters.SetDisplacedSurfaces(node, UInt128.Zero);
            Engine_HiddenClusters.SetClothSurfaces(node, UInt128.Zero);
            for (int pass = 0; pass < 2; ++pass)
            {
                AssertThat(Engine_HiddenClusters.Apply(node, 0x5678u)).IsEqual(0);
                AssertThat(node.Mesh).IsSame(mesh);
                AssertThat(Engine_HiddenClusters.CollapsedMask(node)).IsEqual(UInt128.Zero);
                AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(node)).IsEqual(UInt128.Zero);
                for (int s = 0; s < 3; ++s)
                {
                    AssertThat(node.GetSurfaceOverrideMaterial(s)).IsNull();
                }
            }
        }
        finally
        {
            Engine_HiddenClusters.SetDisplacedSurfaces(node, UInt128.Zero);
            Engine_HiddenClusters.SetClothSurfaces(node, UInt128.Zero);
            node.Free();
            Engine_BodyVariants.Enabled = variants;
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ANodeWhoseMeshChangesStartsAFreshRecordThatStillKnowsItsCloth()
    {
        bool variants = Engine_BodyVariants.Enabled;
        Engine_BodyVariants.Enabled = false;
        ArrayMesh first = ThreeSurfacesWithPartTable();
        ArrayMesh second = ThreeSurfacesWithPartTable();
        var node = new MeshInstance3D { Mesh = first };
        UInt128 zero = UInt128.One;
        try
        {
            // Cloth set before the node's first pass, while it has no record of its own.
            Engine_HiddenClusters.SetClothSurfaces(node, zero);
            AssertThat(Engine_HiddenClusters.IsCollapsed(node.GetSurfaceOverrideMaterial(0))).IsTrue();
            AssertThat(Engine_HiddenClusters.Apply(node, 0x9ABCu)).IsEqual(0);
            AssertThat(Engine_HiddenClusters.CollapsedMask(node)).IsEqual(zero);

            // A new mesh is a new record: it copies the cloth entry and presents it on the new mesh.
            node.Mesh = second;
            AssertThat(Engine_HiddenClusters.Apply(node, 0x9ABCu)).IsEqual(0);
            AssertThat(node.Mesh).IsSame(second);
            AssertThat(Engine_HiddenClusters.CollapsedMask(node)).IsEqual(zero);
            AssertThat(Engine_HiddenClusters.ShadowCollapsedMask(node)).IsEqual(UInt128.Zero);
        }
        finally
        {
            Engine_HiddenClusters.SetClothSurfaces(node, UInt128.Zero);
            node.Free();
            Engine_BodyVariants.Enabled = variants;
        }
    }
}
