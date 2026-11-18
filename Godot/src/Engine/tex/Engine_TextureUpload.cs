using System;
using Godot;
using Refii.Core;

namespace Refii.Textures;

/// <summary>Creates and updates the engine's textures from decoded CPU images.</summary>
/// <remarks>Each call is Godot's own. On a GPU that cannot sample a game format, the image is
/// first prepared into one it can (<see cref="AndroidTextureUpload"/>, once its policy has been
/// captured); the caller's image is never modified either way.</remarks>
public static class Engine_TextureUpload
{
    /// <summary>The captured upload policy; on desktop every format passes through unchanged.</summary>
    public static AndroidTextureUploadPolicy Policy => AndroidTextureUpload.Policy;

    /// <summary>Whether uploads go through <see cref="AndroidTextureUpload"/>'s preparation.</summary>
    private static bool Prepares => Engine_Platform.IsAndroid && AndroidTextureUpload.Policy.Android;

    private sealed record UploadLayout(int Width, int Height, Image.Format Format, bool Mipmaps);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageTexture, UploadLayout> Layouts = new();
    private static bool? _isD3D12Driver;
    private static bool? _isSeparateRenderThread;

    /// <summary>True when the active RenderingServer driver is Direct3D 12.</summary>
    public static bool IsD3D12Driver => _isD3D12Driver ??=
        !Refii.Core.Engine_RdHealth.IsTestHost &&
        string.Equals(RenderingServer.GetCurrentRenderingDriverName(), "d3d12", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when command-line arguments request <c>--render-thread separate</c>.</summary>
    public static bool HasSeparateRenderThreadArgs(System.Collections.Generic.IReadOnlyList<string>? args)
    {
        if (args is null)
        {
            return false;
        }

        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], "--render-thread", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(args[i + 1], "separate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when Godot is running with a dedicated separate render thread.</summary>
    public static bool IsSeparateRenderThread => _isSeparateRenderThread ??=
        !Refii.Core.Engine_RdHealth.IsTestHost &&
        (HasSeparateRenderThreadArgs(OS.GetCmdlineArgs()) ||
         ProjectSettings.GetSetting("rendering/driver/threads/thread_model", 1).AsInt32() == 2);

    /// <summary>
    /// Pure helper controlling synchronous <c>TextureGetRdTexture</c> verification: enabled on
    /// D3D12 only when running single-threaded or already on the render thread (avoiding
    /// <c>push_and_ret</c> round-trips across threads when <c>--render-thread separate</c> is active).
    /// </summary>
    public static bool ShouldVerifyRdBackingSynchronously(bool isD3D12, bool isSeparateRenderThread, bool isOnRenderThread) =>
        isD3D12 && (!isSeparateRenderThread || isOnRenderThread);

    /// <summary>Whether the caller thread should synchronously probe RD texture backing after upload.</summary>
    public static bool VerifyRdBackingOnCallerThread =>
        !Refii.Core.Engine_RdHealth.IsTestHost &&
        ShouldVerifyRdBackingSynchronously(IsD3D12Driver, IsSeparateRenderThread, RenderingServer.IsOnRenderThread());

    /// <summary>
    /// Pure helper verifying exact storage parity (<c>width, height, format, mipmaps</c>) required
    /// for an in-place <c>RS::texture_2d_update</c> without reallocating/replacing an <c>RD::Texture</c>.
    /// </summary>
    public static bool MatchesStorageLayout(
        int currentWidth, int currentHeight, Image.Format currentFormat, bool currentMipmaps,
        int newWidth, int newHeight, Image.Format newFormat, bool newMipmaps) =>
        currentWidth == newWidth &&
        currentHeight == newHeight &&
        currentFormat == newFormat &&
        currentMipmaps == newMipmaps;

    /// <summary>
    /// True when <paramref name="image"/> matches <paramref name="texture"/>'s uploaded dimensions,
    /// format, and mipmap state without performing any GPU readback.
    /// </summary>
    public static bool CanUpdateInPlace(ImageTexture texture, Image image)
    {
        if (texture is null || !GodotObject.IsInstanceValid(texture) || image is null || image.IsEmpty())
        {
            return false;
        }

        int newWidth = image.GetWidth();
        int newHeight = image.GetHeight();
        Image.Format newFormat = image.GetFormat();
        bool newMipmaps = image.HasMipmaps();

        if (Layouts.TryGetValue(texture, out UploadLayout? layout))
        {
            return MatchesStorageLayout(
                layout.Width, layout.Height, layout.Format, layout.Mipmaps,
                newWidth, newHeight, newFormat, newMipmaps);
        }

        int currentWidth = texture.GetWidth();
        int currentHeight = texture.GetHeight();
        if (currentWidth <= 0 || currentHeight <= 0)
        {
            return false;
        }

        if (MatchesStorageLayout(
                currentWidth, currentHeight, texture.GetFormat(), texture.HasMipmaps(),
                newWidth, newHeight, newFormat, newMipmaps))
        {
            Layouts.AddOrUpdate(texture, new UploadLayout(newWidth, newHeight, newFormat, newMipmaps));
            return true;
        }

        return false;
    }

    /// <summary>Creates a 2D texture from a caller-owned image.</summary>
    /// <param name="image">Caller-owned decoded image, which is never modified.</param>
    /// <returns>The ordinary Godot texture with capability-compatible storage.</returns>
    /// <param name="world">A world material's texture, which the low-memory tier may upload a size
    /// down; nothing that reads its own size to place itself may set this.</param>
    public static ImageTexture CreateTexture(Image image, bool world = false)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || image is null || image.IsEmpty())
        {
            return new ImageTexture();
        }

        return Engine_ServerSyncs.RunGpuUpload(
            image.GetDataSize(),
            () => CreateTextureOnMainThread(image, world));
    }

    /// <summary>
    /// Creates an <see cref="ImageTexture"/> directly on the main thread inside an active
    /// batched <see cref="Engine_ServerSyncs.RunGpuUpload"/> scope without double-counting bytes.
    /// </summary>
    internal static ImageTexture CreateTextureOnMainThread(Image image, bool world = false)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost || image is null || image.IsEmpty())
        {
            return new ImageTexture();
        }

        if (Prepares)
        {
            return AndroidTextureUpload.CreateTexture(image, world);
        }

        ImageTexture created = ImageTexture.CreateFromImage(image);
        if (!Refii.Core.Engine_RdHealth.CheckTextureValid(
                created.GetRid(), "Engine_TextureUpload.CreateTexture", verifyRdBacking: VerifyRdBackingOnCallerThread))
        {
            return created;
        }

        Layouts.AddOrUpdate(created, new UploadLayout(
            image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps()));
        return created;
    }

    /// <summary>
    /// The image in the form <see cref="CreateTexture"/> would upload it, for a worker that makes
    /// the image ahead of its texture; anything already uploadable comes back as itself.
    /// </summary>
    /// <remarks>CPU work on a private image only, safe on any thread. The caller owns both images
    /// and disposes the source when a new one comes back.</remarks>
    public static Image PrepareAhead(Image image) => Prepares ? AndroidTextureUpload.PrepareAhead(image) : image;

    /// <summary>Replaces or updates a 2D image using the same policy as its initial upload.</summary>
    /// <param name="texture">Caller-owned GPU texture.</param>
    /// <param name="image">Caller-owned replacement CPU image.</param>
    /// <param name="world">As <see cref="CreateTexture"/>.</param>
    /// <param name="allowD3D12Replace">
    /// When false on Direct3D 12, skips layout-changing <c>RS::texture_replace</c> on already
    /// initialized textures so live descriptor tables never reference a freed <c>ID3D12Resource</c>.
    /// </param>
    public static void SetImage(ImageTexture texture, Image image, bool world = false, bool allowD3D12Replace = true)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost ||
            texture is null || !GodotObject.IsInstanceValid(texture) ||
            image is null || image.IsEmpty())
        {
            return;
        }

        Engine_ServerSyncs.RunGpuUpload(image.GetDataSize(), () =>
        {
            if (Refii.Core.Engine_RdHealth.IsDeviceLost ||
                !GodotObject.IsInstanceValid(texture) ||
                image.IsEmpty())
            {
                return;
            }

            if (Prepares)
            {
                AndroidTextureUpload.SetImage(texture, image, world);
                return;
            }

            if (CanUpdateInPlace(texture, image))
            {
                texture.Update(image);
            }
            else if (!allowD3D12Replace && IsD3D12Driver && texture.GetWidth() > 0 && texture.GetHeight() > 0)
            {
                return;
            }
            else
            {
                texture.SetImage(image);
                Layouts.AddOrUpdate(texture, new UploadLayout(
                    image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps()));
            }

            _ = Refii.Core.Engine_RdHealth.CheckTextureValid(
                texture.GetRid(), "Engine_TextureUpload.SetImage", verifyRdBacking: VerifyRdBackingOnCallerThread);
        });
    }

    /// <summary>
    /// Updates an existing <see cref="ImageTexture"/> from a freshly allocated <see cref="Image"/> snapshot.
    /// </summary>
    /// <remarks>
    /// `--render-thread separate` / D3D12 warning: NEVER mutate a shared <see cref="Image"/> in place with
    /// <c>image.SetData(...)</c> before or after <c>texture.Update(image)</c>, and NEVER call
    /// <c>image.Dispose()</c> immediately after <c>texture.Update(image)</c>. Under <c>--render-thread separate</c>,
    /// <c>RS::texture_2d_update</c> queues a native <c>Ref&lt;Image&gt;</c> into <c>CommandQueueMT</c>; mutating or
    /// freeing that image while the render thread reads it triggers <c>_texture_2d_update: p_image-&gt;is_empty()</c>.
    /// </remarks>
    public static void UpdateFromData(
        ImageTexture? texture, int width, int height, bool useMipmaps, Image.Format format, byte[] data)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost ||
            texture is null || !GodotObject.IsInstanceValid(texture) ||
            width <= 0 || height <= 0 || data is null || data.Length == 0)
        {
            return;
        }

        Image image = Image.CreateFromData(width, height, useMipmaps, format, data);
        if (image is null || image.IsEmpty())
        {
            return;
        }

        SetImage(texture, image, allowD3D12Replace: false);
    }

    /// <summary>Uploads cube/array layers, leaving the source images intact.</summary>
    /// <param name="texture">A cubemap or 2D array owned by the caller.</param>
    /// <param name="images">Uniform source layers, left intact after upload.</param>
    /// <returns>Godot's normal layered creation result.</returns>
    /// <param name="world">As <see cref="CreateTexture"/>.</param>
    public static Error CreateLayered(ImageTextureLayered texture, Godot.Collections.Array<Image> images, bool world = false)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return Error.Failed;
        }

        long bytes = 0;
        for (int i = 0; i < images.Count; i++)
        {
            if (images[i] is { } layer)
            {
                bytes += layer.GetDataSize();
            }
        }

        return Engine_ServerSyncs.RunGpuUpload(bytes, () =>
        {
            if (Refii.Core.Engine_RdHealth.IsDeviceLost)
            {
                return Error.Failed;
            }
            if (Prepares) return AndroidTextureUpload.CreateLayered(texture, images, world);
            Error created = texture.CreateFromImages(images);
            if (created == Error.Ok)
            {
                _ = Refii.Core.Engine_RdHealth.CheckTextureValid(
                    texture.GetRid(), "Engine_TextureUpload.CreateLayered", verifyRdBacking: VerifyRdBackingOnCallerThread);
            }
            GC.KeepAlive(images);
            return created;
        });
    }
}
