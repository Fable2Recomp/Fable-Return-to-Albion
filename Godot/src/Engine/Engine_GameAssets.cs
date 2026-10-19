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
	private static IReadOnlyList<Refii.Mods.ModOverlay> _activeOverlays = Array.Empty<Refii.Mods.ModOverlay>();

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

	public void Unmount()
	{
		BnkFileSystem? previous;
		lock (MountLock)
		{
			previous = _filesystem;
			_filesystem = null;
			_byFileName = null;
			_levels = null;
			_activeOverlays = Array.Empty<Refii.Mods.ModOverlay>();
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
			BnkFileSystem? mounted = BuildMount(live: false, out _, out string failure);

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

			if (mounted is not null)
			{
				// The guest's asset reader is bound in EnsureTextureLoader (main thread, deferred).
			}

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

	/// <summary>
	/// The banks with every enabled mod folded in, built off the main thread. <paramref name="live"/>
	/// leaves the readers that live on the main thread (input bindings, FX recipes, the spell
	/// pages) to the caller, for a session already running. Null when the mount itself failed;
	/// a failure in a later pass is returned with the mount.
	/// </summary>
	private BnkFileSystem? BuildMount(
		bool live, out IReadOnlyList<Refii.Mods.ModOverlay> overlays, out string failure)
	{
		BnkFileSystem? mounted = null;
		overlays = Array.Empty<Refii.Mods.ModOverlay>();
		failure = string.Empty;
		try
		{
			// THE MODS ARE PART OF THE MOUNT. Resolving them here, on the worker, keeps one
			// answer to "what is installed": the tree the guest reads through
			// `host_asset_reader.h` and the tree Godot's own loaders read are the same one,
			// mods included, because there is only one of them.
			overlays = Refii.Mods.ModIndex.ResolveOverlays(
				ModsRoot, out Refii.Mods.ModLoadOrder order);
			mounted = BnkFileSystem.Mount(
				GameDirectory,
				overlays: Refii.Mods.ModIndex.ToMountOverlays(overlays),
				updateRoot: Refii.Core.Engine_GameData.UpdateRootFor(GameDirectory));
			ApplyModOrder(mounted, overlays, order, live);
			Refii.Host.CoopHeroLua.Attach(mounted);
			Refii.Host.CoopText.Attach(mounted);
			try { Refii.Save.Engine_GoldenCockRuntime.Attach(mounted, Refii.Save.Engine_GoldenCockPlacements.Load()); }
			catch (Exception exception) { GD.PushError("[golden-cock] " + exception.Message); }
			Refii.Save.Engine_NewGameRuntime.Attach(mounted);
		}
		catch (Exception exception)
		{
			failure = exception.Message;
		}

		return mounted;
	}

	/// <summary>How long a replaced tree stays open after a live remount: a reader on another
	/// thread (the guest's asset reads, a level or texture worker) may have taken it just before
	/// the swap, and each holds it for one read or one walk.</summary>
	private const double RetireSeconds = 30.0;

	/// <summary>What a live remount produced.</summary>
	/// <param name="Ok">A tree was built and is now served.</param>
	/// <param name="Failure">Why the mount, or one of its passes, failed; empty when none did.</param>
	/// <param name="Mods">The enabled mods folded in, lowest priority first.</param>
	/// <param name="Changed">Mod-contributed paths, in the old tree or the new, whose bytes differ between them.</param>
	public sealed record RemountOutcome(
		bool Ok, string Failure, IReadOnlyList<string> Mods, int FileCount, IReadOnlyList<string> Changed);

	/// <summary>
	/// Builds the mount again from the mods installed now, for a session that is running: the
	/// same passes as <see cref="Mount"/> on a worker, the old tree serving until the new one is
	/// swapped in and closed <see cref="RetireSeconds"/> later. Then, on the main thread, the
	/// loaders are bound to the new tree, <see cref="Mounted"/> is emitted and
	/// <paramref name="completed"/> runs. False, and nothing starts, while a mount is running or
	/// when nothing is mounted.
	/// </summary>
	public bool Remount(Action<RemountOutcome> completed)
	{
		BnkFileSystem? before;
		lock (MountLock)
		{
			if (_mounting || _filesystem is null)
			{
				return false;
			}

			_mounting = true;
			before = _filesystem;
		}

		Task.Run(() =>
		{
			BnkFileSystem? mounted = BuildMount(
				live: true, out IReadOnlyList<Refii.Mods.ModOverlay> overlays, out string failure);
			IReadOnlyList<string> changed = mounted is null
				? Array.Empty<string>()
				: ChangedModPaths(before, mounted);

			lock (MountLock)
			{
				if (mounted is not null)
				{
					_filesystem = mounted;
					_byFileName = null;
					_levels = null;
				}

				_mounting = false;
			}

			if (mounted is not null)
			{
				_ = Task.Delay(TimeSpan.FromSeconds(RetireSeconds)).ContinueWith(_ => before.Dispose());
			}

			var outcome = new RemountOutcome(
				mounted is not null, failure, overlays.Select(o => o.Name).ToList(),
				mounted?.FileCount ?? 0, changed);
			Callable.From(() => FinishRemount(outcome, completed)).CallDeferred();
		});

		return true;
	}

	private void FinishRemount(RemountOutcome outcome, Action<RemountOutcome> completed)
	{
		if (outcome.Ok)
		{
			EnsureTextureLoader();
			EmitSignal(SignalName.Mounted, outcome.FileCount);
		}

		completed(outcome);
	}

	/// <summary>Every path a mod contributes in either tree whose content is not the same in both.</summary>
	private static List<string> ChangedModPaths(BnkFileSystem before, BnkFileSystem after)
	{
		var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (BnkFileSystem tree in new[] { before, after })
		{
			foreach (BnkFile file in tree.EnumerateAllFiles())
			{
				if (file.SourceMod.Length != 0)
				{
					candidates.Add(file.VirtualPath);
				}
			}
		}

		var changed = new List<string>();
		foreach (string path in candidates)
		{
			byte[]? old = ReadOrNull(before, path);
			byte[]? now = ReadOrNull(after, path);
			if (old is null != now is null ||
				(old is not null && now is not null && !old.AsSpan().SequenceEqual(now)))
			{
				changed.Add(path);
			}
		}

		changed.Sort(StringComparer.OrdinalIgnoreCase);
		return changed;
	}

	private static byte[]? ReadOrNull(BnkFileSystem tree, string path)
	{
		try
		{
			return tree.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Installed mods, beside the player's game data.
	///
	/// By convention rather than by config: <c>&lt;app root&gt;/assets/mods</c>, the folder the
	/// launcher's panel already writes to.
	/// </summary>
	public static string ModsRoot =>
		Refii.Mods.ModWorkspace.GetDefaultModsRoot(Refii.Core.Engine_AppRoot.AssetsDirectory);

	/// <summary>Where the fold's report is written for the panel and for a reader of a run.</summary>
	public static string ResolvedReportPath =>
		ProjectSettings.GlobalizePath("user://mods/" + Refii.Mods.ModIndex.ReportFilename);

	/// <summary>
	/// Applies pins, then states what the fold decided.
	///
	/// The report is DERIVED from the finished mount, so it describes what the game will
	/// actually serve rather than what the load order intended.
	///
	/// <paramref name="live"/>: a session is running, so the readers the main thread walks every
	/// frame are not reset here; <see cref="Refii.Mods.ModRemount"/> resets them on that thread.
	/// </summary>
	private static void ApplyModOrder(
		BnkFileSystem mounted,
		IReadOnlyList<Refii.Mods.ModOverlay> overlays,
		Refii.Mods.ModLoadOrder order,
		bool live)
	{
		_activeOverlays = overlays;
		if (overlays.Count == 0)
		{
			Refii.Gdb.GdbPatchCompositor.PurgeCache(ModsRoot);
			return;
		}

		// EVERY CONTENT KIND AGAINST ITS LOADER'S SCHEMA, once per mount and before any loader
		// reads it (ModValidate): an error is content a loader refuses or misreads, a warning
		// content it ignores.
		foreach ((string mod, Refii.Mods.ModIssue issue) in Refii.Mods.ModValidate.ValidateOverlays(overlays))
		{
			string line = $"[mods] {mod}/{issue}";
			if (issue.Error)
			{
				GD.PushError(line);
			}
			else
			{
				GD.PushWarning(line);
			}
		}

		foreach ((string path, string owner) in order.Pins)
		{
			if (!mounted.Promote(path, owner))
			{
				GD.PushWarning(
					$"[mods] pin '{path}' -> '{owner}' matched nothing; '{owner}' does not " +
					"contribute that file.");
			}
		}

		// ROWS AFTER PINS, because a row overlay edits whatever the file fold left in place: a
		// mod that replaces `globals.gdb` outright and another that edits one of its rows compose
		// in the obvious order.
		var merges = Refii.Mods.ModRowOverlays.Apply(mounted, overlays);
		foreach (Refii.Mods.RowMerge merge in merges)
		{
			GD.Print($"[mods] rows {Refii.Mods.ModRowOverlays.Describe(merge)}");
			foreach (string refused in merge.Errors)
			{
				GD.PushWarning($"[mods] {refused}");
			}
		}

		// IMAGES BEFORE THE REPORT, so a converted texture is attributed like everything else.
		// After the row fold because both add virtual files and neither reads the other's.
		foreach (Refii.Mods.ImageConversion conversion in
			Refii.Mods.ModImageOverlays.Apply(mounted, overlays))
		{
			if (conversion.Ok)
			{
				GD.Print($"[mods] image {Refii.Mods.ModImageOverlays.Describe(conversion)}");
			}
			else
			{
				GD.PushWarning($"[mods] {Refii.Mods.ModImageOverlays.Describe(conversion)}");
			}
		}

		// MODELS AFTER IMAGES, for the same reason images come after rows: each adds virtual
		// files and none reads another's.
		foreach (Refii.Mods.MeshConversion conversion in
			Refii.Mods.ModMeshOverlays.Apply(mounted, overlays))
		{
			if (conversion.Ok)
			{
				GD.Print($"[mods] model {Refii.Mods.ModMeshOverlays.Describe(conversion)}");
			}
			else
			{
				GD.PushWarning($"[mods] {Refii.Mods.ModMeshOverlays.Describe(conversion)}");
			}
		}

		// CUSTOM SPELLS before the Lua composition: spells/*.json stands in for one of the eight
		// spell types — its Lua joins the init chunk, its name rows go over the book the row pass
		// left, its icon and look are a file overlay and a recipe.
		foreach (string line in Refii.Mods.ModSpells.Apply(mounted, overlays))
		{
			GD.Print($"[mods] spell {line}");
		}

		// MOD LUA: every enabled mod's mod/init.lua and mod/camera.lua, composed for the payload hook
		// that runs them after the shipped scripts.
		Refii.Mods.ModLua.Apply(mounted, overlays);
		if (!live)
		{
			Refii.Mods.ModSettings.Reload();
			Refii.Mods.ModInput.Reload();
			Refii.Fx.Engine_FxRecipes.Reload();
			Refii.Ui.Engine_UiSpellAssign.ReloadSecondary();
		}

		// Ensure GDB resolvers are bound so composite synthesis can access the unpatched base database:
		Refii.Gdb.Engine_GameGdb.SetResolver(p => mounted.TryGetFile(p, out var f) ? f.ReadAllBytes() : null);
		Refii.Gdb.Engine_GameGdb.SetBaseResolver(p => mounted.ReadBaseBytes(p));

		// TITLE UPDATE PATCHES: Compose multi-mod GDB databases and .save layer maps in load order:
		var composites = Refii.Gdb.GdbPatchCompositor.Apply(mounted, overlays, ModsRoot);
		Refii.Gdb.SaveMapCompositor.Apply(mounted, overlays);
		foreach (Refii.Gdb.CompositeGdbResult comp in composites)
		{
			GD.Print($"[mods] tu composite: {comp.SubPath} across {comp.ContributingMods.Count} mod(s) ({comp.Conflicts.Count} conflict(s))");
			foreach (Refii.Gdb.GdbRecordConflict conflict in comp.Conflicts)
			{
				GD.Print($"[mods]   override: {conflict}");
			}
		}

		var entries = Refii.Mods.ModIndex.Build(mounted);
		if (!Refii.Mods.ModIndex.WriteReport(
				ResolvedReportPath, Instance?.GameDirectory ?? string.Empty, overlays, entries,
				merges, out string error))
		{
			GD.PushWarning($"[mods] the resolved index could not be written: {error}");
		}

		int overrides = 0;
		int additions = 0;
		foreach (Refii.Mods.ResolvedEntry entry in entries)
		{
			if (entry.Kind == Refii.Mods.ResolvedEntry.KindAddition)
			{
				additions++;
			}
			else
			{
				overrides++;
			}
		}

		GD.Print(
			$"[mods] {overlays.Count} enabled, {overrides} override(s), {additions} addition(s) " +
			$"— see {ResolvedReportPath}");
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

		// Read from the player's gamescripts bank, never transcribed into the repo.
		Refii.Animations.Engine_LocomotionStates.SetResolver(ReadAllBytes);

		// The game's GDB for the owned detail pages; bound here AND in addons/bnk_mount.
		Refii.Gdb.Engine_GameGdb.SetResolver(ReadAllBytes);
		Refii.Gdb.Engine_GameGdb.SetBaseResolver(p => Snapshot()?.ReadBaseBytes(p));

		// The game's text bank for localized item names and descriptions; bound here AND in addons/bnk_mount.
		Refii.Gdb.Engine_BnkTextBankLoader.SetGameDirectory(GameDirectory);
		Refii.Gdb.Engine_BnkTextBankLoader.SetResolver(ReadAllBytes);
		Refii.Gdb.Engine_BnkTextBankLoader.Invalidate();

		// The guest reads through this same mount.
		Refii.Host.HostBridge.SetAssetReader(ReadAllBytes, FileSize);
		Refii.Host.HostBridge.SetAssetOverrideQuery(FileIsOverride);

		// Guest texture requests carry a 32-bit hash and no path; the index is a view of this mount.
		Refii.Bnk.AssetHashIndex.Build(EnumerateAllFiles());
		Refii.Host.HostBridge.SetAssetNameResolver(Refii.Bnk.AssetHashIndex.PathForHash);
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

		// Mirrors addons/bnk_mount's EnsureLoaders; a missing binding fails silently.
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

	/// <summary>Paths from a streaming bank: models the game pages in rather than ships resident.</summary>
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

	/// <summary>The scenario a new game starts in. `sub_82379628` writes it at 0x8237972C.</summary>
	public const string DefaultScenario = "defaultscenario";

	/// <summary>The scenario for this level; the guest's live value is a world-manager map keyed
	/// FNV-1(world + level), written by `Layers.ActivateScenario`.</summary>
	public string CurrentScenario { get; set; } = DefaultScenario;

	/// <summary>Picks the level variant whose heightfield the scenario's manifest names.</summary>
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

	/// <summary>Builds a level and saves it as a scene (editor path; the game uses <see cref="BuildLevelNode"/>).</summary>
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

	/// <summary>Every discovered level entry across all mounted banks.</summary>
	public IReadOnlyList<Refii.Level.LevelEntry> DiscoveredLevels()
	{
		if (Snapshot() is not { } fs)
		{
			return Array.Empty<Refii.Level.LevelEntry>();
		}

		return _levels ??= Refii.Level.Engine_LevelIndex.Discover(fs);
	}

	/// <summary>Every discovered level's world and path, once; for tools that walk them all.</summary>
	public List<(string World, string LevelPath)> LevelPaths()
	{
		var paths = new List<(string, string)>();
		if (Snapshot() is not { } fs)
		{
			return paths;
		}

		_levels ??= Refii.Level.Engine_LevelIndex.Discover(fs);
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Refii.Level.LevelEntry entry in _levels)
		{
			if (seen.Add(entry.World + "/" + entry.LevelPath))
			{
				paths.Add((entry.World, entry.LevelPath));
			}
		}

		return paths;
	}

	/// <summary>The level the guest's (world, name) resolves to — the game's own resolution.</summary>
	public bool TryResolve(string world, string name, out Refii.Level.LevelEntry entry, out string error) =>
		TryResolveLevel(world, name, out entry, out error);

	/// <summary>Builds a level and returns the live node; no disk trip (`res://` is read-only when exported).</summary>
	public Node3D? BuildLevelNode(string world, string name, out string error)
	{
		if (!TryResolveLevel(world, name, out Refii.Level.LevelEntry entry, out error))
		{
			return null;
		}

		return Refii.Level.Engine_LevelScene.Build(entry, ReadAllBytes, FindByFileName, out error);
	}

	/// <summary>Resolves the guest's level: scenario first, then the dev override, then name with world as tiebreak.</summary>
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

		// The guest may state a nested path (`Caves\GargoylesCave`, backslash-normalised by
		// `World_ComputeWorldLevelFnv1Key`); `LevelPath` holds the full path, `Name` the leaf.
		string wanted = name.Replace('\\', '/');
		string leaf = wanted[(wanted.LastIndexOf('/') + 1)..];

		// Full path wins; the leaf is a fallback — two worlds can each hold a `GargoylesCave`.
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

		// The scenario picks the heightfield: `Data\Worlds\<World>\<Level>\<Scenario>\<Scenario>.list`
		// (Level_BuildDataWorldsFilePath 0x8250EED0) names every .ghf loaded unfiltered
		// (Level_LoadHeightfieldGhfEntry 0x8250ECE0). Default "DefaultScenario", set at 0x8237972C.
		if (candidates.Count > 1 &&
			TryResolveByScenario(candidates, world, name, out Refii.Level.LevelEntry scenarioPick))
		{
			resolvedEntry = scenarioPick;
			return true;
		}

		// REFII_LEVEL_VARIANT is a dev override; it runs after the scenario lookup.
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


		// The level name is the key; the world is only a tiebreak. The guest's world slot can
		// carry a travel-node name ("Travel_TheWell/BWSSlums") that is not a bank world.
		Refii.Level.LevelEntry? fallback = null;
		foreach (Refii.Level.LevelEntry level in _levels)
		{
			// Same two forms as the candidate scan above.
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

	[ThreadStatic]
	private static (BnkFile? File, byte[]? Bytes) _lastRead;

	public byte[]? ReadAllBytes(string virtualPath)
	{
		if (!TryGetFile(virtualPath, out BnkFile file))
		{
			return null;
		}

		if (ReferenceEquals(_lastRead.File, file) && _lastRead.Bytes is { } cached)
		{
			_lastRead = default;
			return cached;
		}

		return file.ReadAllBytes();
	}

	public bool FileExists(string virtualPath) => TryGetFile(virtualPath, out _);

	/// <summary>
	/// Returns whether the mounted file is contributed by a mod or virtual overlay rather than base game data.
	/// Used by the native C++ VFS bridge to determine when an in-memory virtual asset should take precedence
	/// over a loose file residing on disk (such as globals.gdb or book.babel).
	/// </summary>
	public bool FileIsOverride(string virtualPath)
	{
		if (TryGetFile(virtualPath, out BnkFile file))
		{
			// True if attributed to a mod name, marked as override/loose, or created via virtual row/mesh/image/save overlays:
			return file.IsOverride || file.SourceMod.Length != 0 ||
				file.SourceArchivePath.StartsWith("<rows:") ||
				file.SourceArchivePath.StartsWith("<save_tu:") ||
				virtualPath.EndsWith(".gdb.tu", StringComparison.OrdinalIgnoreCase) ||
				virtualPath.EndsWith(".save.tu", StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	/// <summary>The asset's length without reading it, or -1 if absent. Files with alternates
	/// (`.mdl`, `.tex`) must be read: <see cref="BnkFile.ReadAllBytes"/> concatenates them.</summary>
	public long FileSize(string virtualPath)
	{
		if (!TryGetFile(virtualPath, out BnkFile file))
		{
			return -1;
		}

		if (!file.HasAlternates || file.IsOverride)
		{
			return file.Size;
		}

		if (file.CachedResolvedSize >= 0)
		{
			return file.CachedResolvedSize;
		}

		byte[] bytes = file.ReadAllBytes();
		_lastRead = (file, bytes);
		return bytes.LongLength;
	}

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
