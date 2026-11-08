namespace Refii.Tests.Ui;

using System.Text.RegularExpressions;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The <c>truncate</c> rule of Fable 3's text element, <c>sub_82B46980</c>
/// (<see cref="Engine_UiTextTruncate"/>), against a measure of 10 pixels a character, colour tags
/// free and an icon tag 26 (the layout's own billing).
/// </summary>
[TestSuite]
public class TextTruncateTests
{
	private static int Measure(string text)
	{
		int icons = Regex.Matches(text, "<[a-z_]+\\.img>").Count;
		string bare = Regex.Replace(text, "<[^>]*>", string.Empty);
		return (bare.Length * 10) + (icons * 26);
	}

	private static string Apply(string source, int truncate, int average = 10, string? best = null) =>
		Engine_UiTextTruncate.Apply(source, truncate, Measure, average, 30, best);

	[TestCase]
	public void FittingTextIsUntouched()
	{
		AssertThat(Apply("Short", 150)).IsEqual("Short");
		AssertThat(Apply("Exactly ten", 110)).IsEqual("Exactly ten");
		AssertThat(Apply("Anything at all however long", 0)).IsEqual("Anything at all however long");
	}

	[TestCase]
	public void CutsToTheRoomLeftByTheDots()
	{
		// Room 100 − 30 = 70: seven characters.
		AssertThat(Apply("Abcdefghijklmnopqrstuvwxyz", 100)).IsEqual("Abcdefg...");
	}

	[TestCase]
	public void GrowsInStepsOfTheAverageWidth()
	{
		// Average 20: 3 characters (30), then +2 (50), +1 (60), +1 (70) — stops at the room.
		AssertThat(Apply("Abcdefghijklmnopqrstuvwxyz", 100, average: 20)).IsEqual("Abcdefg...");

		// Room 65, average 30: 2 characters (20), then +1 each step — 70 overshoots and is kept.
		AssertThat(Apply("Abcdefghijklmnopqrstuvwxyz", 95, average: 30)).IsEqual("Abcdefg...");
	}

	[TestCase]
	public void KeepsTheQuantityAfterTheDots()
	{
		// " x12" (40) leaves room 30: three characters.
		AssertThat(Apply("Health Potion x12", 100)).IsEqual("Hea... x12");

		// A space after " x": nothing cut or charged, but " x" still follows the dots.
		AssertThat(Apply("Box x 2 of something long", 100)).IsEqual("Box x 2... x");
	}

	[TestCase]
	public void KeepsTheBestMarkAndTheIcon()
	{
		string mark = Engine_UiTextTruncate.BestMark("Best");
		AssertThat(mark).IsEqual(" <s2>(Best)</s2>");

		// Room 200 − 30 − 70 (" (Best)") = 100: ten characters.
		AssertThat(Apply("Legendary Hammer of Doom" + mark, 200, best: "Best"))
			.IsEqual("Legendary ..." + mark);

		// The icon is charged the line height (30), not its 26. Room 200 − 30 − 30 = 140.
		AssertThat(Apply("Legendary Hammer of Doom<exclamation.img>", 200))
			.IsEqual("Legendary Hamm...<exclamation.img>");

		// No string table: the best mark is not looked for and is cut as text.
		AssertThat(Apply("Legendary Hammer of Doom" + mark, 200)).IsEqual("Legendary Hammer ...");
	}
}
