using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Every zone the Tracy capture holds, one row per source location: how often it ran, its total,
/// mean, median, 95th percentile and worst time, and its self time (what it spent outside the
/// zones under it), over the whole capture or the timeline's view, on every thread or one.
/// Every number is exact: Tracy keeps each zone, and the percentiles are taken over all of them.
/// </summary>
/// <remarks>
/// FIND ZONE: pick a row for that zone's every instance in the same scope — a histogram of their
/// durations, and the slowest; pick one of those to open it in the timeline.
/// </remarks>
public partial class GameDevTabTracyZones : GameDevTab
{
	public override string Title => "Tracy zones";

	public override double RefreshSeconds => 1.0;

	private const int Buckets = 64;
	private const int Slowest = 40;

	private readonly GameDevTracyScope _scope = new();
	private readonly GameDevTable _table = new(new[]
	{
		"zone", "count", "total ms", "mean ms", "median ms", "p95 ms", "max ms", "self ms", "% of range",
	}, sortColumn: 2);

	private readonly Label _summary = Readout();
	private readonly Label _found = Readout();
	private readonly GameDevTracyHistogram _histogram = new();
	private readonly GameDevTable _slowest = new(new[] { "instance", "duration ms", "at", "thread" }, sortColumn: -1);
	private readonly Dictionary<string, short> _srclocs = new();
	private readonly Dictionary<string, string> _locations = new();
	private readonly List<HostTracy.Instance> _slowestShown = new();
	private ulong _shownZones = ulong.MaxValue;
	private short _picked = short.MinValue;
	private string _pickedName = string.Empty;
	private bool _stale = true;

	public GameDevTabTracyZones()
	{
		AddChild(_scope);
		AddChild(_summary);
		var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		split.AddChild(_table);
		var found = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		found.AddChild(_found);
		var detail = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		detail.AddChild(_histogram);
		_slowest.CustomMinimumSize = new Vector2(360, 150);
		detail.AddChild(_slowest);
		found.AddChild(detail);
		split.AddChild(found);
		AddChild(split);

		_scope.Changed += () =>
		{
			_stale = true;
			Refresh();
		};
		_table.Picked += row =>
		{
			if (_srclocs.TryGetValue(row[0], out short srcloc))
			{
				_picked = srcloc;
				_pickedName = row[0];
				FindPicked();
			}
		};
		_slowest.Picked += row =>
		{
			if (int.TryParse(row[0], out int index) && index >= 1 && index <= _slowestShown.Count)
			{
				HostTracy.Instance instance = _slowestShown[index - 1];
				GameDevTabTracy.Reveal(instance.Start, instance.End);
			}
		};
	}

	public override void Refresh()
	{
		HostTracy.Status status = HostTracy.GetStatus();
		if (status.Capture == HostTracy.CaptureState.None)
		{
			_summary.Text = "no capture — start one on the Tracy tab";
			_table.Show(new List<string[]>());
			_histogram.Show(null);
			_slowest.Show(new List<string[]>());
			_found.Text = string.Empty;
			_shownZones = ulong.MaxValue;
			_picked = short.MinValue;
			return;
		}

		// A stopped capture does not change; its table is computed once per scope.
		if (status.Capture == HostTracy.CaptureState.Stopped && status.Zones == _shownZones && !_stale)
		{
			return;
		}

		if (_shownZones == ulong.MaxValue || status.Capture != HostTracy.CaptureState.Stopped)
		{
			_scope.ReloadThreads();
		}

		_shownZones = status.Zones;
		_stale = false;
		(long t0, long t1) = _scope.Range;
		List<HostTracy.ZoneStat> stats = _scope.Whole ? HostTracy.ZoneStats() : HostTracy.ZoneStats(_scope.Thread, t0, t1);
		long first = System.Math.Max(t0, status.FirstNs);
		long last = System.Math.Min(t1, status.LastNs);
		double span = System.Math.Max(1, last - first);

		var rows = new List<string[]>();
		_srclocs.Clear();
		_locations.Clear();
		foreach (HostTracy.ZoneStat stat in stats)
		{
			HostTracy.SourceLocation location = HostTracy.Location(stat.SrcLoc);
			string name = _srclocs.ContainsKey(location.Name) ? $"{location.Name} ({stat.SrcLoc})" : location.Name;
			_srclocs[name] = stat.SrcLoc;
			_locations[name] = location.File.Length != 0
				? $"{location.Function}   —   {location.File}:{location.Line}"
				: location.Function;
			rows.Add(new[]
			{
				name,
				stat.Count.ToString(),
				Ms(stat.Total),
				Ms(stat.Total / (double)System.Math.Max(1UL, stat.Count)),
				Ms(stat.Median),
				Ms(stat.P95),
				Ms(stat.Max),
				Ms(stat.SelfTotal),
				$"{stat.Total / span * 100.0:0.0}",
			});
		}

		_summary.Text = $"{rows.Count} zones over {GameDevTracyTimeline.FormatNs((long)span)}, {_scope.Describe()}" +
			(status.Capture == HostTracy.CaptureState.Stopped ? string.Empty : "   ·   live, updating each second") +
			"   ·   pick a zone to find its instances";
		_table.Show(rows);
		if (_picked != short.MinValue)
		{
			FindPicked();
		}
	}

	private void FindPicked()
	{
		(long t0, long t1) = _scope.Range;
		HostTracy.FindZoneResult result = HostTracy.FindZone(_picked, _scope.Thread, t0, t1, Buckets, logScale: true, Slowest);
		_histogram.Show(result);
		string where = _locations.TryGetValue(_pickedName, out string? location) ? location : string.Empty;
		_found.Text = result.Count == 0
			? $"{_pickedName}: no instances in this range"
			: $"{_pickedName}: {result.Count:N0} instances   ·   total {GameDevTracyTimeline.FormatNs(result.Total)}   ·   " +
				$"median {GameDevTracyTimeline.FormatNs(result.Median)}   ·   p95 {GameDevTracyTimeline.FormatNs(result.P95)}   ·   " +
				$"max {GameDevTracyTimeline.FormatNs(result.Max)}\n{where}";

		HostTracy.Status status = HostTracy.GetStatus();
		var names = new Dictionary<ulong, string>();
		foreach (HostTracy.ThreadInfo thread in HostTracy.Threads())
		{
			names[thread.Id] = thread.Name;
		}

		_slowestShown.Clear();
		_slowestShown.AddRange(result.Slowest);
		var rows = new List<string[]>();
		for (int i = 0; i < result.Slowest.Count; ++i)
		{
			HostTracy.Instance instance = result.Slowest[i];
			rows.Add(new[]
			{
				(i + 1).ToString(),
				Ms(instance.End - instance.Start),
				GameDevTracyTimeline.FormatNs(instance.Start - status.FirstNs),
				names.TryGetValue(instance.Thread, out string? name) ? name : instance.Thread.ToString(),
			});
		}

		_slowest.Show(rows);
	}

	private static string Ms(double ns) => $"{ns / 1e6:0.000}";
}
