using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Refii.Launcher.Core;

namespace Refii.Launcher.Updates;

public sealed class ManagedDownloadProgress
{
	private readonly object _mutex = new();
	private string _currentFileName = string.Empty;
	private string _errorMessage = string.Empty;

	public long BytesDownloaded;
	public long TotalBytes;
	public int CurrentFileIndex;
	public int TotalFiles;
	private int _isComplete;
	private int _hasError;

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

	public float GetPercentage()
	{
		long total = Interlocked.Read(ref TotalBytes);
		if (total == 0)
		{
			return 0.0f;
		}

		return (float)Interlocked.Read(ref BytesDownloaded) / total * 100.0f;
	}

	public void SetCurrentFileName(string fileName)
	{
		lock (_mutex)
		{
			_currentFileName = fileName;
		}
	}

	public string GetCurrentFileName()
	{
		lock (_mutex)
		{
			return _currentFileName;
		}
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

public static class ManagedDownloader
{
	private static readonly HttpClient Client = CreateClient();

	private static HttpClient CreateClient()
	{
		var client = new HttpClient();
		client.DefaultRequestHeaders.UserAgent.ParseAdd("refii-launcher");
		client.Timeout = Timeout.InfiniteTimeSpan;
		return client;
	}

	public static string BuildProgressButtonLabel(ManagedDownloadProgress progress)
	{
		string summary;
		if (Interlocked.Read(ref progress.TotalBytes) > 0)
		{
			summary = progress.GetPercentage().ToString("0.0") + "% downloaded";
		}
		else if (Volatile.Read(ref progress.TotalFiles) > 1)
		{
			summary = "File " + Volatile.Read(ref progress.CurrentFileIndex) +
					  " of " + Volatile.Read(ref progress.TotalFiles);
		}
		else
		{
			summary = "Connecting...";
		}

		return "Downloading Runtime Files...\n" + summary;
	}

	public static void DownloadManagedFilesAsync(
		IReadOnlyList<ManagedDownloadFile> files,
		ManagedDownloadProgress progress,
		Action? onSuccessCallback)
	{
		_ = Task.Run(async () =>
		{
			progress.IsComplete = false;
			progress.HasError = false;
			Interlocked.Exchange(ref progress.BytesDownloaded, 0);
			Interlocked.Exchange(ref progress.TotalBytes, 0);
			Volatile.Write(ref progress.CurrentFileIndex, 0);
			Volatile.Write(ref progress.TotalFiles, files.Count);
			progress.SetCurrentFileName(string.Empty);
			progress.SetErrorMessage(string.Empty);

			for (int index = 0; index < files.Count; index++)
			{
				ManagedDownloadFile file = files[index];
				Volatile.Write(ref progress.CurrentFileIndex, index + 1);
				Interlocked.Exchange(ref progress.BytesDownloaded, 0);
				Interlocked.Exchange(ref progress.TotalBytes, 0);
				progress.SetCurrentFileName(Path.GetFileName(file.LocalRelativePath));

				string destinationPath = ManagedDownloads.GetInstalledManagedFilePath(file);
				string temporaryPath = destinationPath + ".download";

				string url = ManagedDownloads.BuildRawDownloadUrl(file.RepositoryRelativePath);
				string error = await DownloadFileAsync(url, temporaryPath, progress);
				if (error.Length != 0)
				{
					TryDelete(temporaryPath);
					progress.HasError = true;
					progress.SetErrorMessage(error.Length == 0 ? "Download failed" : error);
					progress.IsComplete = true;
					return;
				}

				if (!ReplaceDownloadedFile(temporaryPath, destinationPath, out string replaceError))
				{
					TryDelete(temporaryPath);
					progress.HasError = true;
					progress.SetErrorMessage(
						replaceError.Length == 0 ? "Failed to replace file" : replaceError);
					progress.IsComplete = true;
					return;
				}
			}

			progress.IsComplete = true;
			onSuccessCallback?.Invoke();
		});
	}

	private static async Task<string> DownloadFileAsync(
		string url, string temporaryPath, ManagedDownloadProgress progress)
	{
		try
		{
			string? parent = Path.GetDirectoryName(temporaryPath);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent!);
			}

			using HttpResponseMessage response = await Client.GetAsync(
				url, HttpCompletionOption.ResponseHeadersRead);
			if (!response.IsSuccessStatusCode)
			{
				return "HTTP " + (int)response.StatusCode + " for " + url;
			}

			Interlocked.Exchange(
				ref progress.TotalBytes, response.Content.Headers.ContentLength ?? 0);

			await using Stream source = await response.Content.ReadAsStreamAsync();
			await using var destination = new FileStream(
				temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

			var buffer = new byte[1 << 16];
			long downloaded = 0;
			while (true)
			{
				int read = await source.ReadAsync(buffer);
				if (read <= 0)
				{
					break;
				}

				await destination.WriteAsync(buffer.AsMemory(0, read));
				downloaded += read;
				Interlocked.Exchange(ref progress.BytesDownloaded, downloaded);
			}

			return string.Empty;
		}
		catch (Exception exception)
		{
			return exception.Message;
		}
	}

	private static bool ReplaceDownloadedFile(
		string temporaryPath, string destinationPath, out string error)
	{
		if (!SafePath.IsSafeDestinationUnderBase(
				destinationPath, Platform.GetExecutableDirectory(), out error))
		{
			return false;
		}

		try
		{
			string? parent = Path.GetDirectoryName(destinationPath);
			if (!string.IsNullOrEmpty(parent))
			{
				Directory.CreateDirectory(parent!);
			}

			if (File.Exists(destinationPath))
			{
				File.Delete(destinationPath);
			}

			File.Move(temporaryPath, destinationPath);
			error = string.Empty;
			return true;
		}
		catch (Exception)
		{
			try
			{
				File.Copy(temporaryPath, destinationPath, true);
				File.Delete(temporaryPath);
				error = string.Empty;
				return true;
			}
			catch (Exception exception)
			{
				error = exception.Message;
				return false;
			}
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
}
