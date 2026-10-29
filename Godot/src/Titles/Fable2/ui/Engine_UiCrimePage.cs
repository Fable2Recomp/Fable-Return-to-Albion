using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// Screen 21's three pages, owned — arrest notice, fine and parole terms. Not cursor-driven:
/// the text is written once in the screen's activate (`sub_826A8C78`) and `sub_826A8E28` only
/// toggles page visibility off a `screen+196` detector holding `Float3` (modes 50/51/52).
/// Every value is a plain load — `sub_823C4128` composes both notices into LHStrings on the
/// screen object with the fine beside them, so `RefiiHostGetCrimeScreen` needs no guest thread.
/// Both strings are already Babel-resolved; only the fine's caption is a tag.
/// </summary>
public static class Engine_UiCrimePage
{
	/// <summary>The three pages' text elements — `…PageN.Contents.Text`.</summary>
	private const string Page1Text = "Page1.Contents.Text";

	private const string Page2Text = "Page2.Contents.Text";

	private const string Page3Text = "Page3.Contents.Text";

	/// <summary>The fine's caption; takes the amount as a parameter.</summary>
	private const string FineAmountTag = "GUI_SCREEN_CRIME_FINE_AMOUNT";

	/// <summary>
	/// Page three's parole notice, used when community service is not on offer. `TEXT_` prefixed,
	/// unlike every other tag on this screen.
	/// </summary>
	private const string BrokenParoleTag = "TEXT_AI_CRIME_THREE_STRIKES_BROKEN_PAROLE";

	/// <summary>The values for the crime screen's three pages, or blanks when none is up.</summary>
	public static IReadOnlyDictionary<string, string> Values(
		Refii.Host.HostBridge.CrimeScreen crime,
		Refii.Text.Engine_BabelDatabase? strings)
	{
		var values = new Dictionary<string, string>(3)
		{
			[Page1Text] = string.Empty,
			[Page2Text] = string.Empty,
			[Page3Text] = string.Empty,
		};

		if (!crime.Known)
		{
			return values;
		}

		// Page one — the fine, then the charges. The `\n\n` is the guest's own spacing
		// (`sub_826A8C78`); this screen uses newlines, not the completed-quests page's `<br>`.
		string caption = Babel(strings, FineAmountTag);
		string fine = crime.Fine.ToString(System.Globalization.CultureInfo.InvariantCulture);
		values[Page1Text] = $"{Format(caption, fine)}\n\n{crime.Charge}";

		// Page two — the charges alone.
		values[Page2Text] = crime.Charge;

		// Page three — the charges, then the community-service terms or the broken-parole notice.
		// The branch is `screen+45`, the same flag that gates the CommunityService row.
		string terms = crime.CanServe ? crime.Service : Babel(strings, BrokenParoleTag);
		values[Page3Text] = $"{crime.Charge}\n{terms}";
		return values;
	}

	/// <summary>
	/// Substitutes the amount into the caption's `%d`. A caption carrying none is not an error —
	/// some localisations place the amount elsewhere — so the number is appended.
	/// </summary>
	private static string Format(string caption, string amount)
	{
		if (caption.Length == 0)
		{
			return amount;
		}

		return caption.Contains("%d", System.StringComparison.Ordinal)
			? caption.Replace("%d", amount, System.StringComparison.Ordinal)
			: $"{caption} {amount}";
	}

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && strings.TryGetString(tag, out string value) ? value : string.Empty;
}
