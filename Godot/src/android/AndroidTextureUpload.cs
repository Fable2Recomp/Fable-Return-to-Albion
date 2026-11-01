using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using Refii.Core;

namespace Refii.Textures;

/// <summary>Immutable sampled/update capabilities, captured before Android texture workers start.</summary>
/// <param name="Android">Whether Android capability adaptation is enabled.</param>
/// <param name="Bc1">BC1 RGB can be sampled and updated.</param>
/// <param name="Bc2">BC2 can be sampled and updated.</param>
/// <param name="Bc3">BC3 can be sampled and updated.</param>
/// <param name="Bc4">BC4 can be sampled and updated.</param>
/// <param name="Bc5">BC5 can be sampled and updated.</param>
/// <param name="Rgb8">Three-channel uncompressed RGB8 can be sampled and updated.</param>
/// <param name="Astc4x4">ASTC4x4 UNORM can be sampled and updated without renderer fallback.</param>
/// <param name="TranscodeAstc">BC1/BC3 without hardware support go to ASTC4x4 rather than RGBA8
/// (`android_astc_textures`, on by default; only meaningful with <paramref name="Astc4x4"/>).</param>
public sealed record AndroidTextureUploadPolicy(bool Android, bool Bc1, bool Bc2,
    bool Bc3, bool Bc4, bool Bc5, bool Rgb8, bool Astc4x4 = false, bool TranscodeAstc = true)
{
    /// <summary>Captures each required format once; desktop policy never invokes a device query.</summary>
    /// <param name="android">Enables the capability-dependent Android preparation.</param>
    /// <param name="supported">Tests the renderer's sampled/update usage for one data format.</param>
    /// <returns>A thread-safe immutable policy, with no retained renderer object.</returns>
    /// <param name="transcodeAstc">Whether unsupported BC1/BC3 may become ASTC4x4.</param>
    public static AndroidTextureUploadPolicy Capture(bool android,
        Func<RenderingDevice.DataFormat, bool> supported, bool transcodeAstc = true) => android
        ? new(true, supported(RenderingDevice.DataFormat.Bc1RgbUnormBlock),
            supported(RenderingDevice.DataFormat.Bc2UnormBlock),
            supported(RenderingDevice.DataFormat.Bc3UnormBlock),
            supported(RenderingDevice.DataFormat.Bc4UnormBlock),
            supported(RenderingDevice.DataFormat.Bc5UnormBlock),
            supported(RenderingDevice.DataFormat.R8G8B8Unorm),
            supported(RenderingDevice.DataFormat.Astc4X4UnormBlock), transcodeAstc)
        : new(false, true, true, true, true, true, true);

    /// <summary>Chooses precisely Godot's fallback storage channels for unsupported game formats.</summary>
    /// <param name="source">Decoded or cache-loaded CPU image format.</param>
    /// <returns>The same format for supported/desktop paths, otherwise ASTC4x4 for BC1/BC3 where
    /// it is sampled (<see cref="Engine_AstcTranscode"/>), else RGBA8, R8 or RG8.</returns>
    public Image.Format Target(Image.Format source) => !Android ? source : source switch
    {
        Image.Format.Dxt1 when !Bc1 => Astc ? Image.Format.Astc4X4 : Image.Format.Rgba8,
        Image.Format.Dxt3 when !Bc2 => Image.Format.Rgba8,
        Image.Format.Dxt5 when !Bc3 => Astc ? Image.Format.Astc4X4 : Image.Format.Rgba8,
        Image.Format.RgtcR when !Bc4 => Image.Format.R8,
        Image.Format.RgtcRg when !Bc5 => Image.Format.Rg8,
        Image.Format.Rgb8 when !Rgb8 => Image.Format.Rgba8,
        _ => source,
    };

    private bool Astc => Astc4x4 && TranscodeAstc;
}

/// <summary>Prepares unsupported Android texture formats before entering renderer validation.</summary>
/// <remarks>Godot 4.7.1's automatic BC fallback emits a warning that synchronously captures C#
/// backtraces while holding its global script-language mutex. Preparing the same pixels and
/// channels avoids those repeated worker warning callbacks under memory pressure. The captured
/// S10e stall disappeared with this adaptation; the precise GC wait cycle was not proven.
/// Errors remain enabled. Supported BC images
/// retain their compressed upload. Only temporary duplicates change, preserving source/cache
/// images and existing alpha metadata. No RenderingDevice query occurs on texture workers.
/// Shared code reaches the upload calls through <see cref="Engine_TextureUpload"/>, once
/// <see cref="Policy"/> is the captured Android one.</remarks>
public static class AndroidTextureUpload
{
    private static AndroidTextureUploadPolicy _policy = new(false, true, true, true, true, true, true);
    private static int _initialized;
    private static long _prepared;
    private static readonly Engine_TextureUploadCensus Census = new();

    /// <summary>Cumulative successful upload payloads; these are not resident GPU allocations.</summary>
    public static TextureUploadTotals[] Uploads => Census.Snapshot();

    /// <summary>The immutable policy also serialized into private device evidence.</summary>
    public static AndroidTextureUploadPolicy Policy => Volatile.Read(ref _policy);

    /// <summary>Number of unsupported images prepared, including layered texture slices.</summary>
    public static long PreparedImages => Interlocked.Read(ref _prepared);

    /// <summary>Queries a local renderer once on its creating main thread, then releases it.</summary>
    /// <remarks>A local device permits main-thread capability queries without using the global
    /// renderer's thread-affine device. Its GPU and driver match the active Vulkan renderer.
    /// An unavailable local device conservatively prepares the listed formats on Android.</remarks>
    public static void InitializeOnMainThread()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        if (!Refii.Core.Engine_Platform.IsAndroid) return;
        using RenderingDevice? device = RenderingServer.CreateLocalRenderingDevice();
        const RenderingDevice.TextureUsageBits usage =
            RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.CanUpdateBit;
        bool transcode = true;
        try
        {
            transcode = Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), "android_astc_textures", true);
        }
        catch (Exception) { }
        Volatile.Write(ref _policy, AndroidTextureUploadPolicy.Capture(true,
            format => device?.TextureIsFormatSupportedForUsage(format, usage) ?? false, transcode));
    }

    /// <summary>Creates a 2D texture, releasing any private fallback image after Godot copies it.</summary>
    /// <param name="image">Caller-owned decoded image, which is never modified.</param>
    /// <returns>The ordinary Godot texture with capability-compatible storage.</returns>
    public static ImageTexture CreateTexture(Image image)
    {
        Image prepared = Prepare(image);
        try
        {
            // A transcode or expand that came back empty would raise Godot's
            // `p_image.is_null() || p_image->is_empty()` with no name attached; upload the
            // decoded image as it is and say which one it was.
            if (prepared.IsEmpty() && !image.IsEmpty())
            {
                GD.PushWarning($"[tex] upload prepared an EMPTY image from {image.GetWidth()}x{image.GetHeight()} " +
                    $"{image.GetFormat()} (mips {image.HasMipmaps()}); uploading the decoded image instead");
                prepared.Dispose();
                prepared = image;
            }

            ImageTexture texture = ImageTexture.CreateFromImage(prepared);
            RecordUpload(TextureUploadKind.Create2D, image, prepared);
            return texture;
        }
        finally { if (!ReferenceEquals(prepared, image)) prepared.Dispose(); }
    }

    /// <summary>
    /// The image in the form <see cref="CreateTexture"/> would upload it, for a worker that makes
    /// the image ahead of its texture: an unsupported BC image comes back transcoded (ASTC) or
    /// expanded, anything else as itself. CreateTexture then finds nothing left to do.
    /// </summary>
    /// <remarks>CPU work on a private image only, no renderer object: safe on any thread once the
    /// policy is captured (<see cref="InitializeOnMainThread"/>); before that it changes nothing.
    /// The caller owns both images and disposes the source when a new one comes back.</remarks>
    public static Image PrepareAhead(Image image) => Prepare(image);

    /// <summary>Replaces a 2D image using the same capability policy as its initial upload.</summary>
    /// <param name="texture">Caller-owned GPU texture.</param>
    /// <param name="image">Caller-owned replacement CPU image.</param>
    public static void SetImage(ImageTexture texture, Image image)
    {
        Image prepared = Prepare(image);
        try
        {
            // As in CreateTexture: an empty prepared image is named here, never handed to
            // the server (`_texture_2d_update` refuses it without saying which texture).
            if (prepared.IsEmpty())
            {
                GD.PushWarning($"[tex] replace prepared an EMPTY image from {image.GetWidth()}x{image.GetHeight()} " +
                    $"{image.GetFormat()} (mips {image.HasMipmaps()}); the texture keeps its previous image");
                if (image.IsEmpty()) return;
                prepared.Dispose();
                prepared = image;
            }

            texture.SetImage(prepared);
            RecordUpload(TextureUploadKind.Replace2D, image, prepared);
        }
        finally { if (!ReferenceEquals(prepared, image)) prepared.Dispose(); }
    }

    /// <summary>Uploads cube/array layers with uniform fallback formats and preserved mip chains.</summary>
    /// <param name="texture">A cubemap or 2D array owned by the caller.</param>
    /// <param name="images">Uniform source layers, left intact after upload.</param>
    /// <returns>Godot's normal layered creation result.</returns>
    public static Error CreateLayered(ImageTextureLayered texture, Godot.Collections.Array<Image> images)
    {
        var temporary = new List<Image>();
        var prepared = new Godot.Collections.Array<Image>();
        // Array<T> exposes its same underlying native Array through this conversion;
        // only the untyped owner implements IDisposable in Godot 4.7.1.
        using Godot.Collections.Array preparedStorage = (Godot.Collections.Array)prepared;
        try
        {
            foreach (Image image in images)
            {
                Image next = Prepare(image);
                if (!ReferenceEquals(next, image)) temporary.Add(next);
                prepared.Add(next);
            }
            Error result = texture.CreateFromImages(prepared);
            if (result == Error.Ok)
                for (int i = 0; i < images.Count; i++) RecordUpload(TextureUploadKind.Layer, images[i], prepared[i]);
            return result;
        }
        finally { foreach (Image image in temporary) image.Dispose(); }
    }

    /// <summary>Captures only native scalar metadata after a successful Android upload.</summary>
    /// <param name="kind">The upload operation just completed.</param>
    /// <param name="source">Borrowed, unchanged caller image.</param>
    /// <param name="prepared">Borrowed image passed to the renderer.</param>
    private static void RecordUpload(TextureUploadKind kind, Image source, Image prepared)
    {
        if (!Policy.Android) return;
        Census.Record(kind, source.GetFormat(), prepared.GetFormat(), source.GetDataSize(),
            prepared.GetDataSize(), prepared.GetWidth(), prepared.GetHeight());
    }

    /// <summary>Duplicates only unsupported inputs and performs the renderer's equivalent conversion.</summary>
    /// <param name="image">An image with its original decoded/cache representation.</param>
    /// <returns>The input itself if supported; otherwise a new caller-disposed compatible image.</returns>
    private static Image Prepare(Image image)
    {
        AndroidTextureUploadPolicy policy = Policy;
        if (!policy.Android) return image;
        Image.Format source = image.GetFormat();
        Image.Format target = policy.Target(source);
        if (source == target) return image;
        if (target == Image.Format.Astc4X4)
        {
            Image? astc = Transcode(image, source == Image.Format.Dxt5);
            if (astc is not null) { Interlocked.Increment(ref _prepared); return astc; }
            target = Image.Format.Rgba8;
        }
        Image copy = (Image)image.Duplicate();
        try
        {
            if (copy.IsCompressed())
            {
                Error result = copy.Decompress();
                if (result != Error.Ok)
                    throw new InvalidOperationException($"Android texture decompression failed: {source}, {result}");
            }
            copy.Convert(target);
            Interlocked.Increment(ref _prepared);
            return copy;
        }
        catch { copy.Dispose(); throw; }
    }

    /// <summary>A new ASTC4x4 image of the same size and mip chain, or null if the payload is not
    /// whole blocks (the caller then falls back to RGBA8).</summary>
    /// <remarks>Both formats store every level, down to 1x1, as whole 4x4 blocks in the same order,
    /// so the chain maps block for block without walking its levels.</remarks>
    private static Image? Transcode(Image image, bool bc3)
    {
        byte[] blocks = image.GetData();
        int length = blocks.Length / (bc3 ? 16 : 8) * 16;
        // Pooled: SetData copies it, and a fresh array per texture is large-object garbage that
        // only a gen2 collection returns (measured: the NativeAOT heap 500-970 MB vs 240-310 without).
        byte[] astc = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
        try
        {
            if (!Engine_AstcTranscode.Transcode(blocks, bc3, astc.AsSpan(0, length))) return null;
            var result = new Image();
            result.SetData(image.GetWidth(), image.GetHeight(), image.HasMipmaps(), Image.Format.Astc4X4,
                new ReadOnlySpan<byte>(astc, 0, length));
            if (!result.IsEmpty()) return result;
            result.Dispose();
            return null;
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(astc); }
    }
}
