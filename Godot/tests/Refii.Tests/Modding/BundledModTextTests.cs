namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Text;

/// <summary>
/// The shipped mods' translations: every <c>language/&lt;culture&gt;/text/book.babel.csv</c> beside a
/// mod's en-uk table states exactly the en-uk keys, each with the same markup — placeholders,
/// glyph tags, <c>{TAG}</c>s and escapes — and parses clean the way the mount reads it.
/// </summary>
[TestSuite]
public class BundledModTextTests
{
	/// <summary>The cultures the Fable II discs ship (their <c>cultures.txt</c> codes), each written for every mod with text.</summary>
	private static readonly string[] DiscCultures =
	{
		"fr-fr", "de-de", "it-it", "es-es", "ru-ru", "pl-pl", "cs-cz", "hu-hu", "sk-sk", "ja-jp", "ko-kr", "zh-cht",
	};

	private static readonly Regex Markup = new(@"%\d+|%[a-z]|\{[A-Za-z0-9_]+\}|<[^<>]+>|\\[nrt\\]|\n", RegexOptions.Compiled);

	[TestCase]
	public void EveryTranslationHasTheEnglishKeysAndMarkup()
	{
		string? repo = GodotModScripts.RepoRoot();
		if (repo == null)
		{
			return;  // a packaged test run without the source tree
		}

		int checkedTables = 0;
		foreach (string mod in Directory.GetDirectories(Path.Combine(repo, "Fable2", "mods-src")))
		{
			string language = Path.Combine(mod, "language");
			string english = Path.Combine(language, "en-uk", "text", "book.babel.csv");
			if (!File.Exists(english))
			{
				AssertThat(Directory.Exists(language)).OverrideFailureMessage($"{mod}: translations without an en-uk table").IsFalse();
				continue;
			}

			Dictionary<string, string> source = Rows(english);
			AssertThat(source.Count > 0).OverrideFailureMessage(english).IsTrue();

			var cultures = Directory.GetDirectories(language).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
			foreach (string culture in DiscCultures)
			{
				AssertThat(cultures.Contains(culture)).OverrideFailureMessage($"{mod}: no {culture} table").IsTrue();
			}

			foreach (string folder in Directory.GetDirectories(language))
			{
				string culture = Path.GetFileName(folder);
				AssertThat(Regex.IsMatch(culture, "^[a-z]{2}(-[a-z]{2,4})?$"))
					.OverrideFailureMessage($"{mod}: '{culture}' is not a culture folder the mount matches").IsTrue();
				if (culture == "en-uk")
				{
					continue;
				}

				string path = Path.Combine(folder, "text", "book.babel.csv");
				AssertThat(File.Exists(path)).OverrideFailureMessage($"{folder}: no text/book.babel.csv").IsTrue();
				Dictionary<string, string> translated = Rows(path);

				AssertThat(string.Join(",", translated.Keys)).OverrideFailureMessage(path).IsEqual(string.Join(",", source.Keys));
				foreach ((string key, string value) in source)
				{
					string got = translated[key];
					AssertThat(got.Trim().Length > 0).OverrideFailureMessage($"{path}: {key} is empty").IsTrue();
					AssertThat(Tokens(got)).OverrideFailureMessage($"{path}: {key} markup differs from en-uk")
						.IsEqual(Tokens(value));
				}

				checkedTables++;
			}
		}

		AssertThat(checkedTables > 0).IsTrue();
	}

	/// <summary>A table as the mount reads it (BabelOverlay.ParseCsv), no parse errors, no repeated key, in file order.</summary>
	private static Dictionary<string, string> Rows(string path)
	{
		byte[] bytes = File.ReadAllBytes(path);
		AssertThat(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			.OverrideFailureMessage($"{path}: starts with a byte-order mark").IsFalse();

		var errors = new List<string>();
		List<BabelOverlayRow> rows = BabelOverlay.ParseCsv(new UTF8Encoding(false, true).GetString(bytes), errors);
		AssertThat(errors.Count).OverrideFailureMessage($"{path}: {string.Join("; ", errors)}").IsEqual(0);

		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (BabelOverlayRow row in rows)
		{
			AssertThat(result.TryAdd(row.Key, row.Value)).OverrideFailureMessage($"{path}: {row.Key} twice").IsTrue();
		}

		return result;
	}

	private static string Tokens(string value) =>
		string.Join(" ", Markup.Matches(value).Select(m => m.Value).OrderBy(t => t, StringComparer.Ordinal));
}
