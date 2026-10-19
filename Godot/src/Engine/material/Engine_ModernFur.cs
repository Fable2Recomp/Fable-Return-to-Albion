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
/// </remarks>
public static class Engine_ModernFur
{
    private const string ShaderPath = "res://src/Engine/material/fur_shell.gdshader";

    private static readonly StringName ShellsName = "fur_shells";
    private static readonly StringName LengthName = "fur_length";
    private static readonly StringName LayerName = "fur_layer";
    private static readonly StringName OcclusionName = "fur_occlusion";
    private static readonly StringName SlicesName = "fur_slices";
    private static readonly StringName AlbedoName = "albedo_tex";
    private static readonly StringName PbrName = "is_pbr";
    /// <summary>The source's lighting instance uniforms the shells share (fable_lighting, the entity tint).</summary>
    private static readonly StringName[] Shared = { "probe_r", "probe_g", "probe_b", "lightmap_colour", "entity_tint", "fable_ambient_scale" };

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
    }

    private static readonly System.Collections.Concurrent.ConcurrentQueue<MeshInstance3D> Pending = new();
    private static readonly Dictionary<ulong, Furred> FurredById = new();
    private static readonly List<ulong> Gone = new();
    private static Shader? _shader;
    private static long _frame;

    /// <summary>Furred instances, and the shells drawn on the last frame.</summary>
    public static (int Instances, int Shells) Counts
    {
        get
        {
            int shells = 0;
            foreach (Furred f in FurredById.Values)
            {
                shells += Math.Max(f.Linked, 0);
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
            var pass = new ShaderMaterial { Shader = _shader };
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

    /// <summary>Chains the first <paramref name="count"/> shell passes.</summary>
    private static void Link(Furred furred, int count)
    {
        if (furred.Linked == count)
        {
            return;
        }
        furred.Linked = count;
        for (int i = 0; i < furred.Passes.Length; i++)
        {
            furred.Passes[i].NextPass = i + 1 < count ? furred.Passes[i + 1] : null;
        }
    }

    /// <summary>The source's ambient probe, bake mode and tint, onto the shells.</summary>
    private static void Share(Furred furred)
    {
        foreach (StringName name in Shared)
        {
            Variant value = furred.Source.GetInstanceShaderParameter(name);
            if (value.VariantType != Variant.Type.Nil)
            {
                furred.Shell.SetInstanceShaderParameter(name, value);
            }
        }
    }
}
