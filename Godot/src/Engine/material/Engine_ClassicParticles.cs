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
/// Light groups come through <see cref="Engine_ClassicLitInstances"/> like any lit draw. Trails
/// (record kinds 2 and 3) are Engine_ClassicParticles.Trails; displacement groups and trails draw
/// into Engine_ClassicDisplacement. ONE/ONE groups draw into the scene, after the translucent bucket.
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
        /// <summary>The inputs <see cref="Key"/> is built from, packed; the string is rebuilt only when they change.</summary>
        public long Shape = -1;
        public ulong TextureSignature;
        /// <summary>The <see cref="_inverseVp"/> version its materials hold.</summary>
        public int InverseVpSent = -1;
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
        /// <summary>A displacement group: drawn into the displacement target, not the scene.</summary>
        public bool Displacement;
        /// <summary>True when depth-fade (soft particles) is active, requiring ps_g_InverseViewProjection updates on camera motion.</summary>
        public bool Soft;
        /// <summary>Quads the mesh's surface holds now (a multiple of <see cref="QuadStep"/>); 0 = none.</summary>
        public int Allocated;
        /// <summary>The surface's streams as uploaded in place, when its layout is the one packed here.</summary>
        public byte[] VertexBytes = Array.Empty<byte>();
        public byte[] AttributeBytes = Array.Empty<byte>();
        public bool InPlace;
    }

    /// <summary>
    /// A group's surface is sized in steps of this many quads and rewritten in place while its
    /// record count stays inside the step: recreating it every frame made the render thread free
    /// and rebuild the instance's geometry data, uniform sets and buffers each time. The quads
    /// past the records stand far beyond any fade-out with no size, and draw nothing.
    /// </summary>
    private const int QuadStep = 64;

    /// <summary>The ring copy's clamp on a record's keyframe time (`flt_8209A938`): the tables hold 20 keys.</summary>
    private const float KeyframeLimit = 19f;

    /// <summary>Surface writes so far, for the status line: rewritten in place, and rebuilt whole.</summary>
    public static long InPlaceUploads, Rebuilds;

    private static readonly Vector3 Parked = new(1e7f, 1e7f, 1e7f);

    private const string DisplacementVertex = "FACE_ME_DISPLACEMENT_VERTEX_SHADER";
    private const string DisplacementPixel = "PARTICLE_FACEME_DISPLACEMENT_PIXEL_SHADER";

    /// <summary>
    /// A displacement group's material (`Render_EffectQuadListDraw_INFERRED` 0x82A59620): VS 0039
    /// and PS 0037 into the displacement target, ONE / ONE, depth-tested against the main view's
    /// depth (Engine_ClassicDisplacement), no soft-particle or lighting constants.
    /// </summary>
    /// <summary>
    /// The camera axes (VS c114 up, c115 right) replaced by the group's fixed axes when its
    /// material states them; the translated particle stages read those registers through
    /// XR_VS_CONST.
    /// </summary>
    private static readonly string FixedAxesDefines =
        ViewDefines + "\n" +
        "uniform bool xr_fixed_axes = false;\n" +
        "uniform vec4 xr_fixed_up = vec4(0.0, 0.0, 1.0, 0.0);\n" +
        "uniform vec4 xr_fixed_right = vec4(1.0, 0.0, 0.0, 0.0);\n" +
        "#define XR_VS_CONST(reg_index, reg_value) (xr_fixed_axes && (reg_index) == 114 ? xr_fixed_up : " +
        "(xr_fixed_axes && (reg_index) == 115 ? xr_fixed_right : (reg_value)))";

    private const string DisplacementDefines =
        "global uniform sampler2D " + Engine_ClassicDisplacement.SceneDepthName + ";\n" +
        "global uniform sampler2D " + Engine_ClassicDisplacement.SceneDepthSecondName + ";\n" +
        "#define XR_ALPHA_OUT(a) 0.0\n" +
        "#define XR_BEFORE_OUTPUT(c) if ((XR_SECOND_VIEW ? texture(" + Engine_ClassicDisplacement.SceneDepthSecondName +
        ", SCREEN_UV).r : texture(" + Engine_ClassicDisplacement.SceneDepthName + ", SCREEN_UV).r) > FRAGCOORD.z) { discard; }";

    private static readonly List<ShaderMaterial> _chain = new();

    private readonly Dictionary<uint, Group> _groups = new();
    private readonly List<uint> _gone = new();
    private readonly Dictionary<string, Texture2D?> _textures = new();
    private readonly float[] _buffer = new float[1 << 20];
    private readonly Vector4[] _colours = new Vector4[MaxKeyframes];
    private readonly Vector4[] _scales = new Vector4[MaxKeyframes];
    private readonly Vector4[] _globalScales = new Vector4[MaxKeyframes];
    private readonly Vector4[] _world = new Vector4[3];
    private readonly Vector4[] _uvScales = new Vector4[4];
    private readonly Vector4[] _inverseViewProjection = new Vector4[4];

    /// <summary>g_InverseViewProjection as one Variant, rebuilt when the camera moves; every soft group and trail sets this one.</summary>
    private readonly Refii.Core.Engine_ArrayParam _inverseVp = new();
    private readonly Godot.Collections.Array _arrays = new();
    private long _frames;
    private int _particles;
    private int _skippedDisplacement;
    private bool _reportedMissing;

    /// <summary>The live instance.</summary>
    public static Engine_ClassicParticles? Instance { get; private set; }

    /// <summary>
    /// The owners (quad header [7], trail header [38]: the particle-system instance) of the groups drawn at the last pump,
    /// for GameFx to stand those effects' simulated copies down. Empty under classic.
    /// </summary>
    public static readonly HashSet<uint> DrawnOwners = new();

    /// <summary>Groups drawn last frame, particles among them, displacement groups skipped.</summary>
    /// <summary>
    /// For the debug channel: each group's guest address, world box (Godot), the stated blend
    /// (src, dst, op — header [39..41]) and the material key (textures|soft|anim|blend|oneOne|ref16).
    /// </summary>
    public List<Dictionary<string, object?>> Describe()
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (KeyValuePair<uint, Group> pair in _groups)
        {
            Group group = pair.Value;
            bool live = GodotObject.IsInstanceValid(group.Node) && group.Node.IsInsideTree();
            float[] h = group.Snapshot;
            list.Add(new Dictionary<string, object?>
            {
                ["address"] = $"{pair.Key:X8}",
                ["visible"] = live && group.Node.IsVisibleInTree(),
                ["displacement"] = group.Displacement,
                ["blend"] = group.SnapshotLength > 41 ? new[] { h[39], h[40], h[41] } : null,
                ["key"] = group.Key,
                ["world"] = (float[])group.World.Clone(),
                ["textures"] = TexturesResolved(group),
                ["records"] = group.SnapshotLength > Header ? (group.SnapshotLength - Header) / Record : 0,
                ["allocatedQuads"] = group.Allocated,
                ["inPlace"] = group.InPlace,
                ["layers"] = live ? group.Node.Layers : 0u,
                ["material"] = live && group.Node.GetSurfaceOverrideMaterial(0) is ShaderMaterial { Shader: not null },
                ["aabb"] = live ? group.Node.CustomAabb.ToString() : null,
                ["firstRecord"] = group.SnapshotLength >= Header + 12
                    ? new[] { h[Header], h[Header + 1], h[Header + 2], h[Header + 4], h[Header + 5], h[Header + 6], h[Header + 7] }
                    : null,
                ["drawn"] = live ? DrawnFirstVertex(group) : null,
            });
        }
        return list;
    }

    /// <summary>
    /// The surface's first vertex and its CUSTOM0 as the server holds them (a GPU readback, for
    /// the debug channel only): what was uploaded, to set beside the first record.
    /// </summary>
    private static float[]? DrawnFirstVertex(Group group)
    {
        if (group.Allocated == 0 || group.Mesh.GetSurfaceCount() == 0)
        {
            return null;
        }
        Godot.Collections.Dictionary surface = RenderingServer.MeshGetSurface(group.Mesh.GetRid(), 0);
        if (!surface.TryGetValue("vertex_data", out Variant vertexData) || !surface.TryGetValue("attribute_data", out Variant attributeData))
        {
            return null;
        }
        byte[] v = vertexData.AsByteArray(), a = attributeData.AsByteArray();
        if (v.Length < 12 || a.Length < 16)
        {
            return null;
        }
        return new[]
        {
            BitConverter.ToSingle(v, 0), BitConverter.ToSingle(v, 4), BitConverter.ToSingle(v, 8),
            BitConverter.ToSingle(a, 0), BitConverter.ToSingle(a, 4), BitConverter.ToSingle(a, 8), BitConverter.ToSingle(a, 12),
            v.Length / 12f,
        };
    }

    private bool[] TexturesResolved(Group group)
    {
        int count = group.Key.Length > 0 && int.TryParse(group.Key.Split('|')[0], out int t) ? t : 0;
        var resolved = new bool[Math.Max(count, 0)];
        for (int i = 0; i < resolved.Length && 254 + i * 20 + 20 <= group.SnapshotLength; i++)
        {
            resolved[i] = Texture(group.Snapshot, 254 + i * 20) is not null;
        }
        return resolved;
    }

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

    private uint _lastUsed;
    private bool _lastModern;

    /// <summary>Reads the groups and rebuilds their meshes and constants. Main thread, once a frame, after the lit instances.</summary>
    public void Pump(Camera3D camera)
    {
        ++_frames;
        bool modern = !Engine_ClassicMaterial.Active;
        if (modern)
        {
            // The main view leaves layers past 20 out; the particles' own layer joins it.
            if ((camera.CullMask & Layer) == 0)
            {
                camera.CullMask |= Layer;
            }
        }
        else if (!Engine_ClassicShaders.HasEntry(VertexEntry))
        {
            if (!_reportedMissing)
            {
                _reportedMissing = true;
                GD.Print("[classic] no FACE_ME vertex shader in the bank; particles stay off");
            }
            return;
        }

        bool guestStepped = Refii.Core.Engine_GuestFrame.Changed || _frames <= 1 || modern != _lastModern;
        _lastModern = modern;
        bool inverseVpChanged = InverseViewProjection(camera);
        if (!guestStepped)
        {
            // Between 30 Hz guest frames, the particle buffer and group membership are invariant.
            // Only soft-particle groups need their ps_g_InverseViewProjection uniform refreshed if
            // the interpolated Godot camera moved since last frame.
            if (inverseVpChanged)
            {
                foreach (Group group in _groups.Values)
                {
                    if (!group.Soft || group.InverseVpSent == _inverseVp.Version)
                    {
                        continue;
                    }
                    group.InverseVpSent = _inverseVp.Version;
                    if (modern)
                    {
                        group.Material?.SetShaderParameter(PsInverseViewProjectionName, _inverseVp.Value);
                    }
                    else
                    {
                        Chain(group);
                        foreach (ShaderMaterial m in _chain)
                        {
                            m.SetShaderParameter(PsInverseViewProjectionName, _inverseVp.Value);
                        }
                    }
                }
            }
            PumpTrails(camera, default, guestStepped: false, inverseVpChanged);
            return;
        }

        _particles = 0;
        _skippedDisplacement = 0;
        uint used = _lastUsed = Refii.Host.HostBridge.GetClassicParticles(_buffer);
        DrawnOwners.Clear();
        int groups = used == 0 ? 0 : (int)_buffer[0];
        Vector2 viewport = GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);

        int at = 2;
        int displaced = 0;
        for (int g = 0; g < groups && at + Header <= used; g++)
        {
            int count = (int)_buffer[at + 2];
            if (at + Header + count * Record > used)
            {
                break;
            }
            uint address = BitConverter.SingleToUInt32Bits(_buffer[at]);
            bool displacement = _buffer[at + 43] != 0f;
            if (displacement && Engine_ClassicDisplacement.Instance is null)
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
            if (group.Displacement != displacement)
            {
                group.Displacement = displacement;
                group.Key = string.Empty;
                group.Shape = -1;
                group.TextureSignature = 0;
                group.Node.Layers = displacement ? Engine_ClassicDisplacement.Layer : Layer;
            }
            group.Seen = _frames;
            // The sim states a group at its own rate; a frame that repeats the last records
            // rebuilds nothing.
            int length = Header + count * Record;
            bool changed = guestStepped && (group.SnapshotLength != length || !Same(group.Snapshot, _buffer, at, length));
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
            if (Compose(group, at, changed))
            {
                Constants(group, at, viewport, changed);
                _particles += count;
                uint owner = BitConverter.SingleToUInt32Bits(_buffer[at + 7]);
                if (guestStepped && modern && owner != 0u && count > 0)
                {
                    DrawnOwners.Add(owner);
                }
                if (group.Displacement)
                {
                    ++displaced;
                }
            }
            at += Header + count * Record;
        }

        if (guestStepped)
        {
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

        Engine_ClassicDisplacement.Drawn = displaced;
        PumpTrails(camera, viewport, guestStepped, inverseVpChanged);
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
        int quads = Math.Max(QuadStep, (count + QuadStep - 1) / QuadStep * QuadStep);
        if (group.Capacity < quads)
        {
            group.Capacity = Math.Max(quads, group.Capacity * 2);
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
        // THE SIM'S RECORD IS NOT THE STREAM. The ring copy `sub_822BED88` (verified, TU1:
        // `li r6,0x10` / `li r7,0x20`, 0x822bf3cc..0x822bf43c) builds each 48-byte vertex from
        // the record: POSITION = record +16 (the position); TEXCOORD0 = (+8 size x, +12 size y,
        // +4 rotation, min(+0 keyframe time, 19.0 `flt_8209A938`)); TEXCOORD1 = +32 unchanged.
        // The decl (`sub_82A58E40`) reads them at 0 / 16 / 32. The export hands the record raw.
        int records = at + Header;
        for (int r = 0; r < count; r++)
        {
            int src = records + r * Record;
            var position = new Vector3(_buffer[src + 4], _buffer[src + 5], _buffer[src + 6]);
            float keyframe = MathF.Min(_buffer[src], KeyframeLimit);
            for (int c = 0; c < 4; c++)
            {
                int v = r * 4 + c;
                group.Vertices[v] = position;
                int f = v * 4;
                group.Custom0[f] = _buffer[src + 2];
                group.Custom0[f + 1] = _buffer[src + 3];
                group.Custom0[f + 2] = _buffer[src + 1];
                group.Custom0[f + 3] = keyframe;
                group.Custom1[f] = _buffer[src + 8];
                group.Custom1[f + 1] = _buffer[src + 9];
                group.Custom1[f + 2] = _buffer[src + 10];
                group.Custom1[f + 3] = _buffer[src + 11];
            }
        }
        Array.Fill(group.Vertices, Parked, count * 4, (quads - count) * 4);
        Array.Clear(group.Custom0, count * 16, (quads - count) * 16);
        Array.Clear(group.Custom1, count * 16, (quads - count) * 16);

        if (group.Allocated == quads && group.InPlace)
        {
            Upload(group, quads);
            ++InPlaceUploads;
        }
        else
        {
            Rebuild(group, quads);
            ++Rebuilds;
        }
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

    /// <summary>A new surface of <paramref name="quads"/> quads, and whether its streams are the layout <see cref="Upload"/> packs.</summary>
    private void Rebuild(Group group, int quads)
    {
        for (int i = 0; i < (int)Mesh.ArrayType.Max; i++)
        {
            _arrays[i] = default;
        }
        using (Variant vertices = Variant.CreateFrom(group.Vertices.AsSpan(0, quads * 4)))
        using (Variant custom0 = Variant.CreateFrom(group.Custom0.AsSpan(0, quads * 16)))
        using (Variant custom1 = Variant.CreateFrom(group.Custom1.AsSpan(0, quads * 16)))
        using (Variant indices = Variant.CreateFrom(group.Indices.AsSpan(0, quads * 6)))
        {
            _arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            _arrays[(int)Mesh.ArrayType.Custom0] = custom0;
            _arrays[(int)Mesh.ArrayType.Custom1] = custom1;
            _arrays[(int)Mesh.ArrayType.Index] = indices;
        }
        var flags = (Mesh.ArrayFormat)(
            ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift) |
            ((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift));
        group.Mesh.ClearSurfaces();
        group.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays, null, null, flags);
        group.Allocated = quads;

        // In place only when the server's layout is the packed one: positions alone at 12 bytes,
        // CUSTOM0 then CUSTOM1 as RGBA floats at 32. Anything else keeps the rebuild.
        int vertexCount = quads * 4;
        RenderingServer.ArrayFormat format = (RenderingServer.ArrayFormat)(long)group.Mesh.SurfaceGetFormat(0);
        group.InPlace =
            RenderingServer.MeshSurfaceGetFormatVertexStride(format, vertexCount) == 12 &&
            RenderingServer.MeshSurfaceGetFormatAttributeStride(format, vertexCount) == 32 &&
            RenderingServer.MeshSurfaceGetFormatOffset(format, vertexCount, (int)RenderingServer.ArrayType.Custom0) == 0 &&
            RenderingServer.MeshSurfaceGetFormatOffset(format, vertexCount, (int)RenderingServer.ArrayType.Custom1) == 16;
        if (group.InPlace && group.VertexBytes.Length != vertexCount * 12)
        {
            group.VertexBytes = new byte[vertexCount * 12];
            group.AttributeBytes = new byte[vertexCount * 32];
        }
    }

    /// <summary>The records into the surface's existing streams.</summary>
    private static void Upload(Group group, int quads)
    {
        int vertexCount = quads * 4;
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(group.Vertices.AsSpan(0, vertexCount))
            .CopyTo(group.VertexBytes);
        Span<float> attributes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(group.AttributeBytes.AsSpan());
        for (int v = 0; v < vertexCount; v++)
        {
            group.Custom0.AsSpan(v * 4, 4).CopyTo(attributes.Slice(v * 8, 4));
            group.Custom1.AsSpan(v * 4, 4).CopyTo(attributes.Slice(v * 8 + 4, 4));
        }
        Rid mesh = group.Mesh.GetRid();
        RenderingServer.MeshSurfaceUpdateVertexRegion(mesh, 0, 0, (ReadOnlySpan<byte>)group.VertexBytes.AsSpan(0, vertexCount * 12));
        RenderingServer.MeshSurfaceUpdateAttributeRegion(mesh, 0, 0, (ReadOnlySpan<byte>)group.AttributeBytes.AsSpan(0, vertexCount * 32));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static ulong ComputeTextureSignature(float[] buffer, int at, int textures)
    {
        ulong hash = 14695981039346656037UL;
        uint material = BitConverter.SingleToUInt32Bits(buffer[at + 37]);
        hash = (hash ^ material) * 1099511628211UL;
        for (int t = 0; t < textures; t++)
        {
            int o = at + 254 + t * 20;
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

    /// <summary>The material for the group's variant, rebuilt when the variant changes.</summary>
    private bool Compose(Group group, int at, bool changed)
    {
        if (!changed && group.Material is not null)
        {
            return true;
        }
        int textures = Math.Clamp((int)_buffer[at + 38], 0, 4);
        ulong texSig = ComputeTextureSignature(_buffer, at, textures);
        bool sameTex = texSig == group.TextureSignature;
        group.TextureSignature = texSig;
        bool soft = _buffer[at + 42] > 0f;
        group.Soft = soft;
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
        long shape = ShapeOf(textures, soft, anim, alphaRef16, false, false, src, dst, op);
        if (shape == group.Shape && sameTex && group.Material is not null)
        {
            return true;
        }
        group.Shape = shape;
        string blend = BlendMode(src, dst, op, out bool oneOne);
        string key = $"{textures}|{soft}|{anim}|{blend}|{oneOne}|{alphaRef16}";
        if (key == group.Key && sameTex && group.Material is not null)
        {
            return true;
        }
        group.Key = key;
        if (!Engine_ClassicMaterial.Active)
        {
            return !group.Displacement && ComposeModern(group, at, textures, blend, oneOne, alphaRef16);
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
        string vertexEntry = VertexEntry;
        string pixelEntry = Engine_ClassicShaders.FindEntry("PSHADER", Family, tokens);
        string renderMode = "unshaded, cull_disabled, depth_draw_never, fog_disabled, " + blend;
        string defines = oneOne ? "#define XR_ALPHA_OUT(a) 0.0" : string.Empty;
        if (group.Displacement)
        {
            Engine_ClassicDisplacement.EnsureRegistered();
            vertexEntry = DisplacementVertex;
            pixelEntry = Engine_ClassicShaders.HasEntry(DisplacementPixel) ? DisplacementPixel : string.Empty;
            renderMode = "unshaded, cull_disabled, depth_draw_never, depth_test_disabled, fog_disabled, blend_premul_alpha";
            defines = DisplacementDefines;
        }
        if (pixelEntry.Length == 0)
        {
            return false;
        }
        Shader? shader = Engine_ClassicShaders.Compose(vertexEntry, pixelEntry, renderMode, 0,
            (defines.Length != 0 ? defines + "\n" : string.Empty) + FixedAxesDefines);
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
        if (!group.Displacement)
        {
            // Its light-group variants are composed from these defines: they read the view's own
            // inverse view-projection too.
            Engine_ClassicMaterial.RegisterSinglePass(material, Family, VertexEntry, tokens, renderMode,
                (defines.Length != 0 ? defines + "\n" : string.Empty) + ViewDefines);
        }
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
        if (!group.Attached && !group.Displacement)
        {
            group.Attached = true;
            Engine_ClassicLitInstances.Attach(group.Node);
        }
        return true;
    }

    private const string ModernParticleInclude = "res://src/Engine/fx/fx_guest_particle.gdshaderinc";
    private const string ModernTrailInclude = "res://src/Engine/fx/fx_guest_trail.gdshaderinc";
    private static readonly Dictionary<string, Shader> ModernShaders = new();
    private static readonly StringName ModernTexturesName = "particle_textures";
    private static readonly StringName ModernAlphaOutName = "particle_alpha_out";
    private static readonly StringName AlphaThresholdName = "xr_alpha_threshold";

    /// <summary>
    /// Our own particle or trail shader (fx_guest_particle / fx_guest_trail) in a stated blend,
    /// lit by Godot's lights; one per (stage, blend), shared by every group that states it.
    /// </summary>
    private static Shader ModernShader(string include, string defines, string blend)
    {
        string key = include + "|" + defines + "|" + blend;
        if (!ModernShaders.TryGetValue(key, out Shader? shader))
        {
            shader = new Shader
            {
                Code = "shader_type spatial;\nrender_mode " + blend +
                    ", cull_disabled, depth_draw_never, fog_disabled, shadows_disabled, specular_disabled, skip_vertex_transform;\n" +
                    defines + "#include \"" + include + "\"\n",
            };
            ModernShaders[key] = shader;
        }
        return shader;
    }

    /// <summary>A modern material over <paramref name="shader"/> with the draw's textures, from the entries at <paramref name="entries"/>.</summary>
    private ShaderMaterial ModernMaterial(Shader shader, float[] buffer, int entries, int textures, bool oneOne, bool alphaRef16)
    {
        var material = new ShaderMaterial
        {
            Shader = shader,
            // The title draws its ONE/ONE groups after the translucent bucket.
            RenderPriority = oneOne ? 10 : 0,
        };
        material.SetShaderParameter(ModernTexturesName, Math.Max(textures, 1));
        material.SetShaderParameter(ModernAlphaOutName, oneOne ? 0f : 1f);
        material.SetShaderParameter(AlphaThresholdName, (alphaRef16 ? 16f : 1f) / 255f);
        for (int t = 0; t < textures; t++)
        {
            Texture2D? texture = Texture(buffer, entries + t * 20);
            if (texture is not null)
            {
                material.SetShaderParameter(t == 0 ? "ps_g_ParticleTextureSamplers" : "ps_s" + t, texture);
            }
        }
        return material;
    }

    /// <summary>A group's modern material: the quads on our own particle shader, the same constants as the classic stages.</summary>
    private bool ComposeModern(Group group, int at, int textures, string blend, bool oneOne, bool alphaRef16)
    {
        ShaderMaterial material = ModernMaterial(ModernShader(ModernParticleInclude, string.Empty, blend), _buffer, at + 254, textures, oneOne, alphaRef16);
        group.Material = material;
        group.Node.SetSurfaceOverrideMaterial(0, material);
        return true;
    }

    /// <summary>
    /// The title's blend enums (`Render_MapBlendFactorToD3D`: 0 ZERO, 1 ONE, 2 SRCCOLOR,
    /// 3 INVSRCCOLOR, 4 SRCALPHA, 5 INVSRCALPHA, 6 DESTCOLOR, 7 INVDESTCOLOR, 8 DESTALPHA,
    /// 9 INVDESTALPHA; ops 0 ADD, 1 SUBTRACT, 2 REVSUBTRACT, 3 MIN, 4 MAX) as Godot's modes.
    /// ONE/ONE is premultiplied with the alpha held at zero.
    /// </summary>
    /// <summary>A composition's inputs as one number, compared each frame instead of its key string.</summary>
    private static long ShapeOf(int textures, bool soft, bool anim, bool alphaRef16, bool doubled, bool displaces, int src, int dst, int op) =>
        (uint)textures | (soft ? 8L : 0L) | (anim ? 16L : 0L) | (alphaRef16 ? 32L : 0L) | (doubled ? 64L : 0L) | (displaces ? 128L : 0L) |
        ((long)(src & 0xFFFF) << 8) | ((long)(dst & 0xFFFF) << 24) | ((long)(op & 0xFFFF) << 40);

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
    /// when the camera moves. A fixed-axis group (M+56) takes its own axes (XR_VS_CONST).
    /// </summary>
    private void Constants(Group group, int at, Vector2 viewport, bool changed)
    {
        if (!changed && !Engine_ClassicMaterial.Active && ReferenceEquals(group.Live, group.Material) && group.Material is not null)
        {
            if (_buffer[at + 42] > 0f && group.InverseVpSent != _inverseVp.Version)
            {
                group.InverseVpSent = _inverseVp.Version;
                group.Material.SetShaderParameter(PsInverseViewProjectionName, _inverseVp.Value);
            }
            return;
        }

        ShaderMaterial? liveBefore = group.Live;
        Chain(group);
        bool liveChanged = !ReferenceEquals(liveBefore, group.Live);

        if (changed || liveChanged)
        {
            int staticHash = HashRange(at + 12, 9) ^ (HashRange(at + 38, 16) * 3) ^ (HashRange(at + 64, 270) * 7) ^ (int)group.TextureSignature ^ (int)(group.TextureSignature >> 32);
            if (staticHash != group.StaticHash || liveChanged)
            {
                group.StaticHash = staticHash;
                float f0 = _buffer[at + 12], f1 = _buffer[at + 13], f2 = _buffer[at + 14], f3 = _buffer[at + 15], f4 = _buffer[at + 16];
                var fadeIn = new Vector4(f0, f1, f1 > f0 ? 1f / (f1 - f0) : 0f, 0f);
                var fadeOut = new Vector4(f3, f3 > f2 ? 1f / (f3 - f2) : 1f, f4, 0f);
                // The modern shader adds the scene's ambient itself; it takes the material's own here.
                Vector3 ambient = Engine_ClassicMaterial.Active ? Engine_ClassicFeed.GlobalAmbient : Vector3.Zero;
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
                int textures = Math.Clamp((int)_buffer[at + 38], 0, 4);
                var starts = Vector4.Zero;
                var invDeltas = Vector4.Zero;
                var divisions = Vector4.Zero;
                for (int t = 0; t < 4; t++)
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
                        // Lane t - 1: the blend stages weigh texture t by (age - start[t - 1]) * invDelta[t - 1]
                        // (`sub_821ABC60` stores it at +96 + 4 (t - 1)); lane t left blending off.
                        invDeltas[t - 1] = start != previous ? 1f / (start - previous) : 0f;
                    }
                    if (Math.Max(x, y) > 1f)
                    {
                        _uvScales[t] = new Vector4(x > 0f ? 1f / x : 0f, y > 0f ? 1f / y : 0f, 0f, 0f);
                    }
                }
                float softDistance = _buffer[at + 42];
                float depthFade = softDistance > 0f ? 1f / softDistance : 0f;
                // A fixed-axis group (M+56): its up and right are the quaternion D+32 applied to y and
                // x (`sub_821CA108` 0x821CA388..0x821CA40C, `sub_822A1640` = rotate), not the camera's.
                bool fixedAxes = _buffer[at + 45] != 0f;
                var q = new Quaternion(_buffer[at + 46], _buffer[at + 47], _buffer[at + 48], _buffer[at + 49]);
                Vector3 up = Vector3.Up, right = Vector3.Right;
                if (fixedAxes && q.LengthSquared() > 1e-8f)
                {
                    q = q.Normalized();
                    up = q * new Vector3(0f, 1f, 0f);
                    right = q * new Vector3(1f, 0f, 0f);
                }
                using Variant colours = Variant.CreateFrom(_colours);
                using Variant scales = Variant.CreateFrom(_scales);
                using Variant globalScales = Variant.CreateFrom(_globalScales);
                using Variant uvScales = Variant.CreateFrom(_uvScales);
                foreach (ShaderMaterial m in _chain)
                {
                    m.SetShaderParameter(FixedAxesName, fixedAxes);
                    m.SetShaderParameter(FixedUpName, new Vector4(up.X, up.Y, up.Z, 0f));
                    m.SetShaderParameter(FixedRightName, new Vector4(right.X, right.Y, right.Z, 0f));
                    m.SetShaderParameter(VsFadeInDistanceInfoName, fadeIn);
                    m.SetShaderParameter(VsFadeOutDistanceInfoName, fadeOut);
                    m.SetShaderParameter(VsGlobalAmbientName, new Vector4(ambient.X, ambient.Y, ambient.Z, 1f));
                    m.SetShaderParameter(VsKeyFrameColorsName, colours);
                    m.SetShaderParameter(VsKeyFrameScalesSelfIllumFactorStartTimesName, scales);
                    m.SetShaderParameter(VsKeyFrameGlobalScalesName, globalScales);
                    m.SetShaderParameter(PsTextureStartTimesName, starts);
                    m.SetShaderParameter(PsTextureStartTimesInvDeltaName, invDeltas);
                    m.SetShaderParameter(PsTextureNumXDivsName, divisions);
                    m.SetShaderParameter(PsTextureUVScalesName, uvScales);
                    if (softDistance > 0f)
                    {
                        m.SetShaderParameter(PsDepthFadeScaleName, new Vector4(depthFade, depthFade, depthFade, depthFade));
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
                using Variant world = Variant.CreateFrom(_world);
                foreach (ShaderMaterial m in _chain)
                {
                    m.SetShaderParameter(VsMatWorldName, world);
                }
            }
        }

        if (_buffer[at + 42] > 0f && (liveChanged || group.InverseVpSent != _inverseVp.Version))
        {
            group.InverseVpSent = _inverseVp.Version;
            foreach (ShaderMaterial m in _chain)
            {
                m.SetShaderParameter(PsInverseViewProjectionName, _inverseVp.Value);
            }
        }
    }

    private static readonly StringName FixedAxesName = "xr_fixed_axes";
    private static readonly StringName FixedUpName = "xr_fixed_up";
    private static readonly StringName FixedRightName = "xr_fixed_right";
    private static readonly StringName VsFadeInDistanceInfoName = "vs_g_FadeInDistanceInfo";
    private static readonly StringName VsFadeOutDistanceInfoName = "vs_g_FadeOutDistanceInfo";
    private static readonly StringName VsGlobalAmbientName = "vs_g_GlobalAmbient";
    private static readonly StringName VsKeyFrameColorsName = "vs_g_KeyFrameColors";
    private static readonly StringName VsKeyFrameScalesSelfIllumFactorStartTimesName = "vs_g_KeyFrameScalesSelfIllumFactorStartTimes";
    private static readonly StringName VsKeyFrameGlobalScalesName = "vs_g_KeyFrameGlobalScales";
    private static readonly StringName PsTextureStartTimesName = "ps_g_TextureStartTimes";
    private static readonly StringName PsTextureStartTimesInvDeltaName = "ps_g_TextureStartTimesInvDelta";
    private static readonly StringName PsTextureNumXDivsName = "ps_g_TextureNumXDivs";
    private static readonly StringName PsTextureUVScalesName = "ps_g_TextureUVScales";
    private static readonly StringName PsDepthFadeScaleName = "ps_g_DepthFadeScale";
    private static readonly StringName VsMatWorldName = "vs_g_MatWorld";
    private static readonly StringName PsInverseViewProjectionName = "ps_g_InverseViewProjection";

    /// <summary>
    /// g_InverseViewProjection for a soft group: the pixel shader forms (x, y, z, 1) from the
    /// pixel's screen position (y up) and 1 - depth, dots it with these four rows and divides,
    /// expecting the guest's world. Godot's depth is reversed, so 1 - depth is the standard
    /// [0, 1] depth, which is 2z - 1 in the camera's GL projection; the rows are those of
    /// (Godot world to guest world) x inverse(projection x view) x (that z remap).
    /// </summary>
    /// <remarks>
    /// The material's rows are the first view's, from the camera that draws it (on split screen
    /// player one's, whose projection is not the window camera's); the second view reads its own
    /// from the host register the composition names (<see cref="ViewDefines"/>).
    /// </remarks>
    private bool InverseViewProjection(Camera3D camera)
    {
        Engine_ClassicViews.InverseViewProjection(Engine_ClassicViews.Camera(0) ?? camera, _inverseViewProjection);
        return _inverseVp.Take(_inverseViewProjection);
    }

    /// <summary>
    /// The particle and trail stages' material constants that belong to the view: the soft
    /// groups' g_InverseViewProjection (Engine_ClassicShaders reads the directive).
    /// </summary>
    private static string ViewDefines =>
        Engine_ClassicShaders.ViewParam("ps_g_InverseViewProjection", true, Engine_ClassicRegisters.Ps.HostInverseViewProjection);

    /// <summary>A material texture: by the path the key points at, else by the key as a hash.</summary>
    private Texture2D? Texture(int o) => Texture(_buffer, o);

    /// <summary>A texture entry (key word, start, divisions, 16 words of path text) at <paramref name="o"/>.</summary>
    private Texture2D? Texture(float[] _buffer, int o)
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
