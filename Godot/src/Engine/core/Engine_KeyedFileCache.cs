using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Core;

/// <summary>
/// A file made from other data, kept on disk: its key (a SHA-256 of what it is made from and of
/// the build that makes it), a checksum of its bytes, then the bytes. Read back only when both
/// match, since a damaged or stale copy would be different game data.
/// </summary>
public static class Engine_KeyedFileCache
{
    private const int DigestLength = 32;

    /// <summary>The key for <paramref name="inputs"/>, made by <paramref name="maker"/>'s build in its <paramref name="format"/>.</summary>
    /// <param name="format">Raised by the maker when its output changes in a way the build stamp would not show.</param>
    /// <param name="maker">A type in the assembly whose build stamp goes into the key.</param>
    /// <param name="inputs">Everything the file is made from; a missing input is not an empty one.</param>
    public static byte[] Key(int format, Type maker, IEnumerable<byte[]?> inputs)
    {
        using var hash = Engine_Hash.CreateIncrementalSha256();
        void AddNumber(long value)
        {
            Span<byte> number = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, value);
            hash.AppendData(number);
        }

        AddNumber(format);
        hash.AppendData(maker.Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        foreach (byte[]? input in inputs)
        {
            AddNumber(input?.LongLength ?? -1);
            hash.AppendData(input);
        }

        return hash.GetHashAndReset();
    }

    /// <summary>The bytes kept in <paramref name="path"/>, when made from the same input and whole.</summary>
    public static bool TryRead(string path, byte[] key, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            if (!File.Exists(path)) return false;
            byte[] file = File.ReadAllBytes(path);
            int body = key.Length + DigestLength;
            if (file.Length < body || !file.AsSpan(0, key.Length).SequenceEqual(key) ||
                !file.AsSpan(key.Length, DigestLength).SequenceEqual(Engine_Hash.Sha256(file.AsSpan(body)))) return false;
            bytes = file.AsSpan(body).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Keeps <paramref name="bytes"/> under <paramref name="key"/>. Not being able to only means they are made again next time.</summary>
    public static void Write(string path, byte[] key, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream file = File.Create(temporary))
            {
                file.Write(key);
                file.Write(Engine_Hash.Sha256(bytes));
                file.Write(bytes);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
        }
    }
}
