using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Save;

/// <summary>
/// The save profiles under <see cref="Engine_SaveLayout.Root"/> and which one is active:
/// <c>profiles.toml</c>, and a <c>.profile</c> id file in each profile's folder.
/// </summary>
/// <remarks>
/// A PROFILE IS NAMED AS THE GUEST NAMES ITS PLAYER: the folder is the name the guest signs in with
/// (the RexNet pseudonym, written by the native side as <c>rexnet/profile_name.txt</c>), made safe
/// for a folder. The id in <c>.profile</c> is what identifies it, so a folder renamed by hand is
/// found again by its id and nothing else ever moves it. A profile created before its name is known
/// (a first launch has no key yet) is PROVISIONAL and takes the stated name once, on the next launch
/// that finds it; from then on the folder is fixed.
///
/// <see cref="ActiveDirectory"/> only reads; <see cref="Ensure"/> is the one writer, run before the
/// guest starts (<see cref="Refii.Core.Engine_AppRoot.EnsureLayout"/>).
/// </remarks>
public sealed class Engine_SaveProfiles
{
	public const string DefaultFolder = "Player";

	public sealed record Profile(string Id, string Folder);

	private Engine_SaveProfiles(string root) => Root = root;

	public string Root { get; }

	public string? ActiveId { get; set; }

	public List<Profile> Profiles { get; } = new();

	/// <summary>The sources the migration has taken, by full path: each is migrated once.</summary>
	public List<string> MigratedSources { get; } = new();

	public string FilePath => Path.Combine(Root, Engine_SaveLayout.ProfilesFileName);

	public Profile? Active => Profiles.FirstOrDefault(p => p.Id == ActiveId) ?? Profiles.FirstOrDefault();

	public string DirectoryOf(Profile profile) => Path.Combine(Root, profile.Folder);

	/// <summary>The active profile's folder, read only: where it is, or where <see cref="Ensure"/> would make it.</summary>
	public static string ActiveDirectory(string root)
	{
		Engine_SaveProfiles profiles = Load(root);
		return profiles.Active is { } active ? profiles.DirectoryOf(active) : Path.Combine(root, DefaultFolder);
	}

	/// <summary>
	/// Makes sure a profile exists and is active, gives a provisional one the name its guest stated,
	/// and returns the active profile's folder. Appends what it did to <paramref name="report"/>.
	/// </summary>
	public static string Ensure(string root, List<string>? report = null)
	{
		Directory.CreateDirectory(root);
		Engine_SaveProfiles profiles = Load(root);
		Profile active = profiles.Active ??
			(Directory.Exists(Path.Combine(root, DefaultFolder))
				? profiles.Adopt(DefaultFolder, report)
				: profiles.Create(DefaultFolder, provisional: true, report));
		profiles.ActiveId = active.Id;

		string directory = profiles.DirectoryOf(active);
		if (IsProvisional(directory) &&
			ReadStatedName(directory) is { Length: > 0 } stated)
		{
			string folder = Engine_SaveLayout.SanitizeFolderName(stated);
			if (folder.Length != 0 && !string.Equals(folder, active.Folder, StringComparison.Ordinal))
			{
				folder = profiles.FreeFolder(folder);
				Directory.Move(directory, Path.Combine(root, folder));
				report?.Add($"profile {active.Id} renamed \"{active.Folder}\" -> \"{folder}\" (the name its guest signs in with)");
				profiles.Profiles[profiles.Profiles.IndexOf(active)] = active = active with { Folder = folder };
				directory = profiles.DirectoryOf(active);
			}

			WriteIdFile(directory, active.Id, provisional: false);
		}

		profiles.Save();
		return directory;
	}

	/// <summary>Creates a profile's folder and id file, adds it and makes it active. Not saved until <see cref="Save"/>.</summary>
	public Profile Create(string name, bool provisional, List<string>? report = null)
	{
		string folder = FreeFolder(Engine_SaveLayout.SanitizeFolderName(name) is { Length: > 0 } safe ? safe : DefaultFolder);
		var profile = new Profile(Guid.NewGuid().ToString("N"), folder);
		Directory.CreateDirectory(Path.Combine(Root, folder));
		WriteIdFile(Path.Combine(Root, folder), profile.Id, provisional);
		Profiles.Add(profile);
		ActiveId = profile.Id;
		report?.Add($"profile {profile.Id} created as \"{folder}\"{(provisional ? " (provisional name)" : string.Empty)}");
		return profile;
	}

	/// <summary>Makes an existing folder with no id file a provisional profile: what was written where the active profile was expected.</summary>
	private Profile Adopt(string folder, List<string>? report)
	{
		var profile = new Profile(Guid.NewGuid().ToString("N"), folder);
		WriteIdFile(Path.Combine(Root, folder), profile.Id, provisional: true);
		Profiles.Add(profile);
		ActiveId = profile.Id;
		report?.Add($"profile {profile.Id} adopted \"{folder}\" (provisional name)");
		return profile;
	}

	/// <summary><paramref name="folder"/>, or it with " 2", " 3"... when another folder already has that name.</summary>
	public string FreeFolder(string folder)
	{
		string candidate = folder;
		for (int n = 2; Directory.Exists(Path.Combine(Root, candidate)) || File.Exists(Path.Combine(Root, candidate)); n++)
		{
			candidate = folder + " " + n;
		}

		return candidate;
	}

	public static Engine_SaveProfiles Load(string root)
	{
		var profiles = new Engine_SaveProfiles(root);
		string path = profiles.FilePath;
		if (File.Exists(path))
		{
			profiles.Parse(File.ReadAllLines(path));
		}

		profiles.Reconcile();
		return profiles;
	}

	/// <summary>Finds a moved profile by its id file and lists a profile folder the file does not name.</summary>
	private void Reconcile()
	{
		if (!Directory.Exists(Root))
		{
			return;
		}

		var byId = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (string directory in Directory.EnumerateDirectories(Root))
		{
			if (ReadId(directory) is { } id && !byId.ContainsKey(id))
			{
				byId[id] = Path.GetFileName(directory);
			}
		}

		for (int i = 0; i < Profiles.Count; i++)
		{
			Profile profile = Profiles[i];
			if (byId.TryGetValue(profile.Id, out string? found) && found != profile.Folder)
			{
				Profiles[i] = profile with { Folder = found };
			}
		}

		foreach ((string id, string folder) in byId.OrderBy(pair => pair.Value, StringComparer.Ordinal))
		{
			if (Profiles.All(p => p.Id != id))
			{
				Profiles.Add(new Profile(id, folder));
			}
		}
	}

	private void Parse(string[] lines)
	{
		string? id = null;
		string? folder = null;
		bool inProfile = false;
		bool inSources = false;

		void Flush()
		{
			if (inProfile && id is { Length: > 0 } && folder is { Length: > 0 } &&
				Engine_SaveLayout.SanitizeFolderName(folder) == folder)
			{
				Profiles.Add(new Profile(id, folder));
			}

			id = folder = null;
		}

		foreach (string raw in lines)
		{
			string line = raw.Trim();
			if (inSources)
			{
				if (line.StartsWith(']'))
				{
					inSources = false;
				}
				else if (Unquote(line.TrimEnd(',')) is { } source)
				{
					MigratedSources.Add(source);
				}

				continue;
			}

			if (line.Length == 0 || line.StartsWith('#'))
			{
				continue;
			}

			if (line == "[[profile]]")
			{
				Flush();
				inProfile = true;
				continue;
			}

			int eq = line.IndexOf('=');
			if (eq <= 0)
			{
				continue;
			}

			string key = line[..eq].Trim();
			string value = line[(eq + 1)..].Trim();
			if (key == "migrated_sources" && value.StartsWith('['))
			{
				string rest = value[1..].Trim();
				if (rest.EndsWith(']'))
				{
					foreach (string item in rest[..^1].Split(','))
					{
						if (Unquote(item.Trim()) is { } source)
						{
							MigratedSources.Add(source);
						}
					}
				}
				else
				{
					inSources = true;
				}

				continue;
			}

			string? text = Unquote(value);
			if (inProfile)
			{
				if (key == "id") id = text;
				else if (key == "folder") folder = text;
			}
			else if (key == "active")
			{
				ActiveId = text;
			}
		}

		Flush();
	}

	public void Save()
	{
		Directory.CreateDirectory(Root);
		var text = new StringBuilder();
		text.Append("# Refii save profiles: each is a folder beside this file, found again by the id in its .profile.\n");
		text.Append("# The app keeps this file; the active profile is the one the games play from.\n");
		if (Active is { } active)
		{
			text.Append("active = ").Append(Quote(active.Id)).Append('\n');
		}

		text.Append("migrated_sources = [\n");
		foreach (string source in MigratedSources.Distinct(StringComparer.Ordinal))
		{
			text.Append("  ").Append(Quote(source)).Append(",\n");
		}

		text.Append("]\n");
		foreach (Profile profile in Profiles)
		{
			text.Append("\n[[profile]]\nid = ").Append(Quote(profile.Id)).Append("\nfolder = ").Append(Quote(profile.Folder)).Append('\n');
		}

		string temporary = FilePath + ".tmp";
		File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
		File.Move(temporary, FilePath, overwrite: true);
	}

	/// <summary>The id in a folder's <c>.profile</c>, or null when it is not a profile.</summary>
	public static string? ReadId(string directory) => ReadIdFile(directory).Id;

	public static bool IsProvisional(string directory) => ReadIdFile(directory).Provisional;

	/// <summary>The name the native side recorded the guest signing in with, or null.</summary>
	public static string? ReadStatedName(string profileDirectory)
	{
		string path = Path.Combine(Engine_SaveLayout.Identity(profileDirectory), Engine_SaveLayout.ProfileNameFileName);
		try
		{
			return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static (string? Id, bool Provisional) ReadIdFile(string directory)
	{
		string path = Path.Combine(directory, Engine_SaveLayout.ProfileIdFileName);
		if (!File.Exists(path))
		{
			return (null, false);
		}

		string? id = null;
		bool provisional = false;
		try
		{
			foreach (string raw in File.ReadAllLines(path))
			{
				int eq = raw.IndexOf('=');
				if (eq <= 0) continue;
				string key = raw[..eq].Trim();
				string value = raw[(eq + 1)..].Trim();
				if (key == "id") id = Unquote(value);
				else if (key == "provisional") provisional = value == "true";
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return (null, false);
		}

		return (string.IsNullOrEmpty(id) ? null : id, provisional);
	}

	public static void WriteIdFile(string directory, string id, bool provisional)
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, Engine_SaveLayout.ProfileIdFileName),
			"# This folder is a Refii save profile; the id keeps it the same profile if the folder is renamed.\n" +
			"id = " + Quote(id) + "\n" + (provisional ? "provisional = true\n" : string.Empty),
			new UTF8Encoding(false));
	}

	private static string Quote(string value) =>
		"\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

	private static string? Unquote(string value)
	{
		if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
		{
			return null;
		}

		var builder = new StringBuilder(value.Length);
		for (int i = 1; i < value.Length - 1; i++)
		{
			char c = value[i];
			if (c == '\\' && i + 1 < value.Length - 1)
			{
				builder.Append(value[++i]);
				continue;
			}

			builder.Append(c);
		}

		return builder.ToString();
	}
}
