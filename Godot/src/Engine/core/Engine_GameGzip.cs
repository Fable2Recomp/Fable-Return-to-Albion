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
}
