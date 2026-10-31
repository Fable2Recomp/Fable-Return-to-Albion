namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;

/// <summary>
/// The phone's hero walks replayed offline (<see cref="MotionReplay"/>): the timeline as it ran
/// reproduces the steps the phone drew, and `motion_arrival_fit` draws the same statements evenly.
/// The bundled walk is `herotrace-60c-off-53705` (Galaxy S10e, 2026-10-27, every key off, pace
/// 0.81). The traces were recorded with and without four experimental keys this build does not
/// carry; the timeline is replayed here by its one rule, the one the "off" walks ran. Set
/// REFII_HEROTRACES to a directory of `herotrace-60c-*.json` to score them all
/// (<see cref="ScoreTable"/>).
/// </summary>
[TestSuite]
public class MotionReplayTests
{
	private static string Bundled([CallerFilePath] string source = "") =>
		Path.Combine(Path.GetDirectoryName(source)!, "Data", "herotrace-60c-off-53705.json");

	/// <summary>
	/// A replay's drawn positions, run under the given rules; a timeline starts on the pace the
	/// phone reported, as the phone's had settled on it long before the trace.
	/// </summary>
	private static List<Vector3> Replay(HeroTrace trace, string method) => method switch
	{
		"recorded" => trace.Frames.Select(f => f.Recorded).ToList(),
		"stated" => MotionReplay.Run(trace, new MotionReplay.Stated()),
		"timeline" => MotionReplay.WithRules(false, () => MotionReplay.Run(trace, new MotionReplay.Timeline()), trace.Pace),
		"line n5 100ms" => MotionReplay.Run(trace, new MotionReplay.RawFit(5, 100.0)),
		"line n8 150ms" => MotionReplay.Run(trace, new MotionReplay.RawFit(8, 150.0)),
		"line+spring w14" => MotionReplay.Run(trace, new MotionReplay.RawFit(5, 100.0, omega: 14.0)),
		"arrival fit" => MotionReplay.WithRules(true, () => MotionReplay.Run(trace, new MotionReplay.Timeline())),
		_ => throw new ArgumentException(method),
	};

	private static readonly string[] Methods =
	[
		"recorded", "timeline", "stated", "line n5 100ms", "line n8 150ms",
		"line+spring w14", "arrival fit",
	];

	[TestCase]
	public void TheReplayedTimelineReproducesThePhonesSteps()
	{
		HeroTrace trace = HeroTrace.Load(Bundled());
		AssertThat(trace.Frames.Count).IsEqual(140);
		AssertThat(trace.Frames.Count(f => f.New)).IsGreater(50);

		List<Vector3> recorded = Replay(trace, "recorded");
		List<Vector3> replayed = Replay(trace, "timeline");
		MotionReplay.Evenness phone = MotionReplay.Score(recorded);
		MotionReplay.Evenness replay = MotionReplay.Score(replayed);

		// Frame by frame, after the first second: the replayed pace settles from the reported one.
		var errors = Enumerable.Range(20, trace.Frames.Count - 20).Select(i => recorded[i].DistanceTo(replayed[i]) * 100.0).ToList();
		Console.WriteLine($"[motion-replay] {trace.Name}: phone step sd {phone.StepSd:F0}% change {phone.MeanChange:F0}% " +
			$"bad {phone.StallsLurches}; replay {replay.StepSd:F0}% {replay.MeanChange:F0}% {replay.StallsLurches}; " +
			$"position error mean {errors.Average():F1} cm p95 {errors.OrderBy(e => e).ElementAt((int)(0.95 * errors.Count)):F1} cm");

		// The stop-go reproduced: the same scale of unevenness, and the drawn path within a few cm.
		AssertThat(Math.Abs(replay.StepSd - phone.StepSd)).IsLess(10.0);
		AssertThat(Math.Abs(replay.StallsLurches - phone.StallsLurches)).IsLessEqual(6);
		AssertThat(errors.Average()).IsLess(3.0);
	}

	[TestCase]
	public void TheArrivalFitWalksThePhonesStatementsEvenly()
	{
		HeroTrace trace = HeroTrace.Load(Bundled());
		MotionReplay.Evenness timeline = MotionReplay.Score(Replay(trace, "timeline"));
		List<Vector3> drawn = Replay(trace, "arrival fit");
		MotionReplay.Evenness fit = MotionReplay.Score(drawn);
		MotionReplay.Lag lag = MotionReplay.LagOf(trace, drawn);

		AssertThat(fit.StepSd).IsLess(0.5 * timeline.StepSd);
		AssertThat(fit.StallsLurches).IsLessEqual(3);
		AssertThat(lag.P95Ms).IsLess(180.0);
		AssertThat(lag.OffP95Cm).IsLess(12.0);
	}

	/// <summary>
	/// The table: every method over every trace in REFII_HEROTRACES (or the bundled one), means
	/// across traces. Printed; asserts only that it ran.
	/// </summary>
	[TestCase]
	public void ScoreTable()
	{
		string? directory = System.Environment.GetEnvironmentVariable("REFII_HEROTRACES");
		List<string> files = directory is { Length: > 0 } && Directory.Exists(directory)
			? Directory.GetFiles(directory, "herotrace-60c-*.json").OrderBy(f => f).ToList()
			: [Bundled()];
		var traces = files.Select(HeroTrace.Load).ToList();
		var text = new StringBuilder();
		text.AppendLine($"[motion-replay] {traces.Count} traces: method | step/median sd | mean change | stalls+lurches | " +
			"lag ms mean p95 | off path cm mean p95 | past newest % | body-legs ms mean sd frame");
		foreach (string method in Methods)
		{
			var rows = traces.Select(trace =>
			{
				List<Vector3> drawn = Replay(trace, method);
				return (Score: MotionReplay.Score(drawn), Lag: MotionReplay.LagOf(trace, drawn),
					Legs: MotionReplay.WithRules(false, () => MotionReplay.BodyAgainstLegs(trace, drawn)));
			}).ToList();
			text.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"  {0,-16} {1,5:F1}% {2,5:F1}% {3,5:F1} | {4,4:F0} {5,4:F0} | {6,4:F1} {7,5:F1} | {8,4:F0} | {9,5:F0} {10,4:F0} {11,4:F1}",
				method, rows.Average(r => r.Score.StepSd), rows.Average(r => r.Score.MeanChange),
				rows.Average(r => r.Score.StallsLurches), rows.Average(r => r.Lag.MeanMs), rows.Average(r => r.Lag.P95Ms),
				rows.Average(r => r.Lag.OffMeanCm), rows.Average(r => r.Lag.OffP95Cm), rows.Average(r => r.Lag.PastNewest),
				rows.Average(r => r.Legs.Mean), rows.Average(r => r.Legs.Sd), rows.Average(r => r.Legs.FrameChange)));
		}

		// The answer to a change of walk, on the synthetic jog (MotionReplay.Respond).
		text.AppendLine("[motion-replay] synthetic: method | reversal overshoot cm, late ms | 90° corner cut cm, heading ms | " +
			"half speed ms | stop overrun cm, settle ms, exact");
		foreach ((string name, Func<MotionReplay.Response> respond) in new (string, Func<MotionReplay.Response>)[]
		{
			("timeline", () => MotionReplay.WithRules(false, () => MotionReplay.Respond(() => new MotionReplay.Timeline()))),
			("line n5 100ms", () => MotionReplay.Respond(() => new MotionReplay.RawFit(5, 100.0))),
			("line+spring w14", () => MotionReplay.Respond(() => new MotionReplay.RawFit(5, 100.0, omega: 14.0))),
			("arrival fit", () => MotionReplay.WithRules(true, () => MotionReplay.Respond(() => new MotionReplay.Timeline()))),
		})
		{
			MotionReplay.Response r = respond();
			text.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,
				"  {0,-16} {1,5:F1} {2,4:F0} | {3,5:F1} {4,4:F0} | {5,4:F0} | {6,5:F1} {7,5:F0} {8}",
				name, r.ReverseOvershootCm, r.ReverseLateMs, r.CornerCutCm, r.HeadingMs, r.HalfSpeedMs, r.StopOverrunCm,
				r.SettleMs, r.StopsExactly));
		}

		Console.WriteLine(text.ToString());
		string? report = System.Environment.GetEnvironmentVariable("REFII_MOTION_REPORT");
		if (report is { Length: > 0 })
		{
			File.WriteAllText(report, text.ToString());
		}

		AssertThat(traces.Count).IsGreater(0);
	}
}
