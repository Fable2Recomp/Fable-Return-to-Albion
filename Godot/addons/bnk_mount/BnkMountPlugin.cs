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
    private const string HeroRootMeta = "__bnk_hero_root__";
    private const string EquipmentRootMeta = "__bnk_equipment_root__";
    private const string WeaponsRootMeta = "__bnk_weapons_root__";
    private const string WeaponFamilyPrefix = "weaponfam:";
    private const string ClothesRootMeta = "__bnk_clothes_root__";
    private const string ClothingSlotPrefix = "clothslot:";
    private const string ItemsRootMeta = "__bnk_items_root__";
    private const string ItemCategoryPrefix = "itemcat:";
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
    private Refii.Equipment.Engine_BnkWeaponLoader? _weaponLoader;
    private Refii.Equipment.Engine_BnkClothingLoader? _clothingLoader;
    private Refii.Equipment.Engine_BnkItemLoader? _itemLoader;
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

    private static readonly Color MeshlessItemColor = new(0.62f, 0.62f, 0.62f);

    private EntityInspectorPlugin? _entityInspector;

    public override void _EnterTree()
    {
        // BEFORE THE MOUNT, ON THIS THREAD, AND BOTH HALVES MATTER. Godot restores the last-open
        // scene the moment the editor comes up, and res://levels/pubgames/*.tscn is nothing but
        // game:// references. The rest of the loaders are registered by Inject() once the
        // background bank mount lands, which is far too late: every reference resolved in the
        // meantime failed with "Cannot open file" and Godot CACHED the failure, so the scene could
        // never be opened afterwards either.
        //
        // The index is built here too rather than on demand from inside the loaders. That was tried
        // and is worse than the problem: a loader runs on a resource worker thread, and doing file
        // IO and SWF parsing there while a scene's references are being resolved wedged the
        // filesystem scan and left the whole project unmounted. A second on the main thread, once.
        // GUARDED, AND THE MOUNT GOES FIRST IF THIS FAILS. Anything that throws in _EnterTree takes
        // the whole plugin down with it, and until this was wrapped that is exactly what happened:
        // an exception parsing a pub game aborted _EnterTree before StartMount, so NOTHING mounted
        // — no banks, no game:// tree, on a project that has nothing to do with pub games. The
        // indexing used to live inside the mount task, where BnkFileSystem.Mount's own try/catch
        // covered it; moving it out here moved it out of that cover too.
        try
        {
            Refii.FlashGames.Engine_SwfAssets.EnsureLoaders();
            Refii.FlashGames.Engine_SwfAssets.EnsureIndexed();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[bnk_mount] pub games not indexed: {exception.Message}");
        }

        StartMount();
        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged += OnFilesystemChanged;
        GetTree().NodeAdded += OnNodeAdded;
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
        GetTree().NodeAdded -= OnNodeAdded;
        RemoveStreamingLabel();
        RemoveGameRoot();
        RemoveLoaders();
        Refii.FlashGames.Engine_SwfAssets.RemoveLoaders();
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
        Refii.Textures.Engine_TexPathIndex.SetPathSource(EnumerateTexturePaths);
        Refii.Level.Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
        Refii.Level.Engine_StreamingCandidates.SetHintSource(EnumerateStreamingHints);
        Refii.Entities.Engine_EntityIndex.SetResolver(ResolveBytes);
        // The shared runtime database the owned detail pages read. Bound here as well as in
        // Engine_GameAssets so the editor and the running game answer the same, which is the
        // parity the comment in that file is about.
        Refii.Gdb.Engine_GameGdb.SetResolver(ResolveBytes);
        Refii.Equipment.Engine_WeaponIndex.SetResolver(ResolveBytes);
        Refii.Equipment.Engine_ClothingIndex.SetResolver(ResolveBytes);
        Refii.Equipment.Engine_ItemIndex.SetResolver(ResolveBytes);
        Refii.Physics.Engine_PhysicsIndex.SetResolver(ResolveBytes);
        Refii.Hero.Engine_HeroMorphCatalog.SetResolver(ResolveBytes);
        Refii.Hero.Engine_SkeletalMorphs.SetSource(EnumerateAllPaths, ResolveBytes);
        Refii.Hero.Engine_HeroTextureMorph.SetResolver(ResolveBytes);
        Refii.Animations.Engine_AnimCatalog.SetFileSource(EnumerateAllPaths);
        Refii.Hero.Engine_HeroCatalog.SetModelSource(EnumerateAllPaths);

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

        if (_weaponLoader is null)
        {
            _weaponLoader = new Refii.Equipment.Engine_BnkWeaponLoader();
            ResourceLoader.AddResourceFormatLoader(_weaponLoader);
        }

        if (_clothingLoader is null)
        {
            _clothingLoader = new Refii.Equipment.Engine_BnkClothingLoader();
            ResourceLoader.AddResourceFormatLoader(_clothingLoader);
        }

        if (_itemLoader is null)
        {
            _itemLoader = new Refii.Equipment.Engine_BnkItemLoader();
            ResourceLoader.AddResourceFormatLoader(_itemLoader);
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

        // NOT REGISTERED HERE. _EnterTree already did it through Engine_SwfAssets.EnsureLoaders,
        // which owns its own instances. Registering a second set from this class put six SWF
        // loaders in a table Godot caps, and every load past the cap failed with
        // "Condition i >= loader_count is true" — which looks like a mount failure and is not one.
    }

    private void RemoveLoaders()
    {
        Refii.Entities.Engine_EntityIndex.SetResolver(null);
        Refii.Gdb.Engine_GameGdb.SetResolver(null);
        Refii.Equipment.Engine_WeaponIndex.SetResolver(null);
        Refii.Equipment.Engine_ClothingIndex.SetResolver(null);
        Refii.Equipment.Engine_ItemIndex.SetResolver(null);
        Refii.Physics.Engine_PhysicsIndex.SetResolver(null);
        Refii.Hero.Engine_HeroMorphCatalog.SetResolver(null);
        Refii.Hero.Engine_SkeletalMorphs.SetSource(null, null);
        Refii.Hero.Engine_HeroTextureMorph.SetResolver(null);

        if (_weaponLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_weaponLoader);
            _weaponLoader = null;
        }

        if (_clothingLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_clothingLoader);
            _clothingLoader = null;
        }

        if (_itemLoader is not null)
        {
            ResourceLoader.RemoveResourceFormatLoader(_itemLoader);
            _itemLoader = null;
        }

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
        Refii.Animations.Engine_AnimCatalog.SetFileSource(null);
        Refii.Hero.Engine_HeroCatalog.SetModelSource(null);
        Refii.Textures.Engine_TexPathIndex.SetPathSource(null);
        _byFileName = null;
    }

    private IEnumerable<string> EnumerateAllPaths()
    {
        if (_filesystem is null)
        {
            yield break;
        }

        foreach (BnkFile file in _filesystem.EnumerateAllFiles())
        {
            yield return file.VirtualPath;
        }
    }

    private void OnNodeAdded(Node node)
    {
        if (node is not Refii.Entities.Engine_EntityInstance entity ||
            !entity.SceneFilePath.EndsWith(
                Refii.Entities.Engine_EntityIndex.Extension,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Callable.From(() => UnpackEntity(entity)).CallDeferred();
    }

    private void UnpackEntity(Refii.Entities.Engine_EntityInstance entity)
    {
        if (!IsInstanceValid(entity) || !entity.IsInsideTree())
        {
            return;
        }

        Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root is null || !root.IsAncestorOf(entity))
        {
            return;
        }

        entity.SceneFilePath = string.Empty;
        SetOwnerRecursive(entity, root);
    }

    private void ActivateEntity(PackedScene entity)
    {
        Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root is null)
        {
            GD.Print($"[entities] open a scene, then activate '{entity.ResourceName}' " +
                "to drop it in rigged");
            return;
        }

        Node? instance = entity.Instantiate();
        if (instance is null)
        {
            return;
        }

        root.AddChild(instance);
        SetOwnerRecursive(instance, root);

        EditorInterface.Singleton.GetSelection().Clear();
        EditorInterface.Singleton.GetSelection().AddNode(instance);
    }

    private static void SetOwnerRecursive(Node node, Node root)
    {
        node.Owner = root;
        foreach (Node child in node.GetChildren())
        {
            SetOwnerRecursive(child, root);
        }
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

        TreeItem hero = tree.CreateItem(_gameRoot);
        hero.SetText(0, "Hero");
        hero.SetMetadata(0, HeroRootMeta);
        hero.SetSelectable(0, false);
        hero.SetIcon(0, GetFolderIcon());
        hero.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, hero);
        hero.Collapsed = true;

        TreeItem equipment = tree.CreateItem(_gameRoot);
        equipment.SetText(0, "Equipment");
        equipment.SetMetadata(0, EquipmentRootMeta);
        equipment.SetSelectable(0, false);
        equipment.SetIcon(0, GetFolderIcon());
        equipment.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, equipment);
        equipment.Collapsed = true;

        TreeItem items = tree.CreateItem(_gameRoot);
        items.SetText(0, "Items");
        items.SetMetadata(0, ItemsRootMeta);
        items.SetSelectable(0, false);
        items.SetIcon(0, GetFolderIcon());
        items.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, items);
        items.Collapsed = true;

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
               meta == HeroRootMeta ||
               meta == EquipmentRootMeta ||
               meta == WeaponsRootMeta ||
               meta == ClothesRootMeta ||
               meta.StartsWith(WeaponFamilyPrefix, StringComparison.Ordinal) ||
               meta.StartsWith(ClothingSlotPrefix, StringComparison.Ordinal) ||
               meta == ItemsRootMeta ||
               meta.StartsWith(ItemCategoryPrefix, StringComparison.Ordinal) ||
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
            var entity = ResourceLoader.Load<PackedScene>(
                meta, "PackedScene", ResourceLoader.CacheMode.Ignore);

            if (entity is null)
            {
                GD.Print($"[entities] could not load {virtualPath}");
                return;
            }

            ActivateEntity(entity);
            return;
        }

        if (virtualPath.EndsWith(
                Refii.Equipment.Engine_WeaponIndex.Extension,
                StringComparison.OrdinalIgnoreCase))
        {
            ActivateWeapon(meta, virtualPath);
            return;
        }

        if (virtualPath.EndsWith(
                Refii.Equipment.Engine_ClothingIndex.Extension,
                StringComparison.OrdinalIgnoreCase))
        {
            ActivateClothing(meta, virtualPath);
            return;
        }

        if (virtualPath.EndsWith(
                Refii.Equipment.Engine_ItemIndex.Extension,
                StringComparison.OrdinalIgnoreCase))
        {
            ActivateItem(meta, virtualPath);
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
        bool isFlash = Refii.FlashGames.Engine_SwfAssets.RecognisesTexture(virtualPath);
        bool isFlashMesh = Refii.FlashGames.Engine_SwfAssets.RecognisesMesh(virtualPath);
        bool isFlashFont = Refii.FlashGames.Engine_SwfAssets.RecognisesFont(virtualPath);
        if (!isTexture && !isModel && !isAudio && !isDatabase && !isFlash && !isFlashMesh && !isFlashFont)
        {
            GD.Print($"[bnk_mount] {virtualPath}  ({file.Size:N0} bytes, no viewer for this type)");
            return;
        }

        if (isFlashFont)
        {
            var font = ResourceLoader.Load<FontFile>(meta, "FontFile");
            if (font is null)
            {
                GD.PushWarning($"[bnk_mount] {virtualPath}: the face did not build");
                return;
            }

            EditorInterface.Singleton.InspectObject(font);
            GD.Print($"[bnk_mount] {virtualPath} -> FontFile {font.FontName}  ·  {file.SourceArchiveName}");
            return;
        }

        if (isFlashMesh)
        {
            var part = ResourceLoader.Load<ArrayMesh>(meta, "ArrayMesh");
            if (part is null)
            {
                GD.PushWarning($"[bnk_mount] {virtualPath}: the shape did not build");
                return;
            }

            EditorInterface.Singleton.InspectObject(part);
            GD.Print($"[bnk_mount] {virtualPath} -> ArrayMesh, " +
                     $"{(part.GetSurfaceCount() > 0 ? part.SurfaceGetArrayLen(0) / 3 : 0):N0} triangles  ·  {file.SourceArchiveName}");
            return;
        }

        if (isFlash)
        {
            var flash = ResourceLoader.Load<Texture2D>(meta, "Texture2D");
            if (flash is null)
            {
                GD.PushWarning($"[bnk_mount] {virtualPath}: the SWF bitmap did not decode");
                return;
            }

            EditorInterface.Singleton.InspectObject(flash);
            GD.Print($"[bnk_mount] {virtualPath} -> {flash.GetWidth()}x{flash.GetHeight()} " +
                     $"Texture2D  ·  {file.SourceArchiveName}");
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

            // Hear it, don't just look at it — through the SAME player the packaged editor
            // and the running game use, so "it plays in the browser" and "it plays in the
            // game" cannot drift apart.
            Refii.Audio.Engine_AudioPreview.Play(this, stream);

            GD.Print($"[bnk_mount] {virtualPath} -> " +
                     Refii.Audio.Engine_AudioPreview.Describe(stream));
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

    private void ActivateWeapon(string meta, string virtualPath) =>
        ActivateEquipment(meta, virtualPath, "weapons");

    private void ActivateClothing(string meta, string virtualPath) =>
        ActivateEquipment(meta, virtualPath, "clothes");

    private void ActivateItem(string meta, string virtualPath) =>
        ActivateEquipment(meta, virtualPath, "items");

    private void ActivateEquipment(string meta, string virtualPath, string tag)
    {
        var scene = ResourceLoader.Load<PackedScene>(
            meta, "PackedScene", ResourceLoader.CacheMode.Ignore);

        if (scene is null)
        {
            GD.Print($"[{tag}] could not load {virtualPath}");
            return;
        }

        if (scene.Instantiate() is not Refii.Equipment.Engine_EquipmentInstance instance)
        {
            GD.Print($"[{tag}] {virtualPath} did not instance");
            return;
        }

        Node? root = EditorInterface.Singleton.GetEditedSceneRoot();
        if (root is null)
        {
            EditorInterface.Singleton.InspectObject(instance);
            GD.Print($"[{tag}] {scene.ResourceName}: open a level, then activate " +
                     "or drag it in to place it");
            return;
        }

        root.AddChild(instance);
        SetOwnerRecursive(instance, root);

        EditorInterface.Singleton.GetSelection().Clear();
        EditorInterface.Singleton.GetSelection().AddNode(instance);

        GD.Print($"[{tag}] placed {scene.ResourceName} in {root.Name}  ·  " +
                 (instance.Mesh is null
                     ? "NO MESH"
                     : $"{instance.ModelPath}"));
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
            meta != HeroRootMeta &&
            meta != EquipmentRootMeta &&
            meta != WeaponsRootMeta &&
            meta != ClothesRootMeta &&
            !meta.StartsWith(WeaponFamilyPrefix, StringComparison.Ordinal) &&
            !meta.StartsWith(ClothingSlotPrefix, StringComparison.Ordinal) &&
            meta != ItemsRootMeta &&
            !meta.StartsWith(ItemCategoryPrefix, StringComparison.Ordinal) &&
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

            if (meta == HeroRootMeta)
            {
                PopulateHeroes(item);
                continue;
            }

            if (meta == EquipmentRootMeta)
            {
                PopulateEquipment(item);
                continue;
            }

            if (meta == WeaponsRootMeta)
            {
                PopulateWeaponFamilies(item);
                continue;
            }

            if (meta.StartsWith(WeaponFamilyPrefix, StringComparison.Ordinal))
            {
                PopulateWeaponFamily(item, meta[WeaponFamilyPrefix.Length..]);
                continue;
            }

            if (meta == ClothesRootMeta)
            {
                PopulateClothingSlots(item);
                continue;
            }

            if (meta.StartsWith(ClothingSlotPrefix, StringComparison.Ordinal))
            {
                PopulateClothingSlot(item, meta[ClothingSlotPrefix.Length..]);
                continue;
            }

            if (meta == ItemsRootMeta)
            {
                PopulateItemCategories(item);
                continue;
            }

            if (meta.StartsWith(ItemCategoryPrefix, StringComparison.Ordinal))
            {
                PopulateItemCategory(item, meta[ItemCategoryPrefix.Length..]);
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

    private void PopulateHeroes(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Texture2D? icon = GetFileIcon();
        foreach (Refii.Hero.HeroEntry entry in Refii.Hero.Engine_HeroCatalog.Entries)
        {
            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, Refii.Hero.Engine_HeroCatalog.FileNameOf(entry));
            item.SetMetadata(
                0, FilePrefix + Refii.Hero.Engine_HeroCatalog.VirtualPathOf(entry));
            if (icon is not null)
            {
                item.SetIcon(0, icon);
            }
        }
    }

    private void PopulateEquipment(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        TreeItem weapons = tree.CreateItem(parent);
        weapons.SetText(0, "Weapons");
        weapons.SetMetadata(0, WeaponsRootMeta);
        weapons.SetSelectable(0, false);
        weapons.SetIcon(0, GetFolderIcon());
        weapons.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, weapons);
        weapons.Collapsed = true;

        TreeItem clothes = tree.CreateItem(parent);
        clothes.SetText(0, "Clothes");
        clothes.SetMetadata(0, ClothesRootMeta);
        clothes.SetSelectable(0, false);
        clothes.SetIcon(0, GetFolderIcon());
        clothes.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, clothes);
        clothes.Collapsed = true;
    }

    private void PopulateWeaponFamilies(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        foreach (string family in Refii.Equipment.Engine_WeaponIndex.Families)
        {
            int count = Refii.Equipment.Engine_WeaponIndex.InFamily(family).Count;

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, $"{family} ({count})");
            item.SetMetadata(0, WeaponFamilyPrefix + family);
            item.SetSelectable(0, false);
            item.SetIcon(0, GetFolderIcon());
            item.SetIconModulate(0, GameFolderColor);
            AddPlaceholder(tree, item);
            item.Collapsed = true;
        }
    }

    private void PopulateWeaponFamily(TreeItem parent, string family)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        Texture2D? fallback = GetFileIcon();
        List<Refii.Equipment.WeaponEntry> entries =
            Refii.Equipment.Engine_WeaponIndex.InFamily(family);

        var names = new List<string>(entries.Count);
        var hashes = new List<uint>(entries.Count);
        foreach (Refii.Equipment.WeaponEntry entry in entries)
        {
            names.Add(entry.Name);
            hashes.Add(entry.RecordHash);
        }

        List<string> labels = UniqueLabels(names, hashes);

        for (int i = 0; i < entries.Count; i++)
        {
            Refii.Equipment.WeaponEntry entry = entries[i];

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, labels[i]);
            item.SetTooltipText(0, Refii.Equipment.Engine_WeaponIndex.FileNameOf(entry));
            item.SetMetadata(
                0, FilePrefix + Refii.Equipment.Engine_WeaponIndex.VirtualPathOf(entry));
            ApplyEquipmentIcon(item, entry.Detail.IconTex, fallback);
        }
    }

    private void PopulateClothingSlots(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        foreach (Refii.Equipment.ClothingSlot slot in
                 Refii.Equipment.Engine_ClothingIndex.Slots())
        {
            string folder = Refii.Equipment.Engine_ClothingSlots.FolderOf(slot);
            int count = Refii.Equipment.Engine_ClothingIndex.InSlot(slot).Count;

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, $"{folder} ({count})");
            item.SetMetadata(0, ClothingSlotPrefix + folder);
            item.SetSelectable(0, false);
            item.SetIcon(0, GetFolderIcon());
            item.SetIconModulate(0, GameFolderColor);
            AddPlaceholder(tree, item);
            item.Collapsed = true;
        }
    }

    private void PopulateClothingSlot(TreeItem parent, string folder)
    {
        Tree? tree = ResolveTree();
        if (tree is null ||
            !Refii.Equipment.Engine_ClothingSlots.TryParseFolder(
                folder, out Refii.Equipment.ClothingSlot slot))
        {
            return;
        }

        Texture2D? fallback = GetFileIcon();
        List<Refii.Equipment.ClothingEntry> entries =
            Refii.Equipment.Engine_ClothingIndex.InSlot(slot);

        var names = new List<string>(entries.Count);
        var hashes = new List<uint>(entries.Count);
        foreach (Refii.Equipment.ClothingEntry entry in entries)
        {
            names.Add(entry.Name);
            hashes.Add(entry.RecordHash);
        }

        List<string> labels = UniqueLabels(names, hashes);

        for (int i = 0; i < entries.Count; i++)
        {
            Refii.Equipment.ClothingEntry entry = entries[i];

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, labels[i]);
            item.SetTooltipText(0, Refii.Equipment.Engine_ClothingIndex.FileNameOf(entry));
            item.SetMetadata(
                0, FilePrefix + Refii.Equipment.Engine_ClothingIndex.VirtualPathOf(entry));
            ApplyEquipmentIcon(item, entry.Detail.IconTex, fallback);
        }
    }

    private void PopulateItemCategories(TreeItem parent)
    {
        Tree? tree = ResolveTree();
        if (tree is null)
        {
            return;
        }

        foreach (int category in Refii.Equipment.Engine_ItemIndex.Categories)
        {
            string folder = Refii.Equipment.Engine_ItemCategories.FolderOf(category);
            int count = Refii.Equipment.Engine_ItemIndex.InCategory(category).Count;

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, $"{folder} ({count})");
            item.SetTooltipText(
                0, Refii.Equipment.Engine_ItemCategories.DescribeCategory(category));
            item.SetMetadata(0, ItemCategoryPrefix + folder);
            item.SetSelectable(0, false);
            item.SetIcon(0, GetFolderIcon());
            item.SetIconModulate(0, GameFolderColor);
            AddPlaceholder(tree, item);
            item.Collapsed = true;
        }
    }

    private void PopulateItemCategory(TreeItem parent, string folder)
    {
        Tree? tree = ResolveTree();
        if (tree is null ||
            !Refii.Equipment.Engine_ItemCategories.TryParseFolder(folder, out int category))
        {
            return;
        }

        Texture2D? fallback = GetFileIcon();
        List<Refii.Equipment.ItemEntry> entries =
            Refii.Equipment.Engine_ItemIndex.InCategory(category);

        var names = new List<string>(entries.Count);
        var hashes = new List<uint>(entries.Count);
        foreach (Refii.Equipment.ItemEntry entry in entries)
        {
            names.Add(entry.Name);
            hashes.Add(entry.RecordHash);
        }

        List<string> labels = UniqueLabels(names, hashes);

        for (int i = 0; i < entries.Count; i++)
        {
            Refii.Equipment.ItemEntry entry = entries[i];

            TreeItem item = tree.CreateItem(parent);
            item.SetText(0, labels[i]);
            string mesh = entry.HasModel ? entry.ModelPath : "no mesh";
            item.SetTooltipText(
                0,
                Refii.Equipment.Engine_ItemIndex.FileNameOf(entry) +
                    System.Environment.NewLine + mesh);
            item.SetMetadata(
                0, FilePrefix + Refii.Equipment.Engine_ItemIndex.VirtualPathOf(entry));
            ApplyEquipmentIcon(item, entry.Detail.IconTex, fallback);

            if (!entry.HasModel)
            {
                item.SetCustomColor(0, MeshlessItemColor);
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

        if (Refii.FlashGames.Engine_SwfAssets.RecognisesTexture(path))
        {
            ApplyFlashThumbnail(item, path);
            return;
        }

        if (Refii.FlashGames.Engine_SwfAssets.RecognisesMesh(path))
        {
            Texture2D? mesh = GetClassIcon(new[] { "MeshInstance2D", "ArrayMesh", "Mesh" });
            if (mesh is not null)
            {
                item.SetIcon(0, mesh);
                item.SetIconModulate(0, Colors.White);
            }

            return;
        }

        if (Refii.FlashGames.Engine_SwfAssets.RecognisesFont(path))
        {
            Texture2D? font = GetClassIcon(new[] { "FontFile", "Font", "Label" });
            if (font is not null)
            {
                item.SetIcon(0, font);
                item.SetIconModulate(0, Colors.White);
            }

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

    private static void ApplyFlashThumbnail(TreeItem item, string virtualPath)
    {
        try
        {
            Image? source = Refii.FlashGames.Engine_SwfAssets.TryGetImage(virtualPath);
            if (source is null)
            {
                return;
            }

            var image = (Image)source.Duplicate();
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

    private static void ApplyTextureThumbnail(TreeItem item, BnkFile file)
    {
        ImageTexture? thumb = BuildThumbnail(file);
        if (thumb is null)
        {
            return;
        }

        item.SetIcon(0, thumb);
        item.SetIconModulate(0, Colors.White);
        item.SetIconMaxWidth(0, ThumbnailSize);
    }

    private static ImageTexture? BuildThumbnail(BnkFile file)
    {
        if (file.Size == 0 || file.Size > MaxThumbnailBytes)
        {
            return null;
        }

        try
        {
            Image? image = Refii.Textures.Engine_TexImage.TryCreateImage(
                file.ReadAllBytes(), out _);
            if (image is null)
            {
                return null;
            }

            image.ClearMipmaps();
            image.Decompress();
            int w = image.GetWidth();
            int h = image.GetHeight();
            if (w <= 0 || h <= 0)
            {
                return null;
            }

            float scale = (float)ThumbnailSize / Math.Max(w, h);
            image.Resize(
                Math.Max(1, (int)(w * scale)),
                Math.Max(1, (int)(h * scale)),
                Image.Interpolation.Bilinear);

            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private readonly Dictionary<string, ImageTexture?> _equipmentIcons =
        new(StringComparer.OrdinalIgnoreCase);

    private void ApplyEquipmentIcon(TreeItem item, string iconTex, Texture2D? fallback)
    {
        ImageTexture? icon = EquipmentIcon(iconTex);

        if (icon is not null)
        {
            item.SetIcon(0, icon);
            item.SetIconModulate(0, Colors.White);
            item.SetIconMaxWidth(0, ThumbnailSize);
            return;
        }

        if (fallback is not null)
        {
            item.SetIcon(0, fallback);
        }
    }

    private ImageTexture? EquipmentIcon(string iconTex)
    {
        if (iconTex.Length == 0 || _filesystem is null)
        {
            return null;
        }

        if (_equipmentIcons.TryGetValue(iconTex, out ImageTexture? cached))
        {
            return cached;
        }

        ImageTexture? built = null;
        if (_filesystem.TryGetFile(iconTex, out BnkFile file) ||
            _filesystem.TryGetFile(iconTex.Replace('\\', '/'), out file))
        {
            built = BuildThumbnail(file);
        }

        _equipmentIcons[iconTex] = built;
        return built;
    }

    private static List<string> UniqueLabels(IReadOnlyList<string> names, IReadOnlyList<uint> hashes)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            counts[name] = counts.TryGetValue(name, out int seen) ? seen + 1 : 1;
        }

        var labels = new List<string>(names.Count);
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i].Length != 0 ? names[i] : "(unnamed)";
            labels.Add(counts.TryGetValue(names[i], out int total) && total > 1
                ? $"{name}  [{hashes[i]:X8}]"
                : name);
        }

        return labels;
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
