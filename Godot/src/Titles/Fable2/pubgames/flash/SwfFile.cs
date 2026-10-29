using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Refii.Titles.Fable2.PubGames;

public readonly record struct SwfTag(int Code, int Offset, byte[] Data);

public readonly record struct SwfSymbol(int Id, string Name);

public sealed class SwfFile
{
    public const int TagEnd = 0;
    public const int TagDefineShape = 2;
    public const int TagDefineBitsJpeg = 6;
    public const int TagJpegTables = 8;
    public const int TagDefineFont = 10;
    public const int TagDoAction = 12;
    public const int TagDefineSound = 14;
    public const int TagDefineBitsLossless = 20;
    public const int TagDefineBitsJpeg2 = 21;
    public const int TagDefineShape2 = 22;
    public const int TagDefineShape3 = 32;
    public const int TagDefineBitsJpeg3 = 35;
    public const int TagDefineBitsLossless2 = 36;
    public const int TagDefineEditText = 37;
    public const int TagDefineSprite = 39;
    public const int TagDefineFont2 = 48;
    public const int TagExportAssets = 56;
    public const int TagDoInitAction = 59;
    public const int TagDefineFont3 = 75;
    public const int TagSymbolClass = 76;
    public const int TagDefineShape4 = 83;
    public const int TagDefineBinaryData = 87;

    private SwfFile(string signature, int version, byte[] body)
    {
        Signature = signature;
        Version = version;
        Body = body;
    }

    public string Signature { get; }

    public int Version { get; }

    public byte[] Body { get; }

    public List<SwfTag> Tags { get; } = new();

    public List<SwfSymbol> Exports { get; } = new();

    public SwfRect FrameSize { get; private set; }

    public float FrameRate { get; private set; }

    public int FrameCount { get; private set; }

    public static SwfFile? TryLoad(byte[]? bytes, out string error)
    {
        error = string.Empty;
        if (bytes is null || bytes.Length < 8)
        {
            error = "too small";
            return null;
        }

        string signature = System.Text.Encoding.ASCII.GetString(bytes, 0, 3);
        int version = bytes[3];

        byte[] body;
        switch (signature)
        {
            case "FWS":
                body = bytes[8..];
                break;
            case "CWS":
                if (!TryInflate(bytes, 8, out body))
                {
                    error = "zlib inflate failed";
                    return null;
                }

                break;
            case "ZWS":
                error = "LZMA compressed swf is not supported";
                return null;
            default:
                error = $"not a swf (signature '{signature}')";
                return null;
        }

        var swf = new SwfFile(signature, version, body);
        return swf.Walk(out error) ? swf : null;
    }

    private static bool TryInflate(byte[] bytes, int offset, out byte[] body)
    {
        body = Array.Empty<byte>();
        try
        {
            using var source = new MemoryStream(bytes, offset, bytes.Length - offset);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            using var sink = new MemoryStream();
            zlib.CopyTo(sink);
            body = sink.ToArray();
            return body.Length != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool Walk(out string error)
    {
        error = string.Empty;
        if (Body.Length < 1)
        {
            error = "empty body";
            return false;
        }

        var header = new SwfBitReader(Body);
        FrameSize = header.ReadRect();
        int p = header.BytePosition;
        if (p + 4 > Body.Length)
        {
            error = "truncated header";
            return false;
        }

        FrameRate = BitConverter.ToUInt16(Body, p) / 256f;
        FrameCount = BitConverter.ToUInt16(Body, p + 2);
        p += 4;

        while (p + 2 <= Body.Length)
        {
            ushort packed = BitConverter.ToUInt16(Body, p);
            p += 2;
            int code = packed >> 6;
            int length = packed & 0x3F;
            if (length == 0x3F)
            {
                if (p + 4 > Body.Length)
                {
                    error = "truncated long tag";
                    return false;
                }

                length = (int)BitConverter.ToUInt32(Body, p);
                p += 4;
            }

            if (length < 0 || p + length > Body.Length)
            {
                error = $"tag {code} overruns body";
                return false;
            }

            Tags.Add(new SwfTag(code, p, Body[p..(p + length)]));
            if (code == TagExportAssets)
            {
                ReadExports(Body, p, length);
            }

            p += length;
            if (code == TagEnd)
            {
                break;
            }
        }

        return true;
    }

    private void ReadExports(byte[] data, int offset, int length)
    {
        int end = offset + length;
        if (offset + 2 > end)
        {
            return;
        }

        int count = BitConverter.ToUInt16(data, offset);
        int p = offset + 2;
        for (int i = 0; i < count && p + 2 < end; i++)
        {
            int id = BitConverter.ToUInt16(data, p);
            p += 2;
            int start = p;
            while (p < end && data[p] != 0)
            {
                p++;
            }

            string name = System.Text.Encoding.UTF8.GetString(data, start, p - start);
            p++;
            Exports.Add(new SwfSymbol(id, name));
        }
    }

    public Dictionary<int, int> TagCounts()
    {
        var counts = new Dictionary<int, int>();
        foreach (SwfTag tag in Tags)
        {
            counts.TryGetValue(tag.Code, out int n);
            counts[tag.Code] = n + 1;
        }

        return counts;
    }
}
