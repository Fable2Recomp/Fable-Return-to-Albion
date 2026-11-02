namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// Phone-only code stays out of shared files (docs/android.md, "Where Android code goes"): it lives in
/// Godot/src/android, Godot/src/Titles/*/android and *_android native files, and a shared file keeps
/// at most a one-line hook. These tests hold that line.
/// </summary>
/// <remarks>
/// THE RATCHET. Every shared source line that names the phone (Engine_Platform.IsAndroid,
/// __ANDROID__, REX_PLATFORM_ANDROID, CMake's ANDROID, a FABLE_LITE define) is counted per file
/// against AndroidSeparationBaseline.txt. A file may go down, never up, and a new file starts at
/// zero. When a new hook really is needed, raise that file's count in the baseline in the same
/// commit (or run the tests with REFII_WRITE_ANDROID_BASELINE=1 to rewrite it), so the choice is
/// visible in review.
/// </remarks>
[TestSuite]
public class AndroidSeparationTests
{
	private const string BaselineFile = "Godot/tests/Refii.Tests/AndroidSeparationBaseline.txt";

	/// <summary>The folders phone-only code belongs in; nothing under them is counted.</summary>
	private static readonly Regex AndroidFolder = new(@"^Godot/src/(android|Titles/[^/]+/android)/", RegexOptions.Compiled);

	/// <summary>A source line that names the phone.</summary>
	private static readonly Regex PhoneLine = new(
		@"Engine_Platform\.IsAndroid|OS\.HasFeature\(""android""\)|__ANDROID__|REX_PLATFORM_ANDROID|\bANDROID\b|FABLE_LITE\w*",
		RegexOptions.Compiled);

	private static readonly string[] Roots = { "Godot/src", "src", "Fable2/src", "Fable3/src", "rexglue-net/shadow", "cmake" };

	private static readonly string[] Extensions = { ".cs", ".cpp", ".h", ".hpp", ".c", ".cmake", ".gdshader", ".gdshaderinc" };

	private static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal)
	{
		"thirdparty", "generated", "build", ".godot", "tests",
	};

	private const string HowToFix =
		"Move phone-only code into Godot/src/android (Godot/src/Titles/<title>/android for one title) or a *_android " +
		"native file and leave a one-line call. If the hook is really needed, raise the file's count in " + BaselineFile +
		" in the same commit (or run the tests with REFII_WRITE_ANDROID_BASELINE=1).";

	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")) && Directory.Exists(Path.Combine(at, "Fable2")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static string Relative(string root, string path) =>
		Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

	/// <summary>The shared source files the ratchet counts, as repository-relative paths.</summary>
	private static IEnumerable<string> SharedSources(string root)
	{
		yield return "CMakeLists.txt";
		foreach (string top in Roots)
		{
			string dir = Path.Combine(root, top);
			if (!Directory.Exists(dir))
			{
				continue;
			}

			foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
			{
				string path = Relative(root, file);
				string name = Path.GetFileName(path);
				if ((!Extensions.Contains(Path.GetExtension(name), StringComparer.Ordinal) && name != "CMakeLists.txt") ||
					AndroidFolder.IsMatch(path) ||
					name.Contains("_android", StringComparison.OrdinalIgnoreCase) ||
					path.Split('/').Any(SkippedFolders.Contains))
				{
					continue;
				}

				yield return path;
			}
		}
	}

	/// <summary>Each shared file's count of lines that name the phone; files with none are left out.</summary>
	private static SortedDictionary<string, int> Count(string root)
	{
		var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
		foreach (string path in SharedSources(root))
		{
			string full = Path.Combine(root, path);
			if (!File.Exists(full))
			{
				continue;
			}

			int lines = File.ReadLines(full).Count(line => PhoneLine.IsMatch(line));
			if (lines > 0)
			{
				counts[path] = lines;
			}
		}

		return counts;
	}

	private static Dictionary<string, int> ReadBaseline(string file)
	{
		var baseline = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (string line in File.ReadLines(file))
		{
			if (line.Length == 0 || line.StartsWith('#'))
			{
				continue;
			}

			string[] fields = line.Split('\t');
			baseline[fields[1]] = int.Parse(fields[0]);
		}

		return baseline;
	}

	private static void WriteBaseline(string file, SortedDictionary<string, int> counts)
	{
		var text = new StringBuilder();
		text.Append("# Lines that name the phone in each shared file (AndroidSeparationTests). A count may only go\n");
		text.Append("# down; raise one only for a hook that has to stay in the shared file. Format: count<TAB>path\n");
		foreach ((string path, int count) in counts)
		{
			text.Append(count).Append('\t').Append(path).Append('\n');
		}

		File.WriteAllText(file, text.ToString());
	}

	/// <summary>No shared file names the phone on more lines than the baseline allows.</summary>
	[TestCase]
	public void SharedFilesGainNoPhoneCode()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string baselineFile = Path.Combine(root, BaselineFile);
		SortedDictionary<string, int> counts = Count(root);
		if (Environment.GetEnvironmentVariable("REFII_WRITE_ANDROID_BASELINE") == "1")
		{
			WriteBaseline(baselineFile, counts);
		}

		Dictionary<string, int> baseline = ReadBaseline(baselineFile);
		string[] grown = counts
			.Where(entry => entry.Value > baseline.GetValueOrDefault(entry.Key))
			.Select(entry => $"{entry.Key}: {entry.Value} line(s) name the phone, baseline {baseline.GetValueOrDefault(entry.Key)}")
			.ToArray();
		AssertThat(grown.Length)
			.OverrideFailureMessage("Phone code grew in shared files:\n" + string.Join("\n", grown) + "\n" + HowToFix)
			.IsEqual(0);
	}

	/// <summary>Files named for Android live in the Android folders; native *_android files sit beside their shared ones.</summary>
	[TestCase]
	public void AndroidNamedCodeLivesInTheAndroidFolders()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string[] misplaced = Directory.EnumerateFiles(Path.Combine(root, "Godot", "src"), "*.cs", SearchOption.AllDirectories)
			.Select(file => Relative(root, file))
			.Where(path => Path.GetFileName(path).Contains("Android", StringComparison.Ordinal) && !AndroidFolder.IsMatch(path))
			.ToArray();
		AssertThat(misplaced.Length)
			.OverrideFailureMessage("Android C# outside the Android folders:\n" + string.Join("\n", misplaced) + "\n" + HowToFix)
			.IsEqual(0);
	}

	/// <summary>The lite shaders are built from the full ones (Engine_ShaderVariant), never copied into files.</summary>
	[TestCase]
	public void NoLiteShaderCopies()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string[] copies = Directory.EnumerateFiles(Path.Combine(root, "Godot", "src"), "*.gdshader", SearchOption.AllDirectories)
			.Select(file => Relative(root, file))
			.Where(path => path.Contains("/lite/", StringComparison.Ordinal))
			.ToArray();
		AssertThat(copies.Length)
			.OverrideFailureMessage("Lite shader files are back:\n" + string.Join("\n", copies) +
				"\nA lite build is its full shader plus defines: add the defines to Engine_ShaderVariant instead of a file.")
			.IsEqual(0);
	}

	/// <summary>Godot/docs ships in every install's SDK, so the Android docs live in docs/ instead.</summary>
	[TestCase]
	public void ShippedDocsSayNothingAboutAndroid()
	{
		if (RepoRoot() is not { } root)
		{
			return;
		}

		string[] mentions = Directory.EnumerateFiles(Path.Combine(root, "Godot", "docs"), "*.md", SearchOption.AllDirectories)
			.Where(file => File.ReadAllText(file).Contains("android", StringComparison.OrdinalIgnoreCase))
			.Select(file => Relative(root, file))
			.ToArray();
		AssertThat(mentions.Length)
			.OverrideFailureMessage("Godot/docs ships in every install's SDK; move the Android text to docs/:\n" + string.Join("\n", mentions))
			.IsEqual(0);
	}
}
