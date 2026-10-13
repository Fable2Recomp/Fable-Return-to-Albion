using System;
using System.Collections.Generic;
using Godot;
using Refii.Textures;

namespace Refii.Models;

/// <summary>
/// The classic renderer's particles: the render scene's particle groups as the sim states them
/// to the renderer, drawn with the title's own FACE_ME vertex shader and PARTICLEMATERIAL pixel
/// shaders.
/// </summary>
/// <remarks>
/// THE STATED DATA. The sim hands each group's twin its records, material and bounds
/// (<c>sub_821D8EA8</c>) and its transform (<c>sub_821D9D40</c>); the title's draw
/// (<c>sub_821CA790</c>) binds those 48-byte records as a QUADLIST stream of four vertices a
/// record and lets FACE_ME expand the corners from the vertex index. This node reads the twins
/// (<see cref="Refii.Host.HostBridge.GetClassicParticles"/>) and builds the same stream as a
/// mesh: VERTEX the record's position, CUSTOM0 its TEXCOORD0, CUSTOM1 its TEXCOORD1, four
/// indexed vertices a quad.
///
/// THE DRAW'S CONSTANTS (<c>sub_821CA108</c>): g_MatWorld the group's transform, the camera's
/// position and its right/up axes (or the group's fixed axes, a quaternion), the material's
/// keyframe tables (at most 19), the fades, g_GlobalAmbient (the lighting object's ambient, or
/// white for an unlit group); the pixel shader's texture timings and, for a soft group, the
/// depth fade scale and the inverse view-projection. The pixel variant is
/// ANIM1 | BLEND(textures-1) | DEPTH1 by the material; blend states come from its own record
/// (<c>Render_MapBlendFactorToD3D</c>), alpha test on with reference 16 or 1.
///
/// Light groups come through <see cref="Engine_ClassicLitInstances"/> like any lit draw. Not
/// yet carried: trails (record kinds 2 and 3), displacement groups (the FACE_ME_DISPLACEMENT
/// path), and the title's half-resolution additive layer for ONE/ONE groups, which draw into the
/// scene here.
/// </remarks>
[Tool]
public partial class Engine_ClassicParticles : Node
{
    private const string VertexEntry = "FACE_ME_VERTEX_SHADER";
    private const string Family = "PARTICLEMATERIAL";
    /// <summary>The particles' own render layer (22), which the cascade cameras exclude.</summary>
    public const uint Layer = 1u << 21;
    private const int MaxKeyframes = 20;
    private const int Header = Refii.Host.HostBridge.ClassicParticleHeader;
    private const int Record = Refii.Host.HostBridge.ClassicParticleRecord;

    private sealed class Group
    {
        public MeshInstance3D Node = null!;
        public ArrayMesh Mesh = null!;
        public ShaderMaterial? Material;
        public string Key = string.Empty;
        public int Capacity;
        public Vector3[] Vertices = Array.Empty<Vector3>();
        public float[] Custom0 = Array.Empty<float>();
        public float[] Custom1 = Array.Empty<float>();
        public int[] Indices = Array.Empty<int>();
        public long Seen;
        public bool Attached;
        /// <summary>The header and records as last built, so an unchanged group costs nothing.</summary>
        public float[] Snapshot = Array.Empty<float>();
        public int SnapshotLength;
        public int StaticHash;
        public readonly float[] World = new float[16];
        public ShaderMaterial? Live;
    }

    private static readonly List<ShaderMaterial> _chain = new();

    private readonly Dictionary<uint, Group> _groups = new();
    private readonly List<uint> _gone = new();
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly float[] _buffer = new float[1 << 20];
    private readonly Vector4[] _colours = new Vector4[MaxKeyframes];
    private readonly Vector4[] _scales = new Vector4[MaxKeyframes];
    private readonly Vector4[] _globalScales = new Vector4[MaxKeyframes];
    private readonly Vector4[] _world = new Vector4[3];
    private readonly Vector4[] _uvScales = new Vector4[3];
    private readonly Vector4[] _inverseViewProjection = new Vector4[4];
    private readonly Godot.Collections.Array _arrays = new();
    private long _frames;
    private int _particles;
    private int _skippedDisplacement;
    private bool _reportedMissing;

    /// <summary>The live instance.</summary>
    public static Engine_ClassicParticles? Instance { get; private set; }

    /// <summary>Groups drawn last frame, particles among them, displacement groups skipped.</summary>
    public (int Groups, int Particles, int SkippedDisplacement) Counts => (_groups.Count, _particles, _skippedDisplacement);

    public override void _Ready()
    {
        Instance = this;
        _arrays.Resize((int)Mesh.ArrayType.Max);
    }

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    /// <summary>Reads the groups and rebuilds their meshes and constants. Main thread, once a frame, after the lit instances.</summary>
    public void Pump(Camera3D camera)
    {
        ++_frames;
        _particles = 0;
        _skippedDisplacement = 0;
        if (!Engine_ClassicShaders.HasEntry(VertexEntry))
        {
            if (!_reportedMissing)
            {
                _reportedMissing = true;
                GD.Print("[classic] no FACE_ME vertex shader in the bank; particles stay off");
            }
            return;
        }

        uint used = Refii.Host.HostBridge.GetClassicParticles(_buffer);
        int groups = used == 0 ? 0 : (int)_buffer[0];
        Vector2 viewport = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        InverseViewProjection(camera);

        int at = 2;
        for (int g = 0; g < groups && at + Header <= used; g++)
        {
            int count = (int)_buffer[at + 2];
            if (at + Header + count * Record > used)
            {
                break;
            }
            uint address = BitConverter.SingleToUInt32Bits(_buffer[at]);
            if (_buffer[at + 43] != 0f)
            {
                ++_skippedDisplacement;
                at += Header + count * Record;
                continue;
            }
            if (!_groups.TryGetValue(address, out Group? group))
            {
                group = Create(address);
                _groups[address] = group;
            }
            group.Seen = _frames;
            // The sim states a group at its own rate; a frame that repeats the last records
            // rebuilds nothing.
            int length = Header + count * Record;
            bool changed = group.SnapshotLength != length || !Same(group.Snapshot, _buffer, at, length);
            if (changed)
            {
                if (group.Snapshot.Length < length)
                {
                    group.Snapshot = new float[Math.Max(length, group.Snapshot.Length * 2)];
                }
                Array.Copy(_buffer, at, group.Snapshot, 0, length);
                group.SnapshotLength = length;
                Fill(group, at, count);
            }
            if (Compose(group, at))
            {
                Constants(group, at, viewport);
                _particles += count;
            }
            at += Header + count * Record;
        }

        _gone.Clear();
        foreach (KeyValuePair<uint, Group> pair in _groups)
        {
            if (pair.Value.Seen != _frames)
            {
                pair.Value.Node.QueueFree();
                _gone.Add(pair.Key);
            }
        }
        foreach (uint address in _gone)
        {
            _groups.Remove(address);
        }
    }

    private Group Create(uint address)
    {
        var mesh = new ArrayMesh();
        var node = new MeshInstance3D
        {
            Name = $"Particles_{address:X8}",
            Mesh = mesh,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true,
            // The title's cascade pass draws no particles; the cascade cameras leave this layer out.
            Layers = Layer,
        };
        AddChild(node);
        return new Group { Node = node, Mesh = mesh };
    }

    /// <summary>The stream: four vertices a record, as the title's QUADLIST draws them.</summary>
    private void Fill(Group group, int at, int count)
    {
        if (group.Capacity < count)
        {
            group.Capacity = Math.Max(count, group.Capacity * 2);
            group.Vertices = new Vector3[group.Capacity * 4];
            group.Custom0 = new float[group.Capacity * 16];
            group.Custom1 = new float[group.Capacity * 16];
            group.Indices = new int[group.Capacity * 6];
            for (int q = 0; q < group.Capacity; q++)
            {
                int v = q * 4, i = q * 6;
                group.Indices[i] = v;
                group.Indices[i + 1] = v + 1;
                group.Indices[i + 2] = v + 2;
                group.Indices[i + 3] = v;
                group.Indices[i + 4] = v + 2;
                group.Indices[i + 5] = v + 3;
            }
        }
        int records = at + Header;
        for (int r = 0; r < count; r++)
        {
            int src = records + r * Record;
            var position = new Vector3(_buffer[src], _buffer[src + 1], _buffer[src + 2]);
            for (int c = 0; c < 4; c++)
            {
                int v = r * 4 + c;
                group.Vertices[v] = position;
                int f = v * 4;
                group.Custom0[f] = _buffer[src + 4];
                group.Custom0[f + 1] = _buffer[src + 5];
                group.Custom0[f + 2] = _buffer[src + 6];
                group.Custom0[f + 3] = _buffer[src + 7];
                group.Custom1[f] = _buffer[src + 8];
                group.Custom1[f + 1] = _buffer[src + 9];
                group.Custom1[f + 2] = _buffer[src + 10];
                group.Custom1[f + 3] = _buffer[src + 11];
            }
        }

        // Exact-length views: Godot takes the arrays whole.
        Vector3[] vertices = group.Vertices.Length == count * 4 ? group.Vertices : group.Vertices[..(count * 4)];
        float[] custom0 = group.Custom0.Length == count * 16 ? group.Custom0 : group.Custom0[..(count * 16)];
        float[] custom1 = group.Custom1.Length == count * 16 ? group.Custom1 : group.Custom1[..(count * 16)];
        int[] indices = group.Indices.Length == count * 6 ? group.Indices : group.Indices[..(count * 6)];
        for (int i = 0; i < (int)Mesh.ArrayType.Max; i++)
        {
            _arrays[i] = default;
        }
        _arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        _arrays[(int)Mesh.ArrayType.Custom0] = custom0;
        _arrays[(int)Mesh.ArrayType.Custom1] = custom1;
        _arrays[(int)Mesh.ArrayType.Index] = indices;
        var flags = (Mesh.ArrayFormat)(
            ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift) |
            ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift));
        group.Mesh.ClearSurfaces();
        group.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays, null, null, flags);
        if (group.Material is not null && group.Node.GetSurfaceOverrideMaterial(0) is null)
        {
            group.Node.SetSurfaceOverrideMaterial(0, group.Material);
        }

        // The group's bounds as the sim states them (R+80/R+96), in Godot's axes, with room
        // for the quads the corners extend past their centres.
        Vector3 a = ToGuestBounds(_buffer[at + 4], _buffer[at + 5], _buffer[at + 6]);
        Vector3 b = ToGuestBounds(_buffer[at + 8], _buffer[at + 9], _buffer[at + 10]);
        Vector3 min = a.Min(b) - new Vector3(4f, 4f, 4f);
        Vector3 max = a.Max(b) + new Vector3(4f, 4f, 4f);
        group.Mesh.CustomAabb = new Aabb(min, max - min);
        group.Node.CustomAabb = new Aabb(min, max - min);
    }

    /// <summary>The material for the group's variant, rebuilt when the variant changes.</summary>
    private bool Compose(Group group, int at)
    {
        int textures = Math.Clamp((int)_buffer[at + 38], 0, 3);
        bool soft = _buffer[at + 42] > 0f;
        bool anim = false;
        for (int t = 0; t < textures; t++)
        {
            int o = at + 254 + t * 20;
            if (Math.Max(_buffer[o + 2], _buffer[o + 3]) > 1f)
            {
                anim = true;
            }
        }
        int src = (int)_buffer[at + 39], dst = (int)_buffer[at + 40], op = (int)_buffer[at + 41];
        bool alphaRef16 = _buffer[at + 44] != 0f;
        string blend = BlendMode(src, dst, op, out bool oneOne);
        string key = $"{textures}|{soft}|{anim}|{blend}|{oneOne}|{alphaRef16}";
        if (key == group.Key && group.Material is not null)
        {
            return true;
        }
        group.Key = key;

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
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", Family, tokens);
        if (pixelEntry.Length == 0)
        {
            return false;
        }
        string renderMode = "unshaded, cull_disabled, depth_draw_never, fog_disabled, " + blend;
        string defines = oneOne ? "#define XR_ALPHA_OUT(a) 0.0" : string.Empty;
        Shader? shader = Engine_ClassicShaders.Compose(VertexEntry, pixelEntry, renderMode, 0, defines);
        if (shader is null)
        {
            return false;
        }
        var material = new ShaderMaterial
        {
            Shader = shader,
            // The title draws its ONE/ONE groups after the translucent bucket.
            RenderPriority = oneOne ? 10 : 0,
        };
        Engine_ClassicMaterial.RegisterSinglePass(material, Family, VertexEntry, tokens, renderMode, defines);
        material.SetShaderParameter("xr_spec_constants", 2u);
        material.SetShaderParameter("xr_alpha_threshold", (alphaRef16 ? 16f : 1f) / 255f);
        for (int t = 0; t < textures; t++)
        {
            Texture2D? texture = Texture(at + 254 + t * 20);
            if (texture is not null)
            {
                material.SetShaderParameter(t == 0 ? "ps_g_ParticleTextureSamplers" : "ps_s" + t, texture);
            }
        }
        group.Material = material;
        group.Node.SetSurfaceOverrideMaterial(0, material);
        if (!group.Attached)
        {
            group.Attached = true;
            Engine_ClassicLitInstances.Attach(group.Node);
        }
        return true;
    }

    /// <summary>
    /// The title's blend enums (`Render_MapBlendFactorToD3D`: 0 ZERO, 1 ONE, 2 SRCCOLOR,
    /// 3 INVSRCCOLOR, 4 SRCALPHA, 5 INVSRCALPHA, 6 DESTCOLOR, 7 INVDESTCOLOR, 8 DESTALPHA,
    /// 9 INVDESTALPHA; ops 0 ADD, 1 SUBTRACT, 2 REVSUBTRACT, 3 MIN, 4 MAX) as Godot's modes.
    /// ONE/ONE is premultiplied with the alpha held at zero.
    /// </summary>
    private static string BlendMode(int src, int dst, int op, out bool oneOne)
    {
        oneOne = false;
        if (op == 1 || op == 2)
        {
            return "blend_sub";
        }
        if (src == 1 && dst == 1)
        {
            oneOne = true;
            return "blend_premul_alpha";
        }
        if (src == 1 && dst == 5)
        {
            return "blend_premul_alpha";
        }
        if (src == 4 && dst == 1)
        {
            return "blend_add";
        }
        if ((src == 0 && dst == 2) || (src == 6 && dst == 0) || (src == 6 && dst == 1))
        {
            return "blend_mul";
        }
        return "blend_mix";
    }

    private static bool Same(float[] snapshot, float[] buffer, int at, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (snapshot[i] != buffer[at + i])
            {
                return false;
            }
        }
        return true;
    }

    private int HashRange(int at, int count)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < count; i++)
        {
            hash = (hash ^ BitConverter.SingleToUInt32Bits(_buffer[at + i])) * 16777619u;
        }
        return (int)hash;
    }

    /// <summary>The materials drawing the group now: the live override, its next passes, the source.</summary>
    private static void Chain(Group group)
    {
        _chain.Clear();
        ShaderMaterial? live = group.Node.GetSurfaceOverrideMaterial(0) as ShaderMaterial;
        for (ShaderMaterial? m = live; m is not null; m = m.NextPass as ShaderMaterial)
        {
            _chain.Add(m);
        }
        if (group.Material is not null && !ReferenceEquals(group.Material, live))
        {
            _chain.Add(group.Material);
        }
        group.Live = live;
    }

    /// <summary>
    /// The draw's by-id constants (`sub_821CA108`). The camera's position and axes are frame
    /// registers (the feed writes them); the material's keyframes, fades, timings and ambient
    /// are written when they change; the transform when it moves; a soft group's view inverse
    /// and tile every frame. A fixed-axis group (M+56) takes the camera's axes for now.
    /// </summary>
    private void Constants(Group group, int at, Vector2 viewport)
    {
        ShaderMaterial? liveBefore = group.Live;
        Chain(group);
        bool liveChanged = !ReferenceEquals(liveBefore, group.Live);

        int staticHash = HashRange(at + 12, 9) ^ (HashRange(at + 38, 16) * 3) ^ (HashRange(at + 64, 250) * 7);
        if (staticHash != group.StaticHash || liveChanged)
        {
            group.StaticHash = staticHash;
            float f0 = _buffer[at + 12], f1 = _buffer[at + 13], f2 = _buffer[at + 14], f3 = _buffer[at + 15], f4 = _buffer[at + 16];
            var fadeIn = new Vector4(f0, f1, f1 > f0 ? 1f / (f1 - f0) : 0f, 0f);
            var fadeOut = new Vector4(f3, f3 > f2 ? 1f / (f3 - f2) : 1f, f4, 0f);
            Vector3 ambient = Engine_ClassicFeed.GlobalAmbient;
            if (_buffer[at + 54] == 0f)
            {
                ambient += new Vector3(_buffer[at + 51], _buffer[at + 52], _buffer[at + 53]);
            }
            float scale = _buffer[at + 17];
            int keys = Math.Clamp((int)_buffer[at + 50], 0, 19);
            for (int k = 0; k < MaxKeyframes; k++)
            {
                if (k < keys)
                {
                    int o = at + 64 + k * 10;
                    _colours[k] = new Vector4(_buffer[o], _buffer[o + 1], _buffer[o + 2], _buffer[o + 3]);
                    _scales[k] = new Vector4(_buffer[o + 4], _buffer[o + 5], _buffer[o + 8], _buffer[o + 9]);
                    _globalScales[k] = new Vector4(_buffer[o + 6] * scale, _buffer[o + 7] * scale, 0f, 0f);
                }
                else
                {
                    _colours[k] = Vector4.Zero;
                    _scales[k] = Vector4.Zero;
                    _globalScales[k] = Vector4.Zero;
                }
            }
            int textures = Math.Clamp((int)_buffer[at + 38], 0, 3);
            var starts = Vector4.Zero;
            var invDeltas = Vector4.Zero;
            var divisions = Vector4.Zero;
            for (int t = 0; t < 3; t++)
            {
                _uvScales[t] = Vector4.Zero;
                if (t >= textures)
                {
                    continue;
                }
                int o = at + 254 + t * 20;
                float start = _buffer[o + 1];
                float x = _buffer[o + 2], y = _buffer[o + 3];
                starts[t] = start;
                divisions[t] = x;
                if (t > 0)
                {
                    float previous = _buffer[at + 254 + (t - 1) * 20 + 1];
                    invDeltas[t] = start != previous ? 1f / (start - previous) : 0f;
                }
                if (Math.Max(x, y) > 1f)
                {
                    _uvScales[t] = new Vector4(x > 0f ? 1f / x : 0f, y > 0f ? 1f / y : 0f, 0f, 0f);
                }
            }
            float softDistance = _buffer[at + 42];
            float depthFade = softDistance > 0f ? 1f / softDistance : 0f;
            foreach (ShaderMaterial m in _chain)
            {
                m.SetShaderParameter("vs_g_FadeInDistanceInfo", fadeIn);
                m.SetShaderParameter("vs_g_FadeOutDistanceInfo", fadeOut);
                m.SetShaderParameter("vs_g_GlobalAmbient", new Vector4(ambient.X, ambient.Y, ambient.Z, 1f));
                m.SetShaderParameter("vs_g_KeyFrameColors", _colours);
                m.SetShaderParameter("vs_g_KeyFrameScalesSelfIllumFactorStartTimes", _scales);
                m.SetShaderParameter("vs_g_KeyFrameGlobalScales", _globalScales);
                m.SetShaderParameter("ps_g_TextureStartTimes", starts);
                m.SetShaderParameter("ps_g_TextureStartTimesInvDelta", invDeltas);
                m.SetShaderParameter("ps_g_TextureNumXDivs", divisions);
                m.SetShaderParameter("ps_g_TextureUVScales", _uvScales);
                if (softDistance > 0f)
                {
                    m.SetShaderParameter("ps_g_DepthFadeScale", new Vector4(depthFade, depthFade, depthFade, depthFade));
                }
            }
        }

        // g_MatWorld: the columns of the group's row-major transform (R+320), as the title
        // transposes it (sub_82270CD0) for the shader's dot(c72+i, position).
        bool moved = liveChanged;
        for (int i = 0; i < 16; i++)
        {
            if (group.World[i] != _buffer[at + 21 + i])
            {
                moved = true;
                group.World[i] = _buffer[at + 21 + i];
            }
        }
        if (moved)
        {
            for (int i = 0; i < 3; i++)
            {
                _world[i] = new Vector4(group.World[i], group.World[4 + i], group.World[8 + i], group.World[12 + i]);
            }
            foreach (ShaderMaterial m in _chain)
            {
                m.SetShaderParameter("vs_g_MatWorld", _world);
            }
        }

        if (_buffer[at + 42] > 0f)
        {
            var tile = new Vector4(0f, 0f, 1f / Math.Max(viewport.X, 1f), 1f / Math.Max(viewport.Y, 1f));
            foreach (ShaderMaterial m in _chain)
            {
                m.SetShaderParameter("ps_g_InverseViewProjection", _inverseViewProjection);
            }
        }
    }

    /// <summary>
    /// g_InverseViewProjection for a soft group: the pixel shader forms (x, y, z, 1) from the
    /// pixel's screen position (y up) and 1 - depth, dots it with these four rows and divides,
    /// expecting the guest's world. Godot's depth is reversed, so 1 - depth is the standard
    /// [0, 1] depth, which is 2z - 1 in the camera's GL projection; the rows are those of
    /// (Godot world to guest world) x inverse(projection x view) x (that z remap).
    /// </summary>
    private void InverseViewProjection(Camera3D camera)
    {
        Projection view = new Projection(camera.GlobalTransform.AffineInverse());
        Projection projection = camera.GetCameraProjection();
        Projection remap = Projection.Identity;
        remap.Z = new Vector4(0f, 0f, 2f, 0f);
        remap.W = new Vector4(0f, 0f, -1f, 1f);
        var toGuest = new Projection(
            new Vector4(1f, 0f, 0f, 0f), new Vector4(0f, 0f, 1f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 0f, 0f, 1f));
        Projection m = toGuest * (projection * view).Inverse() * remap;
        for (int i = 0; i < 4; i++)
        {
            _inverseViewProjection[i] = new Vector4(m.X[i], m.Y[i], m.Z[i], m.W[i]);
        }
    }

    /// <summary>A material texture: by the path the key points at, else by the key as a hash.</summary>
    private Texture2D? Texture(int o)
    {
        uint key = BitConverter.SingleToUInt32Bits(_buffer[o]);
        string path = string.Empty;
        if (BitConverter.SingleToUInt32Bits(_buffer[o + 4]) != 0u)
        {
            var bytes = new List<byte>(64);
            for (int w = 0; w < 16 && path.Length == 0; w++)
            {
                uint word = BitConverter.SingleToUInt32Bits(_buffer[o + 4 + w]);
                for (int b = 3; b >= 0; b--)
                {
                    byte ch = (byte)((word >> (8 * b)) & 0xFF);
                    if (ch == 0)
                    {
                        path = System.Text.Encoding.ASCII.GetString(bytes.ToArray());
                        break;
                    }
                    bytes.Add(ch);
                }
            }
            if (path.Length == 0 && bytes.Count != 0)
            {
                path = System.Text.Encoding.ASCII.GetString(bytes.ToArray());
            }
        }
        string cacheKey = path.Length != 0 ? "p:" + path : "h:" + key.ToString("X8");
        if (_textures.TryGetValue(cacheKey, out Texture2D? cached))
        {
            return cached;
        }
        Texture2D? texture;
        if (path.Length != 0)
        {
            texture = Engine_TexLibrary.Get(path, TexSemantic.Unknown, Engine_BnkMeshLoader.ReadBytes, out _);
        }
        else
        {
            texture = Refii.Level.Engine_EnvironmentTextures.Load(key, Engine_BnkMeshLoader.ReadBytes, out path);
        }
        if (texture is null)
        {
            GD.Print($"[classic] particle texture {cacheKey} " + (path.Length == 0 ? "unresolved" : $"'{path}' did not decode"));
        }
        _textures[cacheKey] = texture;
        return texture;
    }

    /// <summary>Godot's axes to the guest's: (x, y, z) -> (-x, z, y).</summary>
    private static Vector3 ToGuest(Vector3 v) => new(-v.X, v.Z, v.Y);

    /// <summary>A guest-world point in Godot's axes (the same map, its own inverse).</summary>
    private static Vector3 ToGuestBounds(float x, float y, float z) => new(-x, z, y);
}
