namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// Each title's Gameface text contract (<see cref="Engine_UiTextContract"/>): Fable 2's buckets,
/// scale, box canvas and line advance are exactly what the shared builder drew before the
/// contract existed, and Fable 3's are its own (`sub_82B46708`, `CTextAsset_vf1`). No Godot type
/// is built.
/// </summary>
[TestSuite]
public class TextContractTests
{
	/// <summary>The selection the builder used for every title before the contract: `sub_828715E0`.</summary>
	private static (UiFontBucket, float) Fable2Before(int pointSize) => pointSize switch
	{
		< 18 => (UiFontBucket.Small, pointSize / 18f),
		18 => (UiFontBucket.Normal, 1f),
		<= 24 => (UiFontBucket.Large, 1f),
		_ => (UiFontBucket.Large, pointSize / 24f),
	};

	[TestCase]
	public void Fable2DrawsExactlyAsBefore()
	{
		Engine_UiTextContract f2 = Engine_UiTextContract.Fable2;
		for (int size = 1; size <= 64; ++size)
		{
			(UiFontBucket bucket, float scale) = f2.SelectFor(size);
			(UiFontBucket wantBucket, float wantScale) = Fable2Before(size);
			AssertThat(bucket).IsEqual(wantBucket);
			AssertThat(scale).IsEqual(wantScale);
		}

		AssertThat(f2.BoxWidthUnits).IsEqual(960f);
		AssertThat(f2.BoxHeightUnits).IsEqual(720f);
		AssertThat(f2.LineAdvanceExtra).IsEqual(1);

		// Icons: the LineHeight + 1 square at the pen, advancing by what they draw.
		AssertThat(f2.IconSize(false, 26)).IsEqual((27f, 27f));
		AssertThat(f2.IconSize(true, 26)).IsEqual((54f, 27f));
		AssertThat(f2.IconPenAdvance(false, 26)).IsEqual(27f);
		AssertThat(f2.IconPenAdvance(true, 26)).IsEqual(54f);
		AssertThat(f2.IconInset).IsEqual(0f);
	}

	/// <summary>`sub_82B46708`: below 16 small, 16 normal, above large, never scaled.</summary>
	[TestCase]
	public void Fable3BucketsAtSixteenUnscaled()
	{
		Engine_UiTextContract f3 = Engine_UiTextContract.Fable3;
		AssertThat(f3.SelectFor(14)).IsEqual((UiFontBucket.Small, 1f));
		AssertThat(f3.SelectFor(15)).IsEqual((UiFontBucket.Small, 1f));
		AssertThat(f3.SelectFor(16)).IsEqual((UiFontBucket.Normal, 1f));
		AssertThat(f3.SelectFor(17)).IsEqual((UiFontBucket.Large, 1f));
		AssertThat(f3.SelectFor(18)).IsEqual((UiFontBucket.Large, 1f));
		AssertThat(f3.SelectFor(24)).IsEqual((UiFontBucket.Large, 1f));
		AssertThat(f3.SelectFor(36)).IsEqual((UiFontBucket.Large, 1f));
		AssertThat(f3.BoxWidthUnits).IsEqual(1280f);
		AssertThat(f3.BoxHeightUnits).IsEqual(720f);
		AssertThat(f3.LineAdvanceExtra).IsEqual(0);

		// Icons: `sub_82B457A8` at side 32 from a pen moved (-6, +6), advancing 26.
		AssertThat(f3.IconSize(false, 23)).IsEqual((32f, 32f));
		AssertThat(f3.IconPenAdvance(false, 23)).IsEqual(26f);
		AssertThat(f3.IconPenAdvance(true, 23)).IsEqual(52f);
		AssertThat(f3.IconInset).IsEqual(6f);
	}

	/// <summary>Fable 3's text draws as it did before c614ef6f5: no snap, no floor, no curve.</summary>
	[TestCase]
	public void Fable3TextShaderIsTheShippedCoverage()
	{
		Engine_UiTextContract f3 = Engine_UiTextContract.Fable3;
		AssertThat(f3.SnapGlyphs).IsFalse();
		AssertThat(f3.ContrastFloor).IsEqual(1f);
		AssertThat(f3.CoverageGamma).IsEqual(1f);
	}

	/// <summary>Fable 2 carries c614ef6f5's snap, 1.3 floor and 2.2 coverage curve.</summary>
	[TestCase]
	public void Fable2TextShaderIsTheThinMenuText()
	{
		Engine_UiTextContract f2 = Engine_UiTextContract.Fable2;
		AssertThat(f2.SnapGlyphs).IsTrue();
		AssertThat(f2.ContrastFloor).IsEqual(1.3f);
		AssertThat(f2.CoverageGamma).IsEqual(2.2f);
	}

	[TestCase]
	public void EachTitleStatesItsOwn()
	{
		AssertThat(Engine_Title.Fable2.UiText).IsSame(Engine_UiTextContract.Fable2);
		AssertThat(Engine_Title.Fable3.UiText).IsSame(Engine_UiTextContract.Fable3);
	}

    [TestCase]
    public void TheSmoothStyleDrawsFableTwosTextWithoutTheConsoleLook()
    {
        bool before = Engine_UiTextContract.ConsoleLook;
        try
        {
            Engine_UiTextContract.ConsoleLook = true;
            AssertThat(Engine_UiTextContract.Fable2.HasConsoleLook).IsTrue();
            AssertThat(Engine_UiTextContract.Fable2.SnapGlyphs).IsTrue();
            AssertThat(Engine_UiTextContract.Fable2.ContrastFloor).IsEqual(1.3f);
            AssertThat(Engine_UiTextContract.Fable2.CoverageGamma).IsEqual(2.2f);

            Engine_UiTextContract.ConsoleLook = false;
            AssertThat(Engine_UiTextContract.Fable2.SnapGlyphs).IsFalse();
            AssertThat(Engine_UiTextContract.Fable2.ContrastFloor).IsEqual(1f);
            AssertThat(Engine_UiTextContract.Fable2.CoverageGamma).IsEqual(1f);

            AssertThat(Engine_UiTextContract.Fable3.HasConsoleLook).IsFalse();
            AssertThat(Engine_UiTextContract.Fable3.SnapGlyphs).IsFalse();
        }
        finally
        {
            Engine_UiTextContract.ConsoleLook = before;
        }
    }
}
