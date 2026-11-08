using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The region map as a mod reads it (<c>RefiiHost.minimap</c>): the level's <c>MinimapInfo</c> record
/// (<see cref="Engine_UiMinimapPage.Resolve(string, string, string)"/>), its map bounds from its own
/// heightfields (<see cref="Refii.Level.Engine_LevelMapBounds"/>), the map image's size from the pause root's
/// file, the named hero's place and turn by the pause root's own rules (<see cref="Engine_UiMinimapPage.Place"/>,
/// <see cref="Engine_UiPauseMinimap.PlayerTurn"/>), and the other quests' objectives from the quest log
/// (<see cref="Fable2Bridge.GetQuestLog(System.Collections.Generic.List{Fable2Bridge.QuestEntry})"/>: the
/// non-primary quests' objective entities, as the world map's minimap plots them). The log is the hero's whose
/// menus this machine shows (<c>RefiiHostGetQuestLog</c> takes no player). Places are in the image's element
/// units around its centre, Y up.
/// </summary>
public static class Engine_UiMinimapMod
{
	/// <summary>Resolves a resource-path hash to a texture; the shell states it with the HUD's.</summary>
	public static System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	/// <summary>The pause root's parsed file, for the map image's size; the shell states it with the HUD's.</summary>
	public static System.Func<Engine_UiScreen?>? PauseRoot { get; set; }

	private static string _levelFor = string.Empty;

	private static string _scenarioFor = string.Empty;

	private static string _extentKey = string.Empty;

	private static float _extent;

	private static float? _halfSize;

	/// <summary>The log <see cref="State"/> walks, kept: a mod's map asks every frame and holds none of it.</summary>
	private static readonly System.Collections.Generic.List<Fable2Bridge.QuestEntry> _questLog = new();

	/// <summary>The current level's record, or null when none stands or the GDB names none.</summary>
	private static Engine_UiMinimapPage.Info? CurrentInfo(out Refii.Host.HostBridge.CurrentLevel level)
	{
		if (Refii.Host.HostBridge.GetCurrentLevel() is not { Level.Length: > 0 } standing)
		{
			level = default;
			return null;
		}

		level = standing;
		if (standing.Level != _levelFor)
		{
			_levelFor = standing.Level;
			_scenarioFor = Refii.Host.HostBridge.GetActiveScenario() ?? string.Empty;
		}

		return Engine_UiMinimapPage.Resolve(standing.Level, _scenarioFor, "mod");
	}

	/// <summary>The current level's map extent from its scenario's heightfields, once per level; 0 when it cannot be read.</summary>
	private static float LevelExtent(in Refii.Host.HostBridge.CurrentLevel level)
	{
		string key = level.World + "\u0001" + level.Level + "\u0001" + _scenarioFor;
		if (key == _extentKey)
		{
			return _extent;
		}

		float extent = 0f;
		if (Refii.Assets.Engine_GameAssets.Instance is { IsMounted: true } assets &&
			assets.TryResolve(level.World, level.Level, out Refii.Level.LevelEntry entry, out _) &&
			Refii.Level.Engine_LevelManifest.TryResolve(entry, assets.ReadAllBytes, out Refii.Level.ScenarioManifest manifest, out _, out _) &&
			Refii.Level.Engine_LevelMapBounds.Of(manifest, assets.ReadAllBytes) is { } bounds)
		{
			extent = bounds.Extent;
		}

		_extentKey = key;
		_extent = extent;
		return extent;
	}

	/// <summary>The map image's half-size, read once from the pause root's file.</summary>
	private static float? HalfSize() => _halfSize ??= Engine_UiPauseMinimap.ImageHalfSize(PauseRoot?.Invoke());

	/// <summary>The map for <paramref name="hero"/> (an entity address, 0 for none).</summary>
	public static Godot.Collections.Dictionary State(uint hero)
	{
		var said = new Godot.Collections.Dictionary();
		if (HalfSize() is { } half)
		{
			said["half_size"] = half;
		}

		if (CurrentInfo(out Refii.Host.HostBridge.CurrentLevel level) is not { } info)
		{
			said["known"] = false;
			return said;
		}

		said["known"] = true;
		said["proper"] = info.Proper;
		said["north"] = Mathf.DegToRad(info.DegreesToNorth);
		if (info.Background != 0u && ResolveTexture?.Invoke(info.Background) is { } texture)
		{
			said["texture"] = texture;
			if (Refii.Bnk.AssetHashIndex.PathForHash(info.Background) is { Length: > 0 } path)
			{
				said["texture_path"] = "game://" + path;
			}
		}

		float extent = LevelExtent(level);
		if (!info.Proper || extent <= 0f)
		{
			return said;
		}

		if (Engine_UiMinimapPage.Place(info, Vector2.Zero, hero, extent) is { } heroAt)
		{
			said["hero"] = heroAt;
			said["facing"] = Engine_UiPauseMinimap.PlayerTurn(info, hero);
		}

		var quests = new Godot.Collections.Array();
		Fable2Bridge.GetQuestLog(_questLog);
		foreach (Fable2Bridge.QuestEntry quest in _questLog)
		{
			if (!quest.Primary && Engine_UiMinimapPage.Place(info, Vector2.Zero, quest.ObjectiveEntity, extent) is { } at)
			{
				quests.Add(at);
			}
		}

		said["quests"] = quests;
		return said;
	}

	/// <summary>A guest world position on the current level's map; null with no proper map.</summary>
	public static Vector2? Point(Vector3 world)
	{
		if (CurrentInfo(out Refii.Host.HostBridge.CurrentLevel level) is not { Proper: true } info)
		{
			return null;
		}

		float extent = LevelExtent(level);
		return extent > 0f ? Engine_UiMinimapPage.Project(info, Vector2.Zero, world, extent) : null;
	}
}
