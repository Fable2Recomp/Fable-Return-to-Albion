namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Text;
using Refii.Ui;

/// <summary>Checks French fallback for host-authored text without changing unknown or game text.</summary>
[TestSuite]
public class HostTextFallbackTests
{
	/// <summary>Only known common phrases are translated, and other cultures remain unchanged.</summary>
	[TestCase]
	public void CommonPhrasesAreTranslatedOnlyForFrench()
	{
		AssertThat(Engine_HostText.ForCulture("FR-fr", "Quit to Desktop")).IsEqual("Quitter le jeu");
		AssertThat(Engine_HostText.ForCulture("fr-fr", "Player {0}", "Sébastien")).IsEqual("Joueur Sébastien");
		AssertThat(Engine_HostText.ForCulture("en-uk", "Quit to Desktop")).IsEqual("Quit to Desktop");
		AssertThat(Engine_HostText.ForCulture("fr-fr", "Unknown host text")).IsEqual("Unknown host text");
	}

	/// <summary>The GUI fallback uses the host catalog while preserving its Babel-hit signal.</summary>
	[TestCase]
	public void GuiTextFallbackUsesTheCurrentCulture()
	{
		Engine_GameCulture.ResetForTests();
		try
		{
			Engine_GameCulture.Set("fr-fr");
			AssertThat(Engine_UiGuiText.Resolve("Multiplayer", null, out bool localized)).IsEqual("Multijoueur");
			AssertThat(localized).IsFalse();
			Engine_GameCulture.Set("en-uk");
			AssertThat(Engine_UiGuiText.Resolve("Multiplayer", null)).IsEqual("Multiplayer");
		}
		finally
		{
			Engine_GameCulture.ResetForTests();
		}
	}
}
