using System;
using System.Collections.Generic;
using System.Text;
using Godot;
using Godot.Collections;
using Refii.Core;

namespace Refii.Lighting;

/// <summary>
/// One of the title's post-processing passes run as it was written: the translated
/// VSHADER_POSTPROCESSING_QUAD and the pass's translated pixel shader, hosted as a compute
/// shader over the pass's target.
/// </summary>
/// <remarks>
/// THE TITLE DRAWS EACH PASS AS ONE QUAD (`sub_822767E0`): VS 0156 over a RECTLIST covering the
/// target, the pass's pixel shader, its constants bound by name. A Godot spatial shader cannot
/// run inside the compositor where the frame's HDR target is, so the two translated stage bodies
/// are carried into a compute shader verbatim and invoked once per target pixel: the vertex
/// stage first, at the clip position the quad has at that pixel (D3D9 pixel i at clip 2i/W - 1,
/// the half-pixel convention every one of the title's fetch offsets assumes), then the pixel
/// stage with its parameter register at (i, j). Nothing between the two is interpolated
/// differently from the rasteriser: VS 0156 is affine in the position.
///
/// Bindings: 0 the target (rgba16f storage), 1 the pass's constants (one vec4 per register, in
/// declaration order, written with <see cref="Set(string, float, float, float, float)"/>), 2 the
/// post state the reduce pass keeps (for constants the title computes from last frame's
/// histogram), 3.. the pass's samplers in declaration order. A fixup (name, GLSL expression)
/// replaces a constant after the load, to take it from the state; one the variant does not
/// declare is skipped.
///
/// A SPRITE pass (<see cref="Kind.Sprite"/>) is one of the title's camera-facing quads drawn into
/// a post layer (the sun disc, the moon bloom, the sun beams: `sub_82277380`, `sub_821CB750`):
/// the quad is screen-aligned, so its texture coordinate is affine in the pixel and the vertex
/// stage (VSHADER_SUNGLARE, blob 20) passes it through. The dispatch covers the quad's pixel
/// rectangle; a pixel is depth-tested against the scene, run through the pixel stage, alpha
/// tested, counted (the title's occlusion query) and blended into the layer. Bindings: 0 the
/// layer (read-write), 1 constants, 2 the post state, 3 the frame's pixel registers (the stage's
/// `xr_const`, the exposure marker resolved from the state), 4 the counters, 5 the scene depth,
/// 6.. the samplers.
/// </remarks>
public sealed class Engine_ClassicPostPass
{
    /// <summary>A full-target pass on VS 0156, or a screen-aligned quad on VS blob 20.</summary>
    public enum Kind
    {
        Fullscreen,
        Sprite,
    }

    /// <summary>One quad of a sprite pass, in the layer's pixels.</summary>
    public struct SpriteDraw
    {
        public Vector4 Rect;      // x0, y0, x1, y1 in layer pixels
        public Vector4 Uv;        // u0, v0, u1, v1 at the rectangle's corners
        public float Depth;       // the quad's depth as the scene depth stores it (reversed Z)
        public float DepthScale;  // scene-depth pixels per layer pixel
        public float AlphaRef;    // the alpha test's reference in [0, 1], or < 0 for none
        public bool Mix;          // SRCALPHA / INVSRCALPHA, else ONE / ONE
        public int Counter;       // the counter its passing pixels add to, or -1
    }

    private const string VertexEntry = "VSHADER_POSTPROCESSING_QUAD";
    private const string SpriteVertexEntry = "VSHADER_SUNGLARE";
    private const string HelpersPath = "res://src/Engine/material/xenos_helpers.gdshaderinc";

    private readonly List<string> _samplers = new();
    private readonly System.Collections.Generic.Dictionary<string, (int Register, int Count)> _constants = new();
    private readonly Rid[] _bound;
    private float[] _values = System.Array.Empty<float>();
    private byte[] _bytes = System.Array.Empty<byte>();
    private bool _dirty = true;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _constantsBuffer;
    private Rid _set;
    private readonly byte[] _push = new byte[16];
    private readonly byte[] _spritePush = new byte[64];

    /// <summary>The post pass kind.</summary>
    public Kind PassKind { get; }

    /// <summary>The pixel shader's entry name.</summary>
    public string Entry { get; }

    /// <summary>The samplers the pass declares, in binding order (3 up).</summary>
    public IReadOnlyList<string> Samplers => _samplers;

    // Sampler name -> true for point, false for linear, as the stage's fetches state it.
    private readonly System.Collections.Generic.Dictionary<string, bool> _stated = new();

    /// <summary>
    /// <paramref name="nearest"/> or <paramref name="linear"/> as the stage's fetches of
    /// <paramref name="sampler"/> state (the translator's <c>xr-sampler-filter</c> line), else
    /// <paramref name="fallback"/>: the fetch, not the pass, owns the filter.
    /// </summary>
    public Rid Filter(string sampler, Rid nearest, Rid linear, Rid fallback) =>
        _stated.TryGetValue(sampler, out bool point) ? (point ? nearest : linear) : fallback;

    private Engine_ClassicPostPass(Kind kind, string entry, int samplerCount)
    {
        PassKind = kind;
        Entry = entry;
        _bound = new Rid[8 + samplerCount * 2];
    }

    /// <summary>
    /// Builds the pass from the translated stages. Null, with the reason, when a stage is missing,
    /// predates the post family's named constants, or fails to compile.
    /// </summary>
    public static Engine_ClassicPostPass? Build(
        RenderingDevice rd, string pixelEntry, (string Name, string Value)[] fixups, string store, out string error,
        Kind kind = Kind.Fullscreen)
    {
        error = string.Empty;
        string vertexEntry = kind == Kind.Sprite ? SpriteVertexEntry : VertexEntry;
        string? vertex = Refii.Models.Engine_ClassicShaders.Source(vertexEntry);
        string? pixel = Refii.Models.Engine_ClassicShaders.Source(pixelEntry);
        if (vertex is null || pixel is null)
        {
            error = $"no translated shader for {(vertex is null ? vertexEntry : pixelEntry)}";
            return null;
        }
        // Before the post family's constants were named, these stages read them as frame
        // registers, which a pass's own registers are not.
        if (kind == Kind.Fullscreen && pixel.Contains("xr_const(", StringComparison.Ordinal))
        {
            error = $"{pixelEntry} predates the post family's named constants";
            return null;
        }
        string helpers = GD.Load<ShaderInclude>(HelpersPath)?.Code ?? string.Empty;
        if (helpers.Length == 0)
        {
            error = $"cannot load {HelpersPath}";
            return null;
        }

        if (!Generate(vertex, pixel, helpers, fixups, store, out List<string> samplers,
                out List<(string Name, int Count)> constants, out string code, out error, kind))
        {
            error = $"{pixelEntry}: {error}";
            return null;
        }

        var pass = new Engine_ClassicPostPass(kind, pixelEntry, samplers.Count);
        pass._samplers.AddRange(samplers);
        foreach (string line in pixel.Split('\n'))
        {
            // `// xr-sampler-filter <name> <filter>`: the filter the stage's fetches state.
            const string marker = "// xr-sampler-filter ";
            if (line.StartsWith(marker, StringComparison.Ordinal))
            {
                string[] parts = line[marker.Length..].Trim().Split(' ');
                if (parts.Length == 2 && parts[1] != "mixed")
                {
                    pass._stated[parts[0]] = parts[1].StartsWith("nearest", StringComparison.Ordinal);
                }
            }
        }
        int register = 0;
        foreach ((string name, int count) in constants)
        {
            pass._constants[name] = (register, count);
            register += count;
        }
        pass._values = new float[Math.Max(register, 1) * 4];
        pass._bytes = new byte[pass._values.Length * 4];

        var source = new RDShaderSource { SourceCompute = code, Language = RenderingDevice.ShaderLanguage.Glsl };
        RDShaderSpirV spirv = rd.CompileSpirV(source);
        if (spirv.CompileErrorCompute.Length != 0)
        {
            error = $"{pixelEntry} failed to compile: {spirv.CompileErrorCompute}";
            return null;
        }
        pass._shader = rd.Shader(spirv, pixelEntry);
        pass._pipeline = pass._shader.IsValid ? rd.ComputePipeline(pass._shader) : default;
        pass._constantsBuffer = rd.StorageBufferCreate((uint)pass._bytes.Length, pass._bytes);
        if (!pass._pipeline.IsValid || !pass._constantsBuffer.IsValid)
        {
            error = $"{pixelEntry}: pipeline or buffer creation failed";
            pass.Free(rd);
            return null;
        }
        return pass;
    }

    /// <summary>
    /// The compute source for a pass from its two translated stages and the helpers include:
    /// pure text, so it is checked outside Godot by the same code (tools: classic post check).
    /// </summary>
    public static bool Generate(string vertex, string pixel, string helpers, (string Name, string Value)[] fixups, string store,
        out List<string> samplers, out List<(string Name, int Count)> constants, out string code, out string error,
        Kind kind = Kind.Fullscreen)
    {
        bool sprite = kind == Kind.Sprite;
        int firstSampler = sprite ? 6 : 3;
        samplers = new List<string>();
        constants = new List<(string Name, int Count)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var globals = new StringBuilder();
        var bodies = new StringBuilder();
        if (!Stage(vertex, "vertex", "xr_vertex_stage", samplers, constants, seen, globals, bodies, out error) ||
            !Stage(pixel, "fragment", "xr_pixel_stage", samplers, constants, seen, globals, bodies, out error))
        {
            code = string.Empty;
            return false;
        }

        var registers = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        int register = 0;
        foreach ((string name, int count) in constants)
        {
            registers[name] = register;
            register += count;
        }

        var text = new StringBuilder(helpers.Length + globals.Length + bodies.Length + 4096);
        text.Append("#version 450\n");
        text.Append("layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n");
        text.Append(sprite
            ? "layout(rgba16f, set = 0, binding = 0) uniform restrict image2D xr_target;\n"
            : "layout(rgba16f, set = 0, binding = 0) uniform restrict writeonly image2D xr_target;\n");
        text.Append("layout(set = 0, binding = 1, std430) restrict readonly buffer XrConstants { vec4 v[]; } xr_constants;\n");
        text.Append("layout(set = 0, binding = 2, std430) restrict readonly buffer XrState { float v[]; } xr_state;\n");
        if (sprite)
        {
            text.Append("layout(set = 0, binding = 3, std430) restrict readonly buffer XrFrame { vec4 v[]; } xr_frame;\n");
            text.Append("layout(set = 0, binding = 4, std430) restrict buffer XrCounters { uint n[]; } xr_counters;\n");
            text.Append("layout(set = 0, binding = 5) uniform sampler2D xr_scene_depth;\n");
        }
        for (int i = 0; i < samplers.Count; i++)
        {
            text.Append("layout(set = 0, binding = ").Append(firstSampler + i).Append(") uniform sampler2D ").Append(samplers[i]).Append(";\n");
        }
        text.Append(sprite
            ? "layout(push_constant, std430) uniform XrPush { vec4 rect; vec4 uv; vec4 params; ivec4 info; } xr_push;\n"
            : "layout(push_constant, std430) uniform XrPush { ivec2 size; vec2 pad; } xr_push;\n");
        text.Append(Prelude);
        if (sprite)
        {
            // The frame's pixel registers, the exposure lane resolved as xenos_common's
            // xr_const_view does for the main view.
            text.Append("vec4 xr_frame_const(int index) {\n");
            text.Append("\tvec4 value = xr_frame.v[index];\n");
            text.Append("\tif (value.w == -12345.0) { value.w = xr_state.v[").Append(Engine_PostFxState.ExposureFrame).Append("]; }\n");
            text.Append("\treturn value;\n}\n");
            text.Append("#define xr_const(registers, index) xr_frame_const(index)\n");
        }
        text.Append(helpers).Append('\n');
        text.Append(globals);
        text.Append(bodies);
        text.Append("#define XR_STORE(c) ").Append(store.Length == 0 ? "(c)" : store).Append('\n');
        text.Append("void main() {\n");
        if (sprite)
        {
            text.Append("\tivec2 xr_pixel = ivec2(gl_GlobalInvocationID.xy) + xr_push.info.xy;\n");
            text.Append("\tvec2 xr_at = vec2(xr_pixel) + 0.5;\n");
            text.Append("\tif (any(greaterThanEqual(xr_pixel, imageSize(xr_target))) || any(lessThan(xr_at, xr_push.rect.xy)) || any(greaterThanEqual(xr_at, xr_push.rect.zw))) { return; }\n");
            // Depth test against the scene (reversed Z: a larger value is nearer), no depth write.
            text.Append("\tif (textureLod(xr_scene_depth, xr_at * xr_push.params.y / vec2(textureSize(xr_scene_depth, 0)), 0.0).r > xr_push.params.x) { return; }\n");
        }
        else
        {
            text.Append("\tivec2 xr_pixel = ivec2(gl_GlobalInvocationID.xy);\n");
            text.Append("\tif (xr_pixel.x >= xr_push.size.x || xr_pixel.y >= xr_push.size.y) { return; }\n");
        }
        foreach ((string name, int count) in constants)
        {
            int at = registers[name];
            if (count == 1)
            {
                text.Append('\t').Append(name).Append(" = xr_constants.v[").Append(at).Append("];\n");
            }
            else
            {
                for (int k = 0; k < count; k++)
                {
                    text.Append('\t').Append(name).Append('[').Append(k).Append("] = xr_constants.v[").Append(at + k).Append("];\n");
                }
            }
        }
        foreach ((string name, string value) in fixups)
        {
            // A fixup for a constant this variant does not declare has nothing to set.
            if (registers.ContainsKey(name))
            {
                text.Append('\t').Append(name).Append(" = ").Append(value).Append(";\n");
            }
        }
        if (sprite)
        {
            // The quad's texture coordinate at this pixel (affine: the quad faces the camera).
            text.Append("\tvec2 xr_t = (xr_at - xr_push.rect.xy) / max(xr_push.rect.zw - xr_push.rect.xy, vec2(1e-6));\n");
            text.Append("\tUV = mix(xr_push.uv.xy, xr_push.uv.zw, xr_t);\n");
            text.Append("\tVERTEX = vec3(0.0);\n");
            text.Append("\txr_vertex_stage();\n");
            text.Append("\tFRAGCOORD = vec4(xr_at, 0.0, 1.0);\n");
            text.Append("\tALBEDO = vec3(0.0);\n\tALPHA = 1.0;\n");
            text.Append("\txr_pixel_stage();\n");
            text.Append("\tvec4 xr_out = XR_STORE(vec4(ALBEDO, ALPHA));\n");
            text.Append("\tif (xr_push.params.z >= 0.0 && !(xr_out.a > xr_push.params.z)) { return; }\n");
            text.Append("\tif (xr_push.info.z >= 0) { atomicAdd(xr_counters.n[xr_push.info.z], 1u); }\n");
            text.Append("\tvec4 xr_dst = imageLoad(xr_target, xr_pixel);\n");
            text.Append("\timageStore(xr_target, xr_pixel, xr_push.params.w > 0.5 ? vec4(xr_out.rgb * xr_out.a + xr_dst.rgb * (1.0 - xr_out.a), xr_dst.a) : vec4(xr_dst.rgb + xr_out.rgb, xr_dst.a));\n");
        }
        else
        {
            text.Append("\tvec2 xr_size = vec2(xr_push.size);\n");
            text.Append("\tVERTEX = vec3(2.0 * float(xr_pixel.x) / xr_size.x - 1.0, 0.0, -(1.0 - 2.0 * float(xr_pixel.y) / xr_size.y));\n");
            text.Append("\txr_vertex_stage();\n");
            text.Append("\tFRAGCOORD = vec4(vec2(xr_pixel) + 0.5, 0.0, 1.0);\n");
            text.Append("\tALBEDO = vec3(0.0);\n\tALPHA = 1.0;\n");
            text.Append("\txr_pixel_stage();\n");
            text.Append("\timageStore(xr_target, xr_pixel, XR_STORE(vec4(ALBEDO, ALPHA)));\n");
        }
        text.Append("}\n");

        code = text.ToString();
        return true;
    }

    /// <summary>Whether the pass declares a constant of this name.</summary>
    public bool Has(string name) => _constants.ContainsKey(name);

    /// <summary>Sets a one-register constant; a name the pass does not declare is ignored.</summary>
    public void Set(string name, float x, float y = 0f, float z = 0f, float w = 0f)
    {
        if (!_constants.TryGetValue(name, out (int Register, int Count) at))
        {
            return;
        }
        int o = at.Register * 4;
        _dirty |= _values[o] != x || _values[o + 1] != y || _values[o + 2] != z || _values[o + 3] != w;
        _values[o] = x;
        _values[o + 1] = y;
        _values[o + 2] = z;
        _values[o + 3] = w;
    }

    /// <summary>Sets a constant's registers from consecutive floats (four a register).</summary>
    public void Set(string name, ReadOnlySpan<float> values)
    {
        if (!_constants.TryGetValue(name, out (int Register, int Count) at))
        {
            return;
        }
        int o = at.Register * 4;
        int n = Math.Min(values.Length, at.Count * 4);
        for (int i = 0; i < n; i++)
        {
            _dirty |= _values[o + i] != values[i];
            _values[o + i] = values[i];
        }
    }

    /// <summary>
    /// Runs the pass over <paramref name="target"/> (<paramref name="size"/>). <paramref name="textures"/>
    /// holds a (sampler, texture) pair per entry of <see cref="Samplers"/>, in that order.
    /// </summary>
    public void Run(RenderingDevice rd, Rid target, Vector2I size, Rid state, ReadOnlySpan<(Rid Sampler, Rid Texture)> textures)
    {
        if (Engine_RdHealth.IsDeviceLost || !_pipeline.IsValid || !target.IsValid || !state.IsValid)
        {
            return;
        }

        Upload(rd);

        bool same = _set.IsValid && rd.UniformSetIsValid(_set) && _bound[0] == target && _bound[1] == state;
        for (int i = 0; same && i < _samplers.Count; i++)
        {
            same = _bound[3 + i * 2] == textures[i].Sampler && _bound[4 + i * 2] == textures[i].Texture;
        }
        if (!same)
        {
            if (_set.IsValid && rd.UniformSetIsValid(_set))
            {
                rd.FreeRid(_set);
            }
            var uniforms = new Array<RDUniform>();
            var image = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
            image.AddId(target);
            uniforms.Add(image);
            var constants = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 1 };
            constants.AddId(_constantsBuffer);
            uniforms.Add(constants);
            var post = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 2 };
            post.AddId(state);
            uniforms.Add(post);
            for (int i = 0; i < _samplers.Count; i++)
            {
                var sampler = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = 3 + i };
                sampler.AddId(textures[i].Sampler);
                sampler.AddId(textures[i].Texture);
                uniforms.Add(sampler);
                _bound[3 + i * 2] = textures[i].Sampler;
                _bound[4 + i * 2] = textures[i].Texture;
            }
            _set = rd.UniformSetCreate(uniforms, _shader, 0);
            GC.KeepAlive(uniforms);
            _bound[0] = target;
            _bound[1] = state;
        }

        BitConverter.TryWriteBytes(_push.AsSpan(0), size.X);
        BitConverter.TryWriteBytes(_push.AsSpan(4), size.Y);
        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipeline);
        rd.ComputeListBindUniformSet(list, _set, 0);
        rd.ComputeListSetPushConstant(list, _push, (uint)_push.Length);
        rd.ComputeListDispatch(list, (uint)((size.X + 7) / 8), (uint)((size.Y + 7) / 8), 1);
        rd.ComputeListEnd();
    }

    /// <summary>
    /// Draws one quad of a sprite pass into <paramref name="target"/>: <paramref name="frame"/> the
    /// frame's pixel registers (vec4 x 256), <paramref name="counters"/> the occlusion counters,
    /// <paramref name="depth"/> the scene depth, <paramref name="textures"/> per <see cref="Samplers"/>.
    /// </summary>
    public void RunSprite(RenderingDevice rd, Rid target, Rid state, Rid frame, Rid counters,
        (Rid Sampler, Rid Texture) depth, ReadOnlySpan<(Rid Sampler, Rid Texture)> textures, in SpriteDraw draw)
    {
        if (Engine_RdHealth.IsDeviceLost || !_pipeline.IsValid || !target.IsValid || !state.IsValid)
        {
            return;
        }

        int x0 = Math.Max((int)MathF.Floor(draw.Rect.X), 0), y0 = Math.Max((int)MathF.Floor(draw.Rect.Y), 0);
        int x1 = (int)MathF.Ceiling(draw.Rect.Z), y1 = (int)MathF.Ceiling(draw.Rect.W);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }
        Upload(rd);

        bool same = _set.IsValid && rd.UniformSetIsValid(_set) && _bound[0] == target && _bound[1] == state &&
            _bound[2] == frame && _bound[3] == counters && _bound[4] == depth.Sampler && _bound[5] == depth.Texture;
        for (int i = 0; same && i < _samplers.Count; i++)
        {
            same = _bound[6 + i * 2] == textures[i].Sampler && _bound[7 + i * 2] == textures[i].Texture;
        }
        if (!same)
        {
            Unbind(rd);
            var uniforms = new Array<RDUniform>
            {
                Uniform(RenderingDevice.UniformType.Image, 0, target),
                Uniform(RenderingDevice.UniformType.StorageBuffer, 1, _constantsBuffer),
                Uniform(RenderingDevice.UniformType.StorageBuffer, 2, state),
                Uniform(RenderingDevice.UniformType.StorageBuffer, 3, frame),
                Uniform(RenderingDevice.UniformType.StorageBuffer, 4, counters),
                Uniform(RenderingDevice.UniformType.SamplerWithTexture, 5, depth.Sampler, depth.Texture),
            };
            for (int i = 0; i < _samplers.Count; i++)
            {
                uniforms.Add(Uniform(RenderingDevice.UniformType.SamplerWithTexture, 6 + i, textures[i].Sampler, textures[i].Texture));
                _bound[6 + i * 2] = textures[i].Sampler;
                _bound[7 + i * 2] = textures[i].Texture;
            }
            _set = rd.UniformSetCreate(uniforms, _shader, 0);
            GC.KeepAlive(uniforms);
            _bound[0] = target;
            _bound[1] = state;
            _bound[2] = frame;
            _bound[3] = counters;
            _bound[4] = depth.Sampler;
            _bound[5] = depth.Texture;
        }

        Span<float> push = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_spritePush.AsSpan());
        push[0] = draw.Rect.X; push[1] = draw.Rect.Y; push[2] = draw.Rect.Z; push[3] = draw.Rect.W;
        push[4] = draw.Uv.X; push[5] = draw.Uv.Y; push[6] = draw.Uv.Z; push[7] = draw.Uv.W;
        push[8] = draw.Depth; push[9] = draw.DepthScale; push[10] = draw.AlphaRef; push[11] = draw.Mix ? 1f : 0f;
        Span<int> info = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(_spritePush.AsSpan(48, 16));
        info[0] = x0; info[1] = y0; info[2] = draw.Counter; info[3] = 0;

        long list = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(list, _pipeline);
        rd.ComputeListBindUniformSet(list, _set, 0);
        rd.ComputeListSetPushConstant(list, _spritePush, (uint)_spritePush.Length);
        rd.ComputeListDispatch(list, (uint)((x1 - x0 + 7) / 8), (uint)((y1 - y0 + 7) / 8), 1);
        rd.ComputeListEnd();
    }

    private void Upload(RenderingDevice rd)
    {
        if (_dirty)
        {
            Buffer.BlockCopy(_values, 0, _bytes, 0, _bytes.Length);
            rd.BufferUpdate(_constantsBuffer, 0, (uint)_bytes.Length, _bytes);
            _dirty = false;
        }
    }

    private static RDUniform Uniform(RenderingDevice.UniformType type, int binding, Rid first, Rid second = default)
    {
        var uniform = new RDUniform { UniformType = type, Binding = binding };
        uniform.AddId(first);
        if (second.IsValid)
        {
            uniform.AddId(second);
        }
        return uniform;
    }

    /// <summary>Drops the uniform set: a bound texture is about to be freed.</summary>
    public void Unbind(RenderingDevice rd)
    {
        if (_set.IsValid && rd.UniformSetIsValid(_set))
        {
            rd.FreeRid(_set);
        }
        _set = default;
        System.Array.Clear(_bound);
    }

    public void Free(RenderingDevice rd)
    {
        Unbind(rd);
        foreach (Rid rid in new[] { _pipeline, _shader, _constantsBuffer })
        {
            if (rid.IsValid)
            {
                rd.FreeRid(rid);
            }
        }
        _pipeline = default;
        _shader = default;
        _constantsBuffer = default;
    }

    /// <summary>
    /// The Godot names a translated stage body uses, as a compute invocation has them: one view,
    /// no object or camera matrix (VS 0156 passes its position through), no alpha test, no shadow
    /// pass, an explicit level 0 for every fetch (a compute shader has no derivatives; the post
    /// targets have no mips).
    /// </summary>
    private const string Prelude =
        "#define texture(t, coord) textureLod(t, coord, 0.0)\n" +
        "#define PROJECTION_MATRIX mat4(1.0)\n" +
        "#define MODELVIEW_MATRIX mat4(1.0)\n" +
        "#define MODEL_MATRIX mat4(1.0)\n" +
        "#define VIEW_MATRIX mat4(1.0)\n" +
        "#define CAMERA_VISIBLE_LAYERS 1u\n" +
        "#define FRONT_FACING true\n" +
        "#define XR_SPEC_CONSTANT_ALPHA_TEST 2u\n" +
        "#define XR_SHADOW_PASS_LAYER (1u << 23u)\n" +
        "#define XR_ALPHA_OUT(a) (a)\n" +
        "#define XR_COLOUR_OUT(c) (c).rgb\n" +
        "#define XR_BEFORE_OUTPUT(c)\n" +
        "#define XR_WRITE_ALPHA(a) ALPHA = (a)\n" +
        "#define XR_POSITION_OUT(p) (p)\n" +
        "#define XR_PARAM_GEN_ZW vec2(0.0)\n" +
        "#define XR_POINT_SIZE(s) (s)\n" +
        "const uint xr_booleans = 0u;\n" +
        "const uint xr_spec_constants = 0u;\n" +
        "const float xr_alpha_threshold = 0.0;\n" +
        "const uint xr_swapped_texcoords = 0u;\n" +
        "const bool xr_use_guest_position = true;\n" +
        "vec3 VERTEX;\nint VERTEX_ID = 0;\nvec2 UV;\nvec2 UV2;\nvec4 COLOR;\nvec3 NORMAL;\n" +
        "vec4 POSITION;\nfloat POINT_SIZE;\nvec4 FRAGCOORD;\nvec3 ALBEDO;\nfloat ALPHA;\n" +
        // The display's encoding: the title's last pass writes pow(c, gamma / 2.2) straight to its
        // 8-bit back buffer; Godot sRGB-encodes this target on output, so the composite decodes.
        "vec3 xr_srgb_decode(vec3 c) {\n" +
        "\treturn mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));\n" +
        "}\n";

    /// <summary>
    /// One stage's text as compute source: its declarations into <paramref name="globals"/>
    /// (samplers and constants collected instead), its processor function renamed into
    /// <paramref name="bodies"/>.
    /// </summary>
    private static bool Stage(string text, string processor, string rename, List<string> samplers,
        List<(string Name, int Count)> constants, HashSet<string> seen, StringBuilder globals,
        StringBuilder bodies, out string error)
    {
        error = string.Empty;
        bool inBody = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (!inBody && trimmed.StartsWith("void " + processor + "()", StringComparison.Ordinal))
            {
                inBody = true;
                bodies.Append("void ").Append(rename).Append("()\n");
                continue;
            }
            if (inBody)
            {
                bodies.Append(line.Replace("discard;", "return;", StringComparison.Ordinal)).Append('\n');
                continue;
            }
            if (trimmed.StartsWith("#include", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            if (trimmed.StartsWith("varying ", StringComparison.Ordinal))
            {
                globals.Append(trimmed["varying ".Length..]).Append('\n');
                continue;
            }
            bool instance = trimmed.StartsWith("instance uniform ", StringComparison.Ordinal);
            if (instance || trimmed.StartsWith("uniform ", StringComparison.Ordinal))
            {
                string declaration = trimmed[(instance ? "instance uniform ".Length : "uniform ".Length)..].TrimEnd(';').Trim();
                int colon = declaration.IndexOf(':');
                if (colon >= 0)
                {
                    declaration = declaration[..colon].Trim();
                }
                int equals = declaration.IndexOf('=');
                string initial = equals >= 0 ? declaration[(equals + 1)..].Trim() : string.Empty;
                if (equals >= 0)
                {
                    declaration = declaration[..equals].Trim();
                }
                int space = declaration.IndexOf(' ');
                if (space < 0)
                {
                    error = $"cannot read the declaration '{trimmed}'";
                    return false;
                }
                string type = declaration[..space];
                string name = declaration[(space + 1)..].Trim();
                int count = 1;
                int bracket = name.IndexOf('[');
                if (bracket >= 0)
                {
                    count = int.Parse(name[(bracket + 1)..name.IndexOf(']')], System.Globalization.CultureInfo.InvariantCulture);
                    name = name[..bracket];
                }
                if (!seen.Add(name))
                {
                    continue;
                }
                if (type == "sampler2D")
                {
                    samplers.Add(name);
                }
                else if (type == "vec4" && initial.Length == 0 || type == "vec4" && instance)
                {
                    constants.Add((name, count));
                    globals.Append("vec4 ").Append(name).Append(count > 1 ? $"[{count}]" : string.Empty).Append(";\n");
                }
                else if (initial.Length != 0)
                {
                    globals.Append("const ").Append(type).Append(' ').Append(name).Append(" = ").Append(initial).Append(";\n");
                }
                else
                {
                    error = $"no host binding for '{trimmed}'";
                    return false;
                }
                continue;
            }
            globals.Append(line).Append('\n');
        }
        if (!inBody)
        {
            error = $"no {processor}() in the stage";
            return false;
        }
        return true;
    }
}
