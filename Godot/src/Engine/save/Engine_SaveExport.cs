using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Save;

/// <summary>
/// A save copied for a console or Xenia: the guest's own files only, in the content layout
/// (`4D5307F1/00000001/&lt;name&gt;/`), the main save filtered of every mod record — never the
/// original, which keeps everything.
/// </summary>
/// <remarks>
/// WHERE THE GAME'S SAVES ARE. Refii keeps each title's below the profile as Xenia keeps them below
/// its title id: `&lt;profile&gt;/Fable 2/00000001/&lt;name&gt;/` holding `mainsave.bin`, `herosave.bin`,
/// `chaptersave.bin`, `saveuid.bin`, `texturemorphs.bin`, `failquestsave.bin` and `Fable2PubInfo.xml`
/// — the files a 360 writes inside its STFS package. Refii's own files beside them
/// (`refii_save.json`, the player store's `refii_player.txt` and blobs) are never read by the game. So
/// an export, written in Xenia's layout (`4D5307F1/00000001/&lt;name&gt;/`), drops into a Xenia profile
/// and loads there; a real 360 needs the same files wrapped into a CON package by a tool such as
/// Velocity or Horizon (a console-signed container Refii does not produce).
///
/// A save made WITH mods carries mod records in the guest's own inventory data, which a vanilla
/// game does not know. The guest tolerates them (see <see cref="Refii.Mods.ModSaveStamp"/>), but
/// they are dead entries there, so the export strips every record of every mod the save was
/// stamped with: the copy is vanilla-only.
/// </remarks>
public static class SaveExport
{
	public const string ExportFolderName = Engine_SaveLayout.ExportFolderName;
	public static string TitleId => Engine_SaveLayout.TitleIdText(Refii.Core.Engine_Title.Active.TitleId);
	public const string ContentType = Engine_SaveLayout.SaveGameType;

	/// <summary>`&lt;profile&gt;/&lt;game&gt;/save-export/4D5307F1/00000001/&lt;name&gt;/`, in the title's own folder.</summary>
	public static string DestinationFor(string profileDirectory, string saveName) =>
		Path.Combine(Engine_SaveLayout.GameDirectory(profileDirectory, Refii.Core.Engine_Title.Active.TitleId),
			ExportFolderName, TitleId, ContentType, saveName);

	/// <summary>
	/// Writes the copy of <paramref name="saveDirectory"/> at <paramref name="destination"/>: the
	/// guest's files (<paramref name="guestFiles"/>, else the active title's
	/// <see cref="Refii.Core.Engine_TitleGame.SaveGuestFiles"/>), `mainsave.bin` without
	/// <paramref name="records"/>. Returns how many inventory entries went. The original is only read.
	/// </summary>
	public static int Copy(string saveDirectory, string destination, ISet<uint> records,
		IReadOnlyList<string>? guestFiles = null)
	{
		Directory.CreateDirectory(destination);
		foreach (string file in Directory.GetFiles(destination))
		{
			File.Delete(file);
		}

		int removed = 0;
		foreach (string name in guestFiles ?? Refii.Core.Engine_TitleGame.Active.SaveGuestFiles)
		{
			string source = Path.Combine(saveDirectory, name);
			if (!File.Exists(source))
			{
				continue;
			}

			string target = Path.Combine(destination, name);
			if (name == "mainsave.bin" && records.Count != 0 && Refii.Core.Engine_TitleGame.Active.FiltersSaveRecords)
			{
				SaveStream stream = SaveStream.ReadFile(source);
				SaveSidecar.HeldRecords held = Refii.Mods.ModSaveFilter.Filter(stream, records);
				removed = held.Inventory.Count + held.Worn.Count + (held.Melee != 0 ? 1 : 0) + (held.Ranged != 0 ? 1 : 0);
				stream.WriteFile(target);
				continue;
			}

			File.Copy(source, target, overwrite: true);
		}

		return removed;
	}
}
