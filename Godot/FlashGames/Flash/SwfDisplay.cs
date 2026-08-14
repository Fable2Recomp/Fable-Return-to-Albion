using System;
using System.Collections.Generic;

namespace Refii.FlashGames;

public readonly record struct SwfColorTransform(
    float RedMultiply, float GreenMultiply, float BlueMultiply, float AlphaMultiply,
    int RedAdd, int GreenAdd, int BlueAdd, int AlphaAdd)
{
    public static readonly SwfColorTransform Identity =
        new(1f, 1f, 1f, 1f, 0, 0, 0, 0);
}

public sealed class SwfPlaceObject
{
    public int Depth { get; init; }

    public int CharacterId { get; init; } = -1;

    public bool IsMove { get; init; }

    public bool HasMatrix { get; init; }

    public SwfMatrix Matrix { get; init; }

    public bool HasColorTransform { get; init; }

    public SwfColorTransform ColorTransform { get; init; } = SwfColorTransform.Identity;

    public int Ratio { get; init; } = -1;

    public string Name { get; init; } = string.Empty;

    public int ClipDepth { get; init; } = -1;

    public int BlendMode { get; init; }
}

public sealed class SwfFrame
{
    public List<SwfPlaceObject> Places { get; } = new();

    public List<int> Removes { get; } = new();

    public string Label { get; set; } = string.Empty;

    public bool Stops { get; set; }
}

public sealed class SwfSprite
{
    public int Id { get; init; }

    public int DeclaredFrameCount { get; init; }

    public List<SwfFrame> Frames { get; } = new();
}

public static class SwfDisplayParser
{
    public const int TagShowFrame = 1;
    public const int TagPlaceObject2 = 26;
    public const int TagRemoveObject2 = 28;
    public const int TagFrameLabel = 43;
    public const int TagPlaceObject3 = 70;

    public static SwfColorTransform ReadColorTransform(SwfBitReader reader, bool withAlpha)
    {
        reader.Align();
        bool hasAdd = reader.ReadUnsigned(1) != 0;
        bool hasMultiply = reader.ReadUnsigned(1) != 0;
        int bits = (int)reader.ReadUnsigned(4);

        float rm = 1f, gm = 1f, bm = 1f, am = 1f;
        if (hasMultiply)
        {
            rm = reader.ReadSigned(bits) / 256f;
            gm = reader.ReadSigned(bits) / 256f;
            bm = reader.ReadSigned(bits) / 256f;
            am = withAlpha ? reader.ReadSigned(bits) / 256f : 1f;
        }

        int ra = 0, ga = 0, ba = 0, aa = 0;
        if (hasAdd)
        {
            ra = reader.ReadSigned(bits);
            ga = reader.ReadSigned(bits);
            ba = reader.ReadSigned(bits);
            aa = withAlpha ? reader.ReadSigned(bits) : 0;
        }

        reader.Align();
        return new SwfColorTransform(rm, gm, bm, am, ra, ga, ba, aa);
    }

    public static SwfPlaceObject? TryParsePlace(SwfTag tag, out string error)
    {
        error = string.Empty;
        if (tag.Code != TagPlaceObject2 && tag.Code != TagPlaceObject3)
        {
            error = "not a place tag";
            return null;
        }

        try
        {
            var reader = new SwfBitReader(tag.Data);
            byte flags = reader.ReadByte();
            bool hasClipActions = (flags & 0x80) != 0;
            bool hasClipDepth = (flags & 0x40) != 0;
            bool hasName = (flags & 0x20) != 0;
            bool hasRatio = (flags & 0x10) != 0;
            bool hasColorTransform = (flags & 0x08) != 0;
            bool hasMatrix = (flags & 0x04) != 0;
            bool hasCharacter = (flags & 0x02) != 0;
            bool isMove = (flags & 0x01) != 0;

            bool hasFilterList = false;
            bool hasBlendMode = false;
            bool hasCacheAsBitmap = false;
            bool hasClassName = false;
            if (tag.Code == TagPlaceObject3)
            {
                byte flags2 = reader.ReadByte();
                hasFilterList = (flags2 & 0x10) != 0;
                hasBlendMode = (flags2 & 0x20) != 0;
                hasCacheAsBitmap = (flags2 & 0x40) != 0;
                hasClassName = (flags2 & 0x80) != 0;
            }

            int depth = reader.ReadUInt16();
            if (hasClassName)
            {
                ReadString(reader);
            }

            int characterId = hasCharacter ? reader.ReadUInt16() : -1;
            SwfMatrix matrix = default;
            if (hasMatrix)
            {
                matrix = reader.ReadMatrix();
            }

            SwfColorTransform colorTransform = SwfColorTransform.Identity;
            if (hasColorTransform)
            {
                colorTransform = ReadColorTransform(reader, true);
            }

            int ratio = hasRatio ? reader.ReadUInt16() : -1;
            string name = hasName ? ReadString(reader) : string.Empty;
            int clipDepth = hasClipDepth ? reader.ReadUInt16() : -1;

            int blendMode = 0;
            if (hasFilterList)
            {
                SkipFilterList(reader);
            }

            if (hasBlendMode)
            {
                blendMode = reader.ReadByte();
            }

            if (hasCacheAsBitmap)
            {
                reader.ReadByte();
            }

            _ = hasClipActions;

            return new SwfPlaceObject
            {
                Depth = depth,
                CharacterId = characterId,
                IsMove = isMove,
                HasMatrix = hasMatrix,
                Matrix = matrix,
                HasColorTransform = hasColorTransform,
                ColorTransform = colorTransform,
                Ratio = ratio,
                Name = name,
                ClipDepth = clipDepth,
                BlendMode = blendMode,
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string ReadString(SwfBitReader reader)
    {
        var bytes = new List<byte>();
        while (true)
        {
            byte b = reader.ReadByte();
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void SkipFilterList(SwfBitReader reader)
    {
        int count = reader.ReadByte();
        for (int i = 0; i < count; i++)
        {
            int id = reader.ReadByte();
            switch (id)
            {
                case 0:
                    reader.ReadUInt32();
                    break;
                case 1:
                case 2:
                case 3:
                    SkipBlur(reader, id);
                    break;
                case 4:
                case 5:
                {
                    int colors = reader.ReadByte();
                    for (int c = 0; c < colors; c++)
                    {
                        reader.ReadUInt32();
                    }

                    for (int c = 0; c < colors; c++)
                    {
                        reader.ReadByte();
                    }

                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt32();
                    reader.ReadUInt16();
                    reader.ReadByte();
                    break;
                }

                case 6:
                    for (int m = 0; m < 20; m++)
                    {
                        reader.ReadUInt32();
                    }

                    break;
                default:
                    throw new InvalidOperationException($"unknown filter id {id}");
            }
        }
    }

    private static void SkipBlur(SwfBitReader reader, int id)
    {
        if (id == 1)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadByte();
            return;
        }

        reader.ReadUInt32();
        if (id == 3)
        {
            reader.ReadUInt32();
        }

        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        if (id == 3)
        {
            reader.ReadUInt32();
        }

        reader.ReadUInt16();
        reader.ReadByte();
    }

    public static SwfSprite? TryParseSprite(SwfTag tag, out string error)
    {
        error = string.Empty;
        if (tag.Code != SwfFile.TagDefineSprite)
        {
            error = "not a sprite tag";
            return null;
        }

        try
        {
            int id = BitConverter.ToUInt16(tag.Data, 0);
            int declared = BitConverter.ToUInt16(tag.Data, 2);
            var sprite = new SwfSprite { Id = id, DeclaredFrameCount = declared };

            var frame = new SwfFrame();
            int p = 4;
            while (p + 2 <= tag.Data.Length)
            {
                ushort packed = BitConverter.ToUInt16(tag.Data, p);
                p += 2;
                int code = packed >> 6;
                int length = packed & 0x3F;
                if (length == 0x3F)
                {
                    length = (int)BitConverter.ToUInt32(tag.Data, p);
                    p += 4;
                }

                if (length < 0 || p + length > tag.Data.Length)
                {
                    error = "nested tag overruns sprite";
                    return null;
                }

                var nested = new SwfTag(code, p, tag.Data[p..(p + length)]);
                switch (code)
                {
                    case TagPlaceObject2:
                    case TagPlaceObject3:
                    {
                        SwfPlaceObject? place = TryParsePlace(nested, out string perr);
                        if (place is null)
                        {
                            error = perr;
                            return null;
                        }

                        frame.Places.Add(place);
                        break;
                    }

                    case TagRemoveObject2:
                        frame.Removes.Add(BitConverter.ToUInt16(nested.Data, 0));
                        break;
                    case TagFrameLabel:
                        frame.Label = System.Text.Encoding.UTF8.GetString(
                            nested.Data, 0, Math.Max(0, nested.Data.Length - 1));
                        break;
                    case SwfFile.TagDoAction:
                        if (SwfActions.ContainsStop(nested.Data))
                        {
                            frame.Stops = true;
                        }

                        break;
                    case TagShowFrame:
                        sprite.Frames.Add(frame);
                        frame = new SwfFrame();
                        break;
                }

                p += length;
                if (code == SwfFile.TagEnd)
                {
                    break;
                }
            }

            if (frame.Places.Count != 0 || frame.Removes.Count != 0)
            {
                sprite.Frames.Add(frame);
            }

            return sprite;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
