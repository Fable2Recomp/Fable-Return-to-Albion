namespace Refii.Tests;

using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Ui;

[TestSuite]
public class UiArtCloneTests
{
    private static Node3D Art()
    {
        var root = new Node3D { Name = "Panel", Position = new Vector3(1, 2, 3), Visible = false };
        root.SetMeta("ordinal", 7);
        var quad = new MeshInstance3D
        {
            Name = "Quad", Mesh = new QuadMesh(), Scale = new Vector3(2, 0.24f, 1),
            MaterialOverride = new ShaderMaterial(),
        };
        quad.AddToGroup("ui_art");
        root.AddChild(quad);
        quad.AddChild(new Label3D { Name = "Text", Text = "Gold" });
        return root;
    }

    [TestCase]
    [RequireGodotRuntime]
    public void TestACachedCloneIsWhatDuplicateGives()
    {
        Node3D art = Art();
        try
        {
            foreach (Node3D clone in new[] { Engine_UiArtClone.Clone(art), Engine_UiArtClone.Clone(art) })
            {
                var duplicate = (Node3D)art.Duplicate();
                try
                {
                    AssertThat(clone.Name).IsEqual(duplicate.Name);
                    AssertThat(clone.Position).IsEqual(duplicate.Position);
                    AssertThat(clone.Visible).IsEqual(duplicate.Visible);
                    AssertThat((int)clone.GetMeta("ordinal")).IsEqual(7);
                    var quad = clone.GetNode<MeshInstance3D>("Quad");
                    AssertThat(quad.Scale).IsEqual(new Vector3(2, 0.24f, 1));
                    // Sub-resources shared, as Duplicate shares them (Art gives each clone its own materials after).
                    AssertThat(quad.Mesh).IsSame(art.GetNode<MeshInstance3D>("Quad").Mesh);
                    AssertThat(quad.MaterialOverride).IsSame(art.GetNode<MeshInstance3D>("Quad").MaterialOverride);
                    AssertThat(quad.IsInGroup("ui_art")).IsTrue();
                    AssertThat(clone.GetNode<Label3D>("Quad/Text").Text).IsEqual("Gold");
                    // No owners, as on the art's own nodes and on a duplicate of them.
                    AssertThat(quad.Owner).IsNull();
                    AssertThat(clone.GetNode("Quad/Text").Owner).IsNull();
                    // Its own nodes, not the art's.
                    AssertThat(quad).IsNotSame(art.GetNode("Quad"));
                }
                finally
                {
                    clone.Free();
                    duplicate.Free();
                }
            }
        }
        finally { art.Free(); }
    }
}
