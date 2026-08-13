using System;
using System.Text;

namespace Refii.FlashGames;

public enum SwfTextAlign
{
    Left = 0,
    Right = 1,
    Center = 2,
    Justify = 3,
}

public sealed class SwfEditText
{
    public int Id { get; init; }

    public SwfRect Bounds { get; init; }

    public int FontId { get; init; } = -1;

    public int FontHeight { get; init; }

    public SwfColor Color { get; init; } = new(0, 0, 0, 255);

    public bool HasTextColor { get; init; }

    public bool Multiline { get; init; }

    public bool WordWrap { get; init; }

    public bool Html { get; init; }

    public SwfTextAlign Align { get; init; }

    public int LeftMargin { get; init; }

    public int RightMargin { get; init; }

    public int Indent { get; init; }

    public int Leading { get; init; }

    public string VariableName { get; init; } = string.Empty;

    public string InitialText { get; init; } = string.Empty;

    public static SwfEditText? TryParse(SwfTag tag, out string error)
    {
        error = string.Empty;
        if (tag.Code != SwfFile.TagDefineEditText)
        {
            error = "not an edit text tag";
            return null;
        }

        try
        {
            var reader = new SwfBitReader(tag.Data);
            int id = reader.ReadUInt16();
            SwfRect bounds = reader.ReadRect();
            reader.Align();

            bool hasText = reader.ReadUnsigned(1) != 0;
            bool wordWrap = reader.ReadUnsigned(1) != 0;
            bool multiline = reader.ReadUnsigned(1) != 0;
            reader.ReadUnsigned(1);
            reader.ReadUnsigned(1);
            bool hasTextColor = reader.ReadUnsigned(1) != 0;
            bool hasMaxLength = reader.ReadUnsigned(1) != 0;
            bool hasFont = reader.ReadUnsigned(1) != 0;
            bool hasFontClass = reader.ReadUnsigned(1) != 0;
            reader.ReadUnsigned(1);
            bool hasLayout = reader.ReadUnsigned(1) != 0;
            reader.ReadUnsigned(1);
            reader.ReadUnsigned(1);
            reader.ReadUnsigned(1);
            bool html = reader.ReadUnsigned(1) != 0;
            reader.ReadUnsigned(1);
            reader.Align();

            int fontId = -1;
            int fontHeight = 0;
            if (hasFont)
            {
                fontId = reader.ReadUInt16();
                fontHeight = reader.ReadUInt16();
            }

            if (hasFontClass)
            {
                ReadString(reader);
            }

            var colour = new SwfColor(0, 0, 0, 255);
            if (hasTextColor)
            {
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                byte a = reader.ReadByte();
                colour = new SwfColor(r, g, b, a);
            }

            if (hasMaxLength)
            {
                reader.ReadUInt16();
            }

            var align = SwfTextAlign.Left;
            int leftMargin = 0;
            int rightMargin = 0;
            int indent = 0;
            int leading = 0;
            if (hasLayout)
            {
                align = (SwfTextAlign)reader.ReadByte();
                leftMargin = reader.ReadUInt16();
                rightMargin = reader.ReadUInt16();
                indent = reader.ReadUInt16();
                leading = (short)reader.ReadUInt16();
            }

            string variableName = ReadString(reader);
            string initialText = hasText ? ReadString(reader) : string.Empty;

            return new SwfEditText
            {
                Id = id,
                Bounds = bounds,
                FontId = fontId,
                FontHeight = fontHeight,
                Color = colour,
                HasTextColor = hasTextColor,
                Multiline = multiline,
                WordWrap = wordWrap,
                Html = html,
                Align = align,
                LeftMargin = leftMargin,
                RightMargin = rightMargin,
                Indent = indent,
                Leading = leading,
                VariableName = variableName,
                InitialText = initialText,
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
        var bytes = new System.Collections.Generic.List<byte>();
        while (true)
        {
            byte b = reader.ReadByte();
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
