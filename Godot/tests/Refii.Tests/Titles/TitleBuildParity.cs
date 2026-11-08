namespace Refii.Tests.Titles;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static GdUnit4.Assertions;

/// <summary>
/// A TITLE'S TWO EXECUTABLES STAY AT PARITY, the checks every title with a title update shares. Source
/// names every guest address by its value in the NAMED build (the title-updated one) and the title's
/// mapper translates it through one table of { named, disc } rows; an address with no row is returned
/// unchanged, which is wrong memory on the other build. Hooks bind by NAME through the two per-build
/// configs, so a name in one and not the other is a hook that silently does nothing on one build.
/// Read from the source tree only. Fable2BuildParityTests and Fable3BuildParityTests state each
/// title's spec.
/// </summary>
public sealed class TitleBuildParity
{
	/// <summary>The table: <c>Fable2/versions/Fable2/guest_addresses.inc</c>.</summary>
	public required string Table { get; init; }

	/// <summary>The named build's config and the disc's, repo-relative.</summary>
	public required string NamedConfig { get; init; }

	public required string DiscConfig { get; init; }

	/// <summary>The named build, as messages say it ("TU1", "TU3").</summary>
	public required string NamedBuild { get; init; }

	/// <summary>Where the title's hooks live (for the hooked-name and midasm-definition checks).</summary>
	public required string HookRoot { get; init; }

	/// <summary>The trees whose address literals the table must cover: (dir, extensions).</summary>
	public required (string Dir, string[] Ext)[] Sources { get; init; }

	/// <summary>Repo-relative paths a source file must not contain to be read (another title's tree, tests).</summary>
	public string[] Excluded { get; init; } = Array.Empty<string>();

	/// <summary>Literals in the guest range that are not named-build addresses, each with its reason.</summary>
	public (string File, uint Value, string Why)[] NotAddresses { get; init; } = Array.Empty<(string, uint, string)>();

	/// <summary>Names bound in one build's config only, each proven from the binaries; NamedOnly = the named build's.</summary>
	public (string Name, bool NamedOnly, string Why)[] OneBuildNames { get; init; } = Array.Empty<(string, bool, string)>();

	/// <summary>The minimum rows, and the minimum names in each config — a smoke test that the right files were read.</summary>
	public int MinimumRows { get; init; }

	public int MinimumNames { get; init; }

	/// <summary>
	/// Whether a midasm hook must take the same REGISTERS on both builds, or only the same number of
	/// them (and the same return shape). The generator passes the registers as the hook's arguments,
	/// so the count is the signature; a build that keeps a value in another register names it.
	/// </summary>
	public bool SameRegisterNames { get; init; } = true;

	private static readonly Regex GuestLiteral =
		new(@"(?<![0-9A-Za-z_])0[xX](8[23][0-9A-Fa-f]{6})(?![0-9A-Fa-f])[uUlL]*", RegexOptions.Compiled);

	private static readonly Regex PickPair =
		new(@"\bPick(?:ByBuild)?(?:<\w+>)?\(\s*0[xX]([0-9A-Fa-f]{8})[uU]?\s*,\s*0[xX]([0-9A-Fa-f]{8})[uU]?\s*\)",
			RegexOptions.Compiled);

	private static readonly Regex Row = new(@"^\{0x([0-9A-F]{8})u, 0x([0-9A-F]{8})u\},$", RegexOptions.Compiled);

	public string Root()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")) && File.Exists(Path.Combine(at, Table)))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		AssertThat(at).OverrideFailureMessage("repo root not found").IsNotNull();
		return at!;
	}

	private List<(uint Named, uint Disc)> ReadRows(string root)
	{
		var rows = new List<(uint, uint)>();
		foreach (string line in File.ReadAllLines(Path.Combine(root, Table)))
		{
			Match m = Row.Match(line.Trim());
			if (m.Success)
			{
				rows.Add((Convert.ToUInt32(m.Groups[1].Value, 16), Convert.ToUInt32(m.Groups[2].Value, 16)));
			}
		}

		return rows;
	}

	/// <summary>Code with comments, string and character literals blanked; line breaks kept.</summary>
	public static string CodeOnly(string text, bool csharp)
	{
		var sb = new StringBuilder(text.Length);
		int i = 0;
		while (i < text.Length)
		{
			char c = text[i];
			char next = i + 1 < text.Length ? text[i + 1] : '\0';
			if (c == '/' && next == '/')
			{
				while (i < text.Length && text[i] != '\n') i++;
				continue;
			}

			if (c == '/' && next == '*')
			{
				int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
				end = end < 0 ? text.Length : end + 2;
				Blank(sb, text, i, end);
				i = end;
				continue;
			}

			if (csharp && c == '"' && next == '"' && i + 2 < text.Length && text[i + 2] == '"')
			{
				int quotes = 0;
				while (i + quotes < text.Length && text[i + quotes] == '"') quotes++;
				string fence = new('"', quotes);
				int end = text.IndexOf(fence, i + quotes, StringComparison.Ordinal);
				end = end < 0 ? text.Length : end + quotes;
				Blank(sb, text, i, end);
				i = end;
				continue;
			}

			if (!csharp && c == 'R' && next == '"' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
			{
				int open = text.IndexOf('(', i + 2);
				if (open > 0)
				{
					string close = ")" + text.Substring(i + 2, open - i - 2) + "\"";
					int end = text.IndexOf(close, open + 1, StringComparison.Ordinal);
					end = end < 0 ? text.Length : end + close.Length;
					Blank(sb, text, i, end);
					i = end;
					continue;
				}
			}

			if (c == '"' || (c == '\'' && !(i > 0 && char.IsLetterOrDigit(text[i - 1]))))
			{
				bool verbatim = csharp && c == '"' && i > 0 && text[i - 1] == '@';
				int j = i + 1;
				while (j < text.Length && text[j] != c && (verbatim || text[j] != '\n'))
				{
					if (!verbatim && text[j] == '\\') j++;
					else if (verbatim && text[j] == '"' && j + 1 < text.Length && text[j + 1] == '"') j++;
					j++;
				}

				int end = Math.Min(j + 1, text.Length);
				Blank(sb, text, i, end);
				i = end;
				continue;
			}

			sb.Append(c);
			i++;
		}

		return sb.ToString();
	}

	private static void Blank(StringBuilder sb, string text, int from, int to)
	{
		for (int k = from; k < to; k++)
		{
			sb.Append(text[k] == '\n' ? '\n' : ' ');
		}
	}

	private IEnumerable<string> SourceFiles(string root)
	{
		foreach ((string dir, string[] ext) in Sources)
		{
			string full = Path.Combine(root, dir);
			if (!Directory.Exists(full))
			{
				continue;
			}

			foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
			{
				string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
				if (!ext.Contains(Path.GetExtension(file)) || Excluded.Any(x => rel.Contains(x, StringComparison.Ordinal)) ||
					rel.Contains("/generated/", StringComparison.Ordinal) || rel.Contains("/thirdparty/", StringComparison.Ordinal))
				{
					continue;
				}

				yield return rel;
			}
		}
	}

	public void AddressTableIsSortedAndOneToOne()
	{
		List<(uint Named, uint Disc)> rows = ReadRows(Root());
		AssertThat(rows.Count >= MinimumRows).OverrideFailureMessage($"{Table}: {rows.Count} rows").IsTrue();

		// The mapper binary-searches by the named value one way and by the disc value the other, so
		// both columns must be unique and the first sorted.
		var unsorted = new List<string>();
		for (int i = 1; i < rows.Count; i++)
		{
			if (rows[i].Named <= rows[i - 1].Named)
			{
				unsorted.Add($"0x{rows[i - 1].Named:X8} then 0x{rows[i].Named:X8}");
			}
		}

		AssertThat(unsorted).OverrideFailureMessage("rows out of order: " + string.Join(", ", unsorted)).IsEmpty();
		var twice = rows.GroupBy(r => r.Disc).Where(g => g.Count() > 1).Select(g => $"0x{g.Key:X8}").ToList();
		AssertThat(twice).OverrideFailureMessage("disc value on two rows: " + string.Join(", ", twice)).IsEmpty();
	}

	public void EveryGuestAddressLiteralHasARow()
	{
		string root = Root();
		var rows = ReadRows(root).ToDictionary(r => r.Named, r => r.Disc);
		var allowed = new HashSet<(string, uint)>(NotAddresses.Select(n => (n.File, n.Value)));
		var used = new HashSet<(string, uint)>();

		var missing = new List<string>();
		foreach (string rel in SourceFiles(root))
		{
			string code = CodeOnly(File.ReadAllText(Path.Combine(root, rel)), rel.EndsWith(".cs", StringComparison.Ordinal));

			// Pick(named, disc) / PickByBuild(named, disc) holding an ADDRESS states the disc value
			// itself; it must agree with the table, which then also covers the named value.
			var pickDisc = new HashSet<int>();
			foreach (Match m in PickPair.Matches(code))
			{
				uint named = Convert.ToUInt32(m.Groups[1].Value, 16);
				uint disc = Convert.ToUInt32(m.Groups[2].Value, 16);
				if (named >> 24 is not (0x82 or 0x83))
				{
					continue;
				}

				int line = code.Take(m.Index).Count(ch => ch == '\n') + 1;
				if (!rows.TryGetValue(named, out uint row) || row != disc)
				{
					missing.Add($"{rel}:{line} Pick(0x{named:X8}, 0x{disc:X8}) disagrees with the table");
				}

				pickDisc.Add(m.Groups[2].Index);
			}

			foreach (Match m in GuestLiteral.Matches(code))
			{
				uint value = Convert.ToUInt32(m.Groups[1].Value, 16);
				if (rows.ContainsKey(value) || pickDisc.Contains(m.Groups[1].Index))
				{
					continue;
				}

				if (allowed.Contains((rel, value)))
				{
					used.Add((rel, value));
					continue;
				}

				int line = code.Take(m.Index).Count(ch => ch == '\n') + 1;
				missing.Add($"{rel}:{line} 0x{value:X8}");
			}
		}

		AssertThat(missing).OverrideFailureMessage(
			$"guest addresses with no {NamedBuild} -> disc row in {Table} " +
			"(match the address in the disc executable and add the row, or list a non-address in " +
			"NotAddresses with its reason):\n  " + string.Join("\n  ", missing)).IsEmpty();

		var stale = NotAddresses.Where(n => !used.Contains((n.File, n.Value))).Select(n => $"{n.File} 0x{n.Value:X8}").ToList();
		AssertThat(stale).OverrideFailureMessage("NotAddresses entries no longer in source: " + string.Join(", ", stale)).IsEmpty();
	}

	private (HashSet<string> Functions, Dictionary<string, string> Midasm) ReadConfig(string path)
	{
		var functions = new HashSet<string>();
		var midasm = new Dictionary<string, string>();
		string? hook = null;
		var shape = new List<string>();
		bool inFunctions = false;

		void Close()
		{
			if (hook != null)
			{
				shape.Sort(StringComparer.Ordinal);
				string described = string.Join(";", shape);
				if (midasm.TryGetValue(hook, out string? before) && before != described)
				{
					described = before + " | " + described;
				}

				midasm[hook] = described;
			}

			hook = null;
			shape.Clear();
		}

		foreach (string raw in File.ReadAllLines(path))
		{
			string line = raw.Trim();
			if (line.StartsWith('#') || line.Length == 0)
			{
				continue;
			}

			if (line.StartsWith('['))
			{
				Close();
				inFunctions = line == "[functions]";
				if (line == "[[midasm_hook]]")
				{
					hook = "";
				}

				continue;
			}

			if (inFunctions)
			{
				Match f = Regex.Match(line, @"name\s*=\s*""([^""]+)""");
				if (f.Success)
				{
					functions.Add(f.Groups[1].Value);
				}
			}
			else if (hook != null)
			{
				Match kv = Regex.Match(line, @"^(\w+)\s*=\s*(.*)$");
				if (!kv.Success)
				{
					continue;
				}

				string key = kv.Groups[1].Value;
				if (key == "name")
				{
					hook = kv.Groups[2].Value.Trim('"');
				}
				else if (key == "registers")
				{
					string registers = Regex.Replace(kv.Groups[2].Value, @"\s", "");
					shape.Add(SameRegisterNames
						? "registers=" + registers
						: "registers=" + registers.Count(ch => ch == '"') / 2);
				}
				else if (key != "address")
				{
					shape.Add(key);  // jump_address / jump_address_on_true / ...: the hook's return shape
				}
			}
		}

		Close();
		return (functions, midasm);
	}

	public void BothConfigsBindTheSameNames()
	{
		string root = Root();
		(HashSet<string> named, Dictionary<string, string> namedMidasm) = ReadConfig(Path.Combine(root, NamedConfig));
		(HashSet<string> disc, Dictionary<string, string> discMidasm) = ReadConfig(Path.Combine(root, DiscConfig));
		AssertThat(named.Count >= MinimumNames && disc.Count >= MinimumNames).IsTrue();

		var namedNames = new HashSet<string>(named.Concat(namedMidasm.Keys));
		var discNames = new HashSet<string>(disc.Concat(discMidasm.Keys));
		var expectNamedOnly = OneBuildNames.Where(n => n.NamedOnly).Select(n => n.Name).ToHashSet();
		var expectDiscOnly = OneBuildNames.Where(n => !n.NamedOnly).Select(n => n.Name).ToHashSet();

		var namedOnly = namedNames.Except(discNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
		var discOnly = discNames.Except(namedNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
		AssertThat(namedOnly.Except(expectNamedOnly).ToList()).OverrideFailureMessage(
			$"named in {NamedConfig} only (find the disc counterpart, or prove it {NamedBuild}-only " +
			"and list it in OneBuildNames): " + string.Join(", ", namedOnly.Except(expectNamedOnly))).IsEmpty();
		AssertThat(discOnly.Except(expectDiscOnly).ToList()).OverrideFailureMessage(
			$"named in {DiscConfig} (disc) only: " + string.Join(", ", discOnly.Except(expectDiscOnly))).IsEmpty();
		AssertThat(expectNamedOnly.Except(namedOnly).Concat(expectDiscOnly.Except(discOnly)).ToList())
			.OverrideFailureMessage("OneBuildNames entries that are no longer one-sided").IsEmpty();

		// A midasm hook the generator calls with a different signature or return shape per build is
		// a mismatch with the one C++ definition.
		var shapes = namedMidasm.Keys.Intersect(discMidasm.Keys)
			.Where(n => namedMidasm[n] != discMidasm[n])
			.Select(n => $"{n}: {NamedBuild} {namedMidasm[n]} / disc {discMidasm[n]}").ToList();
		AssertThat(shapes).OverrideFailureMessage("midasm hooks shaped differently: " + string.Join("; ", shapes)).IsEmpty();
	}

	public void EveryHookedNameBindsOnBothBuilds()
	{
		string root = Root();
		(HashSet<string> named, Dictionary<string, string> namedMidasm) = ReadConfig(Path.Combine(root, NamedConfig));
		(HashSet<string> disc, Dictionary<string, string> discMidasm) = ReadConfig(Path.Combine(root, DiscConfig));
		var oneBuild = OneBuildNames.ToDictionary(n => n.Name, n => n.NamedOnly);

		var hooked = new Dictionary<string, string>();
		var source = new StringBuilder();
		foreach (string rel in SourceFiles(root).Where(f => f.StartsWith(HookRoot, StringComparison.Ordinal)))
		{
			string code = CodeOnly(File.ReadAllText(Path.Combine(root, rel)), false);
			source.Append(code).Append('\n');
			foreach (Match m in Regex.Matches(code, @"\b(?:REX_EXTERN|REX_FUNC|PPC_EXTERN_FUNC|PPC_FUNC|REX_WEAK_FUNC)\(\s*(?:__imp__)?(rex_\w+)|__imp__(rex_\w+)"))
			{
				string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
				hooked.TryAdd(name, rel);
			}
		}

		string all = source.ToString();
		var unbound = new List<string>();
		foreach ((string name, string rel) in hooked.OrderBy(n => n.Key, StringComparer.Ordinal))
		{
			bool inNamed = named.Contains(name), inDisc = disc.Contains(name);
			if (inNamed && inDisc)
			{
				continue;
			}

			// A one-build function binds only where its original exists, so source declares it weak
			// and checks it; otherwise the other build's payload does not link.
			if (oneBuild.TryGetValue(name, out bool namedOnly) && (namedOnly ? inNamed : inDisc) &&
				Regex.IsMatch(all, @"__attribute__\(\(weak\)\)\s*REX_FUNC\(__imp__" + name + @"\)"))
			{
				continue;
			}

			unbound.Add($"{name} ({rel}): {NamedBuild} {(inNamed ? "yes" : "NO")}, disc {(inDisc ? "yes" : "NO")}");
		}

		AssertThat(unbound).OverrideFailureMessage("hooked names missing from a build's config:\n  " + string.Join("\n  ", unbound)).IsEmpty();

		// Every midasm hook either config names has its one C++ definition — written out, or made by a
		// macro whose body defines `void PREFIX##param(` for each of its invocations' arguments.
		var made = new HashSet<string>(StringComparer.Ordinal);
		foreach (Match macro in Regex.Matches(all, @"#define\s+(\w+)\(\s*(\w+)\s*\)(?:[^\n]|\\\n)*?\b(?:void|bool)\s+(\w+)##\2\s*\("))
		{
			foreach (Match use in Regex.Matches(all, @"^\s*" + macro.Groups[1].Value + @"\(\s*(\w+)\s*\)", RegexOptions.Multiline))
			{
				made.Add(macro.Groups[3].Value + use.Groups[1].Value);
			}
		}

		var undefined = namedMidasm.Keys.Union(discMidasm.Keys)
			.Where(n => !made.Contains(n) && !Regex.IsMatch(all, @"\b(?:void|bool)\s+" + n + @"\s*\("))
			.OrderBy(n => n, StringComparer.Ordinal).ToList();
		AssertThat(undefined).OverrideFailureMessage($"midasm hooks with no definition in {HookRoot}: " + string.Join(", ", undefined)).IsEmpty();
	}
}
