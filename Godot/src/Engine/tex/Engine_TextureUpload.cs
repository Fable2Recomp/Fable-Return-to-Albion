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

    /// <summary>True when the active RenderingServer driver is Direct3D 12.</summary>
    public static bool IsD3D12Driver => _isD3D12Driver ??=
        string.Equals(RenderingServer.GetCurrentRenderingDriverName(), "d3d12", StringComparison.OrdinalIgnoreCase);

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
        if (Layouts.TryGetValue(texture, out UploadLayout? layout))
        {
            return MatchesStorageLayout(
                layout.Width, layout.Height, layout.Format, layout.Mipmaps,
                image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps());
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
        if (Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return new ImageTexture();
        }

        return Engine_ServerSyncs.RunGpuUpload(
            image.GetDataSize(),
            () =>
            {
                if (Refii.Core.Engine_RdHealth.IsDeviceLost)
                {
                    return new ImageTexture();
                }

                if (Prepares)
                {
                    return AndroidTextureUpload.CreateTexture(image, world);
                }

                ImageTexture created = ImageTexture.CreateFromImage(image);
                if (!Refii.Core.Engine_RdHealth.CheckTextureValid(
                        created.GetRid(), "Engine_TextureUpload.CreateTexture", verifyRdBacking: IsD3D12Driver))
                {
                    return created;
                }

                Layouts.AddOrUpdate(created, new UploadLayout(
                    image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps()));
                return created;
            });
    }

    /// <summary>
    /// The image in the form <see cref="CreateTexture"/> would upload it, for a worker that makes
    /// the image ahead of its texture; anything already uploadable comes back as itself.
    /// </summary>
    /// <remarks>CPU work on a private image only, safe on any thread. The caller owns both images
    /// and disposes the source when a new one comes back.</remarks>
    public static Image PrepareAhead(Image image) => Prepares ? AndroidTextureUpload.PrepareAhead(image) : image;

    /// <summary>Replaces a 2D image using the same policy as its initial upload.</summary>
    /// <param name="texture">Caller-owned GPU texture.</param>
    /// <param name="image">Caller-owned replacement CPU image.</param>
    /// <param name="world">As <see cref="CreateTexture"/>.</param>
    public static void SetImage(ImageTexture texture, Image image, bool world = false)
    {
        if (Refii.Core.Engine_RdHealth.IsDeviceLost)
        {
            return;
        }

        Engine_ServerSyncs.RunGpuUpload(image.GetDataSize(), () =>
        {
            if (Refii.Core.Engine_RdHealth.IsDeviceLost)
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
            else
            {
                texture.SetImage(image);
                Layouts.AddOrUpdate(texture, new UploadLayout(
                    image.GetWidth(), image.GetHeight(), image.GetFormat(), image.HasMipmaps()));
            }

            _ = Refii.Core.Engine_RdHealth.CheckTextureValid(
                texture.GetRid(), "Engine_TextureUpload.SetImage", verifyRdBacking: IsD3D12Driver);
        });
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
                    texture.GetRid(), "Engine_TextureUpload.CreateLayered", verifyRdBacking: IsD3D12Driver);
            }
            GC.KeepAlive(images);
            return created;
        });
    }
}
