using System;

namespace Refii.Ui;

/// <summary>
/// The <c>truncate</c> attribute of a Gameface text element: a single-line string that measures
/// wider than the attribute's pixel width is cut and ended with <c>...</c>, keeping a trailing
/// quantity, the inventory's "best" mark and an exclamation icon after the dots.
/// </summary>
/// <remarks>
/// Fable 3 360 disc. <c>CTextAsset_vf3</c> reads the attribute as an int (0x82B46318 →
/// <c>lwz r11, 4(r3)</c> 0x82B46330 → <c>stw r11, 0x98(r31)</c> 0x82B46344). <c>sub_82B46980</c>
/// (called from <c>sub_821F6068</c> at 0x821F6160) runs only when the element does not wrap
/// (+0x68 == 0) and its measured width (+0xAC) exceeds a positive truncate (+0x98):
/// <list type="number">
/// <item>the room is <c>truncate − width("...")</c> (<c>word_820D1998</c>), and the step unit
/// the font's <c>+0x24</c> — the .bft header's fifth word, the average character width
/// (<c>sub_82D3F638</c> stores the header in order at +0x18, +0x1C, +0x38 (byte), +0x20, +0x24);</item>
/// <item>a quantity: the first <c>" x"</c> (<c>word_820D4D34</c>) plus the characters after it up
/// to the next space is cut out and its width taken from the room — when the character after
/// <c>" x"</c> is a space or the string ends there, nothing is cut or charged but <c>" x"</c> is
/// still appended at the end;</item>
/// <item>the "best" mark <c>" &lt;s2&gt;(" + GUI_SCREEN_INVENTORY_BEST + ")&lt;/s2&gt;"</c>
/// (<c>word_820D4D3C</c>, <c>unk_820D4D68</c>) is cut out and its width charged;</item>
/// <item><c>&lt;exclamation.img&gt;</c> (<c>word_820D4D78</c>) is cut out and the font's LINE
/// HEIGHT (+0x18) charged — not the icon's 26;</item>
/// <item><c>n = max(1, room / avg)</c> characters of the rest are kept, and while their width is
/// under the room and characters remain, <c>n += max(1, (room − width) / avg)</c> — the cut can
/// end over the room; it never backs off;</item>
/// <item>then <c>...</c>, the quantity, the best mark and the icon are appended in that order and
/// the result goes back through <c>SetText</c> (<c>sub_82B40840</c>), markup and all.</item>
/// </list>
/// Characters are counted in the raw string, so a cut can fall inside a tag, as the guest's does.
/// Widths are the layout's own measure (<c>sub_82B40B28</c> → <c>sub_82D40300</c> with the icon
/// biller <c>sub_82B40A40</c>), integer pixels.
/// </remarks>
public static class Engine_UiTextTruncate
{
	/// <summary>The dots (<c>word_820D1998</c>).</summary>
	public const string Ellipsis = "...";

	/// <summary>The quantity mark (<c>word_820D4D34</c>).</summary>
	public const string QuantityMark = " x";

	/// <summary>The Babel tag the best mark is built from.</summary>
	public const string BestTag = "GUI_SCREEN_INVENTORY_BEST";

	/// <summary>The exclamation icon (<c>word_820D4D78</c>).</summary>
	public const string Exclamation = "<exclamation.img>";

	/// <summary>The best mark around its localised word (<c>word_820D4D3C</c> + word + <c>unk_820D4D68</c>).</summary>
	public static string BestMark(string best) => " <s2>(" + best + ")</s2>";

	/// <summary>
	/// The string to draw for <paramref name="source"/> under a truncate of
	/// <paramref name="truncate"/> pixels: <paramref name="source"/> itself when it fits or no
	/// truncate is set, else the ellipsised form.
	/// </summary>
	/// <param name="measure">The single-line width of a string, markup included, in pixels.</param>
	/// <param name="averageCharWidth">The font's average character width (.bft header word 5).</param>
	/// <param name="lineHeight">The font's line height (.bft header word 2).</param>
	/// <param name="best">
	/// The localised <see cref="BestTag"/>; null when no string table answers, and then no best
	/// mark is looked for.
	/// </param>
	public static string Apply(string source, int truncate, Func<string, int> measure,
		int averageCharWidth, int lineHeight, string? best)
	{
		if (truncate <= 0 || measure(source) <= truncate)
		{
			return source;
		}

		int room = truncate - measure(Ellipsis);

		// The guest divides by +0x24 unguarded; every shipped GUI font has a positive one.
		int unit = Math.Max(1, averageCharWidth);
		string rest = source;

		string quantity = QuantityMark;
		bool hasQuantity = false;
		int mark = rest.IndexOf(QuantityMark, StringComparison.Ordinal);
		if (mark >= 0)
		{
			hasQuantity = true;
			int at = mark + QuantityMark.Length;
			if (at < rest.Length && rest[at] != ' ')
			{
				int end = at;
				while (end < rest.Length && rest[end] != ' ')
				{
					end++;
				}

				quantity = QuantityMark + rest[at..end];
				room -= measure(quantity);
				rest = rest.Replace(quantity, string.Empty, StringComparison.Ordinal);
			}
		}

		string? bestMark = best is null ? null : BestMark(best);
		bool hasBest = false;
		if (bestMark is not null && rest.Contains(bestMark, StringComparison.Ordinal))
		{
			hasBest = true;
			room -= measure(bestMark);
			rest = rest.Replace(bestMark, string.Empty, StringComparison.Ordinal);
		}

		bool hasExclamation = false;
		if (rest.Contains(Exclamation, StringComparison.Ordinal))
		{
			hasExclamation = true;
			room -= lineHeight;
			rest = rest.Replace(Exclamation, string.Empty, StringComparison.Ordinal);
		}

		int count = Math.Max(1, room / unit);
		string cut = Left(rest, count);
		for (int width = measure(cut); width < room; width = measure(cut))
		{
			if (count >= rest.Length)
			{
				break;
			}

			count += Math.Max(1, (room - width) / unit);
			cut = Left(rest, count);
		}

		string result = cut + Ellipsis;
		if (hasQuantity)
		{
			result += quantity;
		}

		if (hasBest)
		{
			result += bestMark;
		}

		if (hasExclamation)
		{
			result += Exclamation;
		}

		return result;
	}

	private static string Left(string text, int count) => text[..Math.Min(count, text.Length)];
}
