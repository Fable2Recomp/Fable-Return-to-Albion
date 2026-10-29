using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Mods;

/// <summary>
/// Archon's Toolbox as the shell's external mod manager: its sidecar layer is
/// <see cref="ArchonLayer"/>, its state on this install <see cref="ArchonToolbox.Detect()"/>.
/// </summary>
public sealed class ArchonModManager : Engine_ExternalModManager
{
	public override string Name => ArchonToolbox.Title;

	public override string Layer => ArchonLayer.Key;

	public override string? StampNow() => ArchonToolbox.Stamp(ArchonToolbox.Detect())?.ToJson();

	public override int ModCount(string layerJson) => ArchonLayer.FromJson(layerJson)?.Mods.Count ?? 0;

	public override Diff Compare(string layerJson)
	{
		ArchonLayer? stamp = ArchonLayer.FromJson(layerJson);
		ArchonToolbox.Diff diff = ArchonToolbox.Compare(stamp, ArchonToolbox.Detect());
		string notice = stamp is null ? string.Empty : ArchonToolbox.NoticeText(stamp, diff);
		return new Diff(diff.Missing.Count, diff.Present, notice);
	}

	public override bool IsFingerprintEntry(ModFingerprint.Mod mod) => ArchonToolbox.IsFingerprintEntry(mod);

	public override IReadOnlyList<ModFingerprint.Mod> FingerprintEntries() =>
		ArchonToolbox.FingerprintEntries(ArchonToolbox.Detect());

	public override string BlockedText(ModFingerprint.Mod theirs, bool haveAnotherBuild) =>
		ArchonToolbox.BlockedText(theirs, haveAnotherBuild);

	public override string OwnBlockedText(ModFingerprint.Mod mine) => ArchonToolbox.OwnBlockedText(mine);
}
