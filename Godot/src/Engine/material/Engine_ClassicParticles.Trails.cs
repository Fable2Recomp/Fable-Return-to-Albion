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
        public int Vertices;
        public ImageTexture? Stream;
        public int StreamWidth;
        public long Seen;
        /// <summary>Drawn into the displacement target (IsDisplacementTexture).</summary>
        public bool Displaces;
    }

    private readonly float[] _trailBuffer = new float[1 << 19];
    private readonly Dictionary<uint, Trail> _trails = new();
    private readonly List<uint> _trailsGone = new();
    private readonly Vector4[] _trailColours = new Vector4[20];
    private readonly Vector4[] _trailSelfIllum = new Vector4[20];
    private readonly Vector4[] _trailWorld = new Vector4[3];
    private readonly Vector4[] _trailUvScales = new Vector4[4];
    private byte[] _streamBytes = Array.Empty<byte>();
    private bool _reportedStaleTrails;

    /// <summary>Trails drawn at the last pump.</summary>
    public int TrailCount => _trails.Count;

    private void PumpTrails(Camera3D camera, Vector2 viewport)
    {
        uint used = Refii.Host.HostBridge.GetClassicTrails(_trailBuffer);
        int groups = used == 0 ? 0 : (int)_trailBuffer[0];
        int at = 2;
        for (int g = 0; g < groups && at + TrailHeader <= used; g++)
        {
            int points = (int)_trailBuffer[at + 3] + 3;
            if (at + TrailHeader + points * TrailPoint > used)
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
            if (ComposeTrail(trail, at))
            {
                if (trail.Displaces)
                {
                    ++Engine_ClassicDisplacement.Drawn;
                }
                StripMesh(trail, (int)_trailBuffer[at + 5]);
                StreamTexture(trail, at + TrailHeader, points);
                TrailConstants(trail, at, camera, viewport);
            }
            at += TrailHeader + points * TrailPoint;
        }

        _trailsGone.Clear();
        foreach (KeyValuePair<uint, Trail> pair in _trails)
        {
            if (pair.Value.Seen != _frames)
            {
                pair.Value.Node.QueueFree();
                _trailsGone.Add(pair.Key);
            }
        }
        foreach (uint address in _trailsGone)
        {
            _trails.Remove(address);
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
        trail.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.TriangleStrip, arrays);
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
        if (_streamBytes.Length < floats * 4)
        {
            _streamBytes = new byte[floats * 4];
        }
        Span<float> texels = MemoryMarshal.Cast<byte, float>(_streamBytes.AsSpan(0, floats * 4));
        for (int p = 0; p < points; p++)
        {
            int src = at + p * TrailPoint;
            for (int lane = 0; lane < 4; lane++)
            {
                texels[p * 4 + lane] = _trailBuffer[src + lane];
                texels[(points + p) * 4 + lane] = _trailBuffer[src + 4 + lane];
            }
        }
        Image image = Image.CreateFromData(points, 2, false, Image.Format.Rgbaf, _streamBytes.AsSpan(0, floats * 4).ToArray());
        if (trail.Stream is null || trail.StreamWidth != points)
        {
            trail.Stream = ImageTexture.CreateFromImage(image);
            trail.StreamWidth = points;
            trail.Material?.SetShaderParameter("xr_vertex_stream", trail.Stream);
        }
        else
        {
            trail.Stream.Update(image);
        }
    }

    private bool ComposeTrail(Trail trail, int at)
    {
        bool doubled = _trailBuffer[at + 1] == 3f;
        int textures = Math.Clamp((int)_trailBuffer[at + 48], 0, 4);
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
        string blend = BlendMode(src, dst, op, out bool oneOne);
        string key = $"{doubled}|{textures}|{soft}|{anim}|{blend}|{oneOne}|{alphaRef16}|{displaces}";
        if (key == trail.Key)
        {
            return trail.Material is not null;
        }
        trail.Key = key;

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
        Shader? shader = Engine_ClassicShaders.Compose(vertexEntry, pixelEntry, renderMode, 0, defines);
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
        if (trail.Stream is not null)
        {
            material.SetShaderParameter("xr_vertex_stream", trail.Stream);
        }
        trail.Material = material;
        if (trail.Mesh.GetSurfaceCount() > 0)
        {
            trail.Mesh.SurfaceSetMaterial(0, material);
        }
        return true;
    }

    private void TrailConstants(Trail trail, int at, Camera3D camera, Vector2 viewport)
    {
        ShaderMaterial? m = trail.Material;
        if (m is null)
        {
            return;
        }
        bool doubled = _trailBuffer[at + 1] == 3f;

        float f0 = _trailBuffer[at + 12], f1 = _trailBuffer[at + 13], f2 = _trailBuffer[at + 14],
            f3 = _trailBuffer[at + 15], f4 = _trailBuffer[at + 16];
        m.SetShaderParameter("vs_g_FadeInDistanceInfo", new Vector4(f0, f1, f1 > f0 ? 1f / (f1 - f0) : 0f, 0f));
        m.SetShaderParameter("vs_g_FadeOutDistanceInfo", new Vector4(f3, f3 > f2 ? 1f / (f3 - f2) : 1f, f4, 0f));
        Vector3 ambient = Engine_ClassicFeed.GlobalAmbient;
        if (_trailBuffer[at + 54] == 0f)
        {
            ambient += new Vector3(_trailBuffer[at + 51], _trailBuffer[at + 52], _trailBuffer[at + 53]);
        }
        m.SetShaderParameter("vs_g_GlobalAmbient", new Vector4(ambient.X, ambient.Y, ambient.Z, 1f));

        // c337 / c340 from the stride-28 keyframes (K+0..12 colour, K+16 the double trail's
        // scale, K+20 self-illum, K+24 start time), 19 at most into the shader's 20.
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
            else
            {
                _trailColours[k] = Vector4.Zero;
                _trailSelfIllum[k] = Vector4.Zero;
            }
        }
        m.SetShaderParameter("vs_g_KeyFrameColors", _trailColours);
        m.SetShaderParameter("vs_g_KeyFrameSelfIllumFactorStartTimesScale", _trailSelfIllum);
        m.SetShaderParameter("vs_g_ParticleTrailProperties",
            new Vector4(_trailBuffer[at + 8], _trailBuffer[at + 9], _trailBuffer[at + 10], 0f));

        // g_MatWorld: the columns of the row-major transform R+320, as for the quads.
        for (int i = 0; i < 3; i++)
        {
            _trailWorld[i] = new Vector4(_trailBuffer[at + 17 + i], _trailBuffer[at + 21 + i],
                _trailBuffer[at + 25 + i], _trailBuffer[at + 29 + i]);
        }
        m.SetShaderParameter("vs_g_MatWorld", _trailWorld);

        if (!doubled)
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
            m.SetShaderParameter("vs_g_CameraZVector", new Vector4(axis.X, axis.Y, axis.Z, 0f));
        }

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
                invDeltas[t] = start != previous ? 1f / (start - previous) : 0f;
            }
            if (Math.Max(x, y) > 1f)
            {
                _trailUvScales[t] = new Vector4(x > 0f ? 1f / x : 0f, y > 0f ? 1f / y : 0f, 0f, 0f);
            }
        }
        m.SetShaderParameter("ps_g_TextureStartTimes", starts);
        m.SetShaderParameter("ps_g_TextureStartTimesInvDelta", invDeltas);
        m.SetShaderParameter("ps_g_TextureNumXDivs", divisions);
        m.SetShaderParameter("ps_g_TextureUVScales", _trailUvScales);
        float softDistance = _trailBuffer[at + 45];
        if (softDistance > 0f)
        {
            float depthFade = 1f / softDistance;
            m.SetShaderParameter("ps_g_DepthFadeScale", new Vector4(depthFade, depthFade, depthFade, depthFade));
            m.SetShaderParameter("ps_g_InverseViewProjection", _inverseViewProjection);
        }
    }
}
