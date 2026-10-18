#if TOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using Refii.Mods;

namespace Refii.EditorTools;

/// <summary>
/// The live half of the modding API: the RUNNING game, through its debug channel
/// (<see cref="LiveGameChannel"/>), so Lua written in the editor runs without a relaunch.
/// </summary>
public partial class RefiiModApi
{
	/// <summary>A game is up and publishing its debug channel.</summary>
	public bool IsGameRunning() => LiveGameChannel.IsGameRunning();

	/// <summary>
	/// Runs <paramref name="chunk"/> in the running game's VM — the shipped scripts' and the
	/// mods' own Lua state — and waits for the outcome. Returns <c>"ok"</c> when the chunk ran
	/// without error; otherwise the reason (no game, a Lua error or a failed assertion).
	/// </summary>
	public string RunInGame(string chunk)
	{
		(bool ok, string note) = LiveGameChannel.RunGameLua(chunk, LiveGameChannel.DefaultRunTimeoutSeconds);
		return ok ? "ok" : note;
	}

	/// <summary>
	/// Runs the active mod's <c>mod/init.lua</c> in the running game exactly as the session
	/// start does — under <see cref="ModLua.Wrap"/>, so a top-level error lands in
	/// <c>RefiiModErrors</c> rather than aborting, and the live behaviour equals the start-up
	/// behaviour. Returns <c>"ok"</c> or the reason.
	/// </summary>
	public string RunModLuaInGame()
	{
		string mod = GetActiveMod();
		if (string.IsNullOrEmpty(mod))
		{
			return NoActiveMod;
		}

		string source = ReadText(ModLua.ModInitPath);
		if (source.Length == 0)
		{
			return $"'{mod}' has no {ModLua.ModInitPath}";
		}

		return RunInGame(ModLua.Wrap(mod, source));
	}

	/// <summary>
	/// Folds the installed mods into the running game again, so a change saved to a package
	/// shows without a relaunch; with <paramref name="lua"/> the mods' init Lua runs again too,
	/// its earlier handlers dropped first. Blocks until the game answers (seconds: the mount walks
	/// every bank). The first line is <c>ok</c>, followed by one line per thing refreshed, per kind
	/// that needs a relaunch, and the Lua's outcome; otherwise the reason.
	/// </summary>
	public string RemountInGame(bool lua)
	{
		LiveGameChannel.RemountResult result = LiveGameChannel.Remount(lua);
		if (!result.Ok)
		{
			return result.Error;
		}

		var lines = new List<string> { "ok" };
		if (result.Warning.Length != 0)
		{
			lines.Add("warning: " + result.Warning);
		}

		lines.AddRange(result.Refreshed.Select(r => "refreshed: " + r));
		lines.AddRange(result.Relaunch.Select(r => "relaunch: " + r));
		if (result.LuaRan is { } ran)
		{
			lines.Add((ran ? "lua: ran — " : "lua: failed — ") + result.LuaNote);
		}
		else if (result.LuaNote.Length != 0)
		{
			lines.Add("lua: " + result.LuaNote);
		}

		return string.Join("\n", lines);
	}

	/// <summary>
	/// The mods' Lua errors the running game has kept (init and handler errors), newest first,
	/// each <c>"HH:mm:ss mod: message"</c>. Empty when there are none or no game is running.
	/// </summary>
	public string[] ModErrorsInGame()
	{
		List<LiveGameChannel.ModError>? errors = LiveGameChannel.ModErrors(0, out _, out _);
		if (errors is null)
		{
			return Array.Empty<string>();
		}

		errors.Reverse();
		return errors.Select(e => $"{e.Time.ToLocalTime():HH:mm:ss} {e.Mod}: {e.Text}").ToArray();
	}
}
#endif
