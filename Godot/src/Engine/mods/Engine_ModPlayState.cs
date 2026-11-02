using System.Collections.Generic;

namespace Refii.Mods;

/// <summary>
/// Whether the game is IN PLAY as the bus last stated it, for <see cref="RefiiHost.in_play"/> — the
/// same composition as Lua's <c>Refii.in_play</c>: no GameFlow pause mode in force (the
/// <c>paused</c> event: mode 1 the pause / GUI mode, every menu entered from play; mode 2 a level
/// change), no level loading (<c>level_unloading</c> until <c>level_loaded</c>); the caller adds the
/// player's hero and the FULL cutscene. The host hears the three kinds while any script is loaded;
/// a script loaded mid-pause sees play until the next edge.
/// </summary>
public sealed class ModPlayState
{
	private readonly HashSet<int> _modes = new();

	/// <summary>Between a level going and the next standing.</summary>
	public bool Loading { get; private set; }

	/// <summary>A pause edge: <paramref name="mode"/> in force (<paramref name="paused"/>) or not.</summary>
	public void Paused(int mode, bool paused)
	{
		if (paused)
		{
			_modes.Add(mode);
		}
		else
		{
			_modes.Remove(mode);
		}
	}

	/// <summary>The level is about to go (level_unloading).</summary>
	public void Unloading() => Loading = true;

	/// <summary>A level stands (level_loaded).</summary>
	public void Loaded() => Loading = false;

	/// <summary>A pause mode is in force: the game clock (<c>now</c>) stands still, as Lua's does.</summary>
	public bool AnyMode => _modes.Count != 0;

	/// <summary>No pause mode and no load: the world runs.</summary>
	public bool WorldRuns => !Loading && _modes.Count == 0;

	/// <summary>In play for a player: the world runs, the player's hero stands, and no FULL cutscene holds for them.</summary>
	public bool InPlay(bool heroStands, bool fullCutscene) => WorldRuns && heroStands && !fullCutscene;
}
