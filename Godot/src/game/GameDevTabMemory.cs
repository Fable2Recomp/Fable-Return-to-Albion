using System.Diagnostics;
using Godot;

namespace Refii.Game;

/// <summary>
/// Where the memory is: the .NET heap and its collections (allocation rate is what drives GC
/// pauses, and GC pauses stall the guest's sim thread), Godot's object and memory monitors, video
/// memory, and the process as the OS sees it.
/// </summary>
public partial class GameDevTabMemory : GameDevTab
{
	public override string Title => "Memory";

	private readonly Label _text = Readout();
	private readonly GameDevGraph _allocGraph = new()
	{
		Unit = "MB/s",
		Budgets = new[] { (50f, "50 MB/s"), (100f, "100 MB/s") },
	};

	private readonly Stopwatch _wall = Stopwatch.StartNew();
	private double _lastWall;
	private long _lastAllocated = System.GC.GetTotalAllocatedBytes();

	public GameDevTabMemory()
	{
		_allocGraph.Lines.Add(new GameDevGraph.Series { Name = "managed alloc", Colour = new Color(1f, 0.6f, 0.4f) });
		AddChild(_text);
		Section("Managed allocation rate");
		AddChild(_allocGraph);
	}

	public override double RefreshSeconds => 0.5;

	public override void Refresh()
	{
		double now = _wall.Elapsed.TotalSeconds;
		double window = System.Math.Max(now - _lastWall, 1e-3);
		_lastWall = now;
		long allocated = System.GC.GetTotalAllocatedBytes();
		double rate = (allocated - _lastAllocated) / window / 1048576.0;
		_lastAllocated = allocated;
		_allocGraph.Push((float)rate);

		const double mb = 1048576.0;
		System.GCMemoryInfo gc = System.GC.GetGCMemoryInfo();
		double Monitor(Performance.Monitor monitor) => Performance.GetMonitor(monitor);
		using Process process = Process.GetCurrentProcess();

		_text.Text =
			$".NET   heap {gc.HeapSizeBytes / mb,8:0} MB   committed {gc.TotalCommittedBytes / mb:0} MB   " +
			$"fragmented {gc.FragmentedBytes / mb:0} MB   alloc {rate:0.0} MB/s\n" +
			$"       collections gen0 {System.GC.CollectionCount(0)}  gen1 {System.GC.CollectionCount(1)}  " +
			$"gen2 {System.GC.CollectionCount(2)}   pause total {System.GC.GetTotalPauseDuration().TotalMilliseconds / 1000.0:0.00} s   " +
			$"last pause {(gc.PauseDurations.Length > 0 ? gc.PauseDurations[0].TotalMilliseconds : 0):0.0} ms\n" +
			$"Godot  static {Monitor(Performance.Monitor.MemoryStatic) / mb:0} MB (peak {Monitor(Performance.Monitor.MemoryStaticMax) / mb:0})   " +
			$"objects {Monitor(Performance.Monitor.ObjectCount):0}   resources {Monitor(Performance.Monitor.ObjectResourceCount):0}   " +
			$"nodes {Monitor(Performance.Monitor.ObjectNodeCount):0}   orphan nodes {Monitor(Performance.Monitor.ObjectOrphanNodeCount):0}\n" +
			$"video  total {Monitor(Performance.Monitor.RenderVideoMemUsed) / mb:0} MB   " +
			$"textures {Monitor(Performance.Monitor.RenderTextureMemUsed) / mb:0} MB   " +
			$"buffers {Monitor(Performance.Monitor.RenderBufferMemUsed) / mb:0} MB\n" +
			$"process working set {process.WorkingSet64 / mb:0} MB   private {process.PrivateMemorySize64 / mb:0} MB   " +
			$"virtual {process.VirtualMemorySize64 / mb / 1024.0:0.0} GB";
	}
}
