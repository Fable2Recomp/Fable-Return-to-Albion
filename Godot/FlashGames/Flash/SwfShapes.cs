using System;
using System.Collections.Generic;

namespace Refii.FlashGames;

public readonly record struct SwfRect(int XMin, int XMax, int YMin, int YMax)
{
    public int Width => XMax - XMin;

    public int Height => YMax - YMin;
}

public readonly record struct SwfMatrix(
    float ScaleX, float RotateSkew0, float RotateSkew1, float ScaleY,
    int TranslateX, int TranslateY);

public readonly record struct SwfColor(byte R, byte G, byte B, byte A);

public enum SwfFillKind
{
    Solid = 0x00,
    LinearGradient = 0x10,
    RadialGradient = 0x12,
    FocalGradient = 0x13,
    RepeatingBitmap = 0x40,
    ClippedBitmap = 0x41,
    NonSmoothedRepeatingBitmap = 0x42,
    NonSmoothedClippedBitmap = 0x43,
}

public readonly record struct SwfGradientStop(byte Ratio, SwfColor Color);

public sealed class SwfFillStyle
{
    public SwfFillKind Kind { get; init; }

    public SwfColor Color { get; init; }

    public SwfMatrix Matrix { get; init; }

    public int BitmapId { get; init; } = -1;

    public List<SwfGradientStop> Stops { get; } = new();

    public float FocalPoint { get; init; }
}

public sealed class SwfLineStyle
{
    public int Width { get; init; }

    public SwfColor Color { get; init; }

    public int FillStyleIndex { get; init; } = -1;
}

public enum SwfEdgeKind
{
    Move,
    Line,
    Curve,
}

public readonly record struct SwfEdge(
    SwfEdgeKind Kind, int ControlX, int ControlY, int AnchorX, int AnchorY);

public sealed class SwfPath
{
    public int FillStyle0 { get; set; }

    public int FillStyle1 { get; set; }

    public int LineStyle { get; set; }

    public int StartX { get; set; }

    public int StartY { get; set; }

    public List<SwfEdge> Edges { get; } = new();
}

public sealed class SwfShape
{
    public int Id { get; init; }

    public int TagCode { get; init; }

    public SwfRect Bounds { get; init; }

    public List<SwfFillStyle> FillStyles { get; } = new();

    public List<SwfLineStyle> LineStyles { get; } = new();

    public List<SwfPath> Paths { get; } = new();

    public int EdgeCount
    {
        get
        {
            int total = 0;
            foreach (SwfPath path in Paths)
            {
                total += path.Edges.Count;
            }

            return total;
        }
    }
}

public static class SwfShapeParser
{
    public static bool IsShapeTag(int code) =>
        code is SwfFile.TagDefineShape or SwfFile.TagDefineShape2
            or SwfFile.TagDefineShape3 or SwfFile.TagDefineShape4;

    public static SwfShape? TryParse(SwfTag tag, out string error)
    {
        error = string.Empty;
        if (!IsShapeTag(tag.Code))
        {
            error = "not a shape tag";
            return null;
        }

        try
        {
            var reader = new SwfBitReader(tag.Data);
            int id = reader.ReadUInt16();
            SwfRect bounds = reader.ReadRect();

            if (tag.Code == SwfFile.TagDefineShape4)
            {
                reader.ReadRect();
                reader.Align();
                reader.ReadUnsigned(5);
                reader.ReadUnsigned(1);
                reader.ReadUnsigned(1);
                reader.ReadUnsigned(1);
                reader.Align();
            }

            var shape = new SwfShape { Id = id, TagCode = tag.Code, Bounds = bounds };
            bool wideColor = tag.Code is SwfFile.TagDefineShape3 or SwfFile.TagDefineShape4;
            bool extendedCounts = tag.Code != SwfFile.TagDefineShape;

            ReadFillStyles(reader, shape.FillStyles, wideColor, extendedCounts, tag.Code);
            ReadLineStyles(reader, shape.LineStyles, wideColor, extendedCounts, tag.Code);

            reader.Align();
            int fillBits = (int)reader.ReadUnsigned(4);
            int lineBits = (int)reader.ReadUnsigned(4);

            ReadRecords(reader, shape, ref fillBits, ref lineBits, wideColor, extendedCounts, tag.Code);
            return shape;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static SwfShape? TryParseGlyph(SwfBitReader reader, int tagCode, out string error)
    {
        error = string.Empty;
        try
        {
            var shape = new SwfShape { Id = -1, TagCode = tagCode };
            shape.FillStyles.Add(new SwfFillStyle
            {
                Kind = SwfFillKind.Solid,
                Color = new SwfColor(255, 255, 255, 255),
            });

            reader.Align();
            int fillBits = (int)reader.ReadUnsigned(4);
            int lineBits = (int)reader.ReadUnsigned(4);
            ReadRecords(reader, shape, ref fillBits, ref lineBits, true, true, tagCode);
            return shape;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static int ReadStyleCount(SwfBitReader reader, bool extendedCounts)
    {
        int count = reader.ReadByte();
        if (count == 0xFF && extendedCounts)
        {
            count = reader.ReadUInt16();
        }

        return count;
    }

    private static void ReadFillStyles(
        SwfBitReader reader, List<SwfFillStyle> into, bool wideColor, bool extendedCounts, int code)
    {
        int count = ReadStyleCount(reader, extendedCounts);
        for (int i = 0; i < count; i++)
        {
            into.Add(ReadFillStyle(reader, wideColor, code));
        }
    }

    private static SwfFillStyle ReadFillStyle(SwfBitReader reader, bool wideColor, int code)
    {
        var kind = (SwfFillKind)reader.ReadByte();
        switch (kind)
        {
            case SwfFillKind.Solid:
                return new SwfFillStyle
                {
                    Kind = kind,
                    Color = wideColor ? reader.ReadRgba() : reader.ReadRgb(),
                };

            case SwfFillKind.LinearGradient:
            case SwfFillKind.RadialGradient:
            case SwfFillKind.FocalGradient:
            {
                SwfMatrix matrix = reader.ReadMatrix();
                reader.Align();
                reader.ReadUnsigned(2);
                reader.ReadUnsigned(2);
                int stops = (int)reader.ReadUnsigned(4);
                var style = new SwfFillStyle { Kind = kind, Matrix = matrix };
                for (int i = 0; i < stops; i++)
                {
                    byte ratio = reader.ReadByte();
                    SwfColor colour = wideColor ? reader.ReadRgba() : reader.ReadRgb();
                    style.Stops.Add(new SwfGradientStop(ratio, colour));
                }

                if (kind == SwfFillKind.FocalGradient)
                {
                    reader.ReadUInt16();
                }

                return style;
            }

            default:
            {
                int bitmapId = reader.ReadUInt16();
                SwfMatrix matrix = reader.ReadMatrix();
                return new SwfFillStyle { Kind = kind, BitmapId = bitmapId, Matrix = matrix };
            }
        }
    }

    private static void ReadLineStyles(
        SwfBitReader reader, List<SwfLineStyle> into, bool wideColor, bool extendedCounts, int code)
    {
        int count = ReadStyleCount(reader, extendedCounts);
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadUInt16();
            if (code == SwfFile.TagDefineShape4)
            {
                reader.ReadUnsigned(2);
                uint joinStyle = reader.ReadUnsigned(2);
                uint hasFill = reader.ReadUnsigned(1);
                reader.ReadUnsigned(1);
                reader.ReadUnsigned(1);
                reader.ReadUnsigned(1);
                reader.ReadUnsigned(5);
                reader.ReadUnsigned(1);
                reader.ReadUnsigned(2);
                if (joinStyle == 2)
                {
                    reader.ReadUInt16();
                }

                if (hasFill != 0)
                {
                    SwfFillStyle fill = ReadFillStyle(reader, true, code);
                    into.Add(new SwfLineStyle { Width = width, Color = fill.Color });
                    continue;
                }

                into.Add(new SwfLineStyle { Width = width, Color = reader.ReadRgba() });
                continue;
            }

            into.Add(new SwfLineStyle
            {
                Width = width,
                Color = wideColor ? reader.ReadRgba() : reader.ReadRgb(),
            });
        }
    }

    private static void ReadRecords(
        SwfBitReader reader, SwfShape shape, ref int fillBits, ref int lineBits,
        bool wideColor, bool extendedCounts, int code)
    {
        int x = 0;
        int y = 0;
        SwfPath? current = null;
        int fill0 = 0;
        int fill1 = 0;
        int line = 0;
        int fillBase = 0;
        int lineBase = 0;

        void Flush()
        {
            if (current is { Edges.Count: > 0 })
            {
                shape.Paths.Add(current);
            }

            current = null;
        }

        while (true)
        {
            uint typeFlag = reader.ReadUnsigned(1);
            if (typeFlag == 0)
            {
                uint newStyles = reader.ReadUnsigned(1);
                uint lineStyleChange = reader.ReadUnsigned(1);
                uint fill1Change = reader.ReadUnsigned(1);
                uint fill0Change = reader.ReadUnsigned(1);
                uint moveTo = reader.ReadUnsigned(1);

                if (newStyles == 0 && lineStyleChange == 0 && fill1Change == 0 &&
                    fill0Change == 0 && moveTo == 0)
                {
                    Flush();
                    return;
                }

                Flush();

                if (moveTo != 0)
                {
                    int moveBits = (int)reader.ReadUnsigned(5);
                    x = reader.ReadSigned(moveBits);
                    y = reader.ReadSigned(moveBits);
                }

                if (fill0Change != 0)
                {
                    fill0 = (int)reader.ReadUnsigned(fillBits);
                    if (fill0 != 0)
                    {
                        fill0 += fillBase;
                    }
                }

                if (fill1Change != 0)
                {
                    fill1 = (int)reader.ReadUnsigned(fillBits);
                    if (fill1 != 0)
                    {
                        fill1 += fillBase;
                    }
                }

                if (lineStyleChange != 0)
                {
                    line = (int)reader.ReadUnsigned(lineBits);
                    if (line != 0)
                    {
                        line += lineBase;
                    }
                }

                if (newStyles != 0 && extendedCounts)
                {
                    fillBase = shape.FillStyles.Count;
                    lineBase = shape.LineStyles.Count;
                    ReadFillStyles(reader, shape.FillStyles, wideColor, extendedCounts, code);
                    ReadLineStyles(reader, shape.LineStyles, wideColor, extendedCounts, code);
                    reader.Align();
                    fillBits = (int)reader.ReadUnsigned(4);
                    lineBits = (int)reader.ReadUnsigned(4);
                    fill0 = 0;
                    fill1 = 0;
                    line = 0;
                }

                current = new SwfPath
                {
                    FillStyle0 = fill0,
                    FillStyle1 = fill1,
                    LineStyle = line,
                    StartX = x,
                    StartY = y,
                };
                continue;
            }

            uint straight = reader.ReadUnsigned(1);
            int numBits = (int)reader.ReadUnsigned(4) + 2;

            current ??= new SwfPath
            {
                FillStyle0 = fill0,
                FillStyle1 = fill1,
                LineStyle = line,
                StartX = x,
                StartY = y,
            };

            if (straight != 0)
            {
                int deltaX = 0;
                int deltaY = 0;
                if (reader.ReadUnsigned(1) != 0)
                {
                    deltaX = reader.ReadSigned(numBits);
                    deltaY = reader.ReadSigned(numBits);
                }
                else if (reader.ReadUnsigned(1) != 0)
                {
                    deltaY = reader.ReadSigned(numBits);
                }
                else
                {
                    deltaX = reader.ReadSigned(numBits);
                }

                x += deltaX;
                y += deltaY;
                current.Edges.Add(new SwfEdge(SwfEdgeKind.Line, 0, 0, x, y));
                continue;
            }

            int controlDeltaX = reader.ReadSigned(numBits);
            int controlDeltaY = reader.ReadSigned(numBits);
            int controlX = x + controlDeltaX;
            int controlY = y + controlDeltaY;
            int anchorDeltaX = reader.ReadSigned(numBits);
            int anchorDeltaY = reader.ReadSigned(numBits);
            x = controlX + anchorDeltaX;
            y = controlY + anchorDeltaY;
            current.Edges.Add(new SwfEdge(SwfEdgeKind.Curve, controlX, controlY, x, y));
        }
    }
}
