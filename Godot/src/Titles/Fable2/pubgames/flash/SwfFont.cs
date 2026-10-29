using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Titles.Fable2.PubGames;

public sealed class SwfGlyph
{
    public SwfShape? Shape { get; init; }

    public SwfGeometry? Geometry { get; init; }

    public int Advance { get; init; }
}

public sealed class SwfFont
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public bool Bold { get; init; }

    public bool Italic { get; init; }

    public int TagCode { get; init; }

    public List<SwfGlyph> Glyphs { get; } = new();

    public Dictionary<int, int> CodeToGlyph { get; } = new();

    public int Ascent { get; private set; }

    public int Descent { get; private set; }

    public int Leading { get; private set; }

    public float UnitsPerEm => TagCode == SwfFile.TagDefineFont3 ? 20480f : 1024f;

    public int GlyphFor(char c) => CodeToGlyph.TryGetValue(c, out int index) ? index : -1;

    public int AdvanceFor(char c)
    {
        int index = GlyphFor(c);
        return index >= 0 && index < Glyphs.Count ? Glyphs[index].Advance : 0;
    }

    public static SwfFont? TryParse(SwfTag tag, out string error)
    {
        error = string.Empty;
        if (tag.Code != SwfFile.TagDefineFont2 && tag.Code != SwfFile.TagDefineFont3)
        {
            error = "not a font tag";
            return null;
        }

        try
        {
            byte[] data = tag.Data;
            int id = BitConverter.ToUInt16(data, 0);
            byte flags = data[2];
            bool hasLayout = (flags & 0x80) != 0;
            bool wideOffsets = (flags & 0x08) != 0;
            bool wideCodes = (flags & 0x04) != 0;
            bool italic = (flags & 0x02) != 0;
            bool bold = (flags & 0x01) != 0;

            int nameLength = data[4];
            string name = Encoding.UTF8.GetString(data, 5, nameLength).TrimEnd('\0');
            int p = 5 + nameLength;

            int glyphCount = BitConverter.ToUInt16(data, p);
            p += 2;

            var font = new SwfFont
            {
                Id = id,
                Name = name,
                Bold = bold,
                Italic = italic,
                TagCode = tag.Code,
            };

            if (glyphCount == 0)
            {
                return font;
            }

            int tableStart = p;
            var offsets = new int[glyphCount + 1];
            for (int i = 0; i < glyphCount; i++)
            {
                offsets[i] = wideOffsets
                    ? (int)BitConverter.ToUInt32(data, tableStart + (i * 4))
                    : BitConverter.ToUInt16(data, tableStart + (i * 2));
            }

            int codeTableOffsetPos = tableStart + (glyphCount * (wideOffsets ? 4 : 2));
            offsets[glyphCount] = wideOffsets
                ? (int)BitConverter.ToUInt32(data, codeTableOffsetPos)
                : BitConverter.ToUInt16(data, codeTableOffsetPos);

            var shapes = new SwfShape?[glyphCount];
            for (int i = 0; i < glyphCount; i++)
            {
                int start = tableStart + offsets[i];
                int end = tableStart + offsets[i + 1];
                if (start < 0 || end > data.Length || end <= start)
                {
                    continue;
                }

                var slice = new byte[end - start];
                Array.Copy(data, start, slice, 0, slice.Length);
                var reader = new SwfBitReader(slice);
                shapes[i] = SwfShapeParser.TryParseGlyph(reader, tag.Code, out _);
            }

            int codeTable = tableStart + offsets[glyphCount];
            for (int i = 0; i < glyphCount; i++)
            {
                int code = wideCodes
                    ? BitConverter.ToUInt16(data, codeTable + (i * 2))
                    : data[codeTable + i];
                font.CodeToGlyph[code] = i;
            }

            var advances = new int[glyphCount];
            if (hasLayout)
            {
                int layout = codeTable + (glyphCount * (wideCodes ? 2 : 1));
                font.Ascent = BitConverter.ToInt16(data, layout);
                font.Descent = BitConverter.ToInt16(data, layout + 2);
                font.Leading = BitConverter.ToInt16(data, layout + 4);
                int advanceTable = layout + 6;
                for (int i = 0; i < glyphCount; i++)
                {
                    int at = advanceTable + (i * 2);
                    advances[i] = at + 1 < data.Length ? BitConverter.ToInt16(data, at) : 0;
                }
            }

            for (int i = 0; i < glyphCount; i++)
            {
                SwfShape? shape = shapes[i];
                font.Glyphs.Add(new SwfGlyph
                {
                    Shape = shape,
                    Geometry = shape is null ? null : SwfShapeGeometry.Build(shape),
                    Advance = advances[i],
                });
            }

            return font;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
