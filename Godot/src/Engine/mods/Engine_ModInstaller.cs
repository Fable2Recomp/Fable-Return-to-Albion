using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.Mods;

/// <summary>
/// What inspecting or installing one <c>.f2mod</c> found. <see cref="Error"/> is empty when the
/// package is acceptable (inspect) or was installed (install).
/// </summary>
/// <param name="Name">The install name: the package's file stem. It is the name in
/// <c>loadorder.toml</c> and the name other mods' <c>requires</c> refer to.</param>
/// <param name="Title">The package metadata's title; the name when it states none.</param>
/// <param name="Version">The package metadata's version; empty when it states none.</param>
/// <param name="Path">The installed package, or for an inspect or download the package inspected.</param>
/// <param name="HardMod">The package carries compiled native sources (<c>native/*.cpp</c> and
/// kin): it is compiled into the game on this machine once the player enables and allows it.</param>
/// <param name="NativeSources">Every entry under <c>native/</c>.</param>
/// <param name="MissingDependencies">Each unmet <c>requires</c>, in <see cref="ModDependencies"/>' words.</param>
/// <param name="Replaces">An installed mod of the same name is (or was) replaced; its file is kept as <c>&lt;name&gt;.f2mod.bak</c>.</param>
public sealed record ModInstallResult(
	string Name,
	string Title,
	string Version,
	string Path,
	bool HardMod,
	IReadOnlyList<string> NativeSources,
	IReadOnlyList<string> MissingDependencies,
	IReadOnlyList<string> Warnings,
	string Error,
	bool Replaces)
{
	public bool Ok => Error.Length == 0;

	public static ModInstallResult Failed(string error, string name = "", string path = "") =>
		new(name, name, string.Empty, path, false, Array.Empty<string>(), Array.Empty<string>(),
			Array.Empty<string>(), error, false);
}

/// <summary>
/// Puts a shared mod package into the mods folder without the dev setup: from a file, or from an
/// https link.
///
/// THE PACKAGE IS VALIDATED AS IT WILL BE INSTALLED. A file is copied into the mods folder's
/// <c>.cache/install/</c> first and that copy is the one checked and then moved into place, so
/// what was checked is what is installed. A download lands in <c>.cache/downloads/</c>; nothing
/// here touches the system temp folder.
///
/// AN INSTALL NEVER TURNS A MOD ON. It is registered in <c>loadorder.toml</c> disabled — a
/// replaced mod too — and the player enables it in the Mods panel, where a hard mod's native code
/// is additionally gated on consent to its exact sources (<c>NativeModConsent</c>).
///
/// Refused: anything that is not a <c>.f2mod</c>; a package over <see cref="MaxPackageBytes"/>;
/// an entry with an absolute path, a drive, a <c>.</c>/<c>..</c> component or a control
/// character; an entry whose bytes lie outside the file; a link that is not https, or that
/// redirects more than <see cref="MaxRedirects"/> times or to anything but https.
/// </summary>
public static class ModInstaller
{
	public const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;
	public const int MaxRedirects = 5;
	public const string BackupExtension = ".bak";

	private const string CacheFolder = ".cache";
	private const string DownloadsFolder = "downloads";
	private const string StagingFolder = "install";
	private const string PartialExtension = ".download";
	private const int MaxNameLength = 64;

	private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

	private static readonly HttpClient Client = CreateClient();

	private static HttpClient CreateClient()
	{
		// Redirects are followed by hand so each hop is counted and held to https.
		var handler = new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(30),
		};
		var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("refii-launcher");
		return client;
	}

	/// <summary>The app's mods folder (<c>assets/mods</c>).</summary>
	public static string DefaultModsRoot => Refii.Assets.Engine_GameAssets.ModsRoot;

	/// <summary><c>&lt;mods&gt;/.cache/downloads</c>: where a link's package lands before it is installed.</summary>
	public static string DownloadsDirectory(string modsRoot) =>
		System.IO.Path.Combine(modsRoot, CacheFolder, DownloadsFolder);

	/// <summary>
	/// Everything an install would report about <paramref name="packagePath"/> — validity, hard
	/// mod, native sources, unmet requirements, replacement — without writing anything.
	/// </summary>
	public static ModInstallResult Inspect(string packagePath, string? modsRoot = null)
	{
		string root = modsRoot ?? DefaultModsRoot;
		string name = System.IO.Path.GetFileNameWithoutExtension(packagePath ?? string.Empty);
		string refusal = CheckFile(packagePath ?? string.Empty, name);
		return refusal.Length != 0
			? ModInstallResult.Failed(refusal, name, packagePath ?? string.Empty)
			: InspectPackage(packagePath!, name, root);
	}

	/// <summary>
	/// Installs <paramref name="packagePath"/> into the mods folder as <c>&lt;name&gt;.f2mod</c>,
	/// keeping a same-named installed package as <c>&lt;name&gt;.f2mod.bak</c>, and registers it
	/// in the load order disabled.
	/// </summary>
	public static ModInstallResult InstallFromFile(string packagePath, string? modsRoot = null)
	{
		string root = modsRoot ?? DefaultModsRoot;
		if (string.IsNullOrEmpty(root))
		{
			return ModInstallResult.Failed("No mods folder is configured.");
		}

		string name = System.IO.Path.GetFileNameWithoutExtension(packagePath ?? string.Empty);
		string refusal = CheckFile(packagePath ?? string.Empty, name);
		if (refusal.Length != 0)
		{
			return ModInstallResult.Failed(refusal, name, packagePath ?? string.Empty);
		}

		string destination = System.IO.Path.Combine(root, name + ModWorkspace.PackageExtension);
		string source = System.IO.Path.GetFullPath(packagePath!);
		bool inPlace = string.Equals(source, System.IO.Path.GetFullPath(destination),
			OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

		string staged = inPlace
			? destination
			: System.IO.Path.Combine(root, CacheFolder, StagingFolder, name + ModWorkspace.PackageExtension);
		try
		{
			if (!inPlace)
			{
				Directory.CreateDirectory(System.IO.Path.GetDirectoryName(staged)!);
				File.Copy(source, staged, overwrite: true);
			}
		}
		catch (Exception exception)
		{
			TryDelete(inPlace ? null : staged);
			return ModInstallResult.Failed("Could not copy the package: " + exception.Message, name, source);
		}

		ModInstallResult checkedCopy = InspectPackage(staged, name, root);
		if (!checkedCopy.Ok)
		{
			TryDelete(inPlace ? null : staged);
			return checkedCopy with { Path = source };
		}

		var warnings = checkedCopy.Warnings.ToList();
		if (!inPlace)
		{
			string backup = destination + BackupExtension;
			bool backedUp = false;
			try
			{
				if (File.Exists(destination))
				{
					File.Move(destination, backup, overwrite: true);
					backedUp = true;
				}

				File.Move(staged, destination);
			}
			catch (Exception exception)
			{
				if (backedUp && !File.Exists(destination))
				{
					try { File.Move(backup, destination); } catch (Exception) { }
				}

				TryDelete(staged);
				return checkedCopy with
				{
					Path = source,
					Error = "Could not put the package in the mods folder: " + exception.Message,
				};
			}
		}

		// Registered disabled; a replaced mod is turned off too, so the player re-enables what
		// they have now been given.
		ModLoadOrder order = ModLoadOrder.Read(root);
		order.Enabled.RemoveAll(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
		order.RegisterMod(name, enabled: false);
		ModDependencies.Sort(order, ModWorkspace.DiscoverModInstalls(root), out _);
		if (!order.Write(root, out string orderError))
		{
			warnings.Add("The load order could not be written: " + orderError);
		}

		return checkedCopy with { Path = destination, Warnings = warnings };
	}

	/// <summary>
	/// Downloads an https link to <c>&lt;mods&gt;/.cache/downloads/&lt;name&gt;.f2mod</c> and
	/// inspects it, for a caller that shows the player what it is before installing it with
	/// <see cref="InstallFromFile"/>. The file name comes from the response's
	/// <c>Content-Disposition</c>, else the final URL's last segment, and must end in
	/// <c>.f2mod</c>. <paramref name="progress"/> gets the fraction done, or -1 while the size is
	/// unknown.
	/// </summary>
	public static async Task<ModInstallResult> DownloadAsync(
		string url, IProgress<float>? progress, CancellationToken cancel = default, string? modsRoot = null)
	{
		string root = modsRoot ?? DefaultModsRoot;
		if (string.IsNullOrEmpty(root))
		{
			return ModInstallResult.Failed("No mods folder is configured.");
		}

		if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out Uri? current) ||
			current.Scheme != Uri.UriSchemeHttps)
		{
			return ModInstallResult.Failed("Only https:// links can be installed.");
		}

		string partial = string.Empty;
		try
		{
			HttpResponseMessage? response = null;
			for (int hop = 0; ; hop++)
			{
				using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
				headers.CancelAfter(HeaderTimeout);
				using var request = new HttpRequestMessage(HttpMethod.Get, current);
				response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token)
					.ConfigureAwait(false);

				if (!IsRedirect(response.StatusCode))
				{
					break;
				}

				Uri? location = response.Headers.Location;
				response.Dispose();
				if (hop >= MaxRedirects)
				{
					return ModInstallResult.Failed($"The link redirects more than {MaxRedirects} times.");
				}

				if (location is null)
				{
					return ModInstallResult.Failed("The link redirects without saying where.");
				}

				current = location.IsAbsoluteUri ? location : new Uri(current, location);
				if (current.Scheme != Uri.UriSchemeHttps)
				{
					return ModInstallResult.Failed("The link redirects to an address that is not https.");
				}
			}

			using (response)
			{
				if (!response.IsSuccessStatusCode)
				{
					return ModInstallResult.Failed(
						$"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
				}

				string fileName = DownloadFileName(response, current);
				string name = System.IO.Path.GetFileNameWithoutExtension(fileName);
				if (!fileName.EndsWith(ModWorkspace.PackageExtension, StringComparison.OrdinalIgnoreCase))
				{
					return ModInstallResult.Failed(
						$"'{fileName}' is not a mod package — only {ModWorkspace.PackageExtension} files are installed.");
				}

				string nameRefusal = CheckName(name);
				if (nameRefusal.Length != 0)
				{
					return ModInstallResult.Failed(nameRefusal, name);
				}

				long length = response.Content.Headers.ContentLength ?? -1;
				if (length > MaxPackageBytes)
				{
					return ModInstallResult.Failed(TooLarge(length), name);
				}

				string downloads = DownloadsDirectory(root);
				Directory.CreateDirectory(downloads);
				string final = System.IO.Path.Combine(downloads, name + ModWorkspace.PackageExtension);
				partial = final + PartialExtension;

				using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
				await using (Stream body = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
				await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
				{
					var buffer = new byte[1 << 16];
					long written = 0;
					float reported = float.NaN;
					progress?.Report(length > 0 ? 0f : -1f);
					while (true)
					{
						stall.CancelAfter(StallTimeout);
						int read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
						if (read == 0)
						{
							break;
						}

						written += read;
						if (written > MaxPackageBytes)
						{
							throw new InvalidDataException(TooLarge(written));
						}

						await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
						if (length > 0)
						{
							float fraction = (float)((double)written / length);
							if (float.IsNaN(reported) || fraction - reported >= 0.01f)
							{
								reported = fraction;
								progress?.Report(Math.Min(fraction, 1f));
							}
						}
					}

					if (length > 0 && written != length)
					{
						throw new InvalidDataException("The download ended early.");
					}
				}

				File.Move(partial, final, overwrite: true);
				partial = string.Empty;
				progress?.Report(1f);
				return Inspect(final, root);
			}
		}
		catch (OperationCanceledException) when (cancel.IsCancellationRequested)
		{
			return ModInstallResult.Failed("The download was cancelled.");
		}
		catch (OperationCanceledException)
		{
			return ModInstallResult.Failed("The server stopped answering.");
		}
		catch (Exception exception)
		{
			return ModInstallResult.Failed("The download failed: " + exception.Message);
		}
		finally
		{
			TryDelete(partial.Length == 0 ? null : partial);
		}
	}

	/// <summary>
	/// <see cref="DownloadAsync"/> then <see cref="InstallFromFile"/>, for a caller that has
	/// nothing to show the player in between; the download is removed either way.
	/// </summary>
	public static async Task<ModInstallResult> InstallFromUrlAsync(
		string url, IProgress<float>? progress, CancellationToken cancel = default, string? modsRoot = null)
	{
		string root = modsRoot ?? DefaultModsRoot;
		ModInstallResult downloaded = await DownloadAsync(url, progress, cancel, root).ConfigureAwait(false);
		if (!downloaded.Ok)
		{
			DiscardDownload(downloaded.Path, root);
			return downloaded;
		}

		ModInstallResult installed = InstallFromFile(downloaded.Path, root);
		DiscardDownload(downloaded.Path, root);
		return installed;
	}

	/// <summary>Deletes a file only when it lies in <see cref="DownloadsDirectory"/>.</summary>
	public static void DiscardDownload(string path, string? modsRoot = null)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		string downloads = System.IO.Path.GetFullPath(DownloadsDirectory(modsRoot ?? DefaultModsRoot))
			+ System.IO.Path.DirectorySeparatorChar;
		string full = System.IO.Path.GetFullPath(path);
		if (full.StartsWith(downloads, StringComparison.Ordinal))
		{
			TryDelete(full);
		}
	}

	/// <summary>The words the launcher shows before an install: hard mod first, then needs, then warnings.</summary>
	public static string Describe(ModInstallResult result)
	{
		var text = new StringBuilder();
		string version = result.Version.Length == 0 ? "no version stated" : "version " + result.Version;
		text.Append($"Install '{result.Title}' ({version}) as '{result.Name}'?");
		if (result.Replaces)
		{
			text.Append($"\n\nThis replaces the installed '{result.Name}'. The old package is kept as " +
				$"{result.Name}{ModWorkspace.PackageExtension}{BackupExtension}.");
		}

		if (result.HardMod)
		{
			text.Append("\n\nHARD MOD — this package contains native code. It is compiled into the game " +
				"on this computer and runs with the game's full access to your system. Install it only " +
				"if you trust its author. Native files:");
			foreach (string file in result.NativeSources.Take(12))
			{
				text.Append("\n  • ").Append(file);
			}

			if (result.NativeSources.Count > 12)
			{
				text.Append($"\n  • … and {result.NativeSources.Count - 12} more");
			}
		}

		if (result.MissingDependencies.Count != 0)
		{
			text.Append("\n\nIt needs mods you do not have enabled:");
			foreach (string need in result.MissingDependencies)
			{
				text.Append("\n  • ").Append(need);
			}
		}

		foreach (string warning in result.Warnings)
		{
			text.Append("\n\n").Append(warning);
		}

		text.Append("\n\nIt is added to the list disabled; tick it there to turn it on" +
			(result.HardMod ? ", which asks you to allow its native code." : "."));
		return text.ToString();
	}

	// ---------------------------------------------------------------------------------------

	/// <summary>The checks that need only the file: exists, is a .f2mod, under the cap, a usable name.</summary>
	private static string CheckFile(string packagePath, string name)
	{
		if (packagePath.Length == 0 || !File.Exists(packagePath))
		{
			return $"No file at '{packagePath}'.";
		}

		if (!packagePath.EndsWith(ModWorkspace.PackageExtension, StringComparison.OrdinalIgnoreCase))
		{
			return $"'{System.IO.Path.GetFileName(packagePath)}' is not a mod package — only " +
				$"{ModWorkspace.PackageExtension} files are installed.";
		}

		long length;
		try
		{
			length = new FileInfo(packagePath).Length;
		}
		catch (Exception exception)
		{
			return exception.Message;
		}

		return length > MaxPackageBytes ? TooLarge(length) : CheckName(name);
	}

	/// <summary>
	/// An install name is joined to the mods root and written into <c>loadorder.toml</c>: letters,
	/// digits, space, <c>_ - .</c>, not starting with a dot, and never refii's own chain owner name.
	/// </summary>
	private static string CheckName(string name)
	{
		if (name.Length == 0 || name.Length > MaxNameLength || name[0] == '.' ||
			!ModWorkspace.IsSafeModFolderName(name) ||
			name.Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ')))
		{
			return $"'{name}' cannot be a mod name: use up to {MaxNameLength} letters, digits, spaces, " +
				"'_', '-' or '.', not starting with '.'. Rename the file and try again.";
		}

		return string.Equals(name, ModNative.RefiiOwner, StringComparison.OrdinalIgnoreCase)
			? $"'{name}' is reserved for the game's own hooks."
			: string.Empty;
	}

	private static ModInstallResult InspectPackage(string packagePath, string name, string modsRoot)
	{
		if (!F2ModPackage.TryOpen(packagePath, out F2ModPackage? package, out string openError) || package is null)
		{
			return ModInstallResult.Failed("Not a readable mod package: " + openError, name, packagePath);
		}

		using (package)
		{
			long length = new FileInfo(packagePath).Length;
			var warnings = new List<string>();
			var native = new List<string>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			bool hard = false;

			foreach (F2ModEntry entry in package.Entries)
			{
				string refusal = CheckEntry(entry, length);
				if (refusal.Length != 0)
				{
					return ModInstallResult.Failed(
						$"Refused: the package's entry '{Printable(entry.Path)}' {refusal}.", name, packagePath);
				}

				string path = ModLoadOrder.Normalize(entry.Path);
				if (!seen.Add(path))
				{
					warnings.Add($"The package lists '{path}' more than once; the last one is used.");
				}

				if (ModNative.IsNativePath(path))
				{
					native.Add(path);
					hard |= ModNative.IsCompiled(path);
				}
			}

			if (package.Entries.Count == 0)
			{
				warnings.Add("The package is empty.");
			}

			ModMetadata meta = package.Metadata;
			if (meta.Version.Length == 0)
			{
				warnings.Add("The package states no version.");
			}

			// MOD JSON VALIDATION: ModValidate.Validate(path, text) per .json entry, as warnings.
			warnings.AddRange(ValidateJson(package));

			List<ModInstall> installed = ModWorkspace.DiscoverModInstalls(modsRoot);
			string self = System.IO.Path.GetFullPath(packagePath);
			bool replaces = installed.Any(mod =>
				string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase) &&
				System.IO.Path.GetFullPath(mod.PackagePath) != self);
			installed.RemoveAll(mod => string.Equals(mod.Name, name, StringComparison.OrdinalIgnoreCase));
			var candidate = new ModInstall { Name = name, PackagePath = packagePath, Metadata = meta };
			List<string> missing = ModDependencies
				.CheckOne(candidate, ModLoadOrder.Read(modsRoot), installed)
				.Select(problem => problem.Message)
				.ToList();

			string title = meta.Title.Length == 0 ? name : ModText.ForMod(packagePath, meta.Title);
			native.Sort(StringComparer.Ordinal);
			return new ModInstallResult(name, title, meta.Version, packagePath, hard, native, missing,
				warnings, string.Empty, replaces);
		}
	}

	/// <summary>Why an entry is refused, or empty: its path must stay inside the package root and its bytes inside the file.</summary>
	private static string CheckEntry(F2ModEntry entry, long fileLength)
	{
		string raw = entry.Path;
		if (raw.Any(char.IsControl))
		{
			return "contains a control character";
		}

		if (raw.StartsWith('/') || raw.StartsWith('\\') || raw.Contains(':') || System.IO.Path.IsPathRooted(raw))
		{
			return "is an absolute path";
		}

		if (!ModNative.IsSafeRelativePath(raw.Replace('\\', '/')))
		{
			return "escapes the package root";
		}

		ulong size = (ulong)fileLength;
		return entry.Offset > size || entry.Size > size - entry.Offset
			? "points outside the file"
			: string.Empty;
	}

	private static IEnumerable<string> ValidateJson(F2ModPackage package)
	{
		// Every JSON kind and mod.toml through the loaders' own schema (Engine_ModValidate): issues
		// are reported before the player installs, never a reason to refuse — the game skips a
		// broken file on its own and names it in the log.
		var issues = new List<string>();
		foreach (F2ModEntry entry in package.Entries)
		{
			string path = entry.Path.Replace('\\', '/');
			if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			string text = System.Text.Encoding.UTF8.GetString(package.ReadEntryBytes(entry));
			foreach (ModIssue issue in ModValidate.Validate(path, text))
			{
				issues.Add((issue.Error ? "error: " : "warning: ") + issue);
			}
		}

		return issues;
	}

	private static bool IsRedirect(HttpStatusCode code) =>
		code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
			or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

	/// <summary>The file name the server states, else the URL's last segment; never a directory part.</summary>
	private static string DownloadFileName(HttpResponseMessage response, Uri uri)
	{
		var disposition = response.Content.Headers.ContentDisposition;
		string stated = (disposition?.FileNameStar ?? disposition?.FileName ?? string.Empty).Trim().Trim('"');
		string raw = stated.Length != 0 ? stated : Uri.UnescapeDataString(uri.AbsolutePath);
		return System.IO.Path.GetFileName(raw.Replace('\\', '/').TrimEnd('/'));
	}

	private static string TooLarge(long bytes) =>
		$"The package is {bytes / (1024.0 * 1024.0):0} MB; the limit is {MaxPackageBytes / (1024 * 1024)} MB.";

	private static string Printable(string path) =>
		new(path.Select(c => char.IsControl(c) ? '?' : c).ToArray());

	private static void TryDelete(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception)
		{
		}
	}
}
