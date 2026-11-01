namespace Refii.Tests.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// THE SHELL NEVER NAMES A TITLE. Shared C# (Godot/addons, and everything under Godot/src outside Titles/) must not
/// reference <c>Refii.Titles.*</c>; each title's C# lives in its own namespace and names no other
/// title; and the shared native build (src/, cmake/) reaches into no title folder. Read from the
/// source tree, so a dependency in the wrong direction fails here rather than surviving as a habit.
/// </summary>
[TestSuite]
public class TitleBoundaryTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")) &&
				Directory.Exists(Path.Combine(at, "Fable2")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static readonly Regex TitleNamespace = new(@"\bRefii\.Titles\.(\w+)", RegexOptions.Compiled);

	private static IEnumerable<string> CSharpUnder(string directory) =>
		Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories);

	[TestCase]
	public void SharedCSharpNamesNoTitle()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string src = Path.Combine(root!, "Godot", "src");
		string titles = Path.Combine(src, "Titles") + Path.DirectorySeparatorChar;

		var offenders = new List<string>();
		IEnumerable<string> shared = CSharpUnder(src).Where(f => !f.StartsWith(titles, StringComparison.Ordinal))
			.Concat(CSharpUnder(Path.Combine(root!, "Godot", "addons")));
		foreach (string file in shared)
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				if (TitleNamespace.IsMatch(lines[i]))
				{
					offenders.Add(Path.GetRelativePath(root!, file) + ":" + (i + 1));
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared code names a title: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	[TestCase]
	public void EachTitleKeepsToItsOwnNamespace()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string titles = Path.Combine(root!, "Godot", "src", "Titles");
		AssertThat(Directory.Exists(titles)).OverrideFailureMessage("no Godot/src/Titles").IsTrue();

		var offenders = new List<string>();
		foreach (string titleDirectory in Directory.EnumerateDirectories(titles))
		{
			string folder = Path.GetFileName(titleDirectory);
			foreach (string file in CSharpUnder(titleDirectory))
			{
				string text = File.ReadAllText(file);
				string relative = Path.GetRelativePath(root!, file);
				if (!Regex.IsMatch(text, @"^namespace Refii\.Titles\." + Regex.Escape(folder) + @"\b", RegexOptions.Multiline))
				{
					offenders.Add(relative + " (not in Refii.Titles." + folder + ")");
				}

				foreach (Match match in TitleNamespace.Matches(text))
				{
					if (match.Groups[1].Value != folder)
					{
						offenders.Add(relative + " (names Refii.Titles." + match.Groups[1].Value + ")");
					}
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("title namespace broken: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	private static readonly Regex GuestAddressRead =
		new(@"\b(?:GuestAddress|Peek\w*|TryPeekWord)\(\s*0x[0-9A-Fa-f]{8}", RegexOptions.Compiled);

	/// <summary>
	/// A guest address is one title's binary: shared C# asks the title's bridge or the title's
	/// contract for what lives there and never reads one itself.
	/// </summary>
	[TestCase]
	public void SharedCSharpReadsNoGuestAddress()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		string src = Path.Combine(root!, "Godot", "src");
		string titles = Path.Combine(src, "Titles") + Path.DirectorySeparatorChar;

		var offenders = new List<string>();
		foreach (string file in CSharpUnder(src).Where(f => !f.StartsWith(titles, StringComparison.Ordinal))
			.Concat(CSharpUnder(Path.Combine(root!, "Godot", "addons"))))
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				string code = lines[i].Split("//")[0];
				if (GuestAddressRead.IsMatch(code))
				{
					offenders.Add(Path.GetRelativePath(root!, file) + ":" + (i + 1));
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared code reads a guest address: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	private static readonly Regex NativeExport =
		new(@"REFII_HOST_ABI_EXPORT[^;{(]*?\b(Refii\w+)\s*\(", RegexOptions.Compiled);

	private static readonly Regex ManagedImport = new(
		@"\[(?:System\.Runtime\.InteropServices\.)?DllImport\((?<args>[^\]]*)\)\]\s*(?:\[[^\]]*\]\s*)*[\w\s]*\bstatic\s+extern\s+[^(]*?\b(?<name>Refii\w+)\s*\(",
		RegexOptions.Compiled);

	private static HashSet<string> ExportsIn(IEnumerable<string> files)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (string file in files)
		{
			foreach (Match match in NativeExport.Matches(File.ReadAllText(file)))
			{
				names.Add(match.Groups[1].Value);
			}
		}

		return names;
	}

	/// <summary>
	/// THE SHELL BINDS ONE ABI. Every export the shared HostBridge declares is answered by every
	/// title's bridge — by the core, or by the title's own feeds — so a title switch never meets
	/// an entry point that is not there. And Fable 3's "nothing stated" answers are only for
	/// exports the shell still binds: one the shell dropped is deleted, not kept.
	/// </summary>
	[TestCase]
	public void EveryTitleBridgeAnswersTheShellsAbi()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();

		var bound = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match match in ManagedImport.Matches(File.ReadAllText(Path.Combine(root!, "Godot", "src", "host", "HostBridge.cs"))))
		{
			Match entry = Regex.Match(match.Groups["args"].Value, @"EntryPoint\s*=\s*""(\w+)""");
			bound.Add(entry.Success ? entry.Groups[1].Value : match.Groups["name"].Value);
		}

		AssertThat(bound.Count > 200).OverrideFailureMessage($"only {bound.Count} shell imports parsed").IsTrue();

		HashSet<string> core = ExportsIn(Directory.EnumerateFiles(Path.Combine(root!, "src", "hostcore", "src"), "*.cpp"));
		var offenders = new List<string>();
		foreach (string title in new[] { "Fable2", "Fable3" })
		{
			string feeds = Path.Combine(root!, title, "src", "hostbridge", "src");
			HashSet<string> answered = ExportsIn(Directory.EnumerateFiles(feeds, "*.cpp"));
			answered.UnionWith(core);
			offenders.AddRange(bound.Where(name => !answered.Contains(name)).Select(name => title + " lacks " + name));
		}

		HashSet<string> unstated = ExportsIn(new[] { Path.Combine(root!, "Fable3", "src", "hostbridge", "src", "unstated_feeds.cpp") });
		offenders.AddRange(unstated.Where(name => !bound.Contains(name)).Select(name => "Fable3 answers unbound " + name));
		offenders.AddRange(unstated.Where(core.Contains).Select(name => "Fable3 shadows the core's " + name));

		AssertThat(offenders.Count)
			.OverrideFailureMessage("the shell's ABI is not answered alike: " + string.Join(", ", offenders.Take(20)))
			.IsEqual(0);
	}

	[TestCase]
	public void SharedNativeBuildReachesIntoNoTitleFolder()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();

		var offenders = new List<string>();
		var files = Directory.EnumerateFiles(Path.Combine(root!, "src"), "CMakeLists.txt", SearchOption.AllDirectories)
			.Concat(Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cmake", SearchOption.AllDirectories))
			.Where(f => !f.Contains(Path.DirectorySeparatorChar + "thirdparty" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				&& !f.Contains(Path.DirectorySeparatorChar + "assetbrowser" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
		foreach (string file in files)
		{
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
			{
				string code = lines[i].Split('#')[0];
				if (Regex.IsMatch(code, @"\bFable[23]/"))
				{
					offenders.Add(Path.GetRelativePath(root!, file) + ":" + (i + 1));
				}
			}
		}

		AssertThat(offenders.Count)
			.OverrideFailureMessage("shared native build reaches into a title: " + string.Join(", ", offenders))
			.IsEqual(0);
	}
}
