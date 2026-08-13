using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;

namespace Refii.FlashGames;

public static class SwfBitmaps
{
    public static Dictionary<int, Image> Extract(SwfFile swf, out List<string> problems)
    {
        problems = new List<string>();
        var images = new Dictionary<int, Image>();
        byte[]? tables = null;

        foreach (SwfTag tag in swf.Tags)
        {
            if (tag.Code == SwfFile.TagJpegTables)
            {
                tables = tag.Data;
            }
        }

        foreach (SwfTag tag in swf.Tags)
        {
            switch (tag.Code)
            {
                case SwfFile.TagDefineBitsJpeg:
                {
                    if (tag.Data.Length < 2)
                    {
                        continue;
                    }

                    int id = BitConverter.ToUInt16(tag.Data, 0);
                    byte[] jpeg = CombineWithTables(tables, tag.Data[2..]);
                    AddJpeg(images, problems, id, jpeg, null);
                    break;
                }

                case SwfFile.TagDefineBitsJpeg2:
                {
                    if (tag.Data.Length < 2)
                    {
                        continue;
                    }

                    int id = BitConverter.ToUInt16(tag.Data, 0);
                    AddJpeg(images, problems, id, StripMarkerPair(tag.Data[2..]), null);
                    break;
                }

                case SwfFile.TagDefineBitsJpeg3:
                {
                    if (tag.Data.Length < 6)
                    {
                        continue;
                    }

                    int id = BitConverter.ToUInt16(tag.Data, 0);
                    int jpegLength = (int)BitConverter.ToUInt32(tag.Data, 2);
                    if (6 + jpegLength > tag.Data.Length)
                    {
                        problems.Add($"id {id}: jpeg3 length overruns tag");
                        continue;
                    }

                    byte[] jpeg = StripMarkerPair(tag.Data[6..(6 + jpegLength)]);
                    byte[]? alpha = null;
                    if (6 + jpegLength < tag.Data.Length)
                    {
                        alpha = Inflate(tag.Data[(6 + jpegLength)..]);
                    }

                    AddJpeg(images, problems, id, jpeg, alpha);
                    break;
                }
            }
        }

        return images;
    }

    private static void AddJpeg(
        Dictionary<int, Image> images, List<string> problems, int id, byte[] jpeg, byte[]? alpha)
    {
        Image? image = null;
        Error error = Error.Failed;
        foreach (byte[] candidate in new[] { StripMarkerPair(jpeg), jpeg })
        {
            var attempt = new Image();
            error = attempt.LoadJpgFromBuffer(candidate);
            if (error == Error.Ok && attempt.GetWidth() > 0)
            {
                image = attempt;
                break;
            }
        }

        if (image is null)
        {
            problems.Add($"id {id}: jpeg decode failed ({error})");
            return;
        }

        if (alpha is not null)
        {
            ApplyAlpha(image, alpha);
        }

        images[id] = image;
    }

    private static void ApplyAlpha(Image image, byte[] alpha)
    {
        int width = image.GetWidth();
        int height = image.GetHeight();
        if (alpha.Length < width * height)
        {
            return;
        }

        if (image.GetFormat() != Image.Format.Rgba8)
        {
            image.Convert(Image.Format.Rgba8);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color colour = image.GetPixel(x, y);
                colour.A = alpha[(y * width) + x] / 255f;
                image.SetPixel(x, y, colour);
            }
        }
    }

    private static byte[] CombineWithTables(byte[]? tables, byte[] data)
    {
        byte[] image = StripMarkerPair(data);
        if (tables is null || tables.Length < 2 || !StartsWithSoi(image))
        {
            return image;
        }

        if (ContainsHuffmanTables(image))
        {
            return image;
        }

        byte[] head = StripMarkerPair(tables);
        if (head.Length >= 2 && head[^2] == 0xFF && head[^1] == 0xD9)
        {
            head = head[..^2];
        }

        if (image.Length >= 2 && image[0] == 0xFF && image[1] == 0xD8)
        {
            image = image[2..];
        }

        var combined = new byte[head.Length + image.Length];
        Buffer.BlockCopy(head, 0, combined, 0, head.Length);
        Buffer.BlockCopy(image, 0, combined, head.Length, image.Length);
        return combined;
    }

    private static byte[] StripMarkerPair(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9 &&
                data[i + 2] == 0xFF && data[i + 3] == 0xD8)
            {
                var joined = new byte[data.Length - 4];
                Buffer.BlockCopy(data, 0, joined, 0, i);
                Buffer.BlockCopy(data, i + 4, joined, i, data.Length - i - 4);
                return joined;
            }
        }

        return data;
    }

    private static bool StartsWithSoi(byte[] data) =>
        data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool ContainsHuffmanTables(byte[] data)
    {
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xC4)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[]? Inflate(byte[] data)
    {
        try
        {
            using var source = new MemoryStream(data);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            using var sink = new MemoryStream();
            zlib.CopyTo(sink);
            return sink.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
