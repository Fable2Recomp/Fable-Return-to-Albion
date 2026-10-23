namespace Refii.Ui;

/// <summary>
/// THE HOST BOX'S PAPER, SIZED TO ITS TEXT. The questions and notices the host raises
/// (<see cref="Engine_UiHostBoxes"/>) carry text of no fixed length — a mod's consent, a sync
/// plan — on papers the artist sized for a few lines: `querybox`'s `Paper` and the front end's
/// `dialog` are the same `paperbox` nine-slice, 384 canvas units tall as authored. One rule grows
/// both: the paper's height follows the wrapped text, between the authored height and the most
/// the window holds with its margins; the width stays authored, so the text wraps at the authored
/// column; the text starts <c>topPad</c> below the paper's top, the prompt row keeps its
/// <c>belowText</c> band above the paper's bottom edge (measured off the art, so the prompts are
/// always under the text, never over it), and text past the window scrolls. Pure arithmetic in
/// canvas units; <see cref="Engine_UiPaperBox"/> measures the art and applies it.
/// </summary>
public static class Engine_UiHostBoxLayout
{
	/// <summary>One corner column of the nine-slice, in the paperbox's local units — two make the
	/// authored paper.</summary>
	public const float CornerLocal = 25.6f;

	/// <summary>The window's margin above and below the tallest paper.</summary>
	public const float Margin = 20f;

	/// <summary>The gap kept between the text window and the prompts' top.</summary>
	public const float PromptGap = 8f;

	/// <summary>A paper's fit: its height, the text window inside it, and the overflow to scroll.</summary>
	public readonly record struct Fit(float PaperHeight, float TextWindow, float ScrollRange)
	{
		public bool Scrolls => ScrollRange > 0.5f;
	}

	/// <summary>
	/// The tallest paper a window <paramref name="canvasHeight"/> tall holds with
	/// <see cref="Margin"/> above and below, for a paper centred <paramref name="centreY"/> off the
	/// window's middle (it grows about its own centre).
	/// </summary>
	public static float MaxPaper(float canvasHeight, float centreY) =>
		canvasHeight - (2f * Margin) - (2f * System.Math.Abs(centreY));

	/// <summary>
	/// The fit for <paramref name="textHeight"/> units of wrapped text: never smaller than the
	/// <paramref name="authoredPaper"/>, never taller than <paramref name="maxPaper"/>; what the
	/// window then cannot show is the scroll range.
	/// </summary>
	public static Fit For(float textHeight, float authoredPaper, float maxPaper, float topPad, float belowText)
	{
		float pads = topPad + belowText;
		float max = System.Math.Max(maxPaper, pads + 1f);
		float paper = System.Math.Clamp(textHeight + pads, System.Math.Min(authoredPaper, max), max);
		float window = paper - pads;
		return new Fit(paper, window, System.Math.Max(0f, textHeight - window));
	}

	/// <summary>The middle column's local width that makes the paper <paramref name="paperHeight"/>
	/// tall at <paramref name="unitsPerLocal"/> canvas units a local unit; 0 is the authored paper.</summary>
	public static float MiddleLocal(float paperHeight, float unitsPerLocal) =>
		unitsPerLocal <= 0f ? 0f : System.Math.Max(0f, (paperHeight / unitsPerLocal) - (2f * CornerLocal));

	/// <summary>The text window's top, for a paper centred on <paramref name="centreY"/>.</summary>
	public static float WindowTop(Fit fit, float centreY, float topPad) =>
		centreY + (fit.PaperHeight * 0.5f) - topPad;

	/// <summary>The text window's bottom edge — the top of the prompts' band.</summary>
	public static float WindowBottom(Fit fit, float centreY, float topPad) =>
		WindowTop(fit, centreY, topPad) - fit.TextWindow;

	/// <summary>How far the prompts move from their authored place: with the bottom edge, which
	/// moves by half the growth about the paper's centre.</summary>
	public static float PromptShift(Fit fit, float authoredPaper) =>
		-(fit.PaperHeight - authoredPaper) * 0.5f;
}
