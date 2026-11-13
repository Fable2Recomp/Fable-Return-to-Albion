using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;

namespace Refii.Models;

/// <summary>
/// The classic renderer's spot shadow maps: the 512² depth map each shadowing spot owns, drawn by
/// Godot from the spot and handed to the translated shaders as one layer of an array, with the
/// map's transform in the light group's row.
/// </summary>
/// <remarks>
/// THE TITLE'S CONTRACT (TU1, verified):
///   - A spot with L+336 and not L+338 owns a shadow object S (`Render_SpotLight_Ctor`), whose
///     map renders every frame (`Render_RenderAllShadowMaps`; S+8 is set at construction).
///   - Camera (`Render_SpotShadow_UpdateCamera` 0x82AA1170): eye L+288, looking along L+304,
///     `Math_PerspectiveFovRH_ZeroNear`(fovY 2·L+324, near S+56, far L+452·L+320); S+512 is
///     the texture matrix (0.5, −0.5, offset 0.5 + 0.5/512) times transpose(view·proj).
///   - Casters: the scene query with mask 8, the bit that puts a twin in the near sun cascade
///     (`sub_821D4F60(scene, 2, S+24 == 0 ? 8 : 16, …)`), drawn with depth bias −S+48 and slope
///     −S+52 into a target cleared to 0 that holds 1 − z: Godot's reversed depth for the same
///     camera, as the cascades' capture takes it.
///   - Receiver: a draw's light group binds ONE map, that of its qualifying spot with the
///     lowest manager index (`Render_LightGroup_Build` 0x82B1EE68: G+20 is overwritten by each
///     qualifying light, walked highest index first), to s9 with PS c32..c35 = S+512 and
///     c48 = (1/512, 1/512, 512, 512), and sets b7 (`Render_BindShadowObjectConstants_FixedRegs`
///     0x821E28C8). Every slot whose row-34 flag is set multiplies by that map: a second
///     shadowing spot in the group is shadowed with the first's map, as on the console.
/// HERE: the maps are layers of one array (Godot has no per-instance sampler); a group's row
/// carries its spot's c32..c35 and c48 with the layer in c48.z, which no stage reads (every
/// stage reads c48.xy only). The translator fetches s9 at that layer. b7 is set on every lit
/// material: a group without a map has row 34 zero and the frame's inert transform.
/// A spot's map is drawn while some group asks for it this frame; the title draws every
/// flagged spot's, but one no draw reads changes nothing on screen. At most
/// <see cref="MaxMaps"/> at once: a spot keeps its layer while it is asked for, a new one takes
/// a free layer, and one left over lights its groups unshadowed and is counted
/// (<see cref="Refused"/>).
/// </remarks>
[Tool]
public partial class Engine_ClassicSpotShadows : Node
{
    public const int MapSize = 512;
    public const int MaxMaps = 8;

    /// <summary>
    /// The spot transform's first register (g_SpotLightShadowTransform, PS c32..c35) and the
    /// resolution's (g_SpotShadowMapResolution, c48), by the title's map. Fable 3's materials read
    /// neither: only its spotlight_shadowmap_check stages bind the transform (c33) and s9, and no
    /// Fable 3 stage declares the resolution, so that one is <see cref="Engine_ClassicRegisters.Absent"/>
    /// and its writes are dropped.
    /// </summary>
    public static int TransformRegister => Engine_ClassicRegisters.Ps.SpotLightShadowTransform;
    public static int ResolutionRegister => Engine_ClassicRegisters.Ps.SpotShadowMapResolution;

    private readonly SubViewport?[] _viewports = new SubViewport?[MaxMaps];
    private readonly Camera3D?[] _cameras = new Camera3D?[MaxMaps];
    private readonly Engine_ClassicShadows.Capture?[] _captures = new Engine_ClassicShadows.Capture?[MaxMaps];
    private readonly int[] _owner = new int[MaxMaps];
    private static readonly Texture2DArrayRD _array = new();
    private Rid _arrayRid;

    // Spots (manager index) a group asked for this frame, and the layer each holds.
    private static readonly HashSet<int> _asked = new();
    private static readonly HashSet<int> _refused = new();
    private static readonly System.Collections.Generic.Dictionary<int, int> _layerOf = new();
    private static readonly float[] _registers = new float[20];

    /// <summary>The live instance.</summary>
    public static Engine_ClassicSpotShadows? Instance { get; private set; }

    /// <summary>
    /// The array the materials bind to g_SpotLightShadowMapSampler: one object for the session,
    /// so a material built before the node binds it too; the node points it at the maps.
    /// </summary>
    public static Texture2DArrayRD Maps => _array;

    /// <summary>The sampler's uniform name in the translated stages.</summary>
    public static readonly StringName SamplerName = "ps_g_SpotLightShadowMapSampler";

    /// <summary>b7 g_DoSpotShadows, in the shared booleans' pixel half.</summary>
    public const uint DoSpotShadows = 1u << (16 + 7);

    /// <summary>Spots asked for on the last frame that got no map (more than <see cref="MaxMaps"/>).</summary>
    public static int Refused { get; private set; }

    /// <summary>Maps drawn on the last frame.</summary>
    public static int Drawn { get; private set; }

    private bool _initialized;

    public override void _Ready()
    {
        Instance = this;
        System.Array.Fill(_owner, -1);
        EnsureClassicInitialized();
    }

    private void EnsureClassicInitialized()
    {
        if (_initialized || !Engine_ClassicMaterial.Active || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return;
        }

        _initialized = true;
        for (int k = 0; k < MaxMaps; k++)
        {
            var capture = new Engine_ClassicShadows.Capture();
            var viewport = new SubViewport
            {
                Name = "ClassicSpotShadow" + k,
                Size = new Vector2I(MapSize, MapSize),
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                Disable3D = true,
                PositionalShadowAtlasSize = 0,
                GuiDisableInput = true,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
                ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
                UseTaa = false,
                UseDebanding = false,
                World3D = Engine_ClassicCasters.World,
            };
            var camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Perspective,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Current = true,
                Compositor = new Compositor { CompositorEffects = new Array<CompositorEffect> { capture } },
                Environment = new Godot.Environment(),
                CullMask = Engine_ClassicShadows.NearCasterLayer | Engine_ClassicShadows.ShadowPassLayer,
            };
            viewport.AddChild(camera);
            AddChild(viewport);
            _viewports[k] = viewport;
            _cameras[k] = camera;
            _captures[k] = capture;
        }
        RenderingServer.CallOnRenderThread(Callable.From(CreateArray));
    }

    /// <summary>The array: one R32F layer per map, cleared to 0 (no caster). Render thread.</summary>
    private void CreateArray()
    {
        if (!Engine_ClassicMaterial.Active || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return;
        }

        RenderingDevice? rd = RenderingServer.GetRenderingDevice();
        if (rd is null)
        {
            return;
        }
        var layers = new Array<byte[]>();
        byte[] empty = new byte[MapSize * MapSize * 4];
        for (int k = 0; k < MaxMaps; k++)
        {
            layers.Add(empty);
        }
        _arrayRid = rd.TextureCreate(new RDTextureFormat
        {
            Width = MapSize,
            Height = MapSize,
            ArrayLayers = MaxMaps,
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2DArray,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit
                | RenderingDevice.TextureUsageBits.CanUpdateBit,
        }, new RDTextureView(), layers);
        if (!Refii.Core.Engine_RdHealth.CheckRdValid(_arrayRid, "Engine_ClassicSpotShadows.CreateArray"))
        {
            _arrayRid = default;
            return;
        }
        Refii.Core.Engine_TracedMemory.Alloc(Refii.Core.Engine_RdRetire.TracedPool, _arrayRid.Id, (ulong)MapSize * MapSize * 4 * MaxMaps);
        Rid rid = _arrayRid;
        for (int k = 0; k < MaxMaps; k++)
        {
            if (_captures[k] is { } capture)
            {
                capture.ArrayTarget = rid;
                capture.ArrayLayer = k;
            }
        }
        Callable.From(() => _array.TextureRdRid = rid).CallDeferred();
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        _asked.Clear();
        _layerOf.Clear();
        Rid rid = _arrayRid;
        _arrayRid = default;
        _array.TextureRdRid = default;
        if (rid.IsValid)
        {
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                Refii.Core.Engine_TracedMemory.Free(Refii.Core.Engine_RdRetire.TracedPool, rid.Id);
                Refii.Core.Engine_RdRetire.Retire(rid);
            }));
        }
    }

    /// <summary>
    /// The frame's inert spot registers, which every row not owned by a shadowing group keeps:
    /// every position maps to the map's centre at z = 0, which reads lit against any stored
    /// depth, so a stage with b7 set and no map is unshadowed and finite. Main thread, with the
    /// frame's lights.
    /// </summary>
    public static void WriteFrameDefaults()
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return;
        }

        Span<float> inert = stackalloc float[16]
        {
            0f, 0f, 0f, 0.5f,
            0f, 0f, 0f, 0.5f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 1f,
        };
        Engine_ClassicConstants.SetFrame(true, TransformRegister, inert);
        Span<float> resolution = stackalloc float[4] { 1f / MapSize, 1f / MapSize, 0f, MapSize };
        Engine_ClassicConstants.SetFrame(true, ResolutionRegister, resolution);
    }

    /// <summary>
    /// The group's shadowing spot, as `Render_LightGroup_Build` picks it: the qualifying spot with
    /// the lowest manager index among <paramref name="members"/>; -1 when none.
    /// </summary>
    public static int ShadowingSpot(IEnumerable<Engine_ClassicLights.Light> members)
    {
        int best = -1;
        foreach (Engine_ClassicLights.Light light in members)
        {
            if (Engine_ClassicLights.OwnsSpotMap(light) && (best < 0 || light.Index < best))
            {
                best = light.Index;
            }
        }
        return best;
    }

    /// <summary>
    /// Writes a group row's spot registers: the shadowing spot's transform and its layer, or the
    /// frame's inert ones when the group has none or its spot got no map (then the row's
    /// own-map flags are cleared, so its spots light unshadowed). Main thread.
    /// </summary>
    public static void WriteRow(int row, int spot, float[] block)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return;
        }

        int layer = spot >= 0 ? Ask(spot) : -1;
        if (layer < 0 || !Engine_ClassicLights.TryLocal(spot, out Engine_ClassicLights.Local local))
        {
            // Row 34/35: the slots' own-map flags.
            System.Array.Clear(block, 34 * 4, 8);
            Engine_ClassicConstants.CopyFrame(true, _frameScratch);
            CopyFrameRegisters(row, TransformRegister, 4);
            CopyFrameRegisters(row, ResolutionRegister, 1);
            return;
        }
        float[,] m = Transform(local);
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 4; c++)
            {
                // Column-vector rows of the row-vector product, as S+512 holds them.
                _registers[r * 4 + c] = m[c, r];
            }
        }
        _registers[16] = 1f / MapSize; _registers[17] = 1f / MapSize; _registers[18] = layer; _registers[19] = MapSize;
        Engine_ClassicConstants.SetRowBlock(true, row, TransformRegister, _registers.AsSpan(0, 16));
        Engine_ClassicConstants.SetRowBlock(true, row, ResolutionRegister, _registers.AsSpan(16, 4));
    }

    private static readonly float[] _frameScratch = new float[Engine_ClassicConstants.Floats];

    /// <summary>The frame's copy of <paramref name="count"/> registers into a row; none for a register the title lacks.</summary>
    private static void CopyFrameRegisters(int row, int register, int count)
    {
        if (register >= 0)
        {
            Engine_ClassicConstants.SetRowBlock(true, row, register, _frameScratch.AsSpan(register * 4, count * 4));
        }
    }

    /// <summary>The spot's layer this frame, or -1 when every map is taken.</summary>
    private static int Ask(int spot)
    {
        if (!Engine_ClassicMaterial.Active)
        {
            return -1;
        }

        _asked.Add(spot);
        if (_layerOf.TryGetValue(spot, out int layer))
        {
            return layer;
        }
        for (int k = 0; k < MaxMaps; k++)
        {
            if (!_layerOf.ContainsValue(k))
            {
                _layerOf[spot] = k;
                return k;
            }
        }
        _refused.Add(spot);
        return -1;
    }

    /// <summary>
    /// Places this frame's maps and frees the layers no group asked for. Main thread, after the
    /// light groups' rows are written (Engine_ClassicLitInstances.Pump).
    /// </summary>
    public void Place()
    {
        if (!Engine_ClassicMaterial.Active || Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            for (int k = 0; k < MaxMaps; k++)
            {
                _owner[k] = -1;
                if (_viewports[k] is { } idleViewport)
                {
                    idleViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                    idleViewport.Disable3D = true;
                }
            }
            _layerOf.Clear();
            _asked.Clear();
            _refused.Clear();
            Drawn = 0;
            Refused = 0;
            return;
        }

        EnsureClassicInitialized();
        Refused = _refused.Count;
        _refused.Clear();

        // Layers whose spot no group asked for this frame are free again.
        _stale.Clear();
        foreach (int index in _layerOf.Keys)
        {
            if (!_asked.Contains(index))
            {
                _stale.Add(index);
            }
        }
        foreach (int index in _stale)
        {
            _layerOf.Remove(index);
        }

        int drawn = 0;
        for (int k = 0; k < MaxMaps; k++)
        {
            int owner = -1;
            foreach ((int index, int layer) in _layerOf)
            {
                if (layer == k)
                {
                    owner = index;
                    break;
                }
            }
            _owner[k] = owner;
            if (_viewports[k] is not { } viewport || _cameras[k] is not { } camera || _captures[k] is not { } capture)
            {
                continue;
            }
            if (owner < 0 || !Engine_ClassicLights.TryLocal(owner, out Engine_ClassicLights.Local local))
            {
                viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
                viewport.Disable3D = true;
                continue;
            }
            Vector3 up = Up(local.Direction);
            Vector3 eyeG = Engine_ClassicShadows.ToGodot(local.Origin);
            Vector3 aheadG = Engine_ClassicShadows.ToGodot(local.Origin + local.Direction.Normalized() * 20f);
            camera.GlobalTransform = new Transform3D(Basis.LookingAt(aheadG - eyeG, Engine_ClassicShadows.ToGodot(up)), eyeG);
            camera.Fov = Mathf.RadToDeg(local.FovY);
            camera.Near = MathF.Max(local.Near, 0.001f);
            camera.Far = MathF.Max(local.Far, camera.Near + 0.01f);
            capture.BiasConstant = local.Bias;
            capture.BiasSlope = local.Slope;
            viewport.Disable3D = false;
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            ++drawn;
        }
        Drawn = drawn;
        _asked.Clear();
    }

    private static readonly List<int> _stale = new();

    /// <summary>The look-at's up: +y, or +z when the spot points along y (our choice; the map and its transform share it).</summary>
    private static Vector3 Up(Vector3 direction) =>
        MathF.Abs(direction.Normalized().Y) > 0.99f ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 1f, 0f);

    /// <summary>view · proj · texmat for the spot, row-vector form, in the guest's axes.</summary>
    private static float[,] Transform(in Engine_ClassicLights.Local local)
    {
        Vector3 dir = local.Direction.Normalized();
        float[,] view = Engine_ClassicShadows.LookAtRH(local.Origin, local.Origin + dir * 20f, Up(dir));
        float near = MathF.Max(local.Near, 0.001f), far = MathF.Max(local.Far, near + 0.01f);
        float ys = 1f / MathF.Tan(local.FovY * 0.5f);
        // Math_PerspectiveFovRH_ZeroNear (0x822941F0), aspect 1.
        var proj = new float[4, 4]
        {
            { ys, 0f, 0f, 0f },
            { 0f, ys, 0f, 0f },
            { 0f, 0f, far / (near - far), -1f },
            { 0f, 0f, near * far / (near - far), 0f },
        };
        return Engine_ClassicShadows.Multiply(Engine_ClassicShadows.Multiply(view, proj), Engine_ClassicShadows.TexMat(MapSize));
    }
}
