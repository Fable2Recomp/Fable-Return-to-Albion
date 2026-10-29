using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Refii.Textures;

namespace Refii.Titles.Fable2.PubGames;

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

        foreach ((string name, string path) in Files(set, game))
        {
            textures._files[name] = path;
        }

        string background = string.Empty;
        if (game.Backgrounds.Count != 0)
        {
            int index = Mathf.Clamp(backgroundIndex, 0, game.Backgrounds.Count - 1);
            background = game.Backgrounds[index];
        }

        var wanted = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
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

            if (!wanted.TryGetValue(resolved, out List<int>? ids))
            {
                ids = new List<int>();
                wanted[resolved] = ids;
            }

            ids.Add(id);
            textures.Bindings.Add($"{name} (id {id}) -> {Path.GetFileName(resolved)}");
        }

        var loaded = new Image?[wanted.Count];
        var paths = new string[wanted.Count];
        wanted.Keys.CopyTo(paths, 0);

        Parallel.For(0, paths.Length, i => loaded[i] = Load(paths[i]));

        for (var i = 0; i < paths.Length; i++)
        {
            foreach (int id in wanted[paths[i]])
            {
                textures._byCharacter[id] = loaded[i];
            }
        }

        return textures;
    }

    public Image? Resolve(int characterId) =>
        _byCharacter.TryGetValue(characterId, out Image? replacement) ? replacement : null;

    private string Match(string exportName, string background) =>
        MatchFile(_files, exportName, background);

    public static string MatchFile(
        IReadOnlyDictionary<string, string> files, string exportName, string background)
    {
        string key = Normalize(exportName);
        if (key == "tablebackground")
        {
            return background;
        }

        if (files.TryGetValue(key, out string? exact))
        {
            return exact;
        }

        foreach ((string candidate, string path) in files)
        {
            if (key.StartsWith(candidate, StringComparison.Ordinal) ||
                candidate.StartsWith(key, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return string.Empty;
    }

    public static Dictionary<string, string> Files(PubGamesSet set, PubGameEntry game)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in set.SharedTextures)
        {
            files[Normalize(Path.GetFileNameWithoutExtension(path))] = path;
        }

        foreach (string path in game.Backgrounds)
        {
            files[Normalize(Path.GetFileNameWithoutExtension(path))] = path;
        }

        return files;
    }

    private static Image? Load(string path)
    {
        if (path.Length == 0 || !File.Exists(path))
        {
            return null;
        }

        byte[] source;
        try
        {
            source = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        var image = new Image();
        if (image.LoadDdsFromBuffer(source) != Error.Ok)
        {
            return null;
        }

        bool hasAlpha = image.IsCompressed() || image.DetectAlpha() != Image.AlphaMode.None;

        return Engine_TexLibrary.EnhanceDecoded(
            source, image, TexSemantic.Ui, hasAlpha,
            Path.GetFileName(path), compress: false) ?? image;
    }

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
}
