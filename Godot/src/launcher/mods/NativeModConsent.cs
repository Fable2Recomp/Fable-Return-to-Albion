using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Refii.Launcher.Core;
using Refii.Launcher.Recompile;
using Refii.Mods;

namespace Refii.Launcher;

/// <summary>
/// The player's consent to a hard mod's native code, and the set a game code build may compile.
/// </summary>
/// <remarks>
/// A HARD MOD IS ARBITRARY NATIVE CODE, compiled into the game module and run with the player's
/// full rights. Enabling one asks once, and the answer is recorded against the mod's
/// <see cref="ModNativeSources.ContentHash"/> rather than its version string: an update that
/// changes any native file asks again even when the author forgot to bump the version, and one
/// that only touches data does not.
///
/// The build compiles only enabled mods whose current sources were consented to. A mod enabled by
/// editing loadorder.toml by hand keeps its data and Lua; its native code is withheld and named.
/// </remarks>
public static class NativeModConsent
{
	private const string Section = "NativeModConsent";

	public static bool IsGranted(ModNativeSources sources) =>
		string.Equals(Ini.GetString(sources.ModName, string.Empty, Section),
			sources.ContentHash, StringComparison.OrdinalIgnoreCase);

	public static void Grant(ModNativeSources sources)
	{
		Ini.SetString(sources.ModName, sources.ContentHash, Section);
		Ini.Save();
	}

	/// <summary>Refii's own hooks, by function, read from the staged sources the build compiles.</summary>
	public static Dictionary<string, string> RefiiOverrides() =>
		ModNative.ReadOverridesInDirectory(
			Path.Combine(GameCodeBuild.TitleRecompileDirectory, "hooks"), ModNative.RefiiOwner);

	/// <summary>
	/// The enabled hard mods a build compiles, highest priority first. <paramref name="withheld"/>
	/// names each enabled one left out because its current sources were never consented to.
	/// </summary>
	public static List<ModNativeSources> ForBuild(string modsRoot, out List<string> withheld)
	{
		withheld = new List<string>();
		var granted = new List<ModNativeSources>();
		foreach (ModNativeSources sources in ModNative.ReadEnabled(modsRoot))
		{
			if (IsGranted(sources))
			{
				granted.Add(sources);
			}
			else
			{
				withheld.Add(sources.ModName);
			}
		}

		return granted;
	}

	/// <summary>The text of the one-time prompt for <paramref name="sources"/>.</summary>
	public static string PromptText(ModNativeSources sources)
	{
		string functions = sources.Overrides.Count == 0
			? "no game functions"
			: sources.Overrides.Count == 1
				? "1 game function"
				: sources.Overrides.Count + " game functions";

		return "'" + sources.ModName + "' contains native code (" +
			sources.CompiledFiles.Count() + " source file(s), replacing " + functions + ").\n\n" +
			"It will be compiled into the game on this machine and runs with the same access to " +
			"your computer as the game itself. Only enable it if you trust its author.\n\n" +
			"Enabling it rebuilds the game code the next time you press Play.";
	}
}
