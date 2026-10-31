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

    /// <summary>
    /// The tints <see cref="Apply"/> last wrote on each node, modern then classic (null where it
    /// wrote none), for <see cref="MirrorToCloth"/>.
    /// </summary>
    /// <remarks>
    /// Held here rather than read back: `GetInstanceShaderParameter` is a value-returning server
    /// call, and with Godot's separate render thread it waits until that thread has finished the
    /// frame it is drawing. A clothed body mirrors its look on every change — each frame of a
    /// fade — so the read cost a whole render frame of overlap per fading entity.
    /// </remarks>
    private static readonly Dictionary<GeometryInstance3D, (Vector4 Modern, Vector4? Classic)> Tints =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>The 7e3 scene ceiling over the exposure at rest: 31.875 / 4.</summary>
    private const float SceneCeilingTint = 31.875f / 4f;

    /// <summary>Nodes drawing the saturation-mask overlay; read by the post-process to skip its pass.</summary>
    private static readonly HashSet<MeshInstance3D> Masked = new(ReferenceEqualityComparer.Instance);

    /// <summary>The overlay each node carries: 0 none, else <see cref="OverlayMask"/> | <see cref="OverlayDepth"/>.</summary>
    private static readonly Dictionary<MeshInstance3D, int> Overlays = new(ReferenceEqualityComparer.Instance);

    private const int OverlayMask = 1;
    private const int OverlayDepth = 2;
    private static readonly ShaderMaterial?[] OverlayMaterials = new ShaderMaterial?[4];

    /// <summary>How many entity nodes carry the saturation mask right now.</summary>
    public static int MaskedCount { get; private set; }

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

    /// <summary>
    /// The body's fade and tint onto its cloth. The guest draws the cloth as part of the body, so
    /// it takes the same c49 tint and the same blend; here the cloth is a child node of its own
    /// and would otherwise stay untinted and opaque under a fading, tinted body.
    /// </summary>
    public static void MirrorToCloth(GeometryInstance3D node)
    {
        if (node.GetNodeOrNull(Refii.Physics.Engine_ClothBody.NodeName) is not GeometryInstance3D cloth)
        {
            return;
        }
        cloth.Transparency = node.Transparency;
        // A node never tinted holds no value, and Nil puts the cloth's back to the default too.
        bool tinted = Tints.TryGetValue(node, out (Vector4 Modern, Vector4? Classic) tint);
        ClothTint written = ClothTints.GetOrCreateValue(cloth);
        written.Modern = MirrorTint(cloth, TintParameter, tinted ? tint.Modern : null, written.Modern);
        if (Refii.Models.Engine_ClassicMaterial.Active)
        {
            written.Classic = MirrorTint(cloth, ClassicTintParameter, tinted ? tint.Classic : null, written.Classic);
        }
    }

    /// <summary>Which tints this class has written on each cloth, so an untinted cloth is not reset.</summary>
    private sealed class ClothTint
    {
        public bool Modern;
        public bool Classic;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryInstance3D, ClothTint> ClothTints = new();

    /// <summary>
    /// One tint onto a cloth; returns whether the cloth now holds one of ours.
    /// </summary>
    /// <remarks>
    /// NIL ONLY TO TAKE OURS BACK. Writing nil is `set_instance_shader_parameter`'s default path,
    /// which asks the server for the default first (a render-thread sync, Engine_InstanceParams)
    /// and leaves the cloth exactly where a cloth never written stands. Only this method writes a
    /// cloth's tint, so one it never tinted is already there and is left alone.
    /// </remarks>
    private static bool MirrorTint(GeometryInstance3D cloth, StringName parameter, Vector4? tint, bool held)
    {
        if (tint is { } value)
        {
            cloth.SetInstanceShaderParameter(parameter, value);
            return true;
        }

        if (held)
        {
            Refii.Core.Engine_InstanceParams.Set(cloth, parameter, default(Variant));
        }
        return false;
    }

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
            Tints.Remove(node);
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
            // THE MODERN TINT IS CAPPED AT THE XBOX'S SCENE CEILING. The title's scene target is
            // 7e3 (`"1280x720 2xMSAA Horizontal Split (PIXEL_FORMAT_A2B10G10R10F_EDRAM)"`), whose
            // largest value is 31.875, and its materials write lit × tint × exposure (c20.w,
            // 4 at rest). qo240's ghosts state (0, 1000, 100000)/255 — blue 392 — and read as a
            // faint blue only because the target saturates; our FP16 scene does not, and the
            // ghost became a blinding glow. Capping the lane at 31.875 / 4 keeps every tint the
            // title can actually show and touches only the over-range ones (qo240, qo900 ghosts,
            // qo860/qo170/qo890 at 9..12). Classic clamps the export itself instead.
            var modern = new Vector4(
                Mathf.Min(look.TintLanes.X, SceneCeilingTint),
                Mathf.Min(look.TintLanes.Y, SceneCeilingTint),
                Mathf.Min(look.TintLanes.Z, SceneCeilingTint), 1f);
            Refii.Core.Engine_InstanceParams.Set(node, TintParameter, modern);
            // The classic shaders read the same tint as the title's PS c49 (the instance's
            // +240, which this appearance setter fills).
            Vector4? classic = null;
            if (Refii.Models.Engine_ClassicMaterial.Active)
            {
                classic = new Vector4(look.TintLanes.X, look.TintLanes.Y, look.TintLanes.Z, 1f);
                node.SetInstanceShaderParameter(ClassicTintParameter, classic.Value);
            }
            else if (Tints.TryGetValue(node, out (Vector4 Modern, Vector4? Classic) held))
            {
                // Not written this time, so the node still holds the earlier one.
                classic = held.Classic;
            }
            Tints[node] = (modern, classic);
        }

        MirrorToCloth(node);

        // THE OVERLAY: SetSaturationMask (+196) marks the node's pixels for the post-process,
        // and a faded node writes the depth its blend tests against (entity_fade_depth). One
        // material carries whichever the look needs.
        bool masked = drawn && look.SaturationMask;
        if (masked != Masked.Contains(node))
        {
            if (masked)
            {
                Masked.Add(node);
            }
            else
            {
                Masked.Remove(node);
            }

            MaskedCount = Masked.Count;
        }

        int overlay = (masked ? OverlayMask : 0) | (node.Transparency > 0f ? OverlayDepth : 0);
        Overlays.TryGetValue(node, out int carried);
        if (overlay != carried)
        {
            node.MaterialOverlay = OverlayMaterial(overlay);
            if (overlay == 0)
            {
                Overlays.Remove(node);
            }
            else
            {
                Overlays[node] = overlay;
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
    /// <summary>
    /// The overlay for a combination. The depth writer draws ahead of every other transparent
    /// surface (render priority -128), so the faded body's own surfaces then pass only where they
    /// are nearest; the mask alone draws with the rest.
    /// </summary>
    private static ShaderMaterial? OverlayMaterial(int overlay)
    {
        if (overlay == 0)
        {
            return null;
        }

        return OverlayMaterials[overlay] ??= new ShaderMaterial
        {
            Shader = GD.Load<Shader>(overlay switch
            {
                OverlayMask => "res://src/Engine/entities/entity_saturation_mask.gdshader",
                OverlayDepth => "res://src/Engine/entities/entity_fade_depth.gdshader",
                _ => "res://src/Engine/entities/entity_fade_depth_mask.gdshader",
            }),
            RenderPriority = (overlay & OverlayDepth) != 0 ? (int)Material.RenderPriorityMin : 0,
        };
    }

    public static void Forget(MeshInstance3D node)
    {
        Applied.Remove(node);
        Tints.Remove(node);
        Overlays.Remove(node);
        if (Masked.Remove(node))
        {
            MaskedCount = Masked.Count;
        }
    }

    /// <summary>Every node is going away (a level change).</summary>
    public static void ForgetAll()
    {
        Applied.Clear();
        Tints.Clear();
        Masked.Clear();
        Overlays.Clear();
        MaskedCount = 0;
    }
}
