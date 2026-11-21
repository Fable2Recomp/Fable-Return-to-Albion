using Godot;

namespace Refii.Textures;

/// <summary>
/// How <see cref="AndroidTextureUpload.SetImage"/> writes a refreshed image: into the texture's
/// existing storage (<c>texture_2d_update</c>) when nothing about that storage changes, otherwise
/// by replacing it (<c>texture_replace</c>).
/// </summary>
/// <remarks>
/// A REPLACE FREES THE OLD DEVICE TEXTURE, and with it every uniform set that bound it: each
/// material sampling the texture rebuilds its set on the render thread. The sky's in-scatter table
/// is refreshed about 15 times a second and every world material samples it (it is a global), so
/// replacing it put about 300 ms of render-thread work into every frame on the Adreno 640s, with
/// the GPU idle behind it. Upstream #34 routed that refresh through Engine_TextureUpload.SetImage
/// (it called ImageTexture.Update before); the desktop branch there updates in place on the same
/// test (<see cref="Engine_TextureUpload.CanUpdateInPlace"/>), the Android branch always replaced.
///
/// ONLY AN UNCONVERTED IMAGE is updated in place. The caller owns it and keeps it past the queued
/// update (the sky retires its images two frames later); a transcoded copy is disposed by
/// SetImage straight after the call, which an update must not race. Converted images are texture
/// swaps, never per-frame refreshes, and keep the replace.
/// </remarks>
public static class AndroidTextureRefresh
{
    /// <summary>Writes <paramref name="prepared"/> into <paramref name="texture"/>, in place when allowed.</summary>
    public static void Write(ImageTexture texture, Image prepared, bool unconverted)
    {
        if (UpdatesInPlace(unconverted,
                texture.GetWidth(), texture.GetHeight(), texture.GetFormat(), texture.HasMipmaps(),
                prepared.GetWidth(), prepared.GetHeight(), prepared.GetFormat(), prepared.HasMipmaps()))
        {
            texture.Update(prepared);
        }
        else
        {
            texture.SetImage(prepared);
        }
    }

    /// <summary>Whether the refresh may update the existing storage instead of replacing it.</summary>
    public static bool UpdatesInPlace(bool unconverted,
        int currentWidth, int currentHeight, Image.Format currentFormat, bool currentMipmaps,
        int newWidth, int newHeight, Image.Format newFormat, bool newMipmaps) =>
        unconverted && currentWidth > 0 && currentHeight > 0 &&
        Engine_TextureUpload.MatchesStorageLayout(
            currentWidth, currentHeight, currentFormat, currentMipmaps,
            newWidth, newHeight, newFormat, newMipmaps);
}
