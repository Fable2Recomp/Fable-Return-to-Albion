using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// Three live levers on every CHARACTER body (a rigged entity mesh, <see cref="AndroidCharacterCensus.IsCharacterBody"/>)
/// and what hangs on it: `android_character_lod_bias`, `android_character_far_shading_metres` and
/// `android_character_far_single_pass_metres`.
/// </summary>
/// <remarks>
/// <para>`android_character_lod_bias` is Godot's lod_bias on the body, its skinned parts, its fur
/// shells and its attachments (1 keeps the shared `mesh_lod_pixels` rule; 0.5 switches each level at
/// half the distance). A townsperson's shipped levels switch on Low at about 5 m and 10 m and its
/// coarsest is 2,500 to 4,500 triangles, so this moves the whole crowd onto the coarse end sooner;
/// with `android_character_far_lod_cm` it moves them onto the far levels too.</para>
/// <para>`android_character_far_shading_metres` is `prop_far_shading_metres` for people: past it a
/// body's lite surfaces draw their FAR copy (Engine_ShaderSpecialisation.FarCopy, the normal map,
/// tangent and binormal compiled out), measured to the body's box with the same hysteresis
/// (Engine_PropFarShading.Decide). On a tile GPU the two tangent-frame varyings are paid per vertex,
/// and a far townsperson is all vertices. A surface carrying another override (a covered cluster's
/// collapse) is left alone; a far copy is taken off before the body's mesh is swapped
/// (<see cref="BeforeMeshSwap"/>), since an override keeps its index across a swap.</para>
/// <para>`android_character_far_single_pass_metres` is `android_translucent_single_pass` past a distance:
/// a far body's translucent surfaces (hair cards, lace: the alpha-tested opaque pass and the blended pass
/// over it, both two-sided) draw the opaque pass alone, cut at the title's cutout reference (GREATER 128)
/// instead of 254, so a far character pays one lit two-sided pass, not two and a blend. A shallow copy of
/// the pass (or of its far copy) without its next pass, one per shared material; the same shader, so no
/// pipeline is compiled. Far away the cut's hard edges and the lost wisps under alpha 128 are a pixel or
/// two; near bodies keep both passes.</para>
/// <para>All three are read live; nothing on the desktop. The far copies' pipelines are not warmed: the
/// first switch of each lite build compiles one.</para>
/// </remarks>
public static class AndroidCharacterLevers
{
    public const string LodBiasKey = "android_character_lod_bias";

    public const string FarShadingKey = "android_character_far_shading_metres";

    public const string FarSinglePassKey = "android_character_far_single_pass_metres";

    /// <summary>The cut a single-pass copy draws at: Engine_MdlMaterial's CutoutAlpha, the title's GREATER 128.</summary>
    public const float CutoutAlpha = 128.5f / 255.0f;

    /// <summary>Bodies visited a frame.</summary>
    public const int BodiesPerFrame = 32;

    /// <summary>A body's levers are written again every this many visits (a power of two), for what was hung on it since.</summary>
    public const int RevisitEvery = 64;

    private sealed class Body
    {
        public required ulong Id;
        public bool Far;
        public bool FarPass;
        public bool Dirty = true;
        public int Visits;
    }

    private static readonly List<Body> Bodies = new();
    private static readonly HashSet<ulong> Known = new();
    private static readonly Dictionary<ulong, Body> ById = new();
    private static float _bias = float.NaN, _metres = float.NaN, _passMetres = float.NaN;
    private static bool _hooked;
    private static int _next;
    private static ulong _tickedFrame = ulong.MaxValue;
    private static int _farBodies, _farPassBodies, _swaps;

    /// <summary>Per shared material (or far copy), its single-pass copy (itself when it has no blended pass).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderMaterial, ShaderMaterial> SingleCopies = new();

    /// <summary>A single-pass copy back to the material it was made from.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderMaterial, ShaderMaterial> SingleSource = new();

    /// <summary>Visits still owed after a setting changed, so a lever turned back off is undone everywhere.</summary>
    private static int _owed;

    /// <summary>The bias the configuration asks for: 1 unless set, clamped to 0.1..4. Pure.</summary>
    public static float WantedBias(IReadOnlyDictionary<string, string> values)
    {
        float bias = Engine_RefiiConfig.Float(values, LodBiasKey, 1f);
        return bias > 0f ? Math.Clamp(bias, 0.1f, 4f) : 1f;
    }

    /// <summary>The far-shading distance the configuration asks for: 0 (off) unless set. Pure.</summary>
    public static float WantedMetres(IReadOnlyDictionary<string, string> values)
    {
        float metres = Engine_RefiiConfig.Float(values, FarShadingKey, 0f);
        return metres > 0f ? metres : 0f;
    }

    /// <summary>The single-pass distance the configuration asks for: 0 (off) unless set. Pure.</summary>
    public static float WantedPassMetres(IReadOnlyDictionary<string, string> values)
    {
        float metres = Engine_RefiiConfig.Float(values, FarSinglePassKey, 0f);
        return metres > 0f ? metres : 0f;
    }

    /// <summary>The single-pass distance in force: 0 (off) off the phone.</summary>
    public static float FarSinglePassMetres
    {
        get
        {
            Load();
            return _passMetres;
        }
    }

    /// <summary>The bias in force: 1 off the phone.</summary>
    public static float LodBias
    {
        get
        {
            Load();
            return _bias;
        }
    }

    /// <summary>The far-shading distance in force: 0 (off) off the phone or without the lite shaders.</summary>
    public static float FarShadingMetres
    {
        get
        {
            Load();
            return _metres;
        }
    }

    private static void Load()
    {
        if (!float.IsNaN(_bias) && !float.IsNaN(_metres) && !float.IsNaN(_passMetres))
        {
            return;
        }

        float bias = 1f, metres = 0f, passMetres = 0f;
        if (Engine_Platform.IsAndroid)
        {
            try
            {
                IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
                bias = WantedBias(values);
                metres = Refii.Models.Engine_ShaderVariant.Lite ? WantedMetres(values) : 0f;
                passMetres = WantedPassMetres(values);
            }
            catch (Exception)
            {
                (bias, metres, passMetres) = (1f, 0f, 0f);
            }
        }

        _bias = bias;
        _metres = metres;
        _passMetres = passMetres;
        if (!_hooked)
        {
            _hooked = true;
            Engine_RefiiConfig.OnConfigChanged += (name, _) =>
            {
                if (string.Equals(name, LodBiasKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, FarShadingKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, FarSinglePassKey, StringComparison.OrdinalIgnoreCase))
                {
                    _bias = _metres = _passMetres = float.NaN;
                    _owed = Bodies.Count;
                    foreach (Body body in Bodies)
                    {
                        body.Dirty = true;
                    }
                }
            };
        }
    }

    /// <summary>A body has its rig (GameEntities.TryRig): a character's takes part from the next sweep. Main thread.</summary>
    public static void Rigged(MeshInstance3D node)
    {
        if (!Engine_Platform.IsAndroid || !AndroidCharacterCensus.IsCharacterBody(node) || !Known.Add(node.GetInstanceId()))
        {
            return;
        }

        var body = new Body { Id = node.GetInstanceId() };
        Bodies.Add(body);
        ById[body.Id] = body;
        ApplyBias(node, LodBias);
    }

    /// <summary>A body's fur shells (Engine_ModernFur) take its bias.</summary>
    public static void Shell(MeshInstance3D body, MeshInstance3D shell)
    {
        if (Engine_Platform.IsAndroid && shell.LodBias != body.LodBias)
        {
            shell.LodBias = body.LodBias;
        }
    }

    /// <summary>
    /// The body's mesh is about to change (Engine_HiddenClusters.Present): its far copies come off,
    /// since an override keeps its surface index, and the next sweep puts the right ones back.
    /// </summary>
    public static void BeforeMeshSwap(MeshInstance3D node)
    {
        if (!Engine_Platform.IsAndroid || !ById.TryGetValue(node.GetInstanceId(), out Body? body))
        {
            return;
        }

        int count = node.GetSurfaceOverrideMaterialCount();
        for (int i = 0; i < count; i++)
        {
            if (IsOurs(node.GetSurfaceOverrideMaterial(i)))
            {
                node.SetSurfaceOverrideMaterial(i, null);
            }
        }

        body.Dirty = true;
    }

    /// <summary>Whether a sweep has nothing to do: no far shading, the neutral bias, no body left far, no visit owed. Pure.</summary>
    public static bool Idle(float metres, float bias, int farBodies, int owed) =>
        metres <= 0f && bias == 1f && farBodies == 0 && owed <= 0;

    /// <summary>One frame's slice of the sweep, from the host's per-frame tick.</summary>
    public static void Tick(Node owner)
    {
        if (Bodies.Count == 0)
        {
            return;
        }

        ulong frame = Engine.GetProcessFrames();
        if (frame == _tickedFrame || owner.GetViewport()?.GetCamera3D() is not { } camera)
        {
            return;
        }

        _tickedFrame = frame;
        float metres = FarShadingMetres;
        float bias = LodBias;

        float passMetres = FarSinglePassMetres;

        // Every lever at its neutral value and nothing left to undo: a new body needs nothing.
        if (Idle(Math.Max(metres, passMetres), bias, _farBodies + _farPassBodies, _owed))
        {
            return;
        }

        Vector3 eye = camera.GlobalPosition;
        int budget = Math.Min(BodiesPerFrame, Bodies.Count);
        for (int n = 0; n < budget && Bodies.Count != 0; n++)
        {
            _owed = Math.Max(0, _owed - 1);
            if (_next >= Bodies.Count)
            {
                _next = 0;
            }

            Body body = Bodies[_next];
            if (GodotObject.InstanceFromId(body.Id) is not MeshInstance3D node || !GodotObject.IsInstanceValid(node))
            {
                _farBodies -= body.Far ? 1 : 0;
                _farPassBodies -= body.FarPass ? 1 : 0;
                Known.Remove(body.Id);
                ById.Remove(body.Id);
                Bodies[_next] = Bodies[^1];
                Bodies.RemoveAt(Bodies.Count - 1);
                continue;
            }

            ++_next;
            if (!node.IsInsideTree())
            {
                continue;
            }

            Aabb world = node.GlobalTransform * node.GetAabb();
            float distance = Refii.Level.Engine_PropFarShading.Distance(world, eye);
            bool far = Refii.Level.Engine_PropFarShading.Decide(distance, metres, body.Far);
            bool farPass = Refii.Level.Engine_PropFarShading.Decide(distance, passMetres, body.FarPass);
            if (far != body.Far || farPass != body.FarPass || body.Dirty || (++body.Visits & (RevisitEvery - 1)) == 0)
            {
                _swaps += far == body.Far && farPass == body.FarPass ? 0 : 1;
                _farBodies += far == body.Far ? 0 : far ? 1 : -1;
                _farPassBodies += farPass == body.FarPass ? 0 : farPass ? 1 : -1;
                body.Far = far;
                body.FarPass = farPass;
                body.Dirty = false;
                ApplyBias(node, bias);
                ApplyShading(node, far, farPass);
            }
        }
    }

    /// <summary>The bias on the body and every geometry under it (skinned parts, shells, attachments).</summary>
    private static void ApplyBias(Node node, float bias)
    {
        if (node is GeometryInstance3D geometry && geometry.LodBias != bias)
        {
            geometry.LodBias = bias;
        }

        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            ApplyBias(node.GetChild(i), bias);
        }
    }

    /// <summary>The far and single-pass copies on, or off, on the body and the meshes under it that draw its materials.</summary>
    private static void ApplyShading(Node node, bool far, bool farPass)
    {
        if (node is MeshInstance3D { Mesh: { } mesh } mi && mi.MaterialOverride is null &&
            node is not Refii.Physics.Engine_ClothBody && node.Name != "FurShells" &&
            node.Name != Refii.Entities.Engine_ViewTwin.NodeName)
        {
            int count = Math.Min(mesh.GetSurfaceCount(), mi.GetSurfaceOverrideMaterialCount());
            for (int i = 0; i < count; i++)
            {
                Material? current = mi.GetSurfaceOverrideMaterial(i);
                if (current is not null && !IsOurs(current))
                {
                    continue;
                }

                Material? want = Want(mesh.SurfaceGetMaterial(i), far, farPass);
                if (!ReferenceEquals(current, want))
                {
                    mi.SetSurfaceOverrideMaterial(i, want);
                }
            }
        }

        int children = node.GetChildCount();
        for (int i = 0; i < children; i++)
        {
            ApplyShading(node.GetChild(i), far, farPass);
        }
    }

    /// <summary>Whether a surface override is one this class puts on (a far or single-pass copy).</summary>
    private static bool IsOurs(Material? material) =>
        Refii.Models.Engine_ShaderSpecialisation.IsFarCopy(material) ||
        (material is ShaderMaterial shaded && SingleSource.TryGetValue(shaded, out _));

    /// <summary>The override a surface drawing <paramref name="source"/> wants: null for its own material.</summary>
    private static Material? Want(Material? source, bool far, bool farPass)
    {
        Material? want = far ? Refii.Models.Engine_ShaderSpecialisation.FarCopy(source) : null;
        if (farPass && SinglePassCopy(want ?? source) is { } single)
        {
            want = single;
        }

        return want;
    }

    /// <summary>
    /// Whether a pass on <paramref name="passPath"/> followed by one on <paramref name="nextPath"/> (full shader
    /// files) is a translucent surface's pair: the alpha-tested two-sided pass, then the blended one. Pure.
    /// </summary>
    public static bool IsTranslucentPair(string passPath, string nextPath) =>
        nextPath == Refii.Models.Engine_ShaderFamily.StandardTranslucentPath &&
        passPath == Refii.Models.Engine_ShaderFamily.StandardPath;

    /// <summary>
    /// A translucent surface's opaque pass without its blended pass, cut at <see cref="CutoutAlpha"/>; null for
    /// any other material. One per material, shared by every body. Main thread.
    /// </summary>
    private static ShaderMaterial? SinglePassCopy(Material? material)
    {
        if (material is not ShaderMaterial pass || pass.NextPass is not ShaderMaterial next)
        {
            return null;
        }

        if (SingleCopies.TryGetValue(pass, out ShaderMaterial? hit))
        {
            return ReferenceEquals(hit, pass) ? null : hit;
        }

        if (!IsTranslucentPair(FamilyOf(pass.Shader), FamilyOf(next.Shader)))
        {
            SingleCopies.AddOrUpdate(pass, pass);
            return null;
        }

        var copy = Refii.Models.Engine_MaterialCopy.Shallow(pass);
        copy.NextPass = null;
        copy.SetShaderParameter("alpha_scissor_threshold", CutoutAlpha);
        SingleCopies.AddOrUpdate(pass, copy);
        SingleSource.AddOrUpdate(copy, pass);
        return copy;
    }

    /// <summary>The full shader file a shader is built from (a lite or specialised build's wrapper's full path).</summary>
    private static string FamilyOf(Shader? shader) =>
        Refii.Models.Engine_ShaderVariant.FullPathOf(Refii.Models.Engine_ShaderSpecialisation.PathOf(shader));

    /// <summary>For `charcensus`: bodies taking part, drawing far, changes made, and the settings.</summary>
    public static Dictionary<string, object?> Census() => new()
    {
        ["bodies"] = Bodies.Count,
        ["farShadedBodies"] = _farBodies,
        ["farSinglePassBodies"] = _farPassBodies,
        ["changes"] = _swaps,
        [LodBiasKey] = LodBias,
        [FarShadingKey] = FarShadingMetres,
        [FarSinglePassKey] = FarSinglePassMetres,
        [AndroidCharacterShading.LiteKey] = AndroidCharacterShading.On.Lite,
        [AndroidCharacterShading.NormalMapsKey] = AndroidCharacterShading.On.FastNormalMaps ? "fast" : "full",
        [AndroidCharacterShading.FragmentWorldKey] = AndroidCharacterShading.On.FragmentWorld,
        [AndroidCharacterShading.GroundAoKey] = AndroidCharacterShading.On.Ground.ToString().ToLowerInvariant(),
        [AndroidCharacterFarLods.Key] = AndroidCharacterFarLods.Centimetres,
        ["farLevelsAdded"] = AndroidCharacterFarLods.LevelsAdded,
        [AndroidFurShellLods.Key] = AndroidFurShellLods.On,
        ["furShellLevelsAdded"] = AndroidFurShellLods.LevelsAdded,
        [AndroidBodyMerge.Key] = AndroidBodyMerge.AnyLods,
    };
}
