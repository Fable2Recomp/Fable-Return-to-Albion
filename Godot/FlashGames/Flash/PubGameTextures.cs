using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Refii.Textures;

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

        // BOUND FIRST, LOADED AFTER, and the two are separate passes because the binding is
        // many-to-one: `Match` accepts a prefix either way round, so several exports routinely
        // name the same file. Loading inside the bind loop decoded that file once per export, and
        // now that a decode can be followed by a neural upscale, the same duplication would be
        // seconds rather than milliseconds.
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

        // IN PARALLEL, because this is a load-time cost the player waits on. A cold cache runs the
        // model once per distinct file and the backend is re-entrant — each call builds its own
        // ncnn Extractor — so the wall clock is the slowest file rather than their sum. A warm one
        // is a handful of disk reads and the parallelism costs nothing.
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

    /// <summary>
    /// Decodes one replacement texture and puts it through the engine's upscaling pipeline.
    /// </summary>
    /// <remarks>
    /// THE PUB GAMES ARE A SEPARATE RUNTIME, but they are not a separate renderer. Their art is
    /// loose <c>.dds</c> beside the <c>.swf</c> rather than a <c>.tex</c> in a bank, so none of
    /// <see cref="Engine_TexLibrary.Get"/> applies — there is no virtual path and Godot owns the
    /// container decode. Everything after that decode is shared:
    /// <see cref="Engine_TexLibrary.EnhanceDecoded"/> is the same policy, the same scale rule, the
    /// same <c>user://</c> cache and the same backend the world and the front end go through, so
    /// the player's upscaling setting means the same thing here as everywhere else and turning it
    /// off gives back the shipped art.
    ///
    /// UNCOMPRESSED, because the consumer is <see cref="SwfBitmapSampler"/> — a CPU sampler that
    /// calls <c>Decompress</c> on anything block compressed. Handing it DXT would spend the encode,
    /// spend the decode, and lose the difference between them for nothing.
    ///
    /// A DECLINED ENHANCEMENT IS THE NORMAL ANSWER, not a failure: no backend registered, the
    /// player's switch off, the editor, or a texture already at the policy's ceiling. Every one of
    /// those keeps the shipped image, which is what used to happen unconditionally.
    /// </remarks>
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

        // DetectAlpha only inspects UNCOMPRESSED pixels — on a DXT surface it reports none whatever
        // the block type says. A compressed pub texture is assumed to carry alpha rather than
        // decompressed here to ask, because the sampler is going to decompress it anyway and the
        // flag's only reader is the cache record.
        bool hasAlpha = image.IsCompressed() || image.DetectAlpha() != Image.AlphaMode.None;

        return Engine_TexLibrary.EnhanceDecoded(
            source, image, TexSemantic.Ui, hasAlpha,
            Path.GetFileName(path), compress: false) ?? image;
    }

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
}
