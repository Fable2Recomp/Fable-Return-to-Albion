using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using Godot;

using Refii.Core;
using Refii.Titles.Fable2.PubGames;
using Refii.Titles.Fable2.PubGames.Framework;
using Refii.Titles.Fable2.PubGames.Keystone;
using Refii.Titles.Fable2.PubGames.MageDice;
using Refii.Titles.Fable2.PubGames.WizardsTower;

namespace Refii.Titles.Fable2;

/// <summary>
/// Proves the money a pub game hands back to the guest is the money the player actually has.
/// </summary>
/// <remarks>
/// THIS IS THE PART THAT CANNOT BE WRONG. Everything else a pub game does is pixels; this is the
/// hero's gold. The guest seeds the game's bank from the real balance and settles by crediting the
/// DIFFERENCE, so a balance handed back a moment too early — before the chips have been netted
/// against the debt and turned into gold — does not look wrong on screen, it just quietly takes
/// the player's winnings.
///
/// IT RUNS WITHOUT GODOT DOING ANYTHING, which is why it is worth having. The whole game layer
/// under <c>Godot/FlashGames</c> is free of any engine dependency, so every one of these cases is
/// exercised directly against the same objects the running game uses — no scene, no frames, no
/// input, and no need for a player to walk into a pub to find out that quitting cost them 500 gold.
///
/// ALL THREE GAMES, NOT JUST KEYSTONE. Mage Dice and Wizard's Tower reach the guest through exactly
/// the same seam and have never been sat down at, so a check that only covered Keystone was
/// covering the one game that had already been played. The three are covered at two different
/// depths and the difference is deliberate:
///
///   - THE SETTLEMENT IS SHARED CODE. <c>Run</c>, <c>EndGame</c> and <c>GameOver</c> live in
///     <c>PubGame</c> and none of the three games overrides any of them — which is not an
///     assumption here, it is <see cref="SharedSettlement"/>, a check that fails if one of them
///     ever does. So the money contract is proved once, on the framework, and the reflection check
///     is what carries that proof to all three.
///   - THE TABLE IS PER GAME. Buy-in, stakes and opponents come out of each game's own
///     settings.xml under the name the guest passes, and there is nothing shared about those. Each
///     game is built for real and its own tables are read.
///
/// The expectations are read off the SWF, not invented: <c>PubGame::Run</c> lends the buy-in as
/// BOTH chips and debt; <c>PubGame::EndGame</c> chooses between warning, cashing out and settling;
/// <c>PubGame::GameOver</c> does the netting; <c>OverlayManager::Init</c> snapshots the bank AFTER
/// the buy-in; <c>KillQuitMenu</c> and <c>KillCashOut</c> both route to <c>GameOver</c>; and
/// <c>GameReallyOver</c> issues the <c>GameOver</c> command. Disassemble them with
/// <c>tools/as2dis.py</c> before changing anything here.
///
/// <code>
/// Godot_v4.7.1-stable_mono_linux.x86_64 --headless --path Godot \
///     --script res://src/Titles/Fable2/tools/PubGameSettlementCheck.cs
/// </code>
/// </remarks>
public partial class PubGameSettlementCheck : SceneTree
{
	private int _checks;

	private int _failures;

	public override void _Initialize()
	{
		string root = ResolveRoot();
		if (root.Length == 0)
		{
			GD.Print("[settle] PUB GAMES NOT FOUND — nothing to check.");
			Quit(1);
			return;
		}

		GD.Print($"[settle] pub games at {root}");

		SharedSettlement();
		BuyInIsLent();
		BankSnapshot();
		KeystoneTables(root);
		MageDiceTables(root);
		WizardsTowerTables(root);
		TutorialScript(root);
		QuitBeforeBetting(root);
		CashOutAhead(root);
		QuitWithLiveBet(root);
		DialogsFreezeTheGame();
		BankCashOutEndsTheGame();
		BankCancelIsDispelled();
		PauseMenuExit();
		ForcedQuickTips();
		KeystoneRound(root);
		KeystoneGameOver(root);
		KeystoneExitMoney(root);

		Finish();
	}

	/// <summary>
	/// The three games settle through the framework, so proving the framework proves all three.
	/// </summary>
	/// <remarks>
	/// THE WHOLE POINT OF THE OTHER SECTIONS RESTS ON THIS ONE. Everything below tests the money
	/// path on a bare <see cref="PubGame"/>, which is only a statement about Mage Dice and Wizard's
	/// Tower for as long as neither of them overrides it. The day one of them does — to skip the
	/// buy-in, or to settle a tournament differently — this fails and says which method, rather
	/// than the suite quietly going on testing a path that game no longer takes.
	/// </remarks>
	private void SharedSettlement()
	{
		GD.Print("\n1. the settlement is shared code");

		foreach (Type game in new[] { typeof(Keystone), typeof(MageDice), typeof(WizardsTower) })
		{
			foreach (string method in new[] { "Run", "EndGame", "GameOver" })
			{
				MethodInfo? found = game.GetMethod(
					method, BindingFlags.Instance | BindingFlags.Public);
				Check(
					$"{game.Name} takes PubGame's {method}",
					found?.DeclaringType, typeof(PubGame));
			}
		}
	}

	/// <summary>
	/// The buy-in is LENT: chips on the table, and a debt for exactly the same amount.
	/// </summary>
	/// <remarks>
	/// <c>PubGame::Run</c> writes both halves — <c>Chips += buyIn</c> then <c>Debt += buyIn</c> —
	/// and <c>GameOver</c> settles by netting one against the other. Taking only the chips leaves
	/// the debt at zero, so <c>debt - chips</c> goes negative and the stack is paid out as gold:
	/// a player who broke even walks away with the buy-in as profit, every session, at every table.
	/// </remarks>
	private void BuyInIsLent()
	{
		GD.Print("\n2. the buy-in is lent, not given");

		Table table = Table.Seat(gold: 2000, buyIn: 500);

		Check("the chips are on the table", table.Human.Chips, 500);
		Check("and the same amount is owed", table.Human.Debt, 500);
		Check("the gold has not moved", table.Human.Gold, 2000);

		// Sitting down and standing straight back up. Nothing was staked, so EndGame settles
		// rather than asking, and the loan cancels against the stack it paid for.
		table.Game.EndGame();

		Check("standing up settles", table.Sink.GameOverIssued, true);
		Check("the loan is repaid", table.Human.Debt, 0);
		Check("the chips are gone", table.Human.Chips, 0);
		Check("and the player is exactly where they started", table.Human.Gold, 2000);
	}

	/// <summary>
	/// The bank measures the session from the moment play starts, not from before the buy-in.
	/// </summary>
	/// <remarks>
	/// <c>PubGame::Run</c> calls <c>overlayManager.Init()</c> AFTER lending the buy-in, and
	/// <c>Init</c> is what reads <c>startingChips</c>, <c>startingGold</c> and
	/// <c>startingDebt</c>. <c>TotalBorrowed</c> adds <c>BuyIn</c> back on its own, so a snapshot
	/// taken one step early counts the buy-in twice and the cash-out card tells the player they
	/// borrowed money they never asked for.
	/// </remarks>
	private void BankSnapshot()
	{
		GD.Print("\n3. the bank is snapshotted after the buy-in");

		Table table = Table.Seat(gold: 2000, buyIn: 500);
		var bank = new Banking(table.Game);

		Check("the starting chips include the buy-in", bank.StartingChips, 500);
		Check("and the starting debt does too", bank.StartingDebt, 500);

		// THE BUY-IN IS ITSELF BORROWED, so this reads 500 rather than 0 — TotalBorrowed adds
		// `BuyIn` back on top of the debt movement precisely because the debt movement nets it out.
		// The number to watch is that it is the buy-in ONCE. A bank snapshotted before Run lends
		// the chips leaves startingDebt at zero and this reads 1000, telling the player at the
		// cheapest table that they have borrowed twice what they sat down with.
		Check("the buy-in reads as borrowed, once", bank.TotalBorrowed(), 500);
		Check("and nothing has been won yet", bank.TotalWinnings(), 0);

		// Borrowing 300 more against the gold is the only thing that should move that line.
		bank.Commit(BankOption.GetChips, 300);
		Check("borrowing 300 more takes it to 800", bank.TotalBorrowed(), 800);
		Check("and still nothing won", bank.TotalWinnings(), 0);
	}

	/// <summary>The variation the guest names is what decides the odds and the stakes.</summary>
	private void KeystoneTables(string root)
	{
		GD.Print("\n4. keystone's own tables reach the game");

		Keystone standard1 = Stone(root, "Standard1", out _, out _);
		Check("Standard1 pays its own 18:1 on OUT3, not the built-in 35",
			standard1.GetPayoutRate(KeystoneSpace.Out3), 18);
		Check("Standard1 pays 200:1 on IN3", standard1.GetPayoutRate(KeystoneSpace.In3), 200);
		Check("Standard1 buys in for 100", standard1.BuyIn, 100);
		Check("Standard1's minimum bet is 5", standard1.MinBet, 5);

		Keystone standard5 = Stone(root, "Standard5", out _, out _);
		Check("Standard5 is a different table entirely",
			standard5.GetPayoutRate(KeystoneSpace.Out3), 190);
		Check("Standard5 buys in for 2000", standard5.BuyIn, 2000);

		// A variation that states no payoutRate inherits the <defaults> array — which is 36 on
		// OUT3, NOT the 35 the game's own InitPayoutRate writes. The file overrides the code even
		// in its defaults section, so "35" appearing here means nothing was applied at all.
		Keystone tutorial = Stone(root, "Tutorial", out _, out _);
		Check("Tutorial inherits the file's default table rather than the code's",
			tutorial.GetPayoutRate(KeystoneSpace.Out3), 36);
		Check("Tutorial is flagged as one", tutorial.Tutorial, true);

		// Only the tournament tables seat opponents; a cash table is the player alone. Worth
		// asserting because a hard-coded opponent count used to override this.
		Check("a cash table seats no AI", standard1.NumberOfAIPlayers, 0);
		Check("a tournament table seats 17",
			Stone(root, "Tournament1", out _, out _).NumberOfAIPlayers, 17);
	}

	/// <summary>
	/// Mage Dice's tables are named after the pubs they sit in, and they are not interchangeable.
	/// </summary>
	/// <remarks>
	/// THE REEL COUNT IS THE TABLE. <c>BoardType</c> picks which of the three stop-and-payout
	/// tables the variation plays — a three-reel board in the Cow and Corset, six at the Tower of
	/// Riches — so a variation name that failed to resolve does not merely mis-price the game, it
	/// deals the wrong number of reels. That makes it the cheapest thing to assert.
	/// </remarks>
	private void MageDiceTables(string root)
	{
		GD.Print("\n5. mage dice's own tables reach the game");

		MageDice cheapest = Dice(root, "CowAndCorset1");
		Check("CowAndCorset1 is the first table the guest can name",
			cheapest.VariantName, "CowAndCorset1");
		Check("it deals three reels", cheapest.TablesA.BoardType, 3);
		Check("it seats no AI", cheapest.NumberOfAIPlayers, 0);
		Check("its buy-in is stated", cheapest.BuyIn > 0, true);
		Check("its reel faces were read", cheapest.TablesA.ReelImages.Count > 0, true);
		Check("and so were its payouts",
			cheapest.TablesA.Payouts[0].Count > 0, true);

		// THE STAKES ARE THE SAME AND THE BOARD IS NOT. Both tables buy in for 100 and bet 1 to
		// 10; what separates them is the fourth reel. A check written against the buy-in would
		// have read "same table" and been wrong about the only thing that differs.
		MageDice richer = Dice(root, "TowerOfRiches1");
		Check("TowerOfRiches1 buys in for the same 100", richer.BuyIn, cheapest.BuyIn);
		Check("but deals four reels", richer.TablesA.BoardType, 4);

		MageDice tutorial = Dice(root, "Tutorial");
		Check("Tutorial is flagged as one", tutorial.Tutorial, true);

		MageDice tournament = Dice(root, "Tournament1");
		Check("Tournament1 is flagged as one", tournament.Tournament, true);
		Check("and it seats opponents", tournament.NumberOfAIPlayers > 0, true);

		// A NAME THE FILE DOES NOT HOLD MUST NOT LOOK LIKE A TABLE. PubGameConfig falls back to
		// <defaults> when the variation is missing, which is the right behaviour but an invisible
		// one — this is the assertion that says the names above were actually found rather than
		// every one of them quietly landing on the same defaults.
		// A NAME THE FILE DOES NOT HOLD gets nothing: Mage Dice's <defaults> states no buyIn at
		// all, so an unresolved variation is a table with no stakes rather than a cheap one. That
		// is worth knowing because it is the shape a mis-spelled guest variation would take.
		MageDice missing = Dice(root, "NoSuchTable");
		Check("a table the file does not hold has no buy-in to fall back on", missing.BuyIn, 0);
	}

	/// <summary>Wizard's Tower deals a different deck per table, and the deck is the game.</summary>
	private void WizardsTowerTables(string root)
	{
		GD.Print("\n6. wizard's tower's own tables reach the game");

		WizardsTower ruby = Tower(root, "Ruby1");
		Check("Ruby1 is the first table the guest can name", ruby.VariantName, "Ruby1");
		Check("its tower has rows", ruby.NumberOfRows > 0, true);
		Check("its deck has a range", ruby.RangeOfCards > 0, true);
		Check("and heroes in it", ruby.NumberOfHeroes > 0, true);
		// FOUR OPPONENTS AT EVERY CASH TABLE, and that is not a Keystone-shaped mistake — Wizard's
		// Tower is dealt to a table rather than played alone, and the count comes from the file's
		// <defaults> because no variation restates it.
		Check("a cash table seats four", ruby.NumberOfAIPlayers, 4);
		Check("Ruby1 buys in for 100", ruby.BuyIn, 100);
		Check("and its deck is 7x10 plus 4 heroes",
			(ruby.RangeOfCards, ruby.NumberOfCards, ruby.NumberOfHeroes), (7, 10, 4));

		WizardsTower diamond = Tower(root, "Diamond1");
		Check("Diamond1 buys in for 500", diamond.BuyIn, 500);
		Check("and deals a shorter deck", diamond.NumberOfCards, 8);

		WizardsTower tutorial = Tower(root, "Tutorial");
		Check("Tutorial is flagged as one", tutorial.Tutorial, true);

		WizardsTower tournament = Tower(root, "Tournament1");
		Check("Tournament1 is flagged as one", tournament.Tournament, true);
		Check("and it seats opponents", tournament.NumberOfAIPlayers > 0, true);

		// A NAME THE FILE DOES NOT HOLD falls back to <defaults>, which buys in for 1 rather than
		// 100. That is the assertion that says the names above were found rather than every one of
		// them quietly landing on the same defaults.
		WizardsTower missing = Tower(root, "NoSuchTable");
		Check("a table the file does not hold falls back to the defaults", missing.BuyIn, 1);
	}

	/// <summary>
	/// The tutorial script walks its 36 slots and stops, rather than running off the end.
	/// </summary>
	/// <remarks>
	/// THE GAPS ARE THE POINT. Blocks 1 and 9 have no stage 2 and block 12 ends at stage 2, so a
	/// script that visits all 36 slots is as wrong as one that stops early — both mean the skips
	/// in <c>TutorialContinue</c> stopped matching the card table they are paired with.
	/// </remarks>
	private void TutorialScript(string root)
	{
		GD.Print("\n7. keystone's tutorial script");

		Keystone game = Stone(root, "Tutorial", out _, out _);
		Seat(game, gold: 2000);
		game.TutorialPreFadeIn();

		Check("starts at block 1 stage 1", (game.TutorialBlock, game.TutorialStage), (1, 1));

		game.TutorialContinue();
		Check("block 1 has no stage 2, so it steps over it",
			(game.TutorialBlock, game.TutorialStage), (1, 3));

		int steps = 0;
		while (game.TutorialBlock != 0 && steps < 200)
		{
			game.TutorialContinue();
			steps++;
		}

		Check("it ends rather than running on", (game.TutorialBlock, game.TutorialStage), (0, 0));
		Check("after 31 more stages - 32 visited of 36 slots", steps, 31);
	}

	/// <summary>
	/// Borrowing chips and leaving without playing must not leave the debt behind.
	/// </summary>
	private void QuitBeforeBetting(string root)
	{
		GD.Print("\n8. quitting after borrowing but before betting");

		Keystone game = Stone(root, "Standard1", out Sink sink, out Overlays overlays);
		KeystonePlayer human = Seat(game, gold: 2000, buyIn: true);

		new Banking(game).Commit(BankOption.GetChips, 500);
		Check("borrowed 500 chips on top of the 100 buy-in",
			(human.Chips, human.Debt), (600, 600));

		game.EndGame();

		Check("nothing was staked, so it settles rather than asking", overlays.Last, "fadeout");
		Check("the game said GameOver", sink.GameOverIssued, true);
		Check("the chips paid the debt off", human.Debt, 0);
		Check("the gold is untouched", human.Gold, 2000);
		Check("the chips are gone", human.Chips, 0);
	}

	/// <summary>The winnings have to become gold before anyone reads the balance.</summary>
	private void CashOutAhead(string root)
	{
		GD.Print("\n9. cashing out ahead");

		Keystone game = Stone(root, "Standard1", out Sink sink, out Overlays overlays);
		KeystonePlayer human = Seat(game, gold: 2000, buyIn: true);

		// A 100 buy-in turned into 900 on the table: 800 of it is the player's.
		human.Chips = 900;
		game.BetsMade = true;

		game.EndGame();
		Check("played and leaving, so the cash-out card comes up", overlays.Last, "cashout");
		Check("and nothing has settled while it is up", sink.GameOverIssued, false);
		Check("the gold has not moved yet", human.Gold, 2000);

		// What closing the card does. The 100 the table lent comes off the top.
		game.GameOver();
		Check("the winnings became gold, less the buy-in", human.Gold, 2800);
		Check("the game said GameOver", sink.GameOverIssued, true);
	}

	/// <summary>Money riding on the next roll is worth stopping the player over.</summary>
	private void QuitWithLiveBet(string root)
	{
		GD.Print("\n10. quitting with a live locked-in bet");

		Keystone game = Stone(root, "Standard1", out Sink sink, out Overlays overlays);
		KeystonePlayer human = Seat(game, gold: 2000, buyIn: true);
		human.Chips = 100;
		human.Bets[KeystoneSpace.Out10] = 20;
		game.ArchPointsLockedIn = true;

		Check("the warning sees the staked bet", game.EndGameWarning(), true);

		game.EndGame();
		Check("so leaving asks first", overlays.Last, "quit");
		Check("and nothing has settled", sink.GameOverIssued, false);
		Check("the bet is still on the table", human.Bets[KeystoneSpace.Out10], 20);
	}

	/// <summary>
	/// The overlay manager is callback 0 and freezes everything behind it while a dialog is up.
	/// </summary>
	/// <remarks>
	/// <c>PubGame::Initialize</c> adds it first (0x00161b); <c>OverlayManager::Run</c> blocks on
	/// <c>AbsorbedInput() | GameIsExiting</c> (0x003279-0x0032e1), which includes the opening
	/// board fade, and says so to the guest with <c>PauseSound</c> on every change.
	/// </remarks>
	private void DialogsFreezeTheGame()
	{
		GD.Print("\n11. dialogs freeze the game behind them");

		Table table = Table.Live(gold: 2000, buyIn: 500);
		Check("the overlay manager is callback 0",
			table.CallbackManager.Callbacks[0] is Dialogs, true);

		table.Frames(1, 0);
		Check("the opening fade holds the game", table.Ticks, 0);
		table.Frames(60, 0);
		Check("and lets it go once it has finished", table.Ticks > 0, true);

		// Blocking is decided before the frame's input, so the game still ticks on the frame the
		// Y press opens the bank, and stops from the next one.
		table.Frames(1, GamePad.YButton);
		Check("Y opens the bank", table.Dialogs.BankShowing, true);
		int before = table.Ticks;
		table.Frames(10, 0);
		Check("and the game does not tick while it is up", table.Ticks, before);
		Check("the guest is told to pause looping sounds",
			table.Sink.Commands.Contains("PauseSound:true"), true);
	}

	/// <summary>The bank's Cash Out ends the session (KillBankingMenu 0x005165-0x0051a8).</summary>
	private void BankCashOutEndsTheGame()
	{
		GD.Print("\n12. cashing out from the bank");

		Table table = Table.Live(gold: 2000, buyIn: 500);
		table.Frames(60, 0);
		table.Press(GamePad.YButton);
		table.Frames(15, 0);
		table.Press(GamePad.DpadUp);
		Check("up from Quick Chips wraps to Cash Out",
			table.Dialogs.SelectedOption, BankOption.CashOut);
		table.Press(GamePad.AButton);
		table.Frames(60, 0);
		Check("Cash Out ends the game", table.Sink.GameOverIssued, true);
		Check("and settles the loan", (table.Human.Chips, table.Human.Debt, table.Human.Gold),
			(0, 0, 2000));
	}

	/// <summary>Every bank exit tells the game (KillBankingMenu 0x005145, 0x0051da, 0x005246).</summary>
	private void BankCancelIsDispelled()
	{
		GD.Print("\n13. closing the bank tells the game");

		Table table = Table.Live(gold: 2000, buyIn: 500);
		table.Frames(60, 0);
		table.Press(GamePad.YButton);
		table.Frames(15, 0);
		table.Press(GamePad.BButton);
		table.Frames(15, 0);
		Check("B closes the bank", table.Dialogs.BankShowing, false);
		Check("and BankingMenuDispelled runs once", table.Dispelled, 1);

		table.Press(GamePad.YButton);
		table.Frames(15, 0);
		table.Press(GamePad.AButton);
		table.Frames(15, 0);
		Check("Quick Chips lends the buy-in again",
			(table.Human.Chips, table.Human.Debt), (1000, 1000));
		Check("and is dispelled too", table.Dispelled, 2);
	}

	/// <summary>Start opens the shim pause menu; Exit ends the game (KillShimPauseMenu 0x007c36).</summary>
	private void PauseMenuExit()
	{
		GD.Print("\n14. the pause menu");

		Table table = Table.Live(gold: 2000, buyIn: 500);
		table.Frames(60, 0);
		table.ActivateShimPause();
		table.Frames(1, 0);
		Check("Start opens the pause menu", table.Dialogs.ShimPauseShowing, true);
		Check("with the pause sound", table.Sink.Commands.Contains("PlaySound:G_pauseUnpause"), true);
		table.Frames(15, 0);
		table.Press(GamePad.DpadDown);
		table.Frames(10, 0);
		table.Press(GamePad.DpadDown);
		table.Press(GamePad.AButton);
		table.Frames(30, 0);
		Check("Controls opens from it", table.Dialogs.ShimControlsShowing, true);
		table.Press(GamePad.BButton);
		table.Frames(30, 0);
		Check("and B returns to the pause menu", table.Dialogs.ShimPauseShowing, true);
		table.Press(GamePad.DpadUp);
		table.Frames(10, 0);
		table.Press(GamePad.DpadUp);
		table.Frames(10, 0);
		table.Press(GamePad.DpadUp);
		table.Press(GamePad.AButton);
		table.Frames(60, 0);
		Check("Exit ends the game", table.Sink.GameOverIssued, true);
	}

	/// <summary>
	/// A forced quick-tips card blocks the game until its timer runs out or A dismisses it
	/// (0x003b5c-0x003c57, 0x003d4c).
	/// </summary>
	private void ForcedQuickTips()
	{
		GD.Print("\n15. forced quick tips");

		Table table = Table.Live(gold: 2000, buyIn: 500);
		table.Dialogs.ForceQuickTips(5000);
		table.Frames(60, 0);
		Check("the forced card is up", table.Dialogs.QuickTipsShowing, true);
		Check("and holds the game", table.Ticks, 0);
		table.Press(GamePad.AButton);
		table.Frames(20, 0);
		Check("A dismisses it", table.Dialogs.QuickTipsShowing, false);
		Check("and the game runs again", table.Ticks > 0, true);
	}

	/// <summary>
	/// A Keystone table run the way the guest runs it — <c>Initialize</c>, the human, <c>Run</c> —
	/// and played through its own callback chain for two rolls.
	/// </summary>
	/// <remarks>
	/// <c>PubGame::Run</c> lends the buy-in, adds the 83 NPCs, allows points and says Running.
	/// A roll that leaves the arch standing goes back to INSIDE betting with the arch bet still
	/// riding (<c>CheckForGameOver</c> 0x007be9-0x007c8d); points reach the guest when the payout
	/// ends (<c>WaitForEndOfPayout</c> 0x0072d3).
	/// </remarks>
	private void KeystoneRound(string root)
	{
		GD.Print("\n16. keystone plays its own lifecycle");

		Keystone game = LiveStone(root, "Standard1", out Sink sink);
		var human = (KeystonePlayer)game.PlayerInfo.GetPlayer(0)!;
		Check("Run added the 83 NPCs", game.PlayerInfo.NumPlayers, 84);
		Check("Run lent the buy-in", (human.Chips, human.Debt), (100, 100));
		Check("Run told the guest the holdings",
			sink.Commands.Exists(c => c.StartsWith(PubCommand.UpdatePlayerHoldings)), true);
		Check("Run allowed points", game.AllowPoints, true);
		Check("Run said Running", sink.Commands.Contains(PubCommand.Running), true);
		Check("the human sits at seat 0", game.BoardInfos[0].GetPlayer(0), human);

		Check("the reveal reaches arch betting", RunUntil(game, "BetOnStones"), true);

		Stake(game, human, KeystoneSpace.Out8, 10);
		PressX(game);
		Check("X with an arch bet reaches inside betting", RunUntil(game, "BetOnInside"), true);

		Stake(game, human, KeystoneSpace.In8, 5);
		game.AllowScriptedRolls = true;
		game.PushScriptedRoll(3, 3, 4);
		int expected = Points(game, KeystoneSpace.Out8, 10) + Points(game, KeystoneSpace.In8, 5);
		PressX(game);
		Check("X rolls and the payout waits for A", RunUntil(game, "WaitForContinue"), true);
		Check("the points reached the guest",
			sink.Commands.Contains($"{PubCommand.PointsEarned}:points:{expected}"), true);
		Check("and the hero's total", human.Points, expected);
		Check("the ten took the OUT10 stone", game.ArchStonesIntact[KeystoneSpace.Out10], true);

		game.SetPadValue(0, 0, 0, 0, 0, 0, 0, GamePad.AButton);
		game.OnEnterFrame(20);
		game.SetPadValue(0, 0, 0, 0, 0, 0, 0, 0);
		Check("the next roll is bet INSIDE, not on the arch", RunUntil(game, "BetOnInside"), true);
		Check("the OUT10 stone fell", game.ArchStonesIntact[KeystoneSpace.Out10], false);
		Check("it is roll 2 of game 1", (game.CurrentRoll, game.CurrentGame), (2, 1));
		Check("the arch bet is still riding", human.Bets[KeystoneSpace.Out8], 10);
		Check("the lost inside bet is gone", (human.Bets[KeystoneSpace.In8], human.Chips), (0, 85));
	}

	/// <summary>
	/// <c>IsGameOver</c> (0x0075ed-0x007654): OUT3, OUT18, or both keystones. A collapse sweeps the
	/// arch bets and starts the next game at arch betting (<c>CollapseArch</c>, <c>StartNextGame</c>).
	/// </summary>
	private void KeystoneGameOver(string root)
	{
		GD.Print("\n17. keystone's arch collapses on 3, 18 and both keystones");

		Keystone bare = Stone(root, "Standard1", out _, out _);
		Seat(bare, gold: 2000);
		bare.ResetArchStones();
		Check("one keystone out is not the end", FallAfter(bare, (3, 3, 4)), false);
		Check("both keystones out is", FallAfter(bare, (3, 4, 4)), true);
		bare.ResetArchStones();
		Check("a 3 collapses it", FallAfter(bare, (1, 1, 1)), true);
		bare.ResetArchStones();
		Check("an 18 collapses it", FallAfter(bare, (6, 6, 6)), true);

		Keystone game = LiveStone(root, "Standard1", out _);
		var human = (KeystonePlayer)game.PlayerInfo.GetPlayer(0)!;
		RunUntil(game, "BetOnStones");
		Stake(game, human, KeystoneSpace.Out8, 10);
		PressX(game);
		RunUntil(game, "BetOnInside");
		game.AllowScriptedRolls = true;
		game.PushScriptedRoll(3, 3, 4);
		PressX(game);
		RunUntil(game, "WaitForContinue");
		PressA(game);
		RunUntil(game, "BetOnInside");
		game.PushScriptedRoll(1, 1, 1);
		PressX(game);
		Check("a 3 on roll 2 is no jackpot but still ends the game",
			RunUntil(game, "WaitForContinue"), true);
		PressA(game);
		Check("the next game opens at arch betting", RunUntil(game, "BetOnStones"), true);
		Check("game 2, roll 1", (game.CurrentGame, game.CurrentRoll), (2, 1));
		Check("the collapse took the riding arch bet", human.Bets[KeystoneSpace.Out8], 0);
		Check("the arch is whole again", game.IsGameOver(), false);
	}

	/// <summary>
	/// <c>GameExiting</c> (0x003a58-0x003aad) and the overlay's chip counts
	/// (<c>KeystoneOverlayManager</c> 0x000499, 0x0004b9-0x000523).
	/// </summary>
	private void KeystoneExitMoney(string root)
	{
		GD.Print("\n18. keystone's exit money");

		Keystone cash = Stone(root, "Standard1", out Sink cashSink, out _);
		KeystonePlayer hero = Seat(cash, gold: 2000, buyIn: true);
		hero.Bets[KeystoneSpace.In8] = 5;
		hero.Chips -= 5;
		cash.ArchPointsLockedIn = true;
		var cashOverlays = new KeystoneOverlayManager(cash, new BareClip());
		Check("chips in play count the stake", cashOverlays.GetChipsInPlay(), 100);
		Check("an unlocked stake can still be taken back",
			cashOverlays.GetChipsInPlayButNotLockedIn(), 5);
		cash.GameOver();
		Check("leaving hands the unlocked stake back", (hero.Gold, hero.Debt), (2000, 0));
		Check("and settles", cashSink.GameOverIssued, true);

		Keystone blood = Stone(root, "KSBloodstone1", out _, out _);
		KeystonePlayer bled = Seat(blood, gold: 2000, buyIn: true);
		bled.Bets[KeystoneSpace.Out8] = blood.GetPayoutBet(KeystoneSpace.Out8);
		bled.Bets[KeystoneSpace.Out9] = blood.GetPayoutBet(KeystoneSpace.Out9);
		int owed = blood.GetPayoutRate(KeystoneSpace.Out8) + blood.GetPayoutRate(KeystoneSpace.Out9);
		var bloodOverlays = new KeystoneOverlayManager(blood, new BareClip());
		Check("Bloodstone owes each live arch bet's rate", blood.GetEarlyExitPayment(), owed);
		Check("and its chips in play ignore the lammers", bloodOverlays.GetChipsInPlay(), bled.Chips);
		Check("the cash-out counts the debt", bloodOverlays.GetChipsInPlayButNotLockedIn(), -owed);
		blood.GameOver();
		Check("leaving early pays it out of the stack, so it stays owed",
			(bled.Gold, bled.Debt), (2000, owed));
	}

	private static Keystone LiveStone(string root, string variation, out Sink sink)
	{
		sink = new Sink();
		var game = new Keystone(sink, new PubManualTimer(), new PubRandom(1))
		{
			VariantName = variation,
			Root = new BareClip("player0chips0", "player0chips1", "player0chips2"),
		};

		Configure(root, "keystone", game);
		game.OverlayManager = new Overlays(game);
		game.Initialize();
		PubGameSeed seed = PubGameSeed.Default(2000);
		seed.CreateHuman(game);
		seed.ApplyAfterSettings(game);
		game.Run();
		return game;
	}

	private static bool RunUntil(Keystone game, string pump)
	{
		for (int frame = 0; frame < 2000; frame++)
		{
			if (game.PumpFunction?.Method.Name == pump)
			{
				return true;
			}

			game.SetPadValue(0, 0, 0, 0, 0, 0, 0, 0);
			game.OnEnterFrame(20);
		}

		return false;
	}

	private static void PressX(Keystone game) => Press(game, GamePad.XButton);

	private static void PressA(Keystone game) => Press(game, GamePad.AButton);

	private static void Press(Keystone game, int button)
	{
		game.SetPadValue(0, 0, 0, 0, 0, 0, 0, button);
		game.OnEnterFrame(20);
		game.SetPadValue(0, 0, 0, 0, 0, 0, 0, 0);
		game.OnEnterFrame(20);
	}

	private static void Stake(Keystone game, KeystonePlayer human, int space, int amount) =>
		game.ChangeBet(amount, human, space, false, null);

	private static int Points(Keystone game, int space, int bet) =>
		(int)Math.Floor((double)bet / game.GetPayoutBet(space) *
			Math.Min(game.GetPayoutBet(space), game.GetPayoutRate(space)));

	private static bool FallAfter(Keystone game, (int, int, int) roll)
	{
		game.SetRoll(roll.Item1, roll.Item2, roll.Item3);
		game.FindWinningSpaces(true);
		game.RemoveArchStone();
		return game.IsGameOver();
	}

	private Keystone Stone(string root, string variation, out Sink sink, out Overlays overlays)
	{
		sink = new Sink();
		var game = new Keystone(sink, new PubManualTimer(), new PubRandom(1))
		{
			VariantName = variation,
		};

		Configure(root, "keystone", game);

		game.InitBetLimits();
		game.InitBoardInfos();

		overlays = new Overlays(game);
		game.OverlayManager = overlays;
		return game;
	}

	private static MageDice Dice(string root, string variation)
	{
		var game = new MageDice(
			new Sink(), new PubManualTimer(), new PubRandom(1), new BareClip())
		{
			VariantName = variation,
		};

		Configure(root, "magedice", game);
		return game;
	}

	private static WizardsTower Tower(string root, string variation)
	{
		var game = new WizardsTower(
			new Sink(), new PubManualTimer(), new PubRandom(1), new BareClip())
		{
			VariantName = variation,
		};

		Configure(root, "wizardstower", game);
		return game;
	}

	/// <summary>
	/// Applies the shared settings and the game's own, exactly as its host does.
	/// </summary>
	/// <remarks>
	/// THE GAME'S OWN CONFIG CLASS, not <c>PubGameConfig</c> alone. The shared pass reads the
	/// stakes and the seat count; everything that makes a table that game's table — Keystone's
	/// payout rates, Mage Dice's reel stops, Wizard's Tower's deck — is read by the per-game
	/// class on top of it, and a check that skipped it would pass on a game with no board.
	/// </remarks>
	private static void Configure(string root, string folder, PubGame game)
	{
		PubGameSettings? shared = Settings(Path.Combine(root, "common", "settings.xml"));
		PubGameSettings? own = Settings(Path.Combine(root, folder, "settings.xml"));

		switch (game)
		{
			case Keystone stone:
				KeystoneConfig.Apply(stone, shared, own);
				break;
			case MageDice dice:
				MageDiceConfig.Apply(dice, shared, own);
				break;
			case WizardsTower tower:
				WizardsTowerConfig.Apply(tower, shared, own);
				break;
			default:
				PubGameConfig.Apply(game, shared, own, new List<string>());
				break;
		}
	}

	private static PubGameSettings? Settings(string path) =>
		File.Exists(path)
			? PubGamesText.ParseSettings(File.ReadAllBytes(path), out _)
			: null;

	/// <summary>Seats the hero, optionally having them buy in the way <c>Run</c> does.</summary>
	private static KeystonePlayer Seat(Keystone game, int gold, bool buyIn = false)
	{
		var human = new KeystonePlayer(0, false, "Hero", gold, 0, 1, 0, 0);
		if (buyIn)
		{
			human.Chips = game.BuyIn;
			human.Debt = game.BuyIn;
		}

		game.PlayerInfo.AddPlayer(0, human);
		game.BoardInfos[0].SetPlayer(0, human);
		return human;
	}

	private static string ResolveRoot() =>
		Engine_GameData.ResolveWhere(
			candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0) is { Length: > 0 } found
			? PubGamesCatalog.ResolveRoot(found)
			: string.Empty;

	private void Check<T>(string what, T actual, T expected)
	{
		_checks++;
		bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
		GD.Print($"  [{(ok ? "ok  " : "FAIL")}] {what}: {actual}" +
			(ok ? string.Empty : $" (expected {expected})"));
		if (!ok)
		{
			_failures++;
		}
	}

	private void Finish()
	{
		GD.Print(_failures == 0
			? $"\n[settle] PUB GAME SETTLEMENT OK — {_checks} check(s)"
			: $"\n[settle] PUB GAME SETTLEMENT FAILED — {_failures} of {_checks} check(s)");
		Quit(_failures == 0 ? 0 : 1);
	}

	/// <summary>
	/// A table that plays nothing, so the settlement can be read on its own.
	/// </summary>
	/// <remarks>
	/// A BARE <c>PubGame</c> IS THE POINT, not a shortcut. <c>Run</c>, <c>EndGame</c> and
	/// <c>GameOver</c> are the framework's and all three games take them unmodified — see
	/// <see cref="SharedSettlement"/> — so the money contract is a property of this class rather
	/// than of any board. Driving it here proves it for Mage Dice and Wizard's Tower without
	/// standing up a scene neither of them can be built without.
	/// </remarks>
	private sealed class Table : PubGame
	{
		private Table(Sink sink)
			: base(sink, new PubManualTimer(), new PubRandom(1))
		{
			Sink = sink;
		}

		public Sink Sink { get; }

		public Player Human => PlayerInfo.GetPlayer(0)!;

		public PubGame Game => this;

		public int Ticks { get; private set; }

		public int Dispelled { get; private set; }

		public Dialogs Dialogs => (Dialogs)OverlayManager!;

		public override void BankingMenuDispelled() => Dispelled++;

		/// <summary>A table with the real overlay manager and one game callback that counts.</summary>
		public static Table Live(int gold, int buyIn)
		{
			var table = new Table(new Sink()) { BuyIn = buyIn, RetailGame = true };
			table.PlayerInfo.AddPlayer(0, new Player(0, false, "Hero", gold, 0, 1, 0, 0));
			table.OverlayManager = new Dialogs(table);
			table.Initialize();
			table.CallbackManager.AddCallbackFunction((_, _, _) =>
			{
				table.Ticks++;
				return false;
			}, null);
			table.Run();
			return table;
		}

		/// <summary><paramref name="count"/> guest advances of 20 ms holding <paramref name="buttons"/>.</summary>
		public void Frames(int count, int buttons)
		{
			for (int i = 0; i < count; i++)
			{
				SetPadValue(0, 0, 0, 0, 0, 0, 0, buttons);
				OnEnterFrame(20);
			}
		}

		public void Press(int button)
		{
			Frames(1, button);
			Frames(1, 0);
		}

		public static Table Seat(int gold, int buyIn)
		{
			var table = new Table(new Sink()) { BuyIn = buyIn, OverlayManager = null };
			table.PlayerInfo.AddPlayer(0, new Player(0, false, "Hero", gold, 0, 1, 0, 0));
			table.OverlayManager = new Overlays(table);
			table.Initialize();
			table.Run();
			return table;
		}
	}

	/// <summary>A clip that exists and does nothing, for a game built but never dealt.</summary>
	/// <remarks>
	/// EVERY LOOKUP MISSES except the children it is built with, deliberately. Mage Dice and
	/// Wizard's Tower take their stage in the constructor and touch it only once they are
	/// initialised, which this check never does — so the honest stand-in is one that answers "no
	/// such child" rather than one that invents a tree. Keystone's lifecycle check names the
	/// three seat-0 chip markers, which is what makes a board of one seat.
	/// </remarks>
	private sealed class BareClip : IPubClip
	{
		private readonly HashSet<string> _children;

		public BareClip(params string[] children)
		{
			_children = new HashSet<string>(children);
		}

		public float X { get; set; }

		public float Y { get; set; }

		public float Alpha { get; set; }

		public float XScale { get; set; }

		public float YScale { get; set; }

		public float Rotation { get; set; }

		public bool Visible { get; set; }

		public int CurrentFrame => 1;

		public int TotalFrames => 1;

		public string Text { get; set; } = string.Empty;

		public float Height => 0f;

		public float TextHeight => 0f;

		public string Name => "bare";

		public void GotoAndStop(int frame)
		{
		}

		public void GotoAndStop(string label)
		{
		}

		public void GotoAndPlay(int frame)
		{
		}

		public void Play()
		{
		}

		public void Stop()
		{
		}

		public IPubClip? GetChild(string name) => _children.Contains(name) ? new BareClip() : null;

		public IPubClip? AttachMovie(string exportName, string instanceName, int depth) => null;

		public IPubClip CreateEmptyMovieClip(string name, int depth) => new BareClip();

		public int GetDepth() => 0;

		public void RemoveMovieClip()
		{
		}

		public void SwapDepths(int depth)
		{
		}

		public int GetNextHighestDepth() => 1;
	}

	/// <summary>The shared overlay manager on a stage with no exports.</summary>
	private sealed class Dialogs : OverlayManager
	{
		public Dialogs(PubGame game)
			: base(game, new BareClip())
		{
		}
	}

	/// <summary>Records the commands the game issues, as the real host does.</summary>
	private sealed class Sink : IPubGameHost
	{
		public bool GameOverIssued { get; private set; }

		public List<string> Commands { get; } = new();

		public void Command(string command, string? argument)
		{
			Commands.Add(argument is null ? command : $"{command}:{argument}");
			if (command == PubCommand.GameOver)
			{
				GameOverIssued = true;
			}
		}
	}

	/// <summary>
	/// Stands in for the overlays, recording which one the game asked for.
	/// </summary>
	/// <remarks>
	/// <c>ActivateFadeOut</c> completes immediately here. In the running game the fade is an
	/// animation and its completion callback is what calls <c>GameReallyOver</c>; collapsing that
	/// keeps the check free of a clock without changing which calls happen or in what order.
	/// </remarks>
	private sealed class Overlays : IPubOverlayManager
	{
		private readonly PubGame _game;

		public Overlays(PubGame game)
		{
			_game = game;
		}

		public string Last { get; private set; } = string.Empty;

		public void Init() => Last = "init";

		public void ActivateNeedChipsAlert() => Last = "needchips";

		public void ActivateStarRatingDialog(
			bool table = false, bool concept = false,
			string? gameName = null, int min = 0, int max = 0) => Last = "star";

		public void ActivateFade() => Last = "fade";

		public void ActivateFadeOut()
		{
			Last = "fadeout";
			_game.GameReallyOver();
		}

		public void ActivateQuitConfirmation() => Last = "quit";

		public void ActivateCashOut() => Last = "cashout";

		public void ActivateGameHelp() => Last = "help";

		public void ActivateShimPauseMenu() => Last = "pause";

		public void ShowSaveIcon(bool show)
		{
		}

		public void CleanUp()
		{
		}
	}
}
