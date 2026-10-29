using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>
/// Which game the app is installing, building and running, and the values that differ by it:
/// the title's folder, which <c>versions/</c> folder the generator reads, what its compiled
/// module is called, and whether hard mods can build for it.
/// </summary>
/// <remarks>
/// ONE VALUE, READ BY EVERYONE. The launcher sets <see cref="Active"/> from its switch and hands it
/// to the game process in <see cref="EnvironmentVariable"/>, which this type reads at start, so the
/// launcher, the game-code build and the running game cannot disagree about the title.
///
/// EVERY TITLE IS ITS OWN FOLDER, the same shape for each: <c>assets/&lt;Folder&gt;/</c> holds the
/// install and everything derived from it (<c>game</c>, <c>update</c>, <c>unpacked</c>,
/// <c>generated</c>, <c>mods</c>, <c>save</c>, <c>metadata</c>), and <c>bin/&lt;Folder&gt;/</c> holds
/// the title's ReXGlue instance — its host bridge, vfs, Lua runtime and compiled module — beside
/// the shared natives in <c>bin/</c>. The native runtime derives the leaves from the game data root
/// the host states, so it needs no title knowledge of its own.
///
/// THE KEY IS THE PROJECT'S NAME FOR THE TITLE: re + Fable II is refii, re + Fable III is refiii.
/// </remarks>
public sealed class Engine_Title
{
	/// <summary>Set by the launcher on the game process: the <see cref="Key"/> of the title to run.</summary>
	public const string EnvironmentVariable = "REFII_TITLE";

	/// <summary>
	/// Set by the launcher on the game process: the compiled module's base name, which the native
	/// payload loader opens beside the title's host bridge instead of <c>refii_generated</c>.
	/// </summary>
	public const string PayloadEnvironmentVariable = "REFII_PAYLOAD_MODULE";

	/// <summary>
	/// The libraries that can make up a title's ReXGlue instance. Each title ships its own in
	/// <c>bin/&lt;Folder&gt;/</c>, so the resolver looks for these there and nowhere else; which of
	/// them a title has is its <see cref="Instance"/>.
	/// </summary>
	public static IReadOnlySet<string> InstanceLibraries { get; } =
		new HashSet<string>(StringComparer.Ordinal) { "refii_hostbridge", "refii_vfs", "refii_lua" };

	private Engine_Title(
		string key, string name, string remakeName, uint titleId, string folder, string moduleName,
		string discVersion, string updatedVersion, bool hasHardMods, params string[] instance)
	{
		Instance = new HashSet<string>(instance, StringComparer.Ordinal);
		Key = key;
		Name = name;
		RemakeName = remakeName;
		TitleId = titleId;
		Folder = folder;
		ModuleName = moduleName;
		_discVersion = discVersion;
		_updatedVersion = updatedVersion;
		HasHardMods = hasHardMods;
	}

	private readonly string _discVersion;
	private readonly string _updatedVersion;

	/// <summary>The stable id the launcher stores and the game process is given.</summary>
	public string Key { get; }

	/// <summary>The game: "Fable 2", for every message about the player's disc and install.</summary>
	public string Name { get; }

	/// <summary>What we call our version of it: "Fable 2 Return to Albion".</summary>
	public string RemakeName { get; }

	/// <summary>The title id the executable's execution info carries.</summary>
	public uint TitleId { get; }

	/// <summary>The title's folder under <c>assets/</c> and <c>bin/</c>, and in the source tree.</summary>
	public string Folder { get; }

	/// <summary>The compiled module's base name, before the platform's prefix and extension.</summary>
	public string ModuleName { get; }

	/// <summary>
	/// Whether hard mods can build for this title: native mod sources are written against Fable 2's
	/// hook seams, so a title without them takes none. Every title compiles its own hooks
	/// (<c>bin/recompile/&lt;Folder&gt;/hook_sources.cmake</c>) whether or not it takes hard mods.
	/// </summary>
	public bool HasHardMods { get; }

	/// <summary>
	/// The instance libraries this title ships in <c>bin/&lt;Folder&gt;/</c>. Every title has its
	/// host bridge; the vfs and Lua runtime are Fable 2's hooks and the code they call.
	/// </summary>
	public IReadOnlySet<string> Instance { get; }

	/// <summary>Whether a title update can be installed, i.e. there is a config for the patched executable.</summary>
	public bool SupportsTitleUpdate => _updatedVersion.Length != 0;

	/// <summary>The <c>versions/</c> folder describing the executable the generator reads.</summary>
	public string VersionFolder(bool titleUpdateInstalled) =>
		titleUpdateInstalled && SupportsTitleUpdate ? _updatedVersion : _discVersion;

	/// <summary><c>assets/&lt;Folder&gt;</c>: the title's install and everything derived from it.</summary>
	public string AssetsDirectory => System.IO.Path.Combine(Engine_AppRoot.AssetsDirectory, Folder);

	/// <summary><c>bin/&lt;Folder&gt;</c>: the title's ReXGlue instance.</summary>
	public string BinDirectory => System.IO.Path.Combine(Engine_AppRoot.BinDirectory, Folder);

	/// <summary><c>assets/&lt;Folder&gt;/save</c>: the title's saves, achievements and per-player state.</summary>
	public string SaveDirectory => System.IO.Path.Combine(AssetsDirectory, "save");

	/// <summary>The game data directory relative to an <c>assets/</c> root: <c>Fable2/game</c>.</summary>
	public string GameDataRelative => System.IO.Path.Combine(Folder, "game");

	public static readonly Engine_Title Fable2 = new(
		"refii", "Fable 2", "Fable 2 Return to Albion", 0x4D5307F1, "Fable2", "refii_generated",
		"Fable2", "Fable2TU1", hasHardMods: true, "refii_hostbridge", "refii_vfs", "refii_lua");

	/// <summary>Fable 3. Its remake has no name yet; it goes by the game's until it does.</summary>
	public static readonly Engine_Title Fable3 = new(
		"refiii", "Fable 3", "Fable 3", 0x4D5308D6, "Fable3", "refiii_generated",
		"Fable3", string.Empty, hasHardMods: false, "refii_hostbridge");

	public static IReadOnlyList<Engine_Title> All { get; } = new[] { Fable2, Fable3 };

	public static Engine_Title? Find(string? key)
	{
		foreach (Engine_Title title in All)
		{
			if (string.Equals(title.Key, key, StringComparison.OrdinalIgnoreCase))
			{
				return title;
			}
		}

		return null;
	}

	/// <summary>
	/// The title this process works on: the one its launcher named, or Fable 2. The launcher
	/// assigns it again from its own setting before anything reads it.
	/// </summary>
	public static Engine_Title Active { get; set; } =
		Find(Environment.GetEnvironmentVariable(EnvironmentVariable)) ?? Fable2;

	public override string ToString() => Name;
}
