using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.FlashGames;

public sealed class PubGameFonts
{
    private readonly Dictionary<int, SwfFont> _byStubId = new();

    private PubGameFonts()
    {
    }

    public List<string> Bindings { get; } = new();

    public List<string> Unbound { get; } = new();

    public SwfDocument? Provider { get; private set; }

    public static PubGameFonts Build(
        SwfDocument document, string fontSwfPath, string language)
    {
        var fonts = new PubGameFonts();
        if (fontSwfPath.Length == 0 || !File.Exists(fontSwfPath))
        {
            foreach ((int id, SwfFont stub) in document.Fonts)
            {
                fonts.Unbound.Add($"{stub.Name} (id {id}) no {language} font pack");
            }

            return fonts;
        }

        SwfFile? swf = SwfFile.TryLoad(File.ReadAllBytes(fontSwfPath), out string error);
        if (swf is null)
        {
            foreach ((int id, SwfFont stub) in document.Fonts)
            {
                fonts.Unbound.Add($"{stub.Name} (id {id}) font pack failed: {error}");
            }

            return fonts;
        }

        SwfDocument provider = SwfDocument.Build(swf);
        fonts.Provider = provider;

        foreach ((int id, SwfFont stub) in document.Fonts)
        {
            if (stub.Glyphs.Count > 4)
            {
                continue;
            }

            SwfFont? match = Match(stub, provider);
            if (match is null)
            {
                fonts.Unbound.Add($"{stub.Name} (id {id})");
                continue;
            }

            fonts._byStubId[id] = match;
            fonts.Bindings.Add(
                $"{stub.Name} (id {id}, bold={stub.Bold}) -> {match.Name} " +
                $"(id {match.Id}, bold={match.Bold}, {match.Glyphs.Count} glyphs)");
        }

        return fonts;
    }

    public SwfFont? Resolve(int fontId) => _byStubId.GetValueOrDefault(fontId);

    private static SwfFont? Match(SwfFont stub, SwfDocument provider)
    {
        string want = Normalize(stub.Name);
        SwfFont? nameOnly = null;

        foreach (SwfFont candidate in provider.Fonts.Values)
        {
            if (candidate.Glyphs.Count == 0)
            {
                continue;
            }

            string have = Normalize(candidate.Name);
            if (!have.StartsWith(want, StringComparison.Ordinal) &&
                !want.StartsWith(have, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.Bold == stub.Bold && candidate.Italic == stub.Italic)
            {
                return candidate;
            }

            nameOnly ??= candidate;
        }

        return nameOnly;
    }

    private static string Normalize(string name) =>
        name.Replace(" ", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
}
