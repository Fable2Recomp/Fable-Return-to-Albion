using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Refii.Bnk;

namespace Refii.Assets;

public partial class Engine_GameAssets : Node
{
    public const string PathPrefix = "game://";

    private static readonly object MountLock = new();

    private BnkFileSystem? _filesystem;
    private bool _mounting;

    public static Engine_GameAssets? Instance { get; private set; }

    [Signal]
    public delegate void MountedEventHandler(int fileCount);

    [Signal]
    public delegate void MountFailedEventHandler(string reason);

    public string GameDirectory { get; private set; } = string.Empty;

    public bool IsMounted
    {
        get
        {
            lock (MountLock)
            {
                return _filesystem is not null;
            }
        }
    }

    public bool IsMounting
    {
        get
        {
            lock (MountLock)
            {
                return _mounting;
            }
        }
    }

    public int ArchiveCount => Snapshot()?.Archives.Count ?? 0;

    public int NestedArchiveCount => Snapshot()?.NestedArchiveCount ?? 0;

    public int FileCount => Snapshot()?.FileCount ?? 0;

    public int ShadowedFileCount => Snapshot()?.ShadowedFileCount ?? 0;

    public IReadOnlyList<string> Failures =>
        Snapshot()?.Failures ?? (IReadOnlyList<string>)Array.Empty<string>();

    public override void _EnterTree() => Instance = this;

    public override void _ExitTree()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        BnkFileSystem? previous;
        lock (MountLock)
        {
            previous = _filesystem;
            _filesystem = null;
        }

        previous?.Dispose();
    }

    public void Mount(string gameDirectory)
    {
        lock (MountLock)
        {
            if (_mounting)
            {
                return;
            }

            _mounting = true;
        }

        GameDirectory = gameDirectory ?? string.Empty;

        Task.Run(() =>
        {
            BnkFileSystem? mounted = null;
            string failure = string.Empty;
            try
            {
                mounted = BnkFileSystem.Mount(GameDirectory);
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }

            BnkFileSystem? previous;
            lock (MountLock)
            {
                previous = _filesystem;
                _filesystem = mounted;
                _mounting = false;
            }

            previous?.Dispose();

            int count = mounted?.FileCount ?? 0;
            if (mounted is null)
            {
                CallDeferred(nameof(ReportFailure), failure);
            }
            else
            {
                CallDeferred(nameof(ReportMounted), count);
            }
        });
    }

    private void ReportMounted(int fileCount)
    {
        EnsureTextureLoader();
        EmitSignal(SignalName.Mounted, fileCount);
    }

    private void EnsureTextureLoader()
    {
        Refii.Textures.Engine_BnkTextureLoader.SetResolver(ReadAllBytes);
        if (_textureLoader is null)
        {
            _textureLoader = new Refii.Textures.Engine_BnkTextureLoader();
            ResourceLoader.AddResourceFormatLoader(_textureLoader);
        }

        Refii.Models.Engine_BnkMeshLoader.SetResolver(ReadAllBytes);
        Refii.Models.Engine_BnkMeshLoader.SetExistsProbe(p => TryGetFile(p, out _));
        if (_meshLoader is null)
        {
            _meshLoader = new Refii.Models.Engine_BnkMeshLoader();
            ResourceLoader.AddResourceFormatLoader(_meshLoader);
        }
    }

    private Refii.Textures.Engine_BnkTextureLoader? _textureLoader;
    private Refii.Models.Engine_BnkMeshLoader? _meshLoader;

    private void ReportFailure(string reason) => EmitSignal(SignalName.MountFailed, reason);

    public bool TryGetFile(string virtualPath, out BnkFile file)
    {
        file = default!;
        BnkFileSystem? fs = Snapshot();
        return fs is not null && fs.TryGetFile(Strip(virtualPath), out file);
    }

    public byte[]? ReadAllBytes(string virtualPath) =>
        TryGetFile(virtualPath, out BnkFile file) ? file.ReadAllBytes() : null;

    public bool FileExists(string virtualPath) => TryGetFile(virtualPath, out _);

    public bool DirectoryExists(string virtualDirectory) =>
        Snapshot()?.DirectoryExists(Strip(virtualDirectory)) ?? false;

    public string[] GetDirectories(string virtualDirectory) =>
        Snapshot()?.EnumerateDirectories(Strip(virtualDirectory)).ToArray() ?? Array.Empty<string>();

    public string[] GetFiles(string virtualDirectory) =>
        Snapshot()?.EnumerateFiles(Strip(virtualDirectory)).ToArray() ?? Array.Empty<string>();

    public IEnumerable<BnkFile> EnumerateAllFiles() =>
        Snapshot()?.EnumerateAllFiles() ?? Enumerable.Empty<BnkFile>();

    private BnkFileSystem? Snapshot()
    {
        lock (MountLock)
        {
            return _filesystem;
        }
    }

    private static string Strip(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return path.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[PathPrefix.Length..]
            : path;
    }
}
