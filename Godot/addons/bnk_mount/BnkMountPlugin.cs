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
    private const string ContainerMeta = "__bnk_container__";
    private const string FilePrefix = "game://";

    private BnkFileSystem? _filesystem;
    private Refii.Textures.Engine_BnkTextureLoader? _textureLoader;
    private Refii.Models.Engine_BnkMeshLoader? _meshLoader;
    private Tree? _tree;
    private TreeItem? _gameRoot;
    private bool _mounting;
    private bool _collapsedHooked;
    private readonly List<TreeItem> _pending = new();

    private static readonly Color GameFolderColor = new(0.36f, 0.85f, 0.47f);

    public override void _EnterTree()
    {
        StartMount();
        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged += OnFilesystemChanged;
    }

    public override void _ExitTree()
    {
        EditorInterface.Singleton.GetResourceFilesystem().FilesystemChanged -= OnFilesystemChanged;
        RemoveGameRoot();
        RemoveLoaders();
        _filesystem?.Dispose();
        _filesystem = null;
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

    private void EnsureLoaders()
    {
        Refii.Textures.Engine_BnkTextureLoader.SetResolver(ResolveBytes);
        Refii.Models.Engine_BnkMeshLoader.SetResolver(ResolveBytes);
        Refii.Models.Engine_BnkMeshLoader.SetExistsProbe(
            p => _filesystem is not null && _filesystem.TryGetFile(p, out _));

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
    }

    private void RemoveLoaders()
    {
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

        Refii.Textures.Engine_BnkTextureLoader.SetResolver(null);
        Refii.Models.Engine_BnkMeshLoader.SetResolver(null);
        Refii.Models.Engine_BnkMeshLoader.SetExistsProbe(null);
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
               meta == ContainerMeta ||
               meta.StartsWith("bnk:", StringComparison.Ordinal) ||
               meta.StartsWith(FilePrefix, StringComparison.Ordinal) ||
               meta == PlaceholderMeta;
    }

    private void ActivateBnkItem(TreeItem item)
    {
        string meta = item.GetMetadata(0).AsString();
        if (!meta.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            item.Collapsed = !item.Collapsed;
            return;
        }

        string virtualPath = meta[FilePrefix.Length..];
        if (_filesystem is null || !_filesystem.TryGetFile(virtualPath, out BnkFile file))
        {
            return;
        }

        bool isTexture = virtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase);
        bool isModel = virtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase);
        if (!isTexture && !isModel)
        {
            GD.Print($"[bnk_mount] {virtualPath}  ({file.Size:N0} bytes, no viewer for this type)");
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
        if (meta != RootMeta && !meta.StartsWith("bnk:", StringComparison.Ordinal))
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
            Populate(item, meta == RootMeta ? string.Empty : meta[4..]);
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
                ApplyThumbnail(item, file);
            }
        }
    }

    private const int ThumbnailSize = 32;
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;

    private static void ApplyThumbnail(TreeItem item, BnkFile file)
    {
        if (!file.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
            file.Size == 0 || file.Size > MaxThumbnailBytes)
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
