using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// The recompiled game's own state: its pacing (sim time, the tick rate it is actually
/// achieving, the gates), the top game-flow state, the UI it has up (screen, modal, display box,
/// menu box), and its perf counters as live rates.
/// </summary>
public partial class GameDevTabGuest : GameDevTab
{
	public override string Title => "Guest";

	private readonly Label _state = Readout();

	private readonly GameDevTable _counters =
		new(new[] { "counter", "value", "per second" }, sortColumn: 2);

	private readonly LineEdit _filter = new() { PlaceholderText = "filter counters…" };
	private readonly Dictionary<string, long> _lastCounters = new();
	private readonly Stopwatch _wall = Stopwatch.StartNew();
	private double _lastWall;
	private double _lastSim;
	private double _simRate;

	/// <summary>
	/// The game-flow state classes, by vtable — each one's type slot (+4) is `li r3,N`
	/// (0x820AEC7C..0x820AED9C, read in IDA); the movie state is `off_820AD93C`.
	/// </summary>
	private static readonly Dictionary<uint, string> FlowStates = new()
	{
		[0x820AEC7Cu] = "1 GUI mode (a screen is up)",
		[0x820AECA0u] = "2",
		[0x820AECC4u] = "3",
		[0x820AECE8u] = "4",
		[0x820AED0Cu] = "5",
		[0x820AED30u] = "7 full cutscene",
		[0x820AED78u] = "8 display box (freezes play)",
		[0x820AED54u] = "9",
		[0x820AED9Cu] = "11",
		[0x820AD93Cu] = "10 movie",
	};

	private static readonly Dictionary<uint, string> BoxClasses = new()
	{
		[0x820AE668u] = "MessageBox",
		[0x820AE688u] = "QueryBox",
		[0x820AE6A8u] = "NumericAdjuster",
		[0x820AE6C8u] = "QuestBox (questrepeatable)",
		[0x820EBFA8u] = "Intermission",
		[0x820EBF3Cu] = "AcceptanceBox",
		[0x820028F4u] = "ButtonBox",
		[0x820EBF1Cu] = "InfoParams card",
	};

	private static readonly uint[] FlowChain = { 0u, 12u, 128u, 4u, 4u };

	public GameDevTabGuest()
	{
		AddChild(_state);
		Section("Guest perf counters — run totals and their rate since the last refresh");
		AddChild(_filter);
		AddChild(_counters);
	}

	public override void Refresh()
	{
		double now = _wall.Elapsed.TotalSeconds;
		double window = System.Math.Max(now - _lastWall, 1e-3);
		_lastWall = now;

		HostBridge.GameLoopPacer pacer = HostBridge.GetGameLoopPacer();
		if (pacer.Known)
		{
			_simRate = (pacer.SimTime - _lastSim) / window;
			_lastSim = pacer.SimTime;
		}

		var text = new System.Text.StringBuilder();
		text.Append($"in game {HostBridge.IsInGame}   place {GameWhereabouts.Current?.DisplayName ?? "-"}\n");
		if (pacer.Known)
		{
			text.Append($"sim {pacer.SimTime,9:0.00} s   advancing {_simRate:0.00} s/s   " +
				$"gates sim={pacer.SimulationGate} world={pacer.WorldGate} quit={pacer.QuitByte}\n");
			text.Append($"frames presented {pacer.Presented}  completed {pacer.Completed}  in flight {pacer.InFlight}   " +
				$"gui players {pacer.GuiRegisteredPlayers}  gui update {pacer.GuiUpdateEnabled}\n");
		}
		else
		{
			text.Append("no game object yet\n");
		}

		text.Append($"top game-flow state   {TopFlowState()}\n");

		if (HostBridge.GetPauseState() is { } pause)
		{
			text.Append($"screen {pause.ScreenId}   modal mode {pause.ModalMode} id {pause.ModalId}   tab {pause.CurrentTab}\n");
		}

		text.Append(HostBridge.GetDisplayBox() is { } box
			? $"display box  {(BoxClasses.TryGetValue(box.Vtable, out string? name) ? name : $"vt {box.Vtable:X8}")} at {box.Box:X8}\n"
			: "display box  none\n");
		text.Append(HostBridge.GetMenuBox() is { } menu
			? $"menu box     {menu.Count} option(s), selected {menu.Selected}\n"
			: "menu box     none\n");
		_state.Text = text.ToString();

		string filter = _filter.Text;
		var rows = new List<string[]>();
		foreach ((string counter, long value) in HostBridge.GetPerfCounters())
		{
			double rate = _lastCounters.TryGetValue(counter, out long before)
				? (value - before) / window
				: 0.0;
			_lastCounters[counter] = value;
			if (filter.Length != 0 &&
				!counter.Contains(filter, System.StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			rows.Add(new[] { counter, value.ToString(), $"{rate:0.0}" });
		}

		_counters.Show(rows);
	}

	/// <summary>The top of the game-flow stack — the chain `sub_82296368` walks.</summary>
	private static string TopFlowState()
	{
		uint at = 0x83496AB8u;
		foreach (uint offset in FlowChain)
		{
			uint[] word = HostBridge.Peek(at + offset, 1);
			if (word.Length != 1 || word[0] < 0x10000u)
			{
				return "-";
			}

			at = word[0];
		}

		uint[] node = HostBridge.Peek(at + 4u, 1);
		if (node.Length != 1 || node[0] == at || node[0] < 0x10000u)
		{
			return "empty";
		}

		uint[] state = HostBridge.Peek(node[0] + 8u, 1);
		uint[] vtable = state.Length == 1 && state[0] >= 0x10000u
			? HostBridge.Peek(state[0], 1)
			: System.Array.Empty<uint>();
		return vtable.Length != 1
			? "-"
			: FlowStates.TryGetValue(vtable[0], out string? name) ? name : $"vt {vtable[0]:X8}";
	}
}
