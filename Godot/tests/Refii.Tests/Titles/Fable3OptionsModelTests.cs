namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;

/// <summary>
/// Fable 3's Options rows from the object's snapshot: the guest's row order per page
/// (sub_825CC938 / sub_825CCB90), its value tables, and the stated-text pages.
/// </summary>
[TestSuite]
public class Fable3OptionsModelTests
{
	private static Fable3OptionsModel.Snapshot Snapshot(int page, int row, uint flag108 = 0u, uint flag112 = 0u, uint canSave = 1u) =>
		Fable3OptionsModel.Parse(
			new uint[] { 0x12345678u, (uint)page, (uint)row, 1u, 0u, 1u, 3u, 2u, 1u, 4u, flag108, flag112, 1u, 0u, canSave },
			new[] { 0.5f, 1.0f, 0.75f, 0.9f });

	private static string? NoStated(string path) => null;

	private static string Resolve(string tag) => "<" + tag + ">";

	[TestCase]
	public void GameSettingsRowsFollowTheWriterOrder()
	{
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.SettingsRows(Snapshot(Fable3OptionsModel.PageGameSettings, 4), Resolve, new List<string>());
		AssertThat(rows.Count).IsEqual(9);
		AssertThat(rows[0].Name).IsEqual("Controller");
		AssertThat(rows[0].Value).IsEqual("<TEXT_GUI_OPTIONS_ON>");
		AssertThat(rows[1].Value).IsEqual("<TEXT_GUI_OPTIONS_OFF>");
		AssertThat(rows[3].Name).IsEqual("Gamma");
		AssertThat(rows[3].Value).IsEqual(Fable3OptionsModel.Slider(0.5f));
		AssertThat(rows[4].Value).IsEqual("<GUI_OPTIONS_BCRUMB_HIGH>");
		AssertThat(rows[6].Name).IsEqual("SoundEffects");
		AssertThat(rows[6].Value).IsEqual(Fable3OptionsModel.Slider(0.75f));
		AssertThat(rows[8].Value).IsEqual("<GUI_SCREEN_OPTIONS_AUDIO_SURROUND>");
		AssertThat(rows[8].Description).IsEqual("GUI_SCREEN_OPTIONS_AUDIO_SPEAKERS_DESC");
	}

	[TestCase]
	public void LitePageLeadsWithTheLanguage()
	{
		var languages = new List<string> { "English", "Français" };
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.SettingsRows(Snapshot(Fable3OptionsModel.PageGameSettingsLite, 1), Resolve, languages);
		AssertThat(rows.Count).IsEqual(7);
		AssertThat(rows[0].Name).IsEqual("Language");
		AssertThat(rows[0].Value).IsEqual("Français");
		AssertThat(rows[1].Name).IsEqual("Subtitles");
		AssertThat(rows[6].Name).IsEqual("SpeakerType");
	}

	[TestCase]
	public void SliderIsTwentyPipsAndAPercentage()
	{
		AssertThat(Fable3OptionsModel.Slider(0f)).IsEqual(new string('□', 20) + "  0%");
		AssertThat(Fable3OptionsModel.Slider(1f)).IsEqual(new string('■', 20) + "  100%");
		AssertThat(Fable3OptionsModel.Slider(0.5f)).IsEqual(new string('■', 10) + new string('□', 10) + "  50%");
	}

	[TestCase]
	public void StatedPagesListTheSectionsRowTexts()
	{
		var texts = new List<KeyValuePair<string, string>>
		{
			new("Options.Sections.Options.SaveManagement", "Save Management"),
			new("Options.Sections.Options.GameSettings", "Game Settings"),
			new("Options.Sections.Options.GameSettings.Description", "nested, not a row"),
			new("Options.Sections.GameSettings.Gamma", "another page"),
			new("HeroStatus.Section1.Values.Strength", "another screen"),
		};
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.StatedRows(Fable3OptionsModel.PageOptions, texts);
		AssertThat(rows.Count).IsEqual(2);
		AssertThat(rows[0].Label).IsEqual("Save Management");
		AssertThat(rows[1].Name).IsEqual("GameSettings");
		AssertThat(Fable3OptionsModel.SettingsRows(Snapshot(Fable3OptionsModel.PageOptions, 1), Resolve, new List<string>()).Count).IsEqual(0);
		AssertThat(Snapshot(Fable3OptionsModel.PageOptions, 1).CanSave).IsTrue();
	}

	[TestCase]
	public void OptionsPageListsTheHandlersRows()
	{
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.Rows(Snapshot(Fable3OptionsModel.PageOptions, 2), Resolve, new List<string>(), NoStated);
		AssertThat(rows.Count).IsEqual(3);
		AssertThat(rows[0].Name).IsEqual("SaveManagement");
		AssertThat(rows[0].Label).IsEqual("<GUI_SAVE_MANAGEMENT>");
		AssertThat(rows[1].Name).IsEqual("GameSettings");
		AssertThat(rows[2].Name).IsEqual("NewGame");
		AssertThat(rows[2].Description).IsEqual("GUI_NEW_GAME_DESC");

		// A stated label wins over the tag; the multiplayer page carries the Kick/Leave row.
		List<Fable3OptionsModel.Row> stated = Fable3OptionsModel.Rows(Snapshot(Fable3OptionsModel.PageMultiplayer, 4, flag108: 1u), Resolve, new List<string>(),
			path => path == "Options.Sections.Multiplayer.SelectionText.Kick/Leave" ? "Kick" : null);
		AssertThat(stated.Count).IsEqual(4);
		AssertThat(stated[3].Label).IsEqual("Kick");
	}

	[TestCase]
	public void SaveManagementGhostsWhatTheShowGhosts()
	{
		List<Fable3OptionsModel.Row> rows = Fable3OptionsModel.Rows(Snapshot(Fable3OptionsModel.PageSaveManagement, 1, flag112: 1u, canSave: 0u), Resolve, new List<string>(), NoStated);
		AssertThat(rows.Count).IsEqual(2);
		AssertThat(rows[0].Name).IsEqual("Save&Continue");
		AssertThat(rows[0].Ghosted).IsTrue();
		AssertThat(rows[1].Name).IsEqual("LoadMangement");
		AssertThat(rows[1].Ghosted).IsTrue();
		AssertThat(Fable3OptionsModel.Rows(Snapshot(Fable3OptionsModel.PageSaveManagement, 1), Resolve, new List<string>(), NoStated)[0].Ghosted).IsFalse();
	}

	[TestCase]
	public void SectionPathsAreTheGuestsElementNames()
	{
		AssertThat(Fable3OptionsModel.SectionPath(Fable3OptionsModel.PageGameSettings)).IsEqual("Options.Sections.GameSettings");
		AssertThat(Fable3OptionsModel.SectionPath(Fable3OptionsModel.PageMultiplayer)).IsEqual("Options.Sections.Multiplayer");
		AssertThat(Fable3OptionsModel.Line(new Fable3OptionsModel.Row("Gamma", "Gamma", "50%", string.Empty))).IsEqual("Gamma    50%");
		AssertThat(Fable3OptionsModel.Line(new Fable3OptionsModel.Row("Save", "Save", string.Empty, string.Empty))).IsEqual("Save");
	}
}
