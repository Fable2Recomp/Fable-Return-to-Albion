using System.Collections.Generic;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Every zone the Tracy capture holds, one row per source location: how often it ran, its total,
/// mean, median, 95th percentile and worst time, and its self time (what it spent outside the
/// zones under it). Every number is exact: Tracy keeps each zone, and the percentiles are taken
/// over all of them. Sorted by total; click a column to sort by it, a row for where it is.
/// </summary>
public partial class GameDevTabTracyZones : GameDevTab
{
	public override string Title => "Tracy zones";

	public override double RefreshSeconds => 1.0;

	private readonly GameDevTable _table = new(new[]
	{
		"zone", "count", "total ms", "mean ms", "median ms", "p95 ms", "max ms", "self ms", "% of capture",
	}, sortColumn: 2);

	private readonly Godot.Label _summary = Readout();
	private readonly Godot.Label _where = Readout();
	private readonly Dictionary<string, string> _locations = new();
	private ulong _shownZones = ulong.MaxValue;

	public GameDevTabTracyZones()
	{
		AddChild(_summary);
		AddChild(_table);
		AddChild(_where);
		_table.Picked += row => _where.Text = _locations.TryGetValue(row[0], out string? where) ? where : string.Empty;
	}

	public override void Refresh()
	{
		HostTracy.Status status = HostTracy.GetStatus();
		if (status.Capture == HostTracy.CaptureState.None)
		{
			_summary.Text = "no capture — start one on the Tracy tab";
			_table.Show(new List<string[]>());
			_shownZones = ulong.MaxValue;
			return;
		}

		// A stopped capture does not change; its table is computed once.
		if (status.Capture == HostTracy.CaptureState.Stopped && status.Zones == _shownZones)
		{
			return;
		}

		_shownZones = status.Zones;
		double span = System.Math.Max(1, status.LastNs - status.FirstNs);
		var rows = new List<string[]>();
		_locations.Clear();
		foreach (HostTracy.ZoneStat stat in HostTracy.ZoneStats())
		{
			HostTracy.SourceLocation location = HostTracy.Location(stat.SrcLoc);
			string name = _locations.ContainsKey(location.Name) ? $"{location.Name} ({stat.SrcLoc})" : location.Name;
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

		_summary.Text = $"{rows.Count} zones over {GameDevTracyTimeline.FormatNs((long)span)}" +
			(status.Capture == HostTracy.CaptureState.Stopped ? string.Empty : "   ·   live, updating each second");
		_table.Show(rows);
	}

	private static string Ms(double ns) => $"{ns / 1e6:0.000}";
}
