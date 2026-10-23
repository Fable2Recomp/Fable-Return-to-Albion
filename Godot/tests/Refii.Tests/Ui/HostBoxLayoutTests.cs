namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The host box's paper sized to its text (<see cref="Engine_UiHostBoxLayout"/>) and the scroll
/// state every text region shares (<see cref="Engine_UiScrollRegion"/>). Pure arithmetic in canvas
/// units — no Godot type is built.
/// </summary>
[TestSuite]
public class HostBoxLayoutTests
{
	/// <summary>The query box's paper as authored, and its pads measured off `querybox.bgf`: the
	/// body's box top (6 + 216/2) under the paper's top (192), and the B prompt's glyph top (-74)
	/// over the paper's bottom (-192) plus the gap.</summary>
	private const float Authored = 384f;

	private const float TopPad = 78f;

	private const float BelowText = 126f;

	private static bool Near(float a, float b) => System.MathF.Abs(a - b) < 0.01f;

	[TestCase]
	public void ShortTextKeepsTheAuthoredPaper()
	{
		Engine_UiHostBoxLayout.Fit fit = Engine_UiHostBoxLayout.For(60f, Authored, 680f, TopPad, BelowText);
		AssertThat(fit.PaperHeight).IsEqual(Authored);
		AssertThat(fit.TextWindow).IsEqual(Authored - TopPad - BelowText);
		AssertThat(fit.Scrolls).IsFalse();
	}

	[TestCase]
	public void ThePaperGrowsWithTheTextUpToTheMaximum()
	{
		float max = Engine_UiHostBoxLayout.MaxPaper(720f, 0f);
		AssertThat(max).IsEqual(680f);
		float previous = 0f;
		for (int lines = 1; lines <= 40; ++lines)
		{
			float text = lines * 28f;
			Engine_UiHostBoxLayout.Fit fit = Engine_UiHostBoxLayout.For(text, Authored, max, TopPad, BelowText);
			AssertThat(fit.PaperHeight >= previous).OverrideFailureMessage($"{lines} line(s) shrank the paper").IsTrue();
			AssertThat(fit.PaperHeight >= Authored && fit.PaperHeight <= max)
				.OverrideFailureMessage($"{lines} line(s): paper {fit.PaperHeight}").IsTrue();
			float wanted = text + TopPad + BelowText;
			if (wanted > Authored && wanted < max)
			{
				AssertThat(Near(fit.PaperHeight, wanted)).OverrideFailureMessage($"{lines} line(s)").IsTrue();
			}

			previous = fit.PaperHeight;
		}
	}

	[TestCase]
	public void PastTheMaximumTheRestScrolls()
	{
		Engine_UiHostBoxLayout.Fit fit = Engine_UiHostBoxLayout.For(900f, Authored, 680f, TopPad, BelowText);
		AssertThat(fit.PaperHeight).IsEqual(680f);
		AssertThat(fit.TextWindow).IsEqual(680f - TopPad - BelowText);
		AssertThat(Near(fit.ScrollRange, 900f - fit.TextWindow)).IsTrue();
		AssertThat(fit.Scrolls).IsTrue();
	}

	[TestCase]
	public void TheConsentAt720FillsTheWindow()
	{
		// 17 lines at the 28.3-unit advance the screenshot measures: ~481 units.
		Engine_UiHostBoxLayout.Fit fit = Engine_UiHostBoxLayout.For(481f, Authored,
			Engine_UiHostBoxLayout.MaxPaper(720f, 0f), TopPad, BelowText);
		AssertThat(fit.PaperHeight).IsEqual(680f);
		AssertThat(Near(fit.ScrollRange, 5f)).IsTrue();
	}

	[TestCase]
	public void AnOffCentrePaperKeepsItsMarginsBothSides()
	{
		// The front end's dialog stands 16 units above the window's middle.
		float max = Engine_UiHostBoxLayout.MaxPaper(720f, 16f);
		AssertThat(max).IsEqual(648f);
		AssertThat(16f + (max * 0.5f) <= 360f - Engine_UiHostBoxLayout.Margin).IsTrue();
		AssertThat(16f - (max * 0.5f) >= -360f + Engine_UiHostBoxLayout.Margin).IsTrue();
	}

	[TestCase]
	public void ThePromptsStayBelowTheTextWindow()
	{
		const float centre = 0f;
		foreach (float text in new[] { 20f, 200f, 400f, 2000f })
		{
			Engine_UiHostBoxLayout.Fit fit = Engine_UiHostBoxLayout.For(text, Authored, 680f, TopPad, BelowText);
			float bottomEdge = centre - (fit.PaperHeight * 0.5f);
			float windowBottom = Engine_UiHostBoxLayout.WindowBottom(fit, centre, TopPad);

			// The prompts' band moves with the bottom edge, so it keeps its distance from it.
			float promptTop = (-192f + BelowText - Engine_UiHostBoxLayout.PromptGap) +
				Engine_UiHostBoxLayout.PromptShift(fit, Authored);
			AssertThat(Near(windowBottom - bottomEdge, BelowText)).OverrideFailureMessage($"text {text}").IsTrue();
			AssertThat(promptTop < windowBottom).OverrideFailureMessage($"text {text}: prompts {promptTop}, window {windowBottom}").IsTrue();
			AssertThat(Near(Engine_UiHostBoxLayout.WindowTop(fit, centre, TopPad), (fit.PaperHeight * 0.5f) - TopPad)).IsTrue();
		}
	}

	[TestCase]
	public void TheMiddleColumnMakesUpTheGrowth()
	{
		// 7.5 canvas units a local unit: the corner columns alone are the authored 384.
		AssertThat(Engine_UiHostBoxLayout.MiddleLocal(Authored, 7.5f)).IsEqual(0f);
		AssertThat(Near(Engine_UiHostBoxLayout.MiddleLocal(680f, 7.5f) * 7.5f, 680f - Authored)).IsTrue();
		AssertThat(Engine_UiHostBoxLayout.MiddleLocal(200f, 7.5f)).IsEqual(0f);
	}

	[TestCase]
	public void TheRegionClampsToItsOverflow()
	{
		var region = new Engine_UiScrollRegion();
		AssertThat(region.Step(-1f, 10.0, 100f)).IsEqual(100f);
		AssertThat(region.Fraction).IsEqual(1f);
		AssertThat(region.Step(1f, 10.0, 100f)).IsEqual(0f);
		AssertThat(region.Step(-1f, 0.1, 0f)).IsEqual(0f);
		AssertThat(region.Scrollable).IsFalse();
	}

	[TestCase]
	public void TheStickScrollsAtTheGuestRate()
	{
		var region = new Engine_UiScrollRegion();
		AssertThat(Near(region.Step(-1f, 0.5, 1000f), Engine_UiScrollRegion.UnitsPerSecond * 0.5f)).IsTrue();
		AssertThat(Near(region.Step(0.5f, 0.5, 1000f), Engine_UiScrollRegion.UnitsPerSecond * 0.25f)).IsTrue();
	}

	[TestCase]
	public void WheelAndBarAreHeldForTheNextStep()
	{
		var region = new Engine_UiScrollRegion();
		region.AddWheel(2);
		AssertThat(region.Offset).IsEqual(0f);
		AssertThat(region.Step(0f, 0.0, 500f)).IsEqual(2f * Engine_UiScrollRegion.UnitsPerNotch);
		region.ScrollTo(0.5f);
		AssertThat(region.Step(0f, 0.0, 500f)).IsEqual(250f);
		region.AddWheel(-100);
		AssertThat(region.Step(0f, 0.0, 500f)).IsEqual(0f);
	}

	[TestCase]
	public void ANewBodyStartsAtTheTop()
	{
		var region = new Engine_UiScrollRegion();
		region.Step(-1f, 1.0, 300f);
		region.Rewind();
		AssertThat(region.Offset).IsEqual(0f);
		region.AddWheel(1);
		region.Reset();
		AssertThat(region.Step(0f, 0.0, 300f)).IsEqual(0f);
	}
}
