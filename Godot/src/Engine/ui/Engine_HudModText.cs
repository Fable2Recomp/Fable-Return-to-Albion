using System;

namespace Refii.Ui;

/// <summary>
/// What a mod's <c>text</c> HUD piece says (<see cref="Engine_HudModPieces"/>): its <c>"text"</c>
/// is a literal, a Babel tag, or a value source (<see cref="Engine_UiValueSources"/>: <c>mod:</c>,
/// <c>game:</c>, <c>player:</c>, <c>hero:</c>) drawn through <c>"format"</c> — a <c>game:</c>
/// text field is drawn as the guest wrote it — and <c>"align"</c> picks the edge the text hangs from.
/// </summary>
/// <remarks>
/// <para>A tag is tried in the string book first and the text drawn as written when the book has
/// no such tag, the game's own fallback for an unresolved tag. A <c>mod:</c> value that is not
/// stated draws nothing — "not stated" must not render as a number.</para>
/// <para><c>"format"</c> is a .NET composite (<c>"Stamina {0:0}"</c>) or a bare numeric format
/// (<c>"0.0"</c>, <c>"N0"</c>), invariant culture; empty draws up to two decimals. Pure, so the
/// rules are held by tests.</para>
/// </remarks>
public static class Engine_HudModText
{
	public const string ModPrefix = "mod:";

	/// <summary>
	/// The piece's string now, or null when it states nothing (an unpublished value, an empty text).
	/// Only <c>mod:</c> answers here; <see cref="Engine_UiValueSources.Text"/> is the whole rule.
	/// </summary>
	public static string? Resolve(string text, string format, Func<string, float?> modValue, Func<string, string?> tag) =>
		Engine_UiValueSources.Text(text, format, new Engine_UiValueSources.Context { Mod = modValue }, tag);

	/// <summary>The piece's string now for one reader (<see cref="Engine_UiValueSources.Context"/>).</summary>
	public static string? Resolve(string text, string format, Engine_UiValueSources.Context context, Func<string, string?> tag) =>
		Engine_UiValueSources.Text(text, format, context, tag);

	/// <summary>A value through the piece's format; a format that does not parse draws the plain number.</summary>
	public static string Format(string format, float value) => Engine_UiValueSources.Format(format, value);

	/// <summary>
	/// The text record's horizontal alignment (0 left, 1 centre, 2 right — <c>UiText.HorzAlign</c>):
	/// the piece's point is the text's left edge, its centre or its right edge.
	/// </summary>
	public static int Align(string align) => align.Trim().ToLowerInvariant() switch
	{
		"centre" or "center" => 1,
		"right" => 2,
		_ => 0,
	};

	/// <summary>The names <see cref="Align"/> reads, for the validator and the Studio.</summary>
	public static readonly string[] AlignNames = { "left", "centre", "center", "right" };

	/// <summary>The point size a text piece is drawn at when it states none — the HUD's own text size.</summary>
	public const int DefaultSize = 18;
}
