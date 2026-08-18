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

		// The locomotion state machine is READ from the player's own gamescripts bank rather
		// than transcribed into this repo, so it needs the mount before it can answer anything.
		Refii.Animations.Engine_LocomotionStates.SetResolver(ReadAllBytes);
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

		// EVERYTHING BELOW WAS REGISTERED ONLY BY THE EDITOR PLUGIN, so the running game had
		// none of it. That is why a level built with correct terrain and nothing standing on
		// it: Engine_PropModelIndex had no model source, so its index never built and all 2748
		// of BWSSlums's placements resolved to nothing (instances=0, unresolved=2335,
		// nomodel=413). The entity index was empty the same way — 7 creatures, 0 placed.
		//
		// The two lists are the same list and have to stay that way. addons/bnk_mount's
		// EnsureLoaders is the other copy; anything added there belongs here too, and the
		// failure mode is silent — the game renders, just emptier than the editor.
		Refii.Audio.Engine_BnkAudioLoader.SetResolver(ReadAllBytes);
		Refii.Gdb.Engine_BnkGdbLoader.SetResolver(ReadAllBytes);
		Refii.Level.Engine_Clouds.SetTexturePathSource(EnumerateTexturePaths);
		Refii.Textures.Engine_TexPathIndex.SetPathSource(EnumerateTexturePaths);
		Refii.Level.Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
		Refii.Level.Engine_StreamingCandidates.SetHintSource(EnumerateStreamingHints);
		Refii.Entities.Engine_EntityIndex.SetResolver(ReadAllBytes);

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

		// Audio was the last instance of the bug the comment above describes: the resolver
		// was set but the loader never registered here, only in addons/bnk_mount. So a sound
		// could be opened in the Godot editor and nowhere else — not in the editor we package
		// outside Godot, and not by the running game. One registration, three contexts.
		if (_audioLoader is null)
		{
			_audioLoader = new Refii.Audio.Engine_BnkAudioLoader();
			ResourceLoader.AddResourceFormatLoader(_audioLoader);
		}

		if (_swfTextureLoader is null)
		{
			_swfTextureLoader = new Refii.FlashGames.Engine_SwfTextureLoader();
			ResourceLoader.AddResourceFormatLoader(_swfTextureLoader, atFront: true);
		}

		if (_swfMeshLoader is null)
		{
			_swfMeshLoader = new Refii.FlashGames.Engine_SwfMeshLoader();
			ResourceLoader.AddResourceFormatLoader(_swfMeshLoader, atFront: true);
		}

		if (_swfFontLoader is null)
		{
			_swfFontLoader = new Refii.FlashGames.Engine_SwfFontLoader();
			ResourceLoader.AddResourceFormatLoader(_swfFontLoader, atFront: true);
		}
	}

	/// <summary>Every <c>.mdl</c> in the mount, for the prop model index.</summary>
	private IEnumerable<Refii.Level.PropModelAsset> EnumerateModelAssets()
	{
		BnkFileSystem? fs = Snapshot();
		if (fs is null)
		{
			yield break;
		}

		foreach (BnkFile file in fs.EnumerateAllFiles())
		{
			if (file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			{
				yield return new Refii.Level.PropModelAsset(
					file.VirtualPath, file.SourceArchivePath);
			}
		}
	}

	/// <summary>Every <c>.tex</c> path in the mount, for the cloud layers.</summary>
	private IEnumerable<string> EnumerateTexturePaths()
	{
		BnkFileSystem? fs = Snapshot();
		if (fs is null)
		{
			yield break;
		}

		foreach (BnkFile file in fs.EnumerateAllFiles())
		{
			if (file.VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
			{
				yield return file.VirtualPath;
			}
		}
	}

	/// <summary>
	/// Paths that came out of a STREAMING bank, which is what marks a model as one the game
	/// pages in rather than one that ships resident.
	/// </summary>
	private IEnumerable<string> EnumerateStreamingHints()
	{
		BnkFileSystem? fs = Snapshot();
		if (fs is null)
		{
			yield break;
		}

		foreach (BnkFile file in fs.EnumerateAllFiles())
		{
			if (file.SourceArchiveName.Contains("streaming", StringComparison.OrdinalIgnoreCase))
			{
				yield return file.VirtualPath;
			}
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
	/// <summary>The scenario a new game starts in. `sub_82379628` writes it at 0x8237972C.</summary>
	public const string DefaultScenario = "defaultscenario";

	/// <summary>
	/// The scenario the guest is in for this level. Defaults to DefaultScenario, which is both
	/// the guest's own fallback when its map has no entry and what a new game sets.
	///
	/// NOT YET READ FROM THE GUEST. The live value is a world-manager map keyed by
	/// FNV-1(world + level), written by the Lua binding `Layers.ActivateScenario`, and reaching
	/// it needs a host-bridge field that does not exist yet. Until it does this is right for a
	/// new game and wrong the moment a quest moves the story on, so it is a named constant
	/// rather than a silent assumption.
	/// </summary>
	public string CurrentScenario { get; set; } = DefaultScenario;

	/// <summary>
	/// Picks the level variant whose heightfield the scenario's own manifest names.
	/// </summary>
	private bool TryResolveByScenario(
		List<Refii.Level.LevelEntry> candidates, string world, string name,
		out Refii.Level.LevelEntry picked)
	{
		picked = candidates[0];

		string levelWorld = candidates[0].World;
		foreach (string scenario in new[] { CurrentScenario, DefaultScenario })
		{
			string listPath =
				$"worlds/{levelWorld}/{name.ToLowerInvariant()}/{scenario}/{scenario}.list";
			byte[]? bytes = ReadAllBytes(listPath) ?? ReadAllBytes("data/" + listPath);
			if (bytes is null)
			{
				continue;
			}

			string text = System.Text.Encoding.ASCII.GetString(bytes);
			if (System.Environment.GetEnvironmentVariable("REFII_DUMP_SCENARIO_LIST") == "1")
			{
				GD.Print($"[assets] ==== {listPath} ====\n{text}");
			}

			foreach (Refii.Level.LevelEntry entry in candidates)
			{
				string stem = System.IO.Path.GetFileNameWithoutExtension(entry.HeightfieldPath);
				if (text.Contains(stem, StringComparison.OrdinalIgnoreCase))
				{
					GD.Print($"[assets] {name}: scenario '{scenario}' names {entry.Variant}");
					entry.Scenario = scenario;
					picked = entry;
					return true;
				}
			}

			GD.Print($"[assets] {name}: {scenario}.list named no known heightfield");
		}

		GD.Print($"[assets] {name}: no scenario manifest found; falling back to name order");
		return false;
	}

	/// <summary>
	/// Builds a level and SAVES it as a scene. The editor's path — see
	/// <see cref="BuildLevelNode"/> for the game's.
	/// </summary>
	public bool TryBuildLevel(string world, string name, out string scenePath, out string error)
	{
		scenePath = string.Empty;
		if (!TryResolveLevel(world, name, out Refii.Level.LevelEntry entry, out error))
		{
			return false;
		}

		return Refii.Level.Engine_LevelScene.TryBuild(
			entry, ReadAllBytes, FindByFileName, out scenePath, out error);
	}

	/// <summary>
	/// Builds a level and hands back the live node, with no trip through the disk.
	///
	/// THE GAME'S PATH. The same construction the editor uses, minus the save and reload —
	/// `res://` is read-only in an exported build, so a game that mounts a level by saving a
	/// `.tscn` and loading it back only works from the project.
	/// </summary>
	public Node3D? BuildLevelNode(string world, string name, out string error)
	{
		if (!TryResolveLevel(world, name, out Refii.Level.LevelEntry entry, out error))
		{
			return null;
		}

		return Refii.Level.Engine_LevelScene.Build(entry, ReadAllBytes, FindByFileName, out error);
	}

	/// <summary>
	/// Picks WHICH level entry the guest means. Scenario first, then the dev override, then the
	/// level name with the world as a tiebreak.
	/// </summary>
	private bool TryResolveLevel(
		string world, string name, out Refii.Level.LevelEntry resolvedEntry, out string error)
	{
		resolvedEntry = default!;
		error = string.Empty;

		BnkFileSystem? fs = Snapshot();
		if (fs is null)
		{
			error = "banks are not mounted";
			return false;
		}

		_levels ??= Refii.Level.Engine_LevelIndex.Discover(fs);

		// THE GUEST NAMES A PATH, NOT ALWAYS A LEAF.
		//
		// Walking into a cave, the guest states its level as `Caves\GargoylesCave` — a nested
		// path in the guest's own separator, which `World_ComputeWorldLevelFnv1Key` normalises
		// to backslash. The index stores the full path in `LevelPath` and only the LAST segment
		// in `Name`, so matching `Name` against the whole thing found nothing and the region
		// never built: "no level named 'Caves\GargoylesCave'" while 83 levels sat under Albion.
		//
		// A grey screen with a correct camera is the symptom, which reads as a renderer fault
		// rather than a lookup miss — the same trap the world-tiebreak comment below describes.
		//
		// Normalise the separator and keep BOTH forms: the full path is the specific answer and
		// the leaf is the fallback, so a level stated either way resolves to the same entry.
		string wanted = name.Replace('\\', '/');
		string leaf = wanted[(wanted.LastIndexOf('/') + 1)..];

		// EVERY variant that answers to this name, named out loud.
		//
		// A level ships several heightfields — chapters, seasons, story states — and we pick by
		// name and take the first. That is a guess wearing the clothes of a lookup: it is how
		// Bowerstone Slums came up as `ch2_heightfield_posh` for a childhood entrance, which is a
		// different chapter of the same place and reads as the wrong season.
		// The FULL PATH wins when the guest gives one. Only if nothing answers to it do we fall
		// back to the leaf, because two worlds can each hold a `GargoylesCave` and taking the
		// leaf first would pick whichever was discovered earliest.
		var candidates = new List<Refii.Level.LevelEntry>();
		foreach (Refii.Level.LevelEntry entry in _levels)
		{
			if (entry.LevelPath.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase))
			{
				candidates.Add(entry);
			}
		}

		if (candidates.Count == 0)
		{
			foreach (Refii.Level.LevelEntry entry in _levels)
			{
				if (entry.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase))
				{
					candidates.Add(entry);
				}
			}
		}

		if (candidates.Count > 1)
		{
			GD.Print($"[assets] {name}: {candidates.Count} variants — " +
				string.Join(", ", candidates.ConvertAll(c => c.Variant)));
		}

		// THE SCENARIO PICKS THE HEIGHTFIELD. Not the name, not the chapter, not the entrance.
		//
		// The guest builds `Data\Worlds\<World>\<Level>\<Scenario>\<Scenario>.list` in
		// Level_BuildDataWorldsFilePath (0x8250EED0) and loads every .ghf that manifest names,
		// with no further filtering — Level_LoadHeightfieldGhfEntry (0x8250ECE0) just opens
		// what it is given. The scenario comes from the world manager's {fnv1(world+level) ->
		// name} map and defaults to "DefaultScenario"; a new game sets exactly that, at
		// 0x8237972C.
		//
		// Matching on the level name and taking the first of five is what put a CHILDHOOD
		// entrance in chapter 2's Bowerstone — a different chapter of the same place, which
		// reads as the wrong season because the seasons ARE the variants. scenarios.list
		// states them: albion\bwsslums has defaultscenario, chapter3slums, chapter3posh,
		// chapter2slums and chapter2posh, against heightfields new_heightfield_9,
		// ch3_heightfield_slums, ch3_heightfield_posh, ch2_heightfield_slums and
		// ch2_heightfield_posh.
		if (candidates.Count > 1 &&
			TryResolveByScenario(candidates, world, name, out Refii.Level.LevelEntry scenarioPick))
		{
			resolvedEntry = scenarioPick;
			return true;
		}

		// REFII_LEVEL_VARIANT is a DEV OVERRIDE for looking at a variant directly. It runs after
		// the scenario lookup so it can only ever override a resolved answer, never stand in for
		// one.
		string forced = System.Environment.GetEnvironmentVariable("REFII_LEVEL_VARIANT") ?? string.Empty;
		if (forced.Length != 0)
		{
			foreach (Refii.Level.LevelEntry entry in candidates)
			{
				if (entry.Variant.Contains(forced, StringComparison.OrdinalIgnoreCase))
				{
					GD.Print($"[assets] variant forced to {entry.Variant}");
					resolvedEntry = entry;
					return true;
				}
			}

			GD.PrintErr($"[assets] no variant of {name} matches '{forced}'");
		}


		// THE LEVEL NAME IS THE KEY; the world is only a tiebreak.
		//
		// The guest states a world/level pair, and it is tempting to require both. It does not
		// hold: starting a new game the guest says "Travel_TheWell/BWSSlums" with entrance
		// "QC010_ChildhoodStart", and Travel_TheWell is not a world in the bank layout at all —
		// the banks register worlds/albion/..., and BWSSlums (Bowerstone Slums) lives there.
		// That slot carries a travel-node name, not a bank world.
		//
		// Requiring both matched nothing, so the guest walked into a region that was never
		// built and Godot drew an empty world through a perfectly good camera — which reads as
		// "the renderer is broken" rather than "the lookup missed".
		//
		// So: prefer an exact world match when the guest does name a real world, otherwise take
		// the level name on its own. Names repeat only across variants of the SAME level, which
		// the first-match behaviour already handled.
		Refii.Level.LevelEntry? fallback = null;
		foreach (Refii.Level.LevelEntry level in _levels)
		{
			// Same two forms as the candidate scan above: the guest may state a nested path.
			if (!level.LevelPath.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase) &&
				!level.Name.Equals(leaf, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (level.World.Equals(world, StringComparison.OrdinalIgnoreCase))
			{
				resolvedEntry = level;
				return true;
			}

			fallback ??= level;
		}

		if (fallback is { } resolved)
		{
			// Named, not silent: resolving across a world mismatch is a real assumption, and if
			// it ever picks the wrong region this line is what says why.
			GD.Print($"[assets] the guest asked for {world}/{name}; the banks register it under " +
				$"{resolved.World}/{resolved.Name} — building that");

			resolvedEntry = resolved;
			return true;
		}

		error = $"no level named '{name}' in the mounted banks " +
			$"(the guest called its world '{world}'; known worlds carry " +
			$"{_levels.Count} level(s))";
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
	private Refii.Entities.Engine_BnkEntityLoader? _entityLoader;
	private Refii.Fx.Engine_BnkFxLoader? _fxLoader;
	private Refii.Audio.Engine_BnkAudioLoader? _audioLoader;
	private Refii.FlashGames.Engine_SwfTextureLoader? _swfTextureLoader;
	private Refii.FlashGames.Engine_SwfMeshLoader? _swfMeshLoader;
	private Refii.FlashGames.Engine_SwfFontLoader? _swfFontLoader;
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
