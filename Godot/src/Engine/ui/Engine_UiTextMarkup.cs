using Godot;

namespace Refii.Ui;

/// <summary>
/// The GUI text engine's inline markup vocabulary — the 46 tags a string may carry between
/// `&lt;` and `&gt;`, and what each does to the run that follows.
///
/// Not one table in the binary: 46 UTF-16BE name literals at `0x820DA824`..`0x820DABC8`, 46
/// `LHString` handles at `0x8349BB3C` (stride 4, static-init thunks at `0x83268E58 + 0x40*k`),
/// and a `std::map&lt;LHString,int&gt;` at `0x8349BBF4` seeded by 46 `operator[]` inserts in
/// `sub_82803598`. The payload lives in two switches keyed on the code the map returns.
///
/// Codes are 1-based and contiguous and are the join: the guest's switches index off them
/// (`addi r11, r10, -6` at `0x8226EDE4` icons, `addi r11, r10, -1` at `0x822707B0` colours).
/// `rightup.img` is inserted twice — code 12 (`0x83269118`) and code 29 (`0x83269558`); with
/// `operator[]` the second wins, so code 12 is unreachable (same art either way).
///
/// Matching is exact, case-sensitive, ordinal: comparator `sub_82804090` is `wcscmp(a,b) &lt; 0`
/// over `sub_82CB01E8`, and every key is lowercase. A miss returns 0 (`sub_82803AC8`) and the
/// parser still consumes through the `&gt;`, emitting an empty segment that costs no width.
/// </summary>
public static class Engine_UiTextMarkup
{
	/// <summary>What a tag does. The code decides which of the guest's two switches runs.</summary>
	public enum Kind
	{
		/// <summary>Codes 1..5 — a PAIRED tag that tints the run it encloses.</summary>
		Colour,

		/// <summary>Code 11 — `&lt;br&gt;`, the line break.</summary>
		LineBreak,

		/// <summary>Codes 6..46 except 11 — a standalone image.</summary>
		Icon,
	}

	/// <summary>
	/// One tag.
	/// </summary>
	/// <param name="Name">The authored spelling, lowercase. The join — never paraphrase it.</param>
	/// <param name="Code">The guest's own code, 1-based, as the map returns it.</param>
	/// <param name="Texture">
	/// The guest's own art path for an <see cref="Kind.Icon"/>, backslashes and all. Empty for
	/// the colour tags and for `&lt;br&gt;`.
	/// </param>
	/// <param name="Colour">
	/// The tint a <see cref="Kind.Colour"/> tag applies. Its alpha is the caller's opacity for
	/// `s1`..`s4`; `&lt;b&gt;` is the one case that hardcodes 1.0 — see <see cref="OpaqueTag"/>.
	/// </param>
	/// <param name="DoubleWidth">
	/// True for `star-1`..`star-5`, the only icons that advance twice — `sub_82802F80` case
	/// 30..34 is `2 * dword_83496D48` where every other icon is `dword_83496D48`.
	/// </param>
	public readonly record struct Tag(
		string Name, int Code, Kind Kind, string Texture, Color Colour, bool DoubleWidth);

	/// <summary>
	/// `&lt;b&gt;` — same RGB as `s1` but alpha forced opaque, not the caller's. Not a bold face:
	/// nothing on this path selects a font.
	/// </summary>
	public const int OpaqueTag = 1;

	/// <summary>`&lt;br&gt;`. `sub_82802DB8` rewrites every `\n` to this before parsing.</summary>
	public const int LineBreakCode = 11;

	/// <summary>
	/// The codes that take a closing form — only these five pair. `sub_828031B8` tests
	/// `code &gt;= 1 &amp;&amp; code &lt;= 5` and synthesises the close as `"&lt;/" + name + "&gt;"` (closing
	/// spellings are not table entries). An unterminated open runs to the end of the string.
	/// </summary>
	public const int FirstPairedCode = 1;

	public const int LastPairedCode = 5;

	// The colours, from the constants the cases load: 0.0 (0x82099484), 0.75 (0x8209949C),
	// 1.0 (0x82099490), 0.4 (0x8209D5FC), 0.5 (0x820992D4), 0.88 (0x821018B0), 0.39 (0x821018B4).
	private static readonly Color Crimson = new(0.75f, 0.0f, 0.0f);
	private static readonly Color Green = new(0.0f, 0.39f, 0.0f);
	private static readonly Color Blue = new(0.4f, 0.4f, 1.0f);
	private static readonly Color Gold = new(1.0f, 0.88f, 0.5f);

	private const string Controller = @"art\gui\controller\";
	private const string Small = @"art\gui\controller\24x24\";
	private const string Opinion = @"art\gui\wheelicons\splashopinions\opinion_";

	/// <summary>
	/// Every tag, in code order. Index is not the code — use <see cref="Find"/>. The art paths
	/// are not read back directly from the image: each icon case passes its path to
	/// `sub_8222CED0` through a stack slot rather than loading a literal at the case.
	/// </summary>
	public static readonly Tag[] Tags =
	{
		new("b", 1, Kind.Colour, "", Crimson, false),
		new("s1", 2, Kind.Colour, "", Crimson, false),
		new("s2", 3, Kind.Colour, "", Green, false),
		new("s3", 4, Kind.Colour, "", Blue, false),
		new("s4", 5, Kind.Colour, "", Gold, false),

		new("a_img", 6, Kind.Icon, Controller + "icon_button_a", default, false),
		new("b_img", 7, Kind.Icon, Controller + "icon_button_b", default, false),
		new("x_img", 8, Kind.Icon, Controller + "icon_button_x", default, false),
		new("y_img", 9, Kind.Icon, Controller + "icon_button_y", default, false),
		new("gold_img", 10, Kind.Icon, Controller + "icon_gold_coin", default, false),

		new("br", 11, Kind.LineBreak, "", default, false),

		// Code 12 is shadowed by code 29 — see the class note. Listed so the codes stay dense.
		new("rightup.img", 12, Kind.Icon, Small + "RightUp", default, false),

		new("start.img", 13, Kind.Icon, Small + "Start", default, false),
		new("back.img", 14, Kind.Icon, Small + "Back", default, false),
		new("dpad.img", 15, Kind.Icon, Small + "DPad", default, false),
		new("left.img", 16, Kind.Icon, Small + "Left", default, false),
		new("leftbutton.img", 17, Kind.Icon, Small + "LeftButton", default, false),
		new("leftdown.img", 18, Kind.Icon, Small + "LeftDown", default, false),
		new("leftleft.img", 19, Kind.Icon, Small + "LeftLeft", default, false),
		new("leftright.img", 20, Kind.Icon, Small + "LeftRight", default, false),
		new("lefttrigger.img", 21, Kind.Icon, Small + "LeftTrigger", default, false),

		// NOT under 24x24 — the shipped path, reproduced rather than regularised.
		new("leftup.img", 22, Kind.Icon, Controller + "LeftUp", default, false),

		new("right.img", 23, Kind.Icon, Small + "Right", default, false),
		new("rightbutton.img", 24, Kind.Icon, Small + "RightButton", default, false),
		new("rightdown.img", 25, Kind.Icon, Small + "RightDown", default, false),
		new("rightleft.img", 26, Kind.Icon, Small + "RightLeft", default, false),
		new("rightright.img", 27, Kind.Icon, Small + "RightRight", default, false),
		new("righttrigger.img", 28, Kind.Icon, Small + "RightTrigger", default, false),
		new("rightup.img", 29, Kind.Icon, Small + "RightUp", default, false),

		new("star-1.img", 30, Kind.Icon, Small + "star-1", default, true),
		new("star-2.img", 31, Kind.Icon, Small + "star-2", default, true),
		new("star-3.img", 32, Kind.Icon, Small + "star-3", default, true),
		new("star-4.img", 33, Kind.Icon, Small + "star-4", default, true),
		new("star-5.img", 34, Kind.Icon, Small + "star-5", default, true),

		new("good_img", 35, Kind.Icon, Opinion + "morality", default, false),
		new("evil_img", 36, Kind.Icon, Opinion + "evil", default, false),
		new("hate_img", 37, Kind.Icon, Opinion + "hate", default, false),
		new("fear_img", 38, Kind.Icon, Opinion + "fear", default, false),
		new("ugly_img", 39, Kind.Icon, Opinion + "ugly", default, false),
		new("love_img", 40, Kind.Icon, Opinion + "love", default, false),
		new("funny_img", 41, Kind.Icon, Opinion + "ridiculousness", default, false),
		new("attractive_img", 42, Kind.Icon, Opinion + "attractiveness", default, false),

		new("bullet.img", 43, Kind.Icon, Small + "bullet", default, false),
		new("motif.img", 44, Kind.Icon, Small + "motif", default, false),
		new("exclamation.img", 45, Kind.Icon, Small + "exclamation", default, false),
		new("e.img", 46, Kind.Icon, Small + "Extendable", default, false),
	};

	/// <summary>
	/// The tag for a run's text, or null when nothing matches. Ordinal and case-sensitive, as the
	/// guest's comparator is; last insert wins, so `rightup.img` answers 29, never 12.
	/// </summary>
	public static Tag? Find(string run)
	{
		Tag? hit = null;
		foreach (Tag tag in Tags)
		{
			if (string.Equals(tag.Name, run, System.StringComparison.Ordinal))
			{
				hit = tag;
			}
		}

		return hit;
	}

	/// <summary>
	/// The icon quad's size in font pixels, before any scale the caller applies.
	///
	/// The box is `lineHeight + 1` square — `lwz r11, 0x18(r7); addi r9, r11, 1` at `0x8226EB08`,
	/// where `font+0x18` is the `.bft` line height (<see cref="Engine_BitmapFont.LineHeight"/>).
	/// Stars are 2x1: case 24 at `0x8226FD60` doubles the width only (`slwi r31, r30, 1`).
	///
	/// The guest's wrap pass bills a different number (`sub_82802F80` returns the bare
	/// `lineHeight`, zero for colour tags); not reproduced, since the wrapping is ours.
	/// </summary>
	public static Vector2I DrawSize(Tag? tag, int lineHeight)
	{
		int side = lineHeight + 1;
		return tag is { Kind: Kind.Icon, DoubleWidth: true }
			? new Vector2I(2 * side, side)
			: new Vector2I(side, side);
	}

	/// <summary>
	/// The resource key for an icon's art — FNV-1 over the lowercased path with `.tex` appended
	/// (`sub_828706F0` → `sub_822CA588`, basis `0x811C9DC5`, multiply THEN xor), the key the
	/// `.fac` UV records carry, not the sdbm32 used for element and attribute names.
	///
	/// The whole texture is the quad: `sub_828706F0` writes UVs from 0.0 and 1.0 only
	/// (`0x82870948`..`0x82870984`), so no sub-rect and no `.fac` v-flip — 41 separate textures.
	/// </summary>
	public static uint ResourceKey(Tag tag) =>
		Refii.Core.Engine_GameHash.ResourcePath(tag.Texture + ".tex");

	/// <summary>One piece of a split string.</summary>
	/// <param name="Text">The characters, for a run with no <paramref name="Tag"/>.</param>
	/// <param name="Tag">The icon, for an image run. Null on text and on the break.</param>
	/// <param name="Colour">
	/// The tint of the paired tag enclosing this run, or null to use the element's own colour.
	/// </param>
	/// <param name="Break">True for `&lt;br&gt;`.</param>
	public readonly record struct Run(string Text, Tag? Tag, Color? Colour, bool Break);

	/// <summary>
	/// Splits a resolved string into runs — `sub_828031B8`, the tokeniser every `.bgf` text
	/// element (kind byte 5) runs on its post-Babel string: `sub_82425F40` stores the resolved
	/// string at `element+140` and flags `+138`; `sub_82270A88` hands `+140` to `sub_82802DB8`
	/// (`SetText` on the markup sub-object at `element+116`), which calls this.
	///
	/// `sub_82802DB8` rewrites `\n` to `&lt;br&gt;` before parsing. Only codes 1..5 pair, with a
	/// synthesised `"&lt;/" + name + "&gt;"` close; a stray `&lt;/s2&gt;` looks up the literal `/s2`,
	/// misses, and is dropped — a miss emits an empty run and adds no width.
	/// </summary>
	public static System.Collections.Generic.List<Run> Split(string display)
	{
		var runs = new System.Collections.Generic.List<Run>();
		Parse(display.Replace("\n", "<br>"), null, runs, 0);
		return runs;
	}

	/// <summary>
	/// Guards the recursion the paired tags introduce. The shipped parser has no such limit
	/// because its input is authored; ours takes whatever the guest hands over.
	/// </summary>
	private const int MaxDepth = 16;

	private static void Parse(
		string input, Color? style, System.Collections.Generic.List<Run> into, int depth)
	{
		while (input.Length != 0)
		{
			int open = input.IndexOf('<');
			int close = input.IndexOf('>');
			if (open < 0 || close < 0 || close <= open)
			{
				into.Add(new Run(input, null, style, false));
				return;
			}

			if (open != 0)
			{
				into.Add(new Run(input[..open], null, style, false));
			}

			string name = input[(open + 1)..close];
			string rest = input[(close + 1)..];

			if (Find(name) is not { } tag)
			{
				// Code 0. Consumed, drawn as nothing, costing no width.
				input = rest;
				continue;
			}

			if (tag.Kind == Kind.LineBreak)
			{
				into.Add(new Run(string.Empty, null, style, true));
				input = rest;
				continue;
			}

			if (tag.Kind == Kind.Icon)
			{
				into.Add(new Run(string.Empty, tag, style, false));
				input = rest;
				continue;
			}

			// Paired. The tint is a MULTIPLIER over the element's own colour, so
			// `Engine_UiTheme.Retint` can recolour a label without rebuilding its glyphs.
			//
			// Stated divergence: `<b>` hardcodes alpha 1.0 in the guest (`0x822707EC` stores the
			// literal instead of `f31`) where `s1`..`s4` take the caller's opacity. A multiplier
			// cannot cancel the element's alpha, so `<b>` fades with its element here.
			Color tint = tag.Colour;

			string closing = "</" + name + ">";
			int end = rest.IndexOf(closing, System.StringComparison.Ordinal);
			string inner = end < 0 ? rest : rest[..end];

			if (depth < MaxDepth)
			{
				Parse(inner, tint, into, depth + 1);
			}
			else
			{
				into.Add(new Run(inner, null, tint, false));
			}

			input = end < 0 ? string.Empty : rest[(end + closing.Length)..];
		}
	}
}
