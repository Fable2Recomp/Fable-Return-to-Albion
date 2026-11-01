using System;
using System.IO;
using Godot;

namespace Refii.Textures;

/// <summary>
/// <see cref="Engine_TextureUpload.PrepareAhead"/>, with its BC to ASTC transcode kept on disk
/// across launches, for the pub-game boards' bitmaps.
/// </summary>
public static class AndroidAstcDiskCache
{
    /// <summary>
    /// <see cref="Engine_TextureUpload.PrepareAhead"/>, with its BC to ASTC transcode kept on disk
    /// across launches: <c>astc_&lt;hash&gt;_&lt;format&gt;_&lt;size&gt;_&lt;build&gt;.bin</c> in
    /// <paramref name="directory"/>, keyed by the BC payload's own hash (<paramref name="digest"/>),
    /// its format, size and mipmaps, and the build (<paramref name="buildStamp"/>, which carries the
    /// transcoder).
    /// </summary>
    /// <remarks>Only an image the policy sends to ASTC is looked up; anything else, and any file
    /// that is missing, short or describes another image, is prepared as before. Only an ASTC
    /// result is written: the transcoder's RGBA8 fallback is not kept. On desktop the policy sends
    /// nothing to ASTC, so this is <see cref="Engine_TextureUpload.PrepareAhead"/> alone.</remarks>
    /// <param name="cached">Incremented, thread-safely, for each image read from the cache.</param>
    public static Image PrepareAhead(Image image, string directory, string buildStamp,
        Func<byte[], string> digest, ref int cached)
    {
        Image.Format source = image.GetFormat();
        if (Engine_TextureUpload.Policy.Target(source) != Image.Format.Astc4X4)
        {
            return Engine_TextureUpload.PrepareAhead(image);
        }

        int width = image.GetWidth(), height = image.GetHeight();
        bool mipmaps = image.HasMipmaps();
        string path = Path.Combine(directory,
            $"astc_{digest(image.GetData())}_{source}_{width}x{height}{(mipmaps ? "m" : string.Empty)}_{buildStamp}.bin");
        byte[]? data = Engine_PreparedImageFile.TryRead(
            path, width, height, (int)Image.Format.Astc4X4, mipmaps);
        if (data is not null)
        {
            var read = new Image();
            read.SetData(width, height, mipmaps, Image.Format.Astc4X4, data);
            if (!read.IsEmpty())
            {
                System.Threading.Interlocked.Increment(ref cached);
                return read;
            }

            read.Dispose();
        }

        Image upload = Engine_TextureUpload.PrepareAhead(image);
        if (!ReferenceEquals(upload, image) && upload.GetFormat() == Image.Format.Astc4X4)
        {
            Engine_PreparedImageFile.TryWrite(
                path, width, height, (int)Image.Format.Astc4X4, mipmaps, upload.GetData());
        }

        return upload;
    }
}
