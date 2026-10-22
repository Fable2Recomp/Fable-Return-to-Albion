#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Refii.Bnk;
using Refii.EditorTools;
using Refii.Mods;

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
    public const string BnkMetaKey = "__bnk_meta__";

    /// <summary>
    /// Attaches custom metadata to a Game:// tree item.
    /// Sets column 0 metadata to "res://" so that Godot's FileSystemDock tooltip handler
    /// evaluates DirAccess::exists("res://") as true and returns nullptr immediately,
    /// preventing the '!FileAccess::exists(p_path)' console error when hovered.
    /// </summary>
    public static void SetItemMeta(TreeItem item, string meta)
    {
        item.SetMeta(BnkMetaKey, meta);
        item.SetMetadata(0, "res://");
    }

    /// <summary>
    /// Retrieves custom metadata from a Game:// tree item, falling back to column 0 metadata.
    /// </summary>
    public static string GetItemMeta(TreeItem? item)
    {
        if (item is null || !GodotObject.IsInstanceValid(item))
        {
            return string.Empty;
        }

        if (item.HasMeta(BnkMetaKey))
        {
            return item.GetMeta(BnkMetaKey).AsString();
        }

        return item.GetMetadata(0).AsString();
    }

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
    private FableFxInspectorPlugin? _fxInspector;
    private readonly EditorScanRequest _extensionScan = new();
    private Refii.Assets.UI.ModdingHubPanel? _modStudioMainPanel;
    private Refii.Assets.UI.ModStudioDockPanel? _modStudioDockPanel;
    private Refii.Assets.UI.LevelPerspectiveToolbar? _levelToolbar;

    public override void _EnterTree()
    {
        SetProcess(false);
        Instance = this;
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
        EditorFileSystem resFs = EditorInterface.Singleton.GetResourceFilesystem();
        ConnectFilesystemSignals(resFs);
        AddStreamingLabel();
        _entityInspector = new EntityInspectorPlugin();
        AddInspectorPlugin(_entityInspector);
        _fxInspector = new FableFxInspectorPlugin();
        AddInspectorPlugin(_fxInspector);
        SetupContextMenu();
        SetupModStudio();
        EnsureFileExtensionsRecognized();
    }

    public override void _ExitTree()
    {
        _extensionScan.Cancel();
        SetProcess(false);
        if (_entityInspector is not null)
        {
            RemoveInspectorPlugin(_entityInspector);
            _entityInspector = null;
        }

        if (_fxInspector is not null)
        {
            RemoveInspectorPlugin(_fxInspector);
            _fxInspector = null;
        }

        EditorFileSystem? resFs = EditorInterface.Singleton?.GetResourceFilesystem();
        if (resFs is not null && GodotObject.IsInstanceValid(resFs))
        {
            DisconnectFilesystemSignals(resFs);
        }
        RemoveStreamingLabel();
        RemoveGameRoot();
        RemoveLoaders();
        Refii.FlashGames.Engine_SwfAssets.RemoveLoaders();
        RemoveContextMenu();
        RemoveModStudio();

        if (Instance == this)
        {
            Instance = null;
        }

        if (_bridge is not null && GodotObject.IsInstanceValid(_bridge))
        {
            try
            {
                _bridge.Call("uninstall");
            }
            catch
            {
            }
            _bridge = null;
        }

        if (_tree is not null && GodotObject.IsInstanceValid(_tree) && _collapsedHooked)
        {
            _tree.ItemCollapsed -= OnItemCollapsed;
            _collapsedHooked = false;
        }

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

    private void ConnectFilesystemSignals(GodotObject filesystem)
    {
        var changed = new Callable(this, MethodName.OnFilesystemChanged);
        var sources = new Callable(this, MethodName.OnSourcesChanged);
        if (!filesystem.IsConnected(EditorFileSystem.SignalName.FilesystemChanged, changed))
            filesystem.Connect(EditorFileSystem.SignalName.FilesystemChanged, changed);
        if (!filesystem.IsConnected(EditorFileSystem.SignalName.SourcesChanged, sources))
            filesystem.Connect(EditorFileSystem.SignalName.SourcesChanged, sources);
    }

    private void DisconnectFilesystemSignals(GodotObject filesystem)
    {
        var changed = new Callable(this, MethodName.OnFilesystemChanged);
        var sources = new Callable(this, MethodName.OnSourcesChanged);
        if (filesystem.IsConnected(EditorFileSystem.SignalName.FilesystemChanged, changed))
            filesystem.Disconnect(EditorFileSystem.SignalName.FilesystemChanged, changed);
        if (filesystem.IsConnected(EditorFileSystem.SignalName.SourcesChanged, sources))
            filesystem.Disconnect(EditorFileSystem.SignalName.SourcesChanged, sources);
    }

    private void OnFilesystemChanged()
    {
        if (!IsGameRootMounted())
        {
            CallDeferred(nameof(Inject));
        }
    }

    private void OnSourcesChanged(bool _)
    {
        if (!IsGameRootMounted())
        {
            CallDeferred(nameof(Inject));
        }
    }

    private bool _injectRetryPending;

    private bool IsGameRootMounted() =>
        _gameRoot is not null && GodotObject.IsInstanceValid(_gameRoot) && _gameRoot.GetTree() is not null;

    private async void ScheduleInjectRetry()
    {
        if (_injectRetryPending)
        {
            return;
        }

        _injectRetryPending = true;
        try
        {
            for (int i = 0; i < 25; i++)
            {
                await ToSignal(GetTree().CreateTimer(0.20), SceneTreeTimer.SignalName.Timeout);
                if (IsGameRootMounted())
                {
                    break;
                }

                Inject();
                if (IsGameRootMounted())
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[bnk_mount] Inject retry error: {ex.Message}");
        }
        finally
        {
            _injectRetryPending = false;
        }
    }

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
                // THE EDITOR BROWSES WHAT THE GAME WILL LOAD, mods included — same mount, same
                // order. An editor that showed only the shipped assets would report a modded
                // file as missing and export a mod against the wrong base.
                string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
                var order = Refii.Mods.ModLoadOrder.Read(modsRoot);

                var overlays = Refii.Mods.ModIndex.ResolveOverlays(
                    modsRoot, order, Refii.Mods.ModWorkspace.DiscoverModInstalls(modsRoot));
                mounted = BnkFileSystem.Mount(
                    gameDirectory,
                    overlays: Refii.Mods.ModIndex.ToMountOverlays(overlays));
                foreach ((string pinned, string owner) in order.Pins)
                {
                    mounted.Promote(pinned, owner);
                }

                Refii.Mods.ModRowOverlays.Apply(mounted, overlays);
                Refii.Mods.ModImageOverlays.Apply(mounted, overlays);
                foreach (string line in Refii.Fx.Engine_FxModEffects.Load(overlays))
                {
                    if (line.StartsWith("REFUSED", StringComparison.Ordinal))
                    {
                        GD.PushWarning($"[bnk_mount] fx effect {line}");
                    }
                }

                var conversions = Refii.Mods.ModMeshOverlays.Apply(mounted, overlays);
                foreach (var c in conversions)
                {
                    if (c.Ok)
                    {
                        GD.Print($"[bnk_mount] model {Refii.Mods.ModMeshOverlays.Describe(c)}");
                    }
                    else
                    {
                        GD.PushWarning($"[bnk_mount] model {Refii.Mods.ModMeshOverlays.Describe(c)}");
                    }
                }

                // Compose multi-mod GDB Title Updates and .save layer maps in load order (or purge if no overlays enabled):
                if (overlays.Count == 0)
                {
                    Refii.Gdb.GdbPatchCompositor.PurgeCache(modsRoot);
                }
                else
                {
                    Refii.Gdb.GdbPatchCompositor.Apply(mounted, overlays, modsRoot);
                    Refii.Gdb.SaveMapCompositor.Apply(mounted, overlays);
                }
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[bnk_mount] {exception.Message}");
            }

            _filesystem = mounted;
            Refii.Gdb.Engine_GameGdb.Invalidate();
            Refii.Gdb.Engine_BnkTextBankLoader.Invalidate();
            Refii.Equipment.Engine_WeaponIndex.Invalidate();
            Refii.Equipment.Engine_ItemIndex.Invalidate();
            Refii.Equipment.Engine_ClothingIndex.Invalidate();
            Refii.Entities.Engine_EntityIndex.Invalidate();
            _ = Refii.Equipment.Engine_WeaponIndex.Families;
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

    /// <summary>Gets the currently mounted virtual bank filesystem, if available.</summary>
    public BnkFileSystem? FileSystem => _filesystem;

    private byte[]? ResolveBytes(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return null;
        }

        string normalized = virtualPath.Replace('\\', '/').TrimStart('/');
        if (normalized.StartsWith("mods/", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? modBytes = EditorModValueService.TryReadFromActiveModPackage(normalized);
            if (modBytes is not null && modBytes.Length > 0)
            {
                return modBytes;
            }
        }

        if (_filesystem is not null && _filesystem.TryGetFile(normalized, out BnkFile file))
        {
            try
            {
                return file.ReadAllBytes();
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    public byte[]? ResolveBaseBytes(string virtualPath)
    {
        if (_filesystem is null)
        {
            return null;
        }

        return _filesystem.ReadBaseBytes(virtualPath);
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
        Refii.Gdb.Engine_GameGdb.SetBaseResolver(ResolveBaseBytes);
        Refii.Gdb.Engine_BnkTextBankLoader.SetResolver(ResolveBytes);
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

        // When running in headless mode (e.g. project export / packaging) or when the engine
        // is shutting down, Godot's core cleanup dismantles ResourceLoader before plugin _ExitTree runs.
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            _weaponLoader = null;
            _clothingLoader = null;
            _itemLoader = null;
            _entityLoader = null;
            _fxLoader = null;
            _textureLoader = null;
            _meshLoader = null;
            _audioLoader = null;
            _gdbLoader = null;
            _terrainLoader = null;
            _albedoLoader = null;
            _terrainMaterialLoader = null;
            Refii.Textures.Engine_BnkTextureLoader.SetResolver(null);
            return;
        }

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

        // Refresh placed scene instances now that virtual filesystem and GDB database are remounted:
        EditorTools.EditorModValueService.RefreshSceneEquipment();

        Tree? tree = ResolveTree();
        TreeItem? root = tree?.GetRoot();
        if (tree is null || root is null)
        {
            if (_filesystem is not null)
            {
                ScheduleInjectRetry();
            }
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
        SetItemMeta(_gameRoot, ContainerMeta);
        _gameRoot.SetSelectable(0, false);
        _gameRoot.SetIcon(0, GetFolderIcon());
        _gameRoot.SetIconModulate(0, GameFolderColor);
        _gameRoot.Collapsed = false;

        TreeItem assets = tree.CreateItem(_gameRoot);
        assets.SetText(0, "Assets");
        SetItemMeta(assets, RootMeta);
        assets.SetSelectable(0, false);
        assets.SetIcon(0, GetFolderIcon());
        assets.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, assets);
        assets.Collapsed = true;

        TreeItem databases = tree.CreateItem(_gameRoot);
        databases.SetText(0, "Database");
        SetItemMeta(databases, DatabaseRootMeta);
        databases.SetSelectable(0, false);
        databases.SetIcon(0, GetFolderIcon());
        databases.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, databases);
        databases.Collapsed = true;

        TreeItem entities = tree.CreateItem(_gameRoot);
        entities.SetText(0, "Entities");
        SetItemMeta(entities, EntitiesRootMeta);
        entities.SetSelectable(0, false);
        entities.SetIcon(0, GetFolderIcon());
        entities.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, entities);
        entities.Collapsed = true;

        TreeItem hero = tree.CreateItem(_gameRoot);
        hero.SetText(0, "Hero");
        SetItemMeta(hero, HeroRootMeta);
        hero.SetSelectable(0, false);
        hero.SetIcon(0, GetFolderIcon());
        hero.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, hero);
        hero.Collapsed = true;

        TreeItem equipment = tree.CreateItem(_gameRoot);
        equipment.SetText(0, "Equipment");
        SetItemMeta(equipment, EquipmentRootMeta);
        equipment.SetSelectable(0, false);
        equipment.SetIcon(0, GetFolderIcon());
        equipment.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, equipment);
        equipment.Collapsed = true;

        TreeItem items = tree.CreateItem(_gameRoot);
        items.SetText(0, "Items");
        SetItemMeta(items, ItemsRootMeta);
        items.SetSelectable(0, false);
        items.SetIcon(0, GetFolderIcon());
        items.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, items);
        items.Collapsed = true;

        TreeItem effects = tree.CreateItem(_gameRoot);
        effects.SetText(0, "Effects");
        SetItemMeta(effects, EffectsRootMeta);
        effects.SetSelectable(0, false);
        effects.SetIcon(0, GetFolderIcon());
        effects.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, effects);
        effects.Collapsed = true;

        TreeItem levels = tree.CreateItem(_gameRoot);
        levels.SetText(0, "Levels");
        SetItemMeta(levels, LevelsRootMeta);
        levels.SetSelectable(0, false);
        levels.SetIcon(0, GetFolderIcon());
        levels.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, levels);
        levels.Collapsed = true;

        TreeItem videos = tree.CreateItem(_gameRoot);
        videos.SetText(0, "Videos");
        SetItemMeta(videos, VideosRootMeta);
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
            Callable.From<TreeItem>(ActivateBnkItem),
            Callable.From<TreeItem, Vector2>(OnItemRmb));

        Node? dock = FindByClass(GetTree().Root, "FileSystemDock", 0);
        if (dock is not null)
        {
            HookDockPopups(dock);
        }
    }

    private const int MenuInspectFileSystemTu = 98765;
    private readonly HashSet<ulong> _hookedPopups = new();

    private void HookDockPopups(Node node)
    {
        if (node is PopupMenu popup && !_hookedPopups.Contains(popup.GetInstanceId()))
        {
            _hookedPopups.Add(popup.GetInstanceId());
            popup.AboutToPopup += () =>
            {
                int existing = popup.GetItemIndex(MenuInspectFileSystemTu);
                if (existing != -1)
                {
                    popup.RemoveItem(existing);
                }

                string[] selected = EditorInterface.Singleton.GetSelectedPaths();
                if (selected != null && selected.Any(p => p.EndsWith(".tu", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase)))
                {
                    popup.AddSeparator();
                    popup.AddItem("Inspect GDB Title Update (.tu)", MenuInspectFileSystemTu);
                }
            };
            popup.IdPressed += id =>
            {
                if (id == MenuInspectFileSystemTu)
                {
                    string[] selected = EditorInterface.Singleton.GetSelectedPaths();
                    string? target = selected?.FirstOrDefault(p => p.EndsWith(".tu", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(target))
                    {
                        string diskPath = target.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                            ? ProjectSettings.GlobalizePath(target)
                            : (File.Exists(target) ? target : ProjectSettings.GlobalizePath(target));

                        if (File.Exists(diskPath))
                        {
                            string content = Refii.Gdb.GdbPrettyPrinter.Print(diskPath, Refii.Gdb.Engine_GameGdb.BaseDatabase);
                            ShowTextPopup($"GDB Title Update Inspection — {Path.GetFileName(diskPath)}", content);
                        }
                    }
                }
            };
        }

        foreach (Node child in node.GetChildren())
        {
            HookDockPopups(child);
        }
    }

    public override void _Process(double delta)
    {
        EditorFileSystem filesystem = EditorInterface.Singleton.GetResourceFilesystem();
        _extensionScan.TryRun(filesystem.IsScanning(), filesystem.IsImporting(), filesystem.ScanSources);
        SetProcess(_extensionScan.Pending);
    }

    private void EnsureFileExtensionsRecognized()
    {
        try
        {
            EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
            if (settings is null)
            {
                return;
            }

            bool changed = false;

            void EnsureExtension(string settingKey, string defaultVal, string[] newExts)
            {
                string current = settings.HasSetting(settingKey)
                    ? settings.GetSetting(settingKey).AsString()
                    : defaultVal;

                var parts = current.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                bool modified = false;
                foreach (string ext in newExts)
                {
                    if (!parts.Contains(ext, StringComparer.OrdinalIgnoreCase))
                    {
                        parts.Add(ext);
                        modified = true;
                    }
                }

                if (modified)
                {
                    // The setting only, never its initial value: a value equal to its default is
                    // not persisted, and this ran — with a rescan — on every editor start.
                    settings.SetSetting(settingKey, string.Join(",", parts));
                    changed = true;
                }
            }

            EnsureExtension("docks/filesystem/other_file_extensions", "ico,icns", new[] { "tu", "gdb" });
            EnsureExtension("docks/filesystem/textfile_extensions", "txt,md,cfg,ini,log,json,yml,yaml,toml,xml", new[] { "tu", "gdb" });

            // NEVER A FULL Scan() FROM THIS PLUGIN. A full scan replaces the editor's directory
            // tree and frees the old one, while Godot's script-documentation thread
            // (EditorHelp::_regen_script_doc_thread, started after the first scan and loading every
            // script in the project) holds raw pointers into it — the editor died that way on
            // 2026-10-17, four seconds after a plugin Scan() finished. ScanSources() re-reads the
            // extension lists and updates the tree in place; during the first scan, where plugins
            // are enabled from, it only marks a scan pending, and the first scan itself reads the
            // lists after the plugins have loaded.
            if (changed)
            {
                _extensionScan.Request();
                SetProcess(true);
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[bnk_mount] Failed to register file extensions in EditorSettings: {ex.Message}");
        }
    }

    /// <summary>Context menu displayed when right-clicking assets under the Game:// root in the FileSystem dock.</summary>
    private PopupMenu? _contextMenu;

    /// <summary>The picker for a glTF to import over the right-clicked model, and that model's path.</summary>
    private FileDialog? _glbImportDialog;
    private string _glbImportTarget = string.Empty;

    /// <summary>Dialog for changing the active mod workspace folder name.</summary>
    private ConfirmationDialog? _modNameDialog;

    /// <summary>Text input field inside the mod name configuration dialog.</summary>
    private LineEdit? _modNameEdit;

    /// <summary>Dropdown selector listing all discovered .f2mod mods.</summary>
    private OptionButton? _modOptionBtn;

    /// <summary>Virtual path of the currently selected asset in the Game:// tree.</summary>
    private string _contextVirtualPath = string.Empty;

    /// <summary>Physical disk path of the selected asset in the res:// filesystem dock, if applicable.</summary>
    private string _contextDiskPath = string.Empty;

    /// <summary>Resolved 3D model paths (.mdl) associated with the right-clicked item.</summary>
    private readonly List<string> _contextModelPaths = new();

    /// <summary>Resolved texture paths (.tex) associated with the right-clicked item.</summary>
    private readonly List<string> _contextTexturePaths = new();

    /// <summary>Resolved GUI/inventory icon texture paths associated with the right-clicked item.</summary>
    private readonly List<string> _contextIconPaths = new();

    /// <summary>Reference to the currently right-clicked TreeItem.</summary>
    private TreeItem? _contextItem;

    public static BnkMountPlugin? Instance { get; private set; }

    private const int MenuExportGlb = 100;
    private const int MenuExportPng = 101;
    private const int MenuExportPbr = 102;
    private const int MenuExportIcon = 103;
    private const int MenuOpenModFolder = 104;
    private const int MenuChangeActiveMod = 105;
    private const int MenuReloadOverlays = 106;
    private const int MenuEditItemValues = 107;
    private const int MenuInspectModTu = 108;
    private const int MenuInspectTu = 109;
    private const int MenuOpenModStudioWorkspace = 110;
    private const int MenuImportGlb = 111;

    private Refii.Equipment.ItemDetail? _contextItemDetail;
    private ModItemEditorDialog? _itemEditorDialog;

    /// <summary>
    /// Opens the visual item property editor dialog for the specified item.
    /// </summary>
    public void OpenModItemEditor(Refii.Equipment.ItemDetail detail)
    {
        SetupItemEditorDialog();
        _itemEditorDialog?.OpenForItem(detail);
    }

    private void SetupItemEditorDialog()
    {
        if (_itemEditorDialog is not null && IsInstanceValid(_itemEditorDialog))
        {
            return;
        }

        _itemEditorDialog = new ModItemEditorDialog();
        EditorInterface.Singleton.GetBaseControl().AddChild(_itemEditorDialog);
    }

    /// <summary>
    /// Creates and registers the asset browser right-click context menu under the editor's base control.
    /// </summary>
    private void SetupContextMenu()
    {
        if (_contextMenu is not null && IsInstanceValid(_contextMenu))
        {
            return;
        }

        _contextMenu = new PopupMenu();
        _contextMenu.IdPressed += OnContextMenuIdPressed;
        EditorInterface.Singleton.GetBaseControl().AddChild(_contextMenu);

        Node? dock = FindByClass(GetTree().Root, "FileSystemDock", 0);
        if (dock is not null)
        {
            HookDockPopups(dock);
        }
    }

    /// <summary>
    /// Picks a `.glb`/`.gltf` to stand in for <paramref name="modelPath"/>: it is converted against
    /// the shipped model first and placed in the mod as `<path>.glb` only if the mount would
    /// accept it, then the overlays are remounted so the Studio serves the mod's mesh.
    /// </summary>
    private void OpenGlbImportDialog(string modelPath)
    {
        _glbImportTarget = modelPath;
        if (_glbImportDialog is null || !IsInstanceValid(_glbImportDialog))
        {
            _glbImportDialog = new FileDialog
            {
                Access = FileDialog.AccessEnum.Filesystem,
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Size = new Vector2I(760, 520),
            };
            _glbImportDialog.AddFilter("*.glb, *.gltf ; glTF models (.glb, .gltf)");
            _glbImportDialog.FileSelected += OnGlbImportSelected;
            EditorInterface.Singleton.GetBaseControl().AddChild(_glbImportDialog);
        }

        _glbImportDialog.Title = $"Import glTF over {Path.GetFileName(modelPath)} into [{EditorModExporter.ActiveModName}]";
        _glbImportDialog.PopupCentered(new Vector2I(760, 520));
    }

    private void OnGlbImportSelected(string sourcePath)
    {
        if (EditorModExporter.ImportModelGlb(
                _glbImportTarget, sourcePath, _filesystem,
                out string placed, out string summary, out string error))
        {
            GD.Print($"[mod_export] Imported {Path.GetFileName(sourcePath)} over {_glbImportTarget}: {summary}; placed at {placed}");
            ReloadModOverlays();
        }
        else
        {
            GD.PushWarning($"[mod_export] {Path.GetFileName(sourcePath)} was not imported over {_glbImportTarget}: {error}");
        }
    }

    /// <summary>
    /// Unhooks signals and frees the context menu and dialogs when the plugin exits the tree.
    /// </summary>
    private void RemoveContextMenu()
    {
        if (_contextMenu is not null && IsInstanceValid(_contextMenu))
        {
            _contextMenu.IdPressed -= OnContextMenuIdPressed;
            _contextMenu.QueueFree();
            _contextMenu = null;
        }

        if (_glbImportDialog is not null && IsInstanceValid(_glbImportDialog))
        {
            _glbImportDialog.FileSelected -= OnGlbImportSelected;
            _glbImportDialog.QueueFree();
            _glbImportDialog = null;
        }

        if (_modNameDialog is not null && IsInstanceValid(_modNameDialog))
        {
            _modNameDialog.QueueFree();
            _modNameDialog = null;
        }

        if (_itemEditorDialog is not null && IsInstanceValid(_itemEditorDialog))
        {
            _itemEditorDialog.QueueFree();
            _itemEditorDialog = null;
        }
    }

    public override bool _HasMainScreen() => true;

    public override string _GetPluginName() => "Mod Studio";

    public override Texture2D? _GetPluginIcon()
    {
        try
        {
            return EditorInterface.Singleton?.GetBaseControl()?.GetThemeIcon("Tools", "EditorIcons");
        }
        catch
        {
            return null;
        }
    }

    public override void _MakeVisible(bool visible)
    {
        if (_modStudioMainPanel is not null && GodotObject.IsInstanceValid(_modStudioMainPanel))
        {
            _modStudioMainPanel.Visible = visible;
            if (visible)
            {
                _modStudioMainPanel.RefreshModList();
            }
        }
    }

    /// <summary>
    /// Initializes and mounts the Fable 2 Mod Studio into both the Godot main screen workspace
    /// and the left dock bar. Defaults the editor to the main screen workspace on startup.
    /// </summary>
    private void SetupModStudio()
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            // 1. Mount as Main Screen workspace
            _modStudioMainPanel = new Refii.Assets.UI.ModdingHubPanel();
            _modStudioMainPanel.Visible = false;
            EditorInterface.Singleton.GetEditorMainScreen().AddChild(_modStudioMainPanel);

            // 2. Mount as Right Bar Dock (alongside Inspector, collapsed by default)
            _modStudioDockPanel = new Refii.Assets.UI.ModStudioDockPanel();
            _modStudioDockPanel.Name = "Mod Studio";
#pragma warning disable CS0618 // AddControlToDock is deprecated in Godot 4.4+ in favor of AddDock(EditorDock)
            AddControlToDock(DockSlot.RightUl, _modStudioDockPanel);
#pragma warning restore CS0618

            // 3. Mount 3D Level Perspective & Save Layer Toolbar in SpatialEditorMenu
            _levelToolbar = new Refii.Assets.UI.LevelPerspectiveToolbar();
            AddControlToContainer(CustomControlContainer.SpatialEditorMenu, _levelToolbar);
            SceneChanged += OnEditorSceneChanged;
            SceneSaved += OnEditorSceneSaved;

            // Subscribe after initial panel construction so startup does not trigger premature filesystem scans
            EditorModExporter.ActiveModChanged += OnActiveModChanged;
            EditorModExporter.ModContentChanged += OnModContentChangedFor3DPreview;

            // Ensure the active mod's creator directory and favorite entry are prepared before Godot's initial scan
            SyncActiveModCreatorFavorite(EditorModExporter.ActiveModName, triggerScan: false);

            // 4. Switch by default to the Main Screen workspace on editor startup
            Callable.From(() =>
            {
                if (_modStudioMainPanel is not null && GodotObject.IsInstanceValid(_modStudioMainPanel))
                {
                    EditorInterface.Singleton.SetMainScreenEditor("Mod Studio");
                    _modStudioMainPanel.RefreshModList();
                }
                if (_modStudioDockPanel is not null && GodotObject.IsInstanceValid(_modStudioDockPanel))
                {
                    _modStudioDockPanel.RefreshModList();
                }
                _levelToolbar?.RefreshForScene(EditorInterface.Singleton.GetEditedSceneRoot());
            }).CallDeferred();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[bnk_mount] Could not initialize Mod Studio: {ex.Message}");
        }
    }

    /// <summary>
    /// Cleans up both the Main Screen and Dock instances of Mod Studio on plugin exit.
    /// </summary>
    private void RemoveModStudio()
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EditorModExporter.ActiveModChanged -= OnActiveModChanged;
        EditorModExporter.ModContentChanged -= OnModContentChangedFor3DPreview;
        SceneChanged -= OnEditorSceneChanged;
        SceneSaved -= OnEditorSceneSaved;

        if (_levelToolbar is not null && GodotObject.IsInstanceValid(_levelToolbar))
        {
            RemoveControlFromContainer(CustomControlContainer.SpatialEditorMenu, _levelToolbar);
            _levelToolbar.QueueFree();
            _levelToolbar = null;
        }

        if (_modStudioDockPanel is not null && GodotObject.IsInstanceValid(_modStudioDockPanel))
        {
#pragma warning disable CS0618 // RemoveControlFromDocks is deprecated in Godot 4.4+ in favor of RemoveDock(EditorDock)
            RemoveControlFromDocks(_modStudioDockPanel);
#pragma warning restore CS0618
            _modStudioDockPanel.QueueFree();
            _modStudioDockPanel = null;
        }

        if (_modStudioMainPanel is not null && GodotObject.IsInstanceValid(_modStudioMainPanel))
        {
            if (_modStudioMainPanel.GetParent() is not null)
            {
                _modStudioMainPanel.GetParent().RemoveChild(_modStudioMainPanel);
            }
            _modStudioMainPanel.QueueFree();
            _modStudioMainPanel = null;
        }
    }

    private void OnEditorSceneChanged(Node? sceneRoot)
    {
        _levelToolbar?.RefreshForScene(sceneRoot);
    }

    private void OnEditorSceneSaved(string filepath)
    {
        try
        {
            Node? editedRoot = EditorInterface.Singleton?.GetEditedSceneRoot();
            Node3D? levelRoot = Refii.Level.LevelRecordMetadata.FindLevelRootNode(editedRoot);
            if (levelRoot is null)
            {
                return;
            }

            if (Refii.Save.Engine_GoldenCockPlacements.Capture(levelRoot))
                GD.Print("[golden-cock] Saved level placement for the installed game.");

            string activeMod = EditorModExporter.ActiveModName;
            if (string.IsNullOrWhiteSpace(activeMod))
            {
                return;
            }

            if (EditorModValueService.SaveActiveLevelSceneToMod(
                    levelRoot,
                    activeMod,
                    out Refii.Level.LevelPatchCompileResult result,
                    out string error))
            {
                if (result.HasAnyChanges)
                {
                    GD.Print(
                        $"[level_edit] Auto-compiled saved scene '{filepath}' into '{activeMod}.f2mod': " +
                        $"{result.ModifiedTransformCount} moved, {result.NewPlacementCount} added, " +
                        $"{result.DeletedRecordCount} removed, {result.LayerStateOverrideCount} layer state(s).");
                }
            }
            else if (!string.IsNullOrEmpty(error))
            {
                GD.PushWarning($"[level_edit] Could not compile saved level scene '{filepath}' to mod: {error}");
            }

            _levelToolbar?.RefreshForScene(editedRoot);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[level_edit] Error during SceneSaved level compilation: {ex.Message}");
        }
    }

    private void OnActiveModChanged(string newModName)
    {
        SyncActiveModCreatorFavorite(newModName, triggerScan: true);
    }

    /// <summary>
    /// Ensures the active mod's creator folder (<c>res://creator/&lt;ActiveMod&gt;/</c>) exists on disk,
    /// pins it into Godot's FileSystemDock Favorites (removing stale creator folders from other mods),
    /// and optionally triggers a filesystem scan so new files appear immediately.
    /// </summary>
    /// <param name="modName">Optional mod name; defaults to <see cref="EditorModExporter.ActiveModName"/>.</param>
    /// <param name="triggerScan">Whether to have the filesystem pick the folder up now (<see cref="EditorFileSystem.ScanSources"/>).</param>
    public void SyncActiveModCreatorFavorite(string? modName = null, bool triggerScan = false)
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string targetMod = !string.IsNullOrWhiteSpace(modName) ? modName.Trim() : EditorModExporter.ActiveModName;
            if (!string.IsNullOrWhiteSpace(targetMod))
            {
                string creatorDir = EditorModExporter.GetCreatorDirectory(targetMod);
                if (!string.IsNullOrEmpty(creatorDir))
                {
                    Directory.CreateDirectory(creatorDir);
                }
            }

            EditorSettings? settings = EditorInterface.Singleton?.GetEditorSettings();
            if (settings is not null)
            {
                const string favKey = "filesystem/file_dialog/favorites";
                var favList = new List<string>();
                if (settings.HasSetting(favKey))
                {
                    string[] existing = settings.GetSetting(favKey).AsStringArray();
                    if (existing is not null)
                    {
                        foreach (string entry in existing)
                        {
                            if (!entry.StartsWith("res://creator/", StringComparison.OrdinalIgnoreCase))
                            {
                                favList.Add(entry);
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(targetMod))
                {
                    string targetRes = EditorModExporter.GetCreatorResPath(targetMod);
                    favList.Insert(0, targetRes);
                }

                settings.SetSetting(favKey, favList.ToArray());
            }

            if (triggerScan)
            {
                EditorInterface.Singleton?.GetResourceFilesystem()?.ScanSources();
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[bnk_mount] Could not sync creator folder favorite: {ex.Message}");
        }
    }

    /// <summary>
    /// Scans the Godot resource filesystem and navigates the FileSystemDock to the specified file or folder inside <c>res://creator/</c>.
    /// </summary>
    /// <param name="diskOrResPath">Absolute disk path or <c>res://</c> path.</param>
    public void RefreshAndNavigateCreatorFile(string diskOrResPath)
    {
        if (string.IsNullOrWhiteSpace(diskOrResPath) ||
            DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            string resPath = diskOrResPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                ? diskOrResPath
                : ProjectSettings.LocalizePath(diskOrResPath);

            if (!resPath.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (Directory.Exists(diskOrResPath) && !resPath.EndsWith("/"))
            {
                resPath += "/";
            }

            EditorInterface.Singleton?.GetResourceFilesystem()?.ScanSources();
            string targetRes = resPath;
            Callable.From(() =>
            {
                try
                {
                    EditorInterface.Singleton?.GetFileSystemDock()?.NavigateToPath(targetRes);
                }
                catch
                {
                }
            }).CallDeferred();
        }
        catch
        {
        }
    }

    private const string GdbPreviewStageResPath = "res://creator/.gdb_preview_stage.tscn";
    private const string GdbPreviewSubjectNodeName = "GdbPreviewSubject";
    private const string GdbPreviewNodeHashMeta = "gdb_node_hash";

    /// <summary>
    /// Opens or updates a 3D preview of the specified GDB record in Godot's 3D viewport,
    /// creating a lit preview stage scene if no 3D scene is currently open, selecting the spawned mesh,
    /// and switching the editor main screen to the 3D viewport.
    /// </summary>
    /// <param name="nodeHash">FNV-1 hash of the GDB record to inspect.</param>
    /// <param name="overrideModelPath">Optional explicit virtual <c>.mdl</c> path to preview.</param>
    /// <param name="switchTo3DView">If true, switches the main screen editor to the 3D viewport.</param>
    /// <returns>True if the 3D preview was spawned/updated; false if no model could be resolved.</returns>
    public bool InspectGdbRecordIn3D(uint nodeHash, string? overrideModelPath = null, bool switchTo3DView = true)
    {
        string? modelPath = !string.IsNullOrWhiteSpace(overrideModelPath)
            ? overrideModelPath.Trim()
            : EditorModValueService.ResolveNodeEffectiveModelPath(nodeHash);

        if (string.IsNullOrWhiteSpace(modelPath))
        {
            GD.PushWarning($"[mod_studio] Record 0x{nodeHash:X8} does not resolve to a 3D model (.mdl).");
            return false;
        }

        string normalized = modelPath.Replace('\\', '/').TrimStart('/');

        // Clear texture and material caches so any newly linked .f2mod assets render fresh
        Refii.Textures.Engine_TexLibrary.Clear();
        Refii.Models.Engine_MdlMaterial.ClearSharedCache();

        byte[]? mdlBytes = EditorModValueService.ReadVirtualAssetBytes(normalized, _filesystem);
        ArrayMesh? mesh = null;
        if (mdlBytes is not null && mdlBytes.Length > 0)
        {
            mesh = Refii.Models.Engine_MdlMesh.TryCreateMesh(mdlBytes, normalized, out _);
        }

        if (mesh is null || mesh.GetSurfaceCount() == 0)
        {
            try
            {
                mesh = ResourceLoader.Load<ArrayMesh>(
                    "game://" + normalized, "ArrayMesh", ResourceLoader.CacheMode.Ignore);
            }
            catch
            {
            }
        }

        if (mesh is null || mesh.GetSurfaceCount() == 0)
        {
            GD.PushWarning($"[mod_studio] Could not build 3D mesh for '{normalized}' (Record 0x{nodeHash:X8}).");
            return false;
        }

        Node? editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
        if (editedRoot is not Node3D)
        {
            EnsureAndOpen3DPreviewStageScene();
            editedRoot = EditorInterface.Singleton.GetEditedSceneRoot();
        }

        if (editedRoot is not Node3D root3D)
        {
            // Fallback: inspect the ArrayMesh directly in the Inspector if scene opening is deferred
            EditorInterface.Singleton.InspectObject(mesh);
            return true;
        }

        Node3D? subjectHolder = root3D.GetNodeOrNull<Node3D>(GdbPreviewSubjectNodeName);
        if (subjectHolder is null)
        {
            subjectHolder = new Node3D { Name = GdbPreviewSubjectNodeName };
            root3D.AddChild(subjectHolder);
            subjectHolder.Owner = root3D;
        }

        subjectHolder.SetMeta(GdbPreviewNodeHashMeta, (long)nodeHash);

        foreach (Node child in subjectHolder.GetChildren())
        {
            subjectHolder.RemoveChild(child);
            child.Free();
        }

        string label = $"Record_0x{nodeHash:X8}";
        if (Refii.Equipment.Engine_RecordNameCatalog.TryGetCuratedNodeName(nodeHash, out string curated) &&
            !string.IsNullOrWhiteSpace(curated))
        {
            label = $"{curated.Replace(' ', '_')}_0x{nodeHash:X8}";
        }

        var meshInstance = new MeshInstance3D
        {
            Name = label,
            Mesh = mesh
        };
        meshInstance.SetMeta(GdbPreviewNodeHashMeta, (long)nodeHash);

        subjectHolder.AddChild(meshInstance);
        meshInstance.Owner = root3D;

        AttachGmdSocketPreviewGizmos(meshInstance, root3D, normalized);

        EditorInterface.Singleton.GetSelection().Clear();
        EditorInterface.Singleton.GetSelection().AddNode(meshInstance);
        EditorInterface.Singleton.InspectObject(meshInstance);

        if (switchTo3DView)
        {
            EditorInterface.Singleton.SetMainScreenEditor("3D");
        }

        GD.Print($"[mod_studio] Inspecting 0x{nodeHash:X8} in 3D view -> {normalized} ({mesh.GetSurfaceCount()} surface(s))");
        return true;
    }

    private void AttachGmdSocketPreviewGizmos(Node3D meshInstance, Node3D ownerRoot, string normalizedModelPath)
    {
        try
        {
            string activeMod = EditorModExporter.GetActiveModName();
            byte[]? gmdBytes = EditorModValueService.ReadVirtualAssetBytes(
                normalizedModelPath + ".gmd",
                activeMod,
                includeActiveModPackage: true,
                FileSystem);
            if (gmdBytes is null || gmdBytes.Length == 0)
            {
                return;
            }

            var gmd = Refii.Models.Engine_GmdSkeleton.TryParse(gmdBytes);
            if (gmd is null || gmd.Attachments.Length == 0)
            {
                return;
            }

            var dummiesHolder = new Node3D { Name = "GmdDummies" };
            meshInstance.AddChild(dummiesHolder);
            dummiesHolder.Owner = ownerRoot;

            var unlitMat = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                NoDepthTest = true
            };

            foreach (var att in gmd.Attachments)
            {
                Transform3D godotXform = gmd.ComposeAttachment(att);

                var socketNode = new Node3D
                {
                    Name = att.Name.Replace('.', '_'),
                    Transform = godotXform
                };
                socketNode.SetMeta("gmd_dummy_name", att.Name);
                dummiesHolder.AddChild(socketNode);
                socketNode.Owner = ownerRoot;

                var gizmoMesh = new ImmediateMesh();
                gizmoMesh.SurfaceBegin(Mesh.PrimitiveType.Lines, unlitMat);

                const float axisLen = 0.08f;
                // X axis (Red)
                gizmoMesh.SurfaceSetColor(new Color(1.0f, 0.25f, 0.25f));
                gizmoMesh.SurfaceAddVertex(Vector3.Zero);
                gizmoMesh.SurfaceAddVertex(new Vector3(axisLen, 0f, 0f));

                // Y axis (Green)
                gizmoMesh.SurfaceSetColor(new Color(0.25f, 1.0f, 0.35f));
                gizmoMesh.SurfaceAddVertex(Vector3.Zero);
                gizmoMesh.SurfaceAddVertex(new Vector3(0f, axisLen, 0f));

                // Z axis (Blue)
                gizmoMesh.SurfaceSetColor(new Color(0.30f, 0.55f, 1.0f));
                gizmoMesh.SurfaceAddVertex(Vector3.Zero);
                gizmoMesh.SurfaceAddVertex(new Vector3(0f, 0f, axisLen));

                // Highlight forward direction for projectile muzzle or carry/weapon grip
                if (att.Name.Contains("Projectile", StringComparison.OrdinalIgnoreCase))
                {
                    var tip = new Vector3(0f, 0f, -0.22f);
                    gizmoMesh.SurfaceSetColor(new Color(1.0f, 0.65f, 0.1f));
                    gizmoMesh.SurfaceAddVertex(Vector3.Zero);
                    gizmoMesh.SurfaceAddVertex(tip);
                    gizmoMesh.SurfaceAddVertex(tip);
                    gizmoMesh.SurfaceAddVertex(tip + new Vector3(0.025f, 0f, 0.04f));
                    gizmoMesh.SurfaceAddVertex(tip);
                    gizmoMesh.SurfaceAddVertex(tip + new Vector3(-0.025f, 0f, 0.04f));
                }
                else if (att.Name.Equals("Weapon.Weapon.Hold", StringComparison.OrdinalIgnoreCase) ||
                         att.Name.Equals("Weapon.Weapon.UpperHold", StringComparison.OrdinalIgnoreCase) ||
                         att.Name.StartsWith("Prop.Carry.", StringComparison.OrdinalIgnoreCase) ||
                         att.Name.StartsWith("Character.Carry.", StringComparison.OrdinalIgnoreCase))
                {
                    var tip = new Vector3(-0.16f, 0f, 0f);
                    gizmoMesh.SurfaceSetColor(new Color(0.2f, 0.95f, 0.85f));
                    gizmoMesh.SurfaceAddVertex(Vector3.Zero);
                    gizmoMesh.SurfaceAddVertex(tip);
                }

                gizmoMesh.SurfaceEnd();

                var gizmoInstance = new MeshInstance3D
                {
                    Name = "SocketGizmo",
                    Mesh = gizmoMesh,
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
                };
                socketNode.AddChild(gizmoInstance);
                gizmoInstance.Owner = ownerRoot;

                Color labelColor = new(0.92f, 0.94f, 1.0f);
                if (att.Name.Equals("Weapon.Weapon.Hold", StringComparison.OrdinalIgnoreCase) ||
                    att.Name.StartsWith("Prop.Carry.", StringComparison.OrdinalIgnoreCase) ||
                    att.Name.StartsWith("Character.Carry.", StringComparison.OrdinalIgnoreCase))
                {
                    labelColor = new Color(0.35f, 0.95f, 0.85f);
                }
                else if (att.Name.Equals("Weapon.Weapon.UpperHold", StringComparison.OrdinalIgnoreCase))
                {
                    labelColor = new Color(0.45f, 0.98f, 0.55f);
                }
                else if (att.Name.Contains("Projectile", StringComparison.OrdinalIgnoreCase))
                {
                    labelColor = new Color(1.0f, 0.72f, 0.25f);
                }
                else if (att.Name.Equals("Weapon.Weapon.Top", StringComparison.OrdinalIgnoreCase))
                {
                    labelColor = new Color(0.95f, 0.50f, 1.0f);
                }
                else if (att.Name.Equals("Weapon.Weapon.Sheath", StringComparison.OrdinalIgnoreCase))
                {
                    labelColor = new Color(0.55f, 0.80f, 1.0f);
                }

                var label3D = new Label3D
                {
                    Name = "SocketLabel",
                    Text = att.Name,
                    PixelSize = 0.0014f,
                    FontSize = 26,
                    OutlineSize = 6,
                    Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                    NoDepthTest = true,
                    Modulate = labelColor,
                    Position = new Vector3(0f, 0.03f, 0f)
                };
                socketNode.AddChild(label3D);
                label3D.Owner = ownerRoot;
            }
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[mod_studio] Could not attach GMD socket gizmos for '{normalizedModelPath}': {ex.Message}");
        }
    }

    private void EnsureAndOpen3DPreviewStageScene()
    {
        try
        {
            string diskPath = ProjectSettings.GlobalizePath(GdbPreviewStageResPath);
            string? dir = Path.GetDirectoryName(diskPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(diskPath))
            {
                var stageRoot = new Node3D { Name = "GdbPreviewStage" };

                var worldEnv = new WorldEnvironment { Name = "PreviewEnvironment" };
                var env = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    BackgroundColor = new Color(0.14f, 0.16f, 0.20f),
                    AmbientLightSource = Godot.Environment.AmbientSource.Color,
                    AmbientLightColor = new Color(0.75f, 0.78f, 0.85f),
                    AmbientLightEnergy = 0.65f,
                    TonemapMode = Godot.Environment.ToneMapper.Linear
                };
                worldEnv.Environment = env;
                stageRoot.AddChild(worldEnv);
                worldEnv.Owner = stageRoot;

                var keyLight = new DirectionalLight3D
                {
                    Name = "KeyLight",
                    LightEnergy = 1.2f,
                    ShadowEnabled = true,
                    RotationDegrees = new Vector3(-45f, -35f, 0f)
                };
                stageRoot.AddChild(keyLight);
                keyLight.Owner = stageRoot;

                var fillLight = new DirectionalLight3D
                {
                    Name = "FillLight",
                    LightEnergy = 0.45f,
                    ShadowEnabled = false,
                    RotationDegrees = new Vector3(-25f, 145f, 0f)
                };
                stageRoot.AddChild(fillLight);
                fillLight.Owner = stageRoot;

                var subjectHolder = new Node3D { Name = GdbPreviewSubjectNodeName };
                stageRoot.AddChild(subjectHolder);
                subjectHolder.Owner = stageRoot;

                var packed = new PackedScene();
                if (packed.Pack(stageRoot) == Error.Ok)
                {
                    ResourceSaver.Save(packed, GdbPreviewStageResPath);
                }
                stageRoot.Free();
            }

            EditorInterface.Singleton.OpenSceneFromPath(GdbPreviewStageResPath);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[mod_studio] Could not open 3D preview stage scene: {ex.Message}");
        }
    }

    private void OnModContentChangedFor3DPreview(string modName)
    {
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Node? editedRoot = EditorInterface.Singleton?.GetEditedSceneRoot();
            if (editedRoot is not Node3D root3D)
            {
                return;
            }

            Node3D? subjectHolder = root3D.GetNodeOrNull<Node3D>(GdbPreviewSubjectNodeName);
            if (subjectHolder is null || !subjectHolder.HasMeta(GdbPreviewNodeHashMeta))
            {
                return;
            }

            uint nodeHash = (uint)subjectHolder.GetMeta(GdbPreviewNodeHashMeta).AsInt64();
            if (nodeHash != 0)
            {
                InspectGdbRecordIn3D(nodeHash, overrideModelPath: null, switchTo3DView: false);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Initializes and attaches the modal dialog used to switch or create active mod workspaces.
    /// </summary>
    private void SetupModNameDialog()
    {
        if (_modNameDialog is not null && IsInstanceValid(_modNameDialog))
        {
            RefreshModNameDialog();
            return;
        }

        _modNameDialog = new ConfirmationDialog();
        _modNameDialog.Title = "Select Active Mod";
        _modNameDialog.MinSize = new Vector2I(380, 180);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 8);

        var chooseLabel = new Label { Text = "Switch to Existing Mod:" };
        vbox.AddChild(chooseLabel);

        _modOptionBtn = new OptionButton();
        _modOptionBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(_modOptionBtn);

        vbox.AddChild(new HSeparator());

        var createLabel = new Label { Text = "Or Create New Mod (.f2mod):" };
        vbox.AddChild(createLabel);

        var createRow = new HBoxContainer();
        createRow.AddThemeConstantOverride("separation", 6);
        _modNameEdit = new LineEdit { PlaceholderText = "NewModName" };
        _modNameEdit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        createRow.AddChild(_modNameEdit);

        var createBtn = new Button { Text = "Create" };
        createBtn.Pressed += () =>
        {
            if (_modNameEdit != null && !string.IsNullOrWhiteSpace(_modNameEdit.Text))
            {
                string newName = _modNameEdit.Text.Trim();
                if (ModWorkspace.IsSafeModFolderName(newName))
                {
                    string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
                    if (string.IsNullOrEmpty(modsRoot))
                    {
                        modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
                    }
                    if (ModWorkspace.CreateModPackage(modsRoot, newName, out _, out string err))
                    {
                        var order = Refii.Mods.ModLoadOrder.Read(modsRoot);
                        order.RegisterMod(newName, enabled: true);
                        order.Write(modsRoot, out _);
                        EditorModExporter.ActiveModName = newName;
                        EditorModExporter.EnsureActiveModWorkspace();
                        _modNameEdit.Text = string.Empty;
                        RefreshModNameDialog();
                        ReloadModOverlays();
                        GD.Print($"[mod_export] Created and activated '{newName}.f2mod'");
                    }
                    else
                    {
                        GD.PushWarning($"[mod_export] Failed to create mod package: {err}");
                    }
                }
                else
                {
                    GD.PushWarning($"[mod_export] '{newName}' is not a valid mod name.");
                }
            }
        };
        createRow.AddChild(createBtn);
        vbox.AddChild(createRow);

        _modNameDialog.AddChild(vbox);

        _modNameDialog.Confirmed += () =>
        {
            if (_modNameEdit != null && !string.IsNullOrWhiteSpace(_modNameEdit.Text))
            {
                string newName = _modNameEdit.Text.Trim();
                if (ModWorkspace.IsSafeModFolderName(newName))
                {
                    string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
                    if (string.IsNullOrEmpty(modsRoot))
                    {
                        modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
                    }
                    ModWorkspace.CreateModPackage(modsRoot, newName, out _, out _);
                    var order = Refii.Mods.ModLoadOrder.Read(modsRoot);
                    order.RegisterMod(newName, enabled: true);
                    order.Write(modsRoot, out _);
                    EditorModExporter.ActiveModName = newName;
                    EditorModExporter.EnsureActiveModWorkspace();
                    ReloadModOverlays();
                    GD.Print($"[mod_export] Active mod set to '{newName}'");
                    return;
                }
            }

            if (_modOptionBtn != null && _modOptionBtn.ItemCount > 0 && _modOptionBtn.Selected >= 0)
            {
                string chosen = _modOptionBtn.GetItemText(_modOptionBtn.Selected);
                if (!string.IsNullOrWhiteSpace(chosen))
                {
                    string modsRoot = Refii.Assets.Engine_GameAssets.ModsRoot;
                    if (string.IsNullOrEmpty(modsRoot))
                    {
                        modsRoot = ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);
                    }
                    var order = Refii.Mods.ModLoadOrder.Read(modsRoot);
                    if (!order.Enabled.Contains(chosen))
                    {
                        order.Enabled.Insert(0, chosen);
                        order.Write(modsRoot, out _);
                    }
                    EditorModExporter.ActiveModName = chosen;
                    EditorModExporter.EnsureActiveModWorkspace();
                    ReloadModOverlays();
                    GD.Print($"[mod_export] Active mod set to '{chosen}'");
                }
            }
        };

        EditorInterface.Singleton.GetBaseControl().AddChild(_modNameDialog);
        RefreshModNameDialog();
    }

    private void RefreshModNameDialog()
    {
        if (_modOptionBtn == null) return;
        _modOptionBtn.Clear();
        List<string> mods = EditorModExporter.DiscoverModNames();
        string current = EditorModExporter.ActiveModName;
        int selectedIndex = 0;
        for (int i = 0; i < mods.Count; i++)
        {
            _modOptionBtn.AddItem(mods[i]);
            if (string.Equals(mods[i], current, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = i;
            }
        }
        if (mods.Count > 0)
        {
            _modOptionBtn.Select(selectedIndex);
        }
    }

    /// <summary>
    /// Callback triggered by the GDScript signal bridge when a user right-clicks an item in the Game:// tree.
    /// </summary>
    /// <param name="item">The TreeItem that received the right-click.</param>
    /// <param name="screenPos">The global screen coordinates of the cursor.</param>
    private void OnItemRmb(TreeItem item, Vector2 screenPos)
    {
        if (item is null || !GodotObject.IsInstanceValid(item))
        {
            return;
        }

        SetupContextMenu();
        if (_contextMenu is null)
        {
            return;
        }

        _contextItem = item;
        _contextItemDetail = null;
        _contextVirtualPath = string.Empty;
        _contextDiskPath = string.Empty;
        _contextModelPaths.Clear();
        _contextTexturePaths.Clear();
        _contextIconPaths.Clear();

        string meta = GetItemMeta(item);
        string text = item.GetText(0);

        if (meta.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            _contextVirtualPath = meta[FilePrefix.Length..];
        }
        else if (meta.StartsWith("bnk:", StringComparison.Ordinal))
        {
            _contextVirtualPath = meta[4..];
        }
        else if (meta.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
        {
            _contextDiskPath = ProjectSettings.GlobalizePath(meta);
        }
        else if (File.Exists(meta))
        {
            _contextDiskPath = meta;
        }
        else if (text.EndsWith(".tu", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
        {
            string[] selected = EditorInterface.Singleton.GetSelectedPaths();
            if (selected != null && selected.Length > 0)
            {
                _contextDiskPath = ProjectSettings.GlobalizePath(selected[0]);
            }
        }

        if (_contextVirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            _contextModelPaths.Add(_contextVirtualPath);
        }
        else if (_contextVirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            _contextTexturePaths.Add(_contextVirtualPath);
        }
        else if (_contextVirtualPath.EndsWith(".weapon", StringComparison.OrdinalIgnoreCase))
        {
            if (Refii.Equipment.Engine_WeaponIndex.TryGet(_contextVirtualPath, out var wEntry))
            {
                _contextItemDetail = wEntry.Detail;
                if (!string.IsNullOrEmpty(wEntry.ModelPath))
                {
                    _contextModelPaths.Add(wEntry.ModelPath);
                }
                if (!string.IsNullOrEmpty(wEntry.Detail.IconTex))
                {
                    _contextIconPaths.Add(wEntry.Detail.IconTex);
                }
            }
        }
        else if (_contextVirtualPath.EndsWith(".clothing", StringComparison.OrdinalIgnoreCase))
        {
            if (Refii.Equipment.Engine_ClothingIndex.TryGet(_contextVirtualPath, out var cEntry))
            {
                _contextItemDetail = cEntry.Detail;
                if (!string.IsNullOrEmpty(cEntry.MaleModelPath))
                {
                    _contextModelPaths.Add(cEntry.MaleModelPath);
                }
                if (!string.IsNullOrEmpty(cEntry.FemaleModelPath) && cEntry.FemaleModelPath != cEntry.MaleModelPath)
                {
                    _contextModelPaths.Add(cEntry.FemaleModelPath);
                }
                if (!string.IsNullOrEmpty(cEntry.Detail.IconTex))
                {
                    _contextIconPaths.Add(cEntry.Detail.IconTex);
                }
            }
        }
        else if (_contextVirtualPath.EndsWith(".item", StringComparison.OrdinalIgnoreCase))
        {
            if (Refii.Equipment.Engine_ItemIndex.TryGet(_contextVirtualPath, out var iEntry))
            {
                _contextItemDetail = iEntry.Detail;
                if (!string.IsNullOrEmpty(iEntry.ModelPath))
                {
                    _contextModelPaths.Add(iEntry.ModelPath);
                }
                if (!string.IsNullOrEmpty(iEntry.Detail.IconTex))
                {
                    _contextIconPaths.Add(iEntry.Detail.IconTex);
                }
            }
        }
        else if (_contextVirtualPath.EndsWith(".entity", StringComparison.OrdinalIgnoreCase))
        {
            if (Refii.Entities.Engine_EntityIndex.TryGet(_contextVirtualPath, out var eEntry))
            {
                foreach (uint modelHash in eEntry.ModelHashes)
                {
                    string? path = Refii.Level.Engine_PropModelIndex.Resolve(modelHash, Array.Empty<string>());
                    if (!string.IsNullOrEmpty(path) && !_contextModelPaths.Contains(path))
                    {
                        _contextModelPaths.Add(path);
                    }
                }
            }
        }

        // For all resolved 3D models, extract the actual 3D textures (Diffuse, Normal, Specular, etc.)
        foreach (string modelPath in _contextModelPaths)
        {
            foreach (string texPath in EditorModExporter.GetModelTexturePaths(modelPath, _filesystem))
            {
                if (!_contextTexturePaths.Contains(texPath))
                {
                    _contextTexturePaths.Add(texPath);
                }
            }
        }

        _contextMenu.Clear();
        string activeMod = EditorModExporter.ActiveModName;

        if (_contextModelPaths.Count == 1)
        {
            string fileName = Path.GetFileName(_contextModelPaths[0]);
            _contextMenu.AddItem($"Export Model ({fileName}.glb) to [{activeMod}]", MenuExportGlb);
            _contextMenu.AddItem($"Import .glb over {fileName} into [{activeMod}]...", MenuImportGlb);
        }
        else if (_contextModelPaths.Count > 1)
        {
            _contextMenu.AddItem($"Export {_contextModelPaths.Count} Models (.glb) to [{activeMod}]", MenuExportGlb);
        }

        if (_contextTexturePaths.Count == 1)
        {
            string fileName = Path.GetFileName(_contextTexturePaths[0]);
            _contextMenu.AddItem($"Export Model Texture ({fileName}) to [{activeMod}]", MenuExportPng);
            _contextMenu.AddItem($"Export PBR Texture Set ({fileName}) to [{activeMod}]", MenuExportPbr);
        }
        else if (_contextTexturePaths.Count > 1)
        {
            _contextMenu.AddItem($"Export {_contextTexturePaths.Count} Model Textures (.png) to [{activeMod}]", MenuExportPng);
            _contextMenu.AddItem($"Export {_contextTexturePaths.Count} PBR Texture Sets (with _orm.png) to [{activeMod}]", MenuExportPbr);
        }

        if (_contextIconPaths.Count == 1)
        {
            string fileName = Path.GetFileName(_contextIconPaths[0]);
            _contextMenu.AddItem($"Export Inventory Icon ({fileName}) to [{activeMod}]", MenuExportIcon);
        }
        else if (_contextIconPaths.Count > 1)
        {
            _contextMenu.AddItem($"Export {_contextIconPaths.Count} Inventory Icons (.png) to [{activeMod}]", MenuExportIcon);
        }

        if (!string.IsNullOrEmpty(_contextVirtualPath) || _contextModelPaths.Count > 0 || _contextTexturePaths.Count > 0 || _contextIconPaths.Count > 0)
        {
            _contextMenu.AddItem("Open in Mod Folder", MenuOpenModFolder);
            _contextMenu.AddSeparator();
        }

        if (_contextItemDetail is not null)
        {
            _contextMenu.AddItem($"✏️ Edit Properties in Mod [{activeMod}]...", MenuEditItemValues);
            _contextMenu.AddSeparator();
        }

        if (_contextVirtualPath.EndsWith(".tu", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(_contextDiskPath) && (_contextDiskPath.EndsWith(".tu", StringComparison.OrdinalIgnoreCase) || _contextDiskPath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))))
        {
            _contextMenu.AddItem("Inspect GDB Title Update (.tu)", MenuInspectTu);
            _contextMenu.AddSeparator();
        }

        _contextMenu.AddItem("🛠️ Open in Mod Studio Workspace", MenuOpenModStudioWorkspace);
        _contextMenu.AddItem($"Active Mod: {activeMod} (Change...)", MenuChangeActiveMod);
        _contextMenu.AddItem("Inspect Active Mod Title Updates (.tu)", MenuInspectModTu);
        _contextMenu.AddItem("Reload Mod Overlays", MenuReloadOverlays);

        Vector2I mousePos = _contextMenu.IsEmbedded()
            ? (Vector2I)(_tree?.GetGlobalMousePosition() ?? screenPos)
            : DisplayServer.MouseGetPosition();
        _contextMenu.Position = mousePos;
        _contextMenu.ResetSize();
        _contextMenu.Popup();
    }

    /// <summary>
    /// Dispatches actions selected from the Game:// right-click context menu.
    /// </summary>
    /// <param name="id">Identifier of the pressed menu option.</param>
    private void OnContextMenuIdPressed(long id)
    {
        switch (id)
        {
            case MenuOpenModStudioWorkspace:
            {
                EditorInterface.Singleton.SetMainScreenEditor("Mod Studio");
                break;
            }
            case MenuEditItemValues:
            {
                if (_contextItemDetail is not null)
                {
                    OpenModItemEditor(_contextItemDetail);
                }
                break;
            }
            case MenuExportGlb:
            {
                string lastPath = string.Empty;
                int successCount = 0;
                foreach (string modelPath in _contextModelPaths)
                {
                    if (EditorModExporter.ExportModel(modelPath, out string exportedPath, out string error))
                    {
                        lastPath = exportedPath;
                        successCount++;
                        GD.Print($"[mod_export] Exported model to: {exportedPath}");
                    }
                    else
                    {
                        GD.PushWarning($"[mod_export] Failed to export model '{modelPath}': {error}");
                    }
                }

                if (successCount > 0 && !string.IsNullOrEmpty(lastPath))
                {
                    EditorModExporter.RevealInExplorer(lastPath);
                }
                break;
            }
            case MenuExportPng:
            {
                string lastPath = string.Empty;
                int successCount = 0;
                foreach (string texPath in _contextTexturePaths)
                {
                    if (EditorModExporter.ExportTexture(texPath, _filesystem, false, out string exportedPath, out string error))
                    {
                        lastPath = exportedPath;
                        successCount++;
                        GD.Print($"[mod_export] Exported model texture to: {exportedPath}");
                    }
                    else
                    {
                        GD.PushWarning($"[mod_export] Failed to export texture '{texPath}': {error}");
                    }
                }

                if (successCount > 0 && !string.IsNullOrEmpty(lastPath))
                {
                    EditorModExporter.RevealInExplorer(lastPath);
                }
                break;
            }
            case MenuExportPbr:
            {
                string lastPath = string.Empty;
                int successCount = 0;
                foreach (string texPath in _contextTexturePaths)
                {
                    if (EditorModExporter.ExportTexture(texPath, _filesystem, true, out string exportedPath, out string error))
                    {
                        lastPath = exportedPath;
                        successCount++;
                        GD.Print($"[mod_export] Exported PBR texture set to: {exportedPath}");
                    }
                    else
                    {
                        GD.PushWarning($"[mod_export] Failed to export PBR texture '{texPath}': {error}");
                    }
                }

                if (successCount > 0 && !string.IsNullOrEmpty(lastPath))
                {
                    EditorModExporter.RevealInExplorer(lastPath);
                }
                break;
            }
            case MenuExportIcon:
            {
                string lastPath = string.Empty;
                int successCount = 0;
                foreach (string iconPath in _contextIconPaths)
                {
                    if (EditorModExporter.ExportTexture(iconPath, _filesystem, false, out string exportedPath, out string error))
                    {
                        lastPath = exportedPath;
                        successCount++;
                        GD.Print($"[mod_export] Exported inventory icon to: {exportedPath}");
                    }
                    else
                    {
                        GD.PushWarning($"[mod_export] Failed to export icon '{iconPath}': {error}");
                    }
                }

                if (successCount > 0 && !string.IsNullOrEmpty(lastPath))
                {
                    EditorModExporter.RevealInExplorer(lastPath);
                }
                break;
            }
            case MenuOpenModFolder:
            {
                string target = !string.IsNullOrEmpty(_contextVirtualPath)
                    ? _contextVirtualPath
                    : _contextModelPaths.Count > 0
                        ? _contextModelPaths[0]
                        : _contextTexturePaths.Count > 0
                            ? _contextTexturePaths[0]
                            : _contextIconPaths.Count > 0
                                ? _contextIconPaths[0]
                                : string.Empty;

                if (string.IsNullOrEmpty(target))
                {
                    EditorModExporter.RevealInExplorer(EditorModExporter.EnsureActiveModWorkspace());
                    return;
                }

                string dir = EditorModExporter.EnsureModAssetDirectory(target);
                EditorModExporter.RevealInExplorer(dir);
                break;
            }
            case MenuChangeActiveMod:
            {
                SetupModNameDialog();
                RefreshModNameDialog();
                if (_modNameEdit is not null)
                {
                    _modNameEdit.Text = string.Empty;
                }
                _modNameDialog?.PopupCentered(new Vector2I(380, 180));
                break;
            }
            case MenuReloadOverlays:
            {
                ReloadModOverlays();
                break;
            }
            case MenuImportGlb:
            {
                if (_contextModelPaths.Count == 1)
                {
                    OpenGlbImportDialog(_contextModelPaths[0]);
                }
                break;
            }
            case MenuInspectModTu:
            {
                InspectModTuDialog(EditorModExporter.ActiveModName);
                break;
            }
            case MenuInspectTu:
            {
                if (!string.IsNullOrEmpty(_contextDiskPath) && File.Exists(_contextDiskPath))
                {
                    string content = Refii.Gdb.GdbPrettyPrinter.Print(_contextDiskPath, Refii.Gdb.Engine_GameGdb.BaseDatabase);
                    ShowTextPopup($"GDB Title Update Inspection — {Path.GetFileName(_contextDiskPath)}", content);
                }
                else if (!string.IsNullOrEmpty(_contextVirtualPath))
                {
                    InspectTuPathDialog(_contextVirtualPath);
                }
                break;
            }
        }
    }

    private void InspectModTuDialog(string modName)
    {
        string filesRoot = Path.Combine(Refii.Assets.Engine_GameAssets.ModsRoot, modName, "files");
        var sb = new System.Text.StringBuilder();
        if (Directory.Exists(filesRoot))
        {
            var tuFiles = Directory.GetFiles(filesRoot, "*.gdb.tu", SearchOption.AllDirectories);
            if (tuFiles.Length == 0)
            {
                sb.AppendLine($"No .gdb.tu files found in mod '{modName}' ({filesRoot}).");
            }
            else
            {
                foreach (string file in tuFiles)
                {
                    sb.AppendLine(Refii.Gdb.GdbPrettyPrinter.Print(file, Refii.Gdb.Engine_GameGdb.BaseDatabase));
                    sb.AppendLine();
                }
            }
        }
        else
        {
            sb.AppendLine($"Mod folder not found: {filesRoot}");
        }

        ShowTextPopup($"GDB Title Update Inspection — {modName}", sb.ToString());
    }

    private void InspectTuPathDialog(string virtualPath)
    {
        string content = string.Empty;
        if (_filesystem != null && _filesystem.TryGetFile(virtualPath, out var file))
        {
            if (File.Exists(file.SourceArchivePath))
            {
                content = Refii.Gdb.GdbPrettyPrinter.Print(file.SourceArchivePath, Refii.Gdb.Engine_GameGdb.BaseDatabase);
            }
            else
            {
                try
                {
                    byte[] bytes = file.ReadAllBytes();
                    string tempPath = Path.Combine(Path.GetTempPath(), "refii_inspect_" + Path.GetFileName(virtualPath));
                    File.WriteAllBytes(tempPath, bytes);
                    content = Refii.Gdb.GdbPrettyPrinter.Print(tempPath, Refii.Gdb.Engine_GameGdb.BaseDatabase);
                    try { File.Delete(tempPath); } catch { }
                }
                catch (Exception ex)
                {
                    content = $"Failed to read virtual file '{virtualPath}': {ex.Message}";
                }
            }
        }
        else
        {
            content = $"File not found in filesystem: '{virtualPath}'";
        }

        ShowTextPopup($"GDB Title Update Inspection — {virtualPath}", content);
    }

    private void ShowTextPopup(string title, string text)
    {
        var dialog = new AcceptDialog();
        dialog.Title = title;
        dialog.MinSize = new Vector2I(680, 480);
        var textEdit = new TextEdit
        {
            Text = text,
            Editable = false,
            CustomMinimumSize = new Vector2(650, 420),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        dialog.AddChild(textEdit);
        EditorInterface.Singleton.GetBaseControl().AddChild(dialog);
        dialog.PopupCentered();
    }

    /// <summary>
    /// Flushes runtime texture, material, and equipment indices, and triggers an asynchronous remount of all mod overlays.
    /// </summary>
    public void ReloadModOverlays()
    {
        GD.Print("[bnk_mount] Reloading mod overlays...");
        Refii.Gdb.Engine_GameGdb.Invalidate();
        Refii.Gdb.Engine_BnkTextBankLoader.Invalidate();
        Refii.Equipment.Engine_WeaponIndex.Invalidate();
        Refii.Equipment.Engine_ItemIndex.Invalidate();
        Refii.Equipment.Engine_ClothingIndex.Invalidate();
        Refii.Textures.Engine_TexLibrary.Clear();
        Refii.Models.Engine_MdlMaterial.ClearSharedCache();
        Refii.Level.Engine_PropStreamer.ClearCache();
        StartMount();
        _modStudioMainPanel?.RefreshModList();
    }

    private bool IsOurs(TreeItem item)
    {
        if (item is null || !GodotObject.IsInstanceValid(item))
        {
            return false;
        }

        string meta = GetItemMeta(item);
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
        string meta = GetItemMeta(item);
        string text = item.GetText(0);

        if (text.EndsWith(".tu", StringComparison.OrdinalIgnoreCase) || meta.EndsWith(".tu", StringComparison.OrdinalIgnoreCase))
        {
            string diskPath = meta.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                ? ProjectSettings.GlobalizePath(meta)
                : (File.Exists(meta) ? meta : string.Empty);

            if (string.IsNullOrEmpty(diskPath) && !meta.StartsWith(FilePrefix))
            {
                string[] selected = EditorInterface.Singleton.GetSelectedPaths();
                if (selected != null && selected.Length > 0 && selected[0].EndsWith(".tu", StringComparison.OrdinalIgnoreCase))
                {
                    diskPath = ProjectSettings.GlobalizePath(selected[0]);
                }
            }

            if (!string.IsNullOrEmpty(diskPath) && File.Exists(diskPath))
            {
                string content = Refii.Gdb.GdbPrettyPrinter.Print(diskPath, Refii.Gdb.Engine_GameGdb.BaseDatabase);
                ShowTextPopup($"GDB Title Update Inspection — {Path.GetFileName(diskPath)}", content);
                return;
            }

            if (meta.StartsWith(FilePrefix))
            {
                InspectTuPathDialog(meta[FilePrefix.Length..]);
                return;
            }
        }

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

        if (virtualPath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
        {
            ActivateLua(virtualPath);
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

    public void RebuildLevelWithScenario(string levelName, string scenarioName)
    {
        if (string.IsNullOrWhiteSpace(levelName) || string.IsNullOrWhiteSpace(scenarioName))
        {
            return;
        }

        Refii.Level.LevelEntry? level = Levels().Find(l =>
            l.Name.Equals(levelName, StringComparison.OrdinalIgnoreCase) ||
            l.SceneNodeName.Equals(levelName, StringComparison.OrdinalIgnoreCase));
        if (level is null)
        {
            return;
        }

        level.Scenario = scenarioName.Trim();
        ActivateLevel(level.HeightfieldPath);
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
        filesystem.UpdateFile(scenePath);

        GD.Print($"[bnk_mount] {level} -> {scenePath}");

        if (Array.IndexOf(EditorInterface.Singleton.GetOpenScenes(), level.ScenePath) >= 0)
        {
            EditorInterface.Singleton.ReloadSceneFromPath(level.ScenePath);
            Callable.From(() => _levelToolbar?.RefreshForScene(EditorInterface.Singleton.GetEditedSceneRoot())).CallDeferred();
            return;
        }

        EditorInterface.Singleton.OpenSceneFromPath(level.ScenePath);
        Callable.From(() => _levelToolbar?.RefreshForScene(EditorInterface.Singleton.GetEditedSceneRoot())).CallDeferred();
    }

    private void OnItemCollapsed(TreeItem item)
    {
        if (item.Collapsed || item.GetChildCount() != 1 || _filesystem is null)
        {
            return;
        }

        if (GetItemMeta(item.GetChild(0)) != PlaceholderMeta)
        {
            return;
        }

        string meta = GetItemMeta(item);
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
            if (GetItemMeta(only) != PlaceholderMeta)
            {
                continue;
            }

            item.RemoveChild(only);
            only.Free();

            string meta = GetItemMeta(item);
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
            SetItemMeta(item, FilePrefix + Refii.Entities.Engine_EntityIndex.VirtualPathOf(entry));
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
            SetItemMeta(item, FilePrefix + Refii.Hero.Engine_HeroCatalog.VirtualPathOf(entry));
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
        SetItemMeta(weapons, WeaponsRootMeta);
        weapons.SetSelectable(0, false);
        weapons.SetIcon(0, GetFolderIcon());
        weapons.SetIconModulate(0, GameFolderColor);
        AddPlaceholder(tree, weapons);
        weapons.Collapsed = true;

        TreeItem clothes = tree.CreateItem(parent);
        clothes.SetText(0, "Clothes");
        SetItemMeta(clothes, ClothesRootMeta);
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
            SetItemMeta(item, WeaponFamilyPrefix + family);
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
            item.SetTooltipText(0, Refii.Equipment.Engine_ItemNames.FormatTooltip(entry.Detail, entry.Family, entry.ModelPath));
            SetItemMeta(item, FilePrefix + Refii.Equipment.Engine_WeaponIndex.VirtualPathOf(entry));
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
            SetItemMeta(item, ClothingSlotPrefix + folder);
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
            string clothModel = entry.MaleModelPath.Length != 0 ? entry.MaleModelPath : entry.FemaleModelPath;
            item.SetTooltipText(0, Refii.Equipment.Engine_ItemNames.FormatTooltip(entry.Detail, Refii.Equipment.Engine_ClothingSlots.LabelOf(entry.Slot), clothModel));
            SetItemMeta(item, FilePrefix + Refii.Equipment.Engine_ClothingIndex.VirtualPathOf(entry));
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
            SetItemMeta(item, ItemCategoryPrefix + folder);
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
            string mesh = entry.HasModel ? entry.ModelPath : "No 3D mesh";
            item.SetTooltipText(
                0,
                Refii.Equipment.Engine_ItemNames.FormatTooltip(
                    entry.Detail, Refii.Equipment.Engine_ItemCategories.LabelOf(entry.Category), mesh));
            SetItemMeta(item, FilePrefix + Refii.Equipment.Engine_ItemIndex.VirtualPathOf(entry));
            ApplyEquipmentIcon(item, entry.Detail.IconTex, fallback);

            if (!entry.HasModel)
            {
                item.SetCustomColor(0, MeshlessItemColor);
            }
        }
    }

    /// <summary>The harvested effect names, kept: a re-overlaid bank (a mod effect saved) is a new
    /// bank instance and takes them again without a second harvest.</summary>
    private Dictionary<uint, string>? _fxNames;

    private Refii.Fx.FxBank? _fxNamedBank;

    private void EnsureFxNames(Refii.Fx.FxBank bank)
    {
        if (ReferenceEquals(_fxNamedBank, bank) || _filesystem is null)
        {
            return;
        }

        _fxNamedBank = bank;
        if (_fxNames is null)
        {
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

            _fxNames = Refii.Fx.Engine_FxNames.Harvest(wanted, files, ResolveBytes);
        }

        // A mod's effect already carries the name it was authored under.
        var unnamed = new Dictionary<uint, string>();
        foreach ((uint hash, string name) in _fxNames)
        {
            if (bank.FindByHash(hash) is { Name.Length: 0 })
            {
                unnamed[hash] = name;
            }
        }

        int applied = Refii.Fx.Engine_FxNames.Apply(bank, unnamed);
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
            SetItemMeta(item, FilePrefix + Refii.Fx.Engine_BnkFxLoader.VirtualPathOf(display));
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
            SetItemMeta(item, LevelRegionPrefix + group.Key);
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
            SetItemMeta(empty, PlaceholderMeta);
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
            SetItemMeta(item, LevelPrefix + level.HeightfieldPath);

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

    /// <summary>
    /// A script-bank entry opens in the Lua editor: decompiled on demand, edited, checked with
    /// the game's compiler and saved into a mod at the same path. The mount then serves the
    /// mod's text to the guest like any other overlaid file, so saving remounts.
    /// </summary>
    private void ActivateLua(string virtualPath)
    {
        if (_filesystem is null || !_filesystem.TryGetFile(virtualPath, out BnkFile file))
        {
            return;
        }

        var editor = new LuaScriptEditor(file, virtualPath, StartMount);
        EditorInterface.Singleton.GetBaseControl().AddChild(editor);
        editor.PopupCentered();
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
            SetItemMeta(item, FilePrefix + "art/videos/" + name + ".bik");

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
            if (file.VirtualPath.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase) ||
                file.VirtualPath.EndsWith(".tu", StringComparison.OrdinalIgnoreCase))
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
            SetItemMeta(item, FilePrefix + file.VirtualPath);

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
            SetItemMeta(empty, PlaceholderMeta);
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
            SetItemMeta(item, "bnk:" + child);
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
            SetItemMeta(item, FilePrefix + child);
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

    public static ImageTexture? GetEquipmentIcon(string iconTex)
    {
        if (string.IsNullOrWhiteSpace(iconTex)) return null;

        if (Instance is not null)
        {
            var tex = Instance.EquipmentIcon(iconTex);
            if (tex is not null) return tex;
        }

        var assets = Engine_GameAssets.Instance;
        if (assets is not null)
        {
            string normalized = iconTex.Replace('\\', '/');
            if (assets.TryGetFile(iconTex, out BnkFile file) ||
                assets.TryGetFile(normalized, out file) ||
                (!normalized.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && assets.TryGetFile(normalized + ".tex", out file)))
            {
                return BuildThumbnail(file);
            }
        }

        return null;
    }

    public ImageTexture? EquipmentIcon(string iconTex)
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
        string normalized = iconTex.Replace('\\', '/');
        if (_filesystem.TryGetFile(iconTex, out BnkFile file) ||
            _filesystem.TryGetFile(normalized, out file) ||
            (!normalized.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) && _filesystem.TryGetFile(normalized + ".tex", out file)))
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
        SetItemMeta(placeholder, PlaceholderMeta);
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

    /// <summary>The game data directory the editor's mount streams from (the UI tab's preview reads its fonts and text there).</summary>
    public static string GameDirectory => ResolveGameDirectory();

    private static string ResolveGameDirectory()
    {
        string resolved = Refii.Core.Engine_GameData.Resolve();
        if (resolved.Length != 0)
        {
            return resolved;
        }

        string projectDir = ProjectSettings.GlobalizePath("res://");
        string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));
        return Path.Combine(repoRoot, "assets", "game");
    }
}
#endif
