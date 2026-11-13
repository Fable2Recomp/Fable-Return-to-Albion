using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Refii.Models;

/// <summary>
/// The title's particle TRAILS and DOUBLE TRAILS on its own TRAILS / DOUBLE_TRAILS vertex shaders
/// and the PARTICLEMATERIAL pixel shaders, from the render twins' stated rings
/// (RefiiHostGetClassicTrails).
/// </summary>
/// <remarks>
/// The guest draws a trail as a TRIANGLESTRIP of 2((n-1)s+1) vertices whose vertex shader places
/// each one from its index: a Catmull-Rom over four of the c + 3 control points the CPU unrolls
/// from the ring (`sub_821B25F8` / `0x82A59DD0`, the stream from `sub_82179C00` / `sub_82A592F0`).
/// Here the strip is a mesh of that many vertices carrying nothing but their index, and the
/// control points are a texture the translated shader fetches by index (`xr_stream_fetch`,
/// XenosRecomp's trail fetch): column = point, row 0 = POSITION0, row 1 = POSITION1. Constants as
/// `sub_821AB600` / `sub_82A59958` set them: c337 keyframe colours, c340 (self-illum, start time,
/// 1 or the double trail's scale), c366 (s, 1/((n-1)s), 1/s, 0), c299 the camera's z or the trail's
/// own fixed axis (the quaternion at D+48 applied to y).
/// </remarks>
public partial class Engine_ClassicParticles
{
    private const int TrailHeader = Refii.Host.HostBridge.ClassicTrailHeader;
    private const int TrailPoint = 8;

    private sealed class Trail
    {
        public required MeshInstance3D Node;
        public required ArrayMesh Mesh;
        public ShaderMaterial? Material;
        public string Key = string.Empty;
        public long Shape = -1;
        public ulong TextureSignature;
        public int Vertices;
        public ImageTexture? Stream;
        /// <summary>The control-point image, refilled in place; and its bytes.</summary>
        public Image? StreamImage;
        public byte[] StreamBytes = Array.Empty<byte>();
        /// <summary>The material the held array parameters were last sent to.</summary>
        public ShaderMaterial? SentTo;
        public readonly Refii.Core.Engine_ArrayParam Colours = new(), SelfIllum = new(), World = new(), UvScales = new();
        public int InverseVpSent = -1;
        public int StreamWidth;
        public long Seen;
        /// <summary>Drawn into the displacement target (IsDisplacementTexture).</summary>
        public bool Displaces;
        /// <summary>True when single-trail camera-aligned billboarding requires vs_g_CameraZVector on camera rotation.</summary>
        public bool CameraAligned;
        /// <summary>The <see cref="CameraAligned"/> its material's xr_camera_axis holds; null for a new material.</summary>
        public bool? CameraAxisSent;
        /// <summary>True when depth-fade (soft trails) requires ps_g_InverseViewProjection on camera motion.</summary>
        public bool Soft;
        public float[] Snapshot = Array.Empty<float>();
        public int SnapshotLength;
        public uint Owner;
        public readonly float[] RawWorld = new float[16];
        public readonly Refii.Core.Engine_TickTimeline Timeline = new(stillWhenUnchanged: false);
        public Transform3D RawEntityWorld = Transform3D.Identity;
        public Vector3 WorldScale = Vector3.One;
        public bool RawIdentityBasis = true;
        public bool ExactIdentity4x4 = true;
        public bool HasWorldSample;
        public bool BoneAttached;
        public bool? DisableDistanceFadeSent;
    }

    private readonly float[] _trailBuffer = new float[1 << 19];
    private readonly Dictionary<uint, Trail> _trails = new();
    private readonly HashSet<uint> _movingTrails = new();
    private readonly List<uint> _settledTrails = new();
    private readonly List<uint> _trailsGone = new();
    private readonly Vector4[] _trailColours = new Vector4[20];
    private readonly Vector4[] _trailSelfIllum = new Vector4[20];
    private readonly Vector4[] _trailWorld = new Vector4[3];
    private readonly Vector4[] _trailUvScales = new Vector4[4];
    private bool _reportedStaleTrails;
    private uint _lastTrailUsed;
    private Vector3 _lastTrailCameraZ;

    /// <summary>Trails drawn at the last pump.</summary>
    public int TrailCount => _trails.Count;

    private void PumpTrails(Camera3D camera, Vector2 viewport, bool guestStepped, bool inverseVpChanged)
    {
        Vector3 cameraZ = camera.GlobalTransform.Basis.Z;
        bool cameraZChanged = cameraZ != _lastTrailCameraZ;
        _lastTrailCameraZ = cameraZ;

        if (!guestStepped)
        {
            AdvanceMovingTrails();
            if (!cameraZChanged && !inverseVpChanged)
            {
                return;
            }
            Vector3 guestAxis = ToGuest(cameraZ);
            var axisVec = new Vector4(guestAxis.X, guestAxis.Y, guestAxis.Z, 0f);
            foreach (Trail trail in _trails.Values)
            {
                ShaderMaterial? m = trail.Material;
                if (m is null)
                {
                    continue;
                }
                if (cameraZChanged && trail.CameraAligned)
                {
                    m.SetShaderParameter(TrailCameraZVectorName, axisVec);
                }
                if (inverseVpChanged && trail.Soft && trail.InverseVpSent != _inverseVp.Version)
                {
                    trail.InverseVpSent = _inverseVp.Version;
                    m.SetShaderParameter(PsInverseViewProjectionName, _inverseVp.Value);
                }
            }
            return;
        }

        uint used = _lastTrailUsed = Refii.Host.HostBridge.GetClassicTrails(_trailBuffer);
        int groups = used == 0 ? 0 : (int)_trailBuffer[0];
        int at = 2;
        for (int g = 0; g < groups && at + TrailHeader <= used; g++)
        {
            int points = (int)_trailBuffer[at + 3] + 3;
            int length = TrailHeader + points * TrailPoint;
            if (at + length > used)
            {
                break;
            }
            uint address = BitConverter.SingleToUInt32Bits(_trailBuffer[at]);
            if (!_trails.TryGetValue(address, out Trail? trail))
            {
                trail = CreateTrail(address);
                _trails[address] = trail;
            }
            trail.Seen = _frames;
            bool changed = trail.SnapshotLength != length || !Same(trail.Snapshot, _trailBuffer, at, length);
            if (changed)
            {
                if (trail.Snapshot.Length < length)
                {
                    trail.Snapshot = new float[Math.Max(length, trail.Snapshot.Length * 2)];
                }
                Array.Copy(_trailBuffer, at, trail.Snapshot, 0, length);
                trail.SnapshotLength = length;
            }
            if (ComposeTrail(trail, at, changed))
            {
                // The trail's particle-system instance (header [38]): its effect stands down in FableFx
                // only if this trail is a visible material (displacement trails must not suppress visible flame geometry).
                uint owner = BitConverter.SingleToUInt32Bits(_trailBuffer[at + 38]);
                if (!Engine_ClassicMaterial.Active && owner != 0u && !trail.Displaces)
                {
                    DrawnOwners.Add(owner);
                }
                if (trail.Displaces)
                {
                    ++Engine_ClassicDisplacement.Drawn;
                }
                if (changed || trail.Vertices != (int)_trailBuffer[at + 5] || trail.Stream is null)
                {
                    StripMesh(trail, (int)_trailBuffer[at + 5]);
                    StreamTexture(trail, at + TrailHeader, points);
                }
                TrailConstants(address, trail, owner, at, camera, viewport, changed);
            }
            at += length;
        }

        _trailsGone.Clear();
        foreach (KeyValuePair<uint, Trail> pair in _trails)
        {
            if (pair.Value.Seen != _frames)
            {
                pair.Value.Node.QueueFree();
                pair.Value.Colours.Dispose();
                pair.Value.SelfIllum.Dispose();
                pair.Value.World.Dispose();
                pair.Value.UvScales.Dispose();
                _trailsGone.Add(pair.Key);
            }
        }
        foreach (uint address in _trailsGone)
        {
            _trails.Remove(address);
            _movingTrails.Remove(address);
        }
    }

    private Trail CreateTrail(uint address)
    {
        var mesh = new ArrayMesh();
        var node = new MeshInstance3D
        {
            Name = $"Trail_{address:X8}",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = Layer,
            // The vertex shader places every vertex from the control points; no box of the
            // mesh's own (all zeros) says where that is.
            ExtraCullMargin = 16384f,
        };
        AddChild(node);
        return new Trail { Node = node, Mesh = mesh };
    }

    /// <summary>The strip: <paramref name="vertices"/> vertices with nothing but their index.</summary>
    private static void StripMesh(Trail trail, int vertices)
    {
        if (vertices == trail.Vertices || vertices < 4)
        {
            return;
        }
        trail.Vertices = vertices;
        trail.Mesh.ClearSurfaces();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[vertices];
        Refii.Core.Engine_ArrayMeshes.AddSurface(trail.Mesh, Mesh.PrimitiveType.TriangleStrip, arrays);
        arrays.Dispose();
        if (trail.Material is not null)
        {
            trail.Mesh.SurfaceSetMaterial(0, trail.Material);
        }
    }

    /// <summary>The control points as the shader fetches them: column = point, rows = POSITION0 / 1.</summary>
    private void StreamTexture(Trail trail, int at, int points)
    {
        int floats = points * 4 * 2;
        if (trail.StreamBytes.Length != floats * 4)
        {
            trail.StreamBytes = new byte[floats * 4];
        }
        Span<float> texels = MemoryMarshal.Cast<byte, float>(trail.StreamBytes.AsSpan());
        for (int p = 0; p < points; p++)
        {
            int src = at + p * TrailPoint;
            for (int lane = 0; lane < 4; lane++)
            {
                texels[p * 4 + lane] = _trailBuffer[src + lane];
                texels[(points + p) * 4 + lane] = _trailBuffer[src + 4 + lane];
            }
        }
        if (trail.StreamImage is null)
        {
            trail.StreamImage = Image.CreateFromData(points, 2, false, Image.Format.Rgbaf, trail.StreamBytes);
        }
        else
        {
            trail.StreamImage.SetData(points, 2, false, Image.Format.Rgbaf, trail.StreamBytes);
        }
        if (trail.Stream is null || trail.StreamWidth != points)
        {
            trail.Stream = ImageTexture.CreateFromImage(trail.StreamImage);
            trail.StreamWidth = points;
            trail.Material?.SetShaderParameter(VertexStreamName, trail.Stream);
        }
        else
        {
            trail.Stream.Update(trail.StreamImage);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong TrailTextureSignature(float[] buffer, int at, int textures)
    {
        ulong hash = 14695981039346656037UL;
        uint material = BitConverter.SingleToUInt32Bits(buffer[at + 7]);
        hash = (hash ^ material) * 1099511628211UL;
        for (int t = 0; t < textures; t++)
        {
            int o = at + 208 + t * 20;
            uint key = BitConverter.SingleToUInt32Bits(buffer[o]);
            hash = (hash ^ key) * 1099511628211UL;
            for (int w = 0; w < 16; w++)
            {
                uint word = BitConverter.SingleToUInt32Bits(buffer[o + 4 + w]);
                if (word == 0u) break;
                hash = (hash ^ word) * 1099511628211UL;
            }
        }
        return hash;
    }

    private bool ComposeTrail(Trail trail, int at, bool changed)
    {
        if (!changed && trail.Material is not null)
        {
            return true;
        }
        bool doubled = _trailBuffer[at + 1] == 3f;
        int textures = Math.Clamp((int)_trailBuffer[at + 48], 0, 4);
        ulong texSig = TrailTextureSignature(_trailBuffer, at, textures);
        bool sameTex = texSig == trail.TextureSignature;
        trail.TextureSignature = texSig;
        bool soft = _trailBuffer[at + 45] > 0f;
        bool anim = false;
        for (int t = 0; t < textures; t++)
        {
            int o = at + 208 + t * 20;
            if (Math.Max(_trailBuffer[o + 2], _trailBuffer[o + 3]) > 1f)
            {
                anim = true;
            }
        }
        int src = (int)_trailBuffer[at + 42], dst = (int)_trailBuffer[at + 43], op = (int)_trailBuffer[at + 44];
        bool alphaRef16 = _trailBuffer[at + 47] != 0f;
        // IsDisplacementTexture: the strip draws into the displacement target (header [55]).
        bool displaces = _trailBuffer[at + 55] != 0f;
        long shape = ShapeOf(textures, soft, anim, alphaRef16, doubled, displaces, src, dst, op);
        if (shape == trail.Shape && sameTex)
        {
            return trail.Material is not null;
        }
        trail.Shape = shape;
        string blend = BlendMode(src, dst, op, out bool oneOne);
        string key = $"{doubled}|{textures}|{soft}|{anim}|{blend}|{oneOne}|{alphaRef16}|{displaces}";
        if (key == trail.Key && sameTex)
        {
            return trail.Material is not null;
        }
        trail.Key = key;
        if (!Engine_ClassicMaterial.Active)
        {
            // The modern renderer draws the guest's displacement through its own target, not these.
            trail.Displaces = false;
            if (displaces)
            {
                trail.Material = null;
                return false;
            }
            ShaderMaterial modern = ModernMaterial(
                ModernShader(ModernTrailInclude, (doubled ? "#define FX_TRAIL_DOUBLE\n" : string.Empty) + HardDefines(soft), blend),
                _trailBuffer, at + 208, textures, oneOne, alphaRef16);
            return UseTrailMaterial(trail, modern);
        }

        var tokens = new List<string>();
        if (anim)
        {
            tokens.Add("ANIM1");
        }
        if (textures > 1)
        {
            tokens.Add("BLEND" + (textures - 1));
        }
        if (soft)
        {
            tokens.Add("DEPTH1");
        }
        string vertexEntry = doubled ? "DOUBLE_TRAILS_VERTEX_SHADER" : "TRAILS_VERTEX_SHADER";
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", Family, tokens);
        if (pixelEntry.Length == 0 || !Engine_ClassicShaders.HasEntry(vertexEntry))
        {
            trail.Material = null;
            return false;
        }
        string renderMode = "unshaded, cull_disabled, depth_draw_never, fog_disabled, " + blend;
        string defines = oneOne ? "#define XR_ALPHA_OUT(a) 0.0" : string.Empty;
        if (displaces)
        {
            // `Render_EffectStripDraw` / `Render_EffectStripDraw2`: VS 0041 / 0043 with PS 0037, the
            // same constants and texture, inside the displacement renderer's ONE / ONE.
            Engine_ClassicDisplacement.EnsureRegistered();
            vertexEntry = doubled ? "DOUBLE_TRAILS_DISPLACEMENT_VERTEX_SHADER" : "TRAILS_DISPLACEMENT_VERTEX_SHADER";
            pixelEntry = Engine_ClassicShaders.HasEntry(DisplacementPixel) && Engine_ClassicShaders.HasEntry(vertexEntry)
                ? DisplacementPixel : string.Empty;
            renderMode = "unshaded, cull_disabled, depth_draw_never, depth_test_disabled, fog_disabled, blend_premul_alpha";
            defines = DisplacementDefines;
        }
        if (pixelEntry.Length == 0)
        {
            trail.Material = null;
            return false;
        }
        trail.Node.Layers = displaces ? Engine_ClassicDisplacement.Layer : Layer;
        trail.Displaces = displaces;
        Shader? shader = Engine_ClassicShaders.Compose(vertexEntry, pixelEntry, renderMode, 0,
            (defines.Length != 0 ? defines + "\n" : string.Empty) + TrailViewDefines);
        // A translation from before the trail fetch read the current vertex for every control
        // point and would draw the strip collapsed: until the shaders are rebuilt, no trail.
        if (shader is null || !shader.Code.Contains("xr_vertex_stream", StringComparison.Ordinal))
        {
            if (shader is not null && !_reportedStaleTrails)
            {
                _reportedStaleTrails = true;
                GD.Print("[classic] the trail shaders predate the indexed fetch; trails stay off until the shaders are compiled again");
            }
            trail.Material = null;
            return false;
        }
        var material = new ShaderMaterial { Shader = shader, RenderPriority = oneOne ? 10 : 0 };
        material.SetShaderParameter("xr_spec_constants", 2u);
        material.SetShaderParameter("xr_alpha_threshold", (alphaRef16 ? 16f : 1f) / 255f);
        for (int t = 0; t < textures; t++)
        {
            Texture2D? texture = Texture(_trailBuffer, at + 208 + t * 20);
            if (texture is not null)
            {
                material.SetShaderParameter(t == 0 ? "ps_g_ParticleTextureSamplers" : "ps_s" + t, texture);
            }
        }
        return UseTrailMaterial(trail, material);
    }

    /// <summary>
    /// The trail stages' material constants that belong to the view: a soft trail's
    /// g_InverseViewProjection, and g_CameraZVector when it is the camera's z (a fixed trail's
    /// axis is its own: xr_camera_axis gates it).
    /// </summary>
    private static string TrailViewDefines =>
        Engine_ClassicShaders.ViewParam("ps_g_InverseViewProjection", true, Engine_ClassicRegisters.Ps.HostInverseViewProjection) + "\n" +
        "uniform bool xr_camera_axis = false;\n" +
        Engine_ClassicShaders.ViewParam("vs_g_CameraZVector", false, Engine_ClassicRegisters.Vs.HostCameraZ, "xr_camera_axis");

    private static bool UseTrailMaterial(Trail trail, ShaderMaterial material)
    {
        trail.CameraAxisSent = null;
        if (trail.Stream is not null)
        {
            material.SetShaderParameter(VertexStreamName, trail.Stream);
        }
        trail.Material = material;
        if (trail.Mesh.GetSurfaceCount() > 0)
        {
            trail.Mesh.SurfaceSetMaterial(0, material);
        }
        return true;
    }

    private void TrailConstants(uint address, Trail trail, uint owner, int at, Camera3D camera, Vector2 viewport, bool changed)
    {
        ShaderMaterial? m = trail.Material;
        if (m is null)
        {
            return;
        }
        bool doubled = _trailBuffer[at + 1] == 3f;
        bool fresh = !ReferenceEquals(trail.SentTo, m);
        trail.SentTo = m;
        bool disableDistanceFade = Refii.AndroidRuntime.AndroidLeanModern.ParticleFadeOff(Engine_ClassicMaterial.Active, Refii.Lighting.Engine_LightingFrame.ModernModel);
        bool fadeModeChanged = trail.DisableDistanceFadeSent != disableDistanceFade;

        if (changed || fresh || fadeModeChanged)
        {
            trail.DisableDistanceFadeSent = disableDistanceFade;
            float f0 = _trailBuffer[at + 12], f1 = _trailBuffer[at + 13], f2 = _trailBuffer[at + 14],
                f3 = _trailBuffer[at + 15], f4 = _trailBuffer[at + 16];
            m.SetShaderParameter(TrailFadeInDistanceInfoName, new Vector4(f0, f1, f1 > f0 ? 1f / (f1 - f0) : 0f, 0f));
            m.SetShaderParameter(TrailFadeOutDistanceInfoName,
                Refii.Fx.Engine_FxSystem.BuildGuestFadeOutInfo(f2, f3, f4, disableDistanceFade));
            // The modern shader adds the scene's ambient itself; it takes the material's own here.
            Vector3 ambient = Engine_ClassicMaterial.Active ? Engine_ClassicFeed.GlobalAmbient : Vector3.Zero;
            if (_trailBuffer[at + 54] == 0f)
            {
                ambient += new Vector3(_trailBuffer[at + 51], _trailBuffer[at + 52], _trailBuffer[at + 53]);
            }
            m.SetShaderParameter(TrailGlobalAmbientName, new Vector4(ambient.X, ambient.Y, ambient.Z, 1f));

            // c337 / c340 from the stride-28 keyframes (K+0..12 colour, K+16 the double trail's
            // scale, K+20 self-illum, K+24 start time), 19 at most into the shader's 20, padded
            // with the last key (`sub_821CA6C8`).
            int keys = Math.Clamp((int)_trailBuffer[at + 50], 0, 19);
            for (int k = 0; k < _trailColours.Length; k++)
            {
                if (k < keys)
                {
                    int o = at + 56 + k * 8;
                    _trailColours[k] = new Vector4(_trailBuffer[o], _trailBuffer[o + 1], _trailBuffer[o + 2], _trailBuffer[o + 3]);
                    _trailSelfIllum[k] = new Vector4(_trailBuffer[o + 5], _trailBuffer[o + 6],
                        doubled ? _trailBuffer[o + 4] : 1f, 0f);
                }
                else if (keys > 0)
                {
                    _trailColours[k] = _trailColours[keys - 1];
                    _trailSelfIllum[k] = _trailSelfIllum[keys - 1];
                }
                else
                {
                    _trailColours[k] = Vector4.Zero;
                    _trailSelfIllum[k] = Vector4.Zero;
                }
            }
            if (trail.Colours.Take(_trailColours) || fresh)
            {
                m.SetShaderParameter(VsKeyFrameColorsName, trail.Colours.Value);
            }
            if (trail.SelfIllum.Take(_trailSelfIllum) || fresh)
            {
                m.SetShaderParameter(TrailSelfIllumName, trail.SelfIllum.Value);
            }
            m.SetShaderParameter(TrailParticleTrailPropertiesName,
                new Vector4(_trailBuffer[at + 8], _trailBuffer[at + 9], _trailBuffer[at + 10], 0f));

            // The pixel side, as for the quads.
            int textures = Math.Clamp((int)_trailBuffer[at + 48], 0, 4);
            var starts = Vector4.Zero;
            var invDeltas = Vector4.Zero;
            var divisions = Vector4.Zero;
            for (int t = 0; t < 4; t++)
            {
                _trailUvScales[t] = Vector4.Zero;
                if (t >= textures)
                {
                    continue;
                }
                int o = at + 208 + t * 20;
                float start = _trailBuffer[o + 1];
                float x = _trailBuffer[o + 2], y = _trailBuffer[o + 3];
                starts[t] = start;
                divisions[t] = x;
                if (t > 0)
                {
                    float previous = _trailBuffer[at + 208 + (t - 1) * 20 + 1];
                    // Lane t - 1: the blend stages weigh texture t by (age - start[t - 1]) * invDelta[t - 1]
                    // (`sub_821ABC60` stores it at +96 + 4 (t - 1)); lane t left blending off.
                    invDeltas[t - 1] = start != previous ? 1f / (start - previous) : 0f;
                }
                if (Math.Max(x, y) > 1f)
                {
                    _trailUvScales[t] = new Vector4(x > 0f ? 1f / x : 0f, y > 0f ? 1f / y : 0f, 0f, 0f);
                }
            }
            m.SetShaderParameter(TrailTextureStartTimesName, starts);
            m.SetShaderParameter(TrailTextureStartTimesInvDeltaName, invDeltas);
            m.SetShaderParameter(TrailTextureNumXDivsName, divisions);
            if (trail.UvScales.Take(_trailUvScales) || fresh)
            {
                m.SetShaderParameter(PsTextureUVScalesName, trail.UvScales.Value);
            }
        }

        ObserveTrailWorld(address, trail, owner, at, m, fresh);

        trail.CameraAligned = !doubled && _trailBuffer[at + 33] == 0f;
        if (trail.CameraAxisSent != trail.CameraAligned)
        {
            trail.CameraAxisSent = trail.CameraAligned;
            m.SetShaderParameter(CameraAxisName, trail.CameraAligned);
        }
        if (!doubled && (changed || fresh || _trailBuffer[at + 33] == 0f))
        {
            // c299: the trail's own axis (y turned by its quaternion, `sub_822A1640`) when it is
            // fixed (TM+0x38), else the camera's z, guest axes.
            Vector3 axis;
            if (_trailBuffer[at + 33] != 0f)
            {
                var q = new Quaternion(_trailBuffer[at + 34], _trailBuffer[at + 35], _trailBuffer[at + 36], _trailBuffer[at + 37]);
                axis = q.LengthSquared() > 1e-12f ? q.Normalized() * Vector3.Up : Vector3.Up;
            }
            else
            {
                axis = ToGuest(camera.GlobalTransform.Basis.Z);
            }
            m.SetShaderParameter(TrailCameraZVectorName, new Vector4(axis.X, axis.Y, axis.Z, 0f));
        }

        float softDistance = _trailBuffer[at + 45];
        trail.Soft = softDistance > 0f;
        if (softDistance > 0f)
        {
            if (changed || fresh)
            {
                float depthFade = 1f / softDistance;
                m.SetShaderParameter(TrailDepthFadeScaleName, new Vector4(depthFade, depthFade, depthFade, depthFade));
            }
            if (fresh || trail.InverseVpSent != _inverseVp.Version)
            {
                trail.InverseVpSent = _inverseVp.Version;
                m.SetShaderParameter(PsInverseViewProjectionName, _inverseVp.Value);
            }
        }
    }

    private void ObserveTrailWorld(uint address, Trail trail, uint owner, int at, ShaderMaterial m, bool fresh)
    {
        trail.Owner = owner;
        bool rawChanged = !trail.HasWorldSample;
        for (int i = 0; i < 16; i++)
        {
            float v = _trailBuffer[at + 17 + i];
            if (trail.RawWorld[i] != v)
            {
                rawChanged = true;
                trail.RawWorld[i] = v;
            }
        }

        if (rawChanged)
        {
            ReadOnlySpan<float> m16 = trail.RawWorld;
            trail.ExactIdentity4x4 = Refii.Fx.Engine_FxSystem.IsExactGuestIdentity4x4(m16);
            trail.HasWorldSample = Refii.Fx.Engine_FxSystem.TryDecomposeGuestWorldMatrix(
                m16, out trail.RawEntityWorld, out trail.WorldScale, out trail.RawIdentityBasis);
        }

        uint tick = Refii.Core.Engine_TickClock.GuestGameTick;
        if (trail.HasWorldSample &&
            Refii.Game.GameFx.Current?.TryResolveBoneAttachedWorld(
                owner, trail.RawEntityWorld, trail.RawIdentityBasis, trail.ExactIdentity4x4,
                out Transform3D boneEntity) == true)
        {
            trail.BoneAttached = true;
            trail.Timeline.Reset(tick, boneEntity);
            _movingTrails.Add(address);
            ApplyTrailWorld(trail, m, boneEntity, fresh);
            return;
        }

        if (trail.HasWorldSample && !trail.ExactIdentity4x4)
        {
            if (trail.BoneAttached ||
                trail.Timeline.Empty ||
                trail.Timeline.Newest.Origin.DistanceSquaredTo(trail.RawEntityWorld.Origin) >
                    Refii.Core.Engine_TickTimeline.TeleportMetres * Refii.Core.Engine_TickTimeline.TeleportMetres ||
                (tick > trail.Timeline.NewestTick && tick - trail.Timeline.NewestTick > 8.0))
            {
                trail.BoneAttached = false;
                trail.Timeline.Reset(tick, trail.RawEntityWorld);
            }
            else
            {
                trail.Timeline.Observe(tick, trail.RawEntityWorld);
            }

            Transform3D drawnEntity = trail.Timeline.Settled
                ? trail.RawEntityWorld
                : trail.Timeline.Advance(Refii.Entities.Engine_DrawnFrame.NowUsec, Refii.Entities.Engine_DrawnFrame.DrawTick);

            if (trail.Timeline.Settled)
            {
                _movingTrails.Remove(address);
            }
            else
            {
                _movingTrails.Add(address);
            }

            ApplyTrailWorld(trail, m, drawnEntity, fresh || rawChanged);
            return;
        }

        trail.BoneAttached = false;
        _movingTrails.Remove(address);
        if (fresh || rawChanged)
        {
            for (int i = 0; i < 3; i++)
            {
                _trailWorld[i] = new Vector4(trail.RawWorld[i], trail.RawWorld[4 + i],
                    trail.RawWorld[8 + i], trail.RawWorld[12 + i]);
            }
            if (trail.World.Take(_trailWorld) || fresh)
            {
                m.SetShaderParameter(VsMatWorldName, trail.World.Value);
            }
        }
    }

    private void AdvanceMovingTrails()
    {
        if (_movingTrails.Count == 0)
        {
            return;
        }

        _settledTrails.Clear();
        ulong nowUsec = Refii.Entities.Engine_DrawnFrame.NowUsec;
        double drawTick = Refii.Entities.Engine_DrawnFrame.DrawTick;
        foreach (uint address in _movingTrails)
        {
            if (!_trails.TryGetValue(address, out Trail? trail) ||
                trail.Material is not { } m ||
                !GodotObject.IsInstanceValid(trail.Node))
            {
                _settledTrails.Add(address);
                continue;
            }

            if (trail.BoneAttached)
            {
                if (Refii.Game.GameFx.Current?.TryResolveBoneAttachedWorld(
                        trail.Owner, trail.RawEntityWorld, trail.RawIdentityBasis, trail.ExactIdentity4x4,
                        out Transform3D boneEntity) == true)
                {
                    ApplyTrailWorld(trail, m, boneEntity, fresh: false);
                }
                continue;
            }

            if (trail.Timeline.Settled)
            {
                _settledTrails.Add(address);
                continue;
            }

            Transform3D drawnEntity = trail.Timeline.Advance(nowUsec, drawTick);
            ApplyTrailWorld(trail, m, drawnEntity, fresh: false);
            if (trail.Timeline.Settled)
            {
                _settledTrails.Add(address);
            }
        }

        foreach (uint address in _settledTrails)
        {
            _movingTrails.Remove(address);
        }
    }

    private void ApplyTrailWorld(Trail trail, ShaderMaterial m, Transform3D drawnEntity, bool fresh)
    {
        Refii.Fx.Engine_FxSystem.ComposeGuestMatWorldColumns(drawnEntity, trail.WorldScale, _trailWorld);
        if (trail.World.Take(_trailWorld) || fresh)
        {
            m.SetShaderParameter(VsMatWorldName, trail.World.Value);
        }
    }

    private static readonly StringName VertexStreamName = "xr_vertex_stream";
    private static readonly StringName TrailFadeInDistanceInfoName = "vs_g_FadeInDistanceInfo";
    private static readonly StringName TrailFadeOutDistanceInfoName = "vs_g_FadeOutDistanceInfo";
    private static readonly StringName TrailGlobalAmbientName = "vs_g_GlobalAmbient";
    private static readonly StringName TrailSelfIllumName = "vs_g_KeyFrameSelfIllumFactorStartTimesScale";
    private static readonly StringName TrailParticleTrailPropertiesName = "vs_g_ParticleTrailProperties";
    private static readonly StringName TrailCameraZVectorName = "vs_g_CameraZVector";
    private static readonly StringName CameraAxisName = "xr_camera_axis";
    private static readonly StringName TrailTextureStartTimesName = "ps_g_TextureStartTimes";
    private static readonly StringName TrailTextureStartTimesInvDeltaName = "ps_g_TextureStartTimesInvDelta";
    private static readonly StringName TrailTextureNumXDivsName = "ps_g_TextureNumXDivs";
    private static readonly StringName TrailDepthFadeScaleName = "ps_g_DepthFadeScale";
}
