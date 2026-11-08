using System;

namespace Refii.Launcher.Core;

/// <summary>
/// The pressings of the active title (<see cref="Refii.Core.Engine_Title.Active"/>) this build
/// recompiles, and the title update each may be installed with. The update is the player's
/// choice: the patched and the disc executables each have their own config under versions/.
/// </summary>
/// <remarks>
/// A pressing is accepted only when its code is byte-identical to the one the generated code,
/// the hooks and every guest address were taken from. The Russian GOTY disc is the USA/Europe
/// disc re-signed: same PE payload, same file key, same section digest; only the signature,
/// media id, version stamp, ratings and LAN key differ, so it runs on the disc config. Its
/// title update (0.0.0.33 to 0.0.1.33) carries the same 48 image-delta blocks as the USA/Europe
/// one, so a patched Russian image is the TU1 program and runs on the TU1 config. The French
/// GOTY disc is the same re-signing again (318 header bytes differ, the payload is identical);
/// its title update has not been found, so it installs without one and runs on the disc config.
/// The Spanish GOTY disc is the same re-signing (319 header bytes differ, the payload is
/// identical); its title update (0.0.0.29 to 0.0.1.29) carries the same 48 image-delta blocks
/// and the same tu1_data.bnk as the USA/Europe one, so it runs on the TU1 config when patched.
///
/// All of it is read from the XEX headers: title id and version from the execution info,
/// the 16-byte media id from the security block, the versions a patch joins from its delta
/// descriptor. Nothing is hashed and no file name is trusted.
/// </remarks>
public static class SupportedGame
{
	private static Refii.Core.Engine_Title Title => Refii.Core.Engine_Title.Active;

	private static bool IsFable3 => Title == Refii.Core.Engine_Title.Fable3;

	public static string Name => IsFable3 ? "Fable 3" : "Fable 2 GOTY";

	public static uint TitleId => Title.TitleId;

	/// <summary>One disc pressing: its media id, its executable version and its title update.</summary>
	public sealed class Pressing
	{
		public string Name = string.Empty;

		/// <summary>The XGD2 media id; its last dword is the short id.</summary>
		public byte[] MediaId = Array.Empty<byte>();

		/// <summary>The disc executable's version, which is also what the patch applies to.</summary>
		public uint BaseVersion;

		/// <summary>The title update's package name as distributed, or empty when none is known.</summary>
		public string TitleUpdateName = string.Empty;

		/// <summary>The version the title update produces; meaningless without one.</summary>
		public uint UpdatedVersion;

		public bool HasTitleUpdate => TitleUpdateName.Length > 0;

		public string ShortMediaIdText =>
			(((uint)MediaId[12] << 24) | ((uint)MediaId[13] << 16) | ((uint)MediaId[14] << 8) | MediaId[15])
			.ToString("X8");

		public bool Matches(ReadOnlySpan<byte> mediaId) => mediaId.SequenceEqual(MediaId);

		/// <summary>The dropdown entry: "USA / Europe (716F0A0D)".</summary>
		public string DisplayName => Name + " (" + ShortMediaIdText + ")";

		/// <summary>The line under the dropdown: the identity the disc and the update must carry.</summary>
		public string Summary =>
			"Media ID " + ShortMediaIdText + ", version " + XexIdentity.FormatVersion(BaseVersion) +
			(HasTitleUpdate
				? ", title update " + TitleUpdateName
				: ", no title update");
	}

	public static readonly Pressing UsaEurope = new()
	{
		Name = "USA / Europe",
		MediaId = new byte[]
		{
			0xA5, 0x09, 0xA2, 0xD6, 0xD9, 0xE9, 0x98, 0x32,
			0x7C, 0x39, 0x80, 0x33, 0x71, 0x6F, 0x0A, 0x0D,
		},
		BaseVersion = 0x0000001A,
		TitleUpdateName = "TU_16L61VH_0000038000000.000000000008Q",
		UpdatedVersion = 0x0000011A,
	};

	public static readonly Pressing Russia = new()
	{
		Name = "Russia",
		MediaId = new byte[]
		{
			0x84, 0x5B, 0xD5, 0xA1, 0xC7, 0xEE, 0xC1, 0xDF,
			0xC1, 0xA5, 0xA4, 0x05, 0x05, 0xFB, 0xEC, 0xEA,
		},
		BaseVersion = 0x00000021,
		TitleUpdateName = "TU_16L61VH_0000044000000.0000000000091",
		UpdatedVersion = 0x00000121,
	};

	public static readonly Pressing France = new()
	{
		Name = "France",
		MediaId = new byte[]
		{
			0x62, 0xDA, 0x71, 0x80, 0x13, 0xD0, 0x5A, 0x4C,
			0x9F, 0x4E, 0xD3, 0x92, 0x13, 0x58, 0xB1, 0xA4,
		},
		BaseVersion = 0x0000001B,
	};

	public static readonly Pressing Spain = new()
	{
		Name = "Spain",
		MediaId = new byte[]
		{
			0xA8, 0x54, 0x3A, 0x62, 0x98, 0x8E, 0x27, 0xB1,
			0x1C, 0x26, 0xFF, 0x12, 0x77, 0x2E, 0x8E, 0xE8,
		},
		BaseVersion = 0x0000001D,
		TitleUpdateName = "TU_16L61VH_000003K000000.000000000008T",
		UpdatedVersion = 0x0000011D,
	};

	/// <summary>
	/// Fable 3's USA / Europe disc (En, Zh, Ko, Pl, Ru, Cs, Hu, Sk), read from its own security
	/// block and execution info. Its title update 3 (0.0.0.3 to 0.0.3.3) is cumulative; the
	/// patched executable's config is Fable3/versions/Fable3TU3.
	/// </summary>
	public static readonly Pressing Fable3UsaEurope = new()
	{
		Name = "USA / Europe",
		MediaId = new byte[]
		{
			0xC3, 0xB3, 0x3A, 0xDB, 0x91, 0x1E, 0x3E, 0xF2,
			0xEC, 0xF4, 0x45, 0xBA, 0xD9, 0x9E, 0x18, 0xE7,
		},
		BaseVersion = 0x00000003,
		TitleUpdateName = "TU_16L626M_000000C000000.00000000000O3",
		UpdatedVersion = 0x00000303,
	};

	private static readonly Pressing[] Fable2Pressings = { UsaEurope, Russia, France, Spain };

	private static readonly Pressing[] Fable3Pressings = { Fable3UsaEurope };

	public static Pressing[] Pressings => IsFable3 ? Fable3Pressings : Fable2Pressings;

	public static string TitleIdText => TitleId.ToString("X8");

	/// <summary>Shown wherever the restriction is stated, so it does not read as permanent.</summary>
	public const string EasingNote =
		"This restriction will be eased as we eventually support more versions closer to release.";

	/// <summary>The supported pressings as "716F0A0D (USA / Europe), 05FBECEA (Russia)".</summary>
	public static string PressingsText
	{
		get
		{
			string[] parts = new string[Pressings.Length];
			for (int i = 0; i < Pressings.Length; i++)
			{
				parts[i] = Pressings[i].ShortMediaIdText + " (" + Pressings[i].Name + ")";
			}

			return string.Join(", ", parts);
		}
	}

	/// <summary>The title updates that may be installed, as "TU_... for 716F0A0D".</summary>
	public static string TitleUpdatesText
	{
		get
		{
			string text = string.Empty;
			foreach (Pressing pressing in Pressings)
			{
				if (!pressing.HasTitleUpdate)
				{
					continue;
				}

				text += (text.Length > 0 ? ", " : string.Empty) +
						pressing.TitleUpdateName + " for " + pressing.ShortMediaIdText;
			}

			return text;
		}
	}

	/// <summary>What the player has to bring, for messages.</summary>
	public static string Requirement =>
		Name + " (title ID " + TitleIdText + ", media ID " + PressingsText + ")";

	public static Pressing? FindPressing(ReadOnlySpan<byte> mediaId)
	{
		foreach (Pressing pressing in Pressings)
		{
			if (pressing.Matches(mediaId))
			{
				return pressing;
			}
		}

		return null;
	}

	/// <summary>Whether a base executable is one of the supported pressings.</summary>
	public static bool IsExecutable(XexIdentity executable, out string error)
	{
		error = string.Empty;

		if (executable.TitleId != TitleId)
		{
			error = "This disc image is not " + Title.Name + ": its title ID is " +
					executable.TitleId.ToString("X8") + ", and this build needs " + Requirement + ".";
			return false;
		}

		Pressing? pressing = FindPressing(executable.MediaId);
		if (pressing == null)
		{
			error = "This is a different pressing of " + Title.Name + ": its media ID is " +
					executable.ShortMediaIdText + ", and this build only supports media ID " +
					PressingsText + ".";
			return false;
		}

		if (executable.Version != pressing.BaseVersion)
		{
			error = "This disc image's executable is version " + executable.VersionText +
					", and this build only supports version " +
					XexIdentity.FormatVersion(pressing.BaseVersion) + " for media ID " +
					pressing.ShortMediaIdText + ".";
			return false;
		}

		return true;
	}

	/// <summary>Whether a base executable is the pressing the player selected.</summary>
	/// <remarks>
	/// The whole list is still consulted so a disc of another supported pressing is named as
	/// such: the fix is a different dropdown choice, not a different disc.
	/// </remarks>
	public static bool IsExecutable(XexIdentity executable, Pressing selected, out string error)
	{
		if (!IsExecutable(executable, out error))
		{
			return false;
		}

		if (!selected.Matches(executable.MediaId))
		{
			Pressing actual = FindPressing(executable.MediaId)!;
			error = "This disc image is the " + actual.Name + " pressing (media ID " +
					actual.ShortMediaIdText + "), not " + selected.DisplayName +
					". Select " + actual.DisplayName + " as the game version.";
			return false;
		}

		return true;
	}

	/// <summary>Whether a patch is the title update of the pressing the player selected.</summary>
	public static bool IsTitleUpdate(XexIdentity patch, Pressing selected, out string error)
	{
		if (!IsTitleUpdate(patch, out error))
		{
			return false;
		}

		if (!selected.Matches(patch.MediaId))
		{
			Pressing actual = FindPressing(patch.MediaId)!;
			error = "This title update is for the " + actual.Name + " pressing (media ID " +
					actual.ShortMediaIdText + "), not " + selected.DisplayName +
					". The one to fetch is " + selected.TitleUpdateName + ".";
			return false;
		}

		return true;
	}

	/// <summary>Whether a patch is the supported title update for its pressing.</summary>
	public static bool IsTitleUpdate(XexIdentity patch, out string error)
	{
		error = string.Empty;

		Pressing? pressing = FindPressing(patch.MediaId);
		if (pressing == null)
		{
			error = "This title update is for a different pressing of " + Title.Name + ": it names media ID " +
					patch.ShortMediaIdText + ", and this build only supports media ID " +
					PressingsText + ". The ones to fetch are " + TitleUpdatesText + ".";
			return false;
		}

		if (!pressing.HasTitleUpdate)
		{
			error = "No title update is supported for the " + pressing.Name + " pressing (media ID " +
					pressing.ShortMediaIdText + ") yet; install it without one.";
			return false;
		}

		if (patch.SourceVersion != pressing.BaseVersion || patch.Version != pressing.UpdatedVersion)
		{
			error = "This is not the supported title update: it patches version " +
					patch.SourceVersionText + " to " + patch.VersionText +
					", and this build needs " + XexIdentity.FormatVersion(pressing.BaseVersion) + " to " +
					XexIdentity.FormatVersion(pressing.UpdatedVersion) + ". The one to fetch is " +
					pressing.TitleUpdateName + ".";
			return false;
		}

		return true;
	}
}
