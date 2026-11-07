using System;
using System.Collections.Generic;
using Godot;
using Refii.Ui;

namespace Refii.Achievements;

/// <summary>One achievement as the title defines it, in the display language.</summary>
public readonly record struct AchievementDefinition(
	uint Id, uint ImageId, uint Gamerscore, uint Flags,
	string Name, string UnlockedDescription, string LockedDescription);

/// <summary>
/// THE ACHIEVEMENT SYSTEM. Definitions come from the title's XDBF (through the seam, once),
/// unlocks come from the guest's writes (a sequence read through the same seam), the store and
/// its file are ours, and the unlocked set goes back down so the guest's own enumerator reads the
/// same truth. The toast takes <see cref="TakeNewUnlocks"/>; the page opens on
/// <see cref="TakeUiRequests"/> — `XamShowAchievementsUI`, which the guest calls from its
/// logbook row (`sub_826D9620` case 150).
///
/// Everything here runs on Godot's thread: the file is written here, never on the guest thread
/// that recorded the write.
/// </summary>
public static class Engine_Achievements
{
	public const string FileName = "achievements.refii";

	private static Engine_XdbfDatabase? _database;
	private static Engine_AchievementStore? _store;
	private static readonly List<AchievementDefinition> _definitions = new();
	private static readonly Dictionary<uint, AchievementDefinition> _byId = new();
	private static readonly Dictionary<uint, Texture2D?> _icons = new();
	private static readonly List<uint> _newUnlocks = new();
	private static ulong _seenSequence;
	private static uint _seenUiRequests;
	private static bool _uiRequestsPrimed;
	private static int _xdbfRetryFrames;

	public static IReadOnlyList<AchievementDefinition> Definitions => _definitions;

	/// <summary>True once the XDBF has been read; definitions are empty until then.</summary>
	public static bool DefinitionsLoaded => _database is not null;

	public static string Report { get; private set; } = "not started";

	/// <summary>A harness's own store file; null takes the install's save directory.</summary>
	public static string? StorePathOverride { get; set; }

	public static string StorePath =>
		StorePathOverride ?? System.IO.Path.Combine(Refii.Core.Engine_AppRoot.SaveDirectory, FileName);

	public static AchievementDefinition? Definition(uint id) =>
		_byId.TryGetValue(id, out AchievementDefinition definition) ? definition : null;

	public static bool IsUnlocked(uint id) => _store?.IsUnlocked(id) ?? false;

	/// <summary>When it was earned, or null while locked or when the record carries no time.</summary>
	public static DateTime? UnlockedAt(uint id) =>
		_store?.Get(id) is { UnlockedUnixSeconds: > 0 } unlock
			? DateTimeOffset.FromUnixTimeSeconds(unlock.UnlockedUnixSeconds).LocalDateTime
			: null;

	public static int UnlockedCount
	{
		get
		{
			int count = 0;
			foreach (AchievementDefinition definition in _definitions)
			{
				if (IsUnlocked(definition.Id))
				{
					count++;
				}
			}

			return count;
		}
	}

	public static int TotalCount => _definitions.Count;

	public static uint UnlockedGamerscore
	{
		get
		{
			uint score = 0;
			foreach (AchievementDefinition definition in _definitions)
			{
				if (IsUnlocked(definition.Id))
				{
					score += definition.Gamerscore;
				}
			}

			return score;
		}
	}

	public static uint TotalGamerscore
	{
		get
		{
			uint score = 0;
			foreach (AchievementDefinition definition in _definitions)
			{
				score += definition.Gamerscore;
			}

			return score;
		}
	}

	/// <summary>The icon as a texture, cached; null when the title ships none for the id.</summary>
	public static Texture2D? Icon(uint id)
	{
		if (_icons.TryGetValue(id, out Texture2D? cached))
		{
			return cached;
		}

		Texture2D? texture = null;
		if (_byId.TryGetValue(id, out AchievementDefinition definition) &&
			_database?.Image(definition.ImageId) is { Length: > 0 } png)
		{
			var image = new Image();
			if (image.LoadPngFromBuffer(png) == Error.Ok)
			{
				texture = Refii.Textures.Engine_TextureUpload.CreateTexture(image);
			}
		}

		_icons[id] = texture;
		return texture;
	}

	/// <summary>Ids unlocked since the last call — the toast's queue. Loaded unlocks are not news.</summary>
	public static List<uint> TakeNewUnlocks()
	{
		var taken = new List<uint>(_newUnlocks);
		_newUnlocks.Clear();
		return taken;
	}

	/// <summary>How many times the guest asked for the achievements UI since the last call.</summary>
	public static int TakeUiRequests()
	{
		uint requests = Engine_UiSource.Current.GetAchievementsUiRequests();
		if (!_uiRequestsPrimed)
		{
			// A request from before this session's first poll is stale — the page must not open
			// on its own at boot.
			_uiRequestsPrimed = true;
			_seenUiRequests = requests;
			return 0;
		}

		int fresh = (int)(requests - _seenUiRequests);
		_seenUiRequests = requests;
		return Math.Max(fresh, 0);
	}

	/// <summary>Once per frame: load what is not loaded, ingest the guest's writes, persist.</summary>
	public static void Drive()
	{
		EnsureStore();

		if (_database is null && --_xdbfRetryFrames <= 0)
		{
			// The resource exists only once the module has launched; ask again every so often
			// rather than every frame — the copy is the whole XDBF.
			_xdbfRetryFrames = 60;
			if (Engine_XdbfDatabase.TryParse(Engine_UiSource.Current.GetTitleXdbf()) is { } database)
			{
				_database = database;
				Refii.Core.Engine_TitleIcon.Adopt(database);
				BuildDefinitions(database);
				PushUnlockedSet();
				Report = $"{_definitions.Count} definition(s) from the XDBF, " +
					$"{_store!.Count} unlock(s) loaded from {StorePath}" +
					(System.IO.File.Exists(StorePath) ? string.Empty : " (no file yet)");
				GD.Print($"[achievements] {Report}");
			}
		}

		Engine_UiModel.AchievementUnlock[] writes =
			Engine_UiSource.Current.GetAchievementUnlocks(_seenSequence, 64);
		if (writes.Length == 0)
		{
			return;
		}

		bool changed = false;
		foreach (Engine_UiModel.AchievementUnlock write in writes)
		{
			if (write.Sequence > _seenSequence)
			{
				_seenSequence = write.Sequence;
			}

			++_writesSeen;
			if (Unlock(write.UserIndex, write.AchievementId, "the guest wrote it"))
			{
				changed = true;
			}
			else
			{
				// Said once per id: the guest re-states what it holds at every boot, and a write
				// that lands on an unlock already in the store is the healthy case, not news.
				if (_restated.Add(write.AchievementId))
				{
					GD.Print($"[achievements] the guest re-stated {write.AchievementId} " +
						$"({Definition(write.AchievementId)?.Name ?? "no definition yet"}) — already unlocked");
				}
			}
		}

		if (changed)
		{
			Commit();
		}
	}

	private static int _writesSeen;

	private static readonly HashSet<uint> _restated = new();

	private static void EnsureStore()
	{
		if (_store is not null)
		{
			return;
		}

		_store = Engine_AchievementStore.Load(StorePath);
		if (_store.LoadProblem is { } problem)
		{
			GD.PushWarning($"[achievements] {StorePath} {problem} — starting empty; the file is " +
				"kept as '.unreadable' when the store is next written");
		}
	}

	/// <summary>
	/// THE ONE WAY AN ACHIEVEMENT BECOMES UNLOCKED — the guest's write and the debug channel's
	/// both land here, so what the test tooling exercises is the path the game takes: the store,
	/// the toast's queue, and (through <see cref="Commit"/>) the file and the guest's enumerator.
	/// False when it was unlocked already.
	/// </summary>
	private static bool Unlock(uint userIndex, uint achievementId, string why)
	{
		if (_store is null ||
			!_store.Add(userIndex, achievementId, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
		{
			return false;
		}

		_newUnlocks.Add(achievementId);
		GD.Print($"[achievements] unlocked {achievementId} " +
			$"({Definition(achievementId)?.Name ?? "no definition yet"}) — {why}");
		return true;
	}

	/// <summary>Writes the store and states the unlocked set down to the guest.</summary>
	private static void Commit()
	{
		if (_store is null)
		{
			return;
		}

		try
		{
			_store.Save(StorePath);
			_lastSaved = $"{_store.Count} unlock(s) at {DateTime.Now:HH:mm:ss}";
		}
		catch (Exception error)
		{
			_lastSaved = $"FAILED: {error.Message}";
			GD.PushWarning($"[achievements] could not write {StorePath}: {error.Message}");
		}

		PushUnlockedSet();
	}

	private static string _lastSaved = "never this session";

	/// <summary>Unlocks one achievement as the guest's write would. For the debug channel.</summary>
	public static bool DebugUnlock(uint achievementId)
	{
		EnsureStore();
		if (!Unlock(0u, achievementId, "the debug channel asked"))
		{
			return false;
		}

		Commit();
		return true;
	}

	/// <summary>Re-locks one achievement, or all of them with id 0. Returns how many went.</summary>
	public static int DebugLock(uint achievementId)
	{
		EnsureStore();
		int removed = achievementId == 0u
			? _store!.Clear()
			: _store!.Remove(achievementId) ? 1 : 0;
		if (removed > 0)
		{
			_restated.Clear();
			GD.Print($"[achievements] re-locked {(achievementId == 0u ? "everything" : achievementId.ToString())} " +
				$"({removed} unlock(s)) — the debug channel asked");
			Commit();
		}

		return removed;
	}

	/// <summary>The whole system's state, for the debug channel.</summary>
	public static Dictionary<string, object?> Describe(bool rows)
	{
		EnsureStore();
		var state = new Dictionary<string, object?>
		{
			["definitionsLoaded"] = DefinitionsLoaded,
			["definitions"] = _definitions.Count,
			["unlocked"] = UnlockedCount,
			["gamerscore"] = $"{UnlockedGamerscore}/{TotalGamerscore}",
			["storePath"] = StorePath,
			["storeFileExists"] = System.IO.File.Exists(StorePath),
			["storeUnlocks"] = _store!.Count,
			["storeLoadProblem"] = _store.LoadProblem,
			["lastSaved"] = _lastSaved,

			// The seam: how many guest writes have crossed the bridge this session. Zero while
			// the guest's own log shows XGIUserWriteAchievements is a seam that is not crossing.
			["guestWritesSeen"] = _writesSeen,
			["guestWriteSequence"] = _seenSequence,
			["toastsQueued"] = _newUnlocks.Count,
		};

		if (rows)
		{
			var list = new List<Dictionary<string, object?>>();
			foreach (AchievementDefinition definition in _definitions)
			{
				list.Add(new Dictionary<string, object?>
				{
					["id"] = definition.Id,
					["name"] = definition.Name,
					["gamerscore"] = definition.Gamerscore,
					["unlocked"] = IsUnlocked(definition.Id),
					["when"] = UnlockedAt(definition.Id)?.ToString("yyyy-MM-dd HH:mm:ss"),
					["hasIcon"] = _database?.Image(definition.ImageId) is { Length: > 0 },
				});
			}

			state["achievements"] = list;

			// An unlock the title does not define is still an unlock — and a sign of a bad id.
			var unknown = new List<uint>();
			foreach (Engine_AchievementStore.Unlock unlock in _store.Unlocks)
			{
				if (!_byId.ContainsKey(unlock.AchievementId))
				{
					unknown.Add(unlock.AchievementId);
				}
			}

			state["unlockedWithoutDefinition"] = unknown;
		}

		return state;
	}

	private static void BuildDefinitions(Engine_XdbfDatabase database)
	{
		_definitions.Clear();
		_byId.Clear();
		_icons.Clear();

		uint language = database.DefaultLanguage;
		if (!database.HasLanguage(language))
		{
			language = Engine_XdbfDatabase.LanguageEnglish;
		}

		foreach (Engine_XdbfDatabase.Achievement entry in database.Achievements)
		{
			var definition = new AchievementDefinition(
				entry.Id, entry.ImageId, entry.Gamerscore, entry.Flags,
				database.String(language, entry.LabelId),
				database.String(language, entry.DescriptionId),
				database.String(language, entry.UnachievedId));
			_definitions.Add(definition);
			_byId[entry.Id] = definition;
		}
	}

	/// <summary>The store, stated down to the seam for the guest's enumerator.</summary>
	private static void PushUnlockedSet()
	{
		if (_store is null)
		{
			return;
		}

		var ids = new List<uint>();
		var times = new List<ulong>();
		foreach (Engine_AchievementStore.Unlock unlock in _store.Unlocks)
		{
			ids.Add(unlock.AchievementId);
			times.Add(unlock.UnlockedUnixSeconds > 0
				? ((ulong)unlock.UnlockedUnixSeconds * 10_000_000ul) + 116444736000000000ul
				: 0ul);
		}

		Engine_UiSource.Current.SetAchievementUnlocks(ids.ToArray(), times.ToArray());
	}
}
