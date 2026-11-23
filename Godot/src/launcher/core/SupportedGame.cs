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
/// The Italian GOTY disc is the same re-signing (318 header bytes differ, the payload is
/// identical); its title update (0.0.0.30 to 0.0.1.30) names the disc's own signature as its
/// source, carries the same first image-delta block hash as the USA/Europe one (each block holds
/// the next one's hash, so the whole chain is the same) and the same tu1_data.bnk, so it runs
/// on the TU1 config when patched.
///
/// THE PROGRAM IS CHECKED, NOT ONLY THE LABEL. Every one of those discs carries the same image
/// digest and file key in its security block (<see cref="Fable2ImageDigest"/>,
/// <see cref="Fable2FileKey"/>), and every one of their title updates the same first delta
/// block hash (<see cref="Fable2DeltaDigest"/>). A Fable 2 disc or update is accepted only
/// with those values, whatever its media id says. That is what lets a pressing be listed
/// before its files have been seen: the German disc (short media id 1D1F480C, version 0.0.0.28)
/// and the French title update are listed from the published title update index, whose file
/// names encode the base version (two base-32 digits, four times the version), and are proven
/// to be this program or refused when the player installs them. The German disc's full 16-byte
/// media id is not known, so it is matched by its short id.
///
/// All of it is read from the XEX headers: title id and version from the execution info,
/// the 16-byte media id, the image digest and the file key from the security block, the
/// versions a patch joins from its delta descriptor, the delta's first block hash from its
/// file format info. Nothing is hashed here and no file name is trusted.
/// </remarks>
public static class SupportedGame
{
	private static Refii.Core.Engine_Title Title => Refii.Core.Engine_Title.Active;

	/// <summary>Display name of the supported edition for <paramref name="title"/>.</summary>
	public static string NameFor(Refii.Core.Engine_Title title) =>
		title == Refii.Core.Engine_Title.Fable3 ? "Fable 3" : "Fable 2 GOTY";

	public static string Name => NameFor(Title);

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
			(((uint)MediaId[^4] << 24) | ((uint)MediaId[^3] << 16) | ((uint)MediaId[^2] << 8) | MediaId[^1])
			.ToString("X8");

		/// <summary>
		/// A pressing whose full media id is known matches all 16 bytes; one known only by its
		/// short id (four bytes here) matches the last dword.
		/// </summary>
		public bool Matches(ReadOnlySpan<byte> mediaId) =>
			MediaId.Length == 16
				? mediaId.SequenceEqual(MediaId)
				: mediaId.Length == 16 && mediaId[12..].SequenceEqual(MediaId);

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
		TitleUpdateName = "TU_16L61VH_000003C000000.000000000008R",
		UpdatedVersion = 0x0000011B,
	};

	/// <summary>
	/// GUESSED, NOT SEEN. No German disc or update has been read: the short media id and the
	/// update's name are from the published index, the versions from the name rule the other
	/// five pressings follow. It may not work; the digest checks refuse it if it is another
	/// program. The French title update above is listed the same way.
	/// </summary>
	public static readonly Pressing Germany = new()
	{
		Name = "Germany",
		MediaId = new byte[] { 0x1D, 0x1F, 0x48, 0x0C },
		BaseVersion = 0x0000001C,
		TitleUpdateName = "TU_16L61VH_000003G000000.000000000008S",
		UpdatedVersion = 0x0000011C,
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

	public static readonly Pressing Italy = new()
	{
		Name = "Italy",
		MediaId = new byte[]
		{
			0x5A, 0x71, 0xA9, 0x6F, 0x4B, 0x39, 0x42, 0x44,
			0x65, 0xC0, 0x57, 0x28, 0x51, 0xF4, 0x8E, 0x54,
		},
		BaseVersion = 0x0000001E,
		TitleUpdateName = "TU_16L61VH_000003O000000.000000000008U",
		UpdatedVersion = 0x0000011E,
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

	private static readonly Pressing[] Fable2Pressings = { UsaEurope, Russia, France, Spain, Italy, Germany };

	/// <summary>The image digest every supported Fable 2 disc executable carries.</summary>
	public static readonly byte[] Fable2ImageDigest = Convert.FromHexString("B77FAF838ED5A19936758B8AC1C0C428D5A18194");

	/// <summary>The encrypted file key every supported Fable 2 disc executable carries.</summary>
	public static readonly byte[] Fable2FileKey = Convert.FromHexString("E2DFE99A95BDE0771D4275148BE8FBC8");

	/// <summary>The first delta block hash every supported Fable 2 title update carries.</summary>
	public static readonly byte[] Fable2DeltaDigest = Convert.FromHexString("E619AEAD089E7FD77B87CEC64DE051BB93C06CB5");


	private static readonly Pressing[] Fable3Pressings = { Fable3UsaEurope };

	/// <summary>The supported disc pressings for <paramref name="title"/>.</summary>
	public static Pressing[] PressingsFor(Refii.Core.Engine_Title title) =>
		title == Refii.Core.Engine_Title.Fable3 ? Fable3Pressings : Fable2Pressings;

	public static Pressing[] Pressings => PressingsFor(Title);

	public static string TitleIdText => TitleId.ToString("X8");

	/// <summary>Shown wherever the restriction is stated, so it does not read as permanent.</summary>
	public const string EasingNote =
		"This restriction will be eased as we eventually support more versions closer to release.";

	/// <summary>The supported pressings for <paramref name="title"/> as "716F0A0D (USA / Europe), ...".</summary>
	public static string PressingsTextFor(Refii.Core.Engine_Title title)
	{
		Pressing[] list = PressingsFor(title);
		string[] parts = new string[list.Length];
		for (int i = 0; i < list.Length; i++)
		{
			parts[i] = list[i].ShortMediaIdText + " (" + list[i].Name + ")";
		}

		return string.Join(", ", parts);
	}

	/// <summary>The supported pressings as "716F0A0D (USA / Europe), 05FBECEA (Russia)".</summary>
	public static string PressingsText => PressingsTextFor(Title);

	/// <summary>The title updates that may be installed for <paramref name="title"/>, as "TU_... for 716F0A0D".</summary>
	public static string TitleUpdatesTextFor(Refii.Core.Engine_Title title)
	{
		string text = string.Empty;
		foreach (Pressing pressing in PressingsFor(title))
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

	/// <summary>The title updates that may be installed, as "TU_... for 716F0A0D".</summary>
	public static string TitleUpdatesText => TitleUpdatesTextFor(Title);

	/// <summary>What the player has to bring for <paramref name="title"/>, for messages.</summary>
	public static string RequirementFor(Refii.Core.Engine_Title title) =>
		NameFor(title) + " (title ID " + title.TitleId.ToString("X8") + ", media ID " + PressingsTextFor(title) + ")";

	/// <summary>What the player has to bring, for messages.</summary>
	public static string Requirement => RequirementFor(Title);

	/// <summary>Finds a supported pressing for <paramref name="title"/> matching <paramref name="mediaId"/>.</summary>
	public static Pressing? FindPressing(Refii.Core.Engine_Title title, ReadOnlySpan<byte> mediaId)
	{
		foreach (Pressing pressing in PressingsFor(title))
		{
			if (pressing.Matches(mediaId))
			{
				return pressing;
			}
		}

		return null;
	}

	public static Pressing? FindPressing(ReadOnlySpan<byte> mediaId) => FindPressing(Title, mediaId);

	/// <summary>Whether a base executable is one of the supported pressings of <paramref name="title"/>.</summary>
	public static bool IsExecutable(Refii.Core.Engine_Title title, XexIdentity executable, out string error)
	{
		error = string.Empty;

		if (executable.TitleId != title.TitleId)
		{
			error = "This disc image is not " + title.Name + ": its title ID is " +
					executable.TitleId.ToString("X8") + ", and this build needs " + RequirementFor(title) + ".";
			return false;
		}

		Pressing? pressing = FindPressing(title, executable.MediaId);
		if (pressing == null)
		{
			error = "This is a different pressing of " + title.Name + ": its media ID is " +
					executable.ShortMediaIdText + ", and this build only supports media ID " +
					PressingsTextFor(title) + ".";
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

		if (title != Refii.Core.Engine_Title.Fable3 &&
			(!executable.ImageDigest.AsSpan().SequenceEqual(Fable2ImageDigest) ||
			 !executable.FileKey.AsSpan().SequenceEqual(Fable2FileKey)))
		{
			error = "This disc image's executable is not the program this build was made from: its " +
					"image digest is " + Convert.ToHexString(executable.ImageDigest) + ", and every supported " +
					title.Name + " disc carries " + Convert.ToHexString(Fable2ImageDigest) + ".";
			return false;
		}

		return true;
	}

	/// <summary>Whether a base executable is one of the supported pressings.</summary>
	public static bool IsExecutable(XexIdentity executable, out string error) =>
		IsExecutable(Title, executable, out error);

	/// <summary>Whether a base executable is the pressing of <paramref name="title"/> the player selected.</summary>
	public static bool IsExecutable(Refii.Core.Engine_Title title, XexIdentity executable, Pressing selected, out string error)
	{
		if (!IsExecutable(title, executable, out error))
		{
			return false;
		}

		if (!selected.Matches(executable.MediaId))
		{
			Pressing actual = FindPressing(title, executable.MediaId)!;
			error = "This disc image is the " + actual.Name + " pressing (media ID " +
					actual.ShortMediaIdText + "), not " + selected.DisplayName +
					". Select " + actual.DisplayName + " as the game version.";
			return false;
		}

		return true;
	}

	/// <summary>Whether a base executable is the pressing the player selected.</summary>
	/// <remarks>
	/// The whole list is still consulted so a disc of another supported pressing is named as
	/// such: the fix is a different dropdown choice, not a different disc.
	/// </remarks>
	public static bool IsExecutable(XexIdentity executable, Pressing selected, out string error) =>
		IsExecutable(Title, executable, selected, out error);

	/// <summary>Whether a patch is the title update of the pressing of <paramref name="title"/> the player selected.</summary>
	public static bool IsTitleUpdate(Refii.Core.Engine_Title title, XexIdentity patch, Pressing selected, out string error)
	{
		if (!IsTitleUpdate(title, patch, out error))
		{
			return false;
		}

		if (!selected.Matches(patch.MediaId))
		{
			Pressing actual = FindPressing(title, patch.MediaId)!;
			error = "This title update is for the " + actual.Name + " pressing (media ID " +
					actual.ShortMediaIdText + "), not " + selected.DisplayName +
					". The one to fetch is " + selected.TitleUpdateName + ".";
			return false;
		}

		return true;
	}

	/// <summary>Whether a patch is the title update of the pressing the player selected.</summary>
	public static bool IsTitleUpdate(XexIdentity patch, Pressing selected, out string error) =>
		IsTitleUpdate(Title, patch, selected, out error);

	/// <summary>Whether a patch is the supported title update for its pressing of <paramref name="title"/>.</summary>
	public static bool IsTitleUpdate(Refii.Core.Engine_Title title, XexIdentity patch, out string error)
	{
		error = string.Empty;

		Pressing? pressing = FindPressing(title, patch.MediaId);
		if (pressing == null)
		{
			error = "This title update is for a different pressing of " + title.Name + ": it names media ID " +
					patch.ShortMediaIdText + ", and this build only supports media ID " +
					PressingsTextFor(title) + ". The ones to fetch are " + TitleUpdatesTextFor(title) + ".";
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

		if (title != Refii.Core.Engine_Title.Fable3 && !patch.DeltaDigest.AsSpan().SequenceEqual(Fable2DeltaDigest))
		{
			error = "This title update does not carry the changes this build was made from: its first " +
					"block hash is " + Convert.ToHexString(patch.DeltaDigest) + ", and every supported " +
					title.Name + " title update carries " + Convert.ToHexString(Fable2DeltaDigest) + ".";
			return false;
		}

		return true;
	}

	/// <summary>Whether a patch is the supported title update for its pressing.</summary>
	public static bool IsTitleUpdate(XexIdentity patch, out string error) =>
		IsTitleUpdate(Title, patch, out error);
}
