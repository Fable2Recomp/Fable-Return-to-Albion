using System;
using System.IO;
using System.IO.Compression;

namespace Refii.Core;

public static class Engine_GameGzip
{
    public static bool IsGzip(byte[]? bytes) =>
        bytes is not null && bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;

    public static byte[]? MaybeGunzip(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        byte[] result = bytes;
        MaybeGunzip(ref result);
        return result;
    }

    public static bool MaybeGunzip(ref byte[] bytes)
    {
        if (!IsGzip(bytes))
        {
            return false;
        }

        if (TryGunzipToStatedSize(bytes) is { } sized)
        {
            bytes = sized;
            return true;
        }

        try
        {
            using var source = new MemoryStream(bytes);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            bytes = output.ToArray();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Inflates into one array of the size the gzip trailer states, so there are no growing buffers
    /// and no final copy. Null unless the stream ends exactly there: several members, or a trailer
    /// that lies, go through the general path above.
    /// </summary>
    private static byte[]? TryGunzipToStatedSize(byte[] bytes)
    {
        const int MinimumGzip = 18;
        // Deflate can reach 1032:1, but a trailer is only trusted to size the buffer up to 64:1. A
        // truncated file, or one that starts like gzip and is not, can state anything in its last
        // four bytes; past this ratio the general path reads what is really there.
        const long TrustedRatio = 64;

        if (bytes.Length < MinimumGzip)
        {
            return null;
        }

        uint stated = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(^4));
        if (stated == 0 || stated > bytes.Length * TrustedRatio || stated > int.MaxValue)
        {
            return null;
        }

        try
        {
            using var source = new MemoryStream(bytes);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            var output = new byte[stated];
            gzip.ReadExactly(output);
            return gzip.ReadByte() == -1 ? output : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (OutOfMemoryException)
        {
            // A stated size the heap cannot give: the general path grows only as far as the data goes.
            return null;
        }
    }
}
