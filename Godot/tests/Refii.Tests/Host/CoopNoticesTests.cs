namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Host;
using Refii.Ui;
using Refii.Titles.Fable2;

[TestSuite]
public class CoopNoticesTests
{
	[TestCase]
	public void TestLocalJoinTellsJoinThenProgressionOncePerRun()
	{
		var notices = new Engine_UiCoopNotices();
		notices.LocalCoop(false, true, told: true);

		AssertThat(notices.Tick(0.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.LocalJoined);
		AssertThat(notices.Tick(1.0, string.Empty, inGame: true)).IsNull();
		AssertThat(notices.Tick(4.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.Progression);

		notices.LocalCoop(true, false, told: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.LocalLeft);

		notices.LocalCoop(false, true, told: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.LocalJoined);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsNull();
	}

	[TestCase]
	public void TestOnlineJoinWaitsForTheName()
	{
		var notices = new Engine_UiCoopNotices();
		notices.Session(CoopSession.Phase.Hosting, CoopSession.Phase.PeerJoined, CoopSession.Role.Host,
			told: true, refused: false);

		AssertThat(notices.Tick(1.0, string.Empty, inGame: true)).IsNull();
		AssertThat(notices.Tick(1.0, "Sparrow", inGame: true)).IsEqual(Engine_UiCoopNotices.HostJoined("Sparrow"));
	}

	[TestCase]
	public void TestOnlineJoinFallsBackWithoutAName()
	{
		var notices = new Engine_UiCoopNotices();
		notices.Session(CoopSession.Phase.Solo, CoopSession.Phase.PeerJoined, CoopSession.Role.Client,
			told: true, refused: false);

		AssertThat(notices.Tick(Engine_UiCoopNotices.NameWaitSeconds + 1.0, string.Empty, inGame: true))
			.IsEqual(Engine_UiCoopNotices.JoinerJoined(string.Empty));
	}

	[TestCase]
	public void TestLossIsToldBySideAndNotAfterARefusal()
	{
		var joiner = new Engine_UiCoopNotices();
		joiner.Session(CoopSession.Phase.Solo, CoopSession.Phase.PeerJoined, CoopSession.Role.Client,
			told: true, refused: false);
		joiner.Session(CoopSession.Phase.PeerJoined, CoopSession.Phase.PeerLost, CoopSession.Role.Solo,
			told: true, refused: false);
		AssertThat(joiner.Tick(0.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.JoinerLost);

		var refused = new Engine_UiCoopNotices();
		refused.Session(CoopSession.Phase.Hosting, CoopSession.Phase.PeerJoined, CoopSession.Role.Host,
			told: true, refused: false);
		refused.Session(CoopSession.Phase.PeerJoined, CoopSession.Phase.PeerLost, CoopSession.Role.Host,
			told: true, refused: true);
		AssertThat(refused.Tick(0.0, string.Empty, inGame: true)).IsNull();
	}

	[TestCase]
	public void TestPressStartOncePerRunAndAgainForANewPad()
	{
		var notices = new Engine_UiCoopNotices();
		notices.Pads(new[] { 0 }, open: true);
		AssertThat(notices.Tick(0.0, string.Empty, inGame: true)).IsNull();

		notices.Pads(new[] { 0, 1 }, open: true);
		AssertThat(notices.Tick(0.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.PressStart);

		notices.Pads(new[] { 0, 1 }, open: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsNull();

		notices.Pads(new[] { 0, 1, 2 }, open: true);
		AssertThat(notices.Tick(5.0, string.Empty, inGame: true)).IsEqual(Engine_UiCoopNotices.PressStart);
	}
}
