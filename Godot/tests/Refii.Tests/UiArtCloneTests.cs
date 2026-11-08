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
            // The first is a plain duplicate, the second packs the art, the third comes from that scene.
            foreach (Node3D clone in new[] { Engine_UiArtClone.Clone(art), Engine_UiArtClone.Clone(art), Engine_UiArtClone.Clone(art) })
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

    /// <summary>Every stored property of a tree, one line a node, walked in order: what a clone must repeat.</summary>
    private static string Describe(Node node)
    {
        var text = new System.Text.StringBuilder();
        Walk(node, node, text);
        return text.ToString();

        static void Walk(Node at, Node root, System.Text.StringBuilder text)
        {
            text.Append(root.GetPathTo(at)).Append(' ').Append(at.GetClass());
            foreach (Godot.Collections.Dictionary property in at.GetPropertyList())
            {
                if (((long)property["usage"] & (long)PropertyUsageFlags.Storage) == 0) continue;
                string name = (string)property["name"];
                if (name == "owner") continue;
                Variant value = at.Get(name);
                // A shared resource is the same object, not an equal one.
                text.Append(' ').Append(name).Append('=').Append(value.VariantType == Variant.Type.Object
                    ? value.AsGodotObject()?.GetInstanceId().ToString() ?? "null"
                    : value.ToString());
            }
            foreach (StringName meta in at.GetMetaList()) text.Append(" meta:").Append(meta).Append('=').Append(at.GetMeta(meta));
            var groups = new System.Collections.Generic.List<string>();
            foreach (StringName group in at.GetGroups()) groups.Add(group);
            groups.Sort(System.StringComparer.Ordinal);
            text.Append(" groups:").AppendJoin(',', groups).Append('\n');
            foreach (Node child in at.GetChildren()) Walk(child, root, text);
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void TestEveryCloneRepeatsTheDuplicateAndPackingLeavesTheArtAlone()
    {
        // Under a parent, as art is in a pattern book's tree.
        var book = new Node3D { Name = "Book" };
        Node3D art = Art();
        book.AddChild(art);
        try
        {
            string before = Describe(art);
            var duplicate = (Node3D)art.Duplicate();
            string expected = Describe(duplicate);
            duplicate.Free();
            for (int i = 0; i < 3; i++)
            {
                Node3D clone = Engine_UiArtClone.Clone(art);
                AssertThat(Describe(clone)).IsEqual(expected);
                clone.Free();
                // The art itself: unchanged, and its nodes own by nothing again.
                AssertThat(Describe(art)).IsEqual(before);
                AssertThat(art.GetNode("Quad").Owner).IsNull();
                AssertThat(art.GetNode("Quad/Text").Owner).IsNull();
                AssertThat(art.GetParent()).IsSame(book);
            }
        }
        finally { book.Free(); }
    }
}
