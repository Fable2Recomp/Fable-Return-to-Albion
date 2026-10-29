using System.Collections.Generic;

namespace Refii.Titles.Fable2.PubGames.Framework;

public sealed class PubTextFormat
{
    public string? Align { get; set; }

    public int? BlockIndent { get; set; }

    public bool? Bold { get; set; }

    public bool? Bullet { get; set; }

    public int? Color { get; set; }

    public string? Font { get; set; }

    public int? Indent { get; set; }

    public bool? Italic { get; set; }

    public int? Leading { get; set; }

    public int? LeftMargin { get; set; }

    public int? RightMargin { get; set; }

    public int? Size { get; set; }

    public string? Target { get; set; }

    public bool? Underline { get; set; }

    public string? Url { get; set; }
}

public sealed class FormattedText
{
    public FormattedText(string text, PubTextFormat? textFormat)
    {
        Text = text;
        Format = textFormat;
    }

    public string Text { get; }

    public PubTextFormat? Format { get; set; }
}

public sealed class Localizer
{
    private readonly Dictionary<string, FormattedText> _dictionary = new();

    public void SetString(string id, string locText)
    {
        _dictionary[id] = new FormattedText(locText, null);
    }

    public bool Contains(string id) => _dictionary.ContainsKey(id);

    public IEnumerable<string> Keys() => _dictionary.Keys;

    public string LocalizeString(string id, IReadOnlyDictionary<string, string>? variables)
    {
        if (!_dictionary.TryGetValue(id, out FormattedText? entry))
        {
            return id;
        }

        string text = entry.Text;
        text = text.Replace("\\n", "\n").Replace("\\t", "\t");
        if (variables is not null)
        {
            foreach ((string key, string value) in variables)
            {
                text = text.Replace("$" + key, value);
            }
        }

        return text;
    }

    public PubTextFormat? GetTextFormat(string id) =>
        _dictionary.TryGetValue(id, out FormattedText? entry) ? entry.Format : null;

    /// <summary>
    /// <c>Localizer::Localize</c> (0x000b0f-0x000b41): sets the text, then the string's own text
    /// format when the file gave it attributes.
    /// </summary>
    public void Localize(IPubClip target, string id, IReadOnlyDictionary<string, string>? variables)
    {
        target.Text = LocalizeString(id, variables);
        PubTextFormat? format = GetTextFormat(id);
        if (format is not null)
        {
            target.SetTextFormat(format);
        }
    }

    public void SetAttribute(string id, string attribute, string? setting)
    {
        if (!_dictionary.TryGetValue(id, out FormattedText? entry))
        {
            return;
        }

        PubTextFormat format = entry.Format ?? new PubTextFormat();
        bool recognised = true;
        switch (attribute)
        {
            case "align":
                format.Align = setting;
                break;
            case "blockIndent":
                format.BlockIndent = ToInt(setting);
                break;
            case "bold":
                format.Bold = ToBool(setting);
                break;
            case "bullet":
                format.Bullet = ToBool(setting);
                break;
            case "color":
                format.Color = ToInt(setting);
                break;
            case "font":
                format.Font = setting;
                break;
            case "indent":
                format.Indent = ToInt(setting);
                break;
            case "italic":
                format.Italic = ToBool(setting);
                break;
            case "leading":
                format.Leading = ToInt(setting);
                break;
            case "leftMargin":
                if (setting is not null)
                {
                    format.LeftMargin = ToInt(setting);
                }

                break;
            case "rightMargin":
                if (setting is not null)
                {
                    format.RightMargin = ToInt(setting);
                }

                break;
            case "size":
                format.Size = ToInt(setting);
                break;
            case "target":
                format.Target = setting;
                break;
            case "underline":
                format.Underline = ToBool(setting);
                break;
            case "url":
                format.Url = setting;
                break;
            default:
                recognised = false;
                break;
        }

        if (entry.Format is null && recognised)
        {
            entry.Format = format;
        }
    }

    private static bool? ToBool(string? text) => text?.ToLowerInvariant() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    private static int? ToInt(string? text) =>
        text is not null && int.TryParse(text, out int value) ? value : null;
}
