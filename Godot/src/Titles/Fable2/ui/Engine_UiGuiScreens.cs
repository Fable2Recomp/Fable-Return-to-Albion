using System.Collections.Generic;
using Refii.Core;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The one-off screens the guest fills in by name: the epilogue, the next-episode page, and
/// the loading screen's tip group.
/// </summary>
/// <remarks>
/// Elements are keyed by `Fnv1HashWithBasis(rawName, 0x811C9DC5)`, one map for text and one for
/// show/hide. Guest channels: text is registry index 6 (`sub_823FEB28`, writes the string and the
/// dirty byte); show/hide is index 9 (`sub_823FE848`, posts logic state 2 = on, 1 = off).
/// </remarks>
public sealed class Engine_UiGuiScreens
{
	private readonly Dictionary<uint, string> _text = new();

	private readonly Dictionary<uint, bool> _shown = new();

	/// <summary>Bumped whenever anything here changes, so a view can skip untouched frames.</summary>
	public ulong Generation { get; private set; }

	/// <summary>An element's stated text, or null when nothing has stated it.</summary>
	public string? Text(string element) =>
		_text.TryGetValue(Engine_GameHash.Fnv1(element), out string? value) ? value : null;

	/// <summary>Whether an element has been shown or hidden, or null when neither.</summary>
	public bool? Shown(string element) =>
		_shown.TryGetValue(Engine_GameHash.Fnv1(element), out bool value) ? value : null;

	/// <summary>States one element's text by name.</summary>
	public void StateText(string element, string display)
	{
		_text[Engine_GameHash.Fnv1(element)] = display;
		++Generation;
	}

	/// <summary>Shows or hides one element by name.</summary>
	public void StateShown(string element, bool shown)
	{
		_shown[Engine_GameHash.Fnv1(element)] = shown;
		++Generation;
	}

	// ---- THE EPILOGUE, screen 12 -----------------------------------------------------------

	/// <summary>Item rewards added since the screen opened.</summary>
	/// <remarks>
	/// `sub_823D4690` increments, then picks `EpilogueItem1` for 1 and `EpilogueItem2` for anything
	/// higher; a third reward overwrites slot 2 (shipped behaviour, not a cap to add).
	/// </remarks>
	public int EpilogueRewards { get; private set; }

	/// <summary>`GUI.SetGoldRewardInEpilogueScreen(gold)` — writes two elements immediately.</summary>
	/// <remarks>
	/// The label element is `EpilogueRenown` but its tag is `GUI_QUEST_COMPLETION_GOLD`; the number
	/// goes to `EpilogueRenownAmt`. Zero blanks both.
	/// </remarks>
	public void SetEpilogueGold(int gold, string goldLabel)
	{
		StateText("EpilogueRenown", gold == 0 ? string.Empty : goldLabel);
		StateText("EpilogueRenownAmt",
			gold == 0 ? string.Empty : gold.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	/// <summary>`GUI.AddItemRewardToEpilogueScreen(tag)` — the caller resolves the tag.</summary>
	/// <param name="display">The RESOLVED text; the guest resolves it at call time, not at draw.</param>
	public void AddEpilogueReward(string display)
	{
		++EpilogueRewards;
		string element = EpilogueRewards > 1 ? "EpilogueItem2" : "EpilogueItem1";
		StateShown(element, true);
		StateText(element, display);
	}

	// ---- THE NEXT-EPISODE PAGE, screen 13 --------------------------------------------------

	/// <summary>True between `TurnOnNextEpisode` and `TurnOffNextEpisode`.</summary>
	public bool EpisodeUp { get; private set; }

	/// <summary>The marketplace offer the download button would buy, or empty.</summary>
	/// <remarks>
	/// Chosen by the button's tag: argument 5 is compared case-insensitively against
	/// `GUI_SCREEN_EPISODE_BUY_EPISODE_{TWO..FIVE}` to pick one of four offer-id globals.
	/// </remarks>
	public string EpisodeOffer { get; private set; } = string.Empty;

	/// <summary>The image name — a RESOURCE identity, lowercased and resource-hashed, never a tag.</summary>
	public string EpisodeImage { get; private set; } = string.Empty;

	/// <summary>`GUI.TurnOnNextEpisode(...)` — the caller resolves the five tags.</summary>
	public void ShowEpisode(
		string title, string line1, string line2, string line3,
		string buttonText, string offer, string image, string continueText)
	{
		StateText("EpisodeTitle", title);
		StateText("EpisodeDescription01", line1);
		StateText("EpisodeDescription02", line2);
		StateText("EpisodeDescription03", line3);
		StateText("EpisodeDownloadButton", buttonText);
		StateText("EpisodeContinueButton", continueText);

		EpisodeImage = image;
		EpisodeOffer = offer;
		EpisodeUp = true;
		++Generation;
	}

	/// <summary>`GUI.TurnOffNextEpisode()` — the exact inverse latch, harmless when already off.</summary>
	public void HideEpisode()
	{
		if (!EpisodeUp)
		{
			return;
		}

		EpisodeUp = false;
		++Generation;
	}

	// ---- THE LOADING SCREEN ----------------------------------------------------------------

	/// <summary>The Babel tag group (not a tag) whose members are the rotating loading tips.</summary>
	/// <remarks>The guest default is `GUI_SCREEN_LOADING_TIPS`, so a screen that never sets it still has tips.</remarks>
	public string LoadingTipGroup { get; private set; } = "GUI_SCREEN_LOADING_TIPS";

	/// <summary>`GUI.SetLoadingScreenTextTagGroupName(name)`.</summary>
	public void SetLoadingTipGroup(string name)
	{
		if (name.Length == 0 || name == LoadingTipGroup)
		{
			return;
		}

		LoadingTipGroup = name;
		++Generation;
	}

	// ---- SOUND -----------------------------------------------------------------------------

	/// <summary>The sound name the guest last asked for; `GUI.PlaySound` resolves it by name, no handle.</summary>
	public string Sound { get; private set; } = string.Empty;

	/// <summary>Incremented on every `PlaySound`, including a repeat of the same name.</summary>
	public int SoundsAsked { get; private set; }

	/// <summary>`GUI.PlaySound(name)`.</summary>
	public void PlaySound(string name)
	{
		if (name.Length == 0)
		{
			return;
		}

		Sound = name;
		++SoundsAsked;
		++Generation;
	}

	/// <summary>One line for a report.</summary>
	public string Describe() =>
		$"{_text.Count} text, {_shown.Count} shown, epilogue rewards {EpilogueRewards}, " +
		$"episode {(EpisodeUp ? EpisodeOffer.Length == 0 ? "up" : "up " + EpisodeOffer : "off")}, " +
		$"tips '{LoadingTipGroup}'";
}
