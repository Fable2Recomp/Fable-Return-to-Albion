using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Refii.Launcher.Core;

public sealed class ManagedDownloadFile
{
	public string RepositoryRelativePath = string.Empty;
	public string LocalRelativePath = string.Empty;
	public string VersionKey = string.Empty;
}

public static class ManagedDownloads
{
	public static string GetConfigSuffix() =>
		OperatingSystem.IsWindows() ? "Windows" : "Linux";

	public static string GetPlatformAwareString(string key, string defaultValue, string section)
	{
		string platformValue = Ini.GetString(key + GetConfigSuffix(), string.Empty, section);
		if (platformValue.Length != 0)
		{
			return platformValue;
		}

		string genericValue = Ini.GetString(key, string.Empty, section);
		if (genericValue.Length != 0)
		{
			return genericValue;
		}

		return defaultValue.Length == 0 ? string.Empty : Ini.GetString(key, defaultValue, section);
	}

	private static string GetManagedFilesConfigValue()
	{
		if (!Ini.GetBool("EnableDownload", false, "Github"))
		{
			return string.Empty;
		}

		string configured = GetPlatformAwareString("ManagedFiles", string.Empty, "Github");
		if (configured.Length != 0)
		{
			return configured;
		}

		return GetPlatformAwareString("ExeRelativePath", string.Empty, "Github");
	}

	public static List<ManagedDownloadFile> GetManagedDownloadFiles()
	{
		var files = new List<ManagedDownloadFile>();
		string value = GetManagedFilesConfigValue();
		if (value.Length == 0)
		{
			return files;
		}

		foreach (string entry in value.Split(';'))
		{
			ManagedDownloadFile file = Parse(entry);
			if (file.RepositoryRelativePath.Length != 0 && file.LocalRelativePath.Length != 0)
			{
				files.Add(file);
			}
		}

		return files;
	}

	private static ManagedDownloadFile Parse(string entry)
	{
		var file = new ManagedDownloadFile();

		int separator = entry.IndexOf('|');
		if (separator < 0)
		{
			file.RepositoryRelativePath = entry.Trim();
			file.LocalRelativePath = Path.GetFileName(file.RepositoryRelativePath);
		}
		else
		{
			file.RepositoryRelativePath = entry[..separator].Trim();
			file.LocalRelativePath = entry[(separator + 1)..].Trim();
		}

		file.VersionKey = file.LocalRelativePath.Replace('\\', '/');
		return file;
	}

	public static bool HasDownloadSource() => GetManagedDownloadFiles().Count != 0;

	public static bool CanCheckForUpdates() => GetManagedDownloadFiles().Count != 0;

	public static string BuildRawDownloadUrl(string repositoryRelativePath) =>
		"https://github.com/" + GitHubApi.GetRepositoryName() + "/raw/" +
		GitHubApi.GetRepositoryBranch() + "/" + repositoryRelativePath;

	public static string GetInstalledManagedFilePath(ManagedDownloadFile file) =>
		Path.Combine(Platform.GetExecutableDirectory(), file.LocalRelativePath);
}

public static class VersionManager
{
	private static string GetVersionFilePath() =>
		Path.Combine(
			Installer.GetInstallBasePath(),
			Ini.GetString("VersionFile", "version.txt", "Game"));

	public static bool VersionFileExists() => File.Exists(GetVersionFilePath());

	public static Dictionary<string, string> LoadSavedVersions()
	{
		var versions = new Dictionary<string, string>(StringComparer.Ordinal);
		string path = GetVersionFilePath();
		if (!File.Exists(path))
		{
			return versions;
		}

		foreach (string rawLine in File.ReadLines(path))
		{
			string line = rawLine.Trim();
			if (line.Length == 0)
			{
				continue;
			}

			int equals = line.IndexOf('=');
			if (equals < 0)
			{
				versions[string.Empty] = line;
				continue;
			}

			versions[line[..equals].Trim()] = line[(equals + 1)..].Trim();
		}

		return versions;
	}

	public static string LoadSavedVersion()
	{
		Dictionary<string, string> versions = LoadSavedVersions();
		return versions.TryGetValue(string.Empty, out string? value) ? value : string.Empty;
	}

	public static void SaveCurrentVersions(IReadOnlyDictionary<string, string> versions)
	{
		string path = GetVersionFilePath();
		try
		{
			string? parent = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent!);
			}

			var builder = new StringBuilder();
			foreach ((string key, string value) in versions)
			{
				if (key.Length == 0)
				{
					builder.Append(value).Append('\n');
				}
				else
				{
					builder.Append(key).Append('=').Append(value).Append('\n');
				}
			}

			File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
		}
		catch (Exception)
		{
		}
	}

	public static void SaveCurrentVersion(string commitSha) =>
		SaveCurrentVersions(new Dictionary<string, string> { [string.Empty] = commitSha });

	public static void RemoveVersionFile()
	{
		try
		{
			File.Delete(GetVersionFilePath());
		}
		catch (Exception)
		{
		}
	}
}
