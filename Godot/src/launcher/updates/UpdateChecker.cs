using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Refii.Launcher.Updates;

namespace Refii.Launcher.Core;

public sealed class UpdateCheckResult
{
	public List<ManagedDownloadFile> UpdatedFiles = new();
	public Dictionary<string, string> LatestVersions = new();
	public bool UpdateAvailable => UpdatedFiles.Count != 0;
	public string Message = string.Empty;
}

public sealed class UpdateChecker
{
	private readonly object _stateMutex = new();
	private bool _checking;

	public Dictionary<string, string> SavedVersions { get; private set; } = new();
	public List<ManagedDownloadFile> PendingUpdateFiles { get; private set; } = new();
	public bool UpdateAvailable { get; private set; }

	public UpdateChecker() => SavedVersions = VersionManager.LoadSavedVersions();

	public void ResetVersions()
	{
		SavedVersions = VersionManager.LoadSavedVersions();
		PendingUpdateFiles.Clear();
		UpdateAvailable = false;
	}

	public async Task<UpdateCheckResult?> CheckAsync(string logPrefix)
	{
		if (_checking ||
			!ManagedDownloads.CanCheckForUpdates() ||
			!ManagedDownloads.HasDownloadSource())
		{
			return null;
		}

		_checking = true;

		string repo = GitHubApi.GetRepositoryName();
		string branch = GitHubApi.GetRepositoryBranch();
		List<ManagedDownloadFile> managedFiles = ManagedDownloads.GetManagedDownloadFiles();

		var result = new UpdateCheckResult();

		foreach (ManagedDownloadFile file in managedFiles)
		{
			string sha = await GitHubApi.FetchLatestCommitShaForFileAsync(
				repo, branch, file.RepositoryRelativePath);
			if (sha.Length != 0)
			{
				result.LatestVersions[file.VersionKey] = sha;
			}
		}

		Dictionary<string, string> savedSnapshot = SavedVersions;

		foreach (ManagedDownloadFile file in managedFiles)
		{
			bool localFileExists = File.Exists(ManagedDownloads.GetInstalledManagedFilePath(file));
			result.LatestVersions.TryGetValue(file.VersionKey, out string? fetched);
			savedSnapshot.TryGetValue(file.VersionKey, out string? saved);

			string fetchedVersion = fetched ?? string.Empty;
			string savedVersion = saved ?? string.Empty;

			if (!localFileExists ||
				(fetchedVersion.Length != 0 && fetchedVersion != savedVersion))
			{
				result.UpdatedFiles.Add(file);
			}
		}

		PendingUpdateFiles = result.UpdatedFiles;
		UpdateAvailable = result.UpdateAvailable;
		_checking = false;

		if (result.UpdatedFiles.Count != 0)
		{
			result.Message = logPrefix + "Runtime update available for " +
							 result.UpdatedFiles.Count + " file(s).";
		}
		else if (result.LatestVersions.Count != 0)
		{
			result.Message = logPrefix + "Runtime files are up to date.";
		}

		return result;
	}

	public ManagedDownloadProgress? StartDownload(
		Dictionary<string, string> latestVersions, System.Action<bool> onSuccess)
	{
		List<ManagedDownloadFile> files = PendingUpdateFiles.Count != 0
			? new List<ManagedDownloadFile>(PendingUpdateFiles)
			: ManagedDownloads.GetManagedDownloadFiles();

		if (files.Count == 0)
		{
			return null;
		}

		var versionsSnapshot = new Dictionary<string, string>(latestVersions);
		var progress = new ManagedDownloadProgress();

		ManagedDownloader.DownloadManagedFilesAsync(files, progress, () =>
		{
			bool savedAnyVersion = false;
			lock (_stateMutex)
			{
				foreach (ManagedDownloadFile file in files)
				{
					if (versionsSnapshot.TryGetValue(file.VersionKey, out string? version) &&
						version.Length != 0)
					{
						SavedVersions[file.VersionKey] = version;
						savedAnyVersion = true;
					}
				}

				if (savedAnyVersion)
				{
					VersionManager.SaveCurrentVersions(SavedVersions);
				}

				PendingUpdateFiles.Clear();
			}

			UpdateAvailable = false;
			onSuccess(savedAnyVersion);
		});

		return progress;
	}
}
