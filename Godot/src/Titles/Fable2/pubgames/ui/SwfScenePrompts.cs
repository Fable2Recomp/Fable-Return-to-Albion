using System;
using System.Collections.Generic;

using Godot;

using Refii.Titles.Fable2.PubGames.Framework;
using Refii.Ui;

namespace Refii.Titles.Fable2.PubGames;

/// <summary>
/// A stage's button prompts in the player's glyphs: every placed prompt shape
/// (<see cref="PubGamePrompts"/>) keeps the SWF's own art while that is the shipped Xbox art
/// (<see cref="Engine_UiKeyGlyphs.PromptShowsShipped"/>), and otherwise draws the core's glyph for
/// its button (<see cref="Engine_UiKeyGlyphs.KeycapFor"/>: the pad glyph, or on the keyboard the
/// key the player bound) in the shape's bounds, the shape's own parts hidden.
/// </summary>
/// <remarks>
/// A prompt shape is a one-frame sprite: every child of it is drawn. Its glyph is one more child,
/// a <see cref="Sprite2D"/> without a character, so the clip tree never wraps it, and it takes the
/// shape's transform, alpha and material as the parts do. A shape never drawn is untouched. The
/// library's templates stay as built; a clip attached from one is looked through as it lands.
/// The choice follows <see cref="Engine_UiKeyGlyphs.Generation"/>; the glyph's pixels are the
/// core's and redraw with it.
/// </remarks>
internal sealed class SwfScenePrompts
{
    /// <summary>The glyph's node name under a prompt shape.</summary>
    public const string GlyphName = "prompt_glyph";

    /// <summary>Each prompt shape's character, with the pad button it names.</summary>
    private readonly Dictionary<int, string> _faces;

    /// <summary>Each shape's bounds in its own space, by character.</summary>
    private readonly Dictionary<int, Rect2> _bounds = new();

    private readonly List<(Node2D Node, string Button)> _placed = new();

    /// <summary>How many shapes were live after the last sweep of freed ones.</summary>
    private int _swept;

    private int _generation;

    public SwfScenePrompts(Dictionary<int, string> faces)
    {
        _faces = faces;
        _generation = Engine_UiKeyGlyphs.Generation;
    }

    /// <summary>Takes every prompt shape under <paramref name="node"/>, <paramref name="skip"/>'s subtree aside, and draws it as the glyphs stand now.</summary>
    public void Track(Node node, Node? skip)
    {
        if (_faces.Count == 0 || node == skip)
        {
            return;
        }

        if (node is Node2D shape && shape.HasMeta(Engine_SwfScene.CharacterMeta) &&
            _faces.TryGetValue((int)shape.GetMeta(Engine_SwfScene.CharacterMeta), out string? button))
        {
            if (_placed.Count >= Math.Max(64, _swept * 2))
            {
                Sweep();
            }

            _placed.Add((shape, button));
            Apply(shape, button, Engine_UiKeyGlyphs.PromptShowsShipped(button));
            return;
        }

        foreach (Node child in node.GetChildren())
        {
            Track(child, skip);
        }
    }

    /// <summary>Redraws every live prompt when the glyphs have changed since the last call.</summary>
    public void Refresh()
    {
        if (_generation == Engine_UiKeyGlyphs.Generation)
        {
            return;
        }

        _generation = Engine_UiKeyGlyphs.Generation;
        Sweep();
        foreach ((Node2D shape, string button) in _placed)
        {
            Apply(shape, button, Engine_UiKeyGlyphs.PromptShowsShipped(button));
        }
    }

    public void Clear()
    {
        _placed.Clear();
        _swept = 0;
    }

    private void Sweep()
    {
        _placed.RemoveAll(placed => !GodotObject.IsInstanceValid(placed.Node) || placed.Node.IsQueuedForDeletion());
        _swept = _placed.Count;
    }

    private void Apply(Node2D shape, string button, bool shipped)
    {
        Sprite2D? glyph = shape.GetNodeOrNull<Sprite2D>(GlyphName);
        if (glyph is null)
        {
            if (shipped || BoundsOf(shape) is not { } box)
            {
                return;
            }

            ImageTexture texture = Engine_UiKeyGlyphs.KeycapFor(button);
            float side = Math.Min(box.Size.X, box.Size.Y);
            glyph = new Sprite2D
            {
                Name = GlyphName,
                Texture = texture,
                Position = box.GetCenter(),
                Scale = Vector2.One * (side / Math.Max(1, texture.GetWidth())),
                TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
            };
            foreach (Node child in shape.GetChildren())
            {
                if (child is CanvasItem part)
                {
                    glyph.UseParentMaterial = part.UseParentMaterial;
                    break;
                }
            }

            shape.AddChild(glyph);
        }

        foreach (Node child in shape.GetChildren())
        {
            if (child is CanvasItem part && part != glyph)
            {
                part.Visible = shipped;
            }
        }

        glyph.Visible = !shipped;
    }

    /// <summary>The union of a shape's meshes in its own space, or null for a shape that draws nothing.</summary>
    private Rect2? BoundsOf(Node2D shape)
    {
        int character = (int)shape.GetMeta(Engine_SwfScene.CharacterMeta);
        if (!_bounds.TryGetValue(character, out Rect2 box))
        {
            bool any = false;
            Accumulate(shape, Transform2D.Identity, ref box, ref any);
            _bounds[character] = box;
        }

        return box.Size.X > 0f && box.Size.Y > 0f ? box : null;
    }

    private static void Accumulate(Node node, Transform2D toShape, ref Rect2 box, ref bool any)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is not Node2D part || part.Name == GlyphName)
            {
                continue;
            }

            Transform2D transform = toShape * part.Transform;
            if (part is MeshInstance2D { Mesh: { } mesh })
            {
                Aabb aabb = mesh.GetAabb();
                Vector2 min = new(aabb.Position.X, aabb.Position.Y), max = new(aabb.End.X, aabb.End.Y);
                foreach (Vector2 corner in new[] { min, new Vector2(max.X, min.Y), max, new Vector2(min.X, max.Y) })
                {
                    Vector2 point = transform * corner;
                    box = any ? box.Expand(point) : new Rect2(point, Vector2.Zero);
                    any = true;
                }
            }

            Accumulate(part, transform, ref box, ref any);
        }
    }
}
