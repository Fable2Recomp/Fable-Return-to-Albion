namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using Refii.Host;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// The rows of Fable 3's Options screen, from the object the guest holds while the screen shows
/// (Fable3/src/hostbridge/src/options_feed.cpp: RefiiHostGetFable3Options). Pure logic, so the
/// tests can run it against a snapshot.
/// </summary>
/// <remarks>
/// THE ROW ORDER AND VALUES ARE THE GUEST'S. The row writer sub_825CC938 (page 3, GameSettings)
/// writes rows 1..9 as `*(obj+92)` selects them: Controller +31, Subtitles +32, Tutorial +33,
/// Gamma +128, Breadcrumb +104, Music +116, SoundEffects +120, Voice +124, SpeakerType +100
/// (0x825cc978..0x825ccb84); sub_825CCB90 (page 4, GameSettingsLite) rows 1..7: Language +64,
/// Subtitles, Gamma, Music, SoundEffects, Voice, SpeakerType (0x825ccbc8..0x825ccd78). The value
/// tags are the tables its static initialiser fills: ON/OFF `dword_83558A64` =
/// TEXT_GUI_OPTIONS_OFF / TEXT_GUI_OPTIONS_ON (0x831b4be0 / 0x831b4c20), breadcrumb
/// `dword_83558A6C` = GUI_OPTIONS_BCRUMB_OFF/LOW/MEDIUM/HIGH/VERYHIGH (0x831b4c70..0x831b4d58),
/// speaker `dword_83558A80` = GUI_SCREEN_OPTIONS_AUDIO_TV/HIFI/SURROUND (0x831b4d98..0x831b4e18).
/// The row labels are the tags options.bgf gives its row elements (GUI_SCREEN_OPTIONS_*, the
/// `.Controller` row's being INVERT_AIM: "Adjust your controllers invert aim").
///
/// THE LIST PAGES are the A-press handler sub_825CB588's rows: pages 1 and 5 (`+84`, 0x825cb73c)
/// row 1 Save Management (page 2, 0x825cb794), row 2 Game Settings (page 3, or the lite page when
/// sub_82347CE8 refuses, 0x825cb7f8/0x825cb830), row 3 New Game (the confirm query, 0x825cb8ac/c0),
/// row 4 Kick/Leave (multiplayer, 0x825cba94 — page 5's section carries it, 0x825c9a98); page 2
/// (`+88`, 0x825cb5b4) row 1 Save and Continue (ghosted by the Show when the save is not allowed,
/// 0x825c99f4..0x825c9a3c), row 2 Load Management (ghosted when +112, 0x825c9a58). Labels are the
/// rows' tags in options.bgf; the texts the guest writes into the section's `SelectionText` /
/// `SelectionTitle` / `SelectionDesription` elements win when stated. A page the handler does not
/// list falls back to every text stated under its section.
/// </remarks>
public static class Fable3OptionsModel
{
	/// <summary>One row: a stable name, its label (resolved), its value (resolved), its description tag and whether the guest ghosts it.</summary>
	public readonly record struct Row(string Name, string Label, string Value, string Description, bool Ghosted = false);

	/// <summary>The Options object's state, the feed's 14 words and 4 values.</summary>
	public readonly record struct Snapshot(
		uint Box, int Page, int SelectedRow, bool Controller, bool Subtitles, bool Tutorial, int Breadcrumb,
		int Speaker, int LanguageIndex, int LanguageCount, uint Flag108, uint Flag112, bool FromPause, bool Lite,
		float Gamma, float Music, float Sfx, float Voice, bool CanSave = true);

	public const int Words = 15;
	public const int Values = 4;

	public const int PageOptions = 1;
	public const int PageSaveManagement = 2;
	public const int PageGameSettings = 3;
	public const int PageGameSettingsLite = 4;
	public const int PageMultiplayer = 5;

	/// <summary>The value tags, as the guest's tables order them.</summary>
	public static readonly string[] OnOff = { "TEXT_GUI_OPTIONS_OFF", "TEXT_GUI_OPTIONS_ON" };

	public static readonly string[] Breadcrumbs =
		{ "GUI_OPTIONS_BCRUMB_OFF", "GUI_OPTIONS_BCRUMB_LOW", "GUI_OPTIONS_BCRUMB_MEDIUM", "GUI_OPTIONS_BCRUMB_HIGH", "GUI_OPTIONS_BCRUMB_VERYHIGH" };

	public static readonly string[] Speakers =
		{ "GUI_SCREEN_OPTIONS_AUDIO_TV", "GUI_SCREEN_OPTIONS_AUDIO_HIFI", "GUI_SCREEN_OPTIONS_AUDIO_SURROUND" };

	/// <summary>The section element each page writes under (`Options` + the suffix, 0x831b3590..0x831b3748).</summary>
	public static string SectionPath(int page) => page switch
	{
		PageOptions => "Options.Sections.Options",
		PageSaveManagement => "Options.Sections.SaveManagement",
		PageGameSettings => "Options.Sections.GameSettings",
		PageGameSettingsLite => "Options.Sections.GameSettingsLite",
		PageMultiplayer => "Options.Sections.Multiplayer",
		_ => "Options",
	};

	/// <summary>The page's title tag (options.bgf): the root carries the screen title.</summary>
	public static string PageTitle(int page) => page switch
	{
		PageSaveManagement => "GUI_SAVE_MANAGEMENT",
		PageGameSettings or PageGameSettingsLite => "GUI_GAME_SETTINGS",
		_ => "GUI_OPTIONS_TITLE",
	};

	public static Snapshot Parse(uint[] words, float[] values) => new(
		words[0], (int)words[1], (int)words[2], words[3] != 0u, words[4] != 0u, words[5] != 0u, (int)words[6],
		(int)words[7], (int)words[8], (int)words[9], words[10], words[11], words[12] != 0u, words[13] != 0u,
		values[0], values[1], values[2], values[3], words.Length < 15 || words[14] != 0u);

	/// <summary>
	/// A slider as text: the guest's bar is `value * 1000` on a 0..1000 meter (sub_825CDBD0,
	/// 0x825cdc38); twenty pips here, a step of 0.025 being half a pip, with the percentage.
	/// </summary>
	public static string Slider(float value)
	{
		float clamped = value < 0f ? 0f : value > 1f ? 1f : value;
		int filled = (int)System.MathF.Round(clamped * 20f);
		return new string('■', filled) + new string('□', 20 - filled) + "  " + (int)System.MathF.Round(clamped * 100f) + "%";
	}

	/// <summary>
	/// The rows of the settings pages (3 and 4), labels and values resolved through
	/// <paramref name="resolve"/>; <paramref name="languages"/> names the language row's value.
	/// Empty for the pages whose rows are element writes.
	/// </summary>
	public static List<Row> SettingsRows(Snapshot s, System.Func<string, string> resolve, IReadOnlyList<string> languages)
	{
		var rows = new List<Row>();
		string Pick(string[] tags, int index) => resolve(index >= 0 && index < tags.Length ? tags[index] : tags[0]);
		Row Toggle(string name, string tag, bool on) => new(name, resolve(tag), Pick(OnOff, on ? 1 : 0), tag + "_DESC");
		Row Bar(string name, string tag, float value) => new(name, resolve(tag), Slider(value), tag + "_DESC");

		switch (s.Page)
		{
			case PageGameSettings:
				rows.Add(Toggle("Controller", "GUI_SCREEN_OPTIONS_INVERT_AIM", s.Controller));
				rows.Add(Toggle("Subtitles", "GUI_SCREEN_OPTIONS_SUBTITLES", s.Subtitles));
				rows.Add(Toggle("Tutorial", "GUI_SCREEN_OPTIONS_TUTORIAL_BOXES", s.Tutorial));
				rows.Add(Bar("Gamma", "GUI_SCREEN_OPTIONS_GAMMA", s.Gamma));
				rows.Add(new Row("Breadcrumb", resolve("GUI_SCREEN_OPTIONS_BREADCRUMB_SIZE"), Pick(Breadcrumbs, s.Breadcrumb), "GUI_SCREEN_OPTIONS_BREADCRUMB_SIZE_DESC"));
				rows.Add(Bar("Music", "GUI_SCREEN_OPTIONS_MUSIC", s.Music));
				rows.Add(Bar("SoundEffects", "GUI_SCREEN_OPTIONS_SOUNDS", s.Sfx));
				rows.Add(Bar("Voice", "GUI_SCREEN_OPTIONS_VOICE", s.Voice));
				rows.Add(new Row("SpeakerType", resolve("GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS"), Pick(Speakers, s.Speaker), "GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS_DESC"));
				break;
			case PageGameSettingsLite:
				string language = s.LanguageIndex >= 0 && s.LanguageIndex < languages.Count ? languages[s.LanguageIndex] : string.Empty;
				rows.Add(new Row("Language", resolve("GUI_SCREEN_OPTIONS_LANGUAGE"), language, "GUI_SCREEN_OPTIONS_LANGUAGE_DESC"));
				rows.Add(Toggle("Subtitles", "GUI_SCREEN_OPTIONS_SUBTITLES", s.Subtitles));
				rows.Add(Bar("Gamma", "GUI_SCREEN_OPTIONS_GAMMA", s.Gamma));
				rows.Add(Bar("Music", "GUI_SCREEN_OPTIONS_MUSIC", s.Music));
				rows.Add(Bar("SoundEffects", "GUI_SCREEN_OPTIONS_SOUNDS", s.Sfx));
				rows.Add(Bar("Voice", "GUI_SCREEN_OPTIONS_VOICE", s.Voice));
				rows.Add(new Row("SpeakerType", resolve("GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS"), Pick(Speakers, s.Speaker), "GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS_DESC"));
				break;
		}

		return rows;
	}

	/// <summary>
	/// The rows of the list pages (1, 2 and 5), as the handler orders them. <paramref name="stated"/>
	/// answers an element path under the page's section with the text the guest wrote, or null;
	/// a stated label or description wins over the tag.
	/// </summary>
	public static List<Row> ListRows(Snapshot s, System.Func<string, string> resolve, System.Func<string, string?> stated)
	{
		var rows = new List<Row>();
		string section = SectionPath(s.Page);
		Row Make(string name, string labelTag, string descriptionTag, string? labelPath, string? descriptionPath, bool ghosted = false)
		{
			string label = (labelPath is not null ? stated(section + "." + labelPath) : null) ?? resolve(labelTag);
			string description = (descriptionPath is not null ? stated(section + "." + descriptionPath) : null) ?? descriptionTag;
			return new Row(name, label, string.Empty, description, ghosted);
		}

		switch (s.Page)
		{
			case PageOptions:
			case PageMultiplayer:
				bool multiplayer = s.Page == PageMultiplayer;
				rows.Add(Make("SaveManagement", "GUI_SAVE_MANAGEMENT", "GUI_SAVE_MANAGEMENT_DESC",
					multiplayer ? "SelectionText.SaveManagement" : "SelectionTitle.SaveManagment", null));
				rows.Add(Make("GameSettings", "GUI_GAME_SETTINGS", "GUI_GAME_SETTINGS_DESC", null, null));
				rows.Add(Make("NewGame", "GUI_NEW_GAME", "GUI_NEW_GAME_DESC",
					multiplayer ? "SelectionText.NewGame" : "SelectionTitle.NewGame", null));
				if (multiplayer || s.Flag108 != 0u)
				{
					rows.Add(Make("Kick/Leave", s.Flag108 == 2u ? "GUI_NET_PAUSED_DISCONNECT_FROM_HOST" : "GUI_ORB_INTERACT_KICK", string.Empty,
						"SelectionText.Kick/Leave", "SelectionDesription.Kick/LeaveDescription"));
				}
				break;
			case PageSaveManagement:
				rows.Add(Make("Save&Continue", "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE", "GUI_SCREEN_PAUSE_SAVE_AND_CONTINUE_DESC",
					"SelectionText.Save&Continue", "SelectionDesription.SaveAndContDescription", !s.CanSave));
				rows.Add(Make("LoadMangement", "GUI_SCREEN_LOAD_MANGEMENT", "GUI_SCREEN_LOAD_MANGEMENT_DESC",
					"SelectionText.LoadMangement", "SelectionDesription.LoadDescription", s.Flag112 != 0u));
				break;
		}

		return rows;
	}

	/// <summary>Every page's rows: the settings pages' values, the list pages' entries.</summary>
	public static List<Row> Rows(Snapshot s, System.Func<string, string> resolve, IReadOnlyList<string> languages, System.Func<string, string?> stated)
	{
		List<Row> rows = SettingsRows(s, resolve, languages);
		return rows.Count > 0 ? rows : ListRows(s, resolve, stated);
	}

	/// <summary>
	/// The rows of a page the guest states as element texts: every text written directly under
	/// the page's section (one path segment below it, the row elements), in path order.
	/// </summary>
	public static List<Row> StatedRows(int page, IEnumerable<KeyValuePair<string, string>> statedTexts)
	{
		string prefix = SectionPath(page) + ".";
		var rows = new List<Row>();
		foreach ((string path, string text) in statedTexts)
		{
			if (!path.StartsWith(prefix, System.StringComparison.Ordinal) || text.Length == 0)
			{
				continue;
			}

			string tail = path[prefix.Length..];
			if (tail.IndexOf('.') >= 0)
			{
				continue;
			}

			rows.Add(new Row(tail, text, string.Empty, string.Empty));
		}

		return rows;
	}

	/// <summary>The one line a row shows in the list: label, then its value when it has one.</summary>
	public static string Line(Row row) => row.Value.Length == 0 ? row.Label : row.Label + "    " + row.Value;
}

/// <summary>
/// Fable 3's Options screen, drawn on the shared menu template (<see cref="Fable3ListScreen"/>)
/// from the object the guest holds while it shows: the rows are the page's
/// (<see cref="Fable3OptionsModel"/>), the highlight the guest's selected row (+84..+96), the
/// title and the highlighted row's description over it. The pad reaches the guest unchanged —
/// left/right step its sliders and toggles (sub_825CAA98 / sub_825CB028), B closes (sub_825C9D08).
/// </summary>
public partial class Fable3Options : Fable3ListScreen
{
	private readonly List<string> _languages = new();
	private double _languagesAge = double.MaxValue;
	private int _page;

	private static readonly uint[] _words = new uint[Fable3OptionsModel.Words];
	private static readonly float[] _values = new float[Fable3OptionsModel.Values];

	protected override string LogTag => "fable3-options";

	protected override string LogContext => $"page {_page} ";

	/// <summary>
	/// Whether the guest's Game Settings page is up with nothing over it, so X opens the host's
	/// Controls page (<see cref="Fable3HostOptions"/>; the guest's input ignores X on Options).
	/// </summary>
	public static bool OffersControls { get; private set; }

	protected override Fable3ScreenPage? Compose(double delta)
	{
		OffersControls = false;
		if (!HostBridge.IsGuestUp || !Read() || Fable3HostOptions.IsOpen)
		{
			return null;
		}

		Fable3OptionsModel.Snapshot snapshot = Fable3OptionsModel.Parse(_words, _values);
		_page = snapshot.Page;

		// The language names are the guest's table; asked when the lite page shows, every second.
		if (snapshot.Page == Fable3OptionsModel.PageGameSettingsLite && (_languagesAge += delta) > 1.0)
		{
			_languagesAge = 0.0;
			_languages.Clear();
			foreach (Engine_UiModel.Language language in Engine_UiSource.Current.GetLanguages())
			{
				_languages.Add(language.Name);
			}
		}

		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.Rows(snapshot, Resolve, _languages, Stated);
		if (rows.Count == 0)
		{
			rows = Fable3OptionsModel.StatedRows(snapshot.Page, StatedTexts());
		}

		var lines = new List<Fable3ScreenPage.Row>(rows.Count);
		foreach (Fable3OptionsModel.Row row in rows)
		{
			lines.Add(new Fable3ScreenPage.Row(row.Name, Fable3OptionsModel.Line(row), row.Ghosted, row.Description));
		}

		Fable3ScreenPage.QueryBox? query = Query();
		OffersControls = query is null &&
			snapshot.Page is Fable3OptionsModel.PageGameSettings or Fable3OptionsModel.PageGameSettingsLite;
		Fable3ScreenPage.Prompt[] prompts = OffersControls
			? new[] { new Fable3ScreenPage.Prompt("X", Fable3SettingsHub.Page.Title) }
			: System.Array.Empty<Fable3ScreenPage.Prompt>();
		return new Fable3ScreenPage(Resolve(Fable3OptionsModel.PageTitle(snapshot.Page)), lines, snapshot.SelectedRow - 1,
			string.Empty, prompts, true, query);
	}

	/// <summary>The Options root's state while its query shows (sub_825CBEC0: `sub_8281A428(Options, pres, 6)` at 0x825cbef4).</summary>
	public const int QueryState = 6;

	private const string QueryRoot = "Options.Sections.QueryBox";

	/// <summary>
	/// THE QUERY IS THE GUEST'S. sub_825CBEC0(box, tag) — called by the A handler sub_825CB588 for
	/// New Game, Save and Continue, Load and Kick/Leave (0x825cb60c, 0x825cb8ac, 0x825cb8c0,
	/// 0x825cb8e4, 0x825cba94) — sets the `Options` root to state 6 (0x825cbef4), writes the
	/// question by tag into `Options.Sections.QueryBox.Group.QueryBox.Text` (sub_82819328,
	/// 0x825cbf08) and raises box+30 (0x825cbf10); the same handler's composed variant does it
	/// inline (0x825cb9e8 state 6, 0x825cba5c text, 0x825cba68 box+30). The input handler sub_825CA410 answers it and restores the page state
	/// (`sub_8281A428(Options, pres, *(box+80))`, 0x825ca614). So the query is up exactly while the
	/// root of the current Options presentation holds 6; its button labels are the ones the guest
	/// wrote into `.Buttons.Buttons.A_Button.small.Text` / `B_Button` (r7: 'Continue' / 'Cancel').
	/// </summary>
	private static Fable3ScreenPage.QueryBox? Query()
	{
		if (Fable3Elements.Latest("Options") is not { State: QueryState })
		{
			return null;
		}

		string text = Fable3Elements.Latest(QueryRoot + ".Group.QueryBox.Text")?.Text ?? string.Empty;
		string title = Fable3Elements.Latest(QueryRoot + ".Group.QueryBox.Title")?.Text ?? string.Empty;
		var prompts = new List<Fable3ScreenPage.Prompt>(2);
		foreach (string button in new[] { "A", "B" })
		{
			if (Fable3Elements.Latest(QueryRoot + ".Buttons.Buttons." + button + "_Button.small.Text") is { Hidden: false, Text: { Length: > 0 } label })
			{
				prompts.Add(new Fable3ScreenPage.Prompt(button, label));
			}
		}

		return new Fable3ScreenPage.QueryBox(title, text, prompts);
	}

	/// <summary>The text the guest wrote into an Options element, by path, or null.</summary>
	private static string? Stated(string path)
	{
		foreach (KeyValuePair<(uint Presentation, string Path), Fable3Elements.Element> entry in Fable3Elements.All)
		{
			if (entry.Key.Path == path && entry.Value.Text is { Length: > 0 } text)
			{
				return text;
			}
		}

		return null;
	}

	private static IEnumerable<KeyValuePair<string, string>> StatedTexts()
	{
		foreach (KeyValuePair<(uint Presentation, string Path), Fable3Elements.Element> entry in Fable3Elements.All)
		{
			if (entry.Value.Text is { Length: > 0 } text)
			{
				yield return new KeyValuePair<string, string>(entry.Key.Path, text);
			}
		}
	}

	/// <summary>The Options object this frame; false when the screen is not showing.</summary>
	private static bool Read()
	{
		try
		{
			return RefiiHostGetFable3Options(_words, (uint)_words.Length, _values, (uint)_values.Length) != 0u;
		}
		catch (System.EntryPointNotFoundException)
		{
			return false;
		}
	}

	[DllImport(HostBridge.Library)]
	private static extern uint RefiiHostGetFable3Options([Out] uint[] words, uint wordCapacity, [Out] float[] values, uint valueCapacity);
}
