using System;
using System.IO;

namespace Refii.Save;

/// <summary>
/// Where the guest keeps a named save: <c>&lt;user data&gt;/&lt;profile&gt;/&lt;title id&gt;/00000001/&lt;save&gt;/</c>,
/// the console's savegame content (type 1) for the active title.
/// </summary>
public static class Engine_SaveDirectory
{
	/// <summary>The Lionhead main save inside a save's directory.</summary>
	public const string MainSave = "mainsave.bin";

	/// <summary>
	/// The newest directory holding a save named <paramref name="saveName"/> across every profile
	/// under <paramref name="userDataRoot"/>, or null.
	/// </summary>
	public static string? Find(string userDataRoot, string saveName)
	{
		if (string.IsNullOrEmpty(userDataRoot) || string.IsNullOrEmpty(saveName) ||
			saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(userDataRoot))
		{
			return null;
		}

		string titleId = Refii.Core.Engine_Title.Active.TitleId.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
		string? best = null;
		DateTime bestTime = DateTime.MinValue;
		foreach (string user in Directory.EnumerateDirectories(userDataRoot))
		{
			string candidate = Path.Combine(user, titleId, "00000001", saveName);
			if (!File.Exists(Path.Combine(candidate, MainSave)))
			{
				continue;
			}

			DateTime when = File.GetLastWriteTimeUtc(Path.Combine(candidate, MainSave));
			if (when > bestTime)
			{
				bestTime = when;
				best = candidate;
			}
		}

		return best;
	}
}
