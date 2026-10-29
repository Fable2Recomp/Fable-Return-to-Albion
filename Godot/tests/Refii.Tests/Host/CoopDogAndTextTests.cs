namespace Refii.Tests;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Titles.Fable2;

[TestSuite]
public class CoopDogAndTextTests
{
	[TestCase]
	public void TestRelabelNamesThePartner()
	{
		AssertThat(CoopText.Relabel("Henchman Wins")).IsEqual("Player Two Wins");
		AssertThat(CoopText.Relabel("Give %s to your Henchman?")).IsEqual("Give %s to your partner?");
		AssertThat(CoopText.Relabel("Co-op Henchman")).IsEqual("Co-op Partner");
		AssertThat(CoopText.Relabel("The Henchman can wait.")).IsEqual("Your partner can wait.");
		AssertThat(CoopText.Relabel("Hero's Score: %1\nHenchman's Score: %2"))
			.IsEqual("Player One's Score: %1\nPlayer Two's Score: %2");
	}

	[TestCase]
	public void TestRelabelLeavesOtherTextAlone()
	{
		const string text = "Hero's Score: %1";
		AssertThat(CoopText.Relabel(text)).IsEqual(text);
	}

	[TestCase]
	public void TestHeroStateCarriesTheDog()
	{
		var kit = new CoopHeroKit.Kit(
			Known: true, Female: false, Morality: 0f, Purity: 0f, Renown: 0,
			Strength: 0.5f, Skill: 0.25f, Will: 0.75f,
			AbilityLevels: new int[15], Worn: new uint[] { 0x1234u }, Inventory: new[] { (7u, 2) },
			Name: "Sparrow", Money: 100, Experience: new float[4], TotalExperience: new float[4],
			Face: 0u, Fat: 0f, Height: 0f, Tan: 0f,
			DogDefinition: 42u, DogSearching: 3, DogCombat: 4,
			DogTricks: new[] { "ExpressionDogTrickBeg", "ExpressionDogTrickWave" },
			DogWorn: new uint[] { 0xCAFEu }, DogName: "Rex");

		byte[] bytes = CoopHeroState.Serialize(kit, 9u);
		var decoded = CoopHeroState.Deserialize(bytes);

		AssertThat(decoded.HasValue).IsTrue();
		CoopHeroKit.Kit back = decoded!.Value.Kit;
		AssertThat(decoded.Value.Version).IsEqual(9u);
		AssertThat(back.DogDefinition).IsEqual(42u);
		AssertThat(back.DogSearching).IsEqual(3);
		AssertThat(back.DogCombat).IsEqual(4);
		AssertThat(string.Join(",", back.DogTricks ?? Array.Empty<string>()))
			.IsEqual("ExpressionDogTrickBeg,ExpressionDogTrickWave");
		AssertThat((back.DogWorn ?? Array.Empty<uint>()).Length).IsEqual(1);
		AssertThat((back.DogWorn ?? Array.Empty<uint>())[0]).IsEqual(0xCAFEu);
		AssertThat(back.DogName).IsEqual("Rex");
		AssertThat(back.Name).IsEqual("Sparrow");
	}
}
