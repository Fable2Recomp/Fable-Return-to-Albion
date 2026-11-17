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

	private static CoopHeroKit.Kit LooksKit() => new(
		Known: true, Female: true, Morality: 0f, Purity: 0f, Renown: 0,
		Strength: 0.5f, Skill: 0.25f, Will: 0.75f,
		AbilityLevels: new int[15], Worn: new uint[] { 0x1234u, 0x5678u }, Inventory: new[] { (7u, 2) },
		Name: string.Empty, Money: 100, Experience: new float[4], TotalExperience: new float[4],
		Face: 0u, Fat: 0f, Height: 0f, Tan: 0f,
		DogName: "Rex",
		WornDyes: new[]
		{
			new Fable2Bridge.WornDye(0x1234u, new Fable2Bridge.SlotDye(200, 10, 30, 0.5f), null),
			new Fable2Bridge.WornDye(0x5678u, null, new Fable2Bridge.SlotDye(1, 2, 3, -0.25f)),
		},
		Scars: new[] { new Fable2Bridge.Scar(0x400u, 0xBEEFu, true), new Fable2Bridge.Scar(8u, 0xF00Du, false) });

	[TestCase]
	public void TestHeroStateCarriesDyesAndScars()
	{
		CoopHeroKit.Kit back = CoopHeroState.Deserialize(CoopHeroState.Serialize(LooksKit(), 3u))!.Value.Kit;

		AssertThat(back.Female).IsTrue();
		Fable2Bridge.WornDye[] dyes = back.WornDyes ?? Array.Empty<Fable2Bridge.WornDye>();
		AssertThat(dyes.Length).IsEqual(2);
		AssertThat(dyes[0]).IsEqual(new Fable2Bridge.WornDye(0x1234u, new Fable2Bridge.SlotDye(200, 10, 30, 0.5f), null));
		AssertThat(dyes[1]).IsEqual(new Fable2Bridge.WornDye(0x5678u, null, new Fable2Bridge.SlotDye(1, 2, 3, -0.25f)));
		Fable2Bridge.Scar[] scars = back.Scars ?? Array.Empty<Fable2Bridge.Scar>();
		AssertThat(scars.Length).IsEqual(2);
		AssertThat(scars[0]).IsEqual(new Fable2Bridge.Scar(0x400u, 0xBEEFu, true));
		AssertThat(scars[1]).IsEqual(new Fable2Bridge.Scar(8u, 0xF00Du, false));
	}

	[TestCase]
	public void TestHeroStateFromAnOlderPeerHasNoLooks()
	{
		// An older peer's record ends at the dog name: cut the extension off a current one.
		byte[] whole = CoopHeroState.Serialize(LooksKit() with { WornDyes = null, Scars = null }, 4u);
		int extension = 4 + 2 + 1;
		byte[] older = whole.AsSpan(0, whole.Length - extension).ToArray();

		var decoded = CoopHeroState.Deserialize(older);
		AssertThat(decoded.HasValue).IsTrue();
		AssertThat(decoded!.Value.Kit.DogName).IsEqual("Rex");
		AssertThat((decoded.Value.Kit.WornDyes ?? Array.Empty<Fable2Bridge.WornDye>()).Length).IsEqual(0);
		AssertThat((decoded.Value.Kit.Scars ?? Array.Empty<Fable2Bridge.Scar>()).Length).IsEqual(0);
	}
}
