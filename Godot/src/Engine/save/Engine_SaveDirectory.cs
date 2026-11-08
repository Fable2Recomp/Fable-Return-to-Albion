using System;
using System.IO;

namespace Refii.Save;

/// <summary>
/// Where the guest keeps a named save: <c>&lt;profile&gt;/&lt;game&gt;/00000001/&lt;save&gt;/</c>, the console's
/// savegame content (type 1) of the active title in the profile the guest runs from
/// (<see cref="Engine_SaveLayout.SaveGames"/>).
/// </summary>
public static class Engine_SaveDirectory
{
	/// <summary>The Lionhead main save inside a save's directory.</summary>
	public const string MainSave = "mainsave.bin";

	/// <summary>
	/// The directory of the save named <paramref name="saveName"/> in the profile at
	/// <paramref name="profileDirectory"/> (the guest's user_data_root), for the active title; null when
	/// it holds no main save. One profile and one title: never another profile's or game's save.
	/// </summary>
	public static string? Find(string profileDirectory, string saveName) =>
		Find(profileDirectory, saveName, Refii.Core.Engine_Title.Active.TitleId);

	public static string? Find(string profileDirectory, string saveName, uint titleId)
	{
		if (string.IsNullOrEmpty(profileDirectory) || string.IsNullOrEmpty(saveName) ||
			saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || saveName is "." or "..")
		{
			return null;
		}

		string candidate = Path.Combine(Engine_SaveLayout.SaveGames(profileDirectory, titleId), saveName);
		return File.Exists(Path.Combine(candidate, MainSave)) ? candidate : null;
	}
}
