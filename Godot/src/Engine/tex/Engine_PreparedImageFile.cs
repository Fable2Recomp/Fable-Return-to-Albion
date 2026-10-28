using System;
using System.IO;

namespace Refii.Textures;

/// <summary>One prepared texture upload on disk: width, height, Godot image format, mipmaps and
/// the payload, as <see cref="Engine_AndroidTextureUpload.PrepareAhead"/> made it.</summary>
/// <remarks>
/// Kept for the BC to ASTC transcode of the pub-game boards, so a cold board load reads the
/// ASTC payload instead of rewriting every block again. The caller keys the file name (source
/// bytes' hash, source format, size, build); this only reads and writes the record, and any
/// file it cannot read whole and as expected is a miss, never an exception, so the caller
/// transcodes as it would have. Written to a temporary file and renamed over the name, so a
/// reader never sees half a file and two workers writing the same name both leave a whole one.
/// </remarks>
public static class Engine_PreparedImageFile
{
    private const uint Magic = 0x41505752; // "RWPA"

    private const int Version = 1;

    private const int HeaderSize = 4 + 4 + 4 + 4 + 4 + 1 + 4;

    /// <summary>Writes the record to <paramref name="path"/> through a temporary file.</summary>
    /// <returns>False if it could not be written; nothing is left at the path then.</returns>
    public static bool TryWrite(string path, int width, int height, int format, bool mipmaps,
        ReadOnlySpan<byte> data)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                Write(stream, width, height, format, mipmaps, data);
            }

            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { File.Delete(temporary); } catch (Exception) { }
            return false;
        }
    }

    /// <summary>Writes the record to a stream.</summary>
    public static void Write(Stream stream, int width, int height, int format, bool mipmaps,
        ReadOnlySpan<byte> data)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(width);
        writer.Write(height);
        writer.Write(format);
        writer.Write(mipmaps);
        writer.Write(data.Length);
        writer.Write(data);
    }

    /// <summary>Reads the record at <paramref name="path"/> if it is whole and describes the
    /// image expected.</summary>
    /// <returns>The payload, or null on any mismatch or failure.</returns>
    public static byte[]? TryRead(string path, int width, int height, int format, bool mipmaps)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            return TryRead(stream, width, height, format, mipmaps);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads a record from a stream; null unless it is whole and matches.</summary>
    public static byte[]? TryRead(Stream stream, int width, int height, int format, bool mipmaps)
    {
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (stream.Length < HeaderSize || reader.ReadUInt32() != Magic || reader.ReadInt32() != Version ||
                reader.ReadInt32() != width || reader.ReadInt32() != height ||
                reader.ReadInt32() != format || reader.ReadBoolean() != mipmaps)
            {
                return null;
            }

            int length = reader.ReadInt32();
            if (length <= 0 || length != stream.Length - HeaderSize)
            {
                return null;
            }

            byte[] data = reader.ReadBytes(length);
            return data.Length == length ? data : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
