using System;
using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// THE VALUE SOURCES a UI record reads by name — a HUD piece's <c>value</c>, <c>max</c>,
/// <c>widthFrom</c>, <c>index</c> and <c>text</c>, a copy's meter <c>values</c>, a screen page's
/// bound fields — resolved one way for the game's pieces and a mod's alike.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>a number — itself.</item>
/// <item><c>mod:&lt;mod&gt;.&lt;name&gt;</c> — what a mod's code published (<c>publish</c>,
/// <c>Refii.publish</c>, <c>refii::mods::Publish</c>).</item>
/// <item><c>game:&lt;name&gt;</c> — what the GUEST published for its own HUD under a <c>hud.bgf</c>
/// binding name: a meter 0..100 (<c>game:PlayerHealth</c>) or, read as text, a text field
/// (<c>game:QuestTitle</c>). The game's own pieces read exactly these.</item>
/// <item><c>player:&lt;key&gt;</c> — the per-player value store (<c>Refii.player_set</c>,
/// <c>refii_mod_player_set</c>, <c>RefiiHost.player_set</c>), read for the READER'S player: a
/// piece of player two's (their half, a joiner's window, a copy of their widget) reads player
/// two's value under the same key, so one record serves both players.</item>
/// <item><c>hero:health</c> / <c>hero:health_max</c> — the local hero's vitals.</item>
/// <item><c>setting:&lt;mod&gt;.&lt;name&gt;</c> — a mod setting in force (<c>settings.json</c>
/// with the player's choice and any live change over it), the value <c>Refii.setting</c> answers.</item>
/// </list>
/// <para>A source that states nothing resolves to null, never to 0 or an empty string: "not
/// stated" must not render as empty or full. Pure .NET; the readers are supplied by whoever owns
/// them (<see cref="Context"/>), so the rules are held by tests.</para>
/// </remarks>
public static class Engine_UiValueSources
{
	public const string ModPrefix = "mod:";
	public const string GamePrefix = "game:";
	public const string PlayerPrefix = "player:";
	public const string HeroPrefix = "hero:";
	public const string SettingPrefix = "setting:";

	/// <summary>The hero sources: the local hero's vitals.</summary>
	public static readonly string[] HeroKeys = { "health", "health_max" };

	public enum Kind
	{
		/// <summary>Nothing written.</summary>
		Empty,

		/// <summary>A literal number.</summary>
		Number,

		/// <summary><c>mod:&lt;key&gt;</c>.</summary>
		Mod,

		/// <summary><c>game:&lt;name&gt;</c>.</summary>
		Game,

		/// <summary><c>player:&lt;key&gt;</c>.</summary>
		Player,

		/// <summary><c>hero:health</c> / <c>hero:health_max</c>.</summary>
		Hero,

		/// <summary><c>setting:&lt;mod&gt;.&lt;name&gt;</c>.</summary>
		Setting,

		/// <summary>Not a source: a literal or a tag where text is read, an error where a number is.</summary>
		Other,
	}

	/// <summary>One parsed source: its kind, the key after the prefix, and the number for a literal.</summary>
	public readonly record struct Source(Kind Kind, string Key, float Number)
	{
		/// <summary>Whether this names a value someone publishes (not a literal, not empty).</summary>
		public bool Named => Kind is Kind.Mod or Kind.Game or Kind.Player or Kind.Hero or Kind.Setting;
	}

	/// <summary>
	/// Who answers each kind of source, for ONE reader. <see cref="Player"/> is the reader's player
	/// (1 or 2), what <c>player:</c> reads under. <see cref="StandIn"/> is an editor preview's:
	/// asked first with the WHOLE source text (<c>"game:PlayerHealth"</c>), it answers what the game
	/// would; a null answer falls through to the real readers.
	/// </summary>
	public sealed record Context
	{
		public int Player { get; init; } = 1;
		public Func<string, float?>? Mod { get; init; }
		public Func<string, float?>? GameMeter { get; init; }
		public Func<string, string?>? GameText { get; init; }
		public Func<int, string, float?>? PlayerValue { get; init; }
		public Func<string, float?>? Hero { get; init; }
		public Func<string, float?>? Setting { get; init; }
		public Func<string, float?>? StandIn { get; init; }
		public Func<string, string?>? StandInText { get; init; }

		/// <summary>The same readers for another player's reader.</summary>
		public Context ForPlayer(int player) => player == Player ? this : this with { Player = player };

		/// <summary>No reader at all: only literals resolve.</summary>
		public static readonly Context None = new();
	}

	/// <summary>
	/// Each text's answer, per thread: every HUD piece asks for its sources every frame, the answer
	/// depends on the text alone, and working it out allocates the key. Bounded for the Studio's
	/// typing, the one place new texts keep arriving.
	/// </summary>
	[ThreadStatic]
	private static Dictionary<string, Source>? _parsed;

	private const int ParsedLimit = 4096;

	/// <summary>What <paramref name="text"/> names. Whitespace around it is ignored.</summary>
	public static Source Parse(string? text)
	{
		string written = text ?? string.Empty;
		Dictionary<string, Source> parsed = _parsed ??= new Dictionary<string, Source>(StringComparer.Ordinal);
		if (parsed.TryGetValue(written, out Source known))
		{
			return known;
		}

		if (parsed.Count >= ParsedLimit)
		{
			parsed.Clear();
		}

		return parsed[written] = ParseText(written);
	}

	private static Source ParseText(string text)
	{
		string source = text.Trim();
		if (source.Length == 0)
		{
			return new Source(Kind.Empty, string.Empty, 0f);
		}

		if (float.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
		{
			return new Source(Kind.Number, string.Empty, number);
		}

		if (Prefixed(source, ModPrefix) is { } mod)
		{
			return new Source(Kind.Mod, mod, 0f);
		}

		if (Prefixed(source, GamePrefix) is { } game)
		{
			return new Source(Kind.Game, game, 0f);
		}

		if (Prefixed(source, PlayerPrefix) is { } player)
		{
			return new Source(Kind.Player, player, 0f);
		}

		if (Prefixed(source, HeroPrefix) is { } hero && Array.IndexOf(HeroKeys, hero) >= 0)
		{
			return new Source(Kind.Hero, hero, 0f);
		}

		if (Prefixed(source, SettingPrefix) is { } setting)
		{
			return new Source(Kind.Setting, setting, 0f);
		}

		return new Source(Kind.Other, source, 0f);
	}

	private static string? Prefixed(string source, string prefix) =>
		source.StartsWith(prefix, StringComparison.Ordinal) ? source[prefix.Length..].Trim() : null;

	/// <summary>Whether <paramref name="text"/> starts with one of the source prefixes (whatever follows).</summary>
	public static bool LooksLikeSource(string? text)
	{
		string source = (text ?? string.Empty).Trim();
		return source.StartsWith(ModPrefix, StringComparison.Ordinal) ||
			source.StartsWith(GamePrefix, StringComparison.Ordinal) ||
			source.StartsWith(PlayerPrefix, StringComparison.Ordinal) ||
			source.StartsWith(HeroPrefix, StringComparison.Ordinal) ||
			source.StartsWith(SettingPrefix, StringComparison.Ordinal);
	}

	/// <summary>
	/// Why <paramref name="text"/> cannot be read as a NUMBER, or null when it can: a number or a
	/// named source with a key. The validator's rule.
	/// </summary>
	public static string? NumberProblem(string? text)
	{
		Source source = Parse(text);
		return source.Kind switch
		{
			Kind.Number or Kind.Hero => null,
			Kind.Mod or Kind.Game or Kind.Player or Kind.Setting when source.Key.Length == 0 =>
				$"'{(text ?? string.Empty).Trim()}' names no value; write mod:<mod>.<name>, game:<meter>, player:<key> or setting:<mod>.<name>",
			Kind.Mod or Kind.Game or Kind.Player or Kind.Setting => null,
			Kind.Empty => "is empty",
			_ => $"'{text}' is not a value source: a number, mod:<mod>.<name>, game:<meter>, player:<key>, " +
				"setting:<mod>.<name>, hero:health or hero:health_max; it never states a value, so the piece never draws",
		};
	}

	/// <summary>
	/// Why <paramref name="text"/> cannot be read as TEXT, or null when it can. Anything that is not
	/// a source is a literal or a Babel tag; a prefix with no key is the one mistake.
	/// </summary>
	public static string? TextProblem(string? text)
	{
		Source source = Parse(text);
		return source.Kind is Kind.Mod or Kind.Game or Kind.Player or Kind.Setting && source.Key.Length == 0
			? $"'{(text ?? string.Empty).Trim()}' names no value; write mod:<mod>.<name>, game:<name>, player:<key> or setting:<mod>.<name>"
			: null;
	}

	/// <summary>The number <paramref name="text"/> states now for this reader, or null when it states none.</summary>
	public static float? Number(string? text, Context context)
	{
		Source source = Parse(text);
		if (source.Kind == Kind.Number)
		{
			return source.Number;
		}

		if (!source.Named || source.Key.Length == 0)
		{
			return null;
		}

		if (context.StandIn?.Invoke((text ?? string.Empty).Trim()) is { } standIn)
		{
			return standIn;
		}

		return source.Kind switch
		{
			Kind.Mod => context.Mod?.Invoke(source.Key),
			Kind.Game => context.GameMeter?.Invoke(source.Key),
			Kind.Player => context.PlayerValue?.Invoke(context.Player is 2 ? 2 : 1, source.Key),
			Kind.Hero => context.Hero?.Invoke(source.Key),
			Kind.Setting => context.Setting?.Invoke(source.Key),
			_ => null,
		};
	}

	/// <summary>
	/// What a text record says now for this reader: a source drawn through <paramref name="format"/>
	/// (a <c>game:</c> name is the guest's text field when it has one, else its meter), anything else
	/// a Babel tag (<paramref name="tag"/>) or, failing that, the literal as written — the game's own
	/// fallback for an unresolved tag. Null while a source states nothing or the text is empty.
	/// </summary>
	public static string? Text(string? text, string format, Context context, Func<string, string?> tag)
	{
		string written = text ?? string.Empty;
		Source source = Parse(written);
		switch (source.Kind)
		{
			case Kind.Empty:
				return null;

			case Kind.Other:
			case Kind.Number:
				return tag(written.Trim()) is { Length: > 0 } found ? found : written;

			case Kind.Game when source.Key.Length != 0:
				if (context.StandInText?.Invoke(written.Trim()) is { } standIn)
				{
					return standIn;
				}

				if (context.GameText?.Invoke(source.Key) is { } stated)
				{
					return stated;
				}

				break;
		}

		return Number(written, context) is { } value ? Format(format, value) : null;
	}

	/// <summary>A value through a format; a format that does not parse draws the plain number.</summary>
	public static string Format(string format, float value)
	{
		if (format.Length == 0)
		{
			return value.ToString("0.##", CultureInfo.InvariantCulture);
		}

		try
		{
			return format.Contains('{', StringComparison.Ordinal)
				? string.Format(CultureInfo.InvariantCulture, format, value)
				: value.ToString(format, CultureInfo.InvariantCulture);
		}
		catch (FormatException)
		{
			return value.ToString("0.##", CultureInfo.InvariantCulture);
		}
	}

	/// <summary>
	/// A meter's value 0..100 from an expression: <c>source/cap</c> is <c>source ÷ cap × 100</c>,
	/// either side a number or a source; a bare source is taken as already 0..100. Null while
	/// either side states nothing or the cap is not above 0.
	/// </summary>
	public static float? Ratio(string expression, Context context)
	{
		int slash = expression.LastIndexOf('/');
		if (slash <= 0)
		{
			return Number(expression, context);
		}

		if (Number(expression[..slash], context) is not { } value ||
			Number(expression[(slash + 1)..], context) is not { } cap || cap <= 0f)
		{
			return null;
		}

		return Math.Clamp(value / cap * 100f, 0f, 100f);
	}

	/// <summary>
	/// The player a reader reads <c>player:</c> under: everything in a view drawn for player two
	/// (their half on split screen, an online joiner's window) is player two's, and so is anything
	/// of player two's widget (a copy of it, a piece that follows it) in a shared view. The rest is
	/// player one's.
	/// </summary>
	public static int PlayerFor(bool viewForPlayerTwo, bool ownedByPlayerTwo) =>
		viewForPlayerTwo || ownedByPlayerTwo ? 2 : 1;
}
