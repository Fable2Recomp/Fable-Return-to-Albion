using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// An element's authored timeline, as a meter or pulser scrubs it: which animation tracks its
/// slides activate, what each writes onto a cloned node, and which descendants exist at a given
/// tick. Every surface that scrubs a `.bgf` timeline — the HUD's pieces, the world hold icon —
/// goes through here.
/// </summary>
public static class Engine_UiTimeline
{
    /// <summary>What one animation track drives on one clone node.</summary>
    public enum Target
    {
        PositionX, PositionY, ScaleX, ScaleY, RotationZ, Opacity, UvScaleV, Other,
    }

    /// <summary>
    /// One descendant's TIME WINDOW: it exists while its component's playhead is in
    /// `[Start, End]`, both ends inclusive.
    /// </summary>
    /// <remarks>
    /// The guest's walk `sub_822849A0` compares the owning component's slide time against the
    /// element's own `timeoffset` (0x82284a5c: `t &lt; offset` is out) and `endtime`
    /// (0x82284a9c: `t &gt; end` is out); an absent attribute is no bound. Out of window it clears
    /// the element's active bit and skips its WHOLE SUBTREE. The offset does not shift the
    /// element's own clock. Slide commands write the same attributes, so the playing slide's
    /// values override the element's.
    /// </remarks>
    public readonly record struct Window(int Ordinal, int Start, int End)
    {
        public bool IsOpen(float t) => Start <= t && t <= End;
    }

    private static uint Id(string name) => Engine_GameHash.Sdbm(name) & Engine_GameHash.AttributeIdMask;

    private static readonly uint PositionXId = Id("position.x");
    private static readonly uint PositionYId = Id("position.y");
    private static readonly uint ScaleXId = Id("scale.x");
    private static readonly uint ScaleYId = Id("scale.y");
    private static readonly uint OpacityId = Id("opacity");

    // Rotation tracks: ExpressionFillPos swings `fill size` ±1.222 rad and ExpressionFillSize
    // swings the two bar groups from ±1.222 toward 0 (hud.bgf owners 1498/1499); the hold ring
    // is three rotating half-discs.
    private static readonly uint RotationZId = Id("rotation.z");

    // ExpressionFillSize scrubs each bar's height 0.16→1.28 AND its image's `scalev`
    // 0.063→0.5 in step, so the quad reveals a taller band of the same atlas cell.
    private static readonly uint ScaleVId = Id("scalev");

    private static readonly uint TimeOffsetId = Id("timeoffset");
    private static readonly uint EndTimeId = Id("endtime");

    public static Target TargetOf(uint attributeId) =>
        attributeId == PositionXId ? Target.PositionX
        : attributeId == PositionYId ? Target.PositionY
        : attributeId == ScaleXId ? Target.ScaleX
        : attributeId == ScaleYId ? Target.ScaleY
        : attributeId == OpacityId ? Target.Opacity
        : attributeId == RotationZId ? Target.RotationZ
        : attributeId == ScaleVId ? Target.UvScaleV
        : Target.Other;

    /// <summary>
    /// The slide an element's timeline plays: <paramref name="stated"/> (the guest's count of
    /// non-master slides, from one), else its first slide after the master — the one a
    /// component opens on.
    /// </summary>
    public static BgfSlide? PlayingSlide(Engine_BgfDocument document, int element, int stated = 0)
    {
        int wanted = stated > 0 ? stated : 1;
        int seen = 0;
        BgfSlide? first = null;
        foreach (BgfSlide slide in document.Slides)
        {
            if (slide.OwnerElement != element || slide.IsMaster)
            {
                continue;
            }

            first ??= slide;
            if (++seen == wanted)
            {
                return slide;
            }
        }

        return first;
    }

    /// <summary>
    /// The animation tracks a scrub of <paramref name="element"/> drives: those its own slides
    /// activate, on it or under it, with a target the scrub can write. A meter scrubs its
    /// element's timeline, while a nested fader's tracks belong to that fader.
    /// </summary>
    public static IEnumerable<(BgfAnimation Animation, Target Target)> Tracks(
        Engine_UiScreen screen, int element)
    {
        var owned = new HashSet<int>();
        foreach (BgfSlide slide in screen.Document.Slides)
        {
            if (slide.OwnerElement != element)
            {
                continue;
            }

            foreach (BgfSlideEntry entry in slide.Entries)
            {
                foreach (int index in entry.ActivatedAnimations ?? System.Array.Empty<int>())
                {
                    if (index >= 0)
                    {
                        owned.Add(index);
                    }
                }
            }
        }

        BgfAnimation[] animations = screen.Document.Animations;
        for (int index = 0; index < animations.Length; ++index)
        {
            BgfAnimation animation = animations[index];
            if (!animation.ActiveOnLoad || animation.Keys.Length == 0 ||
                !InSubtree(screen, animation.ElementOrdinal, element) ||
                (owned.Count > 0 && !owned.Contains(index)))
            {
                continue;
            }

            Target target = TargetOf(animation.AttributeId);
            if (target != Target.Other)
            {
                yield return (animation, target);
            }
        }
    }

    /// <summary>
    /// The time windows under <paramref name="element"/> that bite inside its playing slide.
    /// The exporter writes `endtime` equal to the slide length on nearly every element, which
    /// gates nothing and is left out.
    /// </summary>
    public static List<Window> Windows(Engine_UiScreen screen, int element, BgfSlide? playing)
    {
        var windows = new List<Window>();
        int length = playing?.EndTime ?? 1000;
        foreach (UiNode node in screen.Nodes)
        {
            if (node.Ordinal == element || !InSubtree(screen, node.Ordinal, element))
            {
                continue;
            }

            int start = node.Element.TryGetAttribute(TimeOffsetId, out BgfAttribute offset)
                ? offset.AsInt()
                : int.MinValue;
            int end = node.Element.TryGetAttribute(EndTimeId, out BgfAttribute until)
                ? until.AsInt()
                : int.MaxValue;
            foreach (BgfSlideEntry entry in playing?.Entries ?? System.Array.Empty<BgfSlideEntry>())
            {
                if (entry.ElementOrdinal != node.Ordinal)
                {
                    continue;
                }

                foreach (BgfAttribute command in entry.Commands ?? System.Array.Empty<BgfAttribute>())
                {
                    if (command.Id == TimeOffsetId)
                    {
                        start = command.AsInt();
                    }
                    else if (command.Id == EndTimeId)
                    {
                        end = command.AsInt();
                    }
                }
            }

            if (start > 0 || end < length)
            {
                windows.Add(new Window(node.Ordinal, start, end));
            }
        }

        return windows;
    }

    /// <summary>
    /// Writes one evaluated track value onto a node that is not the scrubbed piece's root —
    /// a root's position is the placing surface's to compose.
    /// </summary>
    public static void Write(Node3D node, Target target, float value)
    {
        switch (target)
        {
            case Target.PositionX:
                node.Position = node.Position with { X = value };
                break;

            case Target.PositionY:
                node.Position = node.Position with { Y = value };
                break;

            // Through the basis column, never `.Scale`: every meter fill in `hud.bgf` is
            // authored `scale.x = 0`, and Godot's scale getter answers (0,0,0) for all three
            // axes on a singular basis. See Engine_UiBasisScale.
            case Target.ScaleX:
                Engine_UiBasisScale.SetX(node, value);
                break;

            case Target.ScaleY:
                Engine_UiBasisScale.SetY(node, value);
                break;

            // Radians, sign as authored — Engine_UiSceneBuilder.BuildTransform keeps rotation.z's
            // sign in its Euler basis. Turned about the authored pivot (the hold ring's
            // half-discs carry `pivot.x = ±50`, the ring's centre).
            case Target.RotationZ:
                Engine_UiSceneBuilder.SetLiveRotationZ(node, value);
                break;

            case Target.Opacity:
                SetOpacity(node, value * 0.01f);
                break;

            case Target.UvScaleV:
                SetUvScaleV(node, value);
                break;
        }
    }

    /// <summary>Whether an element sits at or under <paramref name="root"/>.</summary>
    public static bool InSubtree(Engine_UiScreen screen, int element, int root)
    {
        for (UiNode? at = element >= 0 && element < screen.Nodes.Length
                ? screen.Nodes[element] : null;
            at is not null; at = at.Parent)
        {
            if (at.Ordinal == root)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Writes a scrubbed vertical UV extent onto a quad, keeping the builder's
    /// half-texel inset (Engine_UiSceneBuilder.InsetAxis) so the band's edges do not bleed.</summary>
    private static void SetUvScaleV(Node node, float extent)
    {
        if (node is not MeshInstance3D mesh || mesh.MaterialOverride is not ShaderMaterial material)
        {
            return;
        }

        // The strip is ANCHORED at its far edge: the bar's authored rect is v [0.939, 0.998]
        // for `scalev` 0.063 (the atlas's bottom rows, empty), and the guest's scrub to 0.5
        // grows it upward into the arc art. Hold the far edge, move the near one.
        Vector2 scale = material.GetShaderParameter("uv1_scale").AsVector2();
        Vector2 offset = material.GetShaderParameter("uv1_offset").AsVector2();
        float inset = material.GetShaderParameter("diffuse_tex").As<Texture2D>() is { } tex
            ? 1f / Mathf.Max(tex.GetHeight(), 1)
            : 0f;
        float far = offset.Y + scale.Y;
        float span = Mathf.Max(extent - inset, 0f);
        material.SetShaderParameter("uv1_offset", new Vector2(offset.X, far - span));
        material.SetShaderParameter("uv1_scale", new Vector2(scale.X, span));
    }

    /// <summary>Writes a scrubbed opacity onto a node's own quads — materials are per-piece
    /// owned copies, so this cannot fade a sibling or the pattern book.</summary>
    private static void SetOpacity(Node node, float alpha)
    {
        if (node is MeshInstance3D mesh && mesh.MaterialOverride is ShaderMaterial material)
        {
            material.SetShaderParameter("opacity", alpha);
        }

        foreach (Node child in node.GetChildren())
        {
            SetOpacity(child, alpha);
        }
    }
}
