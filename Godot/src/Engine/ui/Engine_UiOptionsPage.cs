using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// The options screen's pickboxes, owned — screen 18's pages 1 and 2.
///
/// A pickbox is a CHILD text element: `sub_828821D0` stores the chosen index and writes the
/// localised option into `&lt;pickboxPath&gt;.grp.text`, so every key here ends in that suffix.
///
/// Page 1's row labels are not ours: its five label globals only ever reach `sub_82426328`, the
/// opacity show/hide, and the captions are authored in `optionsscreen.bgf`. Pages 3 and 4 do
/// state theirs as Babel tags.
///
/// Pages 3 and 4 also ride a second channel: gamma and the three volumes are PIP POSITIONS, not
/// text — `sub_82881E38` writes `position.x = value * 440 - 210` onto `&lt;slider&gt;.grp.pip` —
/// so they go through <see cref="Engine_UiOwnedBook.HostPositions"/> while their labels and the
/// speaker-mode pickbox use the text channel.
///
/// The profile blob is a dead end: every reader allocates 40 bytes, reads and frees before
/// returning. The screen keeps its own copy — `sub_829C07D0` stores gamma on page 3 and
/// `sub_829C1488` the audio set on page 4 — so all five are plain loads while it is open.
/// </summary>
public static class Engine_UiOptionsPage
{
	/// <summary>The suffix every pickbox's text child carries.</summary>
	private const string TextChild = ".grp.text";

	private const string Subtitles = "Options.Subtitles_pickbox" + TextChild;

	private const string Breadcrumb = "Options.BreadcrumbSize_pickbox" + TextChild;

	private const string TutorialBoxes = "Options.TutorialBoxes_pickbox" + TextChild;

	private const string MultiplayerOrbs = "Options.MultiplayerOrbs_pickbox" + TextChild;

	private const string AutoJoinable = "Options.Auto_joinable_pickbox" + TextChild;

	private const string InvertCamera = "Page2.Contents.pickbox1" + TextChild;

	/// <summary>
	/// The two-state tags. OFF is index 0 — `sub_829BE2C0` pushes OFF before ON and selects on
	/// `value != 0`.
	/// </summary>
	private const string OffTag = "GUI_CONTROLS_OFF";

	private const string OnTag = "GUI_CONTROLS_ON";

	/// <summary>The breadcrumb sizes, and the thresholds the row picks between them.</summary>
	private static readonly (float Least, string Tag)[] BreadcrumbSizes =
	{
		(0.0f, "GUI_OPTIONS_BCRUMB_OFF"),
		(0.5f, "GUI_OPTIONS_BCRUMB_LOW"),
		(1.0f, "GUI_OPTIONS_BCRUMB_MEDIUM"),
		(1.5f, "GUI_OPTIONS_BCRUMB_HIGH"),
		(2.0f, "GUI_OPTIONS_BCRUMB_VERYHIGH"),
	};

	/// <summary>Multiplayer orbs is three-state, not a toggle.</summary>
	private static readonly string[] OrbModes =
	{
		OffTag,
		"GUI_SCREEN_OPTIONS_ORBS_FRIENDS",
		"GUI_SCREEN_OPTIONS_ORBS_ALL",
	};

	/// <summary>Page 3's one slider, and page 4's three — the paths the guest writes.</summary>
	private const string Gamma = "Page3.Contents.slider1";

	private const string Sfx = "Page4.Contents.slider1";

	private const string Music = "Page4.Contents.slider2";

	private const string Speech = "Page4.Contents.slider3";

	/// <summary>The speaker-mode pickbox — page 4's only TEXT row.</summary>
	private const string Speaker = "Page4.Contents.pickbox1" + TextChild;

	/// <summary>The moving part of a slider. The pip is a CHILD, never the slider itself.</summary>
	private const string PipChild = ".grp.pip";

	/// <summary>
	/// The pip's travel, exactly as `sub_82881E38` computes it — `flt_8210177C` is 440.0 and
	/// `flt_82101778` is 210.0, so a full bar spans x -210..230 rather than being centred.
	/// </summary>
	private const float PipSpan = 440.0f;

	private const float PipOrigin = 210.0f;

	/// <summary>The slider row captions, and the speaker modes.</summary>
	private static readonly (string Path, string Tag)[] SliderLabels =
	{
		(Gamma, "GUI_SCREEN_OPTIONS_GAMMA"),
		(Sfx, "GUI_SCREEN_OPTIONS_SOUNDS"),
		(Music, "GUI_SCREEN_OPTIONS_MUSIC"),
		(Speech, "GUI_SCREEN_OPTIONS_VOICE"),
	};

	private static readonly string[] SpeakerModes =
	{
		"GUI_SCREEN_OPTIONS_AUDIO_TV",
		"GUI_SCREEN_OPTIONS_AUDIO_HIFI",
		"GUI_SCREEN_OPTIONS_AUDIO_SURROUND",
	};

	/// <summary>
	/// Where each slider's pip sits for the current settings — EMPTY, not zeroed, while the
	/// screen is shut: a pip at value 0 is a real reading, so stating nothing leaves the authored
	/// pip where it is.
	/// </summary>
	public static IReadOnlyDictionary<string, Godot.Vector3> Positions(
		Refii.Host.HostBridge.OptionSliders sliders)
	{
		var places = new Dictionary<string, Godot.Vector3>(4);
		if (!sliders.Known)
		{
			return places;
		}

		Place(places, Gamma, sliders.Gamma);
		Place(places, Sfx, sliders.Sfx);
		Place(places, Music, sliders.Music);
		Place(places, Speech, sliders.Speech);
		return places;
	}

	/// <summary>
	/// One pip, at the guest's own arithmetic. `y` and `z` are stated too — `sub_824251E0` writes
	/// all three every time, y = 0 and z = -1; an x-only write leaves the pip behind the bar.
	/// </summary>
	private static void Place(
		Dictionary<string, Godot.Vector3> places, string slider, float value)
	{
		float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
		places[slider + PipChild] =
			new Godot.Vector3((clamped * PipSpan) - PipOrigin, 0f, -1f);
	}

	/// <summary>The pickbox captions for the current settings, or blanks.</summary>
	/// <param name="pageOpen">
	/// True while the page level is open, when the pickbox captions are the GUEST's: LEFT/RIGHT
	/// rewrites `.grp.text` at once (`sub_828821D0`) but the setting commits only on A
	/// (`sub_829BF698`), so a caption from the committed bytes would show the old choice. The
	/// seven captions are then omitted so the guest's live text passes through; the slider labels
	/// stay ours.
	/// </param>
	public static IReadOnlyDictionary<string, string> Values(
		Refii.Host.HostBridge.GameOptions options,
		Refii.Host.HostBridge.OptionSliders sliders,
		Refii.Text.Engine_BabelDatabase? strings,
		bool pageOpen = false)
	{
		var values = new Dictionary<string, string>(11);
		if (!pageOpen)
		{
			values[Subtitles] = string.Empty;
			values[Breadcrumb] = string.Empty;
			values[TutorialBoxes] = string.Empty;
			values[MultiplayerOrbs] = string.Empty;
			values[AutoJoinable] = string.Empty;
			values[InvertCamera] = string.Empty;
			values[Speaker] = string.Empty;
		}

		// The slider labels are ours to write, unlike page 1's: `sub_829C08B8` and its two
		// siblings assign Babel tags to these four captions.
		foreach ((string path, string tag) in SliderLabels)
		{
			values[path + TextChild] = Babel(strings, tag);
		}

		if (pageOpen)
		{
			return values;
		}

		if (sliders.Known)
		{
			int mode = sliders.Speaker;
			values[Speaker] = mode >= 0 && mode < SpeakerModes.Length
				? Babel(strings, SpeakerModes[mode])
				: string.Empty;
		}

		if (!options.Known)
		{
			return values;
		}

		values[Subtitles] = Toggle(strings, options.Subtitles);
		values[TutorialBoxes] = Toggle(strings, options.TutorialBoxes);
		values[AutoJoinable] = Toggle(strings, options.AutoJoinable);
		values[InvertCamera] = Toggle(strings, options.InvertCamera);
		values[Breadcrumb] = Babel(strings, BreadcrumbTag(options.Breadcrumb));

		int orbs = options.MultiplayerOrbs;
		values[MultiplayerOrbs] = orbs >= 0 && orbs < OrbModes.Length
			? Babel(strings, OrbModes[orbs])
			: string.Empty;

		return values;
	}

	/// <summary>
	/// The size band a breadcrumb value falls in — the LAST threshold it reaches, not the
	/// nearest, so a value between two bands shows the lower one.
	/// </summary>
	private static string BreadcrumbTag(float size)
	{
		string tag = BreadcrumbSizes[0].Tag;
		foreach ((float least, string candidate) in BreadcrumbSizes)
		{
			if (size >= least)
			{
				tag = candidate;
			}
		}

		return tag;
	}

	private static string Toggle(Refii.Text.Engine_BabelDatabase? strings, bool on) =>
		Babel(strings, on ? OnTag : OffTag);

	private static string Babel(Refii.Text.Engine_BabelDatabase? strings, string tag) =>
		strings is not null && strings.TryGetString(tag, out string value) ? value : string.Empty;
}
