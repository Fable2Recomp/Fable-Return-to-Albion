using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The modern renderer's shell fur (the dog and the wolves): the furred surface drawn again once
/// per shell on our own shader (fur_shell.gdshader), lit by Godot's lights.
/// </summary>
/// <remarks>
/// The title's rule is Engine_ClassicFur's: N - 1 shells, N = clamp(int((L / d) / (2 tan(fovy / 2))
/// x 142.857 + 1), 16, 30) with d the distance to the bound sphere and L the material's fur
/// length; the shells reach L (1 + 0.1 d). The shell mesh and the slices are that class's. The
/// classic shells read two prelit textures (the skin and the fur lit in uv space, at an exposure
/// the shells' own 0.3 B undoes); here Godot lights each shell directly, so neither exists.
///
/// The shells are a child of the furred instance on its skeleton, one pass per shell chained with
/// NextPass; the count and length are instance uniforms, the layer a pass's own.
///
/// THE SHELLS FADE WITH THE BODY, A REMASTER CHOICE (the title's never do: Engine_EntityAppearance,
/// "THE FUR SHELLS FADE WITH THE BODY"). The shell node takes the body's Transparency, the cut
/// staying at 16 on the slice's alpha; while the body keeps its depth the chain also draws each
/// shell's depth first (fur_shell_depth.gdshader, render priority -128), so only the nearest
/// surviving texel blends. At rest neither exists. The shells go when the colour pass is not
/// built, as the title's do. They cast no shadow: the shell item draws only in pass type 2
/// (`sub_82279378` 0x82279384 `lwz r11, 0x6FC(r4)` / `cmpwi cr6, r11, 2`; disc `sub_82279238`
/// 0x82279244).
/// </remarks>
public static class Engine_ModernFur
{
    private const string ShaderPath = "res://src/Engine/material/fur_shell.gdshader";
    private const string DepthShaderPath = "res://src/Engine/material/fur_shell_depth.gdshader";

    private static readonly StringName ShellsName = "fur_shells";
    private static readonly StringName LengthName = "fur_length";
    private static readonly StringName LayerName = "fur_layer";
    private static readonly StringName OcclusionName = "fur_occlusion";
    private static readonly StringName SlicesName = "fur_slices";
    private static readonly StringName AlbedoName = "albedo_tex";
    private static readonly StringName PbrName = "is_pbr";
    private static readonly StringName ProbeRName = "probe_r";
    private static readonly StringName ProbeGName = "probe_g";
    private static readonly StringName ProbeBName = "probe_b";
    private static readonly StringName LightmapColourName = "lightmap_colour";
    private static readonly StringName EntityTintName = "entity_tint";
    private static readonly StringName AmbientScaleName = "fable_ambient_scale";

    /// <summary>Frames between copies of the source's lighting uniforms.</summary>
    private const int ShareFrames = 15;

    private sealed class Furred
    {
        public required MeshInstance3D Source;
        public required MeshInstance3D Shell;
        public required ShaderMaterial[] Passes;
        public required float Length;
        public int Linked = -1;
        public int Stagger;
        public int LastShells = -1;
        public float LastLength = -1f;
        public bool SharedOnce;
        public bool LastHasProbe;
        public Vector4 LastProbeR;
        public Vector4 LastProbeG;
        public Vector4 LastProbeB;
        public Vector4 LastLightmapColour;
        public Vector4 LastEntityTint = Vector4.One;
        public float LastAmbientScale = -1f;
        /// <summary>The shell node's visibility: the source's colour pass is built (Engine_EntityAppearance.IsSubmitted).</summary>
        public bool Shown = true;
        /// <summary>The body's fade on the shell node (Engine_EntityAppearance.FadeOf).</summary>
        public float Transparency;
        /// <summary>The body keeps its depth while faded: the chain draws the shells' depth first.</summary>
        public bool Depth;
        public bool LinkedDepth;
        /// <summary>The depth-only passes, one per shell; made at the first faded frame.</summary>
        public ShaderMaterial[]? DepthPasses;
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<MeshInstance3D> Pending = new();
    private static readonly Dictionary<ulong, Furred> FurredById = new();
    private static readonly List<ulong> Gone = new();
    private static Shader? _shader;
    private static Shader? _depthShader;
    private static bool _depthTried;
    private static long _frame;

    /// <summary>Furred instances, and the shells drawn on the last frame.</summary>
    public static (int Instances, int Shells) Counts
    {
        get
        {
            int shells = 0;
            foreach (Furred f in FurredById.Values)
            {
                shells += f.Shown ? Math.Max(f.Linked, 0) : 0;
            }
            return (FurredById.Count, shells);
        }
    }

    /// <summary>A lit instance; one whose mesh carries fur gets its shells at the next pump. Any thread.</summary>
    public static void Attach(GeometryInstance3D node)
    {
        if (!Engine_ClassicMaterial.Active && node is MeshInstance3D mesh && mesh.Mesh is ArrayMesh source &&
            source.HasMeta(Engine_MdlMesh.FurMeta))
        {
            // Before the lighting this call precedes is written: Share reads it back (Engine_InstanceParams).
            Refii.Core.Engine_InstanceParams.Watch(mesh);
            Pending.Enqueue(mesh);
        }
    }

    /// <summary>Sets every furred instance's shells for the frame. Main thread, once a frame.</summary>
    public static void Pump(Camera3D camera)
    {
        ++_frame;
        while (Pending.TryDequeue(out MeshInstance3D? mesh))
        {
            AttachNow(mesh);
        }
        Gone.Clear();
        float halfTan = MathF.Tan(Mathf.DegToRad(camera.Fov) * 0.5f);
        Vector3 eye = camera.GlobalPosition;
        foreach ((ulong id, Furred furred) in FurredById)
        {
            if (!GodotObject.IsInstanceValid(furred.Source) || !GodotObject.IsInstanceValid(furred.Shell))
            {
                Gone.Add(id);
                continue;
            }
            if (!furred.Source.IsVisibleInTree())
            {
                continue;
            }
            // Drawn while its colour pass is built, else not at all (the title's gate).
            bool shown = Refii.Entities.Engine_EntityAppearance.IsSubmitted(furred.Source);
            if (furred.Shown != shown)
            {
                furred.Shown = shown;
                furred.Shell.Visible = shown;
            }
            if (!shown)
            {
                continue;
            }
            (float fade, bool depth) = Refii.Entities.Engine_EntityAppearance.FadeOf(furred.Source);
            if (furred.Transparency != fade)
            {
                furred.Transparency = fade;
                furred.Shell.Transparency = fade;
            }
            furred.Depth = depth && fade > 0f;
            if (furred.Shell.Layers != furred.Source.Layers)
            {
                furred.Shell.Layers = furred.Source.Layers;
            }
            Aabb localBox = furred.Source.CustomAabb.Size != Vector3.Zero
                ? furred.Source.CustomAabb
                : (furred.Source.Mesh?.GetAabb() ?? default);
            Aabb box = furred.Source.GlobalTransform * localBox;
            float radius = box.Size.Length() * 0.5f;
            float d = MathF.Max((eye - box.GetCenter()).Length() - radius, camera.Near);
            int n = Math.Clamp((int)((furred.Length / d) / halfTan * 142.85713f + 1f), 16, Engine_ClassicFur.MaxShells);
            // The shells are the node's: on split screen the nearer view's (the finer) count and length.
            for (int v = 0; v < Engine_ClassicViews.Count && Engine_ClassicViews.Count > 1; v++)
            {
                if (Engine_ClassicViews.Camera(v) is not { } view)
                {
                    continue;
                }
                float dv = MathF.Max((view.GlobalPosition - box.GetCenter()).Length() - radius, view.Near);
                float tv = MathF.Tan(Mathf.DegToRad(view.Fov) * 0.5f);
                n = Math.Max(n, Math.Clamp((int)((furred.Length / dv) / tv * 142.85713f + 1f), 16, Engine_ClassicFur.MaxShells));
                d = MathF.Min(d, dv);
            }
            n = Refii.AndroidRuntime.AndroidFurShells.Cap(n);
            if (furred.LastShells != n)
            {
                furred.LastShells = n;
                furred.Shell.SetInstanceShaderParameter(ShellsName, (float)n);
            }
            float reach = furred.Length * (1f + 0.1f * d);
            if (MathF.Abs(furred.LastLength - reach) > 1e-4f)
            {
                furred.LastLength = reach;
                furred.Shell.SetInstanceShaderParameter(LengthName, reach);
            }
            Link(furred, n - 1);
            if ((_frame + furred.Stagger) % ShareFrames == 0)
            {
                Share(furred);
            }
        }
        foreach (ulong id in Gone)
        {
            FurredById.Remove(id);
        }
    }

    private static void AttachNow(MeshInstance3D mesh)
    {
        if (!GodotObject.IsInstanceValid(mesh) || mesh.Mesh is not ArrayMesh source ||
            FurredById.ContainsKey(mesh.GetInstanceId()))
        {
            return;
        }
        if (Engine_ClassicFur.Shells(source) is not { } shellMesh ||
            source.GetMeta(Engine_MdlMesh.FurMeta).AsGodotArray() is not { Count: > 0 } blocks ||
            blocks[0].AsGodotDictionary()["fur"].AsFloat32Array() is not { Length: >= 17 } fur)
        {
            return;
        }
        _shader ??= GD.Load<Shader>(ShaderPath);
        if (_shader is null)
        {
            return;
        }

        ShaderMaterial? skin = SkinOf(mesh, shellMesh);
        Texture2DArray slices = Engine_ClassicFur.SliceArrayFor(fur[16]);
        var passes = new ShaderMaterial[Engine_ClassicFur.MaxShells - 1];
        for (int i = 0; i < passes.Length; i++)
        {
            var pass = new ShaderMaterial
            {
                Shader = _shader,
                RenderPriority = Engine_MdlMaterial.CutoutRenderPriority,
            };
            pass.SetShaderParameter(LayerName, i + 1);
            pass.SetShaderParameter(OcclusionName, fur[9]);
            pass.SetShaderParameter(SlicesName, slices);
            if (skin is not null)
            {
                pass.SetShaderParameter(AlbedoName, skin.GetShaderParameter(AlbedoName));
                pass.SetShaderParameter(PbrName, skin.GetShaderParameter(PbrName));
            }
            passes[i] = pass;
        }
        var shell = new MeshInstance3D
        {
            Name = "FurShells",
            Mesh = shellMesh,
            Skin = mesh.Skin,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = mesh.Layers,
            MaterialOverride = passes[0],
        };
        mesh.AddChild(shell);
        Refii.AndroidRuntime.AndroidCharacterLevers.Shell(mesh, shell);
        if (mesh.GetNodeOrNull(mesh.Skeleton) is { } skeleton)
        {
            shell.Skeleton = shell.GetPathTo(skeleton);
        }
        var furred = new Furred
        {
            Source = mesh,
            Shell = shell,
            Passes = passes,
            Length = fur[8],
            Stagger = FurredById.Count % ShareFrames,
        };
        Share(furred);
        FurredById[mesh.GetInstanceId()] = furred;
    }

    /// <summary>The skin material: the one the furred primitive's source surface draws with.</summary>
    private static ShaderMaterial? SkinOf(MeshInstance3D mesh, ArrayMesh shellMesh)
    {
        if (!shellMesh.HasMeta(Engine_ClassicFur.SourceSurfacesMeta) ||
            shellMesh.GetMeta(Engine_ClassicFur.SourceSurfacesMeta).AsInt32Array() is not { Length: > 0 } surfaces)
        {
            return null;
        }
        int s = surfaces[0];
        Material? material = mesh.MaterialOverride ?? mesh.GetSurfaceOverrideMaterial(s) ?? mesh.Mesh.SurfaceGetMaterial(s);
        return material as ShaderMaterial;
    }

    /// <summary>
    /// Chains the first <paramref name="count"/> shell passes, then, while the body keeps its
    /// depth, the same shells' depth passes.
    /// </summary>
    private static void Link(Furred furred, int count)
    {
        ShaderMaterial[]? depth = furred.Depth ? DepthPassesOf(furred) : null;
        if (furred.Linked == count && furred.LinkedDepth == (depth is not null))
        {
            return;
        }
        furred.Linked = count;
        furred.LinkedDepth = depth is not null;
        for (int i = 0; i < furred.Passes.Length; i++)
        {
            furred.Passes[i].NextPass = i + 1 < count ? furred.Passes[i + 1]
                : i + 1 == count && depth is not null ? depth[0] : null;
            if (depth is not null)
            {
                depth[i].NextPass = i + 1 < count ? depth[i + 1] : null;
            }
        }
    }

    /// <summary>The shells' depth passes, each on its colour pass's layer and slices.</summary>
    private static ShaderMaterial[]? DepthPassesOf(Furred furred)
    {
        if (furred.DepthPasses is not null)
        {
            return furred.DepthPasses;
        }
        if (_depthShader is null && !_depthTried)
        {
            _depthTried = true;
            _depthShader = GD.Load<Shader>(DepthShaderPath);
        }
        if (_depthShader is null)
        {
            return null;
        }
        var passes = new ShaderMaterial[furred.Passes.Length];
        for (int i = 0; i < passes.Length; i++)
        {
            var pass = new ShaderMaterial { Shader = _depthShader, RenderPriority = (int)Material.RenderPriorityMin };
            pass.SetShaderParameter(LayerName, i + 1);
            pass.SetShaderParameter(SlicesName, furred.Passes[i].GetShaderParameter(SlicesName));
            passes[i] = pass;
        }
        return furred.DepthPasses = passes;
    }

    /// <summary>The source's ambient probe, bake mode and tint, onto the shells without calling GetInstanceShaderParameter.</summary>
    private static void Share(Furred furred)
    {
        Refii.Lighting.Engine_LevelBakedLighting.ReadInstanceLighting(
            furred.Source,
            staticProp: false,
            out bool hasProbe,
            out Vector4 probeR,
            out Vector4 probeG,
            out Vector4 probeB,
            out Vector4 lightmapColour,
            out float ambientScale);

        if (hasProbe && (!furred.LastHasProbe ||
            furred.LastProbeR != probeR ||
            furred.LastProbeG != probeG ||
            furred.LastProbeB != probeB))
        {
            furred.LastHasProbe = true;
            furred.LastProbeR = probeR;
            furred.LastProbeG = probeG;
            furred.LastProbeB = probeB;
            furred.Shell.SetInstanceShaderParameter(ProbeRName, probeR);
            furred.Shell.SetInstanceShaderParameter(ProbeGName, probeG);
            furred.Shell.SetInstanceShaderParameter(ProbeBName, probeB);
        }

        if (!furred.SharedOnce || furred.LastLightmapColour != lightmapColour)
        {
            furred.LastLightmapColour = lightmapColour;
            furred.Shell.SetInstanceShaderParameter(LightmapColourName, lightmapColour);
        }

        if (!furred.SharedOnce || MathF.Abs(furred.LastAmbientScale - ambientScale) > 1e-4f)
        {
            furred.LastAmbientScale = ambientScale;
            furred.Shell.SetInstanceShaderParameter(AmbientScaleName, ambientScale);
        }

        Vector4 tint = Refii.Entities.Engine_EntityAppearance.GetModernTint(furred.Source);
        if (furred.LastEntityTint != tint)
        {
            furred.LastEntityTint = tint;
            furred.Shell.SetInstanceShaderParameter(EntityTintName, tint);
        }

        furred.SharedOnce = true;
    }
}
