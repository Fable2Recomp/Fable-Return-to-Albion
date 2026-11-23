using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Refii.Launcher.Core;

namespace Refii.Launcher.Updates;

/// <summary>Where an update stands, written by the worker and read by the launcher's frame.</summary>
public sealed class ManagedDownloadProgress
{
	private readonly object _mutex = new();
	private string _errorMessage = string.Empty;

	public long BytesDownloaded;
	public long TotalBytes;
	public int FilesExtracted;
	public int TotalFiles;
	private int _isComplete;
	private int _hasError;
	private int _extracting;

	public bool IsComplete
	{
		get => Volatile.Read(ref _isComplete) != 0;
		set => Volatile.Write(ref _isComplete, value ? 1 : 0);
	}

	public bool HasError
	{
		get => Volatile.Read(ref _hasError) != 0;
		set => Volatile.Write(ref _hasError, value ? 1 : 0);
	}

	/// <summary>The package is down and its files are being put in place.</summary>
	public bool Extracting
	{
		get => Volatile.Read(ref _extracting) != 0;
		set => Volatile.Write(ref _extracting, value ? 1 : 0);
	}

	public float GetPercentage()
	{
		long total = Interlocked.Read(ref TotalBytes);
		return total == 0 ? 0.0f : (float)Interlocked.Read(ref BytesDownloaded) / total * 100.0f;
	}

	public void SetErrorMessage(string message)
	{
		lock (_mutex)
		{
			_errorMessage = message;
		}
	}

	public string GetErrorMessage()
	{
		lock (_mutex)
		{
			return _errorMessage;
		}
	}
}

/// <summary>
/// Downloads a published build's package and installs it over the app, all of it or none of it.
/// </summary>
/// <remarks>
/// <para>THE PACKAGE IS THE APP, NOT THE PLAYER'S DATA. It holds the executable, <c>bin/</c>,
/// <c>sdk/</c> and the bundled files under one top folder; the game data, saves, caches and
/// settings a player has are not in it, so installing it writes none of them.</para>
/// <para>STAGED, THEN APPLIED. The whole package is first extracted to
/// <c>cache/update/staged</c>; until that has succeeded the app is untouched, so a damaged
/// package, a full disk or a dropped connection costs nothing. Applying is renames only: each
/// file being replaced or dropped moves into a backup folder and the staged file moves into its
/// place. A file the system holds (the running launcher, on Windows) can still be renamed, so
/// nothing is overwritten in place.</para>
/// <para>ANY FAILURE PUTS THE OLD BUILD BACK. The plan is written before the first rename
/// (<see cref="PlanName"/>): the backup folder, and for every file whether it replaces one or is
/// new. Rolling back deletes the new files and returns the backup, and is safe to repeat. A plan
/// still present at start means the apply was cut short, and <see cref="Recover"/> rolls it back
/// then.</para>
/// <para>ONLY WHAT A PACKAGE PUT THERE IS EVER REMOVED. The files a package installs are recorded
/// (<see cref="ManifestName"/>); the next update drops the recorded files its own package no
/// longer has. An install with no record yet (unpacked by hand) loses <c>bin/recompile</c>, whose
/// include folders are numbered by order and must not be mixed across builds. The one thing
/// removed that no package recorded is another build configuration of a native library the
/// package installs (<see cref="PackageIntegrity.OtherConfigurations"/>).</para>
/// <para>A PACKAGE IS CHECKED BEFORE IT IS APPLIED. One that ships its hash list
/// (<see cref="PackageIntegrity.HashesName"/>) has every staged file compared with it, and a
/// mismatch stops the update with the install untouched.</para>
/// </remarks>
public static class ReleaseInstaller
{
	/// <summary>The record of the files the last package installed, one relative path a line.</summary>
	public const string ManifestName = "package-files.txt";

	/// <summary>
	/// The apply in progress: the backup folder's name, then one line per package file,
	/// <c>+path</c> for a file the install did not have and <c>=path</c> for one it replaces.
	/// </summary>
	public const string PlanName = "applying.txt";

	/// <summary>Free space an update leaves on the disk beyond what it writes.</summary>
	public const long SpaceMargin = 64L << 20;

	private const string PendingManifestName = ManifestName + ".new";
	private const string StagedName = "staged";
	private const string BackupPrefix = "backup-";

	private static readonly HttpClient Client = CreateClient();

	private static HttpClient CreateClient()
	{
		var client = new HttpClient();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("refii-launcher");
		client.Timeout = Timeout.InfiniteTimeSpan;
		return client;
	}

	/// <summary>Where the download, the staged files, the backup and the install's record are kept: <c>&lt;install&gt;/cache/update</c>.</summary>
	public static string WorkDirectory(string installRoot) => Path.Combine(installRoot, "cache", "update");

	public static string BuildProgressButtonLabel(ManagedDownloadProgress progress)
	{
		if (progress.Extracting)
		{
			int total = Volatile.Read(ref progress.TotalFiles);
			return "Installing update...\n" + (total > 0
				? Volatile.Read(ref progress.FilesExtracted) + " of " + total + " files"
				: "Preparing...");
		}

		return "Downloading update...\n" + (Interlocked.Read(ref progress.TotalBytes) > 0
			? progress.GetPercentage().ToString("0.0") + "% downloaded"
			: "Connecting...");
	}

	/// <summary>
	/// Downloads <paramref name="build"/>'s package and installs it over <paramref name="installRoot"/>
	/// on a worker; <paramref name="onSuccess"/> runs once every file is in place.
	/// </summary>
	public static void InstallAsync(
		ReleaseBuild build, string installRoot, ManagedDownloadProgress progress, Action? onSuccess)
	{
		_ = Task.Run(async () =>
		{
			string work = WorkDirectory(installRoot);
			string package = Path.Combine(work, build.AssetName + ".download");
			try
			{
				// What would stop the install is found before the download, not after it.
				string error = WriteError(installRoot);
				if (error.Length == 0)
				{
					error = SpaceError(build.AssetSize + SpaceMargin, FreeBytes(installRoot));
				}

				if (error.Length == 0)
				{
					Interlocked.Exchange(ref progress.TotalBytes, build.AssetSize);
					error = await DownloadAsync(build, package, progress).ConfigureAwait(false);
				}

				if (error.Length == 0)
				{
					progress.Extracting = true;
					error = Extract(package, installRoot, progress);
				}

				TryDelete(package);
				if (error.Length != 0)
				{
					Fail(progress, error);
					return;
				}
			}
			catch (Exception exception)
			{
				TryDelete(package);
				Fail(progress, Explain(exception));
				return;
			}

			progress.IsComplete = true;
			onSuccess?.Invoke();
		});
	}

	private static void Fail(ManagedDownloadProgress progress, string error)
	{
		progress.HasError = true;
		progress.SetErrorMessage(error.Length == 0 ? "The update failed." : error);
		progress.IsComplete = true;
	}

	private static async Task<string> DownloadAsync(ReleaseBuild build, string path, ManagedDownloadProgress progress)
	{
		using HttpResponseMessage response = await Client.GetAsync(
			build.AssetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			return "The download was refused (HTTP " + (int)response.StatusCode + ").";
		}

		if (response.Content.Headers.ContentLength is { } length && length > 0)
		{
			Interlocked.Exchange(ref progress.TotalBytes, length);
		}

		long downloaded = 0;
		await using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
		await using (var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
		{
			var buffer = new byte[1 << 16];
			while (true)
			{
				int read = await source.ReadAsync(buffer).ConfigureAwait(false);
				if (read <= 0)
				{
					break;
				}

				await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
				downloaded += read;
				Interlocked.Exchange(ref progress.BytesDownloaded, downloaded);
			}
		}

		return build.AssetSize > 0 && downloaded != build.AssetSize
			? "The download ended early (" + Size(downloaded) + " of " + Size(build.AssetSize) + "). Try again."
			: string.Empty;
	}

	/// <summary>
	/// A package entry's path inside the install: the entry's path without its top folder, with
	/// forward slashes. Empty for the top folder itself, a directory entry, or a path that would
	/// leave the install.
	/// </summary>
	public static string InstallPath(string entryName)
	{
		string name = entryName.Replace('\\', '/');
		int slash = name.IndexOf('/');
		if (slash < 0 || name.EndsWith('/'))
		{
			return string.Empty;
		}

		string relative = name[(slash + 1)..];
		return relative.Length != 0 && SafePath.IsSafeRelativePath(relative, out _) ? relative : string.Empty;
	}

	/// <summary>Why <paramref name="installRoot"/> cannot take an update, or empty when it can be written.</summary>
	public static string WriteError(string installRoot)
	{
		try
		{
			Directory.CreateDirectory(WorkDirectory(installRoot));
			string probe = Path.Combine(installRoot, ".write-check-" + Guid.NewGuid().ToString("N"));
			File.WriteAllBytes(probe, Array.Empty<byte>());
			File.Delete(probe);
			return string.Empty;
		}
		catch (Exception)
		{
			return "The app's folder cannot be written to (" + installRoot + "). " +
				"Move the app to a folder you own, or give yourself write access to it.";
		}
	}

	/// <summary>The space free for <paramref name="path"/>'s disk; null when the system does not say.</summary>
	public static long? FreeBytes(string path)
	{
		try
		{
			string full = Path.GetFullPath(path);
			string name = OperatingSystem.IsWindows() ? Path.GetPathRoot(full) ?? full : full;
			return new DriveInfo(name).AvailableFreeSpace;
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>The refusal when <paramref name="free"/> is known and under <paramref name="needed"/>; empty otherwise.</summary>
	public static string SpaceError(long needed, long? free) =>
		free is { } available && available < needed
			? "Not enough free disk space: the update needs " + Size(needed) + " and " + Size(available) + " is free."
			: string.Empty;

	private static string Size(long bytes) =>
		bytes >= 1L << 30 ? (bytes / (double)(1L << 30)).ToString("0.0") + " GB" : (bytes / (double)(1L << 20)).ToString("0") + " MB";

	private static string Explain(Exception exception) => exception switch
	{
		InvalidDataException => "The update package is damaged. Try again.",
		UnauthorizedAccessException => "A file in the app's folder could not be written: " + exception.Message,
		_ => exception.Message,
	};

	/// <summary>
	/// Installs the package at <paramref name="packagePath"/> over <paramref name="installRoot"/>:
	/// stages every file, then swaps them in, dropping what the previous package installed and
	/// this one does not have. Returns an error, or empty; after an error the install holds the
	/// build it held before.
	/// </summary>
	public static string Extract(string packagePath, string installRoot, ManagedDownloadProgress? progress = null)
	{
		string root = Path.GetFullPath(installRoot);
		string work = WorkDirectory(root);
		string blocked = WriteError(root);
		if (blocked.Length != 0)
		{
			return blocked;
		}

		Recover(root, removeDownloads: false);
		string planPath = Path.Combine(work, PlanName);
		if (File.Exists(planPath))
		{
			return "An earlier update could not be undone yet. Restart the launcher, then update again.";
		}

		string staged = Path.Combine(work, StagedName);
		var installed = new List<string>();
		try
		{
			using ZipArchive archive = ZipFile.OpenRead(packagePath);
			var entries = new List<(ZipArchiveEntry Entry, string Relative)>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			long size = 0;
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				string relative = InstallPath(entry.FullName);
				if (relative.Length == 0 || !seen.Add(relative))
				{
					continue;
				}

				string destination = Path.GetFullPath(Path.Combine(root, relative));
				if (!SafePath.IsSafeDestinationUnderBase(destination, root, out string unsafeReason))
				{
					return unsafeReason;
				}

				entries.Add((entry, relative));
				size += entry.Length;
			}

			if (entries.Count == 0)
			{
				return "The update package holds no files.";
			}

			string space = SpaceError(size + SpaceMargin, FreeBytes(root));
			if (space.Length != 0)
			{
				return space;
			}

			if (progress is not null)
			{
				Volatile.Write(ref progress.TotalFiles, entries.Count);
			}

			foreach ((ZipArchiveEntry entry, string relative) in entries)
			{
				string target = Path.Combine(staged, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				entry.ExtractToFile(target, overwrite: true);
				installed.Add(relative);
				if (progress is not null)
				{
					Interlocked.Increment(ref progress.FilesExtracted);
				}
			}
		}
		catch (Exception exception)
		{
			TryDeleteDirectory(staged);
			return Explain(exception) + " The app was not changed.";
		}

		// A package that ships its hash list is checked while it is still only staged.
		PackageIntegrity.Report? report =
			PackageIntegrity.Verify(staged, Path.Combine(staged, PackageIntegrity.HashesName));
		if (report is { Passed: false })
		{
			TryDeleteDirectory(staged);
			return "The update package is damaged: " + (report.Missing.Count + report.Changed.Count)
				+ " of its files do not match its own hash list. Try again. The app was not changed.";
		}

		return Apply(root, installed);
	}

	/// <summary>Swaps the staged files in. Returns an error after putting the old build back, or empty.</summary>
	private static string Apply(string root, List<string> installed)
	{
		string work = WorkDirectory(root);
		string staged = Path.Combine(work, StagedName);
		string manifestPath = Path.Combine(work, ManifestName);
		string planPath = Path.Combine(work, PlanName);
		string backupName = BackupPrefix + Guid.NewGuid().ToString("N")[..8];
		string backup = Path.Combine(work, backupName);
		try
		{
			var plan = new List<string>(installed.Count + 1) { backupName };
			foreach (string relative in installed)
			{
				plan.Add((File.Exists(Path.Combine(root, relative)) ? "=" : "+") + relative);
			}

			List<string> dropped = Dropped(root, manifestPath, installed);
			bool unrecorded = !File.Exists(manifestPath);
			File.WriteAllLines(planPath, plan);

			if (unrecorded)
			{
				// No record of what the last package installed: the one folder that must not
				// carry files across builds leaves whole.
				string recompile = Path.Combine(root, "bin", "recompile");
				if (Directory.Exists(recompile))
				{
					string aside = Path.Combine(backup, "bin", "recompile");
					Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
					Directory.Move(recompile, aside);
				}
			}

			foreach (string relative in dropped)
			{
				MoveAside(root, backup, relative);
			}

			// Another build configuration of a library this package installs (librexruntimerd.so
			// beside librexruntime.so) is a second copy of it for the game to load.
			var kept = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
			foreach (string relative in installed)
			{
				foreach (string other in PackageIntegrity.OtherConfigurations(relative))
				{
					if (!kept.Contains(other))
					{
						MoveAside(root, backup, other);
					}
				}
			}

			foreach (string relative in installed)
			{
				string destination = Path.Combine(root, relative);
				MoveAside(root, backup, relative);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				File.Move(Path.Combine(staged, relative), destination);
			}

			File.WriteAllLines(Path.Combine(work, PendingManifestName), installed);
			// The commit: without the plan, a start finishes the update instead of undoing it.
			File.Delete(planPath);
		}
		catch (Exception exception)
		{
			return Explain(exception) + " " + (RollBack(root)
				? "The previous build was put back."
				: "The previous build could not be fully put back; restart the launcher to finish undoing the update.");
		}

		Recover(root, removeDownloads: false);
		return string.Empty;
	}

	/// <summary>Moves the install's file at <paramref name="relative"/> into the backup, when there is one.</summary>
	private static void MoveAside(string root, string backup, string relative)
	{
		string path = Path.Combine(root, relative);
		if (!File.Exists(path))
		{
			return;
		}

		string aside = Path.Combine(backup, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
		File.Move(path, aside, overwrite: true);
	}

	/// <summary>The files the previous package installed that <paramref name="installed"/> no longer names.</summary>
	private static List<string> Dropped(string root, string manifestPath, List<string> installed)
	{
		var dropped = new List<string>();
		if (!File.Exists(manifestPath))
		{
			return dropped;
		}

		var kept = new HashSet<string>(installed, StringComparer.OrdinalIgnoreCase);
		foreach (string line in File.ReadAllLines(manifestPath))
		{
			string relative = line.Trim();
			if (relative.Length == 0 || kept.Contains(relative) || !SafePath.IsSafeRelativePath(relative, out _))
			{
				continue;
			}

			string path = Path.GetFullPath(Path.Combine(root, relative));
			if (SafePath.IsSafeDestinationUnderBase(path, root, out _))
			{
				dropped.Add(relative);
			}
		}

		return dropped;
	}

	/// <summary>
	/// Undoes the apply the plan describes: deletes the files it added and returns every file in
	/// its backup. Repeatable. False when a file could not be returned; the plan then stays for
	/// the next start.
	/// </summary>
	private static bool RollBack(string root)
	{
		string work = WorkDirectory(root);
		string planPath = Path.Combine(work, PlanName);
		string staged = Path.Combine(work, StagedName);
		try
		{
			string[] plan = File.ReadAllLines(planPath);
			string backupName = plan.Length != 0 ? plan[0].Trim() : string.Empty;
			bool named = backupName.StartsWith(BackupPrefix, StringComparison.Ordinal) &&
				backupName.IndexOfAny(new[] { '/', '\\', '.' }) < 0;
			string backup = Path.Combine(work, named ? backupName : BackupPrefix + "none");

			for (int i = 1; i < plan.Length; i++)
			{
				string line = plan[i];
				if (line.Length < 2 || line[0] != '+' || !SafePath.IsSafeRelativePath(line[1..], out _))
				{
					continue;
				}

				// A file the install did not have: gone again, unless it never left the staging.
				if (!File.Exists(Path.Combine(staged, line[1..])))
				{
					File.Delete(Path.Combine(root, line[1..]));
				}
			}

			if (Directory.Exists(backup))
			{
				foreach (string path in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
				{
					string destination = Path.Combine(root, Path.GetRelativePath(backup, path));
					Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
					File.Move(path, destination, overwrite: true);
				}
			}

			File.Delete(Path.Combine(work, PendingManifestName));
			File.Delete(planPath);
			TryDeleteDirectory(staged);
			TryDeleteDirectory(backup);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// Settles whatever an earlier update left: rolls back an apply that was cut short, finishes
	/// the record of one that committed, and deletes the staging and the backups (a backed-up
	/// file the system still held when its update finished is free by the next start). Called at
	/// start and before every install. Returns a line for the log when an update was undone.
	/// </summary>
	public static string Recover(string installRoot, bool removeDownloads = true)
	{
		try
		{
			string root = Path.GetFullPath(installRoot);
			string work = WorkDirectory(root);
			if (!Directory.Exists(work))
			{
				return string.Empty;
			}

			if (removeDownloads)
			{
				foreach (string path in Directory.EnumerateFiles(work, "*.download"))
				{
					TryDelete(path);
				}
			}

			if (File.Exists(Path.Combine(work, PlanName)))
			{
				return RollBack(root)
					? "An update was interrupted; the previous build was put back."
					: "An update was interrupted and could not be fully undone. Restart the launcher to try again.";
			}

			string pending = Path.Combine(work, PendingManifestName);
			if (File.Exists(pending))
			{
				File.Move(pending, Path.Combine(work, ManifestName), overwrite: true);
			}

			TryDeleteDirectory(Path.Combine(work, StagedName));
			foreach (string backup in Directory.GetDirectories(work, BackupPrefix + "*"))
			{
				TryDeleteDirectory(backup);
			}
		}
		catch (Exception)
		{
		}

		return string.Empty;
	}

	private static void TryDelete(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception)
		{
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
			{
				Directory.Delete(path, recursive: true);
			}
		}
		catch (Exception)
		{
		}
	}
}
