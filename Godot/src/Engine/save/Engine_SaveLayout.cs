using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Refii.Core;

namespace Refii.Save;

/// <summary>
/// Where every title's saves live: ONE folder for the whole app, one profile in it per player,
/// one folder per game in a profile.
/// </summary>
/// <remarks>
/// <code>
/// assets/saves/                         Root (desktop: the install; Android: &lt;userdir&gt;/runtime)
///   profiles.toml                       the profiles and which is active (Engine_SaveProfiles)
///   migration.log                       what the one-time migration moved and copied
///   &lt;Profile&gt;/                          the guest's user_data_root
///     .profile                          the profile's stable id
///     rexnet/                           ONE RexNet identity for every title: key, friends, names
///     Fable 2/                          the content of title 0x4D5307F1
///       00000001/HeroNNN/               saved games
///       Headers/00000001/HeroNNN.header
///       profile/63E83FFD|FE|FF          the title's profile settings
///       refii-snapshots/ save-export/ achievements.refii achievements.toml
///       Older saves/&lt;source&gt;/           migrated saves past the slot limit, never enumerated
///     Fable 3/                          the content of title 0x4D5308D6
///     &lt;TID8&gt;/                           any other title id
/// </code>
/// The native content manager follows the same recipe below the profile, told the title folders by
/// <see cref="TitleFoldersArgument"/>; the XUID picks no folder.
/// </remarks>
public static class Engine_SaveLayout
{
	public const string RootFolderName = "saves";
	public const string ProfilesFileName = "profiles.toml";
	public const string ProfileIdFileName = ".profile";
	public const string MigrationLogName = "migration.log";
	public const string IdentityFolderName = "rexnet";

	/// <summary>The name the guest signs in with, written by the native side beside the key (xam_net.cpp).</summary>
	public const string ProfileNameFileName = "profile_name.txt";

	public const string SaveGameType = "00000001";
	public const string HeadersFolderName = "Headers";
	public const string SettingsFolderName = "profile";
	public const string OlderSavesFolderName = "Older saves";
	public const string ExportFolderName = "save-export";
	public const string SdkAchievementsFileName = "achievements.toml";

	/// <summary><c>&lt;assets&gt;/saves</c>.</summary>
	public static string Root(string assetsDirectory) => Path.Combine(assetsDirectory, RootFolderName);

	/// <summary>A title's folder in a profile: its game's name, else its id as eight hex digits.</summary>
	public static string GameFolder(uint titleId) =>
		Engine_Title.All.FirstOrDefault(title => title.TitleId == titleId)?.SaveFolder ?? TitleIdText(titleId);

	public static string TitleIdText(uint titleId) => titleId.ToString("X8", CultureInfo.InvariantCulture);

	public static string GameDirectory(string profileDirectory, uint titleId) =>
		Path.Combine(profileDirectory, GameFolder(titleId));

	/// <summary><c>&lt;Game&gt;/00000001</c>: the folder the guest enumerates for saved games.</summary>
	public static string SaveGames(string profileDirectory, uint titleId) =>
		Path.Combine(GameDirectory(profileDirectory, titleId), SaveGameType);

	public static string Headers(string profileDirectory, uint titleId) =>
		Path.Combine(GameDirectory(profileDirectory, titleId), HeadersFolderName, SaveGameType);

	public static string Settings(string profileDirectory, uint titleId) =>
		Path.Combine(GameDirectory(profileDirectory, titleId), SettingsFolderName);

	public static string OlderSaves(string profileDirectory, uint titleId) =>
		Path.Combine(GameDirectory(profileDirectory, titleId), OlderSavesFolderName);

	public static string Identity(string profileDirectory) => Path.Combine(profileDirectory, IdentityFolderName);

	/// <summary>The native <c>content_title_folders</c> value: <c>4D5307F1=Fable 2;4D5308D6=Fable 3</c>.</summary>
	public static string TitleFoldersArgument() =>
		string.Join(';', Engine_Title.All.Select(title => TitleIdText(title.TitleId) + "=" + title.SaveFolder));

	/// <summary>
	/// A name made safe as one folder on every platform: path separators, control characters and the
	/// characters Windows refuses become spaces, runs collapse, and leading dots and trailing dots or
	/// spaces go. Empty when nothing usable is left.
	/// </summary>
	public static string SanitizeFolderName(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return string.Empty;
		}

		var builder = new StringBuilder(name.Length);
		foreach (char c in name)
		{
			bool bad = char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';
			char use = bad ? ' ' : c;
			if (use == ' ' && (builder.Length == 0 || builder[^1] == ' '))
			{
				continue;
			}

			builder.Append(use);
		}

		string result = builder.ToString().Trim().TrimStart('.').TrimEnd('.', ' ');
		if (result.Length > 64)
		{
			result = result[..64].TrimEnd('.', ' ');
		}

		return result is "." or ".." ? string.Empty : result;
	}
}
