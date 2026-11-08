namespace Refii.Tests.Ui;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Text;

/// <summary>
/// The culture the host's text and fonts follow (<see cref="Engine_GameCulture"/>): what counts
/// as a stated culture, and the order a language file is looked for in. Pure strings — no Godot
/// type is built.
/// </summary>
[TestSuite]
public class GameCultureTests
{
	[TestCase]
	public void ShippedCulturesNormaliseToTheirFolderNames()
	{
		AssertThat(Engine_GameCulture.Normalize("hu-hu")).IsEqual("hu-hu");
		AssertThat(Engine_GameCulture.Normalize("zh-CHT")).IsEqual("zh-cht");
		AssertThat(Engine_GameCulture.Normalize("en-uk")).IsEqual("en-uk");
	}

	[TestCase]
	public void TornOrEmptyReadsAreNotCultures()
	{
		AssertThat(Engine_GameCulture.Normalize(null)).IsNull();
		AssertThat(Engine_GameCulture.Normalize(string.Empty)).IsNull();
		AssertThat(Engine_GameCulture.Normalize("hu\\hu")).IsNull();
		AssertThat(Engine_GameCulture.Normalize("../en-uk")).IsNull();
		AssertThat(Engine_GameCulture.Normalize("a-culture-code-far-too-long")).IsNull();
	}

	[TestCase]
	public void ACultureIsSearchedBeforeEnglish()
	{
		AssertThat(string.Join(",", Engine_GameCulture.SearchOrder("ko-kr"))).IsEqual("ko-kr,en-uk");
		AssertThat(string.Join(",", Engine_GameCulture.SearchOrder("en-uk"))).IsEqual("en-uk");
		AssertThat(string.Join(",", Engine_GameCulture.SearchOrder(null))).IsEqual("en-uk");
	}

	[TestCase]
	public void MountCandidatesEndOnTheLocaleLessSpelling()
	{
		AssertThat(string.Join(",", Engine_GameCulture.MountCandidates("hu-hu", "text/book.babel"))).IsEqual(
			"language/hu-hu/text/book.babel,data/language/hu-hu/text/book.babel," +
			"language/en-uk/text/book.babel,data/language/en-uk/text/book.babel," +
			"language/text/book.babel,data/language/text/book.babel");
	}

	[TestCase]
	public void OnlyAChangeIsReported()
	{
		Engine_GameCulture.ResetForTests();
		try
		{
			int raised = 0;
			void Count(string _) => ++raised;
			Engine_GameCulture.Changed += Count;
			try
			{
				AssertThat(Engine_GameCulture.Set("en-uk")).IsFalse();
				AssertThat(Engine_GameCulture.Set(null)).IsFalse();
				AssertThat(Engine_GameCulture.Set("HU-HU")).IsTrue();
				AssertThat(Engine_GameCulture.Current).IsEqual("hu-hu");
				AssertThat(Engine_GameCulture.Set("hu-hu")).IsFalse();
				AssertThat(raised).IsEqual(1);
			}
			finally
			{
				Engine_GameCulture.Changed -= Count;
			}
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
		}
	}

	/// <summary>The shipped <c>cultures.txt</c>'s rows that matter here, as the disc spells them.</summary>
	private const string ShippedCultures =
		"; ----------------------------------------------------\r\n" +
		"; FORMAT: lang_code<tab>localized_name<tab>fallback(opt)\r\n" +
		"en-uk\tGUI_SCREEN_OPTIONS_LANGUAGE_EN_UK\tnull\r\n" +
		"en\tGUI_SCREEN_OPTIONS_LANGUAGE_EN\ten-uk\r\n" +
		"es\tGUI_SCREEN_OPTIONS_LANGUAGE_ES\ten-uk\r\n" +
		"ko\tGUI_SCREEN_OPTIONS_LANGUAGE_KO\ten-uk\r\n" +
		"cz\tGUI_SCREEN_OPTIONS_LANGUAGE_CZ\ten-uk\r\n" +
		"\r\n" +
		"ko-kr\tGUI_SCREEN_OPTIONS_LANGUAGE_KO_KR\tko\r\n" +
		"en-us\tGUI_SCREEN_OPTIONS_LANGUAGE_EN_US\ten-uk\r\n" +
		"es-es\tGUI_SCREEN_OPTIONS_LANGUAGE_ES_ES\tes\r\n" +
		"cs-cz\tGUI_SCREEN_OPTIONS_LANGUAGE_CS_CZ\tcz\r\n" +
		"hu-hu\tGUI_SCREEN_OPTIONS_LANGUAGE_HU_HU\thu";

	private static string Pick(params string[] folders)
	{
		Dictionary<string, string?> cultures = Engine_GameCulture.ParseCultures(ShippedCultures);
		IReadOnlyList<string> installed = Engine_GameCulture.InstalledCultures(folders, cultures);
		return Engine_GameCulture.Choose(cultures, installed, installed, Engine_GameCulture.ConsoleCulture);
	}

	[TestCase]
	public void CulturesTxtRowsKeepTheirFallbacks()
	{
		Dictionary<string, string?> cultures = Engine_GameCulture.ParseCultures(ShippedCultures);
		AssertThat(cultures.ContainsKey("en-uk")).IsTrue();
		AssertThat(cultures["en-uk"]).IsNull();
		AssertThat(cultures["en-us"]).IsEqual("en-uk");
		AssertThat(cultures["es-es"]).IsEqual("es");
		AssertThat(cultures.ContainsKey(";")).IsFalse();
	}

	[TestCase]
	public void TheShippedFileDecodesFromBigEndianUtf16()
	{
		byte[] marked = new byte[] { 0xFE, 0xFF };
		byte[] body = Encoding.BigEndianUnicode.GetBytes(ShippedCultures);
		var bytes = new byte[marked.Length + body.Length];
		marked.CopyTo(bytes, 0);
		body.CopyTo(bytes, marked.Length);
		AssertThat(Engine_GameCulture.ParseCultures(Engine_GameCulture.DecodeCulturesText(bytes)).Count)
			.IsEqual(Engine_GameCulture.ParseCultures(ShippedCultures).Count);
	}

	[TestCase]
	public void OnlyCultureFoldersAreLanguages()
	{
		Dictionary<string, string?> cultures = Engine_GameCulture.ParseCultures(ShippedCultures);
		AssertThat(string.Join(",", Engine_GameCulture.InstalledCultures(
			new[] { "fonts", "zz-zz", "ko-kr", "EN-UK", "cs-cz" }, cultures))).IsEqual("cs-cz,en-uk,ko-kr");
	}

	[TestCase]
	public void TheGuestsRulePicksTheCultureAnInstallShips()
	{
		// USA/Europe: English console -> en-us -> en-uk, which ships.
		AssertThat(Pick("cs-cz", "en-uk", "fonts", "hu-hu", "ko-kr")).IsEqual("en-uk");
		// The Spanish GOTY disc ships es-es alone: no preferred culture is installed, so the
		// first installed language is.
		AssertThat(Pick("es-es", "fonts")).IsEqual("es-es");
		// A shipped en-us is the console's own culture and wins over its en-uk fallback.
		AssertThat(Pick("en-uk", "en-us")).IsEqual("en-us");
		// Two languages and neither in the console's chain: the first in name order.
		AssertThat(Pick("ko-kr", "es-es")).IsEqual("es-es");
		// Nothing installed at all: the title's fallback.
		AssertThat(Pick("fonts")).IsEqual("en-uk");
	}

	[TestCase]
	public void ACultureWithoutTextIsSkipped()
	{
		Dictionary<string, string?> cultures = Engine_GameCulture.ParseCultures(ShippedCultures);
		IReadOnlyList<string> installed = Engine_GameCulture.InstalledCultures(new[] { "en-uk", "es-es" }, cultures);
		IReadOnlyList<string> withText = Engine_GameCulture.InstalledCultures(new[] { "es-es" }, cultures);
		AssertThat(Engine_GameCulture.Choose(cultures, installed, withText, Engine_GameCulture.ConsoleCulture))
			.IsEqual("es-es");
	}

	[TestCase]
	public void AnInstallWithoutEnUkSearchesItsOwnCulture()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii_culture_" + Guid.NewGuid().ToString("N"));
		string language = Path.Combine(root, "data", "language");
		Engine_GameCulture.ResetForTests();
		try
		{
			Directory.CreateDirectory(Path.Combine(language, "es-es", "text"));
			Directory.CreateDirectory(Path.Combine(language, "fonts"));
			File.WriteAllBytes(Path.Combine(language, "cultures.txt"),
				Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(ShippedCultures)));

			Engine_GameCulture.UseInstall(root);
			AssertThat(Engine_GameCulture.Current).IsEqual("es-es");
			AssertThat(string.Join(",", Engine_GameCulture.Installed)).IsEqual("es-es");
			AssertThat(string.Join(",", Engine_GameCulture.SearchOrder(Engine_GameCulture.Current))).IsEqual("es-es,en-uk");
			AssertThat(string.Join(",", Engine_GameCulture.SearchOrder("ko-kr"))).IsEqual("ko-kr,en-uk,es-es");
			AssertThat(Engine_GameCulture.IsCultureCode("es")).IsTrue();
			AssertThat(Engine_GameCulture.IsCultureCode("fonts")).IsFalse();

			// The guest stating the culture it already runs in is no change, and a later read
			// of the install does not take a stated culture back.
			AssertThat(Engine_GameCulture.Set("es-es")).IsFalse();
			AssertThat(Engine_GameCulture.Set("ko-kr")).IsTrue();
			Engine_GameCulture.UseInstall(root);
			AssertThat(Engine_GameCulture.Current).IsEqual("ko-kr");
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
			if (Directory.Exists(root))
			{
				Directory.Delete(root, true);
			}
		}
	}

	[TestCase]
	public void AnInstallWithEnUkStaysInEnUk()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii_culture_" + Guid.NewGuid().ToString("N"));
		string language = Path.Combine(root, "language");
		Engine_GameCulture.ResetForTests();
		try
		{
			foreach (string folder in new[] { "cs-cz", "en-uk", "hu-hu", "ko-kr" })
			{
				Directory.CreateDirectory(Path.Combine(language, folder, "text"));
			}

			File.WriteAllBytes(Path.Combine(language, "cultures.txt"),
				Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(ShippedCultures)));
			Engine_GameCulture.UseInstall(root);
			AssertThat(Engine_GameCulture.Current).IsEqual("en-uk");
			AssertThat(string.Join(",", Engine_GameCulture.SearchOrder("hu-hu"))).IsEqual("hu-hu,en-uk,cs-cz,ko-kr");
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
			if (Directory.Exists(root))
			{
				Directory.Delete(root, true);
			}
		}
	}

	[TestCase]
	public void ModTextFollowsTheCultureInForce()
	{
		var englishOnly = new[] { "language/en-uk/text/book.babel" };
		var both = new[] { "language/en-uk/text/book.babel", "language/es-es/text/book.babel" };

		// en-uk in force: every table applies where it is authored (the USA/Europe disc).
		AssertThat(BabelOverlay.TableForCulture("language/en-uk/text/book.babel", englishOnly, "en-uk"))
			.IsEqual("language/en-uk/text/book.babel");
		AssertThat(BabelOverlay.TableForCulture("language/es-es/text/book.babel", both, "en-uk"))
			.IsEqual("language/es-es/text/book.babel");

		// Another culture: the en-uk rows go to its book unless the mod ships that book's own.
		AssertThat(BabelOverlay.TableForCulture("language/en-uk/text/book.babel", englishOnly, "es-es"))
			.IsEqual("language/es-es/text/book.babel");
		AssertThat(BabelOverlay.TableForCulture("data/language/en-uk/text/book.babel", englishOnly, "es-es"))
			.IsEqual("data/language/es-es/text/book.babel");
		AssertThat(BabelOverlay.TableForCulture("language/en-uk/text/book.babel", both, "es-es")).IsNull();
		AssertThat(BabelOverlay.TableForCulture("language/es-es/text/book.babel", both, "es-es"))
			.IsEqual("language/es-es/text/book.babel");
		AssertThat(BabelOverlay.TableForCulture("ui/strings.babel", englishOnly, "es-es"))
			.IsEqual("ui/strings.babel");
	}

	private static byte[] Concat(byte[] first, byte[] second)
	{
		var bytes = new byte[first.Length + second.Length];
		first.CopyTo(bytes, 0);
		second.CopyTo(bytes, first.Length);
		return bytes;
	}
}
