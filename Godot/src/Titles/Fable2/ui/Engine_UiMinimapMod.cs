using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The region map as a mod reads it (<c>RefiiHost.minimap</c>): what the pause root's and the
/// world map's minimaps plot from, in one dictionary. Places are in the map image's element
/// units around the image's centre, through <see cref="Engine_UiMinimapPage.Project"/>.
/// </summary>
public static class Engine_UiMinimapMod
{
	/// <summary>The map image's half-size in element units: the art's rim, read off the pause root.</summary>
	public const float HalfSize = 157.5f;

	/// <summary>Resolves a resource-path hash to a texture; the shell states it with the HUD's.</summary>
	public static System.Func<uint, Texture2D?>? ResolveTexture { get; set; }

	private static string _levelFor = string.Empty;

	private static string _scenarioFor = string.Empty;

	/// <summary>The log <see cref="State"/> walks, kept: a mod's map asks every frame and holds none of it.</summary>
	private static readonly System.Collections.Generic.List<Fable2Bridge.QuestEntry> _questLog = new();

	/// <summary>The current level's record, or null when none stands or the GDB names none.</summary>
	private static Engine_UiMinimapPage.Info? CurrentInfo()
	{
		if (Refii.Host.HostBridge.GetCurrentLevel() is not { Level.Length: > 0 } level)
		{
			return null;
		}

		if (level.Level != _levelFor)
		{
			_levelFor = level.Level;
			_scenarioFor = Refii.Host.HostBridge.GetActiveScenario() ?? string.Empty;
		}

		return Engine_UiMinimapPage.Resolve(level.Level, _scenarioFor, "mod");
	}

	public static Godot.Collections.Dictionary State()
	{
		var said = new Godot.Collections.Dictionary { ["half_size"] = HalfSize };
		if (CurrentInfo() is not { } info)
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
		}

		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
		if (!info.Proper || live.Extent <= 0f)
		{
			return said;
		}

		if (live.Hero != 0u && Refii.Game.GameEntities.TryGuestPosition(live.Hero, out Vector3 heroAt))
		{
			said["hero"] = Engine_UiMinimapPage.Project(info, Vector2.Zero, heroAt, live.Extent);
			if (Refii.Game.GameEntities.TryGuestRotation(live.Hero, out Quaternion heroRotation))
			{
				Vector3 forward = heroRotation * Vector3.Up;  // the guest's (0, 1, 0)
				said["facing"] = Mathf.DegToRad(info.IconRotation + info.DegreesToNorth) -
					Mathf.Atan2(forward.X, forward.Y);
			}
		}

		if (live.Dog != 0u && Refii.Game.GameEntities.TryGuestPosition(live.Dog, out Vector3 dogAt))
		{
			said["dog"] = Engine_UiMinimapPage.Project(info, Vector2.Zero, dogAt, live.Extent);
		}

		if (live.HasTrailTarget)
		{
			said["quest"] = Engine_UiMinimapPage.Project(info, Vector2.Zero, live.TrailTarget, live.Extent);
			said["quest_here"] = live.PrimaryQuestHere;
		}

		var quests = new Godot.Collections.Array();
		Fable2Bridge.GetQuestLog(_questLog);
		foreach (Fable2Bridge.QuestEntry quest in _questLog)
		{
			if (!quest.Primary && quest.ObjectiveEntity != 0u &&
				Refii.Game.GameEntities.TryGuestPosition(quest.ObjectiveEntity, out Vector3 at))
			{
				quests.Add(Engine_UiMinimapPage.Project(info, Vector2.Zero, at, live.Extent));
			}
		}

		said["quests"] = quests;

		var merchants = new Godot.Collections.Array();
		foreach (uint merchant in live.Merchants ?? System.Array.Empty<uint>())
		{
			if (Refii.Game.GameEntities.TryGuestPosition(merchant, out Vector3 at))
			{
				merchants.Add(Engine_UiMinimapPage.Project(info, Vector2.Zero, at, live.Extent));
			}
		}

		said["merchants"] = merchants;
		return said;
	}

	/// <summary>A guest world position on the current level's map; null with no proper map.</summary>
	public static Vector2? Point(Vector3 world)
	{
		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
		return CurrentInfo() is { Proper: true } info && live.Extent > 0f
			? Engine_UiMinimapPage.Project(info, Vector2.Zero, world, live.Extent)
			: null;
	}
}
