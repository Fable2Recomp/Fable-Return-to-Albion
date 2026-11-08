using Godot;

namespace Refii.Ui;

/// <summary>
/// The GUI text engine's inline markup — the tags a string may carry between `&lt;` and `&gt;`,
/// and what each does to the run that follows. The tokeniser is the same in both titles; the
/// VOCABULARY is each title's own (<see cref="Vocabulary.Fable2"/>, <see cref="Vocabulary.Fable3"/>),
/// reached through the title's <see cref="Engine_UiTextContract.Markup"/>.
///
/// Fable 2 (TU1):
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
///
/// Fable 3 (360 disc): 65 tags. `sub_82B41050` seeds the `std::map&lt;LHString,int&gt;` (lookup
/// `sub_82B41690`, a miss returns 0) from 65 `LHString` handles at `0x8355DBB8` (stride 4, UTF-16
/// names at `0x820D3B3C`..`0x820D40D8`). `sub_82B40840` rewrites `\n` (`0x82085780`) to `&lt;br&gt;`
/// (`0x8209F248`) and calls the tokeniser `sub_82B40D00`, which pairs codes 1..7 with a synthesised
/// `"&lt;/" + name + "&gt;"` close. The draw `sub_821F6180` switches on the code: 8..0x41 (except
/// 0xD, the break) are icons, the rest colour runs.
/// </summary>
public static class Engine_UiTextMarkup
{
	/// <summary>What a tag does. The code decides which of the guest's two switches runs.</summary>
	public enum Kind
	{
		/// <summary>A PAIRED tag that tints the run it encloses (Fable 2 codes 1..5, Fable 3 1..7).</summary>
		Colour,

		/// <summary>`&lt;br&gt;`, the line break (Fable 2 code 11, Fable 3 code 13).</summary>
		LineBreak,

		/// <summary>A standalone image (every other code).</summary>
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
	/// True for an icon that draws and advances twice as wide: Fable 2's `star-1`..`star-5`
	/// (`sub_82802F80` case 30..34 is `2 * dword_83496D48`); Fable 3's stars and `motif.img`
	/// (`sub_821F6180` cases 0x20..0x24 and 0x2E pass side 64 to `sub_82B457A8` and advance 52).
	/// </param>
	public readonly record struct Tag(
		string Name, int Code, Kind Kind, string Texture, Color Colour, bool DoubleWidth);

	/// <summary>
	/// Fable 2's `&lt;b&gt;` — same RGB as `s1` but alpha forced opaque, not the caller's. Not a
	/// bold face: nothing on this path selects a font.
	/// </summary>
	public const int OpaqueTag = 1;

	/// <summary>Fable 2's `&lt;br&gt;`. `sub_82802DB8` rewrites every `\n` to this before parsing.</summary>
	public const int LineBreakCode = 11;

	/// <summary>
	/// Fable 2's codes that take a closing form — only these five pair. `sub_828031B8` tests
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
	/// Fable 2's tags, in code order. Index is not the code — use <see cref="Vocabulary.Find"/>.
	/// The art paths are not read back directly from the image: each icon case passes its path
	/// to `sub_8222CED0` through a stack slot rather than loading a literal at the case.
	/// </summary>
	private static readonly Tag[] Fable2Tags =
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

	// Fable 3's colours. `b` loads the vector at 0x820FD340 (0.78, 0.26, 0.30, 1.0) and hands the
	// run draw `sub_82B45538` style 2 where the others pass the element's +0x5C (not drawn here:
	// no style is modelled); s1..s4 and s6 load the vectors 0x8355DD50/60/70/80/90, built at
	// static init (0x831EF738..) from the D3DCOLOR bytes at 0x832A5054 + 4k times 1/255
	// (flt_832A8930) — byte 2 is red, 1 green, 0 blue, 3 alpha (255 in every one); s5 is grey
	// 0.5 at HALF the caller's opacity (`v21 * 0.5`, sub_821F6180 default case, code 6).
	private static readonly Color F3Bold = new(0.78f, 0.26f, 0.30f);
	private static readonly Color F3Red = new(200 / 255f, 67 / 255f, 77 / 255f);
	private static readonly Color F3Green = new(177 / 255f, 224 / 255f, 167 / 255f);
	private static readonly Color F3Blue = new(167 / 255f, 205 / 255f, 224 / 255f);
	private static readonly Color F3Gold = new(241 / 255f, 201 / 255f, 115 / 255f);
	private static readonly Color F3Faint = new(0.5f, 0.5f, 0.5f, 0.5f);

	private const string F3Buttons = @"art\gui_memmount\HUDAssests\Version4\Buttons\";
	private const string F3Controller = @"art\gui_memmount\controller\";
	private const string F3Small = F3Controller + @"24x24\";
	private const string F3Opinion = @"art\gui_memmount\wheelicons\splashopinions\opinion_";
	private const string F3Slide = @"art\gui_memmount\hudassests\version4\middleelements\slideinnotification\";
	private const string F3Collectable = F3Slide + @"collectables\";

	/// <summary>
	/// Fable 3's tags, in code order: the names and codes `sub_82B41050` inserts, each icon's art
	/// the case of `sub_821F6180` that draws it states (`sub_8221F788` on the literal, `.tex`
	/// appended by `sub_82B457A8` at 0x82B457F8). Cases 0x31..0x3F build `collectables\` + a name
	/// (`sub_821DB270`, prefix 0x8355DD04 = static init 0x831EF2B8; names 0x8355DD08..DD48 =
	/// 0x831EF2F8..0x831EF6F8); 0x40/0x41 draw the whole paths 0x8355DD40/DD44.
	/// </summary>
	private static readonly Tag[] Fable3Tags =
	{
		new("b", 1, Kind.Colour, "", F3Bold, false),
		new("s1", 2, Kind.Colour, "", F3Red, false),
		new("s2", 3, Kind.Colour, "", F3Green, false),
		new("s3", 4, Kind.Colour, "", F3Blue, false),
		new("s4", 5, Kind.Colour, "", F3Gold, false),
		new("s5", 6, Kind.Colour, "", F3Faint, false),
		new("s6", 7, Kind.Colour, "", F3Gold, false),

		new("a_img", 8, Kind.Icon, F3Buttons + @"A\24x24_NoRim\A_Complete", default, false),
		new("b_img", 9, Kind.Icon, F3Buttons + @"B\24x24_NoRim\B_Complete", default, false),
		new("x_img", 10, Kind.Icon, F3Buttons + @"X\24x24_NoRim\X_Complete", default, false),
		new("y_img", 11, Kind.Icon, F3Buttons + @"Y\24x24_NoRim\Y_Complete", default, false),
		new("gold_img", 12, Kind.Icon,
			@"Art\gui_memmount\hudassests\version4\lowerelements\infobox\CurrentcySmall", default, false),

		new("br", 13, Kind.LineBreak, "", default, false),

		// Code 14 is shadowed by code 31 (inserted again at 0x8355DC30), as Fable 2's 12 is by 29.
		new("rightup.img", 14, Kind.Icon, F3Small + "RightUp", default, false),

		new("start.img", 15, Kind.Icon, F3Buttons + @"Start\24x24_NoRim\Start_Complete", default, false),
		new("back.img", 16, Kind.Icon, F3Buttons + @"Back\24x24_NoRim\Back_Complete", default, false),
		new("dpad.img", 17, Kind.Icon, F3Buttons + @"Dpad\24x24_NoRim\Dpad_Complete", default, false),
		new("left.img", 18, Kind.Icon, F3Buttons + @"LeftStick\24x24_NoRim\LS_Complete", default, false),
		new("leftbutton.img", 19, Kind.Icon, F3Buttons + @"LeftButton\24x24_NoRim\LB_Complete", default, false),
		new("leftdown.img", 20, Kind.Icon, F3Small + "LeftDown", default, false),
		new("leftleft.img", 21, Kind.Icon, F3Small + "LeftLeft", default, false),
		new("leftright.img", 22, Kind.Icon, F3Small + "LeftRight", default, false),
		new("lefttrigger.img", 23, Kind.Icon, F3Buttons + @"LeftTrigger\24x24_NoRim\LT_Complete", default, false),

		// NOT under 24x24 — the shipped path (0x820D482C), as Fable 2's is.
		new("leftup.img", 24, Kind.Icon, F3Controller + "LeftUp", default, false),

		new("right.img", 25, Kind.Icon, F3Buttons + @"RightStick\24x24_NoRim\RS_Complete", default, false),
		new("rightbutton.img", 26, Kind.Icon, F3Buttons + @"RightButton\24x24_NoRim\RB_Complete", default, false),
		new("rightdown.img", 27, Kind.Icon, F3Small + "RightDown", default, false),
		new("rightleft.img", 28, Kind.Icon, F3Small + "RightLeft", default, false),
		new("rightright.img", 29, Kind.Icon, F3Small + "RightRight", default, false),
		new("righttrigger.img", 30, Kind.Icon, F3Buttons + @"RightTrigger\24x24_NoRim\RT_Complete", default, false),
		new("rightup.img", 31, Kind.Icon, F3Small + "RightUp", default, false),

		new("star-1.img", 32, Kind.Icon, Small + "star-1", default, true),
		new("star-2.img", 33, Kind.Icon, Small + "star-2", default, true),
		new("star-3.img", 34, Kind.Icon, Small + "star-3", default, true),
		new("star-4.img", 35, Kind.Icon, Small + "star-4", default, true),
		new("star-5.img", 36, Kind.Icon, Small + "star-5", default, true),

		new("good_img", 37, Kind.Icon, F3Opinion + "morality", default, false),
		new("evil_img", 38, Kind.Icon, F3Opinion + "evil", default, false),
		new("hate_img", 39, Kind.Icon, F3Opinion + "hate", default, false),
		new("fear_img", 40, Kind.Icon, F3Opinion + "fear", default, false),
		new("ugly_img", 41, Kind.Icon, F3Opinion + "ugly", default, false),
		new("love_img", 42, Kind.Icon, F3Opinion + "love", default, false),
		new("funny_img", 43, Kind.Icon, F3Opinion + "ridiculousness", default, false),
		new("attractive_img", 44, Kind.Icon, F3Opinion + "attractiveness", default, false),

		new("bullet.img", 45, Kind.Icon, F3Small + "bullet", default, false),
		new("motif.img", 46, Kind.Icon, F3Small + "motif", default, true),
		new("exclamation.img", 47, Kind.Icon, F3Small + "exclamation", default, false),
		new("e.img", 48, Kind.Icon, Small + "Extendable", default, false),

		new("silver_key.img", 49, Kind.Icon, F3Collectable + "silverkey", default, false),
		new("gold_key.img", 50, Kind.Icon, F3Collectable + "goldkey", default, false),
		new("chicken.img", 51, Kind.Icon, F3Collectable + "chicken", default, false),
		new("guild_seal.img", 52, Kind.Icon, F3Collectable + "guildseal", default, false),
		new("book.img", 53, Kind.Icon, F3Collectable + "books", default, false),
		new("gnome.img", 54, Kind.Icon, F3Collectable + "gnomes", default, false),
		new("flower.img", 55, Kind.Icon, F3Collectable + "flowers", default, false),
		new("flower_red.img", 56, Kind.Icon, F3Collectable + "flowers_red", default, false),
		new("flower_blue.img", 57, Kind.Icon, F3Collectable + "flowers_blue", default, false),
		new("flower_green.img", 58, Kind.Icon, F3Collectable + "flowers_green", default, false),
		new("flower_purple.img", 59, Kind.Icon, F3Collectable + "flowers_purple", default, false),
		new("flower_yellow.img", 60, Kind.Icon, F3Collectable + "flowers_yellow", default, false),
		new("flower_orange.img", 61, Kind.Icon, F3Collectable + "flowers_orange", default, false),
		new("insult_letter.img", 62, Kind.Icon, F3Collectable + "insultletters", default, false),
		new("lantern.img", 63, Kind.Icon, F3Collectable + "lanterns", default, false),
		new("dog_whistle.img", 64, Kind.Icon, F3Slide + "dogwhistle", default, false),
		new("wolfs_bane.img", 65, Kind.Icon, F3Collectable + "wolfsbane", default, false),
	};

	/// <summary>
	/// One title's tag table and the tokeniser over it. Both titles parse the same way; only the
	/// names, codes, colours and art differ.
	/// </summary>
	public sealed class Vocabulary
	{
		private readonly Tag[] _tags;

		private Vocabulary(Tag[] tags) => _tags = tags;

		/// <summary>Every tag, in code order.</summary>
		public System.Collections.Generic.IReadOnlyList<Tag> Tags => _tags;

		/// <summary>
		/// The tag for a run's text, or null when nothing matches. Ordinal and case-sensitive, as
		/// the guest's comparator is; last insert wins, so `rightup.img` answers its second code.
		/// </summary>
		public Tag? Find(string run)
		{
			Tag? hit = null;
			foreach (Tag tag in _tags)
			{
				if (string.Equals(tag.Name, run, System.StringComparison.Ordinal))
				{
					hit = tag;
				}
			}

			return hit;
		}

		/// <summary>Splits a resolved string into runs (see <see cref="Engine_UiTextMarkup.Split"/>).</summary>
		public System.Collections.Generic.List<Run> Split(string display)
		{
			var runs = new System.Collections.Generic.List<Run>();
			Parse(this, display.Replace("\n", "<br>"), null, runs, 0);
			return runs;
		}

		/// <summary>Fable 2's 46 tags.</summary>
		public static readonly Vocabulary Fable2 = new(Fable2Tags);

		/// <summary>Fable 3's 65 tags.</summary>
		public static readonly Vocabulary Fable3 = new(Fable3Tags);
	}

	/// <summary>The running title's tag for a run's text (<see cref="Vocabulary.Find"/>).</summary>
	public static Tag? Find(string run) => Engine_UiTextContract.Active.Markup.Find(run);

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
	public static System.Collections.Generic.List<Run> Split(string display) =>
		Engine_UiTextContract.Active.Markup.Split(display);

	/// <summary>
	/// Guards the recursion the paired tags introduce. The shipped parser has no such limit
	/// because its input is authored; ours takes whatever the guest hands over.
	/// </summary>
	private const int MaxDepth = 16;

	private static void Parse(Vocabulary vocabulary,
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

			if (vocabulary.Find(name) is not { } tag)
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
				Parse(vocabulary, inner, tint, into, depth + 1);
			}
			else
			{
				into.Add(new Run(inner, null, tint, false));
			}

			input = end < 0 ? string.Empty : rest[(end + closing.Length)..];
		}
	}
}
