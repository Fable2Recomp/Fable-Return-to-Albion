using System;
using Godot;
using Godot.Collections;
using Refii.Core;

namespace Refii.Models;

/// <summary>
/// The classic renderer's sun shadow maps: the two cascades the title renders every frame,
/// placed as its shadow records place them, drawn by Godot into depth viewports and handed to
/// the translated shaders as the two directional shadow samplers with their transforms.
/// </summary>
/// <remarks>
/// THE RECORD'S CONSTRUCTION (`sub_821A29B8`, `sub_821A2B08`, `sub_821A2810`, with the
/// per-frame overrides `sub_822C0FC0` applies before the pass): for cascade k with (extent,
/// back, forward) = (15, 100, 10) and (35, 120, 25), map 1024:
///   centre = eye + normalize(cameraForward) * forward, snapped to the map's texel grid;
///   lightEye = centre - sunTravel * back;
///   up = (0,1,0), or (0,0,1) when |dot(sunTravel, up)| > 0.99;
///   view = D3DXMatrixLookAtRH(lightEye, centre, up);
///   ortho = D3DXMatrixOrthoOffCenterRH(-extent, extent, -extent, extent, 0.01, extent + back);
///   texmat = (0.5,0,0,0; 0,-0.5,0,0; 0,0,1,0; 0.5+0.5/size, 0.5+0.5/size, 0, 1);
///   c36..39 (cascade 0) / c40..43 (cascade 1) = columns of view * ortho * texmat;
///   c13 / c14 = (1/size, 1/size, 0, 0).
/// The shader projects the guest-world position by those rows, compares `1 - z` against the
/// map's red channel and takes a 5x5 tap. The guest clears its map to 0 and writes 1 - z, which
/// is Godot's reversed depth for an orthographic camera with the same near and far, so the
/// viewport's depth is the map as it is.
///
/// All of it is computed in the guest's axes; only the Godot camera is placed in Godot's,
/// which are the guest's rotated ((x, y, z) -> (-x, z, y)), so the rendered map is the guest's
/// as it is and the copy applies only the biases (classic_shadow_capture.glsl).
///
/// A PAIR OF CASCADES PER VIEW. The cascades are fitted to the eye, so split screen's second
/// view has its own pair (a second set of cascade viewports) and its own transforms in its block
/// of the constant file. The materials bind ONE pair of maps (<see cref="CascadeTexture"/>): while
/// split, each set captures into a map of its own and each view's screen-shadow pass copies its
/// set into the bound pair before the view draws (<see cref="CopyForView"/>, render thread),
/// which the rendering device orders after the other view's reads.
/// </remarks>
[Tool]
public partial class Engine_ClassicShadows : Node
{
    public const int Cascades = 2;
    public const int MapSize = 1024;
    /// <summary>XR_SHADOW_PASS_LAYER in xenos_common.gdshaderinc: layer 24, used by no node.</summary>
    public const uint ShadowPassLayer = 1u << 23;

    /// <summary>
    /// The cascades draw only CASTERS, each on its own layer, as the guest's sun shadow scene
    /// ("Directional Light Renderer", sub_82AA0A98) does: a render twin casts into the near
    /// cascade when its +28 mask has bit 8 and into the far one with bit 16, and
    /// `sub_82AA3C78` sets those from the stated flag word (0x40 = no cast, 0x80 = no distant
    /// cast). Nothing else is drawn there: not the landscape, not particles, not water.
    /// Layers 25 and 26, outside every other camera's mask.
    /// </summary>
    public const uint NearCasterLayer = 1u << 24;

    public const uint FarCasterLayer = 1u << 25;

    /// <summary>Casting bits: near cascade, far cascade, both faces (0x100, twin+272).</summary>
    public const byte CastsNear = 1, CastsFar = 2, CastsTwoSided = 4;

    /// <summary>The guest's defaults: ShadowCaster 1, DontCastDistantShadows 0.</summary>
    public const byte CastsDefault = CastsNear | CastsFar;

    /// <summary>The casting bits of a stated flag word, as `sub_82AA3C78` reads it.</summary>
    public static byte CastingOf(uint flags) => (byte)(
        ((flags & 0x40u) == 0 ? CastsNear : 0) |
        ((flags & 0xC0u) == 0 ? CastsFar : 0) |
        ((flags & 0x100u) != 0 ? CastsTwoSided : 0));

    /// <summary>The cascade layers a node with these casting bits joins; none outside classic.</summary>
    public static uint CasterLayers(byte casting) =>
        !Engine_ClassicMaterial.Active ? 0u :
        ((casting & CastsNear) != 0 ? NearCasterLayer : 0u) |
        ((casting & CastsFar) != 0 ? FarCasterLayer : 0u);

    /// <summary>The cascade pass's depth biases (`sub_8228DCC8`): constant and slope scale per cascade.</summary>
    private static readonly (float Constant, float Slope)[] Biases = { (0.001f, 2.0f), (0.005f, 5.0f) };

    private static readonly (float Extent, float Back, float Forward)[] Params =
    {
        (15f, 100f, 10f),
        (35f, 120f, 25f),
    };

    private static readonly Godot.Environment DepthOnly = new();

    // Set s, cascade k at [s * Cascades + k]: set 0 draws view 0 (and is what the materials bind),
    // set 1 view 1 while the screen is split.
    private readonly SubViewport?[] _viewports = new SubViewport?[Engine_ClassicViews.Max * Cascades];
    private readonly Camera3D?[] _cameras = new Camera3D?[Engine_ClassicViews.Max * Cascades];
    private readonly Capture?[] _captures = new Capture?[Engine_ClassicViews.Max * Cascades];
    private int _activeSets = 1;
    private readonly float[] _registers = new float[16];
    private readonly float[] _four = new float[4];

    /// <summary>The live instance, for the material path to bind the maps.</summary>
    public static Engine_ClassicShadows? Instance { get; private set; }

    /// <summary>The map a cascade's sampler reads; valid for the life of the node.</summary>
    public Texture2D CascadeTexture(int cascade) => _captures[cascade]!.Texture;

    /// <summary>
    /// The map holding view <paramref name="view"/>'s cascade for a draw outside that view's own
    /// passes (an offscreen target), where the bound pair holds whichever view copied last. View
    /// 0: the bound pair, or while split the first set's own map once it stands. View 1: the
    /// second set's map (it captures into its published one; null before the first split).
    /// </summary>
    public Texture2D? CascadeTextureFor(int view, int cascade)
    {
        if (view != 0)
        {
            return _captures[Cascades + cascade]?.Texture;
        }
        Capture first = _captures[cascade]!;
        return first.Own && first.OwnReady ? first.OwnTexture : first.Texture;
    }

    /// <summary>The Godot camera that renders cascade <paramref name="cascade"/> (of the first view).</summary>
    public Camera3D CascadeCamera(int cascade) => _cameras[cascade]!;

    public override void _Ready()
    {
        BuildSet(0);
        Instance = this;
    }

    /// <summary>One view's pair of cascade viewports, cameras and captures.</summary>
    private void BuildSet(int set)
    {
        for (int k = 0; k < Cascades; k++)
        {
            int at = set * Cascades + k;
            var capture = new Capture { BiasConstant = Biases[k].Constant, BiasSlope = Biases[k].Slope };
            var compositor = new Compositor
            {
                CompositorEffects = new Array<CompositorEffect> { capture },
            };
            var viewport = new SubViewport
            {
                Name = set == 0 ? "ClassicShadow" + k : $"ClassicShadow{k}View{set}",
                Size = new Vector2I(MapSize, MapSize),
                // The main world, drawn from the light: the same objects the player sees.
                RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
                RenderTargetClearMode = SubViewport.ClearMode.Always,
                PositionalShadowAtlasSize = 0,
                GuiDisableInput = true,
                HandleInputLocally = false,
                Msaa3D = Viewport.Msaa.Disabled,
                ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
                UseTaa = false,
                UseDebanding = false,
                // The casters' own scene (Engine_ClassicCasters), not the main world: the guest's
                // sun shadow scene holds its casters and nothing else.
                World3D = Engine_ClassicCasters.World,
            };
            var camera = new Camera3D
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Size = Params[k].Extent * 2f,
                Near = 0.01f,
                Far = Params[k].Extent + Params[k].Back,
                Current = true,
                Compositor = compositor,
                // Depth is all the capture reads: an empty environment (clear colour; no sky,
                // fog, glow, GI or screen-space effects) instead of the world's.
                Environment = DepthOnly,
            };
            viewport.AddChild(camera);
            AddChild(viewport);
            _viewports[at] = viewport;
            _cameras[at] = camera;
            _captures[at] = capture;
        }
    }

    /// <summary>
    /// Draws a pair of cascades per view: builds the second view's set the first time the screen
    /// splits, and stops drawing it when the screen rejoins. Main thread.
    /// </summary>
    private void Sets(int views)
    {
        if (views == _activeSets)
        {
            return;
        }
        if (views > 1 && _captures[Cascades] is null)
        {
            BuildSet(1);
        }
        _activeSets = views;
        for (int k = 0; k < Cascades; k++)
        {
            // While split, the first set captures into a map of its own as well; the bound pair
            // takes each view's copy (CopyForView).
            _captures[k]!.Own = views > 1;
            if (_viewports[Cascades + k] is { } second)
            {
                second.RenderTargetUpdateMode = views > 1 ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            }
        }
    }

    /// <summary>
    /// Copies view <paramref name="view"/>'s cascade captures into the pair the materials bind.
    /// Render thread, from that view's compositor before it draws its opaque pass (the screen
    /// shadow mask's pre-opaque callback); only while the screen is split.
    /// </summary>
    public void CopyForView(RenderingDevice rd, int view)
    {
        for (int k = 0; k < Cascades; k++)
        {
            int at = Math.Clamp(view, 0, Engine_ClassicViews.Max - 1) * Cascades + k;
            if (_captures[at] is not { } from || _captures[k] is not { } bound)
            {
                continue;
            }
            Rid source = from.Source, target = bound.Bound;
            if (!source.IsValid || !target.IsValid || source == target)
            {
                continue;
            }
            Vector2I size = from.SourceSize, boundSize = bound.BoundSize;
            int w = Math.Min(size.X, boundSize.X), h = Math.Min(size.Y, boundSize.Y);
            if (w <= 0 || h <= 0)
            {
                continue;
            }
            rd.TextureCopy(source, target, Vector3.Zero, Vector3.Zero, new Vector3(w, h, 1), 0, 0, 0, 0);
        }
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
            Engine_ClassicCasters.Clear();
        }
    }

    /// <summary>
    /// Places both cascades for this frame and writes their registers. Main thread, after the
    /// frame feed, before Publish.
    /// </summary>
    /// <param name="main">The drawn camera.</param>
    /// <param name="sunTravelGuest">The sun light's travel direction, guest axes (twin+304).</param>
    public void Pump(Camera3D main, Vector3 sunTravelGuest)
    {
        int views = Engine_ClassicViews.Count;
        Sets(views);
        // The casters follow their drawn nodes and take the guest's eye-distance cut first: on
        // split screen a caster within its reach of either eye casts (both sets draw one world).
        using (Refii.Core.Engine_FramePhases.Measure("classic.shadows.casters"))
        {
            if (views > 1 && Engine_ClassicViews.Camera(1) is { } secondEye)
            {
                Engine_ClassicCasters.Update(main.GlobalPosition, secondEye.GlobalPosition);
            }
            else
            {
                Engine_ClassicCasters.Update(main.GlobalPosition);
            }
        }
        Vector3 travel = sunTravelGuest.Normalized();
        if (!travel.IsFinite() || travel.IsZeroApprox())
        {
            return;
        }
        for (int v = 0; v < views; v++)
        {
            Camera3D view = v == 0 ? main : Engine_ClassicViews.Camera(v) ?? main;
            PlaceSet(v, view, travel);
        }
        if ((main.CullMask & Engine_ClassicParticles.Layer) == 0)
        {
            main.CullMask |= Engine_ClassicParticles.Layer;
        }
    }

    /// <summary>One view's two cascades: fitted to its eye, its registers into its block.</summary>
    private void PlaceSet(int set, Camera3D main, Vector3 travel)
    {
        Vector3 eye = ToGuest(main.GlobalPosition);
        Vector3 forward = ToGuest(-main.GlobalTransform.Basis.Z).Normalized();
        for (int k = 0; k < Cascades; k++)
        {
            (float extent, float back, float ahead) = Params[k];
            Vector3 centre = eye + forward * ahead;

            // The first placement, to snap the centre to the texel grid in the light's plane.
            Basis(travel, out Vector3 xAxis, out Vector3 yAxis, out Vector3 up);
            float texel = 2f * extent / MapSize;
            float cx = centre.Dot(xAxis), cy = centre.Dot(yAxis);
            float sx = MathF.Truncate(cx / texel) * texel, sy = MathF.Truncate(cy / texel) * texel;
            centre += xAxis * (sx - cx) + yAxis * (sy - cy);

            Vector3 lightEye = centre - travel * back;
            float[,] view = LookAtRH(lightEye, centre, up);
            float[,] ortho = OrthoRH(-extent, extent, -extent, extent, 0.01f, extent + back);
            float[,] tex = TexMat(MapSize);
            float[,] m = Multiply(Multiply(view, ortho), tex);
            for (int column = 0; column < 4; column++)
            {
                for (int row = 0; row < 4; row++)
                {
                    _registers[column * 4 + row] = m[row, column];
                }
            }
            int register = k == 0
                ? Engine_ClassicRegisters.Ps.DirectionalLightShadowTransform0
                : Engine_ClassicRegisters.Ps.DirectionalLightShadowTransform1;
            Engine_ClassicConstants.SetFrameView(set, true, register, _registers);
            _four[0] = 1f / MapSize; _four[1] = 1f / MapSize; _four[2] = 0f; _four[3] = 0f;
            Engine_ClassicConstants.SetFrame(true,
                k == 0 ? Engine_ClassicRegisters.Ps.ShadowMapResolution0 : Engine_ClassicRegisters.Ps.ShadowMapResolution0 + 1,
                _four);

            // The Godot camera on the same basis: position, target and up carried over.
            if (_cameras[set * Cascades + k] is not { } camera)
            {
                continue;
            }
            // This cascade's casters plus the shadow-pass marker the shaders test.
            camera.CullMask = (k == 0 ? NearCasterLayer : FarCasterLayer) | ShadowPassLayer;
            Vector3 eyeG = ToGodot(lightEye), centreG = ToGodot(centre), upG = ToGodot(up);
            camera.GlobalTransform = new Transform3D(Godot.Basis.LookingAt(centreG - eyeG, upG), eyeG);
        }
    }

    // Guest (x, y, z) <-> Godot (-x, z, y): a rotation, so the light's basis carries over
    // exactly and the captured map needs no flip.
    internal static Vector3 ToGuest(Vector3 godot) => new(-godot.X, godot.Z, godot.Y);
    internal static Vector3 ToGodot(Vector3 guest) => new(-guest.X, guest.Z, guest.Y);

    /// <summary>The light's basis as D3DXMatrixLookAtRH builds it from the travel direction.</summary>
    private static void Basis(Vector3 travel, out Vector3 xAxis, out Vector3 yAxis, out Vector3 up)
    {
        up = new Vector3(0f, 1f, 0f);
        if (MathF.Abs(travel.Dot(up)) > 0.99f)
        {
            up = new Vector3(0f, 0f, 1f);
        }
        Vector3 zAxis = -travel;
        xAxis = up.Cross(zAxis).Normalized();
        yAxis = zAxis.Cross(xAxis);
    }

    /// <summary>D3DXMatrixLookAtRH, row-major for row vectors (p * M).</summary>
    internal static float[,] LookAtRH(Vector3 eye, Vector3 at, Vector3 up)
    {
        Vector3 z = (eye - at).Normalized();
        Vector3 x = up.Cross(z).Normalized();
        Vector3 y = z.Cross(x);
        return new float[4, 4]
        {
            { x.X, y.X, z.X, 0f },
            { x.Y, y.Y, z.Y, 0f },
            { x.Z, y.Z, z.Z, 0f },
            { -x.Dot(eye), -y.Dot(eye), -z.Dot(eye), 1f },
        };
    }

    /// <summary>D3DXMatrixOrthoOffCenterRH.</summary>
    private static float[,] OrthoRH(float l, float r, float b, float t, float zn, float zf)
    {
        return new float[4, 4]
        {
            { 2f / (r - l), 0f, 0f, 0f },
            { 0f, 2f / (t - b), 0f, 0f },
            { 0f, 0f, 1f / (zn - zf), 0f },
            { (l + r) / (l - r), (t + b) / (b - t), zn / (zn - zf), 1f },
        };
    }

    /// <summary>The clip-to-texture matrix `sub_821A2810` appends.</summary>
    internal static float[,] TexMat(int size)
    {
        float half = 0.5f + 0.5f / size;
        return new float[4, 4]
        {
            { 0.5f, 0f, 0f, 0f },
            { 0f, -0.5f, 0f, 0f },
            { 0f, 0f, 1f, 0f },
            { half, half, 0f, 1f },
        };
    }

    internal static float[,] Multiply(float[,] a, float[,] b)
    {
        var result = new float[4, 4];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                float sum = 0f;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[i, k] * b[k, j];
                }
                result[i, j] = sum;
            }
        }
        return result;
    }

    /// <summary>
    /// Copies a cascade viewport's depth into an R32F map the materials sample, mirrored in u.
    /// One instance per cascade; the same shape as <see cref="Refii.Fx.Engine_FxDepthCaptureEffect"/>.
    /// </summary>
    /// <summary>
    /// What the cascade samplers see: "capture" the light's depth, "black" a constant 0 map,
    /// "white" a constant 1 map. A debug lever for telling the shadow lookup's two outcomes
    /// apart in a live run.
    /// </summary>
    public static string MapMode = "capture";

    [Tool]
    public partial class Capture : CompositorEffect
    {
        /// <summary>The capture's uniform set, by the depth layer and target it binds; and its push constants.</summary>
        private readonly Refii.Core.Engine_UniformSetKey _set = new(2);
        private readonly byte[] _push = new byte[32];

        private const string ShaderPath = "res://src/Engine/material/classic_shadow_capture.glsl";

        private readonly Texture2Drd _texture = new();
        private RenderingDevice? _rd;
        private Rid _shader, _pipeline, _sampler, _target, _black, _white;
        private Vector2I _targetSize;
        private string _bound = "capture";
        private bool _ready;
        private bool _reported;

        public float BiasConstant;
        public float BiasSlope;

        /// <summary>
        /// A layer of a texture array the capture is also copied into (the spot maps,
        /// Engine_ClassicSpotShadows); unset for the cascades. Render thread.
        /// </summary>
        internal Rid ArrayTarget;
        internal int ArrayLayer = -1;

        /// <summary>
        /// Capture into a map of its own instead of the published one: the first view's set while
        /// the screen is split, whose published map takes each view's copy (CopyForView).
        /// </summary>
        public volatile bool Own;

        private Rid _own;
        private Vector2I _ownSize;
        private readonly Texture2Drd _ownTexture = new();

        /// <summary>The capture's own map as a texture (<see cref="Own"/>), for offscreen readers of its view.</summary>
        public Texture2D OwnTexture => _ownTexture;

        /// <summary>Whether <see cref="OwnTexture"/> points at a map yet. Main thread.</summary>
        public bool OwnReady { get; private set; }

        /// <summary>The map the last capture wrote. Render thread.</summary>
        internal Rid Source { get; private set; }

        /// <summary>The size of <see cref="Source"/>. Render thread.</summary>
        internal Vector2I SourceSize { get; private set; }

        /// <summary>The published map (what <see cref="Texture"/> samples). Render thread.</summary>
        internal Rid Bound => _target;

        /// <summary>The size of <see cref="Bound"/>. Render thread.</summary>
        internal Vector2I BoundSize => _targetSize;

        /// <summary>Points the published texture at the capture or at a constant map.</summary>
        private void Bind(string mode)
        {
            if (_bound == mode || _rd is null)
            {
                return;
            }
            Rid rid;
            if (mode == "capture")
            {
                if (!_target.IsValid)
                {
                    return;
                }
                rid = _target;
            }
            else
            {
                ref Rid constant = ref (mode == "white" ? ref _white : ref _black);
                if (!constant.IsValid)
                {
                    float value = mode == "white" ? 1f : 0f;
                    constant = _rd.Texture(new RDTextureFormat
                    {
                        Width = 1,
                        Height = 1,
                        Format = RenderingDevice.DataFormat.R32Sfloat,
                        TextureType = RenderingDevice.TextureType.Type2D,
                        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                            | RenderingDevice.TextureUsageBits.CanUpdateBit,
                    }, new RDTextureView(), BitConverter.GetBytes(value));
                }
                rid = constant;
            }
            _bound = mode;
            Callable.From(() => _texture.TextureRdRid = rid).CallDeferred();
        }

        public Texture2D Texture => _texture;

        public Capture()
        {
            EffectCallbackType = EffectCallbackTypeEnum.PostOpaque;
            RenderingServer.CallOnRenderThread(Callable.From(Initialise));
        }

        private void Initialise()
        {
            _rd = RenderingServer.GetRenderingDevice();
            if (_rd is null) return;
            var file = GD.Load<RDShaderFile>(ShaderPath);
            if (file is null) return;
            _shader = _rd.Shader(file.GetSpirV());
            if (!_shader.IsValid) return;
            _pipeline = _rd.ComputePipeline(_shader);
            _sampler = _rd.Sampler(new RDSamplerState
            {
                MinFilter = RenderingDevice.SamplerFilter.Nearest,
                MagFilter = RenderingDevice.SamplerFilter.Nearest,
                RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
                RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            });
            _ready = _pipeline.IsValid && _sampler.IsValid;
            if (!_ready)
            {
                GD.PushError("[classic] cascade capture did not initialise (" + ShaderPath + "); the sun shadow maps stay empty");
            }
        }

        private void EnsureTarget(Vector2I size)
        {
            if (_target.IsValid && size == _targetSize) return;
            // The materials sample the old map until the deferred re-point below lands.
            Refii.Core.Engine_RdRetire.Retire(_target);
            _target = _rd!.Texture(new RDTextureFormat
            {
                Width = (uint)size.X,
                Height = (uint)size.Y,
                Format = RenderingDevice.DataFormat.R32Sfloat,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                    | RenderingDevice.TextureUsageBits.StorageBit
                    | RenderingDevice.TextureUsageBits.CanUpdateBit
                    | RenderingDevice.TextureUsageBits.CanCopyFromBit
                    | RenderingDevice.TextureUsageBits.CanCopyToBit,
            }, new RDTextureView());
            Refii.Core.Engine_TracedMemory.Alloc(Refii.Core.Engine_RdRetire.TracedPool, _target.Id, (ulong)size.X * (ulong)size.Y * 4);
            _targetSize = size;
            Rid rid = _target;
            _bound = "capture";
            Callable.From(() => _texture.TextureRdRid = rid).CallDeferred();
        }

        /// <summary>The capture's own map (<see cref="Own"/>), made on first use.</summary>
        private void EnsureOwn(Vector2I size)
        {
            if (_own.IsValid && size == _ownSize) return;
            Refii.Core.Engine_RdRetire.Retire(_own);
            _own = _rd!.Texture(new RDTextureFormat
            {
                Width = (uint)size.X,
                Height = (uint)size.Y,
                Format = RenderingDevice.DataFormat.R32Sfloat,
                TextureType = RenderingDevice.TextureType.Type2D,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                    | RenderingDevice.TextureUsageBits.StorageBit
                    | RenderingDevice.TextureUsageBits.CanCopyFromBit,
            }, new RDTextureView());
            Refii.Core.Engine_TracedMemory.Alloc(Refii.Core.Engine_RdRetire.TracedPool, _own.Id, (ulong)size.X * (ulong)size.Y * 4);
            _ownSize = size;
            Rid rid = _own;
            Callable.From(() =>
            {
                _ownTexture.TextureRdRid = rid;
                OwnReady = true;
            }).CallDeferred();
        }

        public override void _Notification(int what)
        {
            if (what != NotificationPredelete)
            {
                return;
            }
            // The textures let go of the maps first: both go through the server's queue, so the
            // release below runs after them.
            _texture.TextureRdRid = default;
            _ownTexture.TextureRdRid = default;
            Capture self = this;
            Refii.Core.Engine_RdRetire.OnRenderThread(() => self.ReleaseDeviceObjects());
        }

        /// <summary>Frees the device objects. Render thread (<see cref="Refii.Core.Engine_RdRetire.OnRenderThread"/>).</summary>
        private void ReleaseDeviceObjects()
        {
            _ready = false;
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (_rd is null || rd is null)
            {
                _rd = null;
                return;
            }
            if (_black.IsValid) rd.FreeRid(_black);
            if (_white.IsValid) rd.FreeRid(_white);
            if (_target.IsValid)
            {
                Refii.Core.Engine_TracedMemory.Free(Refii.Core.Engine_RdRetire.TracedPool, _target.Id);
                rd.FreeRid(_target);
            }
            if (_own.IsValid)
            {
                Refii.Core.Engine_TracedMemory.Free(Refii.Core.Engine_RdRetire.TracedPool, _own.Id);
                rd.FreeRid(_own);
            }
            _own = default;
            Source = default;
            if (_sampler.IsValid) rd.FreeRid(_sampler);
            if (_pipeline.IsValid) rd.FreeRid(_pipeline);
            if (_shader.IsValid) rd.FreeRid(_shader);
            _black = default; _white = default;
            _target = default; _sampler = default; _pipeline = default; _shader = default;
            _rd = null;
        }

        public override void _RenderCallback(int effectCallbackType, RenderData renderData)
        {
            if (_rd is null) return;
            Refii.Core.Engine_RdRetire.Pump(_rd);
            if (!_ready)
            {
                // Without the capture the map must read as empty (no caster), not as the
                // placeholder's white, which would shadow everything.
                Bind("black");
                return;
            }
            if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD buffers) return;
            Vector2I size = buffers.GetInternalSize();
            if (size.X <= 0 || size.Y <= 0) return;
            EnsureTarget(size);
            if (!_reported)
            {
                _reported = true;
                GD.Print($"[classic] cascade capture live at {size.X}x{size.Y}, bias {BiasConstant}/{BiasSlope}");
            }
            if (MapMode != "capture")
            {
                Bind(MapMode);
                return;
            }
            Bind("capture");
            Rid output = _target;
            if (Own)
            {
                EnsureOwn(size);
                output = _own;
            }
            Source = output;
            SourceSize = size;
            Rid depth = buffers.GetDepthLayer(0);
            Rid set = _set.For(_rd, stackalloc Rid[] { depth, output });
            if (!set.IsValid)
            {
                var depthUniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 0 };
                depthUniform.AddId(_sampler);
                depthUniform.AddId(depth);
                var outUniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 1 };
                outUniform.AddId(output);
                set = _set.Hold(Engine_RdCalls.CachedUniformSet(_shader, 0, depthUniform, outUniform));
            }
            byte[] push = _push;
            BitConverter.TryWriteBytes(push.AsSpan(0), size.X);
            BitConverter.TryWriteBytes(push.AsSpan(4), size.Y);
            BitConverter.TryWriteBytes(push.AsSpan(8), 0);
            BitConverter.TryWriteBytes(push.AsSpan(12), BiasConstant);
            BitConverter.TryWriteBytes(push.AsSpan(16), BiasSlope);
            long list = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(list, _pipeline);
            _rd.ComputeListBindUniformSet(list, set, 0);
            _rd.ComputeListSetPushConstant(list, push, (uint)push.Length);
            _rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
            _rd.ComputeListEnd();
            if (ArrayTarget.IsValid && ArrayLayer >= 0)
            {
                _rd.TextureCopy(output, ArrayTarget, Vector3.Zero, Vector3.Zero, new Vector3(size.X, size.Y, 1), 0, 0, 0, (uint)ArrayLayer);
            }
        }
    }
}
