using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Refii.Launcher.Core;

namespace Refii.Launcher.Updates;

/// <summary>
/// Checks an install against the hash list its package ships.
/// </summary>
/// <remarks>
/// <para>THE LIST. Packaging writes <see cref="HashesName"/> at the package's top folder: one line
/// per packaged file, its SHA-256 in hex, two spaces, its path with forward slashes — the format
/// <c>sha256sum -c</c> reads. The list does not name itself.</para>
/// <para>WHEN. The updater checks the files it staged before it swaps any of them in, so a damaged
/// package never reaches the install. The launcher checks the whole install the first time it
/// starts on a build (<see cref="VerifiedName"/> records the list it last passed).</para>
/// <para>WHAT IS NOT A FAULT. A file under <c>sdk/</c> with other contents: that is the modding
/// project, and it is the player's to edit. It still has to be there.</para>
/// </remarks>
public static class PackageIntegrity
{
	/// <summary>The package's hash list, at the install's top folder.</summary>
	public const string HashesName = "package-hashes.txt";

	/// <summary>The SHA-256 of the hash list the install last passed, in the update work folder.</summary>
	public const string VerifiedName = "package-verified.txt";

	private const string EditablePrefix = "sdk/";

	/// <summary>What a check found: how many files it read, and the listed files missing or changed.</summary>
	public sealed record Report(int Checked, IReadOnlyList<string> Missing, IReadOnlyList<string> Changed)
	{
		public bool Passed => Missing.Count == 0 && Changed.Count == 0;

		/// <summary>One line for the log and the Updates tab.</summary>
		public string Summary
		{
			get
			{
				if (Passed)
				{
					return "File check: all " + Checked + " files match this build.";
				}

				var parts = new List<string>();
				if (Missing.Count != 0)
				{
					parts.Add(Missing.Count + " missing (" + Sample(Missing) + ")");
				}

				if (Changed.Count != 0)
				{
					parts.Add(Changed.Count + " changed (" + Sample(Changed) + ")");
				}

				return "File check: " + string.Join(", ", parts)
					+ ". Install this build again from the Updates tab or a fresh download.";
			}
		}

		private static string Sample(IReadOnlyList<string> paths) =>
			string.Join(", ", paths.Take(3)) + (paths.Count > 3 ? ", ..." : string.Empty);
	}

	/// <summary>
	/// Checks the files under <paramref name="root"/> against the list at
	/// <paramref name="listPath"/>. Null when there is no list or it names nothing.
	/// </summary>
	public static Report? Verify(string root, string listPath)
	{
		List<(string Hash, string Relative)> entries = ReadList(listPath);
		if (entries.Count == 0)
		{
			return null;
		}

		var states = new byte[entries.Count];
		Parallel.For(0, entries.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
		{
			(string hash, string relative) = entries[index];
			string path = Path.Combine(root, relative);
			try
			{
				if (!File.Exists(path))
				{
					states[index] = 1;
					return;
				}

				if (relative.StartsWith(EditablePrefix, StringComparison.Ordinal))
				{
					return;
				}

				using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
					FileOptions.SequentialScan);
				string actual = Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream));
				states[index] = string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase) ? (byte)0 : (byte)2;
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				states[index] = 2;
			}
		});

		var missing = new List<string>();
		var changed = new List<string>();
		for (int index = 0; index < entries.Count; index++)
		{
			if (states[index] == 1)
			{
				missing.Add(entries[index].Relative);
			}
			else if (states[index] == 2)
			{
				changed.Add(entries[index].Relative);
			}
		}

		return new Report(entries.Count, missing, changed);
	}

	/// <summary>The install's own check. Null when its build ships no list.</summary>
	public static Report? VerifyInstall(string installRoot) =>
		Verify(installRoot, Path.Combine(installRoot, HashesName));

	/// <summary>True when the install has a hash list it has not passed yet.</summary>
	public static bool IsDue(string installRoot)
	{
		try
		{
			string list = Path.Combine(installRoot, HashesName);
			if (!File.Exists(list))
			{
				return false;
			}

			string marker = Path.Combine(ReleaseInstaller.WorkDirectory(installRoot), VerifiedName);
			return !File.Exists(marker) || File.ReadAllText(marker).Trim() != ListIdentity(list);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>Records that the install passed its current list.</summary>
	public static void MarkVerified(string installRoot)
	{
		try
		{
			string work = ReleaseInstaller.WorkDirectory(installRoot);
			Directory.CreateDirectory(work);
			File.WriteAllText(Path.Combine(work, VerifiedName), ListIdentity(Path.Combine(installRoot, HashesName)));
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static string ListIdentity(string listPath)
	{
		using FileStream stream = File.OpenRead(listPath);
		return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream));
	}

	private static List<(string Hash, string Relative)> ReadList(string listPath)
	{
		var entries = new List<(string, string)>();
		if (!File.Exists(listPath))
		{
			return entries;
		}

		foreach (string line in File.ReadAllLines(listPath))
		{
			// 64 hex digits, two separator characters, then the path.
			if (line.Length < 67 || line[64] != ' ')
			{
				continue;
			}

			string relative = line[66..].Trim();
			if (relative.Length != 0 && relative != HashesName && SafePath.IsSafeRelativePath(relative, out _))
			{
				entries.Add((line[..64], relative));
			}
		}

		return entries;
	}

	/// <summary>
	/// The other build configurations of a native library at <paramref name="relative"/>: the same
	/// name with no suffix, <c>rd</c> or <c>d</c>, in the same folder. Empty for anything that is
	/// not a shared library under <c>bin/</c>.
	/// </summary>
	public static List<string> OtherConfigurations(string relative)
	{
		var others = new List<string>();
		string extension = Path.GetExtension(relative);
		if (!relative.StartsWith("bin/", StringComparison.Ordinal)
			|| relative.StartsWith("bin/recompile/", StringComparison.Ordinal)
			|| (extension != ".so" && extension != ".dll" && extension != ".dylib"))
		{
			return others;
		}

		int slash = relative.LastIndexOf('/');
		string folder = relative[..(slash + 1)];
		string stem = relative[(slash + 1)..^extension.Length];

		// The names this one could be a configuration of: itself, and itself without a suffix.
		var bases = new List<string> { stem };
		foreach (string suffix in new[] { "rd", "d" })
		{
			if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.Ordinal))
			{
				bases.Add(stem[..^suffix.Length]);
				break;
			}
		}

		foreach (string name in bases)
		{
			foreach (string suffix in new[] { string.Empty, "rd", "d" })
			{
				string other = folder + name + suffix + extension;
				if (other != relative && !others.Contains(other))
				{
					others.Add(other);
				}
			}
		}

		return others;
	}
}
