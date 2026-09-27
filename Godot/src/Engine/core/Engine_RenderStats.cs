using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// A viewport's own render measurement — CPU and GPU time, and the visible/shadow split of draw
/// calls, primitives and objects — read on the render thread once per frame and published for
/// the main thread to read freely.
/// </summary>
/// <remarks>
/// NEVER ASK THE RENDERING SERVER FOR A VALUE FROM THE MAIN THREAD. With a separate render
/// thread every value-returning call (`viewport_get_measured_render_time_*`,
/// `viewport_get_render_info`; `FUNC*R` in rendering_server_default.h) is `push_and_ret`: the
/// caller blocks until the render thread has drained its whole queue, which includes the
/// previous frame's draw. One such call per frame serialises the two threads — the main thread
/// cannot run ahead into the next frame while the render thread draws this one, and the render
/// thread then idles for the rest of the main thread's work.
///
/// So the reads happen where they are free: a callable run on the render thread, where the
/// same calls are direct, after the finished frame. Tracked viewports are held as nodes and
/// checked on the main thread before each sample, so a freed viewport's RID never reaches the
/// getters (they print an error per call for an unknown RID).
/// </remarks>
public static class Engine_RenderStats
{
	public struct Sample
	{
		public double CpuMs;
		public double GpuMs;
		public int VisibleCalls, VisiblePrims, VisibleObjects;
		public int ShadowCalls, ShadowPrims, ShadowObjects;

		/// <summary>False until the render thread has answered for this viewport once.</summary>
		public bool Sampled;
	}

	private sealed class Tracked
	{
		public Viewport Node = null!;
		public Rid Rid;
	}

	private static readonly object Gate = new();
	private static readonly List<Tracked> _tracked = new();
	private static readonly Dictionary<ulong, Sample> _published = new();

	/// <summary>The RIDs handed to the render thread this frame; written under the gate.</summary>
	private static Rid[] _pending = System.Array.Empty<Rid>();
	private static int _pendingCount;

	private static readonly Callable OnRenderThread = Callable.From(SampleOnRenderThread);

	/// <summary>
	/// The render thread's CPU time as a share of wall time since the previous ask, 0..1, or NaN
	/// where the OS does not expose it. The "is the render thread the wall" number: a viewport's
	/// measured render time covers one viewport's draw, not the command execution, the other
	/// viewports or the driver.
	/// </summary>
	/// <remarks>
	/// Linux: `/proc/self/task/&lt;tid&gt;/stat` for the thread named "WorkerThread 0", which is
	/// the render thread when `--render-thread separate` is on (a WorkerThreadPool native task).
	/// Found once by name; re-scanned if the file goes away. Elsewhere NaN.
	/// </remarks>
	public static double RenderThreadBusy()
	{
		if (!System.OperatingSystem.IsLinux())
		{
			return double.NaN;
		}

		try
		{
			if (_renderThreadStat is null || !System.IO.File.Exists(_renderThreadStat))
			{
				_renderThreadStat = FindRenderThreadStat();
				_renderThreadTicks = -1;
				if (_renderThreadStat is null)
				{
					return double.NaN;
				}
			}

			string stat = System.IO.File.ReadAllText(_renderThreadStat);
			int close = stat.LastIndexOf(')');
			string[] fields = stat[(close + 2)..].Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
			long ticks = long.Parse(fields[11]) + long.Parse(fields[12]);
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			double busy = double.NaN;
			if (_renderThreadTicks >= 0 && now > _renderThreadAt)
			{
				double wall = (now - _renderThreadAt) / (double)System.Diagnostics.Stopwatch.Frequency;
				busy = (ticks - _renderThreadTicks) / (double)ClockTicksPerSecond / wall;
			}

			_renderThreadTicks = ticks;
			_renderThreadAt = now;
			return busy;
		}
		catch (System.Exception)
		{
			_renderThreadStat = null;
			return double.NaN;
		}
	}

	private static string? _renderThreadStat;
	private static long _renderThreadTicks = -1;
	private static long _renderThreadAt;

	/// <summary>Linux `CLOCK_TCK`; 100 on every distribution kernel.</summary>
	private const double ClockTicksPerSecond = 100.0;

	private static string? FindRenderThreadStat()
	{
		foreach (string task in System.IO.Directory.GetDirectories("/proc/self/task"))
		{
			string comm = System.IO.Path.Combine(task, "comm");
			if (System.IO.File.Exists(comm) && System.IO.File.ReadAllText(comm).Trim() == "WorkerThread 0")
			{
				return System.IO.Path.Combine(task, "stat");
			}
		}

		return null;
	}

	/// <summary>
	/// Starts measuring a viewport. Idempotent; the first sample arrives a frame later.
	/// </summary>
	public static void Track(Viewport viewport)
	{
		lock (Gate)
		{
			foreach (Tracked have in _tracked)
			{
				if (have.Node == viewport)
				{
					return;
				}
			}

			_tracked.Add(new Tracked { Node = viewport, Rid = viewport.GetViewportRid() });
		}

		// Asynchronous: a plain push, and the render thread applies it before its next draw.
		RenderingServer.ViewportSetMeasureRenderTime(viewport.GetViewportRid(), true);
	}

	/// <summary>
	/// The last published sample for a viewport, tracking it from now on if it was not. A
	/// viewport asked for the first time answers `Sampled == false` this frame.
	/// </summary>
	public static Sample Of(Viewport viewport)
	{
		Track(viewport);
		lock (Gate)
		{
			return _published.TryGetValue(viewport.GetViewportRid().Id, out Sample sample)
				? sample
				: default;
		}
	}

	/// <summary>
	/// Once per frame from the main thread: drops freed viewports and asks the render thread
	/// for the finished frame's numbers.
	/// </summary>
	public static void SampleFrame()
	{
		lock (Gate)
		{
			for (int i = _tracked.Count - 1; i >= 0; --i)
			{
				if (!GodotObject.IsInstanceValid(_tracked[i].Node))
				{
					_published.Remove(_tracked[i].Rid.Id);
					_tracked.RemoveAt(i);
				}
			}

			if (_tracked.Count == 0)
			{
				return;
			}

			if (_pending.Length < _tracked.Count)
			{
				_pending = new Rid[System.Math.Max(_tracked.Count, _pending.Length * 2)];
			}

			for (int i = 0; i < _tracked.Count; ++i)
			{
				_pending[i] = _tracked[i].Rid;
			}

			_pendingCount = _tracked.Count;
		}

		// Queued behind everything the main thread has pushed so far, so it runs after the
		// previous frame's draw and reads that frame's measurement. Nothing here waits for it.
		RenderingServer.CallOnRenderThread(OnRenderThread);
	}

	private static void SampleOnRenderThread()
	{
		const RenderingServer.ViewportRenderInfoType Visible = RenderingServer.ViewportRenderInfoType.Visible;
		const RenderingServer.ViewportRenderInfoType Shadow = RenderingServer.ViewportRenderInfoType.Shadow;
		const RenderingServer.ViewportRenderInfo Calls = RenderingServer.ViewportRenderInfo.DrawCallsInFrame;
		const RenderingServer.ViewportRenderInfo Prims = RenderingServer.ViewportRenderInfo.PrimitivesInFrame;
		const RenderingServer.ViewportRenderInfo Objects = RenderingServer.ViewportRenderInfo.ObjectsInFrame;

		lock (Gate)
		{
			for (int i = 0; i < _pendingCount; ++i)
			{
				Rid rid = _pending[i];
				_published[rid.Id] = new Sample
				{
					CpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid),
					GpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid),
					VisibleCalls = RenderingServer.ViewportGetRenderInfo(rid, Visible, Calls),
					VisiblePrims = RenderingServer.ViewportGetRenderInfo(rid, Visible, Prims),
					VisibleObjects = RenderingServer.ViewportGetRenderInfo(rid, Visible, Objects),
					ShadowCalls = RenderingServer.ViewportGetRenderInfo(rid, Shadow, Calls),
					ShadowPrims = RenderingServer.ViewportGetRenderInfo(rid, Shadow, Prims),
					ShadowObjects = RenderingServer.ViewportGetRenderInfo(rid, Shadow, Objects),
					Sampled = true,
				};
			}
		}
	}
}
