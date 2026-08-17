using System;
using System.Collections.Generic;
using Godot;
using Refii.FlashGames.Framework;

namespace Refii.FlashGames;

public sealed class SwfScene
{
    private sealed class Cached
    {
        public ImageTexture? Texture;
        public Vector2 Offset;
        public Vector2 Scale = Vector2.One;
    }

    private readonly SwfStage _stage;
    private readonly Dictionary<int, Cached> _shapes = new();
    private readonly Dictionary<SwfClip, Node2D> _nodes = new();
    private readonly Dictionary<SwfClip, string> _textContent = new();
    private readonly HashSet<Node2D> _containers = new();

    public SwfScene(SwfStage stage, Node2D root, float textureScale = 1f)
    {
        _stage = stage;
        Root = root;
        _textureScale = Mathf.Clamp(textureScale, 0.25f, 8f);
    }

    public Node2D Root { get; }

    public float TextureScale
    {
        get => _textureScale;

        set
        {
            float wanted = Mathf.Clamp(value, 0.25f, 8f);
            if (Mathf.IsEqualApprox(wanted, _textureScale))
            {
                return;
            }

            _textureScale = wanted;
            Rebuild();
        }
    }

    private float _textureScale;

    public int MaxTextureEdge { get; set; } = 2048;

    public bool Vector { get; set; } = true;

    public int DrawnNodes { get; private set; }

    private void Rebuild()
    {
        foreach (Node2D container in _containers)
        {
            if (!GodotObject.IsInstanceValid(container))
            {
                continue;
            }

            container.GetParent()?.RemoveChild(container);
            container.QueueFree();
        }

        _containers.Clear();
        _nodes.Clear();
        _shapes.Clear();
        _textContent.Clear();
    }

    public void Sync()
    {
        DrawnNodes = 0;
        SyncClip(_stage.Root, Root);
    }

    private void SyncClip(SwfClip clip, Node2D parent)
    {
        int index = 0;
        foreach (SwfClip child in clip.Children.Values)
        {
            Node2D node = NodeFor(child, parent, index);
            if (node.GetIndex() != index)
            {
                parent.MoveChild(node, index);
            }

            index++;

            node.Visible = child.Visible && child.Alpha > 0f;
            if (!node.Visible)
            {
                continue;
            }

            DrawnNodes++;
            node.Transform = new Transform2D(
                Mathf.DegToRad(child.Rotation),
                new Vector2(child.XScale / 100f, child.YScale / 100f),
                0f,
                new Vector2(child.X, child.Y));

            node.Modulate = Tint(child);

            if (_stage.Document.EditTexts.ContainsKey(child.CharacterId))
            {
                RefreshText(child, node);
                continue;
            }

            if (_stage.Document.Shapes.ContainsKey(child.CharacterId))
            {
                continue;
            }

            SyncClip(child, node);
        }

        for (int i = parent.GetChildCount() - 1; i >= index; i--)
        {
            if (parent.GetChild(i) is not Node2D extra || !_containers.Contains(extra))
            {
                continue;
            }

            _containers.Remove(extra);
            parent.RemoveChild(extra);
            extra.QueueFree();
        }
    }

    private Color Tint(SwfClip clip)
    {
        SwfColorTransform t = clip.Tint;
        return new Color(
            Mathf.Clamp(t.RedMultiply, 0f, 1f),
            Mathf.Clamp(t.GreenMultiply, 0f, 1f),
            Mathf.Clamp(t.BlueMultiply, 0f, 1f),
            Mathf.Clamp(t.AlphaMultiply * (clip.Alpha / 100f), 0f, 1f));
    }

    private Node2D NodeFor(SwfClip clip, Node2D parent, int index)
    {
        if (_nodes.TryGetValue(clip, out Node2D? existing) &&
            existing is not null && GodotObject.IsInstanceValid(existing))
        {
            return existing;
        }

        Node2D node = Build(clip);
        _containers.Add(node);
        parent.AddChild(node);
        if (index < parent.GetChildCount() - 1)
        {
            parent.MoveChild(node, index);
        }

        _nodes[clip] = node;
        return node;
    }

    public const int BlendMultiply = 3;
    public const int BlendScreen = 4;
    public const int BlendLighten = 5;
    public const int BlendAdd = 8;
    public const int BlendSubtract = 9;

    private static CanvasItemMaterial? MaterialFor(int blendMode)
    {
        CanvasItemMaterial.BlendModeEnum mode = blendMode switch
        {
            BlendMultiply => CanvasItemMaterial.BlendModeEnum.Mul,
            BlendScreen => CanvasItemMaterial.BlendModeEnum.Add,
            BlendLighten => CanvasItemMaterial.BlendModeEnum.Add,
            BlendAdd => CanvasItemMaterial.BlendModeEnum.Add,
            BlendSubtract => CanvasItemMaterial.BlendModeEnum.Sub,
            _ => CanvasItemMaterial.BlendModeEnum.Mix,
        };

        return mode == CanvasItemMaterial.BlendModeEnum.Mix
            ? null
            : new CanvasItemMaterial { BlendMode = mode };
    }

    private Node2D Build(SwfClip clip)
    {
        var container = new Node2D();
        CanvasItemMaterial? material = MaterialFor(clip.BlendMode);
        if (material is not null)
        {
            container.Material = material;
        }

        if (_stage.Document.EditTexts.ContainsKey(clip.CharacterId))
        {
            container.AddChild(new Sprite2D { Centered = false });
            return container;
        }

        if (!_stage.Document.Shapes.ContainsKey(clip.CharacterId))
        {
            return container;
        }

        if (Vector)
        {
            Node2D? built = VectorShape(clip.CharacterId);
            if (built is not null)
            {
                container.AddChild(built);
                return container;
            }
        }

        Cached cached = ShapeTexture(clip.CharacterId);
        if (cached.Texture is null)
        {
            return container;
        }

        container.AddChild(new Sprite2D
        {
            Centered = false,
            Texture = cached.Texture,
            Position = cached.Offset,
            Scale = cached.Scale,
        });

        return container;
    }

    private void RefreshText(SwfClip clip, Node2D container)
    {
        if (container.GetChildCount() == 0 || container.GetChild(0) is not Sprite2D sprite)
        {
            return;
        }

        if (!_stage.Document.EditTexts.TryGetValue(clip.CharacterId, out SwfEditText? field))
        {
            return;
        }

        string content = clip.Text.Length != 0 ? clip.Text : field.InitialText;
        if (_textContent.TryGetValue(clip, out string? previous) && previous == content)
        {
            return;
        }

        _textContent[clip] = content;

        SwfFont? font = _stage.Fonts?.Resolve(field.FontId)
            ?? _stage.Document.Fonts.GetValueOrDefault(field.FontId);
        if (content.Length == 0 || font is null || font.Glyphs.Count == 0)
        {
            sprite.Texture = null;
            return;
        }

        float pixels = SwfShapeGeometry.TwipsPerPixel;
        int width = Mathf.Clamp(
            Mathf.CeilToInt(field.Bounds.Width / pixels * TextureScale), 1, MaxTextureEdge);
        int height = Mathf.Clamp(
            Mathf.CeilToInt((field.Bounds.Height + field.FontHeight) / pixels * TextureScale),
            1, MaxTextureEdge);

        var canvas = new SwfCanvas(width, height, new Color(0f, 0f, 0f, 0f));
        float scale = TextureScale / pixels;
        var placement = new Transform2D(
            new Vector2(scale, 0f),
            new Vector2(0f, scale),
            new Vector2(-field.Bounds.XMin, -field.Bounds.YMin) * scale);

        SwfTextRenderer.Draw(
            canvas, field, font, content, placement, SwfColorTransform.Identity);

        sprite.Texture = ImageTexture.CreateFromImage(canvas.ToImage());
        sprite.Position = new Vector2(field.Bounds.XMin, field.Bounds.YMin) / pixels;
        sprite.Scale = Vector2.One / TextureScale;
    }

    private readonly Dictionary<int, List<SwfVectorPart>> _vectors = new();

    private readonly Dictionary<int, Texture2D?> _hostTextures = new();

    private readonly Dictionary<int, Texture2D?> _embeddedTextures = new();

    private Node2D? VectorShape(int characterId)
    {
        if (!_vectors.TryGetValue(characterId, out List<SwfVectorPart>? parts))
        {
            parts = new List<SwfVectorPart>();
            if (_stage.Document.Shapes.TryGetValue(characterId, out SwfShape? shape) &&
                _stage.Document.Geometry.TryGetValue(characterId, out SwfGeometry? geometry))
            {
                parts = SwfVectorShape.Build(shape, geometry, HostTexture, EmbeddedTexture);
            }

            _vectors[characterId] = parts;
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var root = new Node2D { Name = "Vector" };
        foreach (SwfVectorPart part in parts)
        {
            var instance = new MeshInstance2D { Mesh = part.Mesh, Texture = part.Texture };
            instance.TextureRepeat = part.Repeat
                ? CanvasItem.TextureRepeatEnum.Enabled
                : CanvasItem.TextureRepeatEnum.Disabled;
            root.AddChild(instance);
        }

        return root;
    }

    private Texture2D? HostTexture(int characterId) =>
        Resolve(_hostTextures, characterId, () => _stage.HostResolver?.Invoke(characterId));

    private Texture2D? EmbeddedTexture(int characterId) =>
        Resolve(
            _embeddedTextures, characterId,
            () => _stage.Document.Bitmaps.GetValueOrDefault(characterId));

    private static Texture2D? Resolve(
        Dictionary<int, Texture2D?> cache, int characterId, Func<Image?> load)
    {
        if (cache.TryGetValue(characterId, out Texture2D? cached))
        {
            return cached;
        }

        Image? image = load();
        Texture2D? texture = image is null ? null : ImageTexture.CreateFromImage(image);
        cache[characterId] = texture;
        return texture;
    }

    private Cached ShapeTexture(int characterId)
    {
        if (_shapes.TryGetValue(characterId, out Cached? cached))
        {
            return cached;
        }

        cached = new Cached();
        _shapes[characterId] = cached;

        if (!_stage.Document.Shapes.TryGetValue(characterId, out SwfShape? shape) ||
            !_stage.Document.Geometry.TryGetValue(characterId, out SwfGeometry? geometry))
        {
            return cached;
        }

        float pixels = SwfShapeGeometry.TwipsPerPixel;
        float widthPx = shape.Bounds.Width / pixels;
        float heightPx = shape.Bounds.Height / pixels;
        if (widthPx <= 0f || heightPx <= 0f)
        {
            return cached;
        }

        int width = Mathf.Clamp(Mathf.CeilToInt(widthPx * TextureScale), 1, MaxTextureEdge);
        int height = Mathf.Clamp(Mathf.CeilToInt(heightPx * TextureScale), 1, MaxTextureEdge);

        float scaleX = width / (shape.Bounds.Width == 0 ? 1f : shape.Bounds.Width);
        float scaleY = height / (shape.Bounds.Height == 0 ? 1f : shape.Bounds.Height);

        var canvas = new SwfCanvas(width, height, new Color(0f, 0f, 0f, 0f));
        var placement = new Transform2D(
            new Vector2(scaleX, 0f),
            new Vector2(0f, scaleY),
            new Vector2(-shape.Bounds.XMin * scaleX, -shape.Bounds.YMin * scaleY));

        SwfRasterizer.DrawInto(
            canvas, shape, geometry, placement, SwfColorTransform.Identity,
            _stage.EmbeddedSampler, _stage.HostSampler);

        cached.Texture = ImageTexture.CreateFromImage(canvas.ToImage());
        cached.Offset = new Vector2(shape.Bounds.XMin, shape.Bounds.YMin) / pixels;
        cached.Scale = new Vector2(widthPx / width, heightPx / height);
        return cached;
    }
}
