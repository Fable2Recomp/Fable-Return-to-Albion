using System;
using System.Collections.Generic;
using System.IO;

using Godot;

using Refii.Bnk;
using Refii.Core;
using Refii.FlashGames.Runtime;

namespace Refii.FlashGames;

public static class Engine_SwfAssets
{
    public const string RootFolder = "FlashGames";

    public const string TextureExtension = ".png";

    public const string MeshExtension = ".mesh";

    public const string FontExtension = ".tres";

    public const string ShapesFolder = "shapes";

    public const string CommonFolder = "Common";

    public const string PathPrefix = "game://";

    public const string CacheDirectory = "user://swf_cache";

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

    public static void Index(BnkFileSystem filesystem, string gameDirectory)
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
    }

    private static void IndexFontPack(BnkFileSystem filesystem, string language, string path)
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

        SwfFile? swf = SwfFile.TryLoad(bytes, out string error);
        if (swf is null)
        {
            GD.PushWarning($"[swf] {path}: {error}");
            return;
        }

        var pack = new FontPack
        {
            Path = path,
            Language = language,
            Stem = Path.GetFileNameWithoutExtension(path),
            Swf = swf,
            Hash = Digest(bytes),
        };
        lock (Gate)
        {
            Packs.Add(pack);
        }

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
            filesystem.AddVirtualFile(virtualPath, path, tag.Data.Length, () => ReadFontText(key));
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

    private static void IndexGame(BnkFileSystem filesystem, PubGamesSet set, PubGameEntry entry)
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

        SwfFile? swf = SwfFile.TryLoad(bytes, out string error);
        if (swf is null)
        {
            GD.PushWarning($"[swf] {entry.SwfPath}: {error}");
            return;
        }

        var game = new Game
        {
            Folder = FolderName(entry.Name), SwfPath = entry.SwfPath, Swf = swf, Hash = Digest(bytes),
        };
        lock (Gate)
        {
            Games[game.Folder] = game;
        }

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
            filesystem.AddVirtualFile(virtualPath, source, size, () => ReadPng(key));
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
                filesystem.AddVirtualFile(virtualPath, entry.SwfPath, size, () => ReadMeshBytes(key));
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

        PackedScene? scene = Engine_SwfScene.Build(
            document, game.Swf.FrameRate, stage, game.Folder, SceneKey(gameFolder), library,
            partsFor: id => Parts(game, id),
            fontFor: id => FontFor(game, id));

        SaveParts(game);
        return scene;
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

        Image? image = TryGetEnhancedImage(path);
        if (image is null)
        {
            return null;
        }

        ImageTexture texture = ImageTexture.CreateFromImage(image);
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
                bitmap.Placeholder = image is null ? null : ImageTexture.CreateFromImage(image);
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
        byte[] hash = System.Security.Cryptography.SHA1.HashData(bytes);
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
