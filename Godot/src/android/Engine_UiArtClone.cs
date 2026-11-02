using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The phone's clone of a piece of UI art (<see cref="Engine_UiTheme.Art"/>): the same tree
/// <c>Duplicate()</c> makes, without a wait on the render thread per mesh.
/// </summary>
/// <remarks>
/// <c>Duplicate()</c> copies a node by listing its properties, and a mesh's list asks the
/// RenderingServer for its instance shader parameters. With the render thread separate (Fable 2 on
/// Android), each ask waits for that thread to catch up: on the S25+ the owned books built behind the
/// loading screen asked 14,363 times, and their frames ran 240 ms to 1.3 s, about 13 s of the main
/// thread in a 39 s load. A packed scene lists each property once, when it is packed; instantiating it
/// sets them by name. So each art piece is duplicated and packed once, the first time it is cloned, and
/// every clone after is instantiated from that.
///
/// The clone is the one <c>Duplicate()</c> gives: sub-resources shared (Art gives the clone its own
/// materials after), metadata kept, every group kept (a packed scene keeps only persistent ones, so the
/// rest are added back), and no owners, as the art's own nodes have none. Art whose nodes do have owners
/// is duplicated as before.
/// </remarks>
public static class Engine_UiArtClone
{
    private static readonly ConditionalWeakTable<Node3D, PackedScene> Packed = new();

    /// <summary>A clone of <paramref name="source"/>, from its packed scene, packed on first use.</summary>
    public static Node3D Clone(Node3D source)
    {
        if (!Packed.TryGetValue(source, out PackedScene? scene))
        {
            scene = Pack(source);
            if (scene is null) return (Node3D)source.Duplicate();
            Packed.Add(source, scene);
        }

        if (scene.Instantiate() is not Node3D clone) return (Node3D)source.Duplicate();
        Match(source, clone);
        return clone;
    }

    /// <summary>A packed scene of a private duplicate of <paramref name="source"/>, or null for art with owners.</summary>
    private static PackedScene? Pack(Node3D source)
    {
        if (HasOwners(source)) return null;
        var template = (Node3D)source.Duplicate();
        SetOwners(template, template);
        var scene = new PackedScene();
        Error packed = scene.Pack(template);
        template.Free();
        return packed == Error.Ok ? scene : null;
    }

    private static bool HasOwners(Node node)
    {
        if (node.Owner is not null) return true;
        foreach (Node child in node.GetChildren())
            if (HasOwners(child)) return true;
        return false;
    }

    /// <summary>Owns every node under <paramref name="root"/> by it, so packing keeps the whole tree.</summary>
    private static void SetOwners(Node node, Node root)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = root;
            SetOwners(child, root);
        }
    }

    /// <summary>
    /// Makes an instantiated clone match a duplicate of ownerless art: its owners taken away, and the art's
    /// groups the packed scene left out (those not persistent) added back. The trees are walked side by
    /// side, as instantiation keeps the children's order.
    /// </summary>
    private static void Match(Node source, Node clone)
    {
        var pending = new Stack<(Node Source, Node Clone)>();
        pending.Push((source, clone));
        while (pending.Count > 0)
        {
            (Node from, Node to) = pending.Pop();
            if (to != clone) to.Owner = null;
            foreach (StringName group in from.GetGroups())
                if (!to.IsInGroup(group)) to.AddToGroup(group);
            int count = System.Math.Min(from.GetChildCount(), to.GetChildCount());
            for (int i = 0; i < count; i++) pending.Push((from.GetChild(i), to.GetChild(i)));
        }
    }
}
