using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Refii.Core;

/// <summary>
/// Where the UI tools look for the game data.
///
/// The engine's own answer comes first and is the one that matters — the addon resolves
/// <c>&lt;project&gt;/build/assets/game</c>, staged there by <c>REFII_STAGE_GODOT_BUILD</c>,
/// falling back to <c>&lt;repo&gt;/assets/game</c>. This mirrors that, then adds the
/// locations a DEV tree actually has, because in a source checkout neither of the
/// engine's two locations is populated: nothing has been staged into
/// <c>Godot/build</c>, and <c>assets/game</c> carries no banks. The data lives under a
/// build output.
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
		foreach (string candidate in Candidates())
			if (accepts(candidate))
				return candidate;

		return string.Empty;
	}

	private static IEnumerable<string> Candidates()
	{
		string? fromEnv = System.Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (!string.IsNullOrEmpty(fromEnv))
			yield return fromEnv!;

		string projectDir = ProjectSettings.GlobalizePath("res://");
		string repoRoot = Path.GetFullPath(Path.Combine(projectDir, ".."));

		// The engine's own two, in its order.
		yield return Path.Combine(projectDir, "build", "assets", "game");
		yield return Path.Combine(repoRoot, "assets", "game");

		// Then a dev tree's build outputs. No config is hardcoded — they vary by
		// platform and by debug/netplay — so every one is tried, newest irrelevant
		// because only one is ever populated.
		string builds = Path.Combine(repoRoot, "out", "build");
		if (!Directory.Exists(builds))
			yield break;

		foreach (string config in Directory.EnumerateDirectories(builds))
			yield return Path.Combine(config, "bin", "assets", "game");
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
