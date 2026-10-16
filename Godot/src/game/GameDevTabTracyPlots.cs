using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The Tracy capture's plots — every frame phase's milliseconds, the GPU passes, the game's CPU
/// share, memory — each over the whole capture, and the messages it logged, newest last.
/// </summary>
public partial class GameDevTabTracyPlots : GameDevTab
{
	public override string Title => "Tracy plots";

	public override double RefreshSeconds => 1.0;

	private const int MessageCapacity = 500;

	private readonly Label _summary = Readout();
	private readonly VBoxContainer _charts = new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };
	private readonly List<GameDevTracyPlot> _plots = new();
	private readonly GameDevTable _messages = new(new[] { "message", "at", "thread" }, sortColumn: -1);

	public GameDevTabTracyPlots()
	{
		AddChild(_summary);
		var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		var scroll = new ScrollContainer
		{
			SizeFlagsVertical = SizeFlags.ExpandFill,
			HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
			CustomMinimumSize = new Vector2(0, 200),
		};
		scroll.AddChild(_charts);
		split.AddChild(scroll);
		_messages.CustomMinimumSize = new Vector2(0, 140);
		split.AddChild(_messages);
		AddChild(split);
	}

	public override void Refresh()
	{
		HostTracy.Status status = HostTracy.GetStatus();
		if (status.Capture == HostTracy.CaptureState.None)
		{
			_summary.Text = "no capture — start one on the Tracy tab";
			Resize(0);
			_messages.Show(new List<string[]>());
			return;
		}

		List<HostTracy.PlotInfo> plots = HostTracy.Plots();
		Resize(plots.Count);
		for (int i = 0; i < plots.Count; ++i)
		{
			_plots[i].Show(plots[i], status.FirstNs, status.LastNs);
		}

		var names = new Dictionary<ulong, string>();
		foreach (HostTracy.ThreadInfo thread in HostTracy.Threads())
		{
			names[thread.Id] = thread.Name;
		}

		ulong total = HostTracy.Messages(0, 0).Total;
		ulong first = total > MessageCapacity ? total - MessageCapacity : 0;
		var rows = new List<string[]>();
		foreach (HostTracy.Message message in HostTracy.Messages(first, MessageCapacity).Messages)
		{
			rows.Add(new[]
			{
				message.Text,
				GameDevTracyTimeline.FormatNs(message.Time - status.FirstNs),
				names.TryGetValue(message.Thread, out string? name) ? name : message.Thread.ToString(),
			});
		}

		_messages.Show(rows);
		_summary.Text = $"{plots.Count} plots over {GameDevTracyTimeline.FormatNs(System.Math.Max(0, status.LastNs - status.FirstNs))}   ·   " +
			$"{total:N0} messages{(total > MessageCapacity ? $" (newest {MessageCapacity} shown)" : string.Empty)}";
	}

	private void Resize(int count)
	{
		while (_plots.Count < count)
		{
			var plot = new GameDevTracyPlot();
			_plots.Add(plot);
			_charts.AddChild(plot);
		}

		while (_plots.Count > count)
		{
			_plots[^1].QueueFree();
			_plots.RemoveAt(_plots.Count - 1);
		}
	}
}
