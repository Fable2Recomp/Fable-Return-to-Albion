using System;
using System.Collections.Generic;

using Godot;

using Refii.Titles.Fable2.PubGames;
using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames;

public sealed class SwfSceneClip : IPubClip
{
    private readonly SwfSceneUi _scene;
    private readonly Node2D _node;
    private SwfSceneClip? _parent;
    private int _depth;
    private string _name;
    private readonly int _characterId;

    private SortedDictionary<int, SwfSceneClip>? _children;
    private Dictionary<string, SwfSceneClip>? _byName;

    private Label? _text;
    private bool _textLooked;
    private string _textFallback = string.Empty;

    private AnimationPlayer? _timeline;
    private bool _timelineLooked;

    /// <summary>
    /// The template this clip's node was copied from by a timeline placement, while the node may
    /// be kept for that template's next placement (<see cref="SwfSceneUi.TakeSpare"/>); null for
    /// any other clip, and once the game has been handed this clip.
    /// </summary>
    private Node2D? _spareOf;

    /// <summary><see cref="SwfSceneUi.TintGeneration"/> when the node was copied.</summary>
    private int _spareGeneration;

    private readonly SwfFrameTable? _table;
    private readonly int[] _stops;
    private readonly Dictionary<string, int>? _labels;

    /// <summary>The node's frame count, read once: the animations ask for it every frame.</summary>
    private readonly int _totalFrames;
    private int _frame = 1;
    private bool _playing;
    private double _accumulated;

    internal SwfSceneClip(
        SwfSceneUi scene, Node2D node, SwfSceneClip? parent, string name, int depth)
    {
        _scene = scene;
        _node = node;
        _parent = parent;
        _name = name;
        _depth = depth;
        _characterId = node.HasMeta(SwfSceneMeta.Character)
            ? (int)node.GetMeta(SwfSceneMeta.Character)
            : -1;
        _totalFrames = node.HasMeta(SwfSceneMeta.FrameCount)
            ? Math.Max(1, (int)node.GetMeta(SwfSceneMeta.FrameCount))
            : 1;

        // A clip of a library sprite reads its character's shared timeline; only the stage's own
        // clips, which are never duplicated, still carry theirs as metadata, read here once.
        SwfSceneUi.SharedTimeline? shared = scene.TimelineOf(_characterId);
        _table = SwfSceneUi.TableOf(node) ?? shared?.Table;
        _stops = node.HasMeta(SwfSceneMeta.Stops)
            ? node.GetMeta(SwfSceneMeta.Stops).AsInt32Array()
            : shared?.Stops ?? Array.Empty<int>();
        _labels = SwfSceneUi.LabelsOf(node) ?? shared?.Labels;

        if (_table is not null && TotalFrames > 1 && Array.IndexOf(_stops, 1) < 0)
        {
            _playing = true;
            _scene.SetTicking(this, true);
        }
    }

    public Node2D Node => _node;

    public int Depth => _depth;

    public int CharacterId => _characterId;

    public IEnumerable<SwfSceneClip> Children
    {
        get
        {
            EnsureChildren();
            foreach (SwfSceneClip child in _children!.Values)
            {
                child._spareOf = null;
            }

            return _children.Values;
        }
    }

    /// <summary>The children as <see cref="Children"/> gives them, without handing them out; for tests.</summary>
    internal IEnumerable<SwfSceneClip> ChildrenUnshared()
    {
        EnsureChildren();
        return _children!.Values;
    }

    public string Name => _name;

    private bool Gone => !GodotObject.IsInstanceValid(_node);

    public float X
    {
        get => Gone ? 0f : _node.Position.X;
        set
        {
            if (!Gone)
            {
                _node.Position = new Vector2(value, _node.Position.Y);
            }
        }
    }

    public float Y
    {
        get => Gone ? 0f : _node.Position.Y;
        set
        {
            if (!Gone)
            {
                _node.Position = new Vector2(_node.Position.X, value);
            }
        }
    }

    public float Alpha
    {
        get => Gone ? 0f : _node.Modulate.A * 100f;
        set
        {
            if (Gone)
            {
                return;
            }

            Color m = _node.Modulate;
            m.A = Mathf.Clamp(value / 100f, 0f, 1f);
            _node.Modulate = m;
        }
    }

    public float XScale
    {
        get => Gone ? 100f : _node.Scale.X * 100f;
        set
        {
            if (!Gone)
            {
                _node.Scale = new Vector2(value / 100f, _node.Scale.Y);
            }
        }
    }

    public float YScale
    {
        get => Gone ? 100f : _node.Scale.Y * 100f;
        set
        {
            if (!Gone)
            {
                _node.Scale = new Vector2(_node.Scale.X, value / 100f);
            }
        }
    }

    public float Rotation
    {
        get => Gone ? 0f : Mathf.RadToDeg(_node.Rotation);
        set
        {
            if (!Gone)
            {
                _node.Rotation = Mathf.DegToRad(value);
            }
        }
    }

    public bool Visible
    {
        get => !Gone && _node.Visible;
        set
        {
            if (!Gone)
            {
                _node.Visible = value;
            }
        }
    }

    /// <summary>
    /// A MovieClip colour transform: <c>out = src * multiply + offset / 255</c> per channel, alpha
    /// untouched (the SWF's tints pass alphaMultiplier 1). A shader on the clip, inherited by its
    /// whole subtree, because Modulate can only multiply.
    /// </summary>
    public void SetTint(
        float redMultiply, float greenMultiply, float blueMultiply,
        float redOffset, float greenOffset, float blueOffset)
    {
        if (Gone)
        {
            return;
        }

        if (_node.Material is not ShaderMaterial tint || tint.Shader != TintShader)
        {
            if (_node.Material is not null)
            {
                return;
            }

            tint = new ShaderMaterial { Shader = TintShader };
            _node.Material = tint;
        }

        Color modulate = _node.Modulate;
        _node.Modulate = new Color(1f, 1f, 1f, modulate.A);
        tint.SetShaderParameter("tint_multiply", new Vector3(redMultiply, greenMultiply, blueMultiply));
        tint.SetShaderParameter(
            "tint_offset", new Vector3(redOffset, greenOffset, blueOffset) / 255f);
        InheritMaterial(_node);
        _scene.TintGeneration++;
    }

    private static readonly Shader TintShader = new()
    {
        Code = """
            shader_type canvas_item;
            uniform vec3 tint_multiply = vec3(1.0);
            uniform vec3 tint_offset = vec3(0.0);
            void fragment() {
                COLOR.rgb = COLOR.rgb * tint_multiply + tint_offset;
            }
            """,
    };

    /// <summary>Hands the clip's material to every descendant that has none of its own.</summary>
    private static void InheritMaterial(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is CanvasItem item && item.Material is null)
            {
                item.UseParentMaterial = true;
            }

            InheritMaterial(child);
        }
    }

    /// <summary>
    /// <c>TextField.setTextFormat</c>: the colour, size and alignment a format states; unset
    /// members leave the field as it is.
    /// </summary>
    public void SetTextFormat(PubTextFormat format)
    {
        Label? label = TextLabel();
        if (label is null)
        {
            return;
        }

        if (format.Color is { } rgb)
        {
            label.AddThemeColorOverride(
                "font_color",
                new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f));
        }

        if (format.Size is { } size && size > 0)
        {
            label.AddThemeFontSizeOverride("font_size", size);
        }

        switch (format.Align)
        {
            case "left":
                label.HorizontalAlignment = HorizontalAlignment.Left;
                break;
            case "center":
                label.HorizontalAlignment = HorizontalAlignment.Center;
                break;
            case "right":
                label.HorizontalAlignment = HorizontalAlignment.Right;
                break;
            case "justify":
                label.HorizontalAlignment = HorizontalAlignment.Fill;
                break;
        }
    }

    public string Text
    {
        get => TextLabel()?.Text ?? _textFallback;
        set
        {
            Label? label = TextLabel();
            if (label is not null)
            {
                label.Text = value;
            }
            else
            {
                _textFallback = value;
            }
        }
    }

    public float Height => TextLabel()?.Size.Y ?? 0f;

    public float TextHeight
    {
        get
        {
            Label? label = TextLabel();
            if (label is null || label.Text.Length == 0)
            {
                return 0f;
            }

            return label.GetLineCount() * label.GetLineHeight();
        }
    }

    public int CurrentFrame
    {
        get
        {
            if (_table is not null)
            {
                return _frame;
            }

            AnimationPlayer? player = Timeline();
            if (player is null || player.AssignedAnimation != Engine_SwfScene.TimelineName)
            {
                return _frame;
            }

            return Math.Clamp(
                (int)Math.Round(player.CurrentAnimationPosition * _scene.FrameRate) + 1, 1, TotalFrames);
        }
    }

    public int TotalFrames => Gone ? 1 : _totalFrames;

    public void GotoAndStop(int frame) => Seek(frame, play: false);

    public void GotoAndStop(string label)
    {
        int frame = FrameOfLabel(label);
        if (frame > 0)
        {
            Seek(frame, play: false);
        }
    }

    public void GotoAndPlay(int frame) => Seek(frame, play: true);

    public void Play()
    {
        if (TotalFrames <= 1)
        {
            return;
        }

        if (_table is not null)
        {
            SetPlaying(true);
            return;
        }

        Timeline()?.Play(Engine_SwfScene.TimelineName);
    }

    public void Stop()
    {
        if (_table is not null)
        {
            SetPlaying(false);
            return;
        }

        Timeline()?.Pause();
    }

    public IPubClip? GetChild(string name)
    {
        EnsureChildren();
        SwfSceneClip? child = _byName!.GetValueOrDefault(name);
        if (child is not null)
        {
            child._spareOf = null;
        }

        return child;
    }

    public IPubClip? AttachMovie(string exportName, string instanceName, int depth)
    {
        Node2D? template = _scene.Template(exportName);
        if (template is null)
        {
            return null;
        }

        return Attach(template, instanceName, depth);
    }

    public IPubClip CreateEmptyMovieClip(string name, int depth)
    {
        var node = new Node2D { Name = Engine_SwfScene.SafeName(name) };
        node.SetMeta(SwfSceneMeta.Character, -1);
        return Place(node, name, depth);
    }

    public int GetDepth() => _depth;

    public void RemoveMovieClip() => _parent?.Remove(this);

    public void SwapDepths(int depth)
    {
        if (_parent is null || depth == _depth)
        {
            return;
        }

        _parent.SwapChild(this, depth);
    }

    public int GetNextHighestDepth()
    {
        EnsureChildren();
        var highest = 0;
        foreach (int depth in _children!.Keys)
        {
            if (depth >= highest)
            {
                highest = depth + 1;
            }
        }

        return highest;
    }

    internal void Tick(double deltaSeconds)
    {
        if (!_playing || _table is null || !GodotObject.IsInstanceValid(_node))
        {
            SetPlaying(false);
            return;
        }

        _accumulated += deltaSeconds;
        double step = 1.0 / _scene.FrameRate;
        while (_accumulated >= step && _playing)
        {
            _accumulated -= step;
            int next = _frame >= TotalFrames ? 1 : _frame + 1;
            RebuildFrame(next);
            if (Array.IndexOf(_stops, next) >= 0)
            {
                SetPlaying(false);
            }
        }
    }

    private SwfSceneClip Attach(Node2D template, string name, int depth, bool placed = false)
    {
        // A timeline placement of a static template takes a copy an earlier placement let go of,
        // put back as the template stands, rather than copying the template again: an idle board
        // re-places hundreds of small shapes a frame (Fortune's Tower), and copying them was most
        // of a phone's frame. See SwfSceneUi.Spare for what makes a copy safe to keep.
        bool spare = placed && _scene.Spare(template);
        Node2D? copy = spare ? _scene.TakeSpare(template) : null;
        if (copy is not null)
        {
            // The properties Duplicate copies, not Transform, whose setter re-derives them.
            copy.Position = template.Position;
            copy.Rotation = template.Rotation;
            copy.Scale = template.Scale;
            copy.Skew = template.Skew;
            copy.Modulate = template.Modulate;
        }
        else
        {
            copy = (Node2D)template.Duplicate();
        }

        copy.Name = _scene.NodeName(name);
        copy.Visible = true;
        SwfSceneClip clip = Place(copy, name, depth);
        _scene.Attached(template, copy);
        if (spare)
        {
            clip._spareOf = template;
            clip._spareGeneration = _scene.TintGeneration;
        }

        return clip;
    }

    private SwfSceneClip Place(Node2D node, string name, int depth)
    {
        EnsureChildren();
        if (_children!.TryGetValue(depth, out SwfSceneClip? occupant))
        {
            Remove(occupant);
        }

        node.SetMeta(SwfSceneMeta.Depth, depth);
        _node.AddChild(node);

        SwfSceneClip clip = _scene.Wrap(node, this, name, depth);
        clip._parent = this;
        clip._depth = depth;
        _children[depth] = clip;
        if (name.Length != 0)
        {
            _byName![name] = clip;
        }

        Reorder();
        return clip;
    }

    private void Remove(SwfSceneClip clip)
    {
        EnsureChildren();
        _children!.Remove(clip._depth);
        if (_byName!.TryGetValue(clip._name, out SwfSceneClip? named) && named == clip)
        {
            _byName.Remove(clip._name);
        }

        clip._parent = null;
        clip.ForgetSubtree();
        if (GodotObject.IsInstanceValid(clip._node))
        {
            clip._node.GetParent()?.RemoveChild(clip._node);
            if (clip._spareOf is { } template && clip._children is null &&
                clip._spareGeneration == _scene.TintGeneration)
            {
                clip._spareOf = null;
                _scene.KeepSpare(template, clip._node);
                return;
            }

            clip._node.QueueFree();
        }
    }

    private void ForgetSubtree()
    {
        if (_children is not null)
        {
            foreach (SwfSceneClip child in _children.Values)
            {
                child.ForgetSubtree();
            }
        }

        _scene.Forget(_node);
    }

    private void SwapChild(SwfSceneClip clip, int depth)
    {
        EnsureChildren();
        int from = clip._depth;
        _children!.TryGetValue(depth, out SwfSceneClip? other);
        _children.Remove(from);

        if (other is not null)
        {
            other._depth = from;
            other._node.SetMeta(SwfSceneMeta.Depth, from);
            _children[from] = other;
        }

        clip._depth = depth;
        clip._node.SetMeta(SwfSceneMeta.Depth, depth);
        _children[depth] = clip;
        Reorder();
    }

    private void Reorder()
    {
        var index = 0;
        foreach (SwfSceneClip child in _children!.Values)
        {
            if (child._node.GetParent() == _node)
            {
                _node.MoveChild(child._node, index);
            }

            index++;
        }
    }

    private void EnsureChildren()
    {
        if (Gone)
        {
            _children ??= new SortedDictionary<int, SwfSceneClip>();
            _byName ??= new Dictionary<string, SwfSceneClip>();
            return;
        }

        if (_children is not null)
        {
            return;
        }

        _children = new SortedDictionary<int, SwfSceneClip>();
        _byName = new Dictionary<string, SwfSceneClip>(StringComparer.Ordinal);

        var fallback = 0;
        foreach (Node child in _node.GetChildren())
        {
            if (child is not Node2D node2D || !node2D.HasMeta(SwfSceneMeta.Character))
            {
                continue;
            }

            int depth = node2D.HasMeta(SwfSceneMeta.Depth)
                ? (int)node2D.GetMeta(SwfSceneMeta.Depth)
                : fallback;
            while (_children.ContainsKey(depth))
            {
                depth++;
            }

            fallback = depth + 1;

            SwfSceneClip clip = _scene.Existing(node2D) ?? _scene.Wrap(node2D, this, node2D.Name, depth);
            clip._parent = this;
            clip._depth = depth;
            _children[depth] = clip;
            _byName[clip._name] = clip;
        }
    }

    private Label? TextLabel()
    {
        if (Gone)
        {
            return null;
        }

        if (!_textLooked)
        {
            _textLooked = true;
            _text = _node.GetNodeOrNull<Label>(Engine_SwfScene.TextName);
        }

        return _text;
    }

    private AnimationPlayer? Timeline()
    {
        if (Gone)
        {
            return null;
        }

        if (!_timelineLooked)
        {
            _timelineLooked = true;
            _timeline = _node.GetNodeOrNull<AnimationPlayer>(Engine_SwfScene.TimelineName);
        }

        return _timeline;
    }

    private void Seek(int frame, bool play)
    {
        if (Gone)
        {
            return;
        }

        int total = TotalFrames;
        frame = Math.Clamp(frame, 1, total);

        if (_table is not null)
        {
            RebuildFrame(frame);
            SetPlaying(play && total > 1 && Array.IndexOf(_stops, frame) < 0);
            return;
        }

        _frame = frame;
        AnimationPlayer? player = Timeline();
        if (player is null || total <= 1)
        {
            return;
        }

        if (player.AssignedAnimation != Engine_SwfScene.TimelineName || (play && !player.IsPlaying()))
        {
            player.Play(Engine_SwfScene.TimelineName);
        }

        player.Seek((frame - 1) / _scene.FrameRate, update: true);
        if (!play)
        {
            player.Pause();
        }
    }

    private void SetPlaying(bool playing)
    {
        if (_playing == playing)
        {
            return;
        }

        _playing = playing;
        _accumulated = 0;
        _scene.SetTicking(this, playing);
    }

    /// <summary>Stops this clip's timeline; see <see cref="SwfSceneUi.Release"/>.</summary>
    /// <remarks>The table is managed now, and goes with the clip; it once held a native array.</remarks>
    internal void ReleaseTimeline() => _playing = false;

    private void RebuildFrame(int frame)
    {
        _frame = frame;
        if (_scene.Released || _table is null || frame < 1 || frame > _table.Count)
        {
            return;
        }

        EnsureChildren();
        foreach (SwfPlacement place in _table.Placements(frame))
        {
            int depth = place.Depth;
            SwfSceneClip? child = _children!.GetValueOrDefault(depth);
            if (child is null || child._characterId != place.Character)
            {
                Node2D? template = _scene.Template(place.Character);
                if (template is null)
                {
                    continue;
                }

                child = Attach(template, place.Name ?? $"d{depth}", depth, placed: true);
            }

            ApplyPlacement(child._node, place);
        }

        List<SwfSceneClip>? stale = null;
        foreach ((int depth, SwfSceneClip child) in _children!)
        {
            if (!_table.Places(frame, depth))
            {
                (stale ??= new List<SwfSceneClip>()).Add(child);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (SwfSceneClip child in stale)
        {
            Remove(child);
        }
    }

    private static void ApplyPlacement(Node2D node, in SwfPlacement place)
    {
        if (place.Matrix is { } matrix)
        {
            node.Transform = matrix;
        }

        if (place.Tint is { } tint)
        {
            node.Modulate = tint;
        }
    }

    private int FrameOfLabel(string label)
    {
        if (_labels is not null && _labels.TryGetValue(label, out int labelled))
        {
            return labelled;
        }

        AnimationPlayer? player = Timeline();
        if (player is null || !player.HasAnimation(Engine_SwfScene.TimelineName))
        {
            return -1;
        }

        Animation animation = player.GetAnimation(Engine_SwfScene.TimelineName);
        if (!animation.HasMarker(label))
        {
            return -1;
        }

        return (int)Math.Round(animation.GetMarkerTime(label) * _scene.FrameRate) + 1;
    }
}
