using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Refii.Text;

public sealed class TextBank
{
    private readonly Dictionary<uint, (uint Page, uint Offset)> _entries = new();

    private readonly Dictionary<uint, byte[]> _pages = new();

    public bool Loaded => _entries.Count != 0;

    public int TagCount => _entries.Count;

    /// <summary>The decompressed pages and the tag table, in bytes, for the heap census.</summary>
    internal long HeldBytes
    {
        get
        {
            long bytes = (long)_entries.Count * 24;
            foreach (byte[] page in _pages.Values)
            {
                bytes += page.LongLength;
            }

            return bytes;
        }
    }

    public static readonly string[] LanguageOrder =
    {
        "en-uk", "en-us", "en-gb", "en", "fr-fr", "de-de", "es-es", "it-it",
        "cs-cz", "hu-hu", "ko-kr", "pl-pl", "sk-sk", "zh-cht",
    };

    public static TextBank? FromBytes(byte[] bytes)
    {
        var bank = new TextBank();
        return bank.Parse(bytes) ? bank : null;
    }

    public static TextBank Load(Func<string, byte[]?> read)
    {
        var bank = new TextBank();

        // The culture in force and the guest's remap order first (Engine_GameCulture), then the
        // rest of the known cultures.
        var order = new List<string>(Engine_GameCulture.SearchOrder(Engine_GameCulture.Current));
        foreach (string language in LanguageOrder)
        {
            if (!order.Contains(language))
            {
                order.Add(language);
            }
        }

        foreach (string language in order)
        {
            string path = $"language/{language}/text/book.babel";
            byte[]? bytes = read(path) ?? read("data/" + path);
            if (bytes is not null && bank.Parse(bytes))
            {
                return bank;
            }
        }

        return bank;
    }

    public bool TryLookup(uint tag, out string text)
    {
        text = string.Empty;

        if (!_entries.TryGetValue(tag, out (uint Page, uint Offset) entry) ||
            !_pages.TryGetValue(entry.Page, out byte[]? blob))
        {
            return false;
        }

        long offset = entry.Offset;
        if (offset + 4 > blob.Length)
        {
            return false;
        }

        uint chars = ReadU32(blob, (int)offset);
        if (chars > (8u << 20) || offset + 4 + (long)chars * 2 > blob.Length)
        {
            return false;
        }

        text = Encoding.BigEndianUnicode.GetString(blob, (int)offset + 4, (int)chars * 2)
            .TrimEnd('\0');
        return text.Length != 0;
    }

    private bool Parse(byte[] d)
    {
        if (d.Length < 12)
        {
            return false;
        }

        uint count = ReadU32(d, 4);
        long o = 8;
        if (count > (1u << 24) || o + (long)count * 12 + 4 > d.Length)
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            uint tag = ReadU32(d, (int)o);
            uint page = ReadU32(d, (int)o + 4);
            uint offset = ReadU32(d, (int)o + 8);
            _entries.TryAdd(tag, (page, offset));
            o += 12;
        }

        uint pageCount = ReadU32(d, (int)o);
        o += 4;
        for (uint i = 0; i < pageCount; i++)
        {
            if (o + 12 > d.Length)
            {
                return false;
            }

            uint pageHash = ReadU32(d, (int)o);
            uint comp = ReadU32(d, (int)o + 4);
            uint dec = ReadU32(d, (int)o + 8);
            o += 12;
            if (o + comp > d.Length || dec > (64u << 20))
            {
                return false;
            }

            byte[]? blob = Inflate(d, (int)o, (int)comp, (int)dec);
            if (blob is null)
            {
                return false;
            }

            _pages[pageHash] = blob;
            o += comp;
        }

        return _entries.Count != 0;
    }

    private static byte[]? Inflate(byte[] src, int offset, int length, int expected)
    {
        if (length < 3)
        {
            return null;
        }

        try
        {
            using var input = new MemoryStream(src, offset + 2, length - 2);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var result = new byte[expected];
            int total = 0;
            while (total < expected)
            {
                int got = deflate.Read(result, total, expected - total);
                if (got <= 0)
                {
                    break;
                }

                total += got;
            }

            return total == expected ? result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static uint ReadU32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
}
