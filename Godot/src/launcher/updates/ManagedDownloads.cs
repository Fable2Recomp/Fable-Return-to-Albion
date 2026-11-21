using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Refii.Launcher.Core;

/// <summary>Which of the project's published builds the launcher keeps the install on.</summary>
public enum UpdateChannel
{
	/// <summary>The newest numbered release (a beta).</summary>
	Release,

	/// <summary>The rolling nightly dev build.</summary>
	Nightly,
}

/// <summary>
/// One published build for this platform: the release it belongs to and the package to download.
/// </summary>
/// <param name="Tag">The release's tag (<c>beta-7</c>, <c>nightly</c>).</param>
/// <param name="Title">The release's name as published ("Beta 7"); the tag when it has none.</param>
/// <param name="AssetName">The package's file name.</param>
/// <param name="AssetUrl">Where the package downloads from.</param>
/// <param name="AssetSize">The package's size in bytes, 0 when not stated.</param>
/// <param name="AssetUpdatedAt">When the package was uploaded, as GitHub states it (ISO 8601).</param>
public sealed record ReleaseBuild(
	string Tag, string Title, string AssetName, string AssetUrl, long AssetSize, string AssetUpdatedAt)
{
	/// <summary>
	/// What identifies this build among every build of its channel: the nightly's tag is reused
	/// each night, so the tag alone does not.
	/// </summary>
	public string Stamp => Tag + "@" + AssetUpdatedAt;
}

/// <summary>
/// THE UPDATE'S CONTRACT. The project publishes its builds as GitHub releases: numbered betas, and
/// one rolling pre-release under the tag <c>nightly</c> (.github/workflows/build.yml), each
/// carrying one package per platform named <c>&lt;app&gt;-&lt;platform&gt;-&lt;arch&gt;.zip</c>.
/// The player picks a channel; the launcher takes that channel's newest build for this machine
/// and extracts it over the install (<see cref="Refii.Launcher.Updates.ReleaseInstaller"/>).
/// </summary>
public static class ReleaseUpdates
{
	/// <summary>The tag the rolling nightly dev build is published under.</summary>
	public const string NightlyTag = "nightly";

	private const string Section = "Updates";

	/// <summary>The channel the player chose; the numbered releases until they choose.</summary>
	public static UpdateChannel Channel
	{
		get => string.Equals(Ini.GetString("Channel", "release", Section), "nightly", StringComparison.OrdinalIgnoreCase)
			? UpdateChannel.Nightly
			: UpdateChannel.Release;
		set
		{
			Ini.SetString("Channel", value == UpdateChannel.Nightly ? "nightly" : "release", Section);
			Ini.Save();
		}
	}

	/// <summary>The <see cref="ReleaseBuild.Stamp"/> of the build the launcher last installed; empty before the first.</summary>
	public static string InstalledStamp
	{
		get => Ini.GetString("Installed", string.Empty, Section);
		set
		{
			Ini.SetString("Installed", value, Section);
			Ini.Save();
		}
	}

	/// <summary>The channel as the player reads it.</summary>
	public static string ChannelName(UpdateChannel channel) =>
		channel == UpdateChannel.Nightly ? "Nightly dev build" : "Release (beta)";

	/// <summary>How this machine's package name ends: <c>-linux-x86_64.zip</c>, <c>-windows-arm64.zip</c>.</summary>
	public static string AssetSuffix() =>
		AssetSuffix(OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture == Architecture.Arm64);

	/// <inheritdoc cref="AssetSuffix()"/>
	public static string AssetSuffix(bool windows, bool arm64) =>
		"-" + (windows ? "windows" : "linux") + "-" + (arm64 ? "arm64" : "x86_64") + ".zip";

	/// <summary>The releases listing the channels are read from.</summary>
	public static string ReleasesUrl(string repository) =>
		"https://api.github.com/repos/" + repository + "/releases?per_page=30";

	/// <summary>
	/// The channel's newest build for a machine, from GitHub's releases listing (newest first):
	/// the release tagged <see cref="NightlyTag"/> for the nightly channel, the first other
	/// release for the release channel, skipping a draft and a release with no package whose
	/// name ends in <paramref name="assetSuffix"/>. Null when the listing holds none.
	/// </summary>
	public static ReleaseBuild? Pick(string releasesJson, UpdateChannel channel, string assetSuffix)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(releasesJson);
			if (document.RootElement.ValueKind != JsonValueKind.Array)
			{
				return null;
			}

			foreach (JsonElement release in document.RootElement.EnumerateArray())
			{
				string tag = Text(release, "tag_name");
				bool nightly = string.Equals(tag, NightlyTag, StringComparison.Ordinal);
				if (tag.Length == 0 || nightly != (channel == UpdateChannel.Nightly) || Flag(release, "draft"))
				{
					continue;
				}

				if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				foreach (JsonElement asset in assets.EnumerateArray())
				{
					string name = Text(asset, "name");
					string url = Text(asset, "browser_download_url");
					if (url.Length == 0 || !name.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					string title = Text(release, "name");
					long size = asset.TryGetProperty("size", out JsonElement stated) && stated.ValueKind == JsonValueKind.Number
						? stated.GetInt64()
						: 0L;
					return new ReleaseBuild(tag, title.Length != 0 ? title : tag, name, url, size, Text(asset, "updated_at"));
				}
			}
		}
		catch (JsonException)
		{
		}

		return null;
	}

	/// <summary>How much later than the install's own build a package must be to count as another build.</summary>
	/// <remarks>A package is uploaded minutes after its executable is linked, by the same run.</remarks>
	public static readonly TimeSpan SameBuildWindow = TimeSpan.FromHours(2);

	/// <summary>
	/// Whether <paramref name="build"/> is not the build installed. An install the launcher has
	/// updated before knows its stamp exactly. One that was unpacked by hand has none, and is
	/// compared by time: the package against the install's executable
	/// (<paramref name="installedBuildTimeUtc"/>), newer when it is later by more than
	/// <see cref="SameBuildWindow"/>, and unknown times count as newer.
	/// </summary>
	public static bool IsNewer(ReleaseBuild build, string installedStamp, DateTime? installedBuildTimeUtc)
	{
		if (installedStamp.Length != 0)
		{
			return !string.Equals(build.Stamp, installedStamp, StringComparison.Ordinal);
		}

		if (installedBuildTimeUtc is not { } installed ||
			!DateTime.TryParse(build.AssetUpdatedAt, CultureInfo.InvariantCulture,
				DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime published))
		{
			return true;
		}

		return published - installed > SameBuildWindow;
	}

	private static string Text(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
		value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	private static bool Flag(JsonElement element, string name) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
		value.ValueKind == JsonValueKind.True;
}
