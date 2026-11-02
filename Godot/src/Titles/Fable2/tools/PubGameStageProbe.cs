using System;
using System.Collections.Generic;
using System.Diagnostics;

using Godot;

using Refii.Core;
using Refii.Titles.Fable2.PubGames;

namespace Refii.Titles.Fable2;

/// <summary>
/// Builds all three pub-game stages and proves each one is the stage its game addresses.
/// </summary>
/// <remarks>
/// A STAGE THAT WILL NOT LOAD IS A HANG, not a blank screen. The guest waits at state 2 for as
/// long as it takes, so a host whose <c>Load</c> fails leaves the player in a pub with nothing on
/// screen and no way out. <c>GamePubGames</c> now answers that case by settling the balance
/// unchanged — but answering it is damage control, and the point of this probe is to find out on a
/// build machine rather than in a pub.
///
/// THE SCENE IS EVERY GAME'S ONLY PATH. Keystone, Mage Dice and Wizard's Tower each build their
/// board from the baked scene alone; a scene that will not load is a board that does not open.
///
/// THE NAME CENSUS IS THE SECOND HALF. A stage that loads but is the WRONG stage — or the right
/// one baked from a SWF whose export names moved — fails silently: every <c>GetChild</c> returns
/// null, the game runs with no board, and no defect is reported anywhere. So each game's own
/// root-level lookups are checked against the stage it just built. See <see cref="Addressed"/> for
/// why the authored clips are a failure and the run-time ones are only a report.
///
/// <code>
/// Godot_v4.7.1-stable_mono_linux.x86_64 --headless --path Godot \
///     --script res://src/Titles/Fable2/tools/PubGameStageProbe.cs
/// </code>
/// </remarks>
public partial class PubGameStageProbe : SceneTree
{
	private int _checks;

	private int _failures;

	/// <summary>
	/// The clips each game reaches for off the root, and whether the stage is meant to hold them.
	/// </summary>
	/// <remarks>
	/// KEYED BY THE MOUNT'S SPELLING, which is what <see cref="SwfSceneUi.Load"/> takes — the
	/// game-data folder with its first character uppercased, and nothing else.
	///
	/// THE SPLIT IS THE WHOLE VALUE OF THE LIST. Every one of these names is addressed off the
	/// root by the ported code, but only the first list is AUTHORED into the movie; the second is
	/// created at run time and is null until the game has been initialised, which this probe never
	/// does. Checking them together would mean the probe could only ever say "most of them", and a
	/// clip that quietly stopped being exported would hide in that margin. Split, an authored name
	/// going missing is a failure and nothing else has to move.
	///
	/// Each run-time name was traced to the call that makes it:
	/// Keystone's <c>arrows</c>/<c>chips</c>/<c>shadows</c>/<c>tutorialHilights</c> to
	/// <c>GameBoard</c>; Mage Dice's <c>Shadow0A</c>–<c>C</c> to <c>InitChips</c> attaching
	/// <c>chipStackShadow</c> over the authored <c>Stack0A</c>–<c>C</c>; Wizard's Tower's
	/// <c>HandsClip</c>, <c>Markers</c>, <c>TextAnimation</c> and <c>TotalBoxes</c> to
	/// <c>WizardsTowerLoop</c>.
	///
	/// <c>fadeAll</c> IS THE ODD ONE OUT and belongs where it is. Mage Dice's <c>ShowBoard</c>
	/// reads it and the movie does not export it — but neither did the original: AS2's
	/// <c>MageDice::ShowBoard</c> reads the same <c>this.fadeAll</c> and gets <c>undefined</c>, so
	/// the board fade was already inert in the shipped game. The port returning null there is
	/// faithful, not broken.
	/// </remarks>
	private static readonly Dictionary<string, (string[] Authored, string[] Attached)> Addressed =
		new()
	{
		["Keystone"] = (
			new[]
			{
				"betStats", "fade", "hilights", "notice", "numbersAbove", "overs",
				"player0chips1", "player0chips2", "playerNames", "safeArea", "spaceStats",
				"stones", "tableText", "ticks", "tourneyStats",
			},
			new[] { "arrows", "chips", "shadows", "tutorialHilights" }),
		["Magedice"] = (
			new[]
			{
				"betStats", "board", "bonusMultiplier", "callbackText", "debugMenu", "debugText",
				"freeSpins", "player0name", "playerChips", "Stack0A", "Stack0B", "Stack0C",
				"tourneyStats", "winAmount",
			},
			new[] { "fadeAll", "Shadow0A", "Shadow0B", "Shadow0C" }),
		["Wizardstower"] = (
			new[]
			{
				"AButtonText", "BButtonText", "BetInfo", "BlackMask", "Candle", "foregroundclip",
				"Land", "Launch", "LSbutton", "LSButtonText", "LTbutton", "Name0",
				"OfferAnimating", "OfferInfo", "RTbutton", "SafeArea", "Status0", "Status1",
				"Ybutton", "YButtonText",
			},
			new[] { "HandsClip", "Markers", "TextAnimation", "TotalBoxes" }),
	};

	public override void _Initialize()
	{
		string gameRoot = Engine_GameData.ResolveWhere(
			candidate => PubGamesCatalog.ResolveRoot(candidate).Length != 0);
		if (gameRoot.Length == 0)
		{
			GD.Print("[stage] PUB GAMES NOT FOUND — nothing to build.");
			Quit(1);
			return;
		}

		string pubGames = PubGamesCatalog.ResolveRoot(gameRoot);
		GD.Print($"[stage] pub games at {pubGames}");

		// EXACTLY WHAT A HOST DOES. Mounting publishes the games' bitmaps, shapes and fonts under
		// game:// and indexes them; the loaders are what resolve the baked scene's references back
		// to them. A probe that shortcut either would be testing a path nothing runs.
		Refii.Bnk.BnkFileSystem.Mount(pubGames);
		Engine_SwfAssets.EnsureLoaders();

		foreach ((string game, (string[] authored, string[] attached)) in Addressed)
		{
			Stage(game, authored, attached);
		}

		Finish();
	}

	private void Stage(string game, string[] authored, string[] attached)
	{
		GD.Print($"\n{game}");

		var clock = Stopwatch.StartNew();
		SwfSceneUi? ui;
		try
		{
			ui = SwfSceneUi.Load(game);
		}
		catch (Exception exception)
		{
			Check($"{game}: the stage built", false, exception.Message);
			return;
		}

		double built = clock.Elapsed.TotalMilliseconds;
		if (ui is null)
		{
			// THE HANG CASE. Mage Dice and Wizard's Tower have no fallback, so this is the whole
			// session: the host reports Failed, the guest is answered, and the player never sees a
			// board. It is a failure here so it is never a failure in a pub.
			Check($"{game}: the stage built", false, "SwfSceneUi.Load returned null");
			return;
		}

		Check($"{game}: the stage built", true, $"{built:0} ms, {ui.ScenePath}");
		Check(
			$"{game}: it has a stage size",
			ui.StageSize.X > 1f && ui.StageSize.Y > 1f,
			$"{ui.StageSize.X:0}x{ui.StageSize.Y:0} at {ui.FrameRate:0.##} fps");

		int nodes = Count(ui.Node);
		Check($"{game}: it has a tree", nodes > 1, $"{nodes} node(s)");

		var missing = new List<string>();
		foreach (string name in authored)
		{
			if (ui.Root.GetChild(name) is null)
			{
				missing.Add(name);
			}
		}

		// EVERY AUTHORED NAME, because a stage that answers all but one of them is not a stage
		// with one thing missing — it is a movie whose exports moved, and the game that addresses
		// them is about to run with a board it cannot see and report nothing at all.
		Check(
			$"{game}: every authored clip is on the stage",
			missing.Count == 0,
			missing.Count == 0
				? $"all {authored.Length}"
				: $"absent: {string.Join(" ", missing)}");

		// REPORTED, NOT CHECKED. These are made by the game as it runs, so their absence before it
		// has run is the correct answer; they are printed because one of them turning UP would
		// mean the split above has drifted from the code.
		var early = new List<string>();
		foreach (string name in attached)
		{
			if (ui.Root.GetChild(name) is not null)
			{
				early.Add(name);
			}
		}

		GD.Print(
			$"         {attached.Length} clip(s) the game attaches at run time" +
			(early.Count == 0
				? " — none on the stage, as expected"
				: $" — but the stage already holds: {string.Join(" ", early)}"));

		ui.Node.QueueFree();
	}

	private static int Count(Node node)
	{
		var total = 1;
		foreach (Node child in node.GetChildren())
		{
			total += Count(child);
		}

		return total;
	}

	private void Check(string what, bool ok, string detail)
	{
		_checks++;
		if (!ok)
		{
			_failures++;
		}

		string suffix = detail.Length == 0 ? string.Empty : $"  [{detail}]";
		GD.Print($"  [{(ok ? "ok  " : "FAIL")}] {what}{suffix}");
	}

	private void Finish()
	{
		GD.Print(_failures == 0
			? $"\n[stage] PUB GAME STAGES OK — {_checks} check(s)"
			: $"\n[stage] PUB GAME STAGES FAILED — {_failures} of {_checks} check(s)");
		Quit(_failures == 0 ? 0 : 1);
	}
}
