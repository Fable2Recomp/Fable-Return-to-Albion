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

    public const string AddLayerShaderPath = "res://src/Engine/fx/fx_particle_addone.gdshader";

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
    private ArrayMesh? _mesh;

    /// <summary>The state the geometry was last emitted for: camera position (this node's space)
    /// and which layers it reached.</summary>
    private bool _emitted;
    private Vector3 _emitPos;
    private bool _emitAddNull = true;
    private bool _emitDispActive;
    private bool _emitAddActive;
    private MeshInstance3D? _dispSurface;
    private ArrayMesh? _dispMesh;
    private MeshInstance3D? _addSurface;
    private ArrayMesh? _addMesh;
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

    /// <summary>
    /// The effect instance's intensity as the guest states it (<c>handle+52</c>, 1.0 untouched).
    /// Baked into each particle at spawn, as the guest does, so a change travels through the
    /// effect at the particles' own lifetime.
    /// </summary>
    public float Intensity { get; set; } = 1f;

    /// <summary>
    /// The instance's size scale as the guest states it (element+412 via `SetScale`, 1.0
    /// untouched): a multiplier on every keyframe's global scale, so it applies live.
    /// </summary>
    public float SizeScale { get; set; } = 1f;

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

        // Godot turns processing on in NOTIFICATION_READY, after _EnterTree and before this
        // body; the driver's say is applied again here so it survives that.
        Engine_FxDriver.Register(this);
    }

    public override void _EnterTree() => Engine_FxDriver.Register(this);

    public override void _ExitTree() => Engine_FxDriver.Unregister(this);

    /// <summary>
    /// Self-stepping, taken only while no <see cref="Engine_FxDriver"/> is in the tree (the
    /// editor and the viewer tools); in the game the driver calls <see cref="Step"/> from one
    /// callback for every system.
    /// </summary>
    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("fablefx");
        Step(delta);
    }

    /// <summary>
    /// One frame of this system. Reads the viewport camera and this node's world transform,
    /// so it must run after both are placed for the frame; see <see cref="Engine_FxDriver"/>.
    /// </summary>
    /// <param name="sharedCamera">
    /// The frame's camera, resolved once by <see cref="Engine_FxDriver"/> for every system;
    /// null falls back to this node's viewport camera.
    /// </param>
    public void Step(double delta, Camera3D? sharedCamera = null)
    {
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
        Camera3D? cullCamera = sharedCamera ?? GetViewport()?.GetCamera3D();
        if (cullCamera is not null && !InView(cullCamera))
        {
            ++FrozenThisFrame;
            return;
        }

        _system.SetLoop(Loop);
        _system.SetIntensity(Intensity);
        _system.SetSizeScale(SizeScale);
        Engine_FxSystem.FadeReachScale = Refii.Lighting.Engine_LightingFrame.ModernModel ? 2f : 1f;
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

            // The same dials as the placed fixtures. The guest packs an effect's light and a
            // placed light through one path (`sub_821F0A50`: the same range, cutoff and ramp
            // slots, the same falloff), so `placed_light_range` and `placed_light_scale` apply
            // to both. 1.0 on both is the shipped data.
            light.Visible = true;
            light.Position = state.Position;
            light.OmniRange = range * Refii.Lighting.Engine_LevelLights.PlacedRange;
            light.LightColor = state.Colour;
            light.LightEnergy = energy * ExposureScale * Refii.Lighting.Engine_LevelLights.PlacedScale;

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

        // The camera axes (in this node's space) go to the shader; `fx_billboard.gdshaderinc`
        // expands each camera-facing quad from its centre along them, so a turn does not
        // rebuild geometry.
        PublishCameraAxes(cameraRight, cameraUp);

        // The additive layer takes the ONE/ONE batches only while it can reach the screen —
        // the post chain composites it. Otherwise they draw into the scene.
        ArrayMesh? addMesh = _addMesh is not null && Engine_FxAdditiveLayer.Present ? _addMesh : null;

        // Geometry only when it can differ: a particle frame is 1/30 s, so the last emit stands
        // until a simulation step, a camera move past half a metre, or the additive layer coming
        // or going. Rotation is not a trigger: facing is the vertex shader's job, and what is
        // still built against the camera (a ribbon's side vector `tan.Cross(eye - pos)` and the
        // back-to-front sort) reads only the camera's position.
        bool turned = !_emitted || cameraPos.DistanceSquaredTo(_emitPos) > 0.25f;
        if (_system.LastSteps == 0 && !turned && (addMesh is null) == _emitAddNull)
        {
            if (_emitDispActive) Engine_FxDisplacement.NotifyActive();
            if (_emitAddActive) Engine_FxAdditiveLayer.NotifyActive();
            return;
        }

        _emitted = true;
        _emitPos = cameraPos;
        _emitAddNull = addMesh is null;
        if (addMesh is null && _addMesh is not null) _addMesh.ClearSurfaces();
        // The delegate is cached: a method group converted at the call site allocates a new Func
        // every call.
        _lookupMaterial ??= LookupMaterial;
        _system.EmitGeometry(
            _mesh, _lookupMaterial, cameraPos, cameraRight, cameraUp, _meshData, _dispMesh, addMesh);
        _emitDispActive = _dispMesh is not null && _dispMesh.GetSurfaceCount() > 0;
        _emitAddActive = addMesh is not null && addMesh.GetSurfaceCount() > 0;
        if (_emitDispActive) Engine_FxDisplacement.NotifyActive();
        if (_emitAddActive) Engine_FxAdditiveLayer.NotifyActive();

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

    private static ulong _frustumFrame = ulong.MaxValue;
    private static ulong _frustumCamera;
    private static Plane[] _frustum = System.Array.Empty<Plane>();

    /// <summary>The camera's frustum planes, read once per frame for every system: the query
    /// marshals a fresh array each call.</summary>
    private static Plane[] FrameFrustum(Camera3D camera)
    {
        ulong frame = Engine.GetProcessFrames();
        ulong id = camera.GetInstanceId();
        if (frame != _frustumFrame || id != _frustumCamera)
        {
            Godot.Collections.Array<Plane> planes = camera.GetFrustum();
            if (_frustum.Length != planes.Count) _frustum = new Plane[planes.Count];
            for (int i = 0; i < planes.Count; i++) _frustum[i] = planes[i];
            _frustumFrame = frame;
            _frustumCamera = id;
        }

        return _frustum;
    }

    private bool InView(Camera3D camera)
    {
        Vector3 origin = GlobalPosition;
        if (origin.DistanceTo(camera.GlobalPosition) > FreezeDistance)
        {
            return false;
        }

        foreach (Plane plane in FrameFrustum(camera))
        {
            if (plane.DistanceTo(origin) > FreezeRadius)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Where the scene light was last sampled for a node without a bake probe.</summary>
    private Vector3 _litSampledAt = new(float.NaN, float.NaN, float.NaN);

    private ulong _litSampledFrame;

    /// <summary>
    /// The scene light the particles lerp from: the guest's vertex shader lights a particle
    /// as `lerp(g_GlobalAmbient + point lights, keyframeColour, selfIllum)` (blob 0038).
    ///
    /// A node with a bake probe (level-spawned) composes the probe with the theme ambient. A
    /// node without one (every effect the guest starts at runtime: spells, impacts, auras)
    /// samples the theme ambient plus the registered lights at the node's position, re-sampled
    /// when the effect has moved a metre or 30 frames have passed.
    /// </summary>
    private void ApplyLit()
    {
        float r = 1f, g = 1f, b = 1f;

        // Asked every 30 frames, not every step (each query allocates a wrapper); the answer only
        // changes when something stores a probe on this node, which the cadence still catches.
        ulong askFrame = Engine.GetProcessFrames();
        if (_hasProbe is null || askFrame - _probeAskedFrame >= 30)
        {
            _hasProbe = HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta);
            _probeAskedFrame = askFrame;
        }

        if (_hasProbe == false)
        {
            Vector3 at = GlobalPosition;
            ulong frame = Engine.GetProcessFrames();
            if (_litSampledAt.IsFinite() &&
                at.DistanceSquaredTo(_litSampledAt) < 1f &&
                frame - _litSampledFrame < 30)
            {
                return;
            }

            _litSampledAt = at;
            _litSampledFrame = frame;
            if (Engine_FxSystem.SceneLightAt(at, _litScratch))
            {
                _system.SetAmbient(_litScratch[0], _litScratch[1], _litScratch[2]);
            }

            return;
        }

        // The meta is an Array behind a Variant: reading it builds wrappers every call, and
        // the sample under it only changes when the node is re-lit, so it is re-read on a
        // cadence and the folded band kept.
        ulong now = Engine.GetProcessFrames();
        if (!_probeFolded || now - _probeReadFrame >= 30)
        {
            _probeReadFrame = now;
            _probeFolded = false;
            if (GetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMeta) is { VariantType: Variant.Type.Array } probeMeta)
            {
                var sample = probeMeta.AsGodotArray<Vector4>();
                if (sample.Count == 3)
                {
                    var basis = new Vector4(0.2820948f, 0f, 0.4886025f, 0f);
                    const float gamma = 1.3f;
                    _probeBand[0] = MathF.Pow(MathF.Max(sample[0].Dot(basis), 0f), gamma);
                    _probeBand[1] = MathF.Pow(MathF.Max(sample[1].Dot(basis), 0f), gamma);
                    _probeBand[2] = MathF.Pow(MathF.Max(sample[2].Dot(basis), 0f), gamma);
                    _probeFolded = true;
                }
            }
        }

        if (_probeFolded)
        {
            {
                float br = _probeBand[0], bg = _probeBand[1], bb = _probeBand[2];
                Refii.Lighting.LightingFrame? frame = Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting;
                float[] lm = frame?.LightmapColour ?? White;
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

    private Vector3 _axisRight;
    private Vector3 _axisUp;
    private bool _axesPublished;

    private static readonly StringName CamRightParam = new("fx_cam_right");
    private static readonly StringName CamUpParam = new("fx_cam_up");

    /// <summary>
    /// Hands the shader this node's view of the camera. Instance parameters, because one
    /// material is shared by every effect on screen while each effect's mesh is built in its
    /// own node's space.
    /// </summary>
    /// <remarks>
    /// Checks validity, not just null: a freed Godot object leaves a disposed C# wrapper whose
    /// every member throws.
    /// </remarks>
    private void PublishCameraAxes(Vector3 right, Vector3 up)
    {
        // Only when they moved: each write marks the instance dirty for the render server.
        if (_axesPublished &&
            right.Dot(_axisRight) > 0.999995f && up.Dot(_axisUp) > 0.999995f)
        {
            return;
        }

        _axesPublished = true;
        _axisRight = right;
        _axisUp = up;

        if (_surface is not null && IsInstanceValid(_surface))
        {
            _surface.SetInstanceShaderParameter(CamRightParam, right);
            _surface.SetInstanceShaderParameter(CamUpParam, up);
        }

        if (_addSurface is not null && IsInstanceValid(_addSurface))
        {
            _addSurface.SetInstanceShaderParameter(CamRightParam, right);
            _addSurface.SetInstanceShaderParameter(CamUpParam, up);
        }

        if (_dispSurface is not null && IsInstanceValid(_dispSurface))
        {
            _dispSurface.SetInstanceShaderParameter(CamRightParam, right);
            _dispSurface.SetInstanceShaderParameter(CamUpParam, up);
        }
    }

    private readonly float[] _litScratch = new float[3];
    private System.Func<string, Refii.Fx.VisualBatchMaterial>? _lookupMaterial;
    private readonly float[] _probeBand = new float[3];
    private bool? _hasProbe;
    private ulong _probeAskedFrame;
    private bool _probeFolded;
    private ulong _probeReadFrame;
    private static readonly float[] White = { 1f, 1f, 1f };

    public int DebugAlive => _system.AliveCount();

    public int DebugSpawned => _system.TotalSpawned;

    /// <summary>
    /// Surfaces on the main mesh only: an "addone" batch commits to `_addMesh` and a "disp"
    /// batch to `_dispMesh`, so a purely additive effect has zero surfaces here. Read the three
    /// together.
    /// </summary>
    public int DebugSurfaces => _mesh?.GetSurfaceCount() ?? -1;

    public int DebugAddSurfaces => _addMesh?.GetSurfaceCount() ?? -1;

    public int DebugDispSurfaces => _dispMesh?.GetSurfaceCount() ?? -1;

    /// <summary>Emitters, visuals and particles this effect actually holds — see Census.</summary>
    public (int Emitters, int Visuals, int TrailVisuals, int Particles) DebugCensus => _system.Census();

    /// <summary>Whether Build resolved the effect at all; false means the bank had no such key.</summary>
    public bool DebugBuilt => _built;

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
            _mesh = new ArrayMesh();
            _surface = new MeshInstance3D
            {
                Name = "FxSurface",
                Mesh = _mesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 512f,
            };
            AddChild(_surface);
            _axesPublished = false;  // a new surface has never been told where the camera is
        }

        if (_addSurface is null && _system.HasAdditive)
        {
            _addMesh = new ArrayMesh();
            _addSurface = new MeshInstance3D
            {
                Name = "FxAdditive",
                Mesh = _addMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 512f,
                Layers = Engine_FxAdditiveLayer.LayerMask,
            };
            AddChild(_addSurface);
            _axesPublished = false;  // a new surface has never been told where the camera is
            Engine_FxAdditiveLayer.Ensure(this);
        }

        if (_dispSurface is null && _system.HasDisplacement)
        {
            _dispMesh = new ArrayMesh();
            _dispSurface = new MeshInstance3D
            {
                Name = "FxDisplacement",
                Mesh = _dispMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 512f,
                Layers = Engine_FxDisplacement.LayerMask,
            };
            AddChild(_dispSurface);
            _axesPublished = false;  // a new surface has never been told where the camera is
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
        bool additive = mode is "add" or "addone" or "addlayer";
        string shaderPath = mode switch
        {
            "add" or "addone" => AddShaderPath,
            "addlayer" => AddLayerShaderPath,
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

        if (additive && mode != "addlayer")
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
