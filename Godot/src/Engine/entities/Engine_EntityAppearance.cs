using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Entities;

/// <summary>
/// Draws an entity the way its renderable's appearance setters state it: faded, hidden,
/// tinted, rescaled.
/// </summary>
/// <remarks>
/// The look is <see cref="HostBridge.EntityAppearance"/>, taken at the guest's setters. The
/// original renders a partial alpha as a CONSTANT blend, never a dither or a dissolve texture
/// (`sub_822251D8`): effective alpha = material+0x44 × the stated opacity, and below 0.999
/// (`flt_83320A70`) SrcBlend = BLENDFACTOR, DestBlend = INVBLENDFACTOR, BLENDFACTOR = that alpha.
/// Our entity shaders never write ALPHA, so <see cref="GeometryInstance3D.Transparency"/> is the
/// same `src·a + dst·(1−a)`. The shadow and depth passes (pass types 0, 1, 7) skip the alpha
/// multiply, so a fading body keeps its full shadow — which the cast-only proxy already does.
///
/// Not drawn at all — SetAsDrawable(0), or the product not above 1e-4 — the guest releases the
/// proxies: no body, no shadow. Hiding the node does both; the shadow skeletons mirror it.
/// </remarks>
public static class Engine_EntityAppearance
{
    /// <summary>What was last applied to each node whose look is not the constructor's.</summary>
    private static readonly Dictionary<MeshInstance3D, HostBridge.EntityAppearance> Applied =
        new(ReferenceEqualityComparer.Instance);

    private static readonly StringName TintParameter = "entity_tint";

    /// <summary>Opacity at or above which the guest draws without blending (`flt_83320A70`).</summary>
    private const float OpaqueFrom = 0.999f;

    /// <summary>Entities whose look is not the constructor's; reset by the walk that feeds this.</summary>
    public static int StatedThisWalk;

    /// <summary>Of those, how many are faded, and how many are not drawn.</summary>
    public static int FadedThisWalk;

    public static int HiddenThisWalk;

    public static void BeginWalk()
    {
        StatedThisWalk = 0;
        FadedThisWalk = 0;
        HiddenThisWalk = 0;
    }

    /// <summary>Whether a look needs the node itself: a batch or a married twin cannot carry it.</summary>
    public static bool NeedsOwnNode(in HostBridge.EntityAppearance look) => !look.IsConstructed;

    /// <summary>
    /// Applies the look to <paramref name="node"/>. Idempotent and free when nothing changed; the
    /// common case — a constructed look on a node that never had another — touches no engine
    /// state. The caller has already taken the node out of any batch and any twin marriage.
    /// </summary>
    private static readonly StringName ClassicTintParameter = "ps_g_StandardMaterialTintColour";

    public static void Apply(MeshInstance3D node, in HostBridge.EntityAppearance look, bool principal)
    {
        bool had = Applied.TryGetValue(node, out HostBridge.EntityAppearance was);
        if (!had && look.IsConstructed)
        {
            return;
        }

        bool drawn = look.Drawn(principal);
        if (!look.IsConstructed)
        {
            ++StatedThisWalk;
            if (!drawn)
            {
                ++HiddenThisWalk;
            }
            else if (look.Opacity < OpaqueFrom)
            {
                ++FadedThisWalk;
            }
        }

        if (had && was == look)
        {
            return;
        }

        if (!GodotObject.IsInstanceValid(node))
        {
            Applied.Remove(node);
            return;
        }

        float opacity = Mathf.Clamp(look.Opacity, 0f, 1f);
        node.Transparency = drawn && opacity < OpaqueFrom ? 1f - opacity : 0f;
        node.Visible = drawn;

        // The tint's lanes in the guest's memory order are the shader's x, y, z (`sub_82573570`
        // packs its bytes 2, 1, 0, 3 into +80, +84, +88, +92, and `sub_82220CA8` stores +80..
        // to c49 in order), and `.w` is read by no shader. A corpse is {103,103,103,255}: grey.
        if (!had || was.TintLanes != look.TintLanes)
        {
            node.SetInstanceShaderParameter(TintParameter,
                new Vector4(look.TintLanes.X, look.TintLanes.Y, look.TintLanes.Z, 1f));
            // The classic shaders read the same tint as the title's PS c49 (the instance's
            // +240, which this appearance setter fills).
            if (Refii.Models.Engine_ClassicMaterial.Active)
            {
                node.SetInstanceShaderParameter(ClassicTintParameter,
                    new Vector4(look.TintLanes.X, look.TintLanes.Y, look.TintLanes.Z, 1f));
            }
        }

        if (look.IsConstructed)
        {
            Applied.Remove(node);
        }
        else
        {
            Applied[node] = look;
        }
    }

    /// <summary>
    /// The placement with the stated SCALE (+108, SetScale) folded in: uniform, about the entity's
    /// origin — the proxy builds its world matrix as each rotation row times the scale, then the
    /// translation (`sub_8220B740`, 0x8220b7fc..0x8220b828). Not interpolated: the block's
    /// previous scale is written once and never read.
    /// </summary>
    public static Transform3D Scaled(Transform3D placed, in HostBridge.EntityAppearance look)
    {
        float scale = look.Scale;
        if (scale == 1f || !float.IsFinite(scale) || scale <= 0f)
        {
            return placed;
        }

        placed.Basis = placed.Basis.Scaled(new Vector3(scale, scale, scale));
        return placed;
    }

    /// <summary>The node is going away.</summary>
    public static void Forget(MeshInstance3D node) => Applied.Remove(node);

    /// <summary>Every node is going away (a level change).</summary>
    public static void ForgetAll() => Applied.Clear();
}
