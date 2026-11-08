using System;
using System.Buffers.Binary;
using System.IO;

namespace Refii.Textures;

/// <summary>One prepared texture upload on disk: width, height, Godot image format, mipmaps and
/// the payload, as <see cref="Engine_TextureUpload.PrepareAhead"/> made it.</summary>
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

    /// <summary>2 added the payload checksum: a power loss can leave the right length and no data.</summary>
    private const int Version = 2;

    private const int HeaderSize = 4 + 4 + 4 + 4 + 4 + 1 + 4 + 8;

    /// <summary>A 64-bit FNV-1a over the payload's words, seeded with its length; far cheaper than the read.</summary>
    internal static ulong Checksum(ReadOnlySpan<byte> data)
    {
        ulong hash = 0xCBF29CE484222325UL ^ (ulong)data.Length;
        ReadOnlySpan<ulong> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(data);
        foreach (ulong word in words)
        {
            hash = (hash ^ word) * 0x100000001B3UL;
        }

        for (int i = words.Length * 8; i < data.Length; i++)
        {
            hash = (hash ^ data[i]) * 0x100000001B3UL;
        }

        return hash;
    }

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
        writer.Write(Checksum(data));
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

    /// <summary>Reads the record at <paramref name="path"/> whatever image it describes, into a pooled
    /// array the caller returns. False, with nothing to return, on any mismatch or failure.</summary>
    public static bool TryReadPooled(string path, out int width, out int height, out int format, out bool mipmaps,
        out byte[] payload, out int length)
    {
        width = height = format = length = 0;
        mipmaps = false;
        payload = Array.Empty<byte>();
        byte[]? rented = null;
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using Microsoft.Win32.SafeHandles.SafeFileHandle file = File.OpenHandle(path);
            Span<byte> header = stackalloc byte[HeaderSize];
            if (RandomAccess.Read(file, header, 0) != HeaderSize ||
                BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != Version)
            {
                return false;
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(header[21..]);
            if (size <= 0 || size != RandomAccess.GetLength(file) - HeaderSize)
            {
                return false;
            }

            rented = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
            int read = 0;
            while (read < size)
            {
                int got = RandomAccess.Read(file, rented.AsSpan(read, size - read), HeaderSize + read);
                if (got <= 0)
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    return false;
                }

                read += got;
            }

            if (Checksum(rented.AsSpan(0, size)) != BinaryPrimitives.ReadUInt64LittleEndian(header[25..]))
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                return false;
            }

            width = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            format = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            mipmaps = header[20] != 0;
            payload = rented;
            length = size;
            return true;
        }
        catch (Exception)
        {
            if (rented is not null && payload.Length == 0)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(rented);
            }

            return false;
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
            ulong checksum = reader.ReadUInt64();
            if (length <= 0 || length != stream.Length - HeaderSize)
            {
                return null;
            }

            byte[] data = reader.ReadBytes(length);
            return data.Length == length && Checksum(data) == checksum ? data : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
