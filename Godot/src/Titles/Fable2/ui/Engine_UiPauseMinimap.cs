using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE PAUSE ROOT'S MINIMAP, stated by the host — what the guest's enter (`sub_826A19E0`,
/// vtable slot 2 of the pause root, 0x820CDE18) writes once when the root opens, from the
/// same inputs the guest reads. Element paths are under `Foreground.Pages.Contents.`.
///
/// <list type="bullet">
/// <item>`MiniMap.Bowerstone` — texture = the current level's `MinimapBackgroundImage`
/// (default `Art\GUI\Legacy\mm_barrowfields.tex`, 0x826a1d70), `rotation.z =
/// MinimapDegreesRotationToNorth·π/180` (0x826a21f8).</item>
/// <item>`MiniMap.Player` — `rotation.z = (IconTransformRotation + DegreesToNorth)·π/180 −
/// atan2(fwd.x, fwd.y)` (`fmsubs` at 0x826a3074; the atan2 takes lane 0 then lane 1 at
/// 0x826a303c/40), fwd the hero's orientation applied to (0, 1, 0); then its position through
/// the projection at depth −3.1 (`sub_826A2F98` → `sub_826A3F30` → `sub_826A3C00`).</item>
/// <item>`MiniMap.Dog` — position only, at depth −3.0, and only when the dog exists.</item>
/// <item>Player, Dog and `MiniMap.Legend.Fixed` are shown iff `HasProperMinimap`
/// (0x826a22f8..0x826a2358); the Dog's switch is written even with no dog.</item>
/// <item>The legend (`sub_826A3658`): `Legend.Fixed.KeyName{N}` / `KeyImage{N}` for the nine
/// rows of <see cref="Engine_UiMinimapPage.Legend"/>, the rest hidden — only with a proper
/// minimap.</item>
/// </list>
///
/// The projection is <see cref="Engine_UiMinimapPage.Project"/>, instruction for instruction the
/// world map's `sub_829AEAB8` with the pause root's fields; the origin is `MiniMap.Bowerstone`'s
/// own position, which the guest reads back (`sub_824252E8`) and never moves.
///
/// THE QUEST LINES (0x826a1f04..0x826a2004), once per open: the hero's PRIMARY quest — the
/// first in the log with `quest+0x14` set, which only Lua "SetAsPrimary" writes. `CurrentQuest`
/// = its `NameTag` localised; `Legend.CurrentObjective` = its last objective line NOT completed
/// (`sub_82522E48`: entries `{tag, completed}` at `quest+104`), or the record's
/// `FirstObjectiveTag` when it has no lines (`sub_82522DC0`), or nothing when all are done —
/// localised with `%1` replaced by `CurrentObjectiveParameter` (`sub_8230E268`, quest+40), then
/// `L"\n "` appended. No primary quest hides both. `Legend.Fixed` then hangs under the
/// objective's laid-out text — the template's measured reflow (FlowWhenDrawn).
/// </summary>
public static class Engine_UiPauseMinimap
{
	private const string MapImage = "MiniMap.Bowerstone";

	/// <summary>The image's full path, for its authored position.</summary>
	public const string MapImagePath = "Foreground.Pages.Contents.MiniMap.Bowerstone";

	private const string Player = "MiniMap.Player";

	private const string Dog = "MiniMap.Dog";

	private const string Fixed = "MiniMap.Legend.Fixed";

	private const string KeyName = "MiniMap.Legend.Fixed.KeyName";

	private const string KeyImage = "MiniMap.Legend.Fixed.KeyImage";

	private const string CurrentQuest = "Contents.CurrentQuest";

	private const string CurrentObjective = "MiniMap.Legend.CurrentObjective";

	/// <summary>`unk_820CD4BC`, appended to the objective.</summary>
	private const string ObjectiveTail = "\n ";

	private const string Parameter = "%1";

	/// <summary>The quest lines as the enter wrote them; null until the next open.</summary>
	private static (string Title, string Objective)? _questLines;

	/// <summary>The pause root closed — its next open re-reads the quest, as the guest's enter does.</summary>
	public static void Reset() => _questLines = null;

	/// <summary>Rows the pause legend authors; the guest hides any it does not fill.</summary>
	private const int LegendSlots = 9;

	/// <summary>`flt_82101780` and `flt_821015E4`.</summary>
	private const float PlayerDepth = -3.1f;

	private const float DogDepth = -3.0f;

	private static readonly uint DefaultBackground =
		Refii.Core.Engine_GameHash.ResourcePath(@"Art\GUI\Legacy\mm_barrowfields.tex");

	/// <summary>One frame's statements for the pause book.</summary>
	public sealed class Statements
	{
		public readonly Dictionary<string, string> Values = new();
		public readonly Dictionary<string, uint> Textures = new();
		public readonly Dictionary<string, Vector3> Positions = new();
		public readonly Dictionary<string, float> Rotations = new();
		public readonly Dictionary<string, bool> Active = new();
	}

	private static string _levelFor = string.Empty;

	private static string _scenarioFor = string.Empty;

	/// <summary>
	/// The statements for the current level, or empty ones when no level stands. Called once a
	/// frame while the pause root is up.
	/// </summary>
	public static Statements State(Vector3? mapImageAt, Refii.Text.Engine_BabelDatabase? strings)
	{
		var said = new Statements();
		if (Refii.Host.HostBridge.GetCurrentLevel() is not { Level.Length: > 0 } level)
		{
			return said;
		}

		// The scenario changes only with a level reload; read it with the level.
		if (level.Level != _levelFor)
		{
			_levelFor = level.Level;
			_scenarioFor = Refii.Host.HostBridge.GetActiveScenario() ?? string.Empty;
		}

		_questLines ??= QuestLines(strings);
		bool quest = _questLines.Value.Title.Length != 0 || _questLines.Value.Objective.Length != 0;
		said.Active[CurrentQuest] = quest;
		said.Active[CurrentObjective] = quest;
		if (quest)
		{
			said.Values[CurrentQuest] = _questLines.Value.Title;
			said.Values[CurrentObjective] = _questLines.Value.Objective;
		}

		Engine_UiMinimapPage.Info? info = Engine_UiMinimapPage.Resolve(level.Level, _scenarioFor, "pause");
		said.Textures[MapImage] = info is { Background: not 0u } ? info.Background : DefaultBackground;
		said.Rotations[MapImage] = Mathf.DegToRad(info?.DegreesToNorth ?? 0f);

		bool proper = info is { Proper: true };
		said.Active[Player] = proper;
		said.Active[Dog] = proper;
		said.Active[Fixed] = proper;

		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();
		IReadOnlyList<(string Tag, int Icon)> legend = info is { Proper: true }
			? Engine_UiMinimapPage.Legend(info, heroHere: true, live.PrimaryQuestHere)
			: System.Array.Empty<(string, int)>();
		for (int i = 0; i < LegendSlots; ++i)
		{
			bool shown = i < legend.Count;
			said.Active[KeyName + (i + 1)] = shown;
			said.Active[KeyImage + (i + 1)] = shown;
			if (shown)
			{
				said.Values[KeyName + (i + 1)] =
					strings is not null && strings.TryGetString(legend[i].Tag, out string text) && text.Length != 0
						? text
						: legend[i].Tag;
				said.Textures[KeyImage + (i + 1)] = Engine_UiMinimapPage.IconHash(legend[i].Icon);
			}
		}

		// The guest divides by the extent unguarded; with none it places nothing sensible.
		if (info is null || mapImageAt is not { } mapAt || live.Extent <= 0f)
		{
			return said;
		}

		// THE MENU OWNER'S HERO, not player one's: the pause root resolves the local player
		// (`sub_8229EE78(sub_821C7CE0(…))`), so player two's pause plots player two — the bridge's
		// `live.Hero` is that same resolution.
		var origin = new Vector2(mapAt.X, mapAt.Y);
		if (live.Hero != 0u && Refii.Game.GameEntities.TryGuestPosition(live.Hero, out Vector3 heroAt))
		{
			Vector2 at = Engine_UiMinimapPage.Project(info, origin, heroAt, live.Extent);
			said.Positions[Player] = new Vector3(at.X, at.Y, PlayerDepth);

			float facing = 0f;
			if (Refii.Game.GameEntities.TryGuestRotation(live.Hero, out Quaternion heroRotation))
			{
				Vector3 forward = heroRotation * Vector3.Up;  // the guest's (0, 1, 0)
				facing = Mathf.Atan2(forward.X, forward.Y);
			}

			said.Rotations[Player] = Mathf.DegToRad(info.IconRotation + info.DegreesToNorth) - facing;
		}

		if (live.Dog != 0u && Refii.Game.GameEntities.TryGuestPosition(live.Dog, out Vector3 dogAt))
		{
			Vector2 at = Engine_UiMinimapPage.Project(info, origin, dogAt, live.Extent);
			said.Positions[Dog] = new Vector3(at.X, at.Y, DogDepth);
		}

		return said;
	}

	private static (string Title, string Objective) QuestLines(Refii.Text.Engine_BabelDatabase? strings)
	{
		Fable2Bridge.QuestEntry? primary = null;
		foreach (Fable2Bridge.QuestEntry entry in Fable2Bridge.GetQuestLog())
		{
			if (entry.Primary)
			{
				primary = entry;
				break;
			}
		}

		if (primary is not { RecordId: not 0u } quest)
		{
			return (string.Empty, string.Empty);
		}

		List<string> tasks = Fable2Bridge.GetQuestTasks(quest.RecordId);
		string tag;
		if (tasks.Count == 0)
		{
			tag = Engine_UiQuestSource.FirstObjectiveTagOf(quest.RecordId);
		}
		else
		{
			bool[] done = Fable2Bridge.GetQuestTaskStates(quest.RecordId);
			tag = string.Empty;
			for (int i = tasks.Count - 1; i >= 0; --i)
			{
				if (i >= done.Length || !done[i])
				{
					tag = tasks[i];
					break;
				}
			}
		}

		string objective = Localise(strings, tag);
		if (objective.Contains(Parameter, System.StringComparison.Ordinal))
		{
			objective = objective.Replace(Parameter,
				quest.ObjectiveParameter.ToString(System.Globalization.CultureInfo.InvariantCulture),
				System.StringComparison.Ordinal);
		}

		return (Engine_UiQuestSource.TitleOf(quest.RecordId), objective + ObjectiveTail);
	}

	/// <summary>`sub_8230E0E8`: the tag, then `TEXT_` + tag, then the tag itself.</summary>
	private static string Localise(Refii.Text.Engine_BabelDatabase? strings, string tag)
	{
		if (tag.Length == 0)
		{
			return string.Empty;
		}

		if (strings is not null)
		{
			if (strings.TryGetString(tag, out string text) && text.Length != 0)
			{
				return text;
			}

			if (strings.TryGetString("TEXT_" + tag, out text) && text.Length != 0)
			{
				return text;
			}
		}

		return tag;
	}
}
