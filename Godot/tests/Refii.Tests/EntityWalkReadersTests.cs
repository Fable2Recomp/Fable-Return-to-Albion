using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// Guards the region-change fix (upstream 88b3fce8). <c>HostBridge.GetEntities</c> takes the
/// "changed" edge of every row, so only the entity feed may call it. A second reader that walks with
/// it takes that edge from the feed, and after a level load a recycled address keeps the last
/// region's object standing (2026-11-07 "Load levels faster" did this through the
/// [guest-entities] report). Any other reader uses <c>HostBridge.PeekEntities</c>.
/// </summary>
[TestSuite]
public sealed class EntityWalkReadersTests
{
	/// <summary>The entity feed: the per-frame walk in GameEntities and its worker thread.</summary>
	private static readonly HashSet<string> Feed = new(StringComparer.Ordinal)
	{
		"src/game/GameEntities.cs",
		"src/host/HostEntityWalk.cs",
	};

	private static string FindGodotRoot()
	{
		string? dir = AppContext.BaseDirectory;
		while (!string.IsNullOrEmpty(dir))
		{
			if (File.Exists(Path.Combine(dir, "src", "host", "HostBridge.cs")))
			{
				return dir;
			}
			string candidate = Path.Combine(dir, "Godot");
			if (File.Exists(Path.Combine(candidate, "src", "host", "HostBridge.cs")))
			{
				return candidate;
			}
			dir = Path.GetDirectoryName(dir);
		}
		throw new DirectoryNotFoundException("Could not locate Godot/src/host/HostBridge.cs from the test directory.");
	}

	[TestCase]
	public void OnlyTheEntityFeedTakesTheChangeEdge()
	{
		string root = FindGodotRoot();
		var call = new Regex(@"\bGetEntities\s*\(", RegexOptions.CultureInvariant);
		var offenders = new List<string>();
		foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
		{
			string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
			if (rel == "src/host/HostBridge.cs" || Feed.Contains(rel))
			{
				continue;
			}
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; ++i)
			{
				string line = lines[i].TrimStart();
				if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal))
				{
					continue;
				}
				if (call.IsMatch(line))
				{
					offenders.Add($"{rel}:{i + 1}");
				}
			}
		}

		AssertThat(offenders.Count == 0)
			.OverrideFailureMessage("Only the entity feed may call HostBridge.GetEntities; use PeekEntities for "
				+ "reports and probes, or a level change leaves the last region's objects behind: "
				+ string.Join(", ", offenders))
			.IsTrue();
	}

	[TestCase]
	public void TheFeedFilesStillCallIt()
	{
		// If the feed moves, the list above must move with it, or the guard checks nothing.
		string root = FindGodotRoot();
		foreach (string rel in Feed)
		{
			string path = Path.Combine(root, rel);
			AssertThat(File.Exists(path)).OverrideFailureMessage($"{rel} is gone: update the feed list").IsTrue();
			AssertThat(File.ReadAllText(path).Contains("HostBridge.GetEntities(", StringComparison.Ordinal))
				.OverrideFailureMessage($"{rel} no longer calls GetEntities: update the feed list").IsTrue();
		}
	}
}
