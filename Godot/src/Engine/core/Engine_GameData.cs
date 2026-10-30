using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.Core;

/// <summary>
/// Where the UI tools look for the game data.
///
/// The shared resolution, used by the bnk_mount addon and the UI tools alike: the
/// per-arch app root first, then the legacy flat <c>&lt;project&gt;/build/assets/game</c>
/// and <c>&lt;repo&gt;/assets/game</c>, then the locations a DEV tree actually has,
/// because in a source checkout none of the shipped locations is populated. The data
/// lives under a build output.
///
/// The test is "carries banks", not "exists": an empty <c>assets/game</c> would
/// otherwise win over a populated staged copy and mount nothing.
/// </summary>
public static class Engine_GameData
{
	/// <summary>Explicit override, for a checkout with the data somewhere else entirely.</summary>
	public const string EnvironmentVariable = "REFII_GAME_DATA";

	/// <summary>
	/// The directory to mount, or empty when nothing populated was found. The caller
	/// should report the miss rather than mounting a guess.
	/// </summary>
	public static string Resolve() => ResolveWhere(HasBanks);

	/// <summary>
	/// A title's data directory, active or not: the same search by that title's folder, without the
	/// override (which names the active title's data). Empty when that title is not installed.
	/// </summary>
	public static string ResolveFor(Engine_Title title)
	{
		foreach (string candidate in Candidates(title, includeOverride: false))
			if (HasBanks(candidate))
				return candidate;

		return string.Empty;
	}

	/// <summary>
	/// The title update's data root beside a game data directory: <c>&lt;assets&gt;/update</c>,
	/// the directory the runtime mounts as <c>update:</c> (refii_app.h derives it the same way).
	/// The installer writes the update package's <c>data/tu1_data.bnk</c> under it.
	/// </summary>
	public static string UpdateRootFor(string gameDirectory) => SiblingFor(gameDirectory, "update");

	/// <summary>
	/// The unpacked workspace beside a game data directory: <c>&lt;assets&gt;/unpacked</c>, where
	/// the launcher's install steps write what they derive from the player's data (the translated
	/// shaders under <c>shaders/</c>). The launcher resolves the same path in
	/// <c>Installer.GetLuaWorkspacePath</c>.
	/// </summary>
	public static string UnpackedRootFor(string gameDirectory) => SiblingFor(gameDirectory, "unpacked");

	private static string SiblingFor(string gameDirectory, string name)
	{
		if (string.IsNullOrEmpty(gameDirectory))
			return string.Empty;

		string basePath = Path.TrimEndingDirectorySeparator(gameDirectory);
		if (string.Equals(Path.GetFileName(basePath), "data", StringComparison.OrdinalIgnoreCase))
			basePath = Path.GetDirectoryName(basePath) ?? basePath;
		if (string.Equals(Path.GetFileName(basePath), "game", StringComparison.OrdinalIgnoreCase))
			basePath = Path.GetDirectoryName(basePath) ?? basePath;

		return Path.Combine(basePath, name);
	}

	/// <summary>
	/// The same search, judged by a different test.
	/// </summary>
	/// <remarks>
	/// EXISTING IS NOT THE TEST, and that is the whole point of the parameter. A caller looking
	/// for something that is not a bank — the pub games' loose <c>.swf</c> and <c>.dds</c>, which
	/// live under <c>data/pubgames</c> — cannot use <see cref="Resolve"/>, because in this tree the
	/// banks and the pub games are in DIFFERENT build outputs. It also cannot just take the first
	/// directory that exists: <c>&lt;repo&gt;/assets/game</c> exists and holds only shaders, so a
	/// plain existence check picks it and reports the pub games missing.
	/// </remarks>
	public static string ResolveWhere(Func<string, bool> accepts)
	{
		foreach (string candidate in Candidates(Engine_Title.Active, includeOverride: true))
			if (accepts(candidate))
				return candidate;

		return string.Empty;
	}

	private static IEnumerable<string> Candidates(Engine_Title title, bool includeOverride)
	{
		string? fromEnv = includeOverride ? System.Environment.GetEnvironmentVariable(EnvironmentVariable) : null;
		if (!string.IsNullOrEmpty(fromEnv))
			yield return fromEnv!;

		// THE ACTIVE TITLE'S data, in every location: assets/game for Fable 2, assets/refiii/game
		// for Fable 3. A candidate that ignored the title would hand one game the other's banks.
		string game = title.GameDataRelative;

		// The app root's assets first: that is where the installer writes and where an exported
		// build keeps the player's data. Everything below is a dev-tree fallback.
		yield return Path.Combine(Engine_AppRoot.AssetsDirectory, game);

		string projectDir = Engine_AppRoot.SafeGetProjectDir();
		string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));

		// The engine's own two, in its order.
		yield return Path.Combine(projectDir, "build", "assets", game);
		yield return Path.Combine(repoRoot, "assets", game);

		// Then a dev tree's build outputs. No config is hardcoded — they vary by
		// platform and by debug/netplay — so every one is tried, newest irrelevant
		// because only one is ever populated.
		string builds = Path.Combine(repoRoot, "out", "build");
		if (!Directory.Exists(builds))
			yield break;

		foreach (string config in Directory.EnumerateDirectories(builds))
			yield return Path.Combine(config, "bin", "assets", game);
	}

	/// <summary>
	/// True when a directory tree carries at least one <c>.bnk</c>.
	///
	/// Bounded rather than recursive over everything: an unpacked asset tree is large
	/// enough that walking all of it before concluding "no banks" stalls startup for
	/// tens of seconds, and the banks always sit within a couple of levels.
	/// </summary>
	private static bool HasBanks(string root)
	{
		if (!Directory.Exists(root))
			return false;

		try
		{
			foreach (string _ in Directory.EnumerateFiles(root, "*.bnk", SearchOption.TopDirectoryOnly))
				return true;

			foreach (string child in Directory.EnumerateDirectories(root))
				foreach (string _ in Directory.EnumerateFiles(child, "*.bnk", SearchOption.TopDirectoryOnly))
					return true;
		}
		catch (IOException)
		{
			return false;
		}
		catch (System.UnauthorizedAccessException)
		{
			return false;
		}

		return false;
	}
}
