namespace Refii.Tests.Ui;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// The presentation events a screen's behaviours fire (<see cref="Engine_UiBehaviourEvents"/>):
/// a behaviour fires on going from live to not live, the scripts name the events, and the
/// shipped front end and HUD carry the behaviours the guest registers callbacks for. No Godot
/// type is built.
/// </summary>
[TestSuite]
public class BehaviourEventsTests
{
	private static Engine_UiBehaviourEvents One(string name = "END_LEGAL", string parent = "") =>
		new(new[] { new Engine_UiBehaviourEvents.Behaviour(2, name, 0, parent) });

	[TestCase]
	public void ScriptsNameTheirEvents()
	{
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("BehaviorEndLegal.lua")).IsEqual("END_LEGAL");
		AssertThat(Engine_UiBehaviourEvents.EventOfScript(@"art\gui\gameface\behaviorcanpressa.lua")).IsEqual("CAN_PRESS_A");
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("art/gui/gameface/FinishedTransitioningToGame.lua"))
			.IsEqual("FINISHED_TRANSITIONING_TO_GAME");
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("BehaviorLoadCard.lua")).IsEqual("LOAD_CARD_ACTIVE");
	}

	/// <summary>The gender card's commit is the card choice's statement, and no shipped file carries the load-selected script.</summary>
	[TestCase]
	public void TheCommitsAreNotPosted()
	{
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("BehaviorBoySelected.lua")).IsNull();
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("BehaviorGirlSelected.lua")).IsNull();
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("BehaviorLoadSelected.lua")).IsNull();
		AssertThat(Engine_UiBehaviourEvents.EventOfScript("WidescreenAdapter.lua")).IsNull();
	}

	/// <summary>`sub_82189140` & 0x7FFFFFFF — the ids the guest interns at static init (TU1 0x8349B644 and the rest).</summary>
	[TestCase]
	public void EventIdsAreTheGuestHash()
	{
		var expected = new Dictionary<string, uint>
		{
			["END_LEGAL"] = 0x5470594Bu,
			["END_STARTED"] = 0x2ADF05A3u,
			["CAN_PRESS_A"] = 0x2D9FBA6Au,
			["DIALOG_ACTIVE"] = 0x36D47BC3u,
			["UNFREEZE"] = 0x7686FF10u,
			["OUTRO_FINISHED"] = 0x449FC0FAu,
			["BOY_ACTIVE"] = 0x7BD1EC47u,
			["GIRL_ACTIVE"] = 0x485ADE77u,
			["LOAD_CARD_ACTIVE"] = 0x53CFAC84u,
			["FINISHED_TRANSITIONING_TO_GAME"] = 0x68CC5729u,
		};
		foreach ((string name, uint id) in expected)
		{
			AssertThat(Engine_GameHash.Sdbm(name) & 0x7FFFFFFFu).IsEqual(id);
		}
	}

	[TestCase]
	public void FiresOnGoingFromLiveToNotLive()
	{
		Engine_UiBehaviourEvents events = One();
		AssertThat(events.Observe(new[] { true, true, true })).IsNull();
		AssertThat(events.Observe(new[] { true, true, true })).IsNull();
		List<Engine_UiBehaviourEvents.Fired>? fired = events.Observe(new[] { true, true, false });
		AssertThat(fired).IsNotNull();
		AssertThat(fired!.Count).IsEqual(1);
		AssertThat(fired[0].Event).IsEqual("END_LEGAL");
		AssertThat(fired[0].ParentName).IsEqual(string.Empty);
		AssertThat(fired[0].Ordinal).IsEqual(2);

		// Staying down is not a second deactivation.
		AssertThat(events.Observe(new[] { true, true, false })).IsNull();
	}

	/// <summary>An element that was never live never deactivates — the first stand finding it down is not an event.</summary>
	[TestCase]
	public void NeverLiveNeverFires()
	{
		Engine_UiBehaviourEvents events = One();
		AssertThat(events.Observe(new[] { true, true, false })).IsNull();
		AssertThat(events.Observe(new[] { true, true, false })).IsNull();
	}

	/// <summary>The presentation closing forgets what was live: no event for the close, and the next stand starts afresh.</summary>
	[TestCase]
	public void ResetIsNotADeactivation()
	{
		Engine_UiBehaviourEvents events = One();
		events.Observe(new[] { true, true, true });
		events.Reset();
		AssertThat(events.Observe(new[] { true, true, false })).IsNull();
		events.Observe(new[] { true, true, true });
		AssertThat(events.Observe(new[] { true, true, false })!.Count).IsEqual(1);
	}

	/// <summary>Each live period ends in one event: live again after the window reopens, then down again.</summary>
	[TestCase]
	public void EachLivePeriodFiresOnce()
	{
		Engine_UiBehaviourEvents events = One("UNFREEZE", "dialog");
		var rose = new List<Engine_UiBehaviourEvents.Behaviour>();
		int count = 0;
		foreach (bool live in new[] { true, false, false, true, true, false })
		{
			count += events.Observe(new[] { false, false, live }, rose)?.Count ?? 0;
		}

		AssertThat(count).IsEqual(2);
		AssertThat(rose.Count).IsEqual(2);
		AssertThat(rose[0].ParentName).IsEqual("dialog");
	}

	/// <summary>The bank holding the presentations, beside the tree's packaged install; null without one.</summary>
	private static string? ShippedBank()
	{
		string? env = Environment.GetEnvironmentVariable(Engine_GameData.EnvironmentVariable);
		if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "data", "guiscripts.bnk")))
		{
			return Path.Combine(env, "data", "guiscripts.bnk");
		}

		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			string direct = Path.Combine(at, "assets", "Fable2", "game", "data", "guiscripts.bnk");
			if (File.Exists(direct))
			{
				return direct;
			}
		}

		return null;
	}

	private static Engine_UiScreen? Shipped(string file)
	{
		string? bank = ShippedBank();
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			Console.WriteLine($"[behaviours] no packaged install beside the tree; {file} is not read here");
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (entry.RelativePath.EndsWith(file + ".bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse(file, archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	private static string Found(Engine_UiScreen screen) => string.Join(" ",
		Engine_UiBehaviourEvents.For(screen)!.Behaviours.Select(b => $"{b.ParentName}:{b.Event}")
			.OrderBy(s => s, StringComparer.Ordinal));

	/// <summary>
	/// The registrations' emitters, as the title's `sub_826E2C30` and the main menu's
	/// `sub_826DE740` register them: root END_LEGAL / END_STARTED / CAN_PRESS_A and `dialog`
	/// DIALOG_ACTIVE / UNFREEZE on the title; root OUTRO_FINISHED, `choosecard` BOY / GIRL_ACTIVE
	/// (two each) and UNFREEZE, `loadgame` LOAD_CARD_ACTIVE and `dialog` DIALOG_ACTIVE / UNFREEZE
	/// on the main menu; the HUD root's FINISHED_TRANSITIONING_TO_GAME.
	/// </summary>
	[TestCase]
	public void TheShippedScreensCarryTheRegisteredEmitters()
	{
		if (Shipped("frontendstartupscreen") is { } title)
		{
			AssertThat(Found(title)).IsEqual(
				":CAN_PRESS_A :END_LEGAL :END_STARTED dialog:DIALOG_ACTIVE dialog:UNFREEZE");
		}

		if (Shipped("frontendmainmenu") is { } menu)
		{
			AssertThat(Found(menu)).IsEqual(
				":OUTRO_FINISHED choosecard:BOY_ACTIVE choosecard:BOY_ACTIVE choosecard:GIRL_ACTIVE " +
				"choosecard:GIRL_ACTIVE choosecard:UNFREEZE dialog:DIALOG_ACTIVE dialog:UNFREEZE " +
				"loadgame:LOAD_CARD_ACTIVE");
		}

		if (Shipped("hud") is { } hud)
		{
			AssertThat(Found(hud)).IsEqual(":FINISHED_TRANSITIONING_TO_GAME");
		}
	}

	/// <summary>
	/// The HUD's clock, windows only: on the root's `PauseMenuToGame` (slide 1) the behaviour's
	/// 0..482 window is open at the entry and shut by 500 ms.
	/// </summary>
	[TestCase]
	public void TheHudWindowShutsAfterItsEnd()
	{
		if (Shipped("hud") is not { } hud)
		{
			return;
		}

		int ordinal = Engine_UiBehaviourEvents.For(hud)!.Behaviours[0].Ordinal;
		var windows = new Engine_UiSlidePlayback(hud, new[] { ordinal });
		int[] slides = new int[hud.Nodes.Length];
		slides[0] = 1;
		windows.Step(slides, null, 1000);
		AssertThat(windows.Closed[ordinal]).IsFalse();
		windows.Step(slides, null, 1400);
		AssertThat(windows.Closed[ordinal]).IsFalse();
		windows.Step(slides, null, 1500);
		AssertThat(windows.Closed[ordinal]).IsTrue();
	}
}
