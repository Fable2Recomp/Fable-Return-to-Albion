namespace Refii.Tests.Titles;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Godot;
using Refii.Bnk;
using Refii.Titles.Fable3;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// Fable 3's last UI rules: the markers' guest near plane and push (sub_82B51B58 over
/// CEnginePrimitiveCamera +0x30), their authored fades (hud.bgf tracks, sub_82C3B078's curve), the
/// stacked boxes' meters one fit late (sub_82921110 → sub_8291F8C0 → sub_8221D780), and the colour
/// runs that replace their element's colour and opacity (sub_821F6180). Value types only; no Godot
/// object is built.
/// </summary>
[TestSuite]
public class Fable3FinalUiTests
{
	/// <summary>Behind is depth below the 0.1 near plane, unordered included (the projection's `bge`, 0x82287A98).</summary>
	[TestCase]
	public void BehindIsBelowTheGuestNearPlane()
	{
		AssertThat(Fable3MarkerPlacement.Behind(0.05f)).IsTrue();
		AssertThat(Fable3MarkerPlacement.Behind(-3f)).IsTrue();
		AssertThat(Fable3MarkerPlacement.Behind(float.NaN)).IsTrue();
		AssertThat(Fable3MarkerPlacement.Behind(0.1f)).IsFalse();
		AssertThat(Fable3MarkerPlacement.Behind(4f)).IsFalse();
	}

	/// <summary>The push is max(2 · 0.1, 2 · |depth|) (`fsel` 0x82B51DA0).</summary>
	[TestCase]
	public void ThePushFloorIsTwiceTheGuestNearPlane()
	{
		AssertThat(Fable3MarkerPlacement.Push(0f)).IsEqualApprox(0.2f, 1e-6f);
		AssertThat(Fable3MarkerPlacement.Push(-0.05f)).IsEqualApprox(0.2f, 1e-6f);
		AssertThat(Fable3MarkerPlacement.Push(-3f)).IsEqualApprox(6f, 1e-6f);
	}

	/// <summary>The shipped Fade In / Fade Out keys (hud.bgf tracks 44 / 45): an eased value over a linear time.</summary>
	private static Fable3MarkerPlacement.Fades ShippedFades() => new(
		new BgfAnimation { Keys = new[] { new BgfAnimationKey(0f, 0f, 0f, 0.32728195f, 0f, 66.333336f), new BgfAnimationKey(1000f, 100f, 0f, -1f, 0f, -1f) } },
		new BgfAnimation { Keys = new[] { new BgfAnimationKey(0f, 100f, 0f, 100f, 0f, 33.66666f), new BgfAnimationKey(1000f, 0f, 0f, -1f, 0f, -1f) } });

	[TestCase]
	public void TheMarkerFadesFollowTheAuthoredCurve()
	{
		Fable3MarkerPlacement.Fades fades = ShippedFades();
		AssertThat(Fable3MarkerPlacement.Alpha(fades, false, 0f)).IsEqualApprox(0f, 1e-5f);
		AssertThat(Fable3MarkerPlacement.Alpha(fades, false, 0.5f)).IsEqualApprox(0.37498f, 1e-4f);
		AssertThat(Fable3MarkerPlacement.Alpha(fades, false, 1f)).IsEqualApprox(1f, 1e-5f);
		AssertThat(Fable3MarkerPlacement.Alpha(fades, true, 0.5f)).IsEqualApprox(0.62625f, 1e-4f);
		AssertThat(Fable3MarkerPlacement.Alpha(fades, true, 1f)).IsEqualApprox(0f, 1e-5f);
		// Before hud.bgf is read the fade is linear.
		AssertThat(Fable3MarkerPlacement.Alpha(null, false, 0.25f)).IsEqualApprox(0.25f, 1e-6f);
		AssertThat(Fable3MarkerPlacement.Alpha(null, true, 0.25f)).IsEqualApprox(0.75f, 1e-6f);
	}

	[TestCase]
	public void TheShippedMarkersCarryTheirFades()
	{
		if (Hud() is not { } hud)
		{
			Console.WriteLine("[fable3-final] no Fable 3 bank beside the tree; the shipped marker fades are not checked here");
			return;
		}

		foreach (string marker in new[] { "HeroOffScreenWorldIcon", "HenchmanOffScreenWorldIcon" })
		{
			Fable3MarkerPlacement.Fades? fades = Fable3MarkerPlacement.FadesOf(hud, marker);
			AssertThat(fades is not null).IsTrue();
			AssertThat(fades!.Value.In.Evaluate(500f)).IsEqualApprox(37.498f, 0.01f);
			AssertThat(fades.Value.Out.Evaluate(500f)).IsEqualApprox(62.625f, 0.01f);
		}
	}

	/// <summary>A fit places by the meter the fit before stated; a refit by the latest.</summary>
	[TestCase]
	public void TheFitSeesItsMeterOneFitLate()
	{
		var lag = new Fable3TextBoxes.MeterLag();
		lag.Laid(10f);
		AssertThat(lag.AtFit is null).IsTrue();
		lag.Laid(50f);
		AssertThat(lag.AtFit!.Value).IsEqual(10f);
		lag.Refit();
		AssertThat(lag.AtFit!.Value).IsEqual(50f);
		lag.Laid(null);
		AssertThat(lag.AtFit!.Value).IsEqual(50f);
	}

	[TestCase]
	public void TheMeterPercentIsTheFittedSizeInItsRange()
	{
		var range = new Engine_UiTextBoxStack.Range(30f, 198f);
		AssertThat(Fable3TextBoxes.MeterPercent(range, 30f)!.Value).IsEqual(0f);
		AssertThat(Fable3TextBoxes.MeterPercent(range, 198f)!.Value).IsEqualApprox(100f, 1e-4f);
		AssertThat(Fable3TextBoxes.MeterPercent(range, 114f)!.Value).IsEqualApprox(50f, 1e-4f);
		AssertThat(Fable3TextBoxes.MeterPercent(range, 500f)!.Value).IsEqual(100f);
		AssertThat(Fable3TextBoxes.MeterPercent(new Engine_UiTextBoxStack.Range(5f, 5f), 5f) is null).IsTrue();
		AssertThat(Fable3TextBoxes.MetersOf(Fable3TextBoxes.Corner)).IsEqual(("CornerX", "CornerY"));
		AssertThat(Fable3TextBoxes.MetersOf(Fable3TextBoxes.Subtitles)).IsEqual(("SubtitlesX", "SubtitlesY"));
	}

	/// <summary>CornerX's Slide1 moves `BoxMiddle` [425] x from −152 to −153; the time is cut to a whole tick.</summary>
	[TestCase]
	public void TheCornerLinesStandOnTheCornerXTrack()
	{
		if (Hud() is not { } hud)
		{
			Console.WriteLine("[fable3-final] no Fable 3 bank beside the tree; the CornerX track is not checked here");
			return;
		}

		IReadOnlyList<int> meters = Engine_UiBindings.Read(hud.Document).Elements(Engine_UiBindings.Kind.Meter, "CornerX");
		AssertThat(meters.Count > 0).IsTrue();
		Dictionary<int, (BgfAnimation? X, BgfAnimation? Y)> tracks = Fable3TextBoxes.PositionTracks(hud.Document, meters[0]);
		int boxMiddle = -1;
		foreach (UiNode node in hud.Nodes)
		{
			if (node.Name == "BoxMiddle" && node.Parent?.Ordinal == meters[0])
			{
				boxMiddle = node.Ordinal;
			}
		}

		AssertThat(tracks.ContainsKey(boxMiddle)).IsTrue();
		AssertThat(Fable3TextBoxes.At(tracks[boxMiddle].X, 0f)!.Value).IsEqualApprox(-152f, 1e-4f);
		AssertThat(Fable3TextBoxes.At(tracks[boxMiddle].X, 100f)!.Value).IsEqualApprox(-153f, 1e-4f);
		AssertThat(Fable3TextBoxes.At(null, 50f) is null).IsTrue();
	}

	/// <summary>Fable 3's colour runs replace (s5 keeps half the element's alpha); Fable 2's multiply.</summary>
	[TestCase]
	public void FableThreesColourRunsReplaceFableTwosMultiply()
	{
		Engine_UiTextContract three = Engine_UiTextContract.Fable3;
		AssertThat(three.RunReplaces(new Color(0.78f, 0.26f, 0.30f, 1f))).IsEqual((true, true));
		AssertThat(three.RunReplaces(new Color(0.5f, 0.5f, 0.5f, 0.5f))).IsEqual((true, false));
		AssertThat(three.RunReplaces(null)).IsEqual((false, false));
		AssertThat(three.AlphaFloor).IsEqual(0.09765625f);

		Engine_UiTextContract two = Engine_UiTextContract.Fable2;
		AssertThat(two.ColourRunsReplace).IsFalse();
		AssertThat(two.RunReplaces(new Color(1f, 0f, 0f, 1f))).IsEqual((false, false));
		AssertThat(two.AlphaFloor < 0f).IsTrue();
	}

	private static Engine_UiScreen? Hud()
	{
		string data = Path.Combine("assets", "Fable3", "game", "data", "guiscripts.bnk");
		string? bank = null;
		for (string? at = AppContext.BaseDirectory; at is not null && bank is null; at = Path.GetDirectoryName(at))
		{
			if (File.Exists(Path.Combine(at, data)))
			{
				bank = Path.Combine(at, data);
				break;
			}

			string packages = Path.Combine(at, "out", "package");
			if (Directory.Exists(packages))
			{
				foreach (string package in Directory.EnumerateDirectories(packages))
				{
					if (File.Exists(Path.Combine(package, data)))
					{
						bank = Path.Combine(package, data);
						break;
					}
				}
			}
		}

		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (string.Equals(Path.GetFileName(entry.RelativePath.Replace('\\', '/')), "hud.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("hud", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}
}
