using System.Collections.Generic;
using System.Text;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Where the game's threads spent their time by sampling (tracy_advanced): each sampled function
/// with its self samples (in the function itself) and total samples (in it or anything it called),
/// and, for the one picked, its machine code disassembled with capstone — each instruction with
/// the samples that landed on it, the hottest highlighted.
/// </summary>
public partial class GameDevTabTracySamples : GameDevTab
{
	public override string Title => "Tracy samples";

	public override double RefreshSeconds => 1.0;

	private const int SymbolCapacity = 500;
	private const int InstructionCapacity = 4000;

	private readonly GameDevTable _table = new(new[]
	{
		"function", "self", "self %", "total", "total %", "where",
	}, sortColumn: 1);

	private readonly Label _summary = Readout();
	private readonly RichTextLabel _code = new()
	{
		BbcodeEnabled = true,
		ScrollActive = true,
		SelectionEnabled = true,
		SizeFlagsVertical = SizeFlags.ExpandFill,
		SizeFlagsHorizontal = SizeFlags.ExpandFill,
	};

	private readonly Dictionary<string, ulong> _addresses = new();
	private ulong _shownSamples = ulong.MaxValue;
	private ulong _picked;

	public GameDevTabTracySamples()
	{
		AddChild(_summary);
		var split = new VSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
		split.AddChild(_table);
		_code.AddThemeFontOverride("normal_font", new SystemFont { FontNames = new[] { "monospace" } });
		_code.AddThemeFontSizeOverride("normal_font_size", 13);
		_code.CustomMinimumSize = new Vector2(0, 180);
		split.AddChild(_code);
		AddChild(split);

		_table.Picked += row =>
		{
			if (_addresses.TryGetValue(row[0], out ulong address))
			{
				_picked = address;
				ShowCode(row[0]);
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

		if (status.Capture == HostTracy.CaptureState.Stopped && status.Samples == _shownSamples)
		{
			return;
		}

		_shownSamples = status.Samples;
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

		_summary.Text = $"{status.Samples:N0} samples, {rows.Count} functions" +
			(status.Capture == HostTracy.CaptureState.Stopped ? string.Empty : "   ·   live, updating each second") +
			"   ·   pick a function for its code";
		_table.Show(rows);
	}

	private void ShowCode(string name)
	{
		List<HostTracy.Instruction> code = HostTracy.Disassemble(_picked, InstructionCapacity);
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
