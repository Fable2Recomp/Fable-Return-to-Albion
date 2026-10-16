using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// Godot's own GPU pass timings, read off the RenderingDevice once per frame: the split of the
/// viewport's one `renderGpuMs` into shadows, opaque, sky, transparent, SDFGI, fog and tonemap.
/// </summary>
/// <remarks>
/// `RENDER_TIMESTAMP` puts a GPU timestamp at every pass boundary while frame profiling is on,
/// which the release template keeps behind `--gpu-profile` (`gpu_profile = true` in refii.toml);
/// the switch is not bound to script, so this only reads the table the flag produces. Each
/// timestamp names the step that STARTS there, and the step lasts until the next timestamp. With
/// the flag off the count reads zero and nothing here costs anything.
///
/// Kept as rolling averages for the debug channel's `gpuprofile` op, and handed whole to
/// <see cref="Sink"/> so the profiler bridge can put the same passes on the Tracy timeline. The
/// sink is a hook because this file is core and the bridge is the game's.
/// </remarks>
public static class Engine_GpuPasses
{
	/// <summary>Bytes per packed name handed to the sink, NUL padded.</summary>
	public const int NameStride = 48;

	private sealed class Pass
	{
		public double AverageMs;
		public double PeakMs;
		public double LastMs;
		public int Order;
		public long LastFrame;
	}

	private static readonly Dictionary<string, Pass> Passes = new();
	private static readonly Refii.Core.Engine_TracedLock Gate = new("GPU passes");

	/// <summary>(packed names, stride, cpu times, gpu ns, count) for the finished frame.</summary>
	public static System.Action<byte[], uint, long[], long[], int>? Sink;

	private static ulong _lastFrame = ulong.MaxValue;
	private static long _frames;
	private static byte[] _names = new byte[NameStride * 32];
	private static long[] _cpu = new long[32];
	private static long[] _gpu = new long[32];
	private static string[] _nameStrings = new string[32];

	/// <summary>Per timestamp, its pass name under the viewport it runs in: "vp3/Render Opaque Pass".</summary>
	private static string[] _keys = new string[32];

	/// <summary>Per-frame sums by key: a pass a viewport runs more than once (one per light) is one row.</summary>
	private static readonly Dictionary<string, double> _frameSums = new();

	private static readonly List<string> _frameOrder = new();
	private static uint _nameCount;
	private static long _namesReadAt;

	private const double NanosecondsPerMs = 1_000_000.0;

	/// <summary>Frames between re-reads of an unchanged pass list; see SampleOnRenderThread.</summary>
	private const long NameRefreshFrames = 600;

	/// <summary>True once a table has been read: the engine is capturing timestamps.</summary>
	public static bool Available { get; private set; }

	/// <summary>The finished frame's GPU span, first timestamp to last, in milliseconds.</summary>
	public static double LastTotalMs { get; private set; }

	private static readonly Callable OnRenderThread = Callable.From(SampleOnRenderThread);

	/// <summary>
	/// Asks the render thread for the finished frame's table. The timestamp queries are guarded
	/// to that thread (`get_captured_timestamps_count` refuses the main thread with an error
	/// per call), and with `--render-thread separate` this is not it.
	/// </summary>
	public static void Sample() => RenderingServer.CallOnRenderThread(OnRenderThread);

	/// <summary>Reads the finished frame's table, if there is a new one. Render thread only.</summary>
	private static void SampleOnRenderThread()
	{
		RenderingDevice? rd = RenderingServer.GetRenderingDevice();
		if (rd is null)
		{
			return;
		}

		uint count = rd.GetCapturedTimestampsCount();
		if (count < 2)
		{
			return;
		}

		ulong frame = rd.GetCapturedTimestampsFrame();
		if (frame == _lastFrame)
		{
			return;
		}

		_lastFrame = frame;
		Available = true;
		++_frames;

		if (count > _cpu.Length)
		{
			int grown = System.Math.Max((int)count, _cpu.Length * 2);
			_cpu = new long[grown];
			_gpu = new long[grown];
			_names = new byte[NameStride * grown];
		}

		// Names are read once per pass list, not per frame: each crosses as a fresh managed
		// string. The list changes only when a pass appears or goes (a light starts casting, GI
		// toggles), so it is re-read when the count moves and every NameRefreshFrames besides,
		// which catches a same-length reorder.
		if (count != _nameCount || _frames - _namesReadAt >= NameRefreshFrames)
		{
			if (_nameStrings.Length < count)
			{
				_nameStrings = new string[System.Math.Max((int)count, _nameStrings.Length * 2)];
				_keys = new string[_nameStrings.Length];
			}

			System.Array.Clear(_names, 0, (int)count * NameStride);
			for (uint i = 0; i < count; ++i)
			{
				string name = rd.GetCapturedTimestampName(i);
				_nameStrings[i] = name;
				System.Text.Encoding.UTF8.GetBytes(
					name, 0, System.Math.Min(name.Length, NameStride - 1),
					_names, (int)i * NameStride);
			}

			// Every viewport emits the same pass names (main view, the UI's 3D view, each water
			// mirror), so a pass is keyed by its viewport too. The engine brackets each viewport
			// with "> Render Viewport N" / "< Render Viewport N"; the innermost open one owns the
			// pass. Keys are built here, with the names, so a frame allocates nothing.
			var open = new Stack<string>();
			for (uint i = 0; i < count; ++i)
			{
				string name = _nameStrings[i];
				if (name.StartsWith("> Render Viewport ", System.StringComparison.Ordinal))
				{
					open.Push("vp" + name.Substring("> Render Viewport ".Length));
				}

				_keys[i] = open.Count != 0 ? open.Peek() + "/" + name : name;
				if (name.StartsWith("< Render Viewport ", System.StringComparison.Ordinal) && open.Count != 0)
				{
					open.Pop();
				}
			}

			_nameCount = count;
			_namesReadAt = _frames;
		}

		for (uint i = 0; i < count; ++i)
		{
			_cpu[i] = (long)rd.GetCapturedTimestampCpuTime(i);
			_gpu[i] = (long)rd.GetCapturedTimestampGpuTime(i);
		}

		// Each timestamp names the step that STARTS there; it lasts until the next one. GPU times
		// are NANOSECONDS, whatever the binding's documentation says.
		_frameSums.Clear();
		_frameOrder.Clear();
		for (uint i = 0; i + 1 < count; ++i)
		{
			double ms = (_gpu[i + 1] - _gpu[i]) / NanosecondsPerMs;
			string key = _keys[i];
			if (_frameSums.TryGetValue(key, out double sum))
			{
				_frameSums[key] = sum + ms;
			}
			else
			{
				_frameSums[key] = ms;
				_frameOrder.Add(key);
			}
		}

		for (int k = 0; k < _frameOrder.Count; ++k)
		{
			Record(_frameOrder[k], _frameSums[_frameOrder[k]], k);
		}

		LastTotalMs = (_gpu[count - 1] - _gpu[0]) / NanosecondsPerMs;
		Record("total", LastTotalMs, int.MaxValue);
		Sink?.Invoke(_names, NameStride, _cpu, _gpu, (int)count);
	}

	private static void Record(string name, double ms, int order)
	{
		using (Gate.Hold())
		{
			if (!Passes.TryGetValue(name, out Pass? pass))
			{
				pass = new Pass { AverageMs = ms, Order = order };
				Passes[name] = pass;
			}

			pass.LastMs = ms;
			pass.LastFrame = _frames;
			pass.Order = order;
			pass.AverageMs += (ms - pass.AverageMs) * 0.05;
			pass.PeakMs = System.Math.Max(pass.PeakMs * 0.98, ms);
		}
	}

	/// <summary>
	/// The passes in the engine's own order, each {avgMs, peakMs, lastMs}, with the total last.
	/// Says how to switch the capture on when the engine is not producing one.
	/// </summary>
	public static Dictionary<string, object?> Report()
	{
		var report = new Dictionary<string, object?>
		{
			["available"] = Available,
			["frames"] = _frames,
		};

		if (!Available)
		{
			report["note"] = "the engine is not capturing pass timestamps — set gpu_profile = true " +
				"in refii.toml (the launcher passes --gpu-profile) and relaunch";
			return report;
		}

		var ordered = new List<KeyValuePair<string, Pass>>();
		using (Gate.Hold())
		{
			foreach (KeyValuePair<string, Pass> entry in Passes)
			{
				// A pass the engine stopped emitting drops out rather than lingering at its last value.
				if (_frames - entry.Value.LastFrame < 300)
				{
					ordered.Add(entry);
				}
			}
		}

		ordered.Sort((a, b) => a.Value.Order.CompareTo(b.Value.Order));
		var passes = new List<Dictionary<string, object?>>(ordered.Count);
		foreach ((string name, Pass pass) in ordered)
		{
			passes.Add(new Dictionary<string, object?>
			{
				["pass"] = name,
				["avgMs"] = System.Math.Round(pass.AverageMs, 3),
				["peakMs"] = System.Math.Round(pass.PeakMs, 3),
				["lastMs"] = System.Math.Round(pass.LastMs, 3),
			});
		}

		report["passes"] = passes;
		report["lastTotalMs"] = System.Math.Round(LastTotalMs, 3);
		return report;
	}
}
