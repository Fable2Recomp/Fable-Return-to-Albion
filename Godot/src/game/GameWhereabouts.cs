using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Where the player is: the level the guest states it is standing in, and the name the game
/// shows for it. One source for everything that needs to say so — the debug channel, the perf
/// overlay, the loading banner, and presence integrations such as Discord.
/// </summary>
/// <remarks>
/// STATED, NOT INFERRED. The level comes from <see cref="HostBridge.GetCurrentLevel"/>, the
/// guest's own record of the level it is in (its key is FNV-1 of lowercase "world/level" at
/// Level+0xB4, which changes on exactly the transitions that reload a level). Nothing here reads
/// the camera, the terrain or the sky to guess a region.
///
/// The display name is the game's own: `sub_8231CE98`'s GDB step, the `LevelNameTextTags`
/// record in `globals.gdb`, through Babel (<see cref="Refii.Text.Engine_LevelNames"/>). The
/// guest's other step — the world's script-override map, written only by Lua
/// `SetLevelDisplayName` and used once by the shipped scripts (Thag's Cave in chapter 2) — is not
/// read, so that one cave keeps its GDB name here.
/// </remarks>
public static class GameWhereabouts
{
	/// <summary>One answer: the stated level and its display name.</summary>
	public readonly record struct Place(
		string World, string LevelKey, string Entrance, string DisplayName, uint Key);

	private static Refii.Text.Engine_LevelNames? _names;

	private static Place? _last;

	private static bool _lastResolved;

	/// <summary>
	/// The game's string table, handed over by the UI when its presentation host loads it — the
	/// same instance the HUD reads. Null until then.
	/// </summary>
	public static Refii.Text.Engine_BabelDatabase? Strings { get; set; }

	/// <summary>
	/// The player's level right now, or null before the first load, between levels, and when no
	/// guest is up. Cached by the guest's level key, so asking every frame costs one bridge read.
	/// </summary>
	public static Place? Current
	{
		get
		{
			if (HostBridge.GetCurrentLevel() is not { } level)
			{
				return null;
			}

			if (_last is { } known && known.Key == level.Key && _lastResolved)
			{
				return known;
			}

			// Not cached until the string table is up: an ask before it loads answers the Babel
			// tag (`TEXT_LEVEL_RAVENSCAR`), and the overlay asks from the first frame.
			Refii.Text.Engine_BabelDatabase? strings = Strings;
			string display = DisplayName(level.Level, strings);
			_last = new Place(level.World, level.Level, level.Entrance, display, level.Key);
			_lastResolved = strings is not null;
			return _last;
		}
	}

	/// <summary>
	/// A level key's display name, or the key itself when the table does not name it or cannot
	/// be read yet. The table is loaded on the first ask; a failed load is not cached, because the
	/// first ask can come before the bank resolver is registered.
	/// </summary>
	public static string DisplayName(string levelKey, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (levelKey.Length == 0)
		{
			return levelKey;
		}

		if (_names is null || _names.Failure.Length > 0)
		{
			_names = Refii.Text.Engine_LevelNames.Load(Refii.Gdb.Engine_BnkGdbLoader.ReadBytes);
			Godot.GD.Print(_names.Failure.Length > 0
				? $"[whereabouts] level names unavailable ({_names.Failure}); showing level keys"
				: $"[whereabouts] {_names.Count} level name tags from " +
				  Refii.Text.Engine_LevelNames.GlobalsPath);
		}

		return _names.DisplayName(levelKey, strings);
	}
}
