namespace Refii.Tests.Ui;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// The slide clock (<see cref="Engine_UiSlidePlayback"/>): the time block's per-tick answer
/// (`sub_82284DF8`) and one shipped screen played through. No Godot type is built.
/// </summary>
[TestSuite]
public class SlidePlaybackTests
{
	[TestCase]
	public void ZeroPassesLoops()
	{
		AssertThat(Engine_UiSlidePlayback.TimeAt(1250, 1000, 0, false, out bool done)).IsEqual(250);
		AssertThat(done).IsFalse();
	}

	[TestCase]
	public void OnePassHoldsAtTheEndAndCompletes()
	{
		AssertThat(Engine_UiSlidePlayback.TimeAt(400, 500, 1, false, out bool running)).IsEqual(400);
		AssertThat(running).IsFalse();
		AssertThat(Engine_UiSlidePlayback.TimeAt(9000, 500, 1, false, out bool done)).IsEqual(500);
		AssertThat(done).IsTrue();
	}

	[TestCase]
	public void PingPongHoldsAtTheEndOnAnOddCountAndNeverCompletes()
	{
		AssertThat(Engine_UiSlidePlayback.TimeAt(9000, 600, 1, true, out bool done)).IsEqual(600);
		AssertThat(done).IsFalse();
		AssertThat(Engine_UiSlidePlayback.TimeAt(9000, 600, 2, true, out _)).IsEqual(0);
		AssertThat(Engine_UiSlidePlayback.TimeAt(700, 600, 0, true, out _)).IsEqual(500);
	}

	[TestCase]
	public void TimeBeforeEntryIsZero()
	{
		AssertThat(Engine_UiSlidePlayback.TimeAt(-30, 500, 1, false, out _)).IsEqual(0);
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

	private static Engine_UiScreen? Pause()
	{
		string? bank = ShippedBank();
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (entry.RelativePath.EndsWith("pausemainmenu.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("pausemainmenu", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	/// <summary>
	/// The guest's own statement drives the root: `sub_82426CA8(root, 3)` is a told entry of
	/// `Static` then a told play. Entered alone, `Intro` (authored paused) waits at t = 0; the
	/// told `Static` entry and play run it; a statement older than the component's coming live is
	/// the walk's to supersede and is ignored.
	/// </summary>
	[TestCase]
	public void ToldEntriesAndPlaysDriveTheRoot()
	{
		if (Pause() is not { } screen)
		{
			Console.WriteLine("[playback] no packaged install beside the tree; the told pause play is not run here");
			return;
		}

		int[] slides = Engine_UiRestState.Slides(screen);
		int x = (15 * Engine_UiSlidePlayback.Channels) + (int)Engine_UiSlidePlayback.Channel.PositionX;
		Engine_UiSlideStatements.Told? told = null;
		var play = new Engine_UiSlidePlayback(screen);

		// A stale `Static` told before the root came live: ignored, the root enters `Intro`.
		play.Reset(10);
		told = new Engine_UiSlideStatements.Told(3, 5, false, 6);
		int[] shown = play.Step(slides, null, 0, _ => false, null, i => i == 0 ? told : null, 10);
		AssertThat(shown[0]).IsEqual(1);

		// Told `Static` then play, after it came live.
		told = new Engine_UiSlideStatements.Told(3, 11, false, 12);
		shown = play.Step(slides, null, 100, _ => false, null, i => i == 0 ? told : null, 12);
		AssertThat(shown[0]).IsEqual(3);
		AssertThat(play.Values[x]).IsEqual(405f);
		play.Step(slides, null, 250, _ => false, null, i => i == 0 ? told : null, 12);
		AssertThat(play.Values[x] > 0f && play.Values[x] < 405f).IsTrue();
		play.Step(slides, null, 5000, _ => false, null, i => i == 0 ? told : null, 12);
		AssertThat(play.Values[x]).IsEqual(0f);
	}

	/// <summary>
	/// The pause root plays `Static` (slide 3, `sub_826A23F8`): its `Pages` track runs 405 → 0 over the
	/// first 300 ms of the 600 ms slide and holds.
	/// </summary>
	[TestCase]
	public void PauseStaticSlidesItsPagesIn()
	{
		string? bank = ShippedBank();
		if (bank is null)
		{
			Console.WriteLine("[playback] no packaged install beside the tree; the shipped pause play is not run here");
			return;
		}

		AssertThat(BnkArchive.TryOpen(bank, out BnkArchive? archive, out _)).IsTrue();
		byte[]? bytes = null;
		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (entry.RelativePath.EndsWith("pausemainmenu.bgf", StringComparison.OrdinalIgnoreCase))
				{
					bytes = archive.ReadEntryBytes(entry);
				}
			}
		}

		AssertThat(bytes).IsNotNull();
		Engine_UiScreen screen = Engine_UiScreen.Parse("pausemainmenu", bytes!, null);
		int[] slides = Engine_UiRestState.Slides(screen);
		slides[0] = 3;
		var play = new Engine_UiSlidePlayback(screen);
		int x = (15 * Engine_UiSlidePlayback.Channels) + (int)Engine_UiSlidePlayback.Channel.PositionX;

		play.Step(slides, null, 1000, _ => true);
		AssertThat(play.Values[x]).IsEqual(405f);
		play.Step(slides, null, 1150, _ => true);
		AssertThat(play.Values[x] > 0f && play.Values[x] < 405f).IsTrue();
		play.Step(slides, null, 9000, _ => true);
		AssertThat(play.Values[x]).IsEqual(0f);
	}

	/// <summary>Entered without a play, the authored-paused `Static` waits at its start.</summary>
	[TestCase]
	public void AnUnplayedPausedSlideWaitsAtItsStart()
	{
		if (Pause() is not { } screen)
		{
			return;
		}

		int[] slides = Engine_UiRestState.Slides(screen);
		slides[0] = 3;
		var play = new Engine_UiSlidePlayback(screen);
		int x = (15 * Engine_UiSlidePlayback.Channels) + (int)Engine_UiSlidePlayback.Channel.PositionX;
		play.Step(slides, null, 1000);
		play.Step(slides, null, 9000);
		AssertThat(play.Values[x]).IsEqual(405f);
	}
}
