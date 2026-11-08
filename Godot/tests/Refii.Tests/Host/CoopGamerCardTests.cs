namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

[TestSuite]
public class CoopGamerCardTests
{
	private static NetBridge.GamerCard Card(string peer = "12D3KooWPeer", bool hosting = false, bool partner = false) =>
		new(1u, peer, "Daring Pony", Friend: false, hosting, partner);

	[TestCase]
	public void TestAHostingPlayerIsOfferedAsAJoin()
	{
		AssertThat(Engine_UiGamerCard.Decide(Card(hosting: true), canInvite: true))
			.IsEqual(Engine_UiGamerCard.Offer.Join);
		AssertThat(Engine_UiGamerCard.Decide(Card(hosting: true), canInvite: false))
			.IsEqual(Engine_UiGamerCard.Offer.Join);
	}

	[TestCase]
	public void TestAPlayerNotHostingIsInvitedOnlyWhenThisGameHasASeat()
	{
		AssertThat(Engine_UiGamerCard.Decide(Card(), canInvite: true)).IsEqual(Engine_UiGamerCard.Offer.Invite);
		AssertThat(Engine_UiGamerCard.Decide(Card(), canInvite: false)).IsEqual(Engine_UiGamerCard.Offer.Nothing);
	}

	[TestCase]
	public void TestThePartnerIsNeitherJoinedNorInvited()
	{
		AssertThat(Engine_UiGamerCard.Decide(Card(hosting: true, partner: true), canInvite: true))
			.IsEqual(Engine_UiGamerCard.Offer.Partner);
	}

	[TestCase]
	public void TestAnUnresolvedXuidIsUnknown()
	{
		AssertThat(Engine_UiGamerCard.Decide(Card(peer: string.Empty, hosting: true), canInvite: true))
			.IsEqual(Engine_UiGamerCard.Offer.Unknown);
	}

	[TestCase]
	public void TestOnlyJoinAndInviteAreQuestions()
	{
		AssertThat(Engine_UiGamerCard.Asks(Engine_UiGamerCard.Offer.Join)).IsTrue();
		AssertThat(Engine_UiGamerCard.Asks(Engine_UiGamerCard.Offer.Invite)).IsTrue();
		AssertThat(Engine_UiGamerCard.Asks(Engine_UiGamerCard.Offer.Partner)).IsFalse();
		AssertThat(Engine_UiGamerCard.Asks(Engine_UiGamerCard.Offer.Nothing)).IsFalse();
		AssertThat(Engine_UiGamerCard.Asks(Engine_UiGamerCard.Offer.Unknown)).IsFalse();
		AssertThat(Engine_UiGamerCard.Accept(Engine_UiGamerCard.Offer.Join)).IsEqual("Join Game");
	}

	[TestCase]
	public void TestTheJoinQuestionNamesThePlayerAndWarnsOfLeaving()
	{
		string text = Engine_UiGamerCard.Text(Engine_UiGamerCard.Offer.Join, "Daring Pony");
		AssertThat(text).Contains("Daring Pony's game");
		AssertThat(text).Contains("leaves this one");
	}
}
