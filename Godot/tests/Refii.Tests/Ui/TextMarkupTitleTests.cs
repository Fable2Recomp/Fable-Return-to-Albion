namespace Refii.Tests.Ui;

using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// Each title's inline-markup vocabulary (<see cref="Engine_UiTextMarkup.Vocabulary"/>): Fable 2's
/// 46 tags are the table it always had; Fable 3's 65 are its own — the codes `sub_82B41050`
/// inserts and the art each case of `sub_821F6180` draws. No Godot runtime type is built.
/// </summary>
[TestSuite]
public class TextMarkupTitleTests
{
	[TestCase]
	public void Fable2KeepsItsTable()
	{
		var f2 = Engine_UiTextMarkup.Vocabulary.Fable2;
		AssertThat(f2.Tags.Count).IsEqual(46);
		AssertThat(f2.Tags.Select(t => t.Code).SequenceEqual(Enumerable.Range(1, 46))).IsTrue();
		AssertThat(f2.Find("a_img")!.Value.Texture).IsEqual(@"art\gui\controller\icon_button_a");
		AssertThat(f2.Find("gold_img")!.Value.Texture).IsEqual(@"art\gui\controller\icon_gold_coin");
		AssertThat(f2.Find("rightup.img")!.Value.Code).IsEqual(29);
		AssertThat(f2.Find("br")!.Value.Code).IsEqual(11);
		AssertThat(f2.Find("motif.img")!.Value.DoubleWidth).IsFalse();
		AssertThat(f2.Find("s5")).IsNull();
		AssertThat(f2.Find("silver_key.img")).IsNull();
		AssertThat(f2.Tags.Count(t => t.DoubleWidth)).IsEqual(5);
	}

	[TestCase]
	public void Fable3HasItsOwnCodesAndArt()
	{
		var f3 = Engine_UiTextMarkup.Vocabulary.Fable3;
		AssertThat(f3.Tags.Count).IsEqual(65);
		AssertThat(f3.Tags.Select(t => t.Code).SequenceEqual(Enumerable.Range(1, 65))).IsTrue();

		// Codes 1..7 pair; 13 breaks; every other code is an icon.
		AssertThat(f3.Tags.Where(t => t.Kind == Engine_UiTextMarkup.Kind.Colour).Select(t => t.Code)
			.SequenceEqual(Enumerable.Range(1, 7))).IsTrue();
		AssertThat(f3.Find("br")!.Value.Code).IsEqual(13);
		AssertThat(f3.Find("br")!.Value.Kind).IsEqual(Engine_UiTextMarkup.Kind.LineBreak);

		AssertThat(f3.Find("a_img")!.Value.Code).IsEqual(8);
		AssertThat(f3.Find("a_img")!.Value.Texture)
			.IsEqual(@"art\gui_memmount\HUDAssests\Version4\Buttons\A\24x24_NoRim\A_Complete");
		AssertThat(f3.Find("gold_img")!.Value.Texture)
			.IsEqual(@"Art\gui_memmount\hudassests\version4\lowerelements\infobox\CurrentcySmall");
		AssertThat(f3.Find("start.img")!.Value.Texture)
			.IsEqual(@"art\gui_memmount\HUDAssests\Version4\Buttons\Start\24x24_NoRim\Start_Complete");
		AssertThat(f3.Find("left.img")!.Value.Texture)
			.IsEqual(@"art\gui_memmount\HUDAssests\Version4\Buttons\LeftStick\24x24_NoRim\LS_Complete");
		AssertThat(f3.Find("leftup.img")!.Value.Texture).IsEqual(@"art\gui_memmount\controller\LeftUp");
		AssertThat(f3.Find("e.img")!.Value.Texture).IsEqual(@"art\gui\controller\24x24\Extendable");
		AssertThat(f3.Find("flower_orange.img")!.Value.Texture).IsEqual(
			@"art\gui_memmount\hudassests\version4\middleelements\slideinnotification\collectables\flowers_orange");
		AssertThat(f3.Find("dog_whistle.img")!.Value.Texture).IsEqual(
			@"art\gui_memmount\hudassests\version4\middleelements\slideinnotification\dogwhistle");

		// Inserted twice (0x8355DBEC, 0x8355DC30): the second wins.
		AssertThat(f3.Find("rightup.img")!.Value.Code).IsEqual(31);

		// The wide class: stars and the motif (side 64, advance 52).
		AssertThat(f3.Tags.Where(t => t.DoubleWidth).Select(t => t.Name).SequenceEqual(
			new[] { "star-1.img", "star-2.img", "star-3.img", "star-4.img", "star-5.img", "motif.img" })).IsTrue();

		// s5 draws at half the caller's opacity; the others at full.
		AssertThat(f3.Find("s5")!.Value.Colour.A).IsEqual(0.5f);
		AssertThat(f3.Find("s1")!.Value.Colour.A).IsEqual(1f);
	}

	[TestCase]
	public void Fable3SplitsByItsOwnTable()
	{
		var runs = Engine_UiTextMarkup.Vocabulary.Fable3.Split("<s6>Gold</s6> <gold_img>\nx");
		AssertThat(runs.Count).IsEqual(5);
		AssertThat(runs[0].Text).IsEqual("Gold");
		AssertThat(runs[0].Colour.HasValue).IsTrue();
		AssertThat(runs[2].Tag!.Value.Code).IsEqual(12);
		AssertThat(runs[3].Break).IsTrue();

		// Fable 2 has no s6: the tag is dropped and its text draws untinted.
		var f2 = Engine_UiTextMarkup.Vocabulary.Fable2.Split("<s6>Gold</s6>");
		AssertThat(f2.Count).IsEqual(1);
		AssertThat(f2[0].Text).IsEqual("Gold");
		AssertThat(f2[0].Colour.HasValue).IsFalse();
	}

	[TestCase]
	public void ContractsCarryTheirVocabulary()
	{
		AssertThat(Engine_UiTextContract.Fable2.Markup).IsSame(Engine_UiTextMarkup.Vocabulary.Fable2);
		AssertThat(Engine_UiTextContract.Fable3.Markup).IsSame(Engine_UiTextMarkup.Vocabulary.Fable3);
		AssertThat(Engine_UiTextContract.Fable2.Truncates).IsFalse();
		AssertThat(Engine_UiTextContract.Fable3.Truncates).IsTrue();
	}
}
