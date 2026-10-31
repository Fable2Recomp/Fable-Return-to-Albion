using System.Collections.Generic;
using Godot;
using Refii.Ui;

namespace Refii.AndroidRuntime;

/// <summary>
/// `canvas_lean` for the touch controls: while nothing shows between the UI layer (5) and the touch
/// layer (140), the controls' canvas item is drawn as the last item of the UI layer's canvas, so
/// the two are one render pass over the screen instead of two (<see cref="Engine_CanvasLean"/>).
/// </summary>
/// <remarks>
/// <para>THE SAME PICTURE. Both layers draw with the identity transform into the root viewport, so
/// the controls land on the same pixels; at the canvas's top z they draw after the UI composite,
/// as they did one layer up; and with every layer between them empty nothing else was drawn
/// between the two. A fade, the loading screen, a movie, photo mode's filter, the dev menu or the
/// perf overlay showing puts the controls back on their own layer before that frame is drawn.</para>
/// <para>DONE IN THE RENDERING SERVER ONLY, just before the frame is drawn (frame_pre_draw), after
/// every node has set this frame's visibility. The node keeps its parent and layer; Godot's own
/// re-parenting (a re-entry, a top-level change) wins, and the share is asserted again each frame.
/// Nothing in the touch controls changes: their input, layout and drawing are untouched.</para>
/// <para>The layers are found once (a native search) and then from the tree's node-added signal,
/// connected only while the setting is on.</para>
/// </remarks>
public partial class AndroidCanvasLean : Node
{
    /// <summary>The touch controls, a direct child of their own CanvasLayer.</summary>
    public Control? Touch { get; set; }

    /// <summary>The UI layer the controls may share (GameUi's `UiLayer`), once it exists.</summary>
    public System.Func<CanvasLayer?>? Target { get; set; }

    private readonly HashSet<CanvasLayer> _layers = new();
    private readonly List<Engine_CanvasLean.OtherLayer> _others = new();
    private bool _watching, _connected;
    private CanvasLayer? _sharedWith;

    public override void _EnterTree()
    {
        if (!_connected)
        {
            RenderingServer.FramePreDraw += BeforeDraw;
            _connected = true;
        }
    }

    public override void _ExitTree()
    {
        if (_connected)
        {
            RenderingServer.FramePreDraw -= BeforeDraw;
            _connected = false;
        }

        Watch(false);
        Unshare("exiting");
    }

    private void BeforeDraw()
    {
        if (!IsInsideTree())
        {
            return;
        }

        if (!Engine_CanvasLean.Enabled)
        {
            Watch(false);
            Unshare(null);
            return;
        }

        Watch(true);
        string? blocker = Blocking(out CanvasLayer? target);
        if (blocker is not null || target is null)
        {
            Unshare(blocker ?? "no UI layer");
            return;
        }

        Share(target);
    }

    /// <summary>Why the controls cannot share the UI layer this frame, or null with the layer they can.</summary>
    private string? Blocking(out CanvasLayer? target)
    {
        target = Target?.Invoke();
        if (Touch is not { } touch || !GodotObject.IsInstanceValid(touch) || !touch.IsInsideTree())
        {
            return "no touch controls";
        }

        if (touch.GetParent() is not CanvasLayer own || target is null || !GodotObject.IsInstanceValid(target) ||
            !target.IsInsideTree())
        {
            return "no UI layer";
        }

        Viewport? into = Into(own);
        if (into is null || Into(target) != into)
        {
            return "viewport";
        }

        if (!target.Visible || target.Transform != own.Transform || target.FollowViewportEnabled ||
            own.FollowViewportEnabled || touch.TopLevel)
        {
            return "layer state";
        }

        _others.Clear();
        _layers.RemoveWhere(layer => !GodotObject.IsInstanceValid(layer));
        foreach (CanvasLayer layer in _layers)
        {
            if (layer == own || layer == target || !layer.IsInsideTree() || Into(layer) != into)
            {
                continue;
            }

            // Only a layer that shows can block, so only those are named (a string each).
            int index = layer.Layer;
            if (index >= target.Layer && index <= own.Layer && layer.Visible && Shows(layer))
            {
                _others.Add(new Engine_CanvasLean.OtherLayer(layer.Name, index, true));
            }
        }

        return Engine_CanvasLean.FirstBetween(target.Layer, own.Layer, _others);
    }

    /// <summary>The viewport a layer draws into: its custom one, else the one it stands in.</summary>
    private static Viewport? Into(CanvasLayer layer) =>
        layer.CustomViewport is Viewport custom && GodotObject.IsInstanceValid(custom) ? custom : layer.GetViewport();

    /// <summary>
    /// Whether any canvas item of the layer's canvas is visible: its items, and the items under its
    /// plain nodes (which join the same canvas), not a sub-viewport's or a nested layer's. A visible
    /// item that draws nothing counts too: the test errs toward the controls' own layer.
    /// </summary>
    private static bool Shows(Node node)
    {
        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            Node child = node.GetChild(i);
            switch (child)
            {
                case CanvasItem item:
                    if (item.Visible) return true;
                    break;
                case Viewport or CanvasLayer:
                    break;
                default:
                    if (Shows(child)) return true;
                    break;
            }
        }

        return false;
    }

    private void Share(CanvasLayer target)
    {
        if (Touch is not { } touch)
        {
            return;
        }

        Rid item = touch.GetCanvasItem();
        // Again each frame: Godot puts the item back on its own layer's canvas whenever the node
        // re-enters its canvas, and a set to the same parent only re-sorts that canvas's children.
        RenderingServer.CanvasItemSetParent(item, target.GetCanvas());
        RenderingServer.CanvasItemSetZIndex(item, (int)RenderingServer.CanvasItemZMax);
        if (_sharedWith != target)
        {
            if (_sharedWith is null)
            {
                touch.TreeExiting += OnTouchExiting;
            }
            else if (GodotObject.IsInstanceValid(_sharedWith))
            {
                _sharedWith.TreeExiting -= OnTargetExiting;
            }

            target.TreeExiting += OnTargetExiting;
            _sharedWith = target;
            GD.Print($"[canvas-lean] touch controls drawn in {target.Name}'s pass");
        }

        Engine_CanvasLean.SharedInto = target.Name;
        Engine_CanvasLean.SharedFrom = touch.GetParent()?.Name;
        Engine_CanvasLean.Blocker = null;
    }

    /// <summary>Back on the controls' own layer's canvas (the one their node is in), at their own z.</summary>
    private void Unshare(string? why)
    {
        Engine_CanvasLean.SharedInto = null;
        Engine_CanvasLean.SharedFrom = null;
        Engine_CanvasLean.Blocker = why;
        if (_sharedWith is null)
        {
            return;
        }

        if (GodotObject.IsInstanceValid(_sharedWith))
        {
            _sharedWith.TreeExiting -= OnTargetExiting;
        }

        _sharedWith = null;
        if (Touch is { } touch && GodotObject.IsInstanceValid(touch))
        {
            touch.TreeExiting -= OnTouchExiting;
            Rid item = touch.GetCanvasItem();
            RenderingServer.CanvasItemSetZIndex(item, touch.ZIndex);
            if (touch.IsInsideTree())
            {
                RenderingServer.CanvasItemSetParent(item, touch.GetCanvas());
            }
        }

        GD.Print($"[canvas-lean] touch controls on their own layer ({why ?? "off"})");
    }

    /// <summary>The UI layer leaving frees or detaches its canvas: the controls go home first.</summary>
    private void OnTargetExiting() => Unshare("UI layer left");

    /// <summary>Godot clears the item's parent on exit and restores it on entry; the z is ours to undo.</summary>
    private void OnTouchExiting() => Unshare("touch controls left");

    private void Watch(bool on)
    {
        if (on == _watching || !IsInsideTree())
        {
            return;
        }

        _watching = on;
        SceneTree tree = GetTree();
        _layers.Clear();
        if (!on)
        {
            tree.NodeAdded -= OnNodeAdded;
            return;
        }

        tree.NodeAdded += OnNodeAdded;
        foreach (Node found in tree.Root.FindChildren("*", nameof(CanvasLayer), recursive: true, owned: false))
        {
            if (found is CanvasLayer layer) _layers.Add(layer);
        }
    }

    private void OnNodeAdded(Node node)
    {
        if (node is CanvasLayer layer) _layers.Add(layer);
    }
}
