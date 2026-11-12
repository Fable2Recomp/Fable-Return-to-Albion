using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GdUnit4;
using Refii.Launcher.Native;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

/// <summary>
/// Headless unit and COM integration tests for <see cref="NativeFilePicker"/>, ensuring desktop
/// file/folder pickers never instantiate <c>Godot.FileDialog</c> nodes on the main thread, parse
/// Godot filter specs accurately, skip slow UNC paths, and bind Win32 <c>IFileOpenDialog</c>
/// vtable slots cleanly on a background STA thread.
/// </summary>
[TestSuite]
public sealed class NativeFilePickerTests
{
	[AfterTest]
	public void ResetSingleFlight() => NativeFilePicker.ReleaseSingleFlight();

	[TestCase]
	public void ParseGodotFilters_ConvertsIsoXexpWildcardAndMultiExtensionSpecs()
	{
		NativeFilePicker.NativeFileFilter[] isoFilters = NativeFilePicker.ParseGodotFilters(
			new[] { "*.iso ; Disc image" });
		AssertThat(isoFilters.Length).IsEqual(1);
		AssertThat(isoFilters[0].Label).IsEqual("Disc image (*.iso)");
		AssertThat(isoFilters[0].Spec).IsEqual("*.iso");

		NativeFilePicker.NativeFileFilter[] tuFilters = NativeFilePicker.ParseGodotFilters(
			new[] { "*.xexp ; Title update patch", "* ; Title update package" });
		AssertThat(tuFilters.Length).IsEqual(2);
		AssertThat(tuFilters[0].Label).IsEqual("Title update patch (*.xexp)");
		AssertThat(tuFilters[0].Spec).IsEqual("*.xexp");
		AssertThat(tuFilters[1].Label).IsEqual("Title update package (*.*)");
		AssertThat(tuFilters[1].Spec).IsEqual("*.*");

		NativeFilePicker.NativeFileFilter[] multiFilters = NativeFilePicker.ParseGodotFilters(
			new[] { "*.f2mod, *.f3mod ; Albion mod package" });
		AssertThat(multiFilters.Length).IsEqual(1);
		AssertThat(multiFilters[0].Label).IsEqual("Albion mod package (*.f2mod, *.f3mod)");
		AssertThat(multiFilters[0].Spec).IsEqual("*.f2mod;*.f3mod");

		NativeFilePicker.NativeFileFilter[] fallbackFilters = NativeFilePicker.ParseGodotFilters(
			Array.Empty<string>());
		AssertThat(fallbackFilters.Length).IsEqual(1);
		AssertThat(fallbackFilters[0].Spec).IsEqual("*.*");
	}

	[TestCase]
	public void ResolveInitialDirectory_FollowsPriority_AndNeverProbesPersistedUncShares()
	{
		string root = OperatingSystem.IsWindows() ? @"C:\Users\Hero" : "/home/hero";
		string downloads = Path.Combine(root, "Downloads");
		string games = Path.Combine(root, "Games");
		string remembered = Path.Combine(root, "RememberedIsos");
		string isoHint = Path.Combine(games, "Fable2.iso");

		var existingDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			root,
			downloads,
			games,
			remembered,
		};
		var probedPaths = new List<string>();
		bool FakeExists(string candidate)
		{
			probedPaths.Add(candidate);
			return existingDirs.Contains(candidate);
		}

		// 1. Explicit file hint wins over persisted directory.
		string resolvedFromHint = NativeFilePicker.ResolveInitialDirectory(
			isoHint,
			remembered,
			directoryMode: false,
			FakeExists,
			root);
		AssertThat(resolvedFromHint).IsEqual(games);

		// 2. Empty/stale hint falls back to remembered local directory.
		string resolvedFromPersisted = NativeFilePicker.ResolveInitialDirectory(
			Path.Combine(root, "MissingFolder", "Fable2.iso"),
			remembered,
			directoryMode: false,
			FakeExists,
			root);
		AssertThat(resolvedFromPersisted).IsEqual(remembered);

		// 3. Persisted UNC path (\\server\share) is skipped WITHOUT probing the network!
		probedPaths.Clear();
		const string uncShare = @"\\offline-nas\xbox360\isos";
		AssertThat(NativeFilePicker.IsUncPath(uncShare)).IsTrue();
		AssertThat(NativeFilePicker.IsUncPath("//offline-nas/xbox360/isos")).IsTrue();

		string resolvedSkippingUnc = NativeFilePicker.ResolveInitialDirectory(
			currentPathHint: null,
			persistedDir: uncShare,
			directoryMode: false,
			FakeExists,
			root);
		AssertThat(resolvedSkippingUnc).IsEqual(downloads);
		AssertThat(probedPaths.Contains(uncShare)).IsFalse();
	}

	[TestCase]
	public void SingleFlightGuard_PreventsOverlappingDialogs_AndReleasesCleanly()
	{
		NativeFilePicker.ReleaseSingleFlight();
		AssertThat(NativeFilePicker.IsPickerActive).IsFalse();

		AssertThat(NativeFilePicker.TryAcquireSingleFlight()).IsTrue();
		AssertThat(NativeFilePicker.IsPickerActive).IsTrue();
		AssertThat(NativeFilePicker.TryAcquireSingleFlight()).IsFalse();

		NativeFilePicker.ReleaseSingleFlight();
		AssertThat(NativeFilePicker.IsPickerActive).IsFalse();
		AssertThat(NativeFilePicker.TryAcquireSingleFlight()).IsTrue();
	}

	[TestCase]
	public void WindowsComFileOpenDialog_ConfiguresFileAndFolderModesOnStaThread_WithoutShowingWindow()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		string tempDir = Path.Combine(Path.GetTempPath(), "refii_picker_test_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);

		try
		{
			// Deliberately pass forward slashes and trailing slash to verify Windows Shell normalization.
			string forwardSlashDir = tempDir.Replace('\\', '/') + "/";
			NativeFilePicker.NativeFileFilter[] filters = NativeFilePicker.ParseGodotFilters(
				new[] { "*.xexp ; Title update patch", "* ; Title update package" });

			NativeFilePicker.WindowsDialogDiagnostic fileDiag = default;
			NativeFilePicker.WindowsDialogDiagnostic dirDiag = default;
			Exception? workerError = null;

			var staThread = new Thread(() =>
			{
				try
				{
					fileDiag = NativeFilePicker.ProbeWindowsDialogConfiguration(
						"Select the title update",
						filters,
						forwardSlashDir,
						directoryMode: false);

					dirDiag = NativeFilePicker.ProbeWindowsDialogConfiguration(
						"Select Install Folder",
						Array.Empty<NativeFilePicker.NativeFileFilter>(),
						forwardSlashDir,
						directoryMode: true);
				}
				catch (Exception ex)
				{
					workerError = ex;
				}
			})
			{
				IsBackground = true,
			};
			staThread.SetApartmentState(ApartmentState.STA);
			staThread.Start();
			AssertThat(staThread.Join(TimeSpan.FromSeconds(10))).IsTrue();
			AssertThat(workerError).IsNull();

			AssertThat(fileDiag.Succeeded).IsTrue();
			AssertThat((fileDiag.ConfiguredOptions & NativeFilePicker.WindowsOptionForceFilesystem) != 0).IsTrue();
			AssertThat((fileDiag.ConfiguredOptions & NativeFilePicker.WindowsOptionFileMustExist) != 0).IsTrue();
			AssertThat((fileDiag.ConfiguredOptions & NativeFilePicker.WindowsOptionPickFolders) == 0).IsTrue();
			AssertThat(fileDiag.ConfiguredFileTypeIndex).IsEqual(1u);
			AssertThat(string.Equals(
				Path.GetFullPath(fileDiag.ConfiguredFolderPath ?? string.Empty).TrimEnd('\\'),
				Path.GetFullPath(tempDir).TrimEnd('\\'),
				StringComparison.OrdinalIgnoreCase)).IsTrue();

			AssertThat(dirDiag.Succeeded).IsTrue();
			AssertThat((dirDiag.ConfiguredOptions & NativeFilePicker.WindowsOptionPickFolders) != 0).IsTrue();
			AssertThat(string.Equals(
				Path.GetFullPath(dirDiag.ConfiguredFolderPath ?? string.Empty).TrimEnd('\\'),
				Path.GetFullPath(tempDir).TrimEnd('\\'),
				StringComparison.OrdinalIgnoreCase)).IsTrue();
		}
		finally
		{
			try { Directory.Delete(tempDir, recursive: true); } catch { }
		}
	}

	[TestCase]
	public void DesktopLauncherSources_DoNotInstantiateGodotFileDialogNodes()
	{
		string? repoRoot = FindRepoRoot();
		AssertThat(repoRoot).IsNotNull();

		string launcherSrcDir = Path.Combine(repoRoot!, "Godot", "src", "launcher");
		AssertThat(Directory.Exists(launcherSrcDir)).IsTrue();

		var offenders = new List<string>();
		foreach (string file in Directory.EnumerateFiles(launcherSrcDir, "*.cs", SearchOption.AllDirectories))
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				string trimmed = lines[i].TrimStart();
				if (trimmed.StartsWith("//", StringComparison.Ordinal) ||
					trimmed.StartsWith("*", StringComparison.Ordinal) ||
					trimmed.StartsWith("///", StringComparison.Ordinal))
				{
					continue;
				}

				if (trimmed.Contains("new FileDialog", StringComparison.Ordinal) ||
					trimmed.Contains("new Godot.FileDialog", StringComparison.Ordinal))
				{
					offenders.Add($"{Path.GetRelativePath(repoRoot!, file)}:{i + 1}");
				}
			}
		}

		AssertThat(offenders.Count).IsEqual(0);
	}

	private static string? FindRepoRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (File.Exists(Path.Combine(dir, "CMakeLists.txt")) &&
				Directory.Exists(Path.Combine(dir, "Godot", "src", "launcher")))
			{
				return dir;
			}

			dir = Directory.GetParent(dir)?.FullName;
		}

		return null;
	}
}
