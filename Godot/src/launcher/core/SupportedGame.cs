using System;

namespace Refii.Launcher.Core;

/// <summary>
/// The one pressing of Fable 2 this build recompiles, and the one title update that may be
/// installed with it. The update is the player's choice: each of the two executables has its
/// own config under versions/.
/// </summary>
/// <remarks>
/// The generated code, the hooks and every guest address in this build were taken from one
/// executable with one patch applied. Any other pair extracts and compiles for an hour and
/// then fails or misbehaves at run time, so it is refused before anything is copied.
///
/// All of it is read from the XEX headers: title id and version from the execution info,
/// the 16-byte media id from the security block, the versions a patch joins from its delta
/// descriptor. Nothing is hashed and no file name is trusted.
/// </remarks>
public static class SupportedGame
{
	public const string Name = "Fable 2 GOTY";

	public const uint TitleId = 0x4D5307F1;

	/// <summary>The disc executable's version, which is also what the patch applies to.</summary>
	public const uint BaseVersion = 0x0000001A;

	/// <summary>The version the title update produces.</summary>
	public const uint UpdatedVersion = 0x0000011A;

	/// <summary>The title update's package name, as it is distributed.</summary>
	public const string TitleUpdateName = "TU_16L61VH_0000038000000.000000000008Q";

	/// <summary>The XGD2 media id; its last dword is the short id, 716F0A0D.</summary>
	private static readonly byte[] MediaId =
	{
		0xA5, 0x09, 0xA2, 0xD6, 0xD9, 0xE9, 0x98, 0x32,
		0x7C, 0x39, 0x80, 0x33, 0x71, 0x6F, 0x0A, 0x0D,
	};

	public const string ShortMediaIdText = "716F0A0D";

	public static string TitleIdText => TitleId.ToString("X8");

	/// <summary>Shown wherever the restriction is stated, so it does not read as permanent.</summary>
	public const string EasingNote =
		"This restriction will be eased as we eventually support more versions closer to release.";

	/// <summary>What the player has to bring, for messages.</summary>
	public static string Requirement =>
		Name + " (title ID " + TitleIdText + ", media ID " + ShortMediaIdText +
		") with title update " + TitleUpdateName;

	/// <summary>Whether a base executable is the supported one.</summary>
	public static bool IsExecutable(XexIdentity executable, out string error)
	{
		error = string.Empty;

		if (executable.TitleId != TitleId)
		{
			error = "This disc image is not Fable 2: its title ID is " +
					executable.TitleId.ToString("X8") + ", and this build needs " + Requirement + ".";
			return false;
		}

		if (!executable.MediaId.AsSpan().SequenceEqual(MediaId))
		{
			error = "This is a different pressing of Fable 2: its media ID is " +
					executable.ShortMediaIdText + ", and this build only supports media ID " +
					ShortMediaIdText + " (" + Name + ").";
			return false;
		}

		if (executable.Version != BaseVersion)
		{
			error = "This disc image's executable is version " + executable.VersionText +
					", and this build only supports version " +
					XexIdentity.FormatVersion(BaseVersion) + ".";
			return false;
		}

		return true;
	}

	/// <summary>Whether a patch is the supported title update.</summary>
	public static bool IsTitleUpdate(XexIdentity patch, out string error)
	{
		error = string.Empty;

		if (!patch.MediaId.AsSpan().SequenceEqual(MediaId))
		{
			error = "This title update is for a different pressing of Fable 2: it names media ID " +
					patch.ShortMediaIdText + ", and this build only supports media ID " +
					ShortMediaIdText + ". The one to fetch is " + TitleUpdateName + ".";
			return false;
		}

		if (patch.SourceVersion != BaseVersion || patch.Version != UpdatedVersion)
		{
			error = "This is not the supported title update: it patches version " +
					patch.SourceVersionText + " to " + patch.VersionText +
					", and this build needs " + XexIdentity.FormatVersion(BaseVersion) + " to " +
					XexIdentity.FormatVersion(UpdatedVersion) + ". The one to fetch is " +
					TitleUpdateName + ".";
			return false;
		}

		return true;
	}
}
