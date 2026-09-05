using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace Refii.Launcher.Editing;

/// <summary>
/// Fetches the Godot editor the modding project opens in.
///
/// THE CURRENT STABLE FOR THIS OS, NOT A PINNED BUILD. The plugins are plugins and the project
/// declares the lowest Godot it opens in, so anything at or above that floor works; a newer
/// editor opening an older project is Godot's own supported upgrade path, while an older one
/// refuses it. Only the floor is pinned.
///
/// IT MUST BE THE .NET BUILD. The project is C# — the plain editor cannot open it at all, which
/// is why the flavour is part of the asset name rather than a preference.
///
/// NOTHING IS UNPACKED BEFORE IT IS VERIFIED. The archive is checked against the checksum file
/// the same release publishes; a release without one fails the install rather than being
/// trusted, because an unverified editor is a binary this launcher would then execute.
/// </summary>
public static class EditorInstall
{
	private const string ReleasesUrl = "https://api.github.com/repos/godotengine/godot/releases";

	/// <summary>
	/// Microsoft's own release index. Resolving it ourselves means the only thing downloaded and
	/// EXECUTED is an editor we checksummed — the alternative, their `dotnet-install` script, is
	/// documented but runs a downloaded shell script to get there.
	/// </summary>
	private const string DotnetIndexUrl =
		"https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";

	private static readonly HttpClient Client = CreateClient();

	private static HttpClient CreateClient()
	{
		var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
		client.DefaultRequestHeaders.Add("User-Agent", "refii-launcher");
		return client;
	}

	/// <summary>
	/// Downloads, verifies and unpacks the editor, reporting each step.
	///
	/// Safe to re-run: an install that already succeeded re-verifies rather than re-downloading.
	/// </summary>
	public static async Task<(bool Ok, string Error)> RunAsync(Action<string> report)
	{
		EditorPins pins = EditorWorkspace.ReadPins();

		if (EditorWorkspace.FindEditor() is { } existing)
		{
			report("Checking the editor already downloaded...");
			return await VerifyAsync(existing, report).ConfigureAwait(false);
		}

		report("Looking up the current Godot release...");
		(string? version, string? archiveUrl, string? sumsUrl, string lookupError) =
			await ResolveReleaseAsync(pins).ConfigureAwait(false);
		if (version is null || archiveUrl is null)
		{
			return (false, lookupError);
		}

		if (sumsUrl is null)
		{
			return (false,
				$"Godot {version} publishes no {pins.ChecksumAsset}, so the download cannot be " +
				"verified. Install the editor by hand, or try again when the release is complete.");
		}

		string archiveName = EditorWorkspace.ArchiveNameFor(version, pins)!;
		Directory.CreateDirectory(EditorWorkspace.GodotRoot);
		string archivePath = Path.Combine(EditorWorkspace.GodotRoot, archiveName);

		try
		{
			report($"Downloading Godot {version} for this computer...");
			await DownloadAsync(archiveUrl, archivePath).ConfigureAwait(false);

			report("Checking the download...");
			string? expected = await FindChecksumAsync(sumsUrl, archiveName).ConfigureAwait(false);
			if (expected is null)
			{
				File.Delete(archivePath);
				return (false,
					$"{archiveName} is not listed in {pins.ChecksumAsset}, so the download " +
					"cannot be verified.");
			}

			string actual = Sha512(archivePath);
			if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
			{
				File.Delete(archivePath);
				return (false,
					"The download does not match the checksum Godot published. Nothing was " +
					"installed. Check the connection and try again.");
			}

			report("Unpacking...");
			ZipFile.ExtractToDirectory(archivePath, EditorWorkspace.GodotRoot, overwriteFiles: true);
			File.Delete(archivePath);
		}
		catch (Exception exception)
		{
			return (false, exception.Message);
		}

		string? editor = EditorWorkspace.FindEditor();
		if (editor is null)
		{
			return (false, "The archive unpacked but no editor was found inside it.");
		}

		MakeExecutable(editor);

		if (EditorWorkspace.FindDotnet() is null)
		{
			report($"Downloading the .NET {pins.DotnetMajor} SDK...");
			(bool sdkOk, string sdkError) = await InstallDotnetAsync(pins, report).ConfigureAwait(false);
			if (!sdkOk)
			{
				return (false, sdkError);
			}
		}

		report("Checking the editor runs...");
		return await VerifyAsync(editor, report).ConfigureAwait(false);
	}

	/// <summary>
	/// The newest stable release at or above the floor, with the assets this host needs.
	///
	/// Pre-releases are skipped: a player opening the modding editor wants the editor everyone
	/// else has, not a release candidate.
	/// </summary>
	private static async Task<(string? Version, string? Archive, string? Sums, string Error)>
		ResolveReleaseAsync(EditorPins pins)
	{
		string body;
		try
		{
			body = await Client.GetStringAsync(ReleasesUrl).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			return (null, null, null, $"Could not reach GitHub: {exception.Message}");
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(body);
			foreach (JsonElement release in document.RootElement.EnumerateArray())
			{
				if (Flag(release, "draft") || Flag(release, "prerelease"))
				{
					continue;
				}

				string tag = Text(release, "tag_name");
				if (!tag.EndsWith("-stable", StringComparison.OrdinalIgnoreCase) ||
					!EditorWorkspace.MeetsFloor(tag, pins.GodotFloor))
				{
					continue;
				}

				string? wanted = EditorWorkspace.ArchiveNameFor(tag, pins);
				if (wanted is null)
				{
					return (null, null, null,
						"There is no Godot build for this operating system and processor.");
				}

				string? archive = null;
				string? sums = null;
				if (release.TryGetProperty("assets", out JsonElement assets))
				{
					foreach (JsonElement asset in assets.EnumerateArray())
					{
						string name = Text(asset, "name");
						if (name == wanted)
						{
							archive = Text(asset, "browser_download_url");
						}
						else if (name == pins.ChecksumAsset)
						{
							sums = Text(asset, "browser_download_url");
						}
					}
				}

				if (archive is not null)
				{
					return (tag, archive, sums, string.Empty);
				}
			}
		}
		catch (Exception exception)
		{
			return (null, null, null, $"Could not read the release list: {exception.Message}");
		}

		return (null, null, null,
			$"No Godot release at or above {pins.GodotFloor} has a .NET build for this computer.");
	}

	/// <summary>
	/// Fetches a .NET SDK into <c>tools/dotnet</c>, checksum-verified.
	///
	/// The release index names a channel per major version; the channel names its current SDK and
	/// every file it ships, each with a SHA-512. Nothing is unpacked before that matches.
	/// </summary>
	private static async Task<(bool Ok, string Error)> InstallDotnetAsync(
		EditorPins pins, Action<string> report)
	{
		string? rid = RuntimeIdentifier();
		if (rid is null)
		{
			return (false, "There is no .NET SDK build for this operating system and processor.");
		}

		string? channelUrl;
		try
		{
			string index = await Client.GetStringAsync(DotnetIndexUrl).ConfigureAwait(false);
			channelUrl = FindChannel(index, pins.DotnetMajor);
		}
		catch (Exception exception)
		{
			return (false, $"Could not reach Microsoft's release index: {exception.Message}");
		}

		if (channelUrl is null)
		{
			return (false, $"Microsoft's release index lists no .NET {pins.DotnetMajor} channel.");
		}

		string? url;
		string? expected;
		string? name;
		try
		{
			string channel = await Client.GetStringAsync(channelUrl).ConfigureAwait(false);
			(url, expected, name) = FindSdkFile(channel, rid);
		}
		catch (Exception exception)
		{
			return (false, $"Could not read the .NET {pins.DotnetMajor} channel: {exception.Message}");
		}

		if (url is null || name is null)
		{
			return (false, $"The .NET {pins.DotnetMajor} channel ships no SDK for {rid}.");
		}

		if (expected is null)
		{
			return (false,
				$"The .NET {pins.DotnetMajor} SDK for {rid} publishes no checksum, so it cannot " +
				"be verified. Install .NET by hand and press Open Editor again.");
		}

		Directory.CreateDirectory(EditorWorkspace.DotnetRoot);
		string archive = Path.Combine(EditorWorkspace.DotnetRoot, name);

		try
		{
			await DownloadAsync(url, archive).ConfigureAwait(false);

			report("Checking the .NET download...");
			if (!string.Equals(Sha512(archive), expected, StringComparison.OrdinalIgnoreCase))
			{
				File.Delete(archive);
				return (false,
					"The .NET SDK download does not match the checksum Microsoft published. " +
					"Nothing was installed.");
			}

			report("Unpacking .NET...");
			Unpack(archive, EditorWorkspace.DotnetRoot);
			File.Delete(archive);
		}
		catch (Exception exception)
		{
			return (false, exception.Message);
		}

		string dotnet = Path.Combine(
			EditorWorkspace.DotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
		if (!File.Exists(dotnet))
		{
			return (false, "The .NET archive unpacked but no dotnet was found inside it.");
		}

		MakeExecutable(dotnet);
		return (true, string.Empty);
	}

	/// <summary>The channel whose <c>channel-version</c> is this major.</summary>
	private static string? FindChannel(string index, string major)
	{
		using JsonDocument document = JsonDocument.Parse(index);
		if (!document.RootElement.TryGetProperty("releases-index", out JsonElement channels))
		{
			return null;
		}

		foreach (JsonElement channel in channels.EnumerateArray())
		{
			string version = Text(channel, "channel-version");
			if (version == major || version.StartsWith(major + ".", StringComparison.Ordinal))
			{
				string url = Text(channel, "releases.json");
				if (url.Length != 0)
				{
					return url;
				}
			}
		}

		return null;
	}

	/// <summary>The current SDK's file for this host, with the hash it must match.</summary>
	private static (string? Url, string? Hash, string? Name) FindSdkFile(string channel, string rid)
	{
		using JsonDocument document = JsonDocument.Parse(channel);
		JsonElement root = document.RootElement;
		string latest = Text(root, "latest-sdk");

		if (!root.TryGetProperty("releases", out JsonElement releases))
		{
			return (null, null, null);
		}

		foreach (JsonElement release in releases.EnumerateArray())
		{
			if (!release.TryGetProperty("sdk", out JsonElement sdk))
			{
				continue;
			}

			if (latest.Length != 0 && Text(sdk, "version") != latest)
			{
				continue;
			}

			if (!sdk.TryGetProperty("files", out JsonElement files))
			{
				continue;
			}

			foreach (JsonElement file in files.EnumerateArray())
			{
				string name = Text(file, "name");
				if (Text(file, "rid") != rid ||
					!(name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
					  name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
				{
					continue;
				}

				return (Text(file, "url"), Text(file, "hash"), name);
			}
		}

		return (null, null, null);
	}

	private static string? RuntimeIdentifier() =>
		(OperatingSystem.IsWindows(), System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) switch
		{
			(true, System.Runtime.InteropServices.Architecture.X64) => "win-x64",
			(true, System.Runtime.InteropServices.Architecture.Arm64) => "win-arm64",
			(false, System.Runtime.InteropServices.Architecture.X64) => "linux-x64",
			(false, System.Runtime.InteropServices.Architecture.Arm64) => "linux-arm64",
			_ => null,
		};

	/// <summary>Zip on Windows, tarball everywhere else — the shapes Microsoft ships.</summary>
	private static void Unpack(string archive, string destination)
	{
		if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
		{
			ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
			return;
		}

		var tar = new ProcessStartInfo { FileName = "tar", UseShellExecute = false };
		tar.ArgumentList.Add("-xzf");
		tar.ArgumentList.Add(archive);
		tar.ArgumentList.Add("-C");
		tar.ArgumentList.Add(destination);

		using Process? process = Process.Start(tar);
		process?.WaitForExit();
		if (process is null || process.ExitCode != 0)
		{
			throw new IOException("The .NET archive could not be unpacked.");
		}
	}

	/// <summary>The checksum for one file, from a <c>&lt;hash&gt;  &lt;name&gt;</c> list.</summary>
	private static async Task<string?> FindChecksumAsync(string url, string archiveName)
	{
		string sums;
		try
		{
			sums = await Client.GetStringAsync(url).ConfigureAwait(false);
		}
		catch (Exception)
		{
			return null;
		}

		foreach (string line in sums.Split('\n'))
		{
			string trimmed = line.Trim();
			int split = trimmed.LastIndexOf(' ');
			if (split <= 0)
			{
				continue;
			}

			string name = trimmed[(split + 1)..].TrimStart('*');
			if (name.Equals(archiveName, StringComparison.OrdinalIgnoreCase))
			{
				return trimmed[..split].Trim();
			}
		}

		return null;
	}

	private static async Task DownloadAsync(string url, string destination)
	{
		using HttpResponseMessage response = await Client
			.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
			.ConfigureAwait(false);
		response.EnsureSuccessStatusCode();

		await using Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
		await using var file = new FileStream(
			destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
		await source.CopyToAsync(file).ConfigureAwait(false);
	}

	private static string Sha512(string path)
	{
		using FileStream stream = File.OpenRead(path);
		return Convert.ToHexString(SHA512.HashData(stream)).ToLowerInvariant();
	}

	private static void MakeExecutable(string path)
	{
		if (OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			File.SetUnixFileMode(
				path,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
				UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
				UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		}
		catch (Exception)
		{
		}
	}

	/// <summary>
	/// Runs the editor once, headless, so a corrupt unpack fails here rather than on the
	/// player's first click.
	/// </summary>
	private static async Task<(bool Ok, string Error)> VerifyAsync(string editor, Action<string> report)
	{
		try
		{
			var start = new ProcessStartInfo
			{
				FileName = editor,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			start.ArgumentList.Add("--headless");
			start.ArgumentList.Add("--version");

			using Process? process = Process.Start(start);
			if (process is null)
			{
				return (false, "The editor could not be started.");
			}

			string version = (await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false)).Trim();
			await process.WaitForExitAsync().ConfigureAwait(false);

			if (process.ExitCode != 0 || version.Length == 0)
			{
				return (false, "The editor did not run. The download may be incomplete.");
			}

			report($"Godot {version} is ready.");
			return (true, string.Empty);
		}
		catch (Exception exception)
		{
			return (false, exception.Message);
		}
	}

	private static string Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	private static bool Flag(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
