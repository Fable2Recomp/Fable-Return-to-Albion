namespace Refii.Ui;

/// <summary>
/// How a title's Gameface text element turns its authored attributes into a layout: the canvas its
/// box fractions are measured against, which GUI font bucket a point size draws from and at what
/// scale, and how far the pen drops per line. Both titles ship the same three fonts
/// (<c>language/&lt;culture&gt;/fonts/gui_{small,normal,large}.bft</c>) and the same text element
/// (<c>CTextAsset</c>), but the element's numbers differ, so a layout written for one draws the
/// other at the wrong size and wraps it at the wrong width.
/// </summary>
/// <remarks>
/// Addresses: Fable 2 TU1 (<see cref="Fable2"/>) and Fable 3 360 disc (<see cref="Fable3"/>); the
/// element's vtable is <c>0x82001B18</c> / <c>0x82001738</c> (PC RTTI <c>CTextAsset@NUI</c>).
/// </remarks>
public sealed class Engine_UiTextContract
{
	/// <summary>The width a <c>.fac</c> TEXT record's <c>boxwidth</c> fraction is multiplied by.</summary>
	public float BoxWidthUnits { get; }

	/// <summary>The height a <c>.fac</c> TEXT record's <c>boxheight</c> fraction is multiplied by.</summary>
	public float BoxHeightUnits { get; }

	/// <summary>The point size that draws GUI_Normal; below it draws GUI_Small, above it GUI_Large.</summary>
	public int NormalSize { get; }

	/// <summary>
	/// Whether a size off a bucket boundary draws its bucket scaled (Fable 2: below 18 by
	/// <c>size/18</c>, above 24 by <c>size/24</c>) or at the bucket's own size (Fable 3).
	/// </summary>
	public bool ScalesOffBucket { get; }

	/// <summary>
	/// Pixels the draw adds to the font's <c>LineHeight</c> when it starts the next line. The
	/// measured block is <c>lines × (LineHeight + 1)</c> in both titles.
	/// </summary>
	public int LineAdvanceExtra { get; }

	/// <summary>
	/// An inline icon's pen advance, doubled for a double-width tag; 0 advances by what it draws
	/// (Fable 2: the <c>LineHeight + 1</c> square, <see cref="Engine_UiTextMarkup.DrawSize"/>).
	/// </summary>
	public float IconAdvance { get; }

	/// <summary>An inline icon's drawn side; 0 draws the <c>LineHeight + 1</c> square.</summary>
	public float IconSide { get; }

	/// <summary>Where an icon's top-left sits from the pen, Y up: left by this, up by this.</summary>
	public float IconInset { get; }

	/// <summary>The inline-markup tag table: which tags exist, their codes, colours and icon art.</summary>
	public Engine_UiTextMarkup.Vocabulary Markup { get; }

	/// <summary>
	/// Whether a single-line text element with a <c>truncate</c> attribute is ellipsised when it
	/// measures wider (<see cref="Engine_UiTextTruncate"/>).
	/// </summary>
	public bool Truncates { get; }

	/// <summary>
	/// Whether a colour tag's run REPLACES the element's colour and opacity (Fable 3) instead of
	/// multiplying them (Fable 2). Fable 3's markup draw sub_821F6180 starts every run from the text
	/// object's colour vector +0x40 (the ctor's (1, 1, 1, 1) from unk_820013A0, 0x82B44EB4; the
	/// `textcolor` attributes write its RGB, CTextAsset_vf3 0x82B463B0..0x82B46460), whose alpha lane
	/// +0x4C is the opacity CTextAsset slot 5 sub_82B46680 writes (vperm mask 0x82065380, 0x82B466F0;
	/// its caller is not traced), and a colour code
	/// overwrites all four lanes with the tag's own vector (codes 2..5 and 7: 0x821F8624..0x821F8634;
	/// `b`: 0x821F85A4..0x821F85B8, vector 0x820FD340), whose alpha is 1.0 (bytes 0x832A5054.. all
	/// 0xFF). `s5` alone keeps the element's alpha, halved (0.5 grey, `f31 * f28` at 0x821F85E8).
	/// </summary>
	public bool ColourRunsReplace { get; private init; }

	/// <summary>
	/// The element alpha at or below which a text draws nothing (Fable 3: sub_821F6180 reads +0x4C
	/// into f31 at 0x821F61E0 and skips the whole draw when <c>f31 &lt;= flt_832A18DC</c> =
	/// 0.09765625, 0x821F61E4..0x821F61E8); negative draws at any alpha. The floor matters to the
	/// replacing runs, which would otherwise stay opaque as their element fades out.
	/// </summary>
	public float AlphaFloor { get; private init; } = -1f;

	/// <summary>
	/// Whether a run drawn at style 1 is outlined (Fable 3). Every run hands <c>sub_82B45538</c> a
	/// style in r8 (<c>mr r29, r8</c> 0x82B45560): <c>&lt;b&gt;</c> passes 2 (<c>li r8, 2</c>
	/// 0x821F85A0), every other run its element's +0x5C (<c>lwz r8, 0x5C(r29)</c> 0x821F855C). Only
	/// style 1 sets the packet's byte +0x2C (<c>addi r8, r29, -1; cntlzw; extrwi</c>
	/// 0x82B45708..0x82B45728 → sub_823595E0 <c>stb r30, 0x2C</c> 0x82359664 → instance +212,
	/// 0x821BAF5C), which selects pixel shader 115 PSHADER_GUI_OUTLINED_TEXT_3D over 114
	/// PSHADER_GUI_TEXT_3D (<c>li r3, 0x73</c> / <c>0x72</c> 0x82C9955C / 0x82C99568, ShadersRelease.sbk
	/// entries 115 / 114). That shader takes the 3x3 maximum of the glyph coverage one font texel
	/// apart (c48 = 1/atlas size, param 0x67 0x82C99764 → c48) as alpha and the centre coverage
	/// times the run colour as RGB, so the ring around a glyph is the run's alpha in black. The
	/// <c>TextOutlineColours</c> vector the style also picks (0x82B455F0..0x82B4567C) goes to param
	/// 0xAE (0x82C997D4, 0x82C77384), which no shader in either bank binds, so
	/// <c>sub_82D5D010</c>'s <c>count &gt;= 1</c> test (0x82D5D040) drops it.
	/// </summary>
	public bool Outlines { get; private init; }

	/// <summary>
	/// The style a world icon's title and opinion lines draw at (<see cref="Engine_HudWorldSprites"/>);
	/// 3, <see cref="UiText.Style"/>'s default, leaves the labels unstyled. Fable 3: 1. Those lines
	/// are CWorldIcon3D's +156 / +372 strings, which its draw (vf16 sub_82B50AB8) packs straight into
	/// a text packet, not through a HUD element: the title at 0x82B51030 and the opinion at
	/// 0x82B511EC, each with <c>li r10, 1</c> (0x82B50FD8, 0x82B511A0), which sub_823595E0 stores as
	/// the outline byte (<c>mr r30, r10</c> 0x82359604, <c>stb r30, 0x2C</c> 0x82359664) that
	/// CPrimitiveInstance3DText copies to +212 (vf5 0x821BAF50..0x821BAF5C), the byte style 1 sets.
	/// </summary>
	public int WorldIconTextStyle { get; private init; } = 3;

	/// <summary>A <c>.fac</c> TEXT record's style as the element stores it: below 4 kept, else 0
	/// (<c>cmplwi r5, 4</c> 0x82B45FD8, <c>stw r27 = 0</c> / <c>stw r5, 0x5C</c> 0x82B45FE0 / 0x82B45FE8).</summary>
	public static int ElementStyle(int facStyle) => (uint)facStyle < 4 ? facStyle : 0;

	/// <summary>The style a run draws at: <c>&lt;b&gt;</c> (markup code 1) 2, any other run its element's.</summary>
	public static int RunStyle(int elementStyle, int markupCode) => markupCode == 1 ? 2 : elementStyle;

	/// <summary>Whether a run at <paramref name="runStyle"/> draws the outline (<see cref="Outlines"/>).</summary>
	public bool Outlined(int runStyle) => Outlines && runStyle == 1;

	/// <summary>
	/// How a run with <paramref name="tint"/> (a colour tag's, or null for unmarked text) combines
	/// with its element: whether its RGB and its alpha replace the element's. Fable 3's `s5` is the
	/// one tag whose vector alpha is below 1 (half the element's), so it replaces the RGB only.
	/// </summary>
	public (bool Rgb, bool Alpha) RunReplaces(Godot.Color? tint) =>
		ColourRunsReplace && tint is { } colour ? (true, colour.A >= 1f) : (false, false);

	/// <summary>
	/// The player's text style (<c>ui_text_style</c>): true draws a title's console look where it has
	/// one (<see cref="HasConsoleLook"/>), false draws the plain coverage path. Set from the
	/// config before the first text material is made.
	/// </summary>
	public static bool ConsoleLook { get; set; } = true;

	/// <summary>Whether this title states a console text look the player can choose.</summary>
	public bool HasConsoleLook => _snapGlyphs || _contrastFloor != 1f || _coverageGamma != 1f;

	private readonly bool _snapGlyphs;
	private readonly float _contrastFloor;
	private readonly float _coverageGamma;

	/// <summary>
	/// Whether <c>ui_text.gdshader</c> moves each glyph quad's corners to the nearest pixel edge under
	/// an orthographic camera (<c>snap_glyphs</c>).
	/// </summary>
	public bool SnapGlyphs => ConsoleLook && _snapGlyphs;

	/// <summary>
	/// The least factor the coverage is re-contrasted by about its half point from 0.75x
	/// magnification up (<c>contrast_floor</c>); 1.0 re-contrasts only magnified text, by its
	/// magnification.
	/// </summary>
	public float ContrastFloor => ConsoleLook ? _contrastFloor : 1f;

	/// <summary>
	/// The power a partly covered coverage-path pixel is taken to, mirrored for dark text
	/// (<c>coverage_gamma</c>); 1.0 is no curve.
	/// </summary>
	public float CoverageGamma => ConsoleLook ? _coverageGamma : 1f;

	private Engine_UiTextContract(float boxWidthUnits, float boxHeightUnits, int normalSize,
		bool scalesOffBucket, int lineAdvanceExtra, float iconAdvance, float iconSide, float iconInset,
		Engine_UiTextMarkup.Vocabulary markup, bool truncates, bool snapGlyphs, float contrastFloor,
		float coverageGamma)
	{
		Markup = markup;
		Truncates = truncates;
		_snapGlyphs = snapGlyphs;
		_contrastFloor = contrastFloor;
		_coverageGamma = coverageGamma;
		BoxWidthUnits = boxWidthUnits;
		BoxHeightUnits = boxHeightUnits;
		NormalSize = normalSize;
		ScalesOffBucket = scalesOffBucket;
		LineAdvanceExtra = lineAdvanceExtra;
		IconAdvance = iconAdvance;
		IconSide = iconSide;
		IconInset = iconInset;
	}

	/// <summary>An icon's drawn size, the double-width tags twice as wide.</summary>
	public (float Width, float Height) IconSize(bool doubleWidth, int lineHeight)
	{
		float side = IconSide > 0f ? IconSide : lineHeight + 1;
		return (doubleWidth ? 2f * side : side, side);
	}

	/// <summary>An icon's pen advance.</summary>
	public float IconPenAdvance(bool doubleWidth, int lineHeight) =>
		IconAdvance > 0f ? (doubleWidth ? 2f * IconAdvance : IconAdvance) : IconSize(doubleWidth, lineHeight).Width;

	/// <summary>
	/// Fable 2. Box: <c>CTextAsset_vf1</c> 0x82870E14 (<c>boxwidth × 960.0</c> → +0x50) and
	/// 0x82870E34 (<c>× 720.0</c> → +0x54). Font: <c>sub_828715E0</c> compares the size with 18 at
	/// 0x828715F8 and 0x82871644, stores <c>size × 1/18</c> (0x8287163C) below it and
	/// <c>size × 1/24</c> (0x82871694) above 24 in +0x5C, which the wrap and the measure multiply
	/// (<c>sub_828716D0</c> 0x82871838, 0x828718DC). Line: the draw's <c>&lt;br&gt;</c> case
	/// (0x8226F1B4) drops <c>(LineHeight + 1) × scale</c>. Icons: the <c>LineHeight + 1</c> square
	/// at the pen, advancing by what they draw. Markup: <see cref="Engine_UiTextMarkup.Vocabulary.Fable2"/>.
	/// Truncate: off. TU1 has the same rule as Fable 3 (<c>CTextAsset_vf3</c> reads
	/// <c>truncate</c> into +0xA0 at 0x828711F4; <c>sub_82871930</c> builds the ellipsis) and 52 of
	/// its <c>.bgf</c> files carry the attribute (260 records), but Fable 2's UI was verified
	/// without it and must not move, so it stays off until that is asked for. Shader: snapped glyphs,
	/// a 1.3 contrast floor and the 2.2 coverage curve (c614ef6f5, checked against Fable 2 screens).
	/// </summary>
	public static readonly Engine_UiTextContract Fable2 = new(960f, 720f, 18, true, 1, 0f, 0f, 0f,
		Engine_UiTextMarkup.Vocabulary.Fable2, truncates: false,
		snapGlyphs: true,
		contrastFloor: 1.3f,
		coverageGamma: 2.2f);

	/// <summary>
	/// Fable 3. Box: <c>CTextAsset_vf1</c> 0x82B45F70 (<c>boxwidth × 1280.0</c>, flt_8206D2A0 →
	/// +0x50) and 0x82B45F90 (<c>× 720.0</c> → +0x54); the constructor's defaults are the same
	/// 1280 × 720 (0x82B44ECC, 0x82B44ED4). Font: <c>sub_82B46708</c> compares the size with 16 at
	/// 0x82B46720 — below takes bucket 0 (0x82B4672C), equal bucket 1 (0x82B46740), above bucket 2
	/// (0x82B46750) — and stores no scale: the only multipliers the wrap and the measure apply
	/// (<c>sub_82B467A0</c> 0x82B46898, 0x82B46930/0x82B4694C) are +0xA4/+0xA8, which the
	/// constructor sets from the back buffer (<c>(w/h)/(16/9)</c> 0x82B45034, <c>h/720</c>
	/// 0x82B4502C), 1.0 on the 1280×720 canvas. Line: the draw reads the font's +0x18 LineHeight
	/// with no +1 (<c>sub_821F6180</c> 0x821F624C → 0x821F6298 <c>× +0xA8</c>), and its newline
	/// token (case 0xD) drops that; the measure (<c>sub_82D40300</c> 0x82D40354, 0x82D403A4) still
	/// counts <c>LineHeight + 1</c> per line. Icons: every icon token (8..0x41) calls
	/// <c>sub_82B457A8</c> with side 32 and advances the pen 26 (case 8: <c>li r6, 0x20</c> at
	/// 0x821F67D8, the call at 0x821F67EC, <c>+ f30</c> at 0x821F6808 with f30 = flt_82074480 = 26.0
	/// loaded at 0x821F6428; the line-width sum <c>sub_82B45320</c> 0x82B453F8 and the wrap's
	/// <c>sub_82B40A40</c> 0x82B40B00 bill 26, 52 for its wide class at 0x82B40AF8), from a pen moved
	/// left 6 and up 6 for the icon (<c>sub_821F6180</c> 0x821F661C <c>x − 6.0</c>, 0x821F6644
	/// <c>y + 6.0</c>, flt_8206C77C). The wide class (stars, <c>motif.img</c>) passes side 64 and
	/// advances 52: <c>sub_82B457A8</c> draws <c>side</c> wide (0x82B458A8..0x82B458E8) and a fixed
	/// 32 tall (flt_8206BFB4 at 0x82B45860, <c>fsubs</c> 0x82B45888). Markup:
	/// <see cref="Engine_UiTextMarkup.Vocabulary.Fable3"/>. Truncate: on (<c>sub_82B46980</c>).
	/// Shader: the shipped coverage, re-contrasted only when magnified (the 1280x720 list rows).
	/// Colour runs replace the element's colour and opacity, above the 0.09765625 draw floor
	/// (<see cref="ColourRunsReplace"/>, <see cref="AlphaFloor"/>). Style-1 runs are outlined
	/// (<see cref="Outlines"/>), and so are the world icons' title and opinion lines
	/// (<see cref="WorldIconTextStyle"/>).
	/// </summary>
	public static readonly Engine_UiTextContract Fable3= new(1280f, 720f, 16, false, 0, 26f, 32f, 6f,
		Engine_UiTextMarkup.Vocabulary.Fable3, truncates: true,
		snapGlyphs: false,
		contrastFloor: 1f,
		coverageGamma: 1f)
	{
		ColourRunsReplace = true,
		AlphaFloor = 0.09765625f,
		Outlines = true,
		WorldIconTextStyle = 1,
	};

	/// <summary>The contract of the title this process runs.</summary>
	public static Engine_UiTextContract Active => Refii.Core.Engine_Title.Active.UiText;

	/// <summary>The bucket a point size lands in and the residual scale it draws at.</summary>
	public (UiFontBucket Bucket, float Scale) SelectFor(int pointSize)
	{
		if (pointSize < NormalSize)
		{
			return (UiFontBucket.Small, ScalesOffBucket ? pointSize / (float)NormalSize : 1f);
		}

		if (pointSize == NormalSize)
		{
			return (UiFontBucket.Normal, 1f);
		}

		return (UiFontBucket.Large, ScalesOffBucket && pointSize > 24 ? pointSize / 24f : 1f);
	}
}
