namespace Refii.Tests.Ui;

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
}
