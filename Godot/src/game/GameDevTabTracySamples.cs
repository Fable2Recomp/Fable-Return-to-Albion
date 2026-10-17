using System.Collections.Generic;
using System.Text;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Where the game's threads spent their time by sampling (tracy_advanced), four ways: the sampled
/// functions with their self and total samples; the call tree top-down (callers above callees) or
/// bottom-up (the functions the samples landed in, their callers beneath); and the flame graph.
/// Pick a function in any of them for its machine code, disassembled with capstone, each
/// instruction with the samples that landed on it.
/// </summary>
/// <remarks>
/// The trees and the flame graph follow the scope — the whole capture or the timeline's view,
/// every thread or one; the function list is the Worker's whole-capture totals. A live capture's
/// tree is rebuilt with Refresh, so expanding it is not undone every second.
/// </remarks>
public partial class GameDevTabTracySamples : GameDevTab
{
	public override string Title => "Tracy samples";

	public override double RefreshSeconds => 1.0;

	private const int SymbolCapacity = 500;
	private const int InstructionCapacity = 4000;
	private const int TreeCapacity = 20000;

	private enum Mode
	{
		Functions,
		TopDown,
		BottomUp,
		Flame,
	}

	private readonly GameDevTracyScope _scope = new();
	private readonly OptionButton _mode = new();
	private readonly Button _refresh = new() { Text = "Refresh" };
	private readonly Button _reset = new() { Text = "Zoom out" };
	private readonly Label _summary = Readout();
	private readonly GameDevTable _table = new(new[]
	{
		"function", "self", "self %", "total", "total %", "where",
	}, sortColumn: 1);

	private readonly Tree _tree = new()
	{
		Columns = 5,
		ColumnTitlesVisible = true,
		HideRoot = true,
		SizeFlagsVertical = SizeFlags.ExpandFill,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};

	private readonly GameDevTracyFlame _flame = new();
	private readonly ScrollContainer _flameScroll = new()
	{
		SizeFlagsVertical = SizeFlags.ExpandFill,
		HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
	};

	private readonly RichTextLabel _code = new()
	{
		BbcodeEnabled = true,
		ScrollActive = true,
		SelectionEnabled = true,
		SizeFlagsVertical = SizeFlags.ExpandFill,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};

	private readonly Dictionary<string, ulong> _addresses = new();
	private readonly List<HostTracy.TreeNode> _treeNodes = new();
	private ulong _shownSamples = ulong.MaxValue;
	private bool _stale = true;

	public GameDevTabTracySamples()
	{
		var bar = new HBoxContainer();
		bar.AddThemeConstantOverride("separation", 8);
		bar.AddChild(new Label { Text = "view" });
		foreach (string item in new[] { "functions", "call tree, top-down", "call tree, bottom-up", "flame graph" })
		{
			_mode.AddItem(item);
		}

		bar.AddChild(_mode);
		bar.AddChild(_scope);
		bar.AddChild(_refresh);
		bar.AddChild(_reset);
		AddChild(bar);
		AddChild(_summary);

		_tree.SetColumnTitle(0, "function");
		_tree.SetColumnExpand(0, true);
		string[] titles = { "total", "total %", "self", "self %" };
		for (int i = 0; i < titles.Length; ++i)
		{
			_tree.SetColumnTitle(i + 1, titles[i]);
			_tree.SetColumnExpand(i + 1, false);
			_tree.SetColumnCustomMinimumWidth(i + 1, 90);
		}

		_flameScroll.AddChild(_flame);

		var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		var views = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		views.AddChild(_table);
		views.AddChild(_tree);
		views.AddChild(_flameScroll);
		split.AddChild(views);
		_code.AddThemeFontOverride("normal_font", new SystemFont { FontNames = new[] { "monospace" } });
		_code.AddThemeFontSizeOverride("normal_font_size", 13);
		_code.CustomMinimumSize = new Vector2(0, 160);
		split.AddChild(_code);
		AddChild(split);

		_mode.ItemSelected += _ =>
		{
			_stale = true;
			ShowMode();
			Refresh();
		};
		_scope.Changed += () =>
		{
			_stale = true;
			Refresh();
		};
		_refresh.Pressed += () =>
		{
			_stale = true;
			Refresh();
		};
		_reset.Pressed += _flame.ResetFocus;
		_table.Picked += row =>
		{
			if (_addresses.TryGetValue(row[0], out ulong address))
			{
				ShowCode(address, row[0]);
			}
		};
		_tree.ItemSelected += () =>
		{
			if (_tree.GetSelected() is { } item && (int)item.GetMetadata(0) is int index and >= 0 && index < _treeNodes.Count)
			{
				ShowCode(_treeNodes[index].Address, _treeNodes[index].Name);
			}
		};
		_flame.Picked += ShowCode;
		ShowMode();
	}

	private Mode Current => (Mode)_mode.Selected;

	private void ShowMode()
	{
		_table.Visible = Current == Mode.Functions;
		_tree.Visible = Current is Mode.TopDown or Mode.BottomUp;
		_flameScroll.Visible = Current == Mode.Flame;
		_reset.Visible = Current == Mode.Flame;
		_scope.Visible = Current != Mode.Functions;
	}

	public override void Refresh()
	{
		HostTracy.Status status = HostTracy.GetStatus();
		if (status.Capture == HostTracy.CaptureState.None)
		{
			_summary.Text = "no capture — start one on the Tracy tab";
			_table.Show(new List<string[]>());
			_tree.Clear();
			_flame.Show(new List<HostTracy.TreeNode>(), 0);
			_code.Text = string.Empty;
			_shownSamples = ulong.MaxValue;
			return;
		}

		if (status.Samples == 0)
		{
			_summary.Text = status.SystemTracing != 0
				? "no samples in this capture — on Windows sampling needs the game to run as Administrator; on Linux, perf events for this process"
				: "no samples: this capture was started without the advanced data (tracy_advanced, restart to change)";
			_table.Show(new List<string[]>());
			return;
		}

		bool live = status.Capture != HostTracy.CaptureState.Stopped;
		if (_shownSamples == ulong.MaxValue || live)
		{
			_scope.ReloadThreads();
		}

		// A stopped capture does not change; a live tree waits for Refresh (the list and the
		// flame graph follow a live capture every second).
		bool changed = status.Samples != _shownSamples;
		if (!_stale && (!changed || (!live) || Current is Mode.TopDown or Mode.BottomUp))
		{
			return;
		}

		_stale = false;
		_shownSamples = status.Samples;
		switch (Current)
		{
			case Mode.Functions:
				ShowFunctions(status);
				break;
			case Mode.TopDown:
			case Mode.BottomUp:
				ShowTree(status, Current == Mode.BottomUp);
				break;
			case Mode.Flame:
				ShowFlame(status);
				break;
		}
	}

	private void ShowFunctions(HostTracy.Status status)
	{
		double total = System.Math.Max(1UL, status.Samples);
		var rows = new List<string[]>();
		_addresses.Clear();
		foreach (HostTracy.SampledSymbol symbol in HostTracy.SampledSymbols(SymbolCapacity))
		{
			string name = _addresses.ContainsKey(symbol.Name) ? $"{symbol.Name} @{symbol.Address:X}" : symbol.Name;
			_addresses[name] = symbol.Address;
			rows.Add(new[]
			{
				name,
				symbol.Exclusive.ToString(),
				$"{symbol.Exclusive / total * 100.0:0.00}",
				symbol.Inclusive.ToString(),
				$"{symbol.Inclusive / total * 100.0:0.00}",
				symbol.File.Length != 0 ? $"{System.IO.Path.GetFileName(symbol.File)}:{symbol.Line}" : string.Empty,
			});
		}

		_summary.Text = $"{status.Samples:N0} samples, {rows.Count} functions, whole capture" +
			(status.Capture == HostTracy.CaptureState.Stopped ? string.Empty : "   ·   live, updating each second") +
			"   ·   pick a function for its code";
		_table.Show(rows);
	}

	private void ShowTree(HostTracy.Status status, bool inverted)
	{
		(long t0, long t1) = _scope.Range;
		(List<HostTracy.TreeNode> nodes, ulong samples) = HostTracy.SampleTree(_scope.Thread, t0, t1, inverted,
			MinSamples(status.Samples), TreeCapacity);
		_treeNodes.Clear();
		_treeNodes.AddRange(nodes);
		_tree.Clear();
		TreeItem root = _tree.CreateItem();
		var items = new TreeItem[nodes.Count];
		double denominator = System.Math.Max(1UL, samples);
		for (int i = 0; i < nodes.Count; ++i)
		{
			HostTracy.TreeNode node = nodes[i];
			TreeItem item = _tree.CreateItem(node.Parent >= 0 ? items[node.Parent] : root);
			item.SetText(0, node.Name);
			item.SetText(1, node.Total.ToString("N0"));
			item.SetText(2, $"{node.Total / denominator * 100.0:0.0}");
			item.SetText(3, node.Self.ToString("N0"));
			item.SetText(4, $"{node.Self / denominator * 100.0:0.0}");
			item.SetMetadata(0, i);
			item.Collapsed = node.Depth >= 2;
			items[i] = item;
		}

		_summary.Text = $"{samples:N0} samples, {_scope.Describe()}, {(inverted ? "bottom-up: where samples landed, callers beneath" : "top-down: callers above callees")}" +
			(status.Capture == HostTracy.CaptureState.Stopped ? string.Empty : "   ·   live: Refresh to rebuild") +
			"   ·   pick a function for its code";
	}

	private void ShowFlame(HostTracy.Status status)
	{
		(long t0, long t1) = _scope.Range;
		(List<HostTracy.TreeNode> nodes, ulong samples) = HostTracy.SampleTree(_scope.Thread, t0, t1, false,
			MinSamples(status.Samples), TreeCapacity);
		_flame.Show(nodes, samples);
		_summary.Text = $"{samples:N0} samples, {_scope.Describe()}   ·   click to zoom in, right-click to step out, hover for numbers";
	}

	/// <summary>Branches below a ten-thousandth of the samples are left out: invisible anyway.</summary>
	private static uint MinSamples(ulong samples) => (uint)System.Math.Max(1UL, samples / 10000);

	private void ShowCode(ulong address, string name)
	{
		List<HostTracy.Instruction> code = HostTracy.Disassemble(address, InstructionCapacity);
		if (code.Count == 0)
		{
			_code.Text = $"{name}: no machine code for it in this capture (the client sends it as the function is first sampled)";
			return;
		}

		uint hottest = 1;
		uint samples = 0;
		foreach (HostTracy.Instruction instruction in code)
		{
			hottest = System.Math.Max(hottest, instruction.Samples);
			samples += instruction.Samples;
		}

		var text = new StringBuilder();
		text.Append($"[b]{Escape(name)}[/b]   {code.Count} instructions, {samples} samples on them\n");
		foreach (HostTracy.Instruction instruction in code)
		{
			string line = $"{instruction.Address:X12}  {(instruction.Samples > 0 ? instruction.Samples.ToString() : string.Empty),6}  {Escape(instruction.Text)}";
			if (instruction.Samples == 0)
			{
				text.Append("[color=#9aa4b0]").Append(line).Append("[/color]\n");
				continue;
			}

			float heat = instruction.Samples / (float)hottest;
			Color colour = new Color(1f, 1f - (0.7f * heat), 0.35f - (0.3f * heat));
			text.Append($"[color=#{colour.ToHtml(false)}]").Append(line).Append("[/color]\n");
		}

		_code.Text = text.ToString();
	}

	private static string Escape(string text) => text.Replace("[", "[lb]");
}
