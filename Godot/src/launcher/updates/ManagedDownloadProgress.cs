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
/// Downloads a published build's package and extracts it over the install.
/// </summary>
/// <remarks>
/// <para>THE PACKAGE IS THE APP, NOT THE PLAYER'S DATA. It holds the executable, <c>bin/</c>,
/// <c>sdk/</c> and the bundled files under one top folder; the game data, saves, caches and
/// settings a player has are not in it, so extracting it writes none of them.</para>
/// <para>ONLY WHAT A PACKAGE PUT THERE IS EVER REMOVED. The files a package installs are recorded
/// (<see cref="ManifestName"/>); the next update deletes the recorded files its own package no
/// longer has, and nothing else. An install with no record yet (unpacked by hand) loses only
/// <c>bin/recompile</c>, whose include folders are numbered by order and must not be mixed
/// across builds.</para>
/// <para>A FILE IN USE IS MOVED ASIDE. The launcher is running from the files being replaced.
/// Each new file is written beside its target and renamed over it; where the system refuses
/// that for a loaded file (Windows), the old one is renamed out of the way first and deleted at
/// the next start (<see cref="RemoveLeftovers"/>).</para>
/// </remarks>
public static class ReleaseInstaller
{
	/// <summary>The record of the files the last package installed, one relative path a line.</summary>
	public const string ManifestName = "package-files.txt";

	/// <summary>The files an update moved aside because the system held them, one path a line.</summary>
	private const string LeftoversName = "leftovers.txt";

	private const string AsideSuffix = ".old-update";
	private const string NewSuffix = ".new-update";

	private static readonly HttpClient Client = CreateClient();

	private static HttpClient CreateClient()
	{
		var client = new HttpClient();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("refii-launcher");
		client.Timeout = Timeout.InfiniteTimeSpan;
		return client;
	}

	/// <summary>Where the download and the install's record are kept: <c>&lt;install&gt;/cache/update</c>.</summary>
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
	/// Downloads <paramref name="build"/>'s package and extracts it over <paramref name="installRoot"/>
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
				Directory.CreateDirectory(work);
				Interlocked.Exchange(ref progress.TotalBytes, build.AssetSize);
				string error = await DownloadAsync(build.AssetUrl, package, progress).ConfigureAwait(false);
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
				Fail(progress, exception.Message);
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

	private static async Task<string> DownloadAsync(string url, string path, ManagedDownloadProgress progress)
	{
		using HttpResponseMessage response = await Client.GetAsync(
			url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			return "HTTP " + (int)response.StatusCode + " for " + url;
		}

		if (response.Content.Headers.ContentLength is { } length && length > 0)
		{
			Interlocked.Exchange(ref progress.TotalBytes, length);
		}

		await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
		var buffer = new byte[1 << 16];
		long downloaded = 0;
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

		return string.Empty;
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

	/// <summary>
	/// Extracts the package at <paramref name="packagePath"/> over <paramref name="installRoot"/>,
	/// removes what the previous package installed and this one does not have, and records this
	/// package's files. Returns an error, or empty.
	/// </summary>
	public static string Extract(string packagePath, string installRoot, ManagedDownloadProgress? progress = null)
	{
		string root = Path.GetFullPath(installRoot);
		string work = WorkDirectory(root);
		Directory.CreateDirectory(work);
		string manifestPath = Path.Combine(work, ManifestName);
		var installed = new List<string>();
		var aside = new List<string>();

		using (ZipArchive archive = ZipFile.OpenRead(packagePath))
		{
			var entries = new List<(ZipArchiveEntry Entry, string Relative)>();
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				string relative = InstallPath(entry.FullName);
				if (relative.Length != 0)
				{
					entries.Add((entry, relative));
				}
			}

			if (entries.Count == 0)
			{
				return "The update package holds no files.";
			}

			if (progress is not null)
			{
				Volatile.Write(ref progress.TotalFiles, entries.Count);
			}

			if (!File.Exists(manifestPath))
			{
				// No record of what the last package installed: the one folder that must not
				// carry files across builds is cleared outright.
				TryDeleteDirectory(Path.Combine(root, "bin", "recompile"));
			}

			foreach ((ZipArchiveEntry entry, string relative) in entries)
			{
				string destination = Path.GetFullPath(Path.Combine(root, relative));
				if (!SafePath.IsSafeDestinationUnderBase(destination, root, out string unsafeReason))
				{
					return unsafeReason;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				string incoming = destination + NewSuffix;
				entry.ExtractToFile(incoming, overwrite: true);
				Replace(incoming, destination, aside);
				installed.Add(relative);
				if (progress is not null)
				{
					Interlocked.Increment(ref progress.FilesExtracted);
				}
			}
		}

		RemoveDropped(root, manifestPath, installed);
		File.WriteAllLines(manifestPath, installed);
		if (aside.Count != 0)
		{
			File.AppendAllLines(Path.Combine(work, LeftoversName), aside);
		}

		return string.Empty;
	}

	/// <summary>Deletes the files the previous package installed that <paramref name="installed"/> no longer names.</summary>
	private static void RemoveDropped(string root, string manifestPath, List<string> installed)
	{
		if (!File.Exists(manifestPath))
		{
			return;
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
				TryDelete(path);
			}
		}
	}

	/// <summary>Puts <paramref name="incoming"/> at <paramref name="destination"/>, moving a file the system holds out of the way.</summary>
	private static void Replace(string incoming, string destination, List<string> movedAside)
	{
		try
		{
			File.Move(incoming, destination, overwrite: true);
		}
		catch (Exception) when (File.Exists(destination))
		{
			string aside = destination + AsideSuffix;
			TryDelete(aside);
			File.Move(destination, aside);
			File.Move(incoming, destination);
			movedAside.Add(aside);
		}
	}

	/// <summary>
	/// Deletes the files an earlier update moved aside and any download it left behind. Called at
	/// start, when nothing holds them any more; a file still held is kept on the list for next time.
	/// </summary>
	public static void RemoveLeftovers(string installRoot)
	{
		try
		{
			string work = WorkDirectory(installRoot);
			if (!Directory.Exists(work))
			{
				return;
			}

			foreach (string path in Directory.EnumerateFiles(work, "*.download"))
			{
				TryDelete(path);
			}

			string list = Path.Combine(work, LeftoversName);
			if (!File.Exists(list))
			{
				return;
			}

			var still = new List<string>();
			foreach (string line in File.ReadAllLines(list))
			{
				string path = line.Trim();
				if (path.Length == 0 || !path.EndsWith(AsideSuffix, StringComparison.Ordinal))
				{
					continue;
				}

				TryDelete(path);
				if (File.Exists(path))
				{
					still.Add(path);
				}
			}

			if (still.Count == 0)
			{
				TryDelete(list);
			}
			else
			{
				File.WriteAllLines(list, still);
			}
		}
		catch (Exception)
		{
		}
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
