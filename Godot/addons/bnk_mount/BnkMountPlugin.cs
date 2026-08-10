#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Refii.Bnk;

namespace Refii.Assets;

[Tool]
public partial class BnkMountPlugin : EditorPlugin
{
    private const string RootLabel = "Game://";
    private const string PlaceholderMeta = "__bnk_placeholder__";
    private const string RootMeta = "__bnk_root__";
    private const string DatabaseRootMeta = "__bnk_database_root__";
    private const string LevelsRootMeta = "__bnk_levels_root__";
    private const string EntitiesRootMeta = "__bnk_entities_root__";
    private const string EffectsRootMeta = "__bnk_effects_root__";
    private const string VideosRootMeta = "__bnk_videos_root__";
    private const string LevelRegionPrefix = "lvlregion:";
    private const string LevelPrefix = "lvl:";
    private const string ContainerMeta = "__bnk_container__";
    private const string FilePrefix = "game://";

    private BnkFileSystem? _filesystem;
    private Refii.Textures.Engine_BnkTextureLoader? _textureLoader;
    private Refii.Models.Engine_BnkMeshLoader? _meshLoader;
    private Refii.Audio.Engine_BnkAudioLoader? _audioLoader;
    private Refii.Gdb.Engine_BnkGdbLoader? _gdbLoader;
    private Refii.Level.Engine_BnkTerrainLoader? _terrainLoader;
    private Refii.Level.Engine_BnkTerrainAlbedoLoader? _albedoLoader;
    private Refii.Level.Engine_BnkTerrainMaterialLoader? _terrainMaterialLoader;
    private Refii.Entities.Engine_BnkEntityLoader? _entityLoader;
    private Refii.Fx.Engine_BnkFxLoader? _fxLoader;
    private Dictionary<string, string>? _byFileName;
    private Tree? _tree;
    private TreeItem? _gameRoot;
    private bool _mounting;
    private bool _collapsedHooked;
    private readonly List<TreeItem> _pending = new();

    private Label? _streamingLabel;
    private Tween? _streamingFade;
    private bool _fadingStreamingLabel;

    private const double StreamingHoldSeconds = 1.0;
    private const double StreamingFadeSeconds = 0.8;

    private static readonly Color GameFolderColor = new(0.36f, 0.85f, 0.47f);

    private static readonly Color StreamingBusyColor = new(1.0f, 0.78f, 0.35f);

    private static readonly Color StreamingDoneColor = new(0.55f, 0.85f, 0.55f);

    private EntityInspectorPlugin? _entityInspector;

    public override void _EnterTree()
    {
        StartMount();
        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged += OnFilesystemChanged;
        AddStreamingLabel();
        _entityInspector = new EntityInspectorPlugin();
        AddInspectorPlugin(_entityInspector);
    }

    public override void _ExitTree()
    {
        if (_entityInspector is not null)
        {
            RemoveInspectorPlugin(_entityInspector);
            _entityInspector = null;
        }

        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged -= OnFilesystemChanged;
        RemoveStreamingLabel();
        RemoveGameRoot();
        RemoveLoaders();
        _filesystem?.Dispose();
        _filesystem = null;
    }

    private void OnStreamingProgress(int done, int total)
    {
        if (_streamingLabel is null || !IsInstanceValid(_streamingLabel))
        {
            return;
        }

        if (total <= 0)
        {
            CancelStreamingFade();
            _streamingLabel.Visible = false;
            return;
        }

        bool complete = done >= total;

        _streamingLabel.Text = complete
            ? $"File streaming: {total}/{total} — done"
            : $"File streaming: {done}/{total}";
        _streamingLabel.AddThemeColorOverride(
            "font_color", complete ? StreamingDoneColor : StreamingBusyColor);

        if (!complete)
        {
            CancelStreamingFade();
            _streamingLabel.Visible = true;
            _streamingLabel.Modulate = Colors.White;
            return;
        }

        if (_fadingStreamingLabel)
        {
            return;
        }

        _fadingStreamingLabel = true;
        _streamingLabel.Visible = true;
        _streamingLabel.Modulate = Colors.White;

        Label label = _streamingLabel;
        _streamingFade = label.CreateTween();
        _streamingFade.TweenInterval(StreamingHoldSeconds);
        _streamingFade.TweenProperty(label, "modulate:a", 0.0f, StreamingFadeSeconds);
        _streamingFade.TweenCallback(Callable.From(() =>
        {
            if (IsInstanceValid(label))
            {
                label.Visible = false;
            }
        }));
    }

    private void CancelStreamingFade()
    {
        if (_streamingFade is not null && IsInstanceValid(_streamingFade))
        {
            _streamingFade.Kill();
        }

        _streamingFade = null;
        _fadingStreamingLabel = false;
    }

    private void AddStreamingLabel()
    {
        if (_streamingLabel is not null)
        {
            return;
        }

        SubViewport? viewport = EditorInterface.Singleton.GetEditorViewport3D(0);
        if (viewport?.GetParent() is not Control host)
        {
            return;
        }

        _streamingLabel = new Label
        {
            Text = "File streaming:",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(16f, 42f),
        };

        _streamingLabel.AddThemeConstantOverride("outline_size", 6);
        _streamingLabel.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.85f));
        _streamingLabel.AddThemeFontSizeOverride("font_size", 16);

        host.AddChild(_streamingLabel);
        Refii.Level.Engine_PropStreamer.ProgressChanged += OnStreamingProgress;
        Refii.Level.Engine_PropStreamer.PublishProgress();
    }

    private void RemoveStreamingLabel()
    {
        Refii.Level.Engine_PropStreamer.ProgressChanged -= OnStreamingProgress;
        CancelStreamingFade();

        if (_streamingLabel is null)
        {
            return;
        }

        if (IsInstanceValid(_streamingLabel))
        {
            _streamingLabel.GetParent()?.RemoveChild(_streamingLabel);
            _streamingLabel.QueueFree();
        }

        _streamingLabel = null;
    }

    private void OnFilesystemChanged() => CallDeferred(nameof(Inject));

    private void StartMount()
    {
        if (_mounting)
        {
            return;
        }

        _mounting = true;
        string gameDirectory = ResolveGameDirectory();
        Refii.Gdb.Engine_BnkTextBankLoader.SetGameDirectory(gameDirectory);

        Task.Run(() =>
        {
            BnkFileSystem? mounted = null;
            try
            {
                mounted = BnkFileSystem.Mount(gameDirectory);
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[bnk_mount] {exception.Message}");
            }

            _filesystem = mounted;
            _videos = Refii.Video.Engine_VideoLibrary.Load(gameDirectory);
            _byFileName = null;
            _mounting = false;
            CallDeferred(nameof(Inject));
        });
    }

    private Tree? ResolveTree()
    {
        if (GodotObject.IsInstanceValid(_tree))
        {
            return _tree;
        }

        Node? dock = FindByClass(GetTree().Root, "FileSystemDock", 0);
        _tree = dock is null ? null : FindByClass(dock, "Tree", 0) as Tree;
        return _tree;
    }

    private byte[]? ResolveBytes(string virtualPath)
    {
        if (_filesystem is null || !_filesystem.TryGetFile(virtualPath, out BnkFile file))
        {
            return null;
        }

        try
        {
            return file.ReadAllBytes();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? HeightfieldForEhf(string ehfVirtualPath)
    {
        foreach (Refii.Level.LevelEntry level in Levels())
        {
            if (level.MainEhfPath.Equals(ehfVirtualPath, StringComparison.OrdinalIgnoreCase))
            {
                return level.HeightfieldPath;
            }
        }

        return null;
    }

    private string? FindByFileName(string fileName)
    {
        if (_filesystem is null)
        {
            return null;
        }

        if (_byFileName is null)
        {
            _byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (BnkFile file in _filesystem.EnumerateAllFiles())
            {
                string path = file.VirtualPath;
                int slash = path.LastIndexOf('/');
                string name = slash < 0 ? path : path[(slash + 1)..];
                if (name.Length != 0)
                {
                    _byFileName.TryAdd(name, path);
                }
            }
        }

        return _byFileName.TryGetValue(fileName, out string? found) ? found : null;
    }

    private IEnumerable<string> EnumerateTexturePaths()
    {
        if (_filesystem is null)
        {
            yield break;
        }

        foreach (BnkFile file in _filesystem.EnumerateAllFiles())
        {
            if (file.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            {
                yield return file.VirtualPath;
            }
        }
    }

    private IEnumerable<string> EnumerateStreamingHints()
    {
        if (_filesystem is null)
        {
            yield break;
        }

        foreach (BnkFile file in _filesystem.EnumerateAllFiles())
        {
            if (!file.SourceArchiveName.Contains(
                    "streaming", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (file.VirtualPath.EndsWith(".hkx", StringComparison.OrdinalIgnoreCase) ||
                file.VirtualPath.EndsWith(".mdl.gmd", StringComparison.OrdinalIgnoreCase))
            {
                yield return file.VirtualPath;
            }
        }
    }

    private IEnumerable<Refii.Level.PropModelAsset> EnumerateModelAssets()
    {
        if (_filesystem is null)
        {
            yield break;
        }

        foreach (BnkFile file in _filesystem.EnumerateAllFiles())
        {
            if (file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Refii.Level.PropModelAsset(
                    file.VirtualPath, file.SourceArchivePath);
            }
        }
    }

    private void EnsureLoaders()
    {
        Refii.Textures.Engine_BnkTextureLoader.SetResolver(ResolveBytes);
        Refii.Models.Engine_BnkMeshLoader.SetResolver(ResolveBytes);
        Refii.Models.Engine_BnkMeshLoader.SetExistsProbe(
            p => _filesystem is not null && _filesystem.TryGetFile(p, out _));
        Refii.Audio.Engine_BnkAudioLoader.SetResolver(ResolveBytes);
        Refii.Gdb.Engine_BnkGdbLoader.SetResolver(ResolveBytes);
        Refii.Level.Engine_BnkTerrainLoader.SetResolver(ResolveBytes);
        Refii.Level.Engine_BnkTerrainAlbedoLoader.SetResolver(ResolveBytes);
        Refii.Level.Engine_BnkTerrainAlbedoLoader.SetHeightfieldLookup(HeightfieldForEhf);
        Refii.Level.Engine_BnkTerrainMaterialLoader.SetResolver(ResolveBytes);
        Refii.Level.Engine_BnkTerrainMaterialLoader.SetNameLookup(FindByFileName);
        Refii.Level.Engine_Clouds.SetTexturePathSource(EnumerateTexturePaths);
        Refii.Level.Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
        Refii.Level.Engine_StreamingCandidates.SetHintSource(EnumerateStreamingHints);
        Refii.Entities.Engine_EntityIndex.SetResolver(ResolveBytes);

        if (_entityLoader is null)
        {
            _entityLoader = new Refii.Entities.Engine_BnkEntityLoader();
            ResourceLoader.AddResourceFormatLoader(_entityLoader);
        }

        if (_fxLoader is null)
        {
            _fxLoader = new Refii.Fx.Engine_BnkFxLoader();
            ResourceLoader.AddResourceFormatLoader(_fxLoader);
        }

        if (_textureLoader is null)
        {
            _textureLoader = new Refii.Textures.Engine_BnkTextureLoader();
            ResourceLoader.AddResourceFormatLoader(_textureLoader);
        }

        if (_meshLoader is null)
        {
            _meshLoader = new Refii.Models.Engine_BnkMeshLoader();
            ResourceLoader.AddResourceFormatLoader(_meshLoader);
        }

        if (_audioLoader is null)
        {
            _audioLoader = new Refii.Audio.Engine_BnkAudioLoader();
            ResourceLoader.AddResourceFormatLoader(_audioLoader);
        }

        if (_gdbLoader is null)
        {
            _gdbLoader = new Refii.Gdb.Engine_BnkGdbLoader();
            ResourceLoader.AddResourceFormatLoader(_gdbLoader);
        }

        if (_terrainLoader is null)
        {
            _terrainLoader = new Refii.Level.Engine_BnkTerrainLoader();
            ResourceLoader.AddResourceFormatLoader(_terrainLoader);
        }

        if (_albedoLoader is null)
        {
            _albedoLoader = new Refii.Level.Engine_BnkTerrainAlbedoLoader();
            ResourceLoader.AddResourceFormatLoader(_albedoLoader);
        }

        if (_terrainMaterialLoader is null)
        {
            _terrainMaterialLoader = new Refii.Level.Engine_BnkTerrainMaterialLoader();
            ResourceLoader.AddResourceFormatLoader(_terrainMaterialLoader);
        }
    }

    private void RemoveLoaders()
    {
        Refii.Entities.Engine_EntityIndex.SetResolver(null);

        if (_entityLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_entityLoader);
            _entityLoader = null;
        }

        if (_fxLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_fxLoader);
            _fxLoader = null;
        }

        if (_textureLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_textureLoader);
            _textureLoader = null;
        }

        if (_meshLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_meshLoader);
            _meshLoader = null;
        }

        if (_audioLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_audioLoader);
            _audioLoader = null;
        }

        if (_gdbLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_gdbLoader);
            _gdbLoader = null;
        }

        if (_terrainLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_terrainLoader);
            _terrainLoader = null;
        }

        if (_albedoLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_albedoLoader);
            _albedoLoader = null;
        }

        if (_terrainMaterialLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_terrainMaterialLoader);
            _terrainMaterialLoader = null;
        }

        Refii.Textures.Engine_BnkTextureLoader.SetResolver(null);
        Refii.Models.Engine_BnkMeshLoader.SetResolver(null);
        Refii.Models.Engine_BnkMeshLoader.SetExistsProbe(null);
        Refii.Audio.Engine_BnkAudioLoader.SetResolver(null);
        Refii.Gdb.Engine_BnkGdbLoader.SetResolver(null);
        Refii.Level.Engine_BnkTerrainLoader.SetResolver(null);
        Refii.Level.Engine_BnkTerrainAlbedoLoader.SetResolver(null);
        Refii.Level.Engine_BnkTerrainAlbedoLoader.SetHeightfieldLookup(null);
        Refii.Level.Engine_BnkTerrainMaterialLoader.SetResolver(null);
        Refii.Level.Engine_BnkTerrainMaterialLoader.SetNameLookup(null);
        _byFileName = null;
    }

    private void Inject()
    {
        if (_filesystem is null)
        {
            return;
        }

        EnsureLoaders();

        Tree? tree = ResolveTree();
        TreeItem? root = tree?.GetRoot();
        if (tree is null || root is null)
        {
            return;
        }

        if (!_collapsedHooked)
        {
            _collapsedHooked = true;
            tree.ItemCollapsed += OnItemCollapsed;
            InterceptDockSignals(tree);
        }

        RemoveGameRoot();

        _gameRoot = tree.CreateItem(root);
        _gameRoot.SetText(0, RootLabel);
        _gameRoot.SetMetadata(0, ContainerMeta);
        _gameRoot.SetSelectable(0, false);
        _gameRoot.SetIcon(0, GetFolderIcon());
        _gameRoot.SetIconModulate(0, GameFolderColor);
        _gameRoot.Collapsed = false;

        TreeItem assets = tree.CreateItem(_gameRoot);
        assets.SetText(0, "Assets");
        assets.SetMetadata(0, RootMeta);
        assets.SetSelectable(0, false);
        assets.SetIcon(0, GetFolderIcon());
        assets.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, assets);
        assets.Collapsed = true;

        TreeItem databases = tree.CreateItem(_gameRoot);
        databases.SetText(0, "Database");
        databases.SetMetadata(0, DatabaseRootMeta);
        databases.SetSelectable(0, false);
        databases.SetIcon(0, GetFolderIcon());
        databases.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, databases);
        databases.Collapsed = true;

        TreeItem entities = tree.CreateItem(_gameRoot);
        entities.SetText(0, "Entities");
        entities.SetMetadata(0, EntitiesRootMeta);
        entities.SetSelectable(0, false);
        entities.SetIcon(0, GetFolderIcon());
        entities.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, entities);
        entities.Collapsed = true;

        TreeItem effects = tree.CreateItem(_gameRoot);
        effects.SetText(0, "Effects");
        effects.SetMetadata(0, EffectsRootMeta);
        effects.SetSelectable(0, false);
        effects.SetIcon(0, GetFolderIcon());
        effects.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, effects);
        effects.Collapsed = true;

        TreeItem levels = tree.CreateItem(_gameRoot);
        levels.SetText(0, "Levels");
        levels.SetMetadata(0, LevelsRootMeta);
        levels.SetSelectable(0, false);
        levels.SetIcon(0, GetFolderIcon());
        levels.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, levels);
        levels.Collapsed = true;

        TreeItem videos = tree.CreateItem(_gameRoot);
        videos.SetText(0, "Videos");
        videos.SetMetadata(0, VideosRootMeta);
        videos.SetSelectable(0, false);
        videos.SetIcon(0, GetFolderIcon());
        videos.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, videos);
        videos.Collapsed = true;

        TreeItem? resRoot = FindResRoot(root);
        if (resRoot is not null)
        {
            _gameRoot.MoveBefore(resRoot);
        }
    }

    private void RemoveGameRoot()
    {
        if (_gameRoot is null || !GodotObject.IsInstanceValid(_gameRoot))
        {
            _gameRoot = null;
            return;
        }

        _gameRoot.GetParent()?.RemoveChild(_gameRoot);
        _gameRoot.Free();
        _gameRoot = null;
    }

    private static TreeItem? FindResRoot(TreeItem root)
    {
        TreeItem? child = root.GetFirstChild();
        while (child is not null)
        {
            if (child.GetText(0).StartsWith("res://", StringComparison.Ordinal))
            {
                return child;
            }

            child = child.GetNext();
        }

        return null;
    }

    private GodotObject? _bridge;

    private void InterceptDockSignals(Tree tree)
    {
        var script = GD.Load<GDScript>("res://addons/bnk_mount/bnk_signal_bridge.gd");
        if (script is null)
        {
            GD.PushWarning("[bnk_mount] signal bridge script missing");
            return;
        }

        _bridge = (GodotObject)script.New();
        _bridge.Call(
            "install",
            tree,
            Callable.From<TreeItem, bool>(IsOurs),
            Callable.From<TreeItem>(ActivateBnkItem));
    }

    private bool IsOurs(TreeItem item)
    {
        if (item is null || !GodotObject.IsInstanceValid(item))
        {
            return false;
        }

        string meta = item.GetMetadata(0).AsString();
        return meta == RootMeta ||
               meta == DatabaseRootMeta ||
               meta == LevelsRootMeta ||
               meta == EntitiesRootMeta ||
               meta == VideosRootMeta ||
               meta == ContainerMeta ||
               meta.StartsWith("bnk:", StringComparison.Ordinal) ||
               meta.StartsWith(LevelRegionPrefix, StringComparison.Ordinal) ||
               meta.StartsWith(LevelPrefix, StringComparison.Ordinal) ||
               meta.StartsWith(FilePrefix, StringComparison.Ordinal) ||
               meta == PlaceholderMeta;
    }

    private void ActivateBnkItem(TreeItem item)
    {
        string meta = item.GetMetadata(0).AsString();

        if (meta.StartsWith(LevelPrefix, StringComparison.Ordinal))
        {
            ActivateLevel(meta[LevelPrefix.Length..]);
            return;
        }

        if (!meta.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            item.Collapsed = !item.Collapsed;
            return;
        }

        string virtualPath = meta[FilePrefix.Length..];

        if (virtualPath.EndsWith(
                Refii.Entities.Engine_EntityIndex.Extension,
                StringComparison.OrdinalIgnoreCase))
        {
            var entity = ResourceLoader.Load<Refii.Entities.Engine_EntityResource>(
                meta, "ArrayMesh", ResourceLoader.CacheMode.Ignore);

            if (entity is null)
            {
                GD.Print($"[entities] could not load {virtualPath}");
                return;
            }

            EditorInterface.Singleton.InspectObject(entity);
            return;
        }

        // Before the bank lookup: movies live on disc, so TryGetFile would reject every
        // one of them.
        if (virtualPath.EndsWith(".bik", StringComparison.OrdinalIgnoreCase))
        {
            ActivateVideo(meta, virtualPath);
            return;
        }

        if (virtualPath.EndsWith(
                Refii.Fx.Engine_BnkFxLoader.Extension, StringComparison.OrdinalIgnoreCase))
        {
            ActivateFx(meta, virtualPath);
            return;
        }

        if (_filesystem is null || !_filesystem.TryGetFile(virtualPath, out BnkFile file))
        {
            return;
        }

        bool isTexture = virtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
        bool isModel = virtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
        bool isAudio = virtualPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        bool isDatabase = virtualPath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase);
        if (!isTexture && !isModel && !isAudio && !isDatabase)
        {
            GD.Print($"[bnk_mount] {virtualPath}  ({file.Size:N0} bytes, no viewer for this type)");
            return;
        }

        if (isDatabase)
        {
            var database = ResourceLoader.Load<Refii.Gdb.GdbDatabase>(meta, "GdbDatabase");
            if (database is null)
            {
                return;
            }

            EditorInterface.Singleton.InspectObject(database);

            GD.Print($"[bnk_mount] {virtualPath} -> {database.RecordCount:N0} records, " +
                     $"{database.NamedRecordCount:N0} named, " +
                     $"{database.DictionaryEntryCount:N0} dictionary entries");
            return;
        }

        if (isAudio)
        {
            var stream = ResourceLoader.Load<AudioStreamWav>(meta, "AudioStreamWAV");
            if (stream is null)
            {
                return;
            }

            EditorInterface.Singleton.InspectObject(stream);

            GD.Print($"[bnk_mount] {virtualPath} -> {stream.GetLength():0.00}s, " +
                     $"{stream.MixRate} Hz, {(stream.Stereo ? "stereo" : "mono")}");
            return;
        }

        if (isModel)
        {
            var mesh = ResourceLoader.Load<ArrayMesh>(meta, "ArrayMesh");
            if (mesh is null)
            {
                return;
            }

            EditorInterface.Singleton.InspectObject(mesh);

            if (mesh.GetSurfaceCount() == 0)
            {
                GD.Print($"[bnk_mount] {virtualPath} -> header only, no body in any archive");
                return;
            }

            int tris = 0;
            for (int s = 0; s < mesh.GetSurfaceCount(); ++s)
            {
                tris += mesh.SurfaceGetArrayIndexLen(s) / 3;
            }

            GD.Print($"[bnk_mount] {virtualPath} -> {mesh.GetSurfaceCount()} surface(s), " +
                     $"{tris:N0} triangles");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = file.ReadAllBytes();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[bnk_mount] {virtualPath}: {exception.Message}");
            return;
        }

        ImageTexture? texture = Refii.Textures.Engine_TexImage.TryCreateTexture(
            bytes, out string error);
        if (texture is null)
        {
            GD.PushWarning($"[bnk_mount] {virtualPath}: {error}");
            return;
        }

        texture.ResourcePath = string.Empty;
        EditorInterface.Singleton.InspectObject(texture);
        GD.Print($"[bnk_mount] {virtualPath} -> {texture.GetWidth()}x{texture.GetHeight()} Texture2D");
    }

    private void ActivateFx(string meta, string virtualPath)
    {
        string effect = Refii.Fx.Engine_BnkFxLoader.EffectNameOf(virtualPath);

        var scene = ResourceLoader.Load<PackedScene>(
            meta, "PackedScene", ResourceLoader.CacheMode.Ignore);
        if (scene is null)
        {
            GD.Print($"[bnk_mount] fx '{effect}' could not be loaded from the bank");
            return;
        }

        Node? instance = scene.Instantiate();
        if (instance is null)
        {
            return;
        }

        Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root is null)
        {
            instance.Free();
            GD.Print($"[bnk_mount] open a scene, then activate '{effect}' to drop it in");
            return;
        }

        root.AddChild(instance);
        instance.Owner = root;
        EditorInterface.Singleton.GetSelection().Clear();
        EditorInterface.Singleton.GetSelection().AddNode(instance);
        GD.Print($"[bnk_mount] fx '{effect}' added from bank -> {root.Name}");
    }

    private void ActivateLevel(string heightfieldPath)
    {
        Refii.Level.LevelEntry? level = Levels()
            .Find(l => l.HeightfieldPath.Equals(heightfieldPath, StringComparison.OrdinalIgnoreCase));

        if (level is null || _filesystem is null)
        {
            return;
        }

        bool sceneExisted = Godot.FileAccess.FileExists(level.ScenePath);

        string vista = level.AdjacentEhfPaths.Length != 0
            ? $" (+{level.AdjacentEhfPaths.Length} adjacent)"
            : string.Empty;
        GD.Print($"[bnk_mount] building {level}{vista} …");

        if (!Refii.Level.Engine_LevelScene.TryBuild(
                level, ResolveBytes, FindByFileName, out string scenePath, out string error))
        {
            GD.PushWarning($"[bnk_mount] {level}: {error}");
            return;
        }

        EditorFileSystem filesystem = EditorInterface.Singleton.GetResourceFilesystem();
        if (sceneExisted)
        {
            filesystem.UpdateFile(scenePath);
        }
        else
        {
            filesystem.Scan();
        }

        GD.Print($"[bnk_mount] {level} -> {scenePath}");

        if (Array.IndexOf(EditorInterface.Singleton.GetOpenScenes(), level.ScenePath) >= 0)
        {
            EditorInterface.Singleton.ReloadSceneFromPath(level.ScenePath);
            return;
        }

        EditorInterface.Singleton.OpenSceneFromPath(level.ScenePath);
    }

    private void OnItemCollapsed(TreeItem item)
    {
        if (item.Collapsed || item.GetChildCount() != 1 || _filesystem is null)
        {
            return;
        }

        if (item.GetChild(0).GetMetadata(0).AsString() != PlaceholderMeta)
        {
            return;
        }

        string meta = item.GetMetadata(0).AsString();
        if (meta != RootMeta &&
            meta != DatabaseRootMeta &&
            meta != LevelsRootMeta &&
            meta != EntitiesRootMeta &&
            meta != EffectsRootMeta &&
            meta != VideosRootMeta &&
            !meta.StartsWith(LevelRegionPrefix, StringComparison.Ordinal) &&
            !meta.StartsWith("bnk:", StringComparison.Ordinal))
        {
            return;
        }

        _pending.Add(item);
        CallDeferred(nameof(FlushPending));
    }

    private void FlushPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var work = new List<TreeItem>(_pending);
        _pending.Clear();

        foreach (TreeItem item in work)
        {
            if (!GodotObject.IsInstanceValid(item) || item.GetChildCount() != 1)
            {
                continue;
            }

            TreeItem only = item.GetChild(0);
            if (only.GetMetadata(0).AsString() != PlaceholderMeta)
            {
                continue;
            }

            item.RemoveChild(only);
            only.Free();

            string meta = item.GetMetadata(0).AsString();
            if (meta == DatabaseRootMeta)
            {
                PopulateDatabases(item);
                continue;
            }

            if (meta == LevelsRootMeta)
            {
                PopulateRegions(item);
                continue;
            }

            if (meta == EntitiesRootMeta)
            {
                PopulateEntities(item);
                continue;
            }

            if (meta == EffectsRootMeta)
            {
                PopulateEffects(item);
                continue;
            }

            if (meta == VideosRootMeta)
            {
                PopulateVideos(item);
                continue;
            }

            if (meta.StartsWith(LevelRegionPrefix, StringComparison.Ordinal))
            {
                PopulateRegionLevels(item, meta[LevelRegionPrefix.Length..]);
                continue;
            }

            Populate(item, meta == RootMeta ? string.Empty : meta[4..]);
        }
    }

    private List<Refii.Level.LevelEntry>? _levels;

    /// <summary>
    /// The movies, indexed separately from the banks — Fable 2 ships every .bik loose
    /// under art/videos, so none of them appear in the bank index.
    /// </summary>
    private Refii.Video.Engine_VideoLibrary _videos = new();

    private List<Refii.Level.LevelEntry> Levels()
    {
        if (_levels is null && _filesystem is not null)
        {
            _levels = Refii.Level.Engine_LevelIndex.Discover(_filesystem);
        }

        return _levels ?? new List<Refii.Level.LevelEntry>();
    }

    private void PopulateEntities(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Texture2D? icon = GetFileIcon();
        foreach (Refii.Gdb.EntityEntry entry in Refii.Entities.Engine_EntityIndex.Entries)
        {
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, Refii.Entities.Engine_EntityIndex.FileNameOf(entry));
            item.SetMetadata(
                0, FilePrefix + Refii.Entities.Engine_EntityIndex.VirtualPathOf(entry));
            if (icon is not null)
            {
                item.SetIcon(0, icon);
            }
        }
    }

    private bool _fxNamesDone;

    private void EnsureFxNames(Refii.Fx.FxBank bank)
    {
        if (_fxNamesDone || _filesystem is null)
        {
            return;
        }

        _fxNamesDone = true;

        var wanted = new HashSet<uint>();
        foreach (Refii.Fx.FxEffect effect in bank.Effects)
        {
            wanted.Add(effect.NameHash);
        }

        var files = new List<string>();
        foreach (BnkFile file in _filesystem.EnumerateAllFiles())
        {
            files.Add(file.VirtualPath);
        }

        Dictionary<uint, string> names =
            Refii.Fx.Engine_FxNames.Harvest(wanted, files, ResolveBytes);
        int applied = Refii.Fx.Engine_FxNames.Apply(bank, names);
        GD.Print($"[bnk_mount] fx names resolved: {applied}/{bank.Effects.Count}");
    }

    private void PopulateEffects(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Refii.Fx.FxBank? bank = Refii.Fx.FableFx.SharedBank();
        if (bank is null)
        {
            return;
        }

        EnsureFxNames(bank);

        var rows = new List<(string Display, bool Named)>(bank.Effects.Count);
        foreach (Refii.Fx.FxEffect effect in bank.Effects)
        {
            bool named = effect.Name.Length != 0;
            string display = named ? effect.Name : $"FX_{effect.NameHash:X8}";
            rows.Add((display, named));
        }

        rows.Sort((a, b) =>
        {
            if (a.Named != b.Named)
            {
                return a.Named ? -1 : 1;
            }

            return string.Compare(a.Display, b.Display, StringComparison.OrdinalIgnoreCase);
        });

        Texture2D? icon = GetFileIcon();
        foreach ((string display, bool _) in rows)
        {
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, display);
            item.SetMetadata(0, FilePrefix + Refii.Fx.Engine_BnkFxLoader.VirtualPathOf(display));
            if (icon is not null)
            {
                item.SetIcon(0, icon);
            }
        }
    }

    private void PopulateRegions(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Texture2D folder = GetFolderIcon();
        var groups = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Refii.Level.LevelEntry level in Levels())
        {
            groups[level.Group] = groups.GetValueOrDefault(level.Group) + 1;
        }

        foreach (KeyValuePair<string, int> group in groups)
        {
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, $"{group.Key}  ({group.Value})");
            item.SetMetadata(0, LevelRegionPrefix + group.Key);
            item.SetSelectable(0, false);
            item.SetIcon(0, folder);
            item.SetIconModulate(0, GameFolderColor);
            AddPlaceholder(tree, item);
            item.Collapsed = true;
        }

        if (groups.Count == 0)
        {
            TreeItem empty = tree.CreateItem(parent);
            empty.SetText(0, "(no heightfields found)");
            empty.SetSelectable(0, false);
            empty.SetMetadata(0, PlaceholderMeta);
        }
    }

    private void PopulateRegionLevels(TreeItem parent, string group)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Texture2D? icon = GetClassIcon(LevelIconNames);

        foreach (Refii.Level.LevelEntry level in Levels())
        {
            if (!level.Group.Equals(group, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, level.DisplayName);
            item.SetMetadata(0, LevelPrefix + level.HeightfieldPath);

            if (icon is not null)
            {
                item.SetIcon(0, icon);
                item.SetIconModulate(0, Colors.White);
            }

            bool built = Godot.FileAccess.FileExists(level.ScenePath);
            string scenarios = level.Scenarios.Length != 0
                ? $"\nscenarios: {string.Join(", ", level.Scenarios)}"
                : string.Empty;

            item.SetTooltipText(0,
                $"{level.World}/{level.LevelPath}{scenarios}\n" +
                $"game://{level.HeightfieldPath}\n{level.HeightfieldSize:N0} bytes\n" +
                (built ? $"built → {level.ScenePath}" : "double-click to build the .tscn"));
        }
    }

    private void ActivateVideo(string resourcePath, string virtualPath)
    {
        var video = ResourceLoader.Load<Refii.Video.Engine_BinkVideo>(resourcePath);
        if (video is null)
        {
            return;
        }

        EditorInterface.Singleton.InspectObject(video);

        GD.Print($"[bnk_mount] {virtualPath} -> {video.Width}x{video.Height}, " +
                 $"{video.FrameCount:N0} frames  ·  {video.Duration:0.0}s");
    }

    private void PopulateVideos(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Texture2D? icon = GetClassIcon(VideoIconNames);

        foreach (string name in _videos.Names)
        {
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, name + ".bik");
            item.SetMetadata(0, FilePrefix + "art/videos/" + name + ".bik");

            if (icon is not null)
            {
                item.SetIcon(0, icon);
                item.SetIconModulate(0, Colors.White);
            }
            else
            {
                item.SetIcon(0, GetFileIcon());
                item.SetIconModulate(0, GameFolderColor);
            }

            item.SetTooltipText(0, $"game://art/videos/{name}.bik\nloose file");
        }
    }

    private static readonly string[] VideoIconNames =
    {
        "VideoStreamPlayer", "AnimatedTexture", "FileVideo", "File",
    };

    private void PopulateDatabases(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null || _filesystem is null)
        {
            return;
        }

        var databases = new List<BnkFile>();
        foreach (BnkFile file in _filesystem.EnumerateAllFiles())
        {
            if (file.VirtualPath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
            {
                databases.Add(file);
            }
        }

        databases.Sort((a, b) =>
        {
            int byName = string.Compare(
                Path.GetFileName(a.VirtualPath), Path.GetFileName(b.VirtualPath),
                StringComparison.OrdinalIgnoreCase);
            return byName != 0
                ? byName
                : string.Compare(a.VirtualPath, b.VirtualPath, StringComparison.OrdinalIgnoreCase);
        });

        Texture2D? icon = GetClassIcon(DatabaseIconNames);

        foreach (BnkFile file in databases)
        {
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, Path.GetFileName(file.VirtualPath));
            item.SetMetadata(0, FilePrefix + file.VirtualPath);

            if (icon is not null)
            {
                item.SetIcon(0, icon);
                item.SetIconModulate(0, Colors.White);
            }
            else
            {
                item.SetIcon(0, GetFileIcon());
                item.SetIconModulate(0, GameFolderColor);
            }

            string origin = file.IsLoose
                ? $"loose file · {file.SourceArchivePath}"
                : file.SourceArchiveName;
            item.SetTooltipText(0,
                $"game://{file.VirtualPath}\n{file.Size:N0} bytes  ·  {origin}");
        }

        if (databases.Count == 0)
        {
            TreeItem empty = tree.CreateItem(parent);
            empty.SetText(0, "(no .gdb found)");
            empty.SetSelectable(0, false);
            empty.SetMetadata(0, PlaceholderMeta);
        }
    }

    private void Populate(TreeItem parent, string directory)
    {
        Tree? tree = ResolveTree();
        if (tree is null || _filesystem is null)
        {
            return;
        }

        Texture2D folderIcon = GetFolderIcon();
        Texture2D fileIcon = GetFileIcon();

        foreach (string name in _filesystem.EnumerateDirectories(directory))
        {
            string child = directory.Length == 0 ? name : directory + "/" + name;
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, name);
            item.SetMetadata(0, "bnk:" + child);
            item.SetSelectable(0, false);
            item.SetIcon(0, folderIcon);
            item.SetIconModulate(0, GameFolderColor);
            AddPlaceholder(tree, item);
            item.Collapsed = true;
        }

        foreach (string name in _filesystem.EnumerateFiles(directory))
        {
            string child = directory.Length == 0 ? name : directory + "/" + name;
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, name);
            item.SetMetadata(0, FilePrefix + child);
            item.SetIcon(0, fileIcon);
            item.SetIconModulate(0, GameFolderColor);
            if (_filesystem.TryGetFile(child, out BnkFile file))
            {
                item.SetTooltipText(0,
                    $"game://{child}\n{file.Size:N0} bytes  ·  {file.SourceArchiveName}");
                ApplyIcon(item, file);
            }
        }
    }

    private const int ThumbnailSize = 32;
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;

    private static readonly string[] DatabaseIconNames =
        { "PackedDataContainer", "Dictionary", "FileList", "ItemList" };

    private static readonly string[] LevelIconNames =
        { "PackedScene", "Node3D", "WorldEnvironment" };

    private static void ApplyIcon(TreeItem item, BnkFile file)
    {
        string path = file.VirtualPath;

        if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTextureThumbnail(item, file);
            return;
        }

        if (path.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
        {
            Texture2D? database = GetClassIcon(DatabaseIconNames);
            if (database is not null)
            {
                item.SetIcon(0, database);
                item.SetIconModulate(0, Colors.White);
            }

            return;
        }

        string[]? candidates =
            path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)
                ? new[] { "ArrayMesh", "Mesh", "MeshInstance3D" }
                : path.EndsWith(".gmd", StringComparison.OrdinalIgnoreCase)
                    ? new[] { "Skeleton3D", "BoneAttachment3D" }
                    : path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        ? new[] { "AudioStreamWAV", "AudioStream", "AudioStreamPlayer" }
                        : null;

        if (candidates is null)
        {
            return;
        }

        Texture2D? icon = GetClassIcon(candidates);
        if (icon is null)
        {
            return;
        }

        item.SetIcon(0, icon);

        item.SetIconModulate(0, Colors.White);
    }

    private static Texture2D? GetClassIcon(string[] classNames)
    {
        Theme theme = EditorInterface.Singleton.GetEditorTheme();
        foreach (string className in classNames)
        {
            if (theme.HasIcon(className, "EditorIcons"))
            {
                return theme.GetIcon(className, "EditorIcons");
            }
        }

        return null;
    }

    private static void ApplyTextureThumbnail(TreeItem item, BnkFile file)
    {
        if (file.Size == 0 || file.Size > MaxThumbnailBytes)
        {
            return;
        }

        try
        {
            Image? image = Refii.Textures.Engine_TexImage.TryCreateImage(
                file.ReadAllBytes(), out _);
            if (image is null)
            {
                return;
            }

            image.ClearMipmaps();
            image.Decompress();
            int w = image.GetWidth();
            int h = image.GetHeight();
            if (w <= 0 || h <= 0)
            {
                return;
            }

            float scale = (float)ThumbnailSize / Math.Max(w, h);
            image.Resize(
                Math.Max(1, (int)(w * scale)),
                Math.Max(1, (int)(h * scale)),
                Image.Interpolation.Bilinear);

            ImageTexture thumb = ImageTexture.CreateFromImage(image);
            item.SetIcon(0, thumb);
            item.SetIconModulate(0, Colors.White);
            item.SetIconMaxWidth(0, ThumbnailSize);
        }
        catch (Exception)
        {
        }
    }

    private static void AddPlaceholder(Tree tree, TreeItem parent)
    {
        TreeItem placeholder = tree.CreateItem(parent);
        placeholder.SetText(0, "...");
        placeholder.SetSelectable(0, false);
        placeholder.SetMetadata(0, PlaceholderMeta);
    }

    private static Texture2D GetFolderIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("Folder", "EditorIcons");

    private static Texture2D GetFileIcon() =>
        EditorInterface.Singleton.GetEditorTheme().GetIcon("File", "EditorIcons");

    private static Node? FindByClass(Node node, string className, int depth)
    {
        if (depth > 12)
        {
            return null;
        }

        if (node.GetClass() == className)
        {
            return node;
        }

        foreach (Node child in node.GetChildren())
        {
            Node? found = FindByClass(child, className, depth + 1);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string ResolveGameDirectory()
    {
        string projectDir = ProjectSettings.GlobalizePath("res://");
        string staged = Path.Combine(projectDir, "build", "assets", "game");
        if (Directory.Exists(staged))
        {
            return staged;
        }

        string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));
        return Path.Combine(repoRoot, "assets", "game");
    }
}
#endif
