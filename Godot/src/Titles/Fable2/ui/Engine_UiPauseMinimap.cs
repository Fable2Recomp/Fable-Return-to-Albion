using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE PAUSE ROOT'S MINIMAP: the hero's and the dog's tokens, and the quest markers the guest
/// names. Element paths are under `Foreground.Pages.Contents.`.
///
/// Everything else on the page is the guest's own statement, taken as told: the readouts
/// (`sub_826A4D50`, SetText every frame), the quest lines (SetTextFromTag 0x826a1f8c, SetText
/// 0x826a2004), the legend's names (SetText 0x826a3b70) and icons (the inline store at
/// 0x826a3bbc, stated by gui_pause_hooks.cpp), the map image (the inline store at 0x826a21f0,
/// stated the same way) and its turn (SetRotation 0x826a2214), and every element's show and hide.
///
/// <list type="bullet">
/// <item>`MiniMap.Player` — `rotation.z = (IconTransformRotation + DegreesToNorth)·π/180 −
/// atan2(fwd.x, fwd.y)` (`fmsubs` at 0x826a3074; the atan2 takes lane 0 then lane 1 at
/// 0x826a303c/40), fwd the hero's orientation applied to (0, 1, 0); then its position through
/// the projection at depth −3.1 (`sub_826A2F98` → `sub_826A3F30` → `sub_826A3C00`).</item>
/// <item>`MiniMap.Dog` — position only, at depth −3.0, and only when the dog exists.</item>
/// <item>The quest markers — each `ReusableTokens` entry the marker pass names to its placer
/// (`sub_826A3FD8`, HostBridge.GetMapMarkers) at the world point it names, through the same
/// projection, under `MiniMap` behind both tokens.</item>
/// </list>
///
/// The projection is <see cref="Engine_UiMinimapPage.Project"/>, instruction for instruction the
/// world map's `sub_829AEAB8` with the pause root's fields; the origin is `MiniMap.Bowerstone`'s
/// own position, which the guest reads back (`sub_824252E8`) and never moves.
/// </summary>
public static class Engine_UiPauseMinimap
{
	/// <summary>The image's full path, for its authored position.</summary>
	public const string MapImagePath = "Foreground.Pages.Contents.MiniMap.Bowerstone";

	/// <summary>The element the tokens and the markers are positioned in.</summary>
	public const string MiniMapPath = "Foreground.Pages.Contents.MiniMap";

	private const string Player = "MiniMap.Player";

	private const string Dog = "MiniMap.Dog";

	/// <summary>`flt_82101780` and `flt_821015E4`.</summary>
	private const float PlayerDepth = -3.1f;

	private const float DogDepth = -3.0f;

	/// <summary>Behind the dog and the hero, over the map image.</summary>
	public const float MarkerDepth = -2.9f;

	/// <summary>One frame's statements for the pause book.</summary>
	public sealed class Statements
	{
		public readonly Dictionary<string, Vector3> Positions = new();
		public readonly Dictionary<string, float> Rotations = new();
		public readonly Dictionary<string, bool> Active = new();

		/// <summary>The guest's markers, projected into `MiniMap`'s units.</summary>
		public readonly List<(string Token, Vector2 At)> Markers = new();
	}

	private static string _levelFor = string.Empty;

	private static string _scenarioFor = string.Empty;

	/// <summary>
	/// The statements for the current level, or empty ones when no level stands. Called once a
	/// frame while the pause root is up, with the markers the guest named this open.
	/// </summary>
	public static Statements State(Vector3? mapImageAt, IReadOnlyList<Engine_UiModel.MapMarker> markers)
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

		Engine_UiMinimapPage.Info? info = Engine_UiMinimapPage.Resolve(level.Level, _scenarioFor, "pause");
		bool proper = info is { Proper: true };
		said.Active[Player] = proper;
		said.Active[Dog] = proper;

		Fable2Bridge.MinimapLive live = Fable2Bridge.GetMinimapLive();

		// The guest divides by the extent unguarded; with none it places nothing sensible.
		if (info is null || mapImageAt is not { } mapAt || live.Extent <= 0f)
		{
			return said;
		}

		var origin = new Vector2(mapAt.X, mapAt.Y);
		foreach (Engine_UiModel.MapMarker marker in markers)
		{
			said.Markers.Add((marker.Token,
				Engine_UiMinimapPage.Project(info, origin, new Vector3(marker.X, marker.Y, marker.Z), live.Extent)));
		}

		// THE MENU OWNER'S HERO, not player one's: the pause root resolves the local player
		// (`sub_8229EE78(sub_821C7CE0(…))`), so player two's pause plots player two — the bridge's
		// `live.Hero` is that same resolution.
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
}
