using System;
using System.Collections.Generic;
using System.IO;

using Godot;

using Refii.Bnk;
using Refii.Core;

namespace Refii.Titles.Fable2.PubGames;

public static class Engine_SwfAssets
{
    public const string RootFolder = "FlashGames";

    public const string TextureExtension = ".png";

    public const string MeshExtension = ".mesh";

    public const string FontExtension = ".tres";

    public const string ShapesFolder = "shapes";

    public const string CommonFolder = "Common";

    public const string PathPrefix = "game://";

    /// <summary>Converted SWF assets: <c>&lt;app&gt;/cache/swf</c>.</summary>
    /// <remarks>
    /// In the app's cache container, not <c>user://</c> — this is derived data like every other
    /// cache, and everything regenerable belongs in one folder inside the install.
    /// </remarks>
    public static string CacheDirectory =>
        System.IO.Path.Combine(Refii.Core.Engine_AppRoot.CacheDirectory, "swf");

    private static readonly string BuildStamp =
        typeof(Engine_SwfAssets).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Bitmap> Bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MeshEntry> Meshes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Game> Games = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Face> Faces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<FontPack> Packs = new();

    private sealed class FontPack
    {
        public required string Path { get; init; }

        public required string Language { get; init; }

        public required string Stem { get; init; }

        public required SwfFile Swf { get; init; }

        public required string Hash { get; init; }

        public SwfDocument? Document;

        public readonly Dictionary<int, string> FacePaths = new();
    }

    private sealed class Face
    {
        public required FontPack Pack { get; init; }

        public required int FontId { get; init; }

        public FontFile? Font;

        public byte[]? Text;

        public bool Failed;
    }

    private sealed class Game
    {
        public required string Folder { get; init; }

        public required string SwfPath { get; init; }

        public required SwfFile Swf { get; init; }

        public required string Hash { get; init; }

        public SwfDocument? Document;

        public readonly Dictionary<int, string> TexturePaths = new();

        public readonly Dictionary<int, List<SwfVectorPart>> Parts = new();

        public Dictionary<int, List<CachedPart>>? Cached;

        public bool CacheRead;

        public bool PartsDirty;
    }

    private sealed class CachedPart
    {
        public required int Fill { get; init; }

        public required Vector2[] Vertices { get; init; }

        public Color[]? Colours { get; init; }

        public Vector2[]? Uvs { get; init; }
    }

    private sealed class Bitmap
    {
        public required Game Game { get; init; }

        public required SwfTag Tag { get; init; }

        public byte[]? Tables { get; init; }

        public string Substitute { get; init; } = string.Empty;

        public Image? Image;

        public byte[]? Png;

        public bool Failed;

        public Texture2D? Placeholder;

        public Image? Enhanced;

        /// <summary>The enhanced image as the texture upload takes it (an unsupported BC image
        /// transcoded or expanded), made ahead on a worker by <see cref="PrepareBitmaps"/> and
        /// handed to the first <see cref="TextureFor"/>, which owns it from then on.</summary>
        public Image? Upload;
    }

    private sealed class MeshEntry
    {
        public required Game Game { get; init; }

        public required int ShapeId { get; init; }

        public required int Fill { get; init; }
    }

    private static Engine_SwfTextureLoader? _textureLoader;
    private static Engine_SwfMeshLoader? _meshLoader;
    private static Engine_SwfFontLoader? _fontLoader;

    public static void EnsureLoaders()
    {
        if (_textureLoader is null)
        {
            _textureLoader = new Engine_SwfTextureLoader();
            ResourceLoader.AddResourceFormatLoader(_textureLoader, atFront: true);
        }

        if (_meshLoader is null)
        {
            _meshLoader = new Engine_SwfMeshLoader();
            ResourceLoader.AddResourceFormatLoader(_meshLoader, atFront: true);
        }

        if (_fontLoader is null)
        {
            _fontLoader = new Engine_SwfFontLoader();
            ResourceLoader.AddResourceFormatLoader(_fontLoader, atFront: true);
        }
    }

    public static void RemoveLoaders()
    {
        // When running in headless mode (e.g. during project export / packaging) or when the engine
        // is shutting down, Godot's core cleanup dismantles ResourceLoader before plugin _ExitTree runs.
        // Calling RemoveResourceFormatLoader in this state asserts "Condition 'i >= loader_count' is true".
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            _fontLoader = null;
            _meshLoader = null;
            _textureLoader = null;
            return;
        }

        if (_fontLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_fontLoader);
            _fontLoader = null;
        }

        if (_meshLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_meshLoader);
            _meshLoader = null;
        }

        if (_textureLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_textureLoader);
            _textureLoader = null;
        }
    }

    private static bool _indexed;

    /// <summary>
    /// Builds the pub-game index if nothing has yet, resolving the game data itself.
    /// </summary>
    /// <remarks>
    /// FOR THE EDITOR, WHERE NOTHING GUARANTEES THE ORDER. At runtime a host mounts and then loads
    /// its stage, so the index is always ready first. In the editor the addon starts the bank mount
    /// on a background thread and Godot restores the last-open scene immediately — and
    /// <c>res://levels/pubgames/*.tscn</c> is nothing but <c>game://</c> references, so every one
    /// resolved before that thread finished failed with "Cannot open file" and got cached as a
    /// failure. Registering the loaders early is half the fix; this is the other half.
    ///
    /// CALLED FROM THE EDITOR PLUGIN'S <c>_EnterTree</c>, ON THE MAIN THREAD, AND NOWHERE ELSE.
    /// It was briefly wired into the loaders' <c>Load</c> instead, to avoid paying for it on
    /// projects that never open a pub game — but a loader runs on Godot's resource worker threads
    /// while a scene's external references are being resolved, and doing this much work there (file
    /// IO, SWF parsing, GD.PushWarning) wedged the editor's filesystem scan and left nothing
    /// mounted at all. It is about a second, once, on the main thread.
    /// </remarks>
    public static void EnsureIndexed()
    {
        if (_indexed)
        {
            return;
        }

        try
        {
            string root = Engine_GameData.ResolveWhere(
                candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
            if (root.Length == 0)
            {
                // NOT AN ERROR. A checkout with no game data staged is the normal state of this
                // repo; the pub games simply are not there to index.
                return;
            }

            Index(null, root);
        }
        catch (Exception exception)
        {
            // NEVER THROWS TO ITS CALLER. This runs during editor plugin start-up, where an
            // exception costs the whole plugin — and a malformed pub game must not be able to
            // unmount the entire project.
            GD.PushWarning($"[swf] pub games not indexed: {exception.Message}");
        }
    }

    /// <summary>
    /// Indexes the pub games' bitmaps, shapes and fonts.
    /// </summary>
    /// <param name="filesystem">
    /// Where to publish the virtual files, or null to build the index only. The loaders read the
    /// static tables below rather than the filesystem, so a null here still makes every
    /// <c>game://FlashGames/*</c> path loadable — it just does not list them in the file browser.
    /// </param>
    public static void Index(BnkFileSystem? filesystem, string gameDirectory)
    {
        string gameRoot = PubGamesCatalog.ResolveRoot(gameDirectory).Length != 0
            ? gameDirectory
            : Engine_GameData.ResolveWhere(
                candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
        if (gameRoot.Length == 0)
        {
            return;
        }

        PubGamesSet? set;
        try
        {
            set = PubGamesCatalog.Discover(gameRoot);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[swf] pub games not indexed: {exception.Message}");
            return;
        }

        if (set is null)
        {
            return;
        }

        foreach ((string language, string path) in set.SharedFonts)
        {
            IndexFontPack(filesystem, language, path);
        }

        foreach (PubGameEntry game in set.Games)
        {
            IndexGame(filesystem, set, game);
        }

        _indexed = true;
    }

    private static void IndexFontPack(BnkFileSystem? filesystem, string language, string path)
    {
        // INDEXED TWICE, PARSED ONCE. The editor builds the index lazily with no filesystem, and
        // the background bank mount comes along later with one; the second pass only has to publish
        // the virtual files. Re-reading the SWF would cost a megabyte of decompression per pack and
        // leave a duplicate in Packs, which PreferredPack walks.
        FontPack? existing = null;
        lock (Gate)
        {
            foreach (FontPack candidate in Packs)
            {
                if (string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    existing = candidate;
                    break;
                }
            }
        }

        FontPack pack;
        if (existing is not null)
        {
            pack = existing;
        }
        else
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[swf] {path}: {exception.Message}");
                return;
            }

            SwfFile? loaded = SwfFile.TryLoad(bytes, out string error);
            if (loaded is null)
            {
                GD.PushWarning($"[swf] {path}: {error}");
                return;
            }

            pack = new FontPack
            {
                Path = path,
                Language = language,
                Stem = Path.GetFileNameWithoutExtension(path),
                Swf = loaded,
                Hash = Digest(bytes),
            };
            lock (Gate)
            {
                Packs.Add(pack);
            }
        }

        SwfFile swf = pack.Swf;
        string folder = RootFolder + "/" + CommonFolder + "/" + SafeName(pack.Stem);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SwfTag tag in swf.Tags)
        {
            if (tag.Code != SwfFile.TagDefineFont2 && tag.Code != SwfFile.TagDefineFont3)
            {
                continue;
            }

            if (!FontHeader(tag, out int id, out string name, out bool bold, out bool italic))
            {
                continue;
            }

            string stem = SafeName(name.Length != 0 ? name : $"font_{id}") +
                (bold ? "_bold" : string.Empty) + (italic ? "_italic" : string.Empty);
            stem = Unique(seen, stem, id);

            string virtualPath = folder + "/" + stem + FontExtension;
            lock (Gate)
            {
                Faces[virtualPath] = new Face { Pack = pack, FontId = id };
            }

            pack.FacePaths[id] = virtualPath;
            string key = virtualPath;
            filesystem?.AddVirtualFile(virtualPath, path, tag.Data.Length, () => ReadFontText(key));
        }
    }

    private static bool FontHeader(SwfTag tag, out int id, out string name, out bool bold, out bool italic)
    {
        id = -1;
        name = string.Empty;
        bold = italic = false;
        byte[] data = tag.Data;
        if (data.Length < 5)
        {
            return false;
        }

        id = BitConverter.ToUInt16(data, 0);
        byte flags = data[2];
        italic = (flags & 0x02) != 0;
        bold = (flags & 0x01) != 0;
        int nameLength = data[4];
        if (5 + nameLength > data.Length)
        {
            return false;
        }

        name = System.Text.Encoding.UTF8.GetString(data, 5, nameLength).TrimEnd('\0');
        return true;
    }

    private static void IndexGame(BnkFileSystem? filesystem, PubGamesSet set, PubGameEntry entry)
    {
        // SAME REUSE AS THE FONT PACKS ABOVE: the lazy editor pass parses, the later mount pass only
        // publishes. Re-parsing would also throw away every SwfVectorPart already built against the
        // old Game object, so the scene's meshes would be rebuilt from scratch.
        string folderName = FolderName(entry.Name);
        Game? game;
        lock (Gate)
        {
            Games.TryGetValue(folderName, out game);
        }

        if (game is null)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(entry.SwfPath);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[swf] {entry.SwfPath}: {exception.Message}");
                return;
            }

            SwfFile? loaded = SwfFile.TryLoad(bytes, out string error);
            if (loaded is null)
            {
                GD.PushWarning($"[swf] {entry.SwfPath}: {error}");
                return;
            }

            game = new Game
            {
                Folder = folderName, SwfPath = entry.SwfPath, Swf = loaded, Hash = Digest(bytes),
            };
            lock (Gate)
            {
                Games[game.Folder] = game;
            }
        }

        SwfFile swf = game.Swf;

        var names = new Dictionary<int, string>();
        foreach (SwfSymbol symbol in swf.Exports)
        {
            names.TryAdd(symbol.Id, symbol.Name);
        }

        string folder = RootFolder + "/" + game.Folder;
        byte[]? tables = SwfBitmaps.JpegTables(swf);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shapeSizes = new Dictionary<int, int>();

        Dictionary<string, string> files = PubGameTextures.Files(set, entry);
        string background = entry.Backgrounds.Count != 0 ? entry.Backgrounds[0] : string.Empty;

        foreach (SwfTag tag in swf.Tags)
        {
            if (SwfShapeParser.IsShapeTag(tag.Code) && tag.Data.Length >= 2)
            {
                shapeSizes[BitConverter.ToUInt16(tag.Data, 0)] = tag.Data.Length;
                continue;
            }

            int id = SwfBitmaps.BitmapId(tag);
            if (id < 0)
            {
                continue;
            }

            bool named = names.TryGetValue(id, out string? exported) && exported.Length != 0;
            string stem = Unique(seen, named ? SafeName(exported!) : $"bitmap_{id}", id);
            string substitute = named ? PubGameTextures.MatchFile(files, exported!, background) : string.Empty;
            if (substitute.Length != 0 && !File.Exists(substitute))
            {
                substitute = string.Empty;
            }

            string virtualPath = folder + "/" + stem + TextureExtension;
            lock (Gate)
            {
                Bitmaps[virtualPath] = new Bitmap
                {
                    Game = game, Tag = tag, Tables = tables, Substitute = substitute,
                };
            }

            game.TexturePaths[id] = virtualPath;
            string key = virtualPath;
            long size = substitute.Length != 0 ? new FileInfo(substitute).Length : tag.Data.Length;
            string source = substitute.Length != 0 ? substitute : entry.SwfPath;
            filesystem?.AddVirtualFile(virtualPath, source, size, () => ReadPng(key));
        }

        SwfDocument document = DocumentOf(game);
        foreach ((int shapeId, SwfShape shape) in document.Shapes)
        {
            if (!document.Geometry.TryGetValue(shapeId, out SwfGeometry? geometry))
            {
                continue;
            }

            int size = shapeSizes.GetValueOrDefault(shapeId);
            foreach (int fill in SwfVectorShape.PartFills(shape, geometry))
            {
                string virtualPath = MeshPath(game.Folder, shapeId, fill);
                lock (Gate)
                {
                    Meshes[virtualPath] = new MeshEntry { Game = game, ShapeId = shapeId, Fill = fill };
                }

                string key = virtualPath;
                filesystem?.AddVirtualFile(virtualPath, entry.SwfPath, size, () => ReadMeshBytes(key));
            }
        }
    }

    private static string MeshPath(string gameFolder, int shapeId, int fill) =>
        $"{RootFolder}/{gameFolder}/{ShapesFolder}/{shapeId}_f{fill}{MeshExtension}";

    public static PackedScene? BuildScene(string gameFolder)
    {
        Game? game;
        lock (Gate)
        {
            if (!Games.TryGetValue(gameFolder, out game))
            {
                return null;
            }
        }

        SwfDocument document = DocumentOf(game);
        SwfRect frame = game.Swf.FrameSize;
        var stage = new Vector2(
            Mathf.Max(frame.Width, 1) / SwfShapeGeometry.TwipsPerPixel,
            Mathf.Max(frame.Height, 1) / SwfShapeGeometry.TwipsPerPixel);

        var library = new List<(int, string)>(game.Swf.Exports.Count);
        foreach (SwfSymbol symbol in game.Swf.Exports)
        {
            library.Add((symbol.Id, symbol.Name));
        }

        PrepareBitmaps(game);
        PackedScene? scene;
        try
        {
            scene = Engine_SwfScene.Build(
                document, game.Swf.FrameRate, stage, game.Folder, SceneKey(gameFolder), library,
                partsFor: id => Parts(game, id),
                fontFor: id => FontFor(game, id));
        }
        finally
        {
            ReleaseUploads(game);
        }

        SaveParts(game);
        return scene;
    }

    /// <summary>
    /// Decodes the game's bitmaps on worker threads before the build asks for them one at a time
    /// on the main thread: the DDS substitute or SWF JPEG/lossless decode and, on Android, the
    /// BC to ASTC transcode (<see cref="Refii.Textures.Engine_AndroidTextureUpload.PrepareAhead"/>).
    /// </summary>
    /// <remarks>CPU work on private images only; every texture is still created on the calling
    /// thread by <see cref="TextureFor"/>, from the same pixels it would have made itself. Bitmaps
    /// whose texture is already loaded are skipped. With enhancement on, the enhanced image may
    /// come from an upscaler, so only the decode runs ahead and the rest stays where it was.</remarks>
    private static void PrepareBitmaps(Game game)
    {
        var pending = new List<string>(game.TexturePaths.Count);
        lock (Gate)
        {
            foreach (string path in game.TexturePaths.Values)
            {
                if (Bitmaps.ContainsKey(path) && !ResourceLoader.HasCached(PathPrefix + path))
                {
                    pending.Add(path);
                }
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        bool enhance = Refii.Textures.Engine_TexEnhance.Enabled;
        string uploads = ProjectSettings.GlobalizePath(CacheDirectory);
        int cachedUploads = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        System.Threading.Tasks.Parallel.ForEach(pending,
            new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount - 1),
            },
            path =>
            {
                if (enhance)
                {
                    TryGetImage(path);
                    return;
                }

                Image? image = TryGetEnhancedImage(path);
                if (image is null)
                {
                    return;
                }

                Image upload = PrepareUpload(image, uploads, ref cachedUploads);
                Bitmap bitmap;
                lock (Gate)
                {
                    bitmap = Bitmaps[path];
                }

                lock (bitmap)
                {
                    if (bitmap.Upload is null && ReferenceEquals(bitmap.Enhanced, image))
                    {
                        bitmap.Upload = upload;
                    }
                    else if (!ReferenceEquals(upload, image))
                    {
                        upload.Dispose();
                    }
                }
            });
        GD.Print($"[swf] {game.Folder}: {pending.Count} bitmaps prepared ahead in {clock.Elapsed.TotalMilliseconds:0} ms" +
                 $" ({cachedUploads} ASTC from the cache)");
    }

    /// <summary>
    /// <see cref="Refii.Textures.Engine_AndroidTextureUpload.PrepareAhead"/>, with its BC to ASTC
    /// transcode kept on disk across launches: <c>astc_&lt;hash&gt;_&lt;format&gt;_&lt;size&gt;_&lt;build&gt;.bin</c>
    /// in <see cref="CacheDirectory"/>, keyed by the BC payload's own hash, its format, size and
    /// mipmaps, and the build (which carries the transcoder).
    /// </summary>
    /// <remarks>Only an image the policy sends to ASTC is looked up; anything else, and any file
    /// that is missing, short or describes another image, is prepared as before. Only an ASTC
    /// result is written: the transcoder's RGBA8 fallback is not kept.</remarks>
    private static Image PrepareUpload(Image image, string directory, ref int cached)
    {
        Image.Format source = image.GetFormat();
        if (Refii.Textures.Engine_AndroidTextureUpload.Policy.Target(source) != Image.Format.Astc4X4)
        {
            return Refii.Textures.Engine_AndroidTextureUpload.PrepareAhead(image);
        }

        int width = image.GetWidth(), height = image.GetHeight();
        bool mipmaps = image.HasMipmaps();
        string path = Path.Combine(directory,
            $"astc_{Digest(image.GetData())}_{source}_{width}x{height}{(mipmaps ? "m" : string.Empty)}_{BuildStamp}.bin");
        byte[]? data = Refii.Textures.Engine_PreparedImageFile.TryRead(
            path, width, height, (int)Image.Format.Astc4X4, mipmaps);
        if (data is not null)
        {
            var read = new Image();
            read.SetData(width, height, mipmaps, Image.Format.Astc4X4, data);
            if (!read.IsEmpty())
            {
                System.Threading.Interlocked.Increment(ref cached);
                return read;
            }

            read.Dispose();
        }

        Image upload = Refii.Textures.Engine_AndroidTextureUpload.PrepareAhead(image);
        if (!ReferenceEquals(upload, image) && upload.GetFormat() == Image.Format.Astc4X4)
        {
            Refii.Textures.Engine_PreparedImageFile.TryWrite(
                path, width, height, (int)Image.Format.Astc4X4, mipmaps, upload.GetData());
        }

        return upload;
    }

    /// <summary>Drops the prepared uploads the build never asked for.</summary>
    private static void ReleaseUploads(Game game)
    {
        foreach (string path in game.TexturePaths.Values)
        {
            Bitmap? bitmap;
            lock (Gate)
            {
                if (!Bitmaps.TryGetValue(path, out bitmap))
                {
                    continue;
                }
            }

            Image? upload = TakeUpload(bitmap);
            if (upload is not null && !ReferenceEquals(upload, bitmap.Enhanced))
            {
                upload.Dispose();
            }
        }
    }

    private static Image? TakeUpload(Bitmap bitmap)
    {
        lock (bitmap)
        {
            Image? upload = bitmap.Upload;
            bitmap.Upload = null;
            return upload;
        }
    }

    public static void SaveCaches(string gameFolder)
    {
        Game? game;
        lock (Gate)
        {
            if (!Games.TryGetValue(gameFolder, out game))
            {
                return;
            }
        }

        SaveParts(game);
    }

    public static string SceneKey(string gameFolder)
    {
        lock (Gate)
        {
            return Games.TryGetValue(gameFolder, out Game? game)
                ? $"{game.Hash}:{Engine_SwfScene.FormatVersion}"
                : string.Empty;
        }
    }

    private static IReadOnlyList<SwfVectorPart> Parts(Game game, int shapeId)
    {
        lock (game)
        {
            if (game.Parts.TryGetValue(shapeId, out List<SwfVectorPart>? built))
            {
                return built;
            }

            SwfDocument document = DocumentOf(game);
            if (!document.Shapes.TryGetValue(shapeId, out SwfShape? shape) ||
                !document.Geometry.TryGetValue(shapeId, out SwfGeometry? geometry))
            {
                return Array.Empty<SwfVectorPart>();
            }

            EnsureCacheRead(game, document);
            List<SwfVectorPart>? parts = null;
            if (game.Cached is not null && game.Cached.Remove(shapeId, out List<CachedPart>? cached))
            {
                parts = FromCache(game, shape, cached);
            }

            if (parts is null)
            {
                parts = SwfVectorShape.Build(
                    shape, geometry, id => TextureFor(game, id), id => Placeholder(game, id));
                game.PartsDirty = true;
            }

            foreach (SwfVectorPart part in parts)
            {
                part.Mesh.TakeOverPath(PathPrefix + MeshPath(game.Folder, shapeId, part.FirstFill));
            }

            game.Parts[shapeId] = parts;
            return parts;
        }
    }

    private static List<SwfVectorPart>? FromCache(Game game, SwfShape shape, List<CachedPart> cached)
    {
        var parts = new List<SwfVectorPart>(cached.Count);
        foreach (CachedPart entry in cached)
        {
            if (entry.Fill < 1 || entry.Fill > shape.FillStyles.Count)
            {
                return null;
            }

            SwfFillStyle style = shape.FillStyles[entry.Fill - 1];
            Texture2D? texture = null;
            var repeat = false;
            if (style.Kind != SwfFillKind.Solid)
            {
                texture = style.BitmapId >= 0
                    ? TextureFor(game, style.BitmapId)
                    : SwfVectorShape.GradientTexture(style);
                repeat = style.Kind is SwfFillKind.RepeatingBitmap or SwfFillKind.NonSmoothedRepeatingBitmap;
            }

            parts.Add(new SwfVectorPart
            {
                Mesh = SwfVectorShape.MeshFrom(entry.Vertices, entry.Colours, entry.Uvs),
                Texture = texture,
                Repeat = repeat,
                FirstFill = entry.Fill,
                Vertices = entry.Vertices,
                Colours = entry.Colours,
                Uvs = entry.Uvs,
            });
        }

        return parts;
    }

    private const uint PartsMagic = 0x53575032;

    private static string PartsCachePath(Game game) =>
        $"{CacheDirectory}/{game.Folder}_{game.Hash}_{BuildStamp}_shapes.bin";

    private static void EnsureCacheRead(Game game, SwfDocument document)
    {
        if (game.CacheRead)
        {
            return;
        }

        game.CacheRead = true;
        string absolute = ProjectSettings.GlobalizePath(PartsCachePath(game));
        if (!File.Exists(absolute))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(absolute);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != PartsMagic)
            {
                return;
            }

            var cached = new Dictionary<int, List<CachedPart>>();
            int count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                int shapeId = reader.ReadInt32();
                int fill = reader.ReadInt32();
                int vertexCount = reader.ReadInt32();
                bool hasColours = reader.ReadBoolean();
                bool hasUvs = reader.ReadBoolean();

                var vertices = new Vector2[vertexCount];
                for (var v = 0; v < vertexCount; v++)
                {
                    vertices[v] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                }

                Color[]? colours = null;
                if (hasColours)
                {
                    colours = new Color[vertexCount];
                    for (var v = 0; v < vertexCount; v++)
                    {
                        colours[v] = Color.Color8(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                    }
                }

                Vector2[]? uvs = null;
                if (hasUvs)
                {
                    uvs = new Vector2[vertexCount];
                    for (var v = 0; v < vertexCount; v++)
                    {
                        uvs[v] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    }
                }

                if (!cached.TryGetValue(shapeId, out List<CachedPart>? list))
                {
                    list = new List<CachedPart>();
                    cached[shapeId] = list;
                }

                list.Add(new CachedPart { Fill = fill, Vertices = vertices, Colours = colours, Uvs = uvs });
            }

            game.Cached = cached;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[swf] cache {absolute}: {exception.Message}; rebuilding");
            game.Cached = null;
        }
    }

    private static void SaveParts(Game game)
    {
        lock (game)
        {
            if (!game.PartsDirty)
            {
                return;
            }

            try
            {
                string absolute = ProjectSettings.GlobalizePath(PartsCachePath(game));
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                using var stream = File.Create(absolute);
                using var writer = new BinaryWriter(stream);
                writer.Write(PartsMagic);

                var count = 0;
                foreach (List<SwfVectorPart> parts in game.Parts.Values)
                {
                    count += parts.Count;
                }

                writer.Write(count);
                foreach ((int shapeId, List<SwfVectorPart> parts) in game.Parts)
                {
                    foreach (SwfVectorPart part in parts)
                    {
                        writer.Write(shapeId);
                        writer.Write(part.FirstFill);
                        writer.Write(part.Vertices.Length);
                        writer.Write(part.Colours is not null);
                        writer.Write(part.Uvs is not null);
                        foreach (Vector2 v in part.Vertices)
                        {
                            writer.Write(v.X);
                            writer.Write(v.Y);
                        }

                        if (part.Colours is not null)
                        {
                            foreach (Color c in part.Colours)
                            {
                                writer.Write((byte)c.R8);
                                writer.Write((byte)c.G8);
                                writer.Write((byte)c.B8);
                                writer.Write((byte)c.A8);
                            }
                        }

                        if (part.Uvs is not null)
                        {
                            foreach (Vector2 uv in part.Uvs)
                            {
                                writer.Write(uv.X);
                                writer.Write(uv.Y);
                            }
                        }
                    }
                }

                game.PartsDirty = false;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[swf] cache {PartsCachePath(game)}: {exception.Message}");
            }
        }
    }
    public static ArrayMesh? TryGetMesh(string virtualPath)
    {

        MeshEntry? entry;
        lock (Gate)
        {
            if (!Meshes.TryGetValue(Strip(virtualPath), out entry))
            {
                return null;
            }
        }

        foreach (SwfVectorPart part in Parts(entry.Game, entry.ShapeId))
        {
            if (part.FirstFill == entry.Fill)
            {
                return part.Mesh;
            }
        }

        return null;
    }

    public static bool RecognisesMesh(string virtualPath) => Recognises(virtualPath, MeshExtension);

    public static Image? TryGetImage(string virtualPath)
    {

        Bitmap? bitmap;
        lock (Gate)
        {
            if (!Bitmaps.TryGetValue(Strip(virtualPath), out bitmap))
            {
                return null;
            }
        }

        lock (bitmap)
        {
            if (bitmap.Image is null && !bitmap.Failed)
            {
                string? problem = null;
                bitmap.Image = LoadSubstitute(bitmap.Substitute)
                    ?? SwfBitmaps.Decode(bitmap.Tag, bitmap.Tables, out _, out problem);
                if (bitmap.Image is null)
                {
                    bitmap.Failed = true;
                    GD.PushWarning($"[swf] {virtualPath}: {problem ?? "undecodable bitmap"}");
                }
            }

            return bitmap.Image;
        }
    }

    public static Image? TryGetEnhancedImage(string virtualPath)
    {
        Image? image = TryGetImage(virtualPath);
        if (image is null)
        {
            return null;
        }

        Bitmap bitmap;
        lock (Gate)
        {
            bitmap = Bitmaps[Strip(virtualPath)];
        }

        lock (bitmap)
        {
            if (bitmap.Enhanced is null)
            {
                byte[] source;
                try
                {
                    source = bitmap.Substitute.Length != 0 ? File.ReadAllBytes(bitmap.Substitute) : bitmap.Tag.Data;
                }
                catch (Exception)
                {
                    source = bitmap.Tag.Data;
                }

                bool hasAlpha = image.IsCompressed() || image.DetectAlpha() != Image.AlphaMode.None;
                string name = Path.GetFileName(bitmap.Substitute.Length != 0 ? bitmap.Substitute : virtualPath);
                bitmap.Enhanced = Refii.Textures.Engine_TexLibrary.EnhanceDecoded(
                    source, image, Refii.Textures.TexSemantic.Ui, hasAlpha, name, compress: false) ?? image;
            }

            return bitmap.Enhanced;
        }
    }

    private static Image? LoadSubstitute(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        try
        {
            var image = new Image();
            return image.LoadDdsFromBuffer(File.ReadAllBytes(path)) == Error.Ok ? image : null;
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[swf] {path}: {exception.Message}");
            return null;
        }
    }

    public static bool IsSubstituted(string virtualPath)
    {
        lock (Gate)
        {
            return Bitmaps.TryGetValue(Strip(virtualPath), out Bitmap? bitmap) &&
                bitmap.Substitute.Length != 0;
        }
    }

    public static bool RecognisesTexture(string virtualPath) =>
        Recognises(virtualPath, TextureExtension);

    public static bool Recognises(string virtualPath) => RecognisesTexture(virtualPath);

    public static bool RecognisesFont(string virtualPath) => Recognises(virtualPath, FontExtension);

    private static bool Recognises(string virtualPath, string extension)
    {
        string path = Strip(virtualPath);
        return path.StartsWith(RootFolder + "/", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    }

    public static FontFile? TryBuildFont(string virtualPath)
    {

        Face? face;
        lock (Gate)
        {
            if (!Faces.TryGetValue(Strip(virtualPath), out face))
            {
                return null;
            }
        }

        lock (face)
        {
            if (face.Font is null && !face.Failed)
            {
                string cached = $"{CacheDirectory}/{face.Pack.Stem}_{face.Pack.Hash}_{BuildStamp}_" +
                    $"{Path.GetFileNameWithoutExtension(Strip(virtualPath))}.res";
                face.Font = LoadCached<FontFile>(cached, "FontFile");

                if (face.Font is null)
                {
                    SwfDocument pack = DocumentOf(face.Pack);
                    SwfFont? font = pack.Fonts.GetValueOrDefault(face.FontId);
                    face.Font = font is null ? null : Engine_SwfFont.Build(font);
                    if (face.Font is not null)
                    {
                        SaveCached(face.Font, cached);
                    }

                    // Drop the parsed outlines once the font is built and cached: they are a large
                    // object graph the GC walks on every full collection. Another face of the same
                    // pack re-parses the tags.
                    face.Pack.Document = null;
                }

                if (face.Font is null)
                {
                    face.Failed = true;
                    GD.PushWarning($"[swf] {virtualPath}: face has no glyphs");
                }
                else
                {
                    face.Font.TakeOverPath(PathPrefix + Strip(virtualPath));
                }
            }

            return face.Font;
        }
    }

    private static (Font? Font, SwfFont? Face) FontFor(Game game, int fontId)
    {
        SwfDocument document = DocumentOf(game);
        SwfFont? stub = document.Fonts.GetValueOrDefault(fontId);
        if (stub is null)
        {
            return (null, null);
        }

        FontPack? pack = PreferredPack();
        if (pack is null)
        {
            return (null, null);
        }

        SwfDocument provider = DocumentOf(pack);
        SwfFont? match = stub.Glyphs.Count > 4 ? null : PubGameFonts.Match(stub, provider);
        if (match is null || !pack.FacePaths.TryGetValue(match.Id, out string? path))
        {
            return (null, null);
        }

        string full = PathPrefix + path;
        Font? font = ResourceLoader.HasCached(full)
            ? ResourceLoader.Load<FontFile>(full, "FontFile")
            : TryBuildFont(path);
        return (font, match);
    }

    private static FontPack? PreferredPack()
    {
        lock (Gate)
        {
            FontPack? first = null;
            foreach (FontPack pack in Packs)
            {
                first ??= pack;
                if (pack.Language.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    return pack;
                }
            }

            return first;
        }
    }

    private static SwfDocument DocumentOf(FontPack pack)
    {
        lock (pack)
        {
            return pack.Document ??= SwfDocument.Build(pack.Swf);
        }
    }

    private static SwfDocument DocumentOf(Game game)
    {
        lock (game)
        {
            return game.Document ??= SwfDocument.Build(game.Swf);
        }
    }

    private static Texture2D? TextureFor(Game game, int bitmapId)
    {
        if (!game.TexturePaths.TryGetValue(bitmapId, out string? path))
        {
            return null;
        }

        string full = PathPrefix + path;
        if (ResourceLoader.HasCached(full))
        {
            return ResourceLoader.Load<Texture2D>(full, "Texture2D");
        }

        Bitmap? bitmap;
        lock (Gate)
        {
            Bitmaps.TryGetValue(path, out bitmap);
        }

        // Made ahead by PrepareBitmaps: the same pixels CreateTexture would prepare, so it finds
        // nothing left to do. Disposed here when it is a private copy, as CreateTexture would.
        Image? upload = bitmap is null ? null : TakeUpload(bitmap);
        Image? image = upload ?? TryGetEnhancedImage(path);
        if (image is null)
        {
            return null;
        }

        ImageTexture texture;
        try
        {
            texture = Refii.Textures.Engine_AndroidTextureUpload.CreateTexture(image);
        }
        finally
        {
            if (upload is not null && !ReferenceEquals(upload, bitmap!.Enhanced))
            {
                upload.Dispose();
            }
        }

        texture.TakeOverPath(full);
        return texture;
    }

    private static Texture2D? Placeholder(Game game, int bitmapId)
    {
        if (!game.TexturePaths.TryGetValue(bitmapId, out string? path))
        {
            return null;
        }

        Bitmap? bitmap;
        lock (Gate)
        {
            if (!Bitmaps.TryGetValue(path, out bitmap))
            {
                return null;
            }
        }

        lock (bitmap)
        {
            if (bitmap.Placeholder is null)
            {
                Image? image = SwfBitmaps.Decode(bitmap.Tag, bitmap.Tables, out _, out _);
                bitmap.Placeholder = image is null ? null : Refii.Textures.Engine_AndroidTextureUpload.CreateTexture(image);
            }

            return bitmap.Placeholder;
        }
    }

    private static byte[] ReadPng(string virtualPath)
    {
        Image? image = TryGetImage(virtualPath);
        if (image is null)
        {
            return Array.Empty<byte>();
        }

        Bitmap bitmap;
        lock (Gate)
        {
            bitmap = Bitmaps[virtualPath];
        }

        lock (bitmap)
        {
            return bitmap.Png ??= image.SavePngToBuffer();
        }
    }

    private static byte[] ReadMeshBytes(string virtualPath)
    {
        ArrayMesh? mesh = TryGetMesh(virtualPath);
        return mesh is null ? Array.Empty<byte>() : Serialize(mesh, MeshExtension);
    }

    private static byte[] ReadFontText(string virtualPath)
    {
        Face? face;
        lock (Gate)
        {
            if (!Faces.TryGetValue(virtualPath, out face))
            {
                return Array.Empty<byte>();
            }
        }

        FontFile? font = TryBuildFont(virtualPath);
        if (font is null)
        {
            return Array.Empty<byte>();
        }

        lock (face)
        {
            return face.Text ??= Serialize(font, FontExtension);
        }
    }

    private static byte[] Serialize(Resource resource, string extension)
    {
        string scratch = $"user://swf_scratch_{Guid.NewGuid():N}{extension}";
        Error saved = ResourceSaver.Save(resource, scratch);
        if (saved != Error.Ok)
        {
            GD.PushWarning($"[swf] {resource.ResourcePath}: not serialised ({saved})");
            return Array.Empty<byte>();
        }

        byte[] bytes = Godot.FileAccess.GetFileAsBytes(scratch);
        DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(scratch));
        return bytes;
    }

    private static T? LoadCached<T>(string path, string type) where T : Resource
    {
        if (!Godot.FileAccess.FileExists(path))
        {
            return null;
        }

        try
        {
            return ResourceLoader.Load<T>(path, type, ResourceLoader.CacheMode.Ignore);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[swf] cache {path}: {exception.Message}");
            return null;
        }
    }

    private static void SaveCached(Resource resource, string path)
    {
        try
        {
            string directory = ProjectSettings.GlobalizePath(CacheDirectory);
            DirAccess.MakeDirRecursiveAbsolute(directory);
            Error saved = ResourceSaver.Save(resource, path, ResourceSaver.SaverFlags.Compress);
            if (saved != Error.Ok)
            {
                GD.PushWarning($"[swf] cache {path}: not written ({saved})");
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[swf] cache {path}: {exception.Message}");
        }
    }

    private static string Digest(byte[] bytes)
    {
        byte[] hash = Refii.Core.Engine_Hash.Sha1(bytes);
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string Unique(HashSet<string> seen, string stem, int id)
    {
        if (seen.Add(stem))
        {
            return stem;
        }

        string suffixed = $"{stem}_{id}";
        seen.Add(suffixed);
        return suffixed;
    }

    private static string Strip(string path)
    {
        string trimmed = path.Replace('\\', '/');
        if (trimmed.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[PathPrefix.Length..];
        }

        return trimmed.Trim('/');
    }

    private static string FolderName(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    private static string SafeName(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] == '/')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }
}
