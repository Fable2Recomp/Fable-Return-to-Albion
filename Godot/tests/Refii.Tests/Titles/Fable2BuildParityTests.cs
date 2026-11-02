namespace Refii.Tests.Titles;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;

/// <summary>
/// TU1 AND THE UNPATCHED DISC BUILD STAY AT PARITY. Source names every guest address by its TU1
/// value and <c>refii::guest::Address</c> / <c>ToTu1</c> (C#: <c>GuestAddress</c> /
/// <c>GuestAddressToTu1</c>) maps it through Fable2/versions/Fable2/guest_addresses.inc; an
/// address with no row is returned unchanged, which is wrong memory on the disc build. Hooks bind
/// by NAME through the two per-build configs (Fable2/versions/Fable2TU1 is TU1, Fable2/versions/Fable2
/// the disc), so a name in one and not the other is a hook that silently does nothing on one build.
/// Read from the source tree only.
/// </summary>
[TestSuite]
public class Fable2BuildParityTests
{
	/// <summary>
	/// Literals in the guest's address range that are NOT TU1 guest addresses, each with the reason.
	/// A new one fails until it is mapped (a row) or listed here with its reason.
	/// </summary>
	private static readonly (string File, uint Value, string Why)[] NotAddresses =
	{
		("Fable2/src/hostbridge/src/host_bridge.cpp", 0x83000000u, "upper bound of a code-pointer sanity range"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A7A000u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A7C000u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B6D000u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B6E800u, "TU1 trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6DA58u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6EF30u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6C0C0u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A6C5ACu, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A676C0u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82A67D18u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B67608u, "disc trace span bound"),
		("Fable2/src/vfs/src/core/internal_state_guest.cpp", 0x82B68BB0u, "disc trace span bound"),
		("Godot/src/Engine/equipment/items/Engine_RecordNameCatalog.cs", 0x82DF620Fu, "record name hash"),
		("Godot/src/Engine/equipment/items/Engine_RecordNameCatalog.cs", 0x835C36C9u, "record name hash"),
		("Godot/src/Engine/level/environment/Engine_ToneMapTheme.cs", 0x82D8E57Au, "theme name hash"),
		("Godot/src/Engine/entities/Engine_EntityGameplay.cs", 0x83632C03u, "property name hash"),
		("Godot/src/Engine/entities/Engine_EntityGameplay.cs", 0x820BD770u, "property name hash"),
	};

	/// <summary>
	/// Names bound in one build's config only, each proven from the binaries. The TU1 functions are
	/// declared weak in source, so the disc payload still links.
	/// </summary>
	private static readonly (string Name, bool Tu1Only, string Why)[] OneBuildNames =
	{
		("rex_lhCoopText_DonationRecipient", true,
			"episodic DLC text; the disc executable contains no EPISODIC string at all"),
		("rex_lhGameInit_RegisterBankProvider", true,
			"TU1 split it out of GameInit; no counterpart body in the disc .text, nothing binds it"),
		("rex_lhGuiRegistry_SetImage", true,
			"inlined into the disc epilogue sub_824F23A0; the disc states it via RefiiGuiImageInlined"),
		("RefiiGuiImageInlined", false,
			"the disc's counterpart of the TU1 rex_lhGuiRegistry_SetImage hook"),
	};

	private static readonly Regex GuestLiteral =
		new(@"(?<![0-9A-Za-z_])0[xX](8[23][0-9A-Fa-f]{6})(?![0-9A-Fa-f])[uUlL]*", RegexOptions.Compiled);

	private static readonly Regex PickPair =
		new(@"\bPick(?:ByBuild)?(?:<\w+>)?\(\s*0[xX]([0-9A-Fa-f]{8})[uU]?\s*,\s*0[xX]([0-9A-Fa-f]{8})[uU]?\s*\)",
			RegexOptions.Compiled);

	private static readonly Regex Row = new(@"^\{0x([0-9A-F]{8})u, 0x([0-9A-F]{8})u\},$", RegexOptions.Compiled);

	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")) &&
				File.Exists(Path.Combine(at, "Fable2", "versions", "Fable2", "guest_addresses.inc")))
			{
				return at;
			}

			at = Path.GetDirectoryName(at);
		}

		return null;
	}

	private static List<(uint Tu1, uint Disc)> ReadRows(string root)
	{
		var rows = new List<(uint, uint)>();
		foreach (string line in File.ReadAllLines(Path.Combine(root, "Fable2", "versions", "Fable2", "guest_addresses.inc")))
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
	private static string CodeOnly(string text, bool csharp)
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

	private static IEnumerable<string> SourceFiles(string root)
	{
		string[] native = { ".cpp", ".h", ".hpp", ".cc", ".c", ".inc" };
		var dirs = new (string Dir, string[] Ext)[]
		{
			("Fable2/src", native),
			("src/runtime", native),
			("src/host", native),
			("src/hostcore", native),
			("src/payload", native),
			("src/abi", native),
			("src/achievement", native),
			("rexglue-net/shadow/rexglue-sdk", native),
			("Godot/src", new[] { ".cs" }),
		};
		foreach ((string dir, string[] ext) in dirs)
		{
			string full = Path.Combine(root, dir);
			if (!Directory.Exists(full))
			{
				continue;
			}

			foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
			{
				string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
				if (!ext.Contains(Path.GetExtension(file)) || rel.Contains("/Fable3/", StringComparison.Ordinal) ||
					rel.Contains("/generated/", StringComparison.Ordinal) || rel.Contains("/thirdparty/", StringComparison.Ordinal))
				{
					continue;
				}

				yield return rel;
			}
		}
	}

	[TestCase]
	public void AddressTableIsSortedAndOneToOne()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		List<(uint Tu1, uint Disc)> rows = ReadRows(root!);
		AssertThat(rows.Count > 500).IsTrue();

		// refii_guest_address binary-searches by TU1 value and refii_guest_address_to_tu1 by the
		// disc value, so both columns must be unique and the first sorted.
		var unsorted = new List<string>();
		for (int i = 1; i < rows.Count; i++)
		{
			if (rows[i].Tu1 <= rows[i - 1].Tu1)
			{
				unsorted.Add($"0x{rows[i - 1].Tu1:X8} then 0x{rows[i].Tu1:X8}");
			}
		}

		AssertThat(unsorted).OverrideFailureMessage("rows out of order: " + string.Join(", ", unsorted)).IsEmpty();
		var twice = rows.GroupBy(r => r.Disc).Where(g => g.Count() > 1).Select(g => $"0x{g.Key:X8}").ToList();
		AssertThat(twice).OverrideFailureMessage("disc value on two rows: " + string.Join(", ", twice)).IsEmpty();
	}

	[TestCase]
	public void EveryGuestAddressLiteralHasARow()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		var rows = ReadRows(root!).ToDictionary(r => r.Tu1, r => r.Disc);
		var allowed = new HashSet<(string, uint)>(NotAddresses.Select(n => (n.File, n.Value)));
		var used = new HashSet<(string, uint)>();

		var missing = new List<string>();
		foreach (string rel in SourceFiles(root!))
		{
			string code = CodeOnly(File.ReadAllText(Path.Combine(root!, rel)), rel.EndsWith(".cs", StringComparison.Ordinal));

			// Pick(tu1, disc) / PickByBuild(tu1, disc) holding an ADDRESS states the disc value
			// itself; it must agree with the table, which then also covers the TU1 value.
			var pickDisc = new HashSet<int>();
			foreach (Match m in PickPair.Matches(code))
			{
				uint tu1 = Convert.ToUInt32(m.Groups[1].Value, 16);
				uint disc = Convert.ToUInt32(m.Groups[2].Value, 16);
				if (tu1 >> 24 is not (0x82 or 0x83))
				{
					continue;
				}

				int line = code.Take(m.Index).Count(ch => ch == '\n') + 1;
				if (!rows.TryGetValue(tu1, out uint row) || row != disc)
				{
					missing.Add($"{rel}:{line} Pick(0x{tu1:X8}, 0x{disc:X8}) disagrees with the table");
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
			"guest addresses with no TU1 -> disc row in Fable2/versions/Fable2/guest_addresses.inc " +
			"(match the address in the disc executable and add the row, or list a non-address in " +
			"NotAddresses with its reason):\n  " + string.Join("\n  ", missing)).IsEmpty();

		var stale = NotAddresses.Where(n => !used.Contains((n.File, n.Value))).Select(n => $"{n.File} 0x{n.Value:X8}").ToList();
		AssertThat(stale).OverrideFailureMessage("NotAddresses entries no longer in source: " + string.Join(", ", stale)).IsEmpty();
	}

	private static (HashSet<string> Functions, Dictionary<string, string> Midasm) ReadConfig(string path)
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
					shape.Add("registers=" + Regex.Replace(kv.Groups[2].Value, @"\s", ""));
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

	[TestCase]
	public void BothConfigsBindTheSameNames()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		(HashSet<string> tu1, Dictionary<string, string> tu1Midasm) =
			ReadConfig(Path.Combine(root!, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
		(HashSet<string> disc, Dictionary<string, string> discMidasm) =
			ReadConfig(Path.Combine(root!, "Fable2", "versions", "Fable2", "refii_config.toml"));
		AssertThat(tu1.Count > 2000 && disc.Count > 2000).IsTrue();

		var tu1Names = new HashSet<string>(tu1.Concat(tu1Midasm.Keys));
		var discNames = new HashSet<string>(disc.Concat(discMidasm.Keys));
		var expectTu1Only = OneBuildNames.Where(n => n.Tu1Only).Select(n => n.Name).ToHashSet();
		var expectDiscOnly = OneBuildNames.Where(n => !n.Tu1Only).Select(n => n.Name).ToHashSet();

		var tu1Only = tu1Names.Except(discNames).OrderBy(n => n, StringComparer.Ordinal).ToList();
		var discOnly = discNames.Except(tu1Names).OrderBy(n => n, StringComparer.Ordinal).ToList();
		AssertThat(tu1Only.Except(expectTu1Only).ToList()).OverrideFailureMessage(
			"named in Fable2TU1/refii_config.toml only (find the disc counterpart, or prove it TU1-only " +
			"and list it in OneBuildNames): " + string.Join(", ", tu1Only.Except(expectTu1Only))).IsEmpty();
		AssertThat(discOnly.Except(expectDiscOnly).ToList()).OverrideFailureMessage(
			"named in Fable2/refii_config.toml (disc) only: " + string.Join(", ", discOnly.Except(expectDiscOnly))).IsEmpty();
		AssertThat(expectTu1Only.Except(tu1Only).Concat(expectDiscOnly.Except(discOnly)).ToList())
			.OverrideFailureMessage("OneBuildNames entries that are no longer one-sided").IsEmpty();

		// A midasm hook the generator calls with different registers or return shape per build is a
		// signature mismatch with the one C++ definition.
		var shapes = tu1Midasm.Keys.Intersect(discMidasm.Keys)
			.Where(n => tu1Midasm[n] != discMidasm[n])
			.Select(n => $"{n}: TU1 {tu1Midasm[n]} / disc {discMidasm[n]}").ToList();
		AssertThat(shapes).OverrideFailureMessage("midasm hooks shaped differently: " + string.Join("; ", shapes)).IsEmpty();
	}

	[TestCase]
	public void EveryHookedNameBindsOnBothBuilds()
	{
		string? root = RepoRoot();
		AssertThat(root).OverrideFailureMessage("repo root not found").IsNotNull();
		(HashSet<string> tu1, Dictionary<string, string> tu1Midasm) =
			ReadConfig(Path.Combine(root!, "Fable2", "versions", "Fable2TU1", "refii_config.toml"));
		(HashSet<string> disc, Dictionary<string, string> discMidasm) =
			ReadConfig(Path.Combine(root!, "Fable2", "versions", "Fable2", "refii_config.toml"));
		var tu1Only = OneBuildNames.Where(n => n.Tu1Only).Select(n => n.Name).ToHashSet();

		var named = new Dictionary<string, string>();
		var source = new StringBuilder();
		foreach (string rel in SourceFiles(root!).Where(f => f.StartsWith("Fable2/src/", StringComparison.Ordinal)))
		{
			string code = CodeOnly(File.ReadAllText(Path.Combine(root!, rel)), false);
			source.Append(code).Append('\n');
			foreach (Match m in Regex.Matches(code, @"\b(?:REX_EXTERN|REX_FUNC|PPC_EXTERN_FUNC|PPC_FUNC|REX_WEAK_FUNC)\(\s*(?:__imp__)?(rex_\w+)|__imp__(rex_\w+)"))
			{
				string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
				named.TryAdd(name, rel);
			}
		}

		string all = source.ToString();
		var unbound = new List<string>();
		foreach ((string name, string rel) in named.OrderBy(n => n.Key, StringComparer.Ordinal))
		{
			bool inTu1 = tu1.Contains(name), inDisc = disc.Contains(name);
			if (inTu1 && inDisc)
			{
				continue;
			}

			// A TU1-only function binds only when the original exists, so source declares it weak
			// and checks it; otherwise the disc payload does not link.
			if (inTu1 && tu1Only.Contains(name) &&
				Regex.IsMatch(all, @"__attribute__\(\(weak\)\)\s*REX_FUNC\(__imp__" + name + @"\)"))
			{
				continue;
			}

			unbound.Add($"{name} ({rel}): TU1 {(inTu1 ? "yes" : "NO")}, disc {(inDisc ? "yes" : "NO")}");
		}

		AssertThat(unbound).OverrideFailureMessage("hooked names missing from a build's config:\n  " + string.Join("\n  ", unbound)).IsEmpty();

		// Every midasm hook either config names has its one C++ definition.
		var undefined = tu1Midasm.Keys.Union(discMidasm.Keys)
			.Where(n => !Regex.IsMatch(all, @"\b(?:void|bool)\s+" + n + @"\s*\("))
			.OrderBy(n => n, StringComparer.Ordinal).ToList();
		AssertThat(undefined).OverrideFailureMessage("midasm hooks with no definition in Fable2/src: " + string.Join(", ", undefined)).IsEmpty();
	}
}
