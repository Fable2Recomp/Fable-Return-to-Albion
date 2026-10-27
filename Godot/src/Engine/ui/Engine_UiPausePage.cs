using System.Collections.Generic;
using System.Globalization;

namespace Refii.Ui;

/// <summary>
/// THE PAUSE ROOT'S READOUTS, PULLED RATHER THAN MIRRORED.
///
/// Renown, the purse, the clock and the four experience orbs, read off the hero the host already
/// samples rather than off the guest's presentation.
///
/// The compositions are the guest's own (`sub_826A4D50`, the pause root's per-tick filler):
///
///   renown   `Babel(GUI_SCREEN_INVENTORY_RENOWN) + " " + "%d"` — a labelled line, not a bare
///            number; the separator is one space (`word_820B42CC`), no icon token.
///   purse    `"&lt;gold_img&gt;" + "%d"` — the icon markup is part of the string.
///   orbs     `(int)orbs[0..3]` bare, truncated — General, Strength, Skill, Will.
///   clock    the Babel template `GUI_TIME_TEXT_MORNING`/`_EVENING` (split at 12:00) with
///            `%hr12`/`%hr24`/`%min` each replaced by a `%02d` value (`sub_82515798`). The
///            12-hour fold keeps the shipped quirk: only hours ABOVE 13 subtract 12, 0 -> 12.
///
/// The nine `Legend.Fixed.KeyName*` entries and the current-quest lines are deliberately NOT
/// here: they are authored captions and quest state that want their own sources, and inventing
/// a value for them would replace a stale string with a wrong one.
/// </summary>
public static class Engine_UiPausePage
{
	private const string Renown = "Contents.Renown";

	private const string Money = "Money.Text";

	private const string Clock = "Contents.Time";

	private const string RenownTag = "GUI_SCREEN_INVENTORY_RENOWN";

	private const string MorningTag = "GUI_TIME_TEXT_MORNING";

	private const string EveningTag = "GUI_TIME_TEXT_EVENING";

	private const string GoldToken = "<gold_img>";

	/// <summary>
	/// The four orb readouts, in the guest's own pool order. Keyed without a page so one entry
	/// serves every bearer — <see cref="Engine_UiOwnedBook.HostValues"/> matches on trailing
	/// whole segments.
	/// </summary>
	private static readonly string[] OrbValuePaths =
	{
		"experienceorbs.General.valueText",
		"experienceorbs.Strength.valueText",
		"experienceorbs.Skill.valueText",
		"experienceorbs.Will.valueText",
	};

	/// <summary>The readouts, or an empty set before a hero exists.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Refii.Host.HostBridge.HeroVitals vitals,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(OrbValuePaths.Length + 3);
		if (!vitals.Known)
		{
			return values;
		}

		string renown = vitals.RenownBase.ToString(CultureInfo.InvariantCulture);
		values[Renown] = Babel(strings, RenownTag) is { Length: > 0 } caption
			? $"{caption} {renown}"
			: renown;
		values[Money] = GoldToken + vitals.Gold.ToString(CultureInfo.InvariantCulture);

		if (vitals.ExperienceAvailable is { Length: > 3 } orbs)
		{
			for (int pool = 0; pool < OrbValuePaths.Length; ++pool)
			{
				// Truncated, not rounded — the guest prints `(int)orbs[i]`, and rounding would
				// show a point of experience the affordability test does not have.
				values[OrbValuePaths[pool]] =
					((int)orbs[pool]).ToString(CultureInfo.InvariantCulture);
			}
		}

		if (Refii.Host.HostBridge.TimeOfDay() is { } clock)
		{
			values[Clock] = ClockText(clock.Fraction, strings);
		}

		return values;
	}

	/// <summary>
	/// The day fraction worded as `sub_82515798` words it: the localized morning/evening
	/// template with its `%hr12`/`%hr24`/`%min` tokens substituted, each `%02d`. Falls back to
	/// a plain 12-hour clock when the book lacks the tags.
	/// </summary>
	private static string ClockText(float fraction, Refii.Text.Engine_BabelDatabase? strings)
	{
		int minutes = (int)(System.Math.Clamp(fraction, 0f, 0.999999f) * 24f * 60f);
		int hour24 = minutes / 60;
		int minute = minutes % 60;

		// The shipped fold: only hours ABOVE 13 subtract 12 (13:xx stays "13"), zero is 12.
		int hour12 = hour24;
		if (hour12 > 13)
		{
			hour12 -= 12;
		}

		if (hour12 == 0)
		{
			hour12 = 12;
		}

		// The split is on the HHMM integer against 1200, as `sub_82515798` compares it.
		string template = Babel(strings, (hour24 * 100) + minute < 1200 ? MorningTag : EveningTag);
		if (template.Length == 0)
		{
			return string.Create(CultureInfo.InvariantCulture,
				$"{(hour12 > 12 ? hour12 - 12 : hour12)}:{minute:00} {(hour24 < 12 ? "am" : "pm")}");
		}

		return template
			.Replace("%hr12", hour12.ToString("00", CultureInfo.InvariantCulture))
			.Replace("%hr24", hour24.ToString("00", CultureInfo.InvariantCulture))
			.Replace("%min", minute.ToString("00", CultureInfo.InvariantCulture));
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is { } book && book.TryGetString(tag, out string resolved) ? resolved : string.Empty;
}
