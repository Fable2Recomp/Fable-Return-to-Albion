using System;
using System.IO;
using System.Threading.Tasks;
using Refii.Launcher.Updates;

namespace Refii.Launcher.Core;

/// <summary>What one look at the project's releases found.</summary>
public sealed class UpdateCheckResult
{
	/// <summary>The chosen channel's newest build for this machine; null when there is none or the listing could not be read.</summary>
	public ReleaseBuild? Latest;

	/// <summary>Whether <see cref="Latest"/> is not the build installed.</summary>
	public bool UpdateAvailable;

	/// <summary>A line for the launcher's log.</summary>
	public string Message = string.Empty;
}

/// <summary>
/// Keeps the install on the player's chosen channel (<see cref="ReleaseUpdates"/>): looks at the
/// project's releases on every start and on request, and installs the channel's newest build.
/// </summary>
public sealed class UpdateChecker
{
	private bool _checking;

	/// <summary>The channel's newest build as last read; null before the first read or when none is published.</summary>
	public ReleaseBuild? Latest { get; private set; }

	/// <summary>Whether <see cref="Latest"/> is not the build installed.</summary>
	public bool UpdateAvailable { get; private set; }

	/// <summary>Whether the last look reached the releases at all.</summary>
	public bool Reached { get; private set; }

	/// <summary>Why the last look did not reach the releases, worded for the player; empty when it did.</summary>
	public string Failure { get; private set; } = string.Empty;

	/// <summary>An install the launcher can replace: a desktop package, never a phone's or the editor's project.</summary>
	public static bool CanUpdate => !OperatingSystem.IsAndroid() && !Godot.Engine.IsEditorHint() && !Godot.OS.HasFeature("editor");

	/// <summary>When this install's own executable was built: the time its package gave the file.</summary>
	private static DateTime? InstalledBuildTimeUtc()
	{
		try
		{
			string path = Godot.OS.GetExecutablePath();
			return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	public async Task<UpdateCheckResult?> CheckAsync(string logPrefix)
	{
		if (_checking || !CanUpdate)
		{
			return null;
		}

		_checking = true;
		var result = new UpdateCheckResult();
		try
		{
			UpdateChannel channel = ReleaseUpdates.Channel;
			string channelName = ReleaseUpdates.ChannelName(channel);
			(string? listing, string failure) = await GitHubApi.GetAsync(
				ReleaseUpdates.ReleasesUrl(GitHubApi.GetRepositoryName()));
			Reached = listing is not null;
			Failure = failure;
			if (listing is null)
			{
				result.Message = logPrefix + "the project's releases could not be read. " + failure;
			}
			else
			{
				result.Latest = ReleaseUpdates.Pick(listing, channel, ReleaseUpdates.AssetSuffix());
				result.UpdateAvailable = result.Latest is not null &&
					ReleaseUpdates.IsNewer(result.Latest, ReleaseUpdates.InstalledStamp, InstalledBuildTimeUtc());
				result.Message = logPrefix + (result.Latest is null
					? $"no {channelName} is published for this platform yet."
					: result.UpdateAvailable
						? $"{result.Latest.Title} is available ({channelName})."
						: $"up to date with {result.Latest.Title} ({channelName}).");
			}

			Latest = result.Latest;
			UpdateAvailable = result.UpdateAvailable;
		}
		finally
		{
			_checking = false;
		}

		return result;
	}

	/// <summary>
	/// Downloads and installs <see cref="Latest"/> on a worker; <paramref name="onInstalled"/> runs
	/// there once every file is in place. The caller then records it from the main thread
	/// (<see cref="MarkInstalled"/>): the config is not written from a worker. Null when there is
	/// nothing to install.
	/// </summary>
	public ManagedDownloadProgress? StartDownload(Action onInstalled)
	{
		if (Latest is not { } build || !CanUpdate)
		{
			return null;
		}

		var progress = new ManagedDownloadProgress();
		ReleaseInstaller.InstallAsync(build, Platform.GetExecutableDirectory(), progress, onInstalled);
		return progress;
	}

	/// <summary>Records <see cref="Latest"/> as the build installed. Main thread.</summary>
	public void MarkInstalled()
	{
		if (Latest is { } build)
		{
			ReleaseUpdates.InstalledStamp = build.Stamp;
		}

		UpdateAvailable = false;
	}
}
