using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;
using Refii.Textures;

namespace Refii.Fx;

[Tool]
[GlobalClass]
public partial class FableFx : Node3D
{
    public const string BankPath = "art/particles/particle_bank.bnk";

    public const string MixShaderPath = "res://src/Engine/fx/fx_particle.gdshader";

    public const string AddShaderPath = "res://src/Engine/fx/fx_particle_add.gdshader";

    public const string MixAlphaShaderPath = "res://src/Engine/fx/fx_particle_mixalpha.gdshader";

    public const string DarkenShaderPath = "res://src/Engine/fx/fx_particle_darken.gdshader";

    public const string SubShaderPath = "res://src/Engine/fx/fx_particle_sub.gdshader";

    public const string DispShaderPath = "res://src/Engine/fx/fx_particle_disp.gdshader";

    public const string TextureFolder = "art/fx/pfx_textures/";

    [Export]
    public float ExposureScale { get; set; } = 1.0f;

    static FableFx()
    {
        Engine_FxDepthCaptureEffect.RegisterGlobals();
    }

    private static FxBank? _bank;
    private static bool _bankTried;

    private readonly Engine_FxSystem _system = new();
    private readonly Dictionary<string, VisualBatchMaterial> _materials = new();
    private readonly Dictionary<string, Engine_FxSystem.MeshInstanceData> _meshData = new();
    private readonly Dictionary<string, MultiMeshInstance3D> _meshNodes = new();

    private MeshInstance3D? _surface;
    private ImmediateMesh? _mesh;
    private MeshInstance3D? _dispSurface;
    private ImmediateMesh? _dispMesh;
    private bool _built;
    private string _builtName = string.Empty;

    /// <summary>
    /// One <see cref="OmniLight3D"/> per <c>CPointLightEntityType</c> the effect carries.
    ///
    /// These are why a torch now lights the wall behind it. 72 of the bank's entities are lights
    /// and every one was parsed and discarded before — the flame drew and lit nothing.
    /// </summary>
    private readonly List<OmniLight3D> _lights = new();

    private string _effectName = string.Empty;

    [Export]
    public string EffectName
    {
        get => _effectName;
        set
        {
            _effectName = value ?? string.Empty;
            _built = false;
        }
    }

    [Export]
    public bool Loop { get; set; } = true;

    [Export]
    public bool Playing { get; set; } = true;

    [Export]
    public float EffectScale { get; set; } = 1f;

    private static ulong _bankRetryAtMsec;

    public static FxBank? SharedBank()
    {
        if (_bank is not null)
        {
            return _bank;
        }

        ulong now = Time.GetTicksMsec();
        if (_bankTried && now < _bankRetryAtMsec)
        {
            return null;
        }

        _bankTried = true;
        _bankRetryAtMsec = now + 2000;
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(BankPath) ??
                        Engine_BnkMeshLoader.ReadBytes("data/" + BankPath);
        if (bytes is null)
        {
            string root = Refii.Core.Engine_GameData.Resolve();
            if (root.Length != 0)
            {
                foreach (string candidate in new[]
                         {
                             System.IO.Path.Combine(
                                 root, BankPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                             System.IO.Path.Combine(
                                 root, "data",
                                 BankPath.Replace('/', System.IO.Path.DirectorySeparatorChar)),
                         })
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        bytes = System.IO.File.ReadAllBytes(candidate);
                        break;
                    }
                }
            }
        }

        if (bytes is null)
        {
            GD.Print($"[fx] particle bank not found at {BankPath}");
            return null;
        }

        var bank = new FxBank();
        if (!Engine_FxBank.Parse(bytes, bank))
        {
            GD.Print($"[fx] particle bank parse failed: {bank.Error}");
            return null;
        }

        GD.Print(
            $"[fx] bank loaded: {bank.Effects.Count} effects, " +
            $"{bank.Emitters.Count} emitters, {bank.Systems.Count} systems");
        _bank = bank;
        return bank;
    }

    public override void _Ready()
    {
        EnsureBuilt();
    }

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fablefx");
        if (!Playing)
        {
            return;
        }

        EnsureBuilt();
        if (!_built || _mesh is null || _system.IsEmpty)
        {
            return;
        }

        // THE GUEST FREEZES WHAT THE CAMERA CANNOT SEE: `sub_83233EF0` tests each instance's
        // distance against its stated cull radius and the view cone, and both update branches
        // gate on the result — no simulation, no aging, lights left as they are (they are never
        // view-culled). 209 systems in the market cost ~20 ms a frame simulating and rebuilding
        // billboards for effects behind the camera. A frozen system keeps its last geometry.
        Camera3D? cullCamera = GetViewport()?.GetCamera3D();
        if (cullCamera is not null && !InView(cullCamera))
        {
            ++FrozenThisFrame;
            return;
        }

        _system.SetLoop(Loop);
        ApplyLit();
        // The node's world transform goes in so the system can hold world-space particles still
        // when this effect moves — see AttachToRoot. A node bolted to a wall passes the same
        // transform every frame and the system does nothing with it.
        _system.Update((float)delta, GlobalTransform);

        // THE LIGHTS FOLLOW THE SAME CLOCK AS THE PARTICLES, so a brazier's glow breathes with
        // its flame instead of sitting at a constant. The colour and the energy stay SEPARATE —
        // the guest premultiplies them, but Godot multiplies LightColor by LightEnergy on its way
        // into LIGHT_COLOR, so splitting them here reproduces the same product and lets the tint
        // reach our shader unchanged. This is the same split the placed level lights use.
        for (int i = 0; i < _lights.Count; i++)
        {
            Engine_FxSystem.FxLightState state = _system.LightAt(i);
            OmniLight3D light = _lights[i];

            float range = state.Range;
            float energy = state.Energy;
            if (!(range > 0f) || !float.IsFinite(range) || !float.IsFinite(energy))
            {
                light.Visible = false;
                continue;
            }

            light.Visible = true;
            light.Position = state.Position;
            light.OmniRange = range;
            light.LightColor = state.Colour;
            light.LightEnergy = energy * ExposureScale;

            // THE GUEST REACHES ZERO AT Cutoff, ramping over the Ramp metres inside it —
            // intensity × clamp((min(Cutoff, 1000) − dist) / Ramp, 0, 1) in `sub_821F0A50`
            // (0x821f0d38) — the same window shape as the placed fixtures, so the same Godot
            // mapping: begin = cutoff − ramp, length = ramp. The modern model doubles the
            // reach because its live lights carry what the bakes used to.
            if (state.Cutoff > 0f)
            {
                float cutoff = MathF.Min(state.Cutoff, 1000f);
                float ramp = MathF.Max(state.Ramp, 0.01f);
                if (Refii.Lighting.Engine_LightingFrame.ModernModel)
                {
                    cutoff *= 2f;
                    ramp = MathF.Max(ramp * 2f, cutoff * 0.5f);
                }

                light.DistanceFadeEnabled = true;
                light.DistanceFadeBegin = MathF.Max(cutoff - ramp, 0f);
                light.DistanceFadeLength = ramp;
            }
            else
            {
                light.DistanceFadeEnabled = false;
            }
        }

        Camera3D? camera = GetViewport()?.GetCamera3D();
#if TOOLS
        // EDITOR ONLY, and compiled out of an export rather than merely skipped.
        // EditorInterface does not exist in an export template build, so referencing it
        // unguarded fails the export even though IsEditorHint() is false there at runtime
        // and this branch could never be taken. Same guard the addons use.
        if (camera is null && Engine.IsEditorHint())
        {
            camera = EditorInterface.Singleton.GetEditorViewport3D(0).GetCamera3D();
        }
#endif

        if (camera is null)
        {
            return;
        }

        Transform3D local = GlobalTransform.AffineInverse() * camera.GlobalTransform;
        Vector3 cameraPos = local.Origin;
        Vector3 cameraRight = local.Basis.X.Normalized();
        Vector3 cameraUp = local.Basis.Y.Normalized();

        _system.EmitGeometry(
            _mesh, LookupMaterial, cameraPos, cameraRight, cameraUp, _meshData, _dispMesh);
        if (_dispMesh is not null && _dispMesh.GetSurfaceCount() > 0) Engine_FxDisplacement.NotifyActive();

        foreach ((string path, Engine_FxSystem.MeshInstanceData data) in _meshData)
        {
            if (!_meshNodes.TryGetValue(path, out MultiMeshInstance3D? node))
            {
                node = BuildMeshNode(data.MeshPath, data.Displacement);
                if (data.Displacement) Engine_FxDisplacement.Ensure(this);
                if (node is null)
                {
                    continue;
                }

                _meshNodes[path] = node;
                AddChild(node);
            }

            if (node.Multimesh is not MultiMesh multi)
            {
                continue;
            }

            int count = data.Instances.Count;
            if (multi.InstanceCount < count)
            {
                multi.InstanceCount = Math.Max(count, 16);
            }

            multi.VisibleInstanceCount = count;
            for (int i = 0; i < count; i++)
            {
                (Transform3D transform, Color colour) = data.Instances[i];
                multi.SetInstanceTransform(i, transform);
                multi.SetInstanceColor(i, colour);
            }
        }
    }

    /// <summary>Systems frozen off-camera in the last frame; reset by the metrics reader.</summary>
    public static int FrozenThisFrame;

    /// <summary>Freeze reach: beyond this an effect is a few pixels and the guest would have
    /// culled it by its radius.</summary>
    public const float FreezeDistance = 160f;

    /// <summary>A generous bounding sphere for the frustum test, so a wide plume at the edge
    /// of the view keeps simulating.</summary>
    public const float FreezeRadius = 14f;

    private bool InView(Camera3D camera)
    {
        Vector3 origin = GlobalPosition;
        if (origin.DistanceTo(camera.GlobalPosition) > FreezeDistance)
        {
            return false;
        }

        foreach (Plane plane in camera.GetFrustum())
        {
            if (plane.DistanceTo(origin) > FreezeRadius)
            {
                return false;
            }
        }

        return true;
    }

    private void ApplyLit()
    {
        float r = 1f, g = 1f, b = 1f;
        if (HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta) &&
            GetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta) is { VariantType: Variant.Type.Array } probeMeta)
        {
            var sample = probeMeta.AsGodotArray<Vector4>();
            if (sample.Count == 3)
            {
                var basis = new Vector4(0.2820948f, 0f, 0.4886025f, 0f);
                float br = MathF.Max(sample[0].Dot(basis), 0f);
                float bg = MathF.Max(sample[1].Dot(basis), 0f);
                float bb = MathF.Max(sample[2].Dot(basis), 0f);
                const float gamma = 1.3f;
                br = MathF.Pow(br, gamma); bg = MathF.Pow(bg, gamma); bb = MathF.Pow(bb, gamma);
                Refii.Lighting.LightingFrame? frame = Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting;
                float[] lm = frame?.LightmapColour ?? new float[] { 1f, 1f, 1f };
                float ar = 0f, ag = 0f, ab = 0f;
                if (frame is not null && frame.HasAmbient)
                {
                    ar = frame.AmbientColour[0] * frame.AmbientEnergy;
                    ag = frame.AmbientColour[1] * frame.AmbientEnergy;
                    ab = frame.AmbientColour[2] * frame.AmbientEnergy;
                }

                r = (br * lm[0]) + ar; g = (bg * lm[1]) + ag; b = (bb * lm[2]) + ab;
            }
        }

        _system.SetAmbient(r, g, b);
    }

    public int DebugAlive => _system.AliveCount();

    public int DebugSpawned => _system.TotalSpawned;

    public int DebugSurfaces => _mesh?.GetSurfaceCount() ?? -1;

    public void Restart()
    {
        _built = false;
        EnsureBuilt();
    }

    private void EnsureBuilt()
    {
        if (_built && _builtName == _effectName)
        {
            return;
        }

        FxBank? bank = SharedBank();
        if (bank is null || _effectName.Length == 0)
        {
            return;
        }

        foreach (MultiMeshInstance3D node in _meshNodes.Values)
        {
            node.QueueFree();
        }

        _meshNodes.Clear();
        _meshData.Clear();

        foreach (OmniLight3D light in _lights)
        {
            light.QueueFree();
        }

        _lights.Clear();

        var placement = new FxPlacement { Scale = EffectScale };
        if (Engine_BnkFxLoader.TryParseHashToken(_effectName, out uint hash))
        {
            placement.EffectHash = hash;
        }
        else
        {
            placement.EffectName = _effectName;
        }

        _system.Build(bank, new[] { placement });

        if (_surface is null)
        {
            _mesh = new ImmediateMesh();
            _surface = new MeshInstance3D
            {
                Name = "FxSurface",
                Mesh = _mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 512f,
            };
            AddChild(_surface);
        }

        if (_dispSurface is null && _system.HasDisplacement)
        {
            _dispMesh = new ImmediateMesh();
            _dispSurface = new MeshInstance3D
            {
                Name = "FxDisplacement",
                Mesh = _dispMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 512f,
                Layers = Engine_FxDisplacement.LayerMask,
            };
            AddChild(_dispSurface);
            Engine_FxDisplacement.Ensure(this);
        }

        // THE EFFECT'S LIGHTS, built once. Their colour, range and energy are all timelines and
        // move every frame, but how MANY there are is fixed by the bank.
        bool anyHalo = false;
        for (int i = 0; i < _system.LightCount; i++)
        {
            var light = new OmniLight3D
            {
                Name = $"FxLight_{i}",

                // ZERO DECAY, matching the placed level lights exactly. `fable_attenuation`
                // recovers the guest's linear ramp by inverting Godot's falloff in closed form,
                // and that inversion is only valid for the zero-decay form — Godot's default of
                // 1.0 multiplies in a `pow(d, -1)` term that is wrong everywhere and worst close
                // to the fixture, which for a torch is exactly where you are standing.
                OmniAttenuation = 0.0f,
                ShadowEnabled = false,
            };
            _lights.Add(light);
            AddChild(light);

            // THE GLOW LAYER. The guest gives every in-range, in-frustum, non-black light a
            // glow quad, gated on a per-light texture key the shipped FX data never states —
            // with a runtime-built default texture set as the holder's fallback. Fable mode
            // follows the data (no keys, no glows); the modern model runs the mechanism with
            // the procedural disc standing in for the default provider.
            uint halo = _system.HaloAt(i);
            if (halo != 0 || Refii.Lighting.Engine_LightingFrame.ModernModel)
            {
                Engine_PostFxHalos.Register(light, halo);
                anyHalo = true;
            }
        }

        if (anyHalo)
        {
            Engine_PostFxHalos.Ensure(this);
        }

        _built = true;
        _builtName = _effectName;

        if (!placement.Resolved)
        {
            GD.Print($"[fx] effect '{_effectName}' not found in bank");
        }
    }

    private readonly HashSet<string> _materialsMissingTexture = new();
    private ulong _textureRetryAtMsec;

    private VisualBatchMaterial? LookupMaterial(string key)
    {
        if (_materials.TryGetValue(key, out VisualBatchMaterial? cached))
        {
            if (_materialsMissingTexture.Contains(key) &&
                Time.GetTicksMsec() >= _textureRetryAtMsec)
            {
                _textureRetryAtMsec = Time.GetTicksMsec() + 2000;
                int retrySplit = key.IndexOf('|');
                Texture2D? late = LoadTexture(retrySplit >= 0 ? key[..retrySplit] : key);
                if (late is not null && cached.Material is ShaderMaterial sm)
                {
                    sm.SetShaderParameter("fx_tex", late);
                    _materialsMissingTexture.Remove(key);
                }
            }

            return cached;
        }

        string[] segments = key.Split('|');
        string texture = segments[0];
        string mode = segments.Length > 1 ? segments[1] : string.Empty;
        bool satMask = false;
        float softDepth = 0f;
        for (int i = 2; i < segments.Length; i++)
        {
            if (segments[i] == "sat") satMask = true;
            else if (segments[i].StartsWith("soft", StringComparison.Ordinal))
            {
                float.TryParse(segments[i][4..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out softDepth);
            }
        }
        bool additive = mode is "add" or "addone";
        string shaderPath = mode switch
        {
            "add" or "addone" => AddShaderPath,
            "mixalpha" => MixAlphaShaderPath,
            "darken" => DarkenShaderPath,
            "sub" => SubShaderPath,
            "disp" => DispShaderPath,
            _ => MixShaderPath,
        };

        Shader? shader = GD.Load<Shader>(shaderPath);
        if (shader is null)
        {
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("fx_alpha_ref", satMask ? 16.0f : 1.0f);
        material.SetShaderParameter("fx_soft_inv", softDepth > 0f ? 1.0f / softDepth : 0.0f);
        if (mode == "darken")
        {
            material.RenderPriority = -1;
        }
        else if (mode != "disp")
        {
            material.SetShaderParameter(
                "fx_exposure", Engine_FxSystem.Exposure * ExposureScale);
        }

        if (additive)
        {
            material.SetShaderParameter("fx_src_alpha", mode == "add" ? 1.0f : 0.0f);
        }

        Texture2D? tex = LoadTexture(texture);
        if (tex is not null)
        {
            material.SetShaderParameter("fx_tex", tex);
        }
        else
        {
            _materialsMissingTexture.Add(key);
        }

        var batch = new VisualBatchMaterial { Material = material };
        _materials[key] = batch;
        return batch;
    }

    private static Texture2D? LoadTexture(string name)
    {
        string leaf = name.Replace('\\', '/');
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        foreach (string candidate in new[]
                 {
                     TextureFolder + leaf,
                     name.Replace('\\', '/'),
                 })
        {
            byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(candidate) ??
                            Engine_BnkMeshLoader.ReadBytes("data/" + candidate);
            if (bytes is null)
            {
                continue;
            }

            Texture2D? texture = Engine_TexImage.TryCreateTexture(bytes, out _);
            if (texture is not null)
            {
                return texture;
            }
        }

        GD.Print($"[fx] texture '{name}' unresolved");
        return null;
    }

    public const string MeshDispShaderPath = "res://src/Engine/fx/fx_mesh_disp.gdshader";

    private static MultiMeshInstance3D? BuildMeshNode(string meshPath, bool displacement = false)
    {
        string virtualPath = meshPath.Replace('\\', '/');
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath) ??
                        Engine_BnkMeshLoader.ReadBytes("data/" + virtualPath);
        if (bytes is null)
        {
            GD.Print($"[fx] mesh '{meshPath}' unresolved");
            return null;
        }

        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(
            bytes, virtualPath, out string error, out _);
        if (mesh is null)
        {
            GD.Print($"[fx] mesh '{meshPath}' failed: {error}");
            return null;
        }

        var multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = 16,
            VisibleInstanceCount = 0,
        };

        var node = new MultiMeshInstance3D
        {
            Name = "FxMesh_" + System.IO.Path.GetFileNameWithoutExtension(virtualPath),
            Multimesh = multi,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 512f,
        };
        if (displacement)
        {
            var dispMat = new ShaderMaterial { Shader = GD.Load<Shader>(MeshDispShaderPath) };
            if (mesh.GetSurfaceCount() > 0 && mesh.SurfaceGetMaterial(0) is ShaderMaterial src && src.GetShaderParameter("albedo_tex").Obj is Texture2D albedo)
            {
                dispMat.SetShaderParameter("albedo_tex", albedo);
            }
            node.MaterialOverride = dispMat;
            node.Layers = Engine_FxDisplacement.LayerMask;
        }

        return node;
    }
}
