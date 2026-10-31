using System.Collections.Generic;
using Godot;

namespace Refii.Game;

/// <summary>
/// Godot's own monitors — the editor debugger's Monitors panel, in the running game: every
/// <see cref="Performance.Monitor"/> and every custom monitor, in readable units. Click a row to
/// graph it.
/// </summary>
public partial class GameDevTabMonitors : GameDevTab
{
	public override string Title => "Monitors";

	private readonly GameDevTable _table = new(new[] { "monitor", "value" });
	private readonly GameDevGraph _graph = new() { Budgets = System.Array.Empty<(float, string)>(), Unit = "" };
	private readonly Label _graphed = new() { Text = "click a monitor to graph it" };

	private Performance.Monitor? _monitor;
	private string? _custom;

	private static readonly Performance.Monitor[] All =
		System.Enum.GetValues<Performance.Monitor>();

	public GameDevTabMonitors()
	{
		_graph.Lines.Add(new GameDevGraph.Series { Name = "value", Colour = new Color(0.55f, 0.85f, 1f) });
		AddChild(_graphed);
		AddChild(_graph);
		AddChild(_table);

		_table.Picked += row =>
		{
			_monitor = null;
			_custom = null;
			foreach (Performance.Monitor monitor in All)
			{
				if (Label(monitor) == row[0])
				{
					_monitor = monitor;
				}
			}

			if (_monitor is null)
			{
				_custom = row[0];
			}

			_graphed.Text = $"graphing {row[0]}";
		};
	}

	public override void Tick(double delta)
	{
		if (_monitor is { } monitor)
		{
			_graph.Push((float)ScaleValue(monitor, Performance.GetMonitor(monitor)));
		}
		else if (_custom is { } custom)
		{
			_graph.Push(Performance.GetCustomMonitor(custom).AsSingle());
		}
	}

	public override void Refresh()
	{
		var rows = new List<string[]>();
		foreach (Performance.Monitor monitor in All)
		{
			if (monitor == Performance.Monitor.MonitorMax)
			{
				continue;
			}

			rows.Add(new[] { Label(monitor), Format(monitor, Performance.GetMonitor(monitor)) });
		}

		foreach (StringName name in Performance.GetCustomMonitorNames())
		{
			rows.Add(new[] { name.ToString(), Performance.GetCustomMonitor(name).ToString() });
		}

		_table.Show(rows);
	}

	private static string Label(Performance.Monitor monitor) => monitor.ToString();

	/// <summary>Times in ms, memory in MB, everything else as the engine counts it.</summary>
	private static double ScaleValue(Performance.Monitor monitor, double value)
	{
		string name = monitor.ToString();
		return name.StartsWith("Time", System.StringComparison.Ordinal) && name != "TimeFps"
			? value * 1000.0
			: name.Contains("Mem", System.StringComparison.Ordinal)
				? value / 1048576.0
				: value;
	}

	private static string Format(Performance.Monitor monitor, double value)
	{
		string name = monitor.ToString();
		double scaled = ScaleValue(monitor, value);
		return name.StartsWith("Time", System.StringComparison.Ordinal) && name != "TimeFps"
			? $"{scaled:0.00} ms"
			: name.Contains("Mem", System.StringComparison.Ordinal)
				? $"{scaled:0.0} MB"
				: $"{scaled:0.##}";
	}
}
