using System.Collections.Generic;

namespace Refii.Mods;

/// <summary>
/// A mod manager outside ours whose mods a title's saves can be written under — Fable 2's is
/// Archon's Toolbox. The shell keeps its state in the save's sidecar under <see cref="Layer"/>,
/// as the JSON the manager writes, and asks it about that state; it never reads the JSON itself.
/// </summary>
/// <remarks>
/// Its mods are compared and named, never filtered: their state lives in the save the way that
/// manager put it there, which is not ours to cut.
/// </remarks>
public abstract class Engine_ExternalModManager
{
	/// <summary>What the player calls it, for notices and the log.</summary>
	public abstract string Name { get; }

	/// <summary>The sidecar key its layer is kept under.</summary>
	public abstract string Layer { get; }

	/// <summary>The install now, as the layer's JSON value; null when there is nothing to state.</summary>
	public abstract string? StampNow();

	/// <summary>How many of its mods a stamped layer names.</summary>
	public abstract int ModCount(string layerJson);

	/// <summary>A stamped layer against the install now.</summary>
	public abstract Diff Compare(string layerJson);

	/// <summary>Whether a co-op fingerprint entry is one of its mods: compared, never synced.</summary>
	public abstract bool IsFingerprintEntry(ModFingerprint.Mod mod);

	/// <summary>Its mods running now, as data-only fingerprint entries for the co-op comparison.</summary>
	public abstract IReadOnlyList<ModFingerprint.Mod> FingerprintEntries();

	/// <summary>Why the host's entry cannot be synced to this machine.</summary>
	public abstract string BlockedText(ModFingerprint.Mod theirs, bool haveAnotherBuild);

	/// <summary>Why this machine's entry blocks a join.</summary>
	public abstract string OwnBlockedText(ModFingerprint.Mod mine);

	/// <summary>
	/// A stamped layer against now: how many of its mods are not running, whether the manager is
	/// applied at all, and the notice paragraph the save's box shows for it.
	/// </summary>
	public sealed record Diff(int Missing, bool Present, string Notice);
}
