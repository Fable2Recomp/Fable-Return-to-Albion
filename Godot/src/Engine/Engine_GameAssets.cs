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
				_byFileName = null;
				_levels = null;
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

		Refii.Video.Engine_BnkVideoLoader.SetResolver(ReadAllBytes);
		Refii.Video.Engine_BnkVideoLoader.SetLibrary(
			Refii.Video.Engine_VideoLibrary.Load(GameDirectory));
		if (_videoLoader is null)
		{
			_videoLoader = new Refii.Video.Engine_BnkVideoLoader();
			ResourceLoader.AddResourceFormatLoader(_videoLoader);
		}

		Refii.Level.Engine_BnkTerrainLoader.SetResolver(ReadAllBytes);
		Refii.Level.Engine_BnkTerrainAlbedoLoader.SetResolver(ReadAllBytes);
		Refii.Level.Engine_BnkTerrainAlbedoLoader.SetHeightfieldLookup(HeightfieldForEhf);
		Refii.Level.Engine_BnkTerrainMaterialLoader.SetResolver(ReadAllBytes);
		Refii.Level.Engine_BnkTerrainMaterialLoader.SetNameLookup(FindByFileName);

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

	/// <summary>
	/// Builds the region the GUEST named, through the SAME construction path the editor
	/// uses — <see cref="Refii.Level.Engine_LevelScene.TryBuild"/>.
	///
	/// It lives here rather than in the game code because everything it needs is already
	/// here: the mounted banks, the byte reader and the name lookup the terrain materials
	/// resolve through. Reaching into those from outside would mean a second way to build a
	/// level, and the editor and the running game executing different construction paths is
	/// exactly what this project is arranged to prevent.
	/// </summary>
	public bool TryBuildLevel(string world, string name, out string scenePath, out string error)
	{
		scenePath = string.Empty;
		error = string.Empty;

		BnkFileSystem? fs = Snapshot();
		if (fs is null)
		{
			error = "banks are not mounted";
			return false;
		}

		_levels ??= Refii.Level.Engine_LevelIndex.Discover(fs);

		foreach (Refii.Level.LevelEntry level in _levels)
		{
			if (!level.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
				!level.World.Equals(world, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			return Refii.Level.Engine_LevelScene.TryBuild(
				level, ReadAllBytes, FindByFileName, out scenePath, out error);
		}

		error = $"no level named {world}/{name} in the mounted banks";
		return false;
	}

	private string? FindByFileName(string fileName)
	{
		Dictionary<string, string>? index = _byFileName;
		if (index is null)
		{
			BnkFileSystem? fs = Snapshot();
			if (fs is null)
			{
				return null;
			}

			index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (BnkFile file in fs.EnumerateAllFiles())
			{
				string path = file.VirtualPath;
				int slash = path.LastIndexOf('/');
				string name = slash < 0 ? path : path[(slash + 1)..];
				if (name.Length != 0)
				{
					index.TryAdd(name, path);
				}
			}

			_byFileName = index;
		}

		return index.TryGetValue(fileName, out string? found) ? found : null;
	}

	private string? HeightfieldForEhf(string ehfVirtualPath)
	{
		BnkFileSystem? fs = Snapshot();
		if (fs is null)
		{
			return null;
		}

		_levels ??= Refii.Level.Engine_LevelIndex.Discover(fs);

		foreach (Refii.Level.LevelEntry level in _levels)
		{
			if (level.MainEhfPath.Equals(ehfVirtualPath, StringComparison.OrdinalIgnoreCase))
			{
				return level.HeightfieldPath;
			}
		}

		return null;
	}

	private Refii.Textures.Engine_BnkTextureLoader? _textureLoader;
	private Refii.Models.Engine_BnkMeshLoader? _meshLoader;
	private Refii.Video.Engine_BnkVideoLoader? _videoLoader;
	private Refii.Level.Engine_BnkTerrainLoader? _terrainLoader;
	private Refii.Level.Engine_BnkTerrainAlbedoLoader? _albedoLoader;
	private Refii.Level.Engine_BnkTerrainMaterialLoader? _terrainMaterialLoader;
	private Dictionary<string, string>? _byFileName;
	private List<Refii.Level.LevelEntry>? _levels;

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
