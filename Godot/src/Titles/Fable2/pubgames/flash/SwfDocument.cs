using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2.PubGames;

public sealed class SwfDocument
{
    public Dictionary<int, SwfShape> Shapes { get; } = new();

    public Dictionary<int, SwfGeometry> Geometry { get; } = new();

    public Dictionary<int, SwfSprite> Sprites { get; } = new();

    public Dictionary<int, Image> Bitmaps { get; } = new();

    public Dictionary<string, int> Exports { get; } = new(StringComparer.Ordinal);

    public Dictionary<int, SwfFont> Fonts { get; } = new();

    public Dictionary<int, SwfEditText> EditTexts { get; } = new();

    public List<SwfFrame> RootFrames { get; } = new();

    public List<string> Problems { get; } = new();

    public static SwfDocument Build(SwfFile swf)
    {
        var document = new SwfDocument();

        foreach (SwfSymbol symbol in swf.Exports)
        {
            document.Exports[symbol.Name] = symbol.Id;
        }

        Dictionary<int, Image> bitmaps = SwfBitmaps.Extract(swf, out List<string> bitmapProblems);
        foreach ((int id, Image image) in bitmaps)
        {
            document.Bitmaps[id] = image;
        }

        document.Problems.AddRange(bitmapProblems);

        var frame = new SwfFrame();
        foreach (SwfTag tag in swf.Tags)
        {
            if (SwfShapeParser.IsShapeTag(tag.Code))
            {
                SwfShape? shape = SwfShapeParser.TryParse(tag, out string shapeError);
                if (shape is null)
                {
                    document.Problems.Add($"shape tag {tag.Code}: {shapeError}");
                    continue;
                }

                document.Shapes[shape.Id] = shape;
                document.Geometry[shape.Id] = SwfShapeGeometry.Build(shape);
                continue;
            }

            switch (tag.Code)
            {
                case SwfFile.TagDefineFont2:
                case SwfFile.TagDefineFont3:
                {
                    SwfFont? font = SwfFont.TryParse(tag, out string fontError);
                    if (font is null)
                    {
                        document.Problems.Add($"font: {fontError}");
                        break;
                    }

                    document.Fonts[font.Id] = font;
                    break;
                }

                case SwfFile.TagDefineEditText:
                {
                    SwfEditText? field = SwfEditText.TryParse(tag, out string textError);
                    if (field is null)
                    {
                        document.Problems.Add($"editText: {textError}");
                        break;
                    }

                    document.EditTexts[field.Id] = field;
                    break;
                }

                case SwfFile.TagDefineSprite:
                {
                    SwfSprite? sprite = SwfDisplayParser.TryParseSprite(tag, out string spriteError);
                    if (sprite is null)
                    {
                        document.Problems.Add($"sprite: {spriteError}");
                        break;
                    }

                    document.Sprites[sprite.Id] = sprite;
                    break;
                }

                case SwfDisplayParser.TagPlaceObject2:
                case SwfDisplayParser.TagPlaceObject3:
                {
                    SwfPlaceObject? place = SwfDisplayParser.TryParsePlace(tag, out string placeError);
                    if (place is null)
                    {
                        document.Problems.Add($"place: {placeError}");
                        break;
                    }

                    frame.Places.Add(place);
                    break;
                }

                case SwfDisplayParser.TagRemoveObject2:
                    if (tag.Data.Length >= 2)
                    {
                        frame.Removes.Add(BitConverter.ToUInt16(tag.Data, 0));
                    }

                    break;

                case SwfDisplayParser.TagShowFrame:
                    document.RootFrames.Add(frame);
                    frame = new SwfFrame();
                    break;
            }
        }

        if (frame.Places.Count != 0 || frame.Removes.Count != 0)
        {
            document.RootFrames.Add(frame);
        }

        return document;
    }

    public bool TryGetExport(string name, out int id) => Exports.TryGetValue(name, out id);
}
