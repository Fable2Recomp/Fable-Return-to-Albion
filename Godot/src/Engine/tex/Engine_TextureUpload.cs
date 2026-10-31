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

    /// <summary>Creates a 2D texture from a caller-owned image.</summary>
    /// <param name="image">Caller-owned decoded image, which is never modified.</param>
    /// <returns>The ordinary Godot texture with capability-compatible storage.</returns>
    public static ImageTexture CreateTexture(Image image) =>
        Prepares ? AndroidTextureUpload.CreateTexture(image) : ImageTexture.CreateFromImage(image);

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
    public static void SetImage(ImageTexture texture, Image image)
    {
        if (Prepares) AndroidTextureUpload.SetImage(texture, image);
        else texture.SetImage(image);
    }

    /// <summary>Uploads cube/array layers, leaving the source images intact.</summary>
    /// <param name="texture">A cubemap or 2D array owned by the caller.</param>
    /// <param name="images">Uniform source layers, left intact after upload.</param>
    /// <returns>Godot's normal layered creation result.</returns>
    public static Error CreateLayered(ImageTextureLayered texture, Godot.Collections.Array<Image> images)
    {
        if (Prepares) return AndroidTextureUpload.CreateLayered(texture, images);
        Error created = texture.CreateFromImages(images);
        GC.KeepAlive(images);
        return created;
    }
}
