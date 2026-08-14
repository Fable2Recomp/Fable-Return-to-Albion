using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Refii.FlashGames;

public sealed class PubGameSetting
{
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Section { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;

    public List<string> Items { get; } = new();

    public bool IsArray => Items.Count != 0;
}

public sealed class PubGameSettings
{
    public List<PubGameSetting> All { get; } = new();

    public Dictionary<string, PubGameSetting> Section(string section)
    {
        var map = new Dictionary<string, PubGameSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (PubGameSetting setting in All)
        {
            if (string.Equals(setting.Section, section, StringComparison.OrdinalIgnoreCase))
            {
                map[setting.Name] = setting;
            }
        }

        return map;
    }

    public Dictionary<string, PubGameSetting> Settings { get; } =
        new(StringComparer.Ordinal);
}

public static class PubGamesText
{
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return new UnicodeEncoding(false, false).GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return new UnicodeEncoding(true, false).GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    public static XDocument? TryParse(byte[]? bytes, out string error)
    {
        error = string.Empty;
        if (bytes is null || bytes.Length == 0)
        {
            error = "empty";
            return null;
        }

        try
        {
            string text = Decode(bytes);
            using var reader = new StringReader(text);
            using XmlReader xml = XmlReader.Create(
                reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            return XDocument.Load(xml, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static Dictionary<string, string>? ParseLocalization(byte[]? bytes, out string error)
    {
        XDocument? document = TryParse(bytes, out error);
        if (document?.Root is null)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (XElement data in document.Root.Descendants("data"))
        {
            string? id = data.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            XElement? value = data.Element("value");
            map[id!] = value is null ? string.Empty : value.Value;
        }

        return map;
    }

    public static PubGameSettings? ParseSettings(byte[]? bytes, out string error)
    {
        XDocument? document = TryParse(bytes, out error);
        if (document?.Root is null)
        {
            return null;
        }

        var settings = new PubGameSettings();
        foreach (XElement element in document.Root.Descendants())
        {
            string? name = element.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var setting = new PubGameSetting
            {
                Name = name!,
                Section = element.Parent?.Name.LocalName ?? string.Empty,
                Type = element.Attribute("type")?.Value ?? string.Empty,
                Value = element.Attribute("value")?.Value ?? string.Empty,
            };

            foreach (XElement item in element.Elements("item"))
            {
                setting.Items.Add(item.Attribute("value")?.Value ?? string.Empty);
            }

            settings.Settings[name!] = setting;
            settings.All.Add(setting);
        }

        return settings;
    }
}
