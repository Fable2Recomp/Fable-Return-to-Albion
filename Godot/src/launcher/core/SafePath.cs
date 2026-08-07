using System;
using System.Collections.Generic;
using System.IO;

namespace Refii.Launcher.Core;

public static class SafePath
{
	private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
	{
		"con", "prn", "aux", "nul",
		"com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
		"lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
	};

	private static bool IsWindowsReservedName(string lowercase)
	{
		int dot = lowercase.IndexOf('.');
		string stem = dot < 0 ? lowercase : lowercase[..dot];
		return ReservedNames.Contains(stem);
	}

	public static bool IsWindowsCompatibleFilename(string filename)
	{
		if (filename.Length == 0)
		{
			return false;
		}

		foreach (char ch in filename)
		{
			if (ch <= 0x1F || ch == '<' || ch == '>' || ch == ':' || ch == '"' ||
				ch == '/' || ch == '\\' || ch == '|' || ch == '?' || ch == '*')
			{
				return false;
			}
		}

		char last = filename[^1];
		if (last == ' ' || last == '.')
		{
			return false;
		}

		return !IsWindowsReservedName(filename.ToLowerInvariant());
	}

	private static List<string> Components(string path)
	{
		var parts = new List<string>();
		foreach (string part in path.Split('/', '\\'))
		{
			if (part.Length != 0)
			{
				parts.Add(part);
			}
		}

		return parts;
	}

	public static bool IsSafeRelativePath(string path, out string error)
	{
		List<string> parts = Components(path);
		if (parts.Count == 0)
		{
			error = "invalid output path: empty";
			return false;
		}

		foreach (string part in parts)
		{
			if (part == "." || part == "..")
			{
				error = "invalid output path component: " + part;
				return false;
			}

			if (!IsWindowsCompatibleFilename(part))
			{
				error = "output path component not compatible with Windows: " + part;
				return false;
			}
		}

		error = string.Empty;
		return true;
	}

	public static bool IsSafeDestinationUnderBase(string destinationPath, string basePath, out string error)
	{
		string normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(basePath));
		string normalizedDestination = Path.GetFullPath(destinationPath);

		string relative = Path.GetRelativePath(normalizedBase, normalizedDestination);
		if (relative.Length == 0 || relative == "." || Path.IsPathRooted(relative))
		{
			error = "destination path is outside the install root: " + normalizedDestination;
			return false;
		}

		foreach (string part in Components(relative))
		{
			if (part == "..")
			{
				error = "destination path is outside the install root: " + normalizedDestination;
				return false;
			}
		}

		return IsSafeRelativePath(relative, out error);
	}
}
