using System;

namespace Refii.Ui;

/// <summary>
/// A BOUND TEXT on a page or a row: a value source read through the one value grammar
/// (<see cref="Engine_UiValueSources"/>: <c>mod:</c>, <c>player:</c>, <c>game:</c>, <c>hero:</c>,
/// a Babel tag or a literal), plus what a page and a row add to it —
/// <list type="bullet">
/// <item><c>row:label</c> / <c>row:name</c> — the highlighted row;</item>
/// <item><c>{row}</c> anywhere in a binding — the highlighted row's name, so one binding reads a
/// value per row (<c>mod:mymod.price.{row}</c>);</item>
/// <item><c>|&lt;format&gt;</c> after a value source — the number format
/// (<c>mod:souls.stamina|0</c>, <c>player:souls.level|Level {0}</c>).</item>
/// </list>
/// Used by a screen's data page (<see cref="Engine_UiDataPage"/>) and the <c>values:</c> row source
/// (<see cref="Engine_UiModRows"/>). A value nobody stated says nothing — never a number it is not.
/// </summary>
/// <remarks>Pure: the readers are the caller's (<see cref="Lookups.Values"/>), so the forms are held by tests.</remarks>
public static class Engine_UiValueSource
{
	public const string ModPrefix = Engine_UiValueSources.ModPrefix;
	public const string PlayerPrefix = Engine_UiValueSources.PlayerPrefix;
	public const string RowPrefix = "row:";
	public const string RowToken = "{row}";

	/// <summary>The readers for one page or list: the value sources' (for the menu's player), the string book, the row.</summary>
	public readonly record struct Lookups(
		Engine_UiValueSources.Context Values, Func<string, string?> Tag, string RowName = "", string RowLabel = "");

	/// <summary>The binding and its format: the text after the last <c>|</c> of a value source.</summary>
	public static (string Binding, string Format) SplitFormat(string text)
	{
		string trimmed = text.Trim();
		if (!IsValue(trimmed))
		{
			return (trimmed, string.Empty);
		}

		int bar = trimmed.LastIndexOf('|');
		return bar < 0 ? (trimmed, string.Empty) : (trimmed[..bar].Trim(), trimmed[(bar + 1)..]);
	}

	/// <summary>Whether a binding reads a value source rather than text.</summary>
	public static bool IsValue(string binding) => Engine_UiValueSources.LooksLikeSource(binding);

	/// <summary>The binding's text now, or null when it states nothing (an unstated value, an empty text).</summary>
	public static string? Resolve(string text, in Lookups lookups)
	{
		(string binding, string format) = SplitFormat(text.Replace(RowToken, lookups.RowName, StringComparison.Ordinal));
		if (binding.StartsWith(RowPrefix, StringComparison.Ordinal))
		{
			return binding[RowPrefix.Length..] switch
			{
				"label" => lookups.RowLabel.Length != 0 ? lookups.RowLabel : lookups.RowName,
				"name" => lookups.RowName,
				_ => null,
			};
		}

		return Engine_UiValueSources.Text(binding, format, lookups.Values, lookups.Tag);
	}

	/// <summary>
	/// Whether a switch binding is on: a value source that states a non-zero number, or text reading
	/// <c>true</c>, <c>on</c>, <c>yes</c> or <c>1</c>. An unstated value is off.
	/// </summary>
	public static bool Truth(string text, in Lookups lookups)
	{
		(string binding, _) = SplitFormat(text.Replace(RowToken, lookups.RowName, StringComparison.Ordinal));
		if (Engine_UiValueSources.Parse(binding).Named)
		{
			return Engine_UiValueSources.Number(binding, lookups.Values) is { } value && value != 0f;
		}

		return binding.ToLowerInvariant() is "true" or "on" or "yes" or "1";
	}

	/// <summary>A problem with a binding for the validator, or null when it reads.</summary>
	public static string? Problem(string text)
	{
		(string binding, _) = SplitFormat(text);
		if (binding.StartsWith(RowPrefix, StringComparison.Ordinal))
		{
			return binding[RowPrefix.Length..] is "label" or "name" ? null : "'row:' reads label or name";
		}

		return Engine_UiValueSources.TextProblem(binding);
	}

	/// <summary>
	/// The live readers for a page or list of <paramref name="player"/>'s: published mod values, the
	/// player store under that player, mod settings. (The guest's own HUD meters belong to the HUD's views.)
	/// </summary>
	public static Engine_UiValueSources.Context Live(int player) => new()
	{
		Player = player is 2 ? 2 : 1,
		Mod = key => Refii.Host.HostBridge.TryGetModValue(key, out float value) ? value : null,
		PlayerValue = (p, key) => Refii.Host.HostBridge.GetPlayerValue(p, key),
		Setting = key => Refii.Mods.ModSettings.TryGet(key, out float value) ? value : null,
	};
}
