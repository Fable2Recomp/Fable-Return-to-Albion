using Godot;
using Refii.Core;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Godot's own numbers into the in-game Tracy capture, as plots beside the frame phases: process,
/// physics and navigation time, object, node and resource counts, static and managed memory, the
/// video memory split, what the main viewport drew and what it cost, pipeline compilations and the
/// .NET collector — one point per frame, only while a capture streams.
/// </summary>
/// <remarks>
/// Nothing here asks the rendering server a question of its own: the counters are the ones
/// Engine_FramePhases already samples every frame, and the viewport's costs are the render
/// thread's published sample (Engine_RenderStats), so the feed adds no render-thread sync.
/// </remarks>
public partial class GameTracyFeed : Node
{
	private Engine_FramePhases.FrameCounters _was;
	private bool _haveWas;
	private double _sinceBusy;

	public override void _Ready()
	{
		Engine_RenderStats.Track(GetTree().Root);
	}

	public override void _Process(double delta)
	{
		if (!HostTracy.Live)
		{
			_haveWas = false;
			return;
		}

		Plot("Godot: process ms", Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0);
		Plot("Godot: physics ms", Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0);
		Plot("Godot: navigation ms", Performance.GetMonitor(Performance.Monitor.TimeNavigationProcess) * 1000.0);
		Plot("Godot: objects", Performance.GetMonitor(Performance.Monitor.ObjectCount));
		Plot("Godot: orphan nodes", Performance.GetMonitor(Performance.Monitor.ObjectOrphanNodeCount));
		Plot("Godot: physics active", Performance.GetMonitor(Performance.Monitor.Physics3DActiveObjects));
		Plot("Godot: static memory MB", Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0);
		Plot("Godot: audio latency ms", Performance.GetMonitor(Performance.Monitor.AudioOutputLatency) * 1000.0);
		Plot(".NET: managed heap MB", System.GC.GetTotalMemory(false) / 1048576.0);

		Engine_FramePhases.FrameCounters now = Engine_FramePhases.LatestCounters;
		Plot("Godot: video memory MB", now.VideoMemMB);
		Plot("Godot: texture memory MB", now.TextureMemMB);
		Plot("Godot: buffer memory MB", now.BufferMemMB);
		Plot("Godot: objects drawn", now.ObjectsDrawn);
		Plot("Godot: primitives", now.Primitives);
		Plot("Godot: nodes", now.Nodes);
		Plot("Godot: resources", now.Resources);
		if (_haveWas)
		{
			Plot("Godot: pipeline compilations", now.PipelinesCanvas + now.PipelinesMesh + now.PipelinesSurface +
				now.PipelinesDraw + now.PipelinesSpecialization - (_was.PipelinesCanvas + _was.PipelinesMesh +
				_was.PipelinesSurface + _was.PipelinesDraw + _was.PipelinesSpecialization));
			Plot(".NET: GC gen0", now.Gen0 - _was.Gen0);
			Plot(".NET: GC gen1", now.Gen1 - _was.Gen1);
			Plot(".NET: GC gen2", now.Gen2 - _was.Gen2);
			Plot(".NET: GC pause ms", now.GcPauseMs - _was.GcPauseMs);
		}

		_was = now;
		_haveWas = true;

		Engine_RenderStats.Sample main = Engine_RenderStats.Of(GetTree().Root);
		if (main.Sampled)
		{
			Plot("Godot: main viewport CPU ms", main.CpuMs);
			Plot("Godot: main viewport GPU ms", main.GpuMs);
			Plot("Godot: draw calls", main.VisibleCalls);
			Plot("Godot: shadow draw calls", main.ShadowCalls);
		}

		// The OS accrues thread CPU per scheduler tick: a share is only meaningful over a second.
		_sinceBusy += delta;
		if (_sinceBusy >= 1.0)
		{
			_sinceBusy = 0.0;
			double busy = Engine_RenderStats.RenderThreadBusy();
			if (double.IsFinite(busy))
			{
				Plot("Godot: render thread busy %", busy * 100.0);
			}
		}
	}

	private static void Plot(string name, double value) => HostBridge.TracePlot(name, value);
}
