using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Godot;
using Refii.BuildCache;
using Refii.Launcher;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

/// <summary>
/// Headless unit and feature tests covering the enlarged 1440x840 launcher design base,
/// widened SettingsDrawer geometry without left-dock overlap, uniform right-column control width,
/// and deterministic cleanup of legacy <c>Fable 2</c> / <c>refii-*</c> release artefacts.
/// </summary>
[TestSuite]
public sealed class LauncherPolishAndPackagingCleanupTests
{
	[AfterTest]
	public void ResetLauncherScale() =>
		LauncherScale.Update(new Vector2(LauncherScale.BaseWidth, LauncherScale.BaseHeight));

	[TestCase]
	public void DefaultDesktopViewport_WidensSettingsDrawer_AndAvoidsDockOverlap()
	{
		var defaultViewport = new Vector2(1440.0f, 840.0f);
		LauncherScale.Update(defaultViewport);

		AssertThat(LauncherScale.BaseWidth).IsEqual(1440.0f);
		AssertThat(LauncherScale.BaseHeight).IsEqual(840.0f);
		AssertThat(Math.Abs(LauncherScale.Unit - 1.0f) < 1e-4f).IsTrue();
		AssertThat(SettingsPanel.ControlColumnWidth).IsEqual(196);

		LauncherLayout idleLayout = LauncherLayout.Build(
			Vector2.Zero,
			defaultViewport,
			menuBarHeight: 40.0f,
			dualDock: true,
			pipelineStripVisible: false);

		// Widened right-side settings drawer gives ample room (~585px inner width vs previous 464px).
		AssertThat(idleLayout.SettingsSize.X).IsGreaterEqual(580.0f);
		AssertThat(idleLayout.ActionPos.X + idleLayout.ActionSize.X).IsLess(idleLayout.SettingsPos.X);
		AssertThat(idleLayout.InfoPos.X + idleLayout.InfoSize.X).IsLess(idleLayout.SettingsPos.X);

		LauncherLayout buildingLayout = LauncherLayout.Build(
			Vector2.Zero,
			defaultViewport,
			menuBarHeight: 40.0f,
			dualDock: true,
			pipelineStripVisible: true);

		AssertThat(buildingLayout.ActionSize.Y).IsGreater(idleLayout.ActionSize.Y);
		AssertThat(buildingLayout.ActionPos.X + buildingLayout.ActionSize.X).IsLess(buildingLayout.SettingsPos.X);

		// Compact 1280x760 laptop window scales smoothly and still keeps left dock clear of SettingsDrawer.
		var compactViewport = new Vector2(1280.0f, 760.0f);
		LauncherScale.Update(compactViewport);
		LauncherLayout compactLayout = LauncherLayout.Build(
			Vector2.Zero,
			compactViewport,
			menuBarHeight: 36.0f,
			dualDock: true,
			pipelineStripVisible: true);

		AssertThat(compactLayout.SettingsSize.X).IsGreaterEqual(500.0f);
		AssertThat(compactLayout.ActionPos.X + compactLayout.ActionSize.X).IsLess(compactLayout.SettingsPos.X);
	}

	[TestCase]
	public void LegacyPackageCleanup_RemovesOldFable2AndRefiiArtifacts_WhilePreservingCurrentOutputs()
	{
		string root = Path.Combine(Path.GetTempPath(), "refii_pkg_cleanup_" + Guid.NewGuid().ToString("N"));
		string releaseDir = Path.Combine(root, "out", "package");
		string workDir = Path.Combine(releaseDir, "windows-x86_64");
		string projectDir = Path.Combine(root, "Godot");

		Directory.CreateDirectory(workDir);
		Directory.CreateDirectory(projectDir);

		try
		{
			// Active package folder & shared caches that MUST survive
			string activeAppDir = Path.Combine(workDir, "Fable Return to Albion");
			Directory.CreateDirectory(activeAppDir);
			File.WriteAllText(Path.Combine(activeAppDir, "Fable Return to Albion.exe"), "active-bin");
			File.WriteAllText(Path.Combine(workDir, ".zig-stage-stamp.txt"), "zig-ok");
			File.WriteAllText(Path.Combine(workDir, ".crt-imports-cache.json"), "{}");

			string activeZip = Path.Combine(releaseDir, "Fable Return to Albion-windows-x86_64.zip");
			File.WriteAllText(activeZip, "active-zip");
			File.WriteAllText(activeZip + ".idx", "active-idx");

			string otherArchZip = Path.Combine(releaseDir, "Fable Return to Albion-linux-x86_64.zip");
			File.WriteAllText(otherArchZip, "linux-zip");

			File.WriteAllText(Path.Combine(projectDir, "project.godot"), "config_version=5");

			// Legacy directories, zips, sidecars, and stray exports that MUST be removed
			string legacyDir1 = Path.Combine(workDir, "Fable 2 Return to Albion");
			string legacyDir2 = Path.Combine(workDir, "Fable 2");
			Directory.CreateDirectory(legacyDir1);
			File.WriteAllText(Path.Combine(legacyDir1, "Fable 2 Return to Albion.exe"), "old-bin");
			Directory.CreateDirectory(legacyDir2);
			File.WriteAllText(Path.Combine(legacyDir2, "Fable 2.exe"), "very-old-bin");

			string legacyRefiiZip = Path.Combine(releaseDir, "refii-windows-x86_64.zip");
			string legacyRefiiIdx = legacyRefiiZip + ".idx";
			string legacyF2Zip = Path.Combine(releaseDir, "Fable 2 Return to Albion-windows-x86_64.zip");
			string legacyBareZip = Path.Combine(releaseDir, "Fable 2.zip");
			File.WriteAllText(legacyRefiiZip, "stale");
			File.WriteAllText(legacyRefiiIdx, "stale-idx");
			File.WriteAllText(legacyF2Zip, "stale");
			File.WriteAllText(legacyBareZip, "stale");

			string strayProjectExe = Path.Combine(projectDir, "Fable 2 Return to Albion.exe");
			string strayProjectPck = Path.Combine(projectDir, "Fable 2.pck");
			string strayDataDir = Path.Combine(projectDir, "data_Fable 2 Return to Albion_windows_x86_64");
			File.WriteAllText(strayProjectExe, "stray");
			File.WriteAllText(strayProjectPck, "stray");
			Directory.CreateDirectory(strayDataDir);
			File.WriteAllText(Path.Combine(strayDataDir, "refii-launcher.dll"), "stray-dll");

			IReadOnlyList<string> removed = LegacyPackageCleanup.CleanLegacyArtifacts(
				workDir: workDir,
				appName: "Fable Return to Albion",
				releasePackageDir: releaseDir,
				activeZipPath: activeZip,
				toolPlatform: "windows",
				toolArch: "x86_64",
				projectDir: projectDir);

			AssertThat(removed.Count).IsGreaterEqual(9);

			// Legacy items gone
			AssertThat(Directory.Exists(legacyDir1)).IsFalse();
			AssertThat(Directory.Exists(legacyDir2)).IsFalse();
			AssertThat(File.Exists(legacyRefiiZip)).IsFalse();
			AssertThat(File.Exists(legacyRefiiIdx)).IsFalse();
			AssertThat(File.Exists(legacyF2Zip)).IsFalse();
			AssertThat(File.Exists(legacyBareZip)).IsFalse();
			AssertThat(File.Exists(strayProjectExe)).IsFalse();
			AssertThat(File.Exists(strayProjectPck)).IsFalse();
			AssertThat(Directory.Exists(strayDataDir)).IsFalse();

			// Active items & caches intact
			AssertThat(Directory.Exists(activeAppDir)).IsTrue();
			AssertThat(File.Exists(Path.Combine(activeAppDir, "Fable Return to Albion.exe"))).IsTrue();
			AssertThat(File.Exists(Path.Combine(workDir, ".zig-stage-stamp.txt"))).IsTrue();
			AssertThat(File.Exists(Path.Combine(workDir, ".crt-imports-cache.json"))).IsTrue();
			AssertThat(File.Exists(activeZip)).IsTrue();
			AssertThat(File.Exists(activeZip + ".idx")).IsTrue();
			AssertThat(File.Exists(otherArchZip)).IsTrue();
			AssertThat(File.Exists(Path.Combine(projectDir, "project.godot"))).IsTrue();
		}
		finally
		{
			if (Directory.Exists(root))
			{
				Directory.Delete(root, recursive: true);
			}
		}
	}
}
