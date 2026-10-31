using System;
using Godot;
using Godot.Collections;

namespace Refii.Core;

/// <summary>
/// The <see cref="RenderingDevice"/> calls that take a Godot collection, with that collection
/// built here, held until Godot has read it, then freed. Most compositor effects make their
/// device objects through these rather than with an inline <c>new Array&lt;…&gt;</c> or an
/// omitted collection argument.
/// </summary>
/// <remarks>
/// A collection whose only use is as the argument can be finalized while Godot still reads it
/// under NativeAOT; see <see cref="Engine_ArrayMeshes"/>' remarks. An omitted collection
/// argument is the same temporary, made by the binding. The phones crashed on the finalizer
/// thread in <c>Array::_unref</c> during world loads, a later <c>Array</c> landing on a block
/// such a temporary's early free had corrupted. Godot copies every collection here into its
/// own storage, so freeing ours after the call is safe. The <see cref="RefCounted"/> arguments
/// (formats, views, uniforms) are held too: Godot takes its own reference only once the call
/// is under way.
/// </remarks>
public static class Engine_RdCalls
{
    /// <summary>Shared empty arguments: Godot only reads them, and their refcount is atomic.</summary>
    private static readonly Array<RDPipelineSpecializationConstant> NoSpecialization = new();
    private static readonly Array<byte[]> NoData = new();

    /// <summary><see cref="RenderingDevice.UniformSetCreate"/> for <paramref name="uniforms"/>.</summary>
    public static Rid UniformSet(this RenderingDevice rd, RDUniform[] uniforms, Rid shader, uint set)
    {
        Array<RDUniform> array = ArrayOf(uniforms);
        // Array<T> in Godot 4.7.1 is not IDisposable; its untyped owner is the same native array.
        using var owner = (Godot.Collections.Array)array;
        Rid rid = rd.UniformSetCreate(array, shader, set);
        GC.KeepAlive(uniforms);
        return rid;
    }

    /// <summary><see cref="UniformSetCacheRD.GetCache"/> for <paramref name="uniforms"/>.</summary>
    public static Rid CachedUniformSet(Rid shader, uint set, params RDUniform[] uniforms)
    {
        Array<RDUniform> array = ArrayOf(uniforms);
        using var owner = (Godot.Collections.Array)array;
        Rid rid = UniformSetCacheRD.GetCache(shader, set, array);
        GC.KeepAlive(uniforms);
        return rid;
    }

    /// <summary><see cref="RenderingDevice.TextureCreate"/>, with <paramref name="data"/> as its one layer when given.</summary>
    public static Rid Texture(this RenderingDevice rd, RDTextureFormat format, RDTextureView view, byte[]? data = null)
    {
        Array<byte[]> layers = data is null ? NoData : new Array<byte[]> { data };
        Rid rid = rd.TextureCreate(format, view, layers);
        if (data is not null)
        {
            ((Godot.Collections.Array)layers).Dispose();
        }
        GC.KeepAlive(layers);
        GC.KeepAlive(format);
        GC.KeepAlive(view);
        return rid;
    }

    /// <summary><see cref="RenderingDevice.ComputePipelineCreate"/> with no specialization constants.</summary>
    public static Rid ComputePipeline(this RenderingDevice rd, Rid shader)
    {
        Rid rid = rd.ComputePipelineCreate(shader, NoSpecialization);
        GC.KeepAlive(NoSpecialization);
        return rid;
    }

    /// <summary>
    /// <see cref="RenderingDevice.RenderPipelineCreate"/> with no dynamic state and no
    /// specialization constants, its state objects held through the call.
    /// </summary>
    public static Rid RenderPipeline(this RenderingDevice rd, Rid shader, long framebufferFormat, long vertexFormat,
        RenderingDevice.RenderPrimitive primitive, RDPipelineRasterizationState rasterization,
        RDPipelineMultisampleState multisample, RDPipelineDepthStencilState depthStencil,
        RDPipelineColorBlendState blend)
    {
        Rid rid = rd.RenderPipelineCreate(shader, framebufferFormat, vertexFormat, primitive, rasterization,
            multisample, depthStencil, blend, 0, 0, NoSpecialization);
        GC.KeepAlive(rasterization);
        GC.KeepAlive(multisample);
        GC.KeepAlive(depthStencil);
        GC.KeepAlive(blend);
        return rid;
    }

    /// <summary><see cref="RenderingDevice.TextureCreateSharedFromSlice"/>, <paramref name="view"/> held through the call.</summary>
    public static Rid TextureSharedFromSlice(this RenderingDevice rd, RDTextureView view, Rid texture, uint layer,
        uint mipmap, uint mipmaps, RenderingDevice.TextureSliceType sliceType)
    {
        Rid rid = rd.TextureCreateSharedFromSlice(view, texture, layer, mipmap, mipmaps, sliceType);
        GC.KeepAlive(view);
        return rid;
    }

    /// <summary><see cref="RenderingDevice.SamplerCreate"/>, <paramref name="state"/> held through the call.</summary>
    public static Rid Sampler(this RenderingDevice rd, RDSamplerState state)
    {
        Rid rid = rd.SamplerCreate(state);
        GC.KeepAlive(state);
        return rid;
    }

    /// <summary><see cref="RenderingDevice.ShaderCompileSpirVFromSource"/>, <paramref name="source"/> held through the call.</summary>
    public static RDShaderSpirV CompileSpirV(this RenderingDevice rd, RDShaderSource source)
    {
        RDShaderSpirV spirv = rd.ShaderCompileSpirVFromSource(source);
        GC.KeepAlive(source);
        return spirv;
    }

    /// <summary><see cref="RenderingDevice.ShaderCreateFromSpirV"/>, <paramref name="spirv"/> held through the call.</summary>
    public static Rid Shader(this RenderingDevice rd, RDShaderSpirV spirv, string name = "")
    {
        Rid rid = rd.ShaderCreateFromSpirV(spirv, name);
        GC.KeepAlive(spirv);
        return rid;
    }

    /// <summary><see cref="RenderingDevice.FramebufferCreate"/> over <paramref name="textures"/>.</summary>
    public static Rid Framebuffer(this RenderingDevice rd, params Rid[] textures)
    {
        var array = new Array<Rid>();
        using var owner = (Godot.Collections.Array)array;
        foreach (Rid texture in textures)
        {
            array.Add(texture);
        }
        return rd.FramebufferCreate(array);
    }

    private static Array<RDUniform> ArrayOf(RDUniform[] uniforms)
    {
        var array = new Array<RDUniform>();
        foreach (RDUniform uniform in uniforms)
        {
            array.Add(uniform);
        }
        return array;
    }
}
