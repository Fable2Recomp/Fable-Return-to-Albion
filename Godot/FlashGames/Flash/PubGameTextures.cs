using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.FlashGames;

public sealed class PubGameTextures
{
    private readonly Dictionary<int, Image?> _byCharacter = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    private PubGameTextures()
    {
    }

    public List<string> Bindings { get; } = new();

    public List<string> Unbound { get; } = new();

    public static PubGameTextures Build(
        PubGamesSet set, PubGameEntry game, SwfDocument document, int backgroundIndex = 0)
    {
        var textures = new PubGameTextures();

        foreach (string path in set.SharedTextures)
        {
            textures._files[Normalize(Path.GetFileNameWithoutExtension(path))] = path;
        }

        foreach (string path in game.Backgrounds)
        {
            textures._files[Normalize(Path.GetFileNameWithoutExtension(path))] = path;
        }

        string background = string.Empty;
        if (game.Backgrounds.Count != 0)
        {
            int index = Mathf.Clamp(backgroundIndex, 0, game.Backgrounds.Count - 1);
            background = game.Backgrounds[index];
        }

        foreach ((string name, int id) in document.Exports)
        {
            if (!document.Bitmaps.ContainsKey(id))
            {
                continue;
            }

            string resolved = textures.Match(name, background);
            if (resolved.Length == 0)
            {
                textures.Unbound.Add($"{name} (id {id})");
                continue;
            }

            textures._byCharacter[id] = Load(resolved);
            textures.Bindings.Add($"{name} (id {id}) -> {Path.GetFileName(resolved)}");
        }

        return textures;
    }

    public Image? Resolve(int characterId) =>
        _byCharacter.TryGetValue(characterId, out Image? replacement) ? replacement : null;

    private string Match(string exportName, string background)
    {
        string key = Normalize(exportName);
        if (key == "tablebackground")
        {
            return background;
        }

        if (_files.TryGetValue(key, out string? exact))
        {
            return exact;
        }

        foreach ((string candidate, string path) in _files)
        {
            if (key.StartsWith(candidate, StringComparison.Ordinal) ||
                candidate.StartsWith(key, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static Image? Load(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        var image = new Image();
        return image.LoadDdsFromBuffer(File.ReadAllBytes(path)) == Error.Ok ? image : null;
    }

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
}
