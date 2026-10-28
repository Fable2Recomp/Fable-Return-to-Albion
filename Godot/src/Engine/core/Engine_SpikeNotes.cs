using System.Diagnostics;
using System.Threading;

namespace Refii.Core;

/// <summary>
/// What a frame did for the first time or in bulk: effect nodes and materials and textures
/// made, surfaces built, bytes uploaded, entity nodes created, the wait for the pose folds, clip
/// decodes. Counted at each site, read only by the hitch line (<see cref="Engine_FramePhases"/>).
/// </summary>
/// <remarks>
/// The phases say WHERE a slow frame went (fx.flush, ent.pose) but not what inside them was new
/// that frame: an 80 ms fx.flush is a texture made on first use or one surface rebuilt, an
/// ent.pose of 200 ms is the folds' wait behind the thread pool or a clip decoded in-line. One
/// add per event, nothing per frame but the window's close.
/// </remarks>
public static class Engine_SpikeNotes
{
	/// <summary>The events, fixed so the counters are plain arrays.</summary>
	public enum Kind
	{
		/// <summary>A FableFx node made for an effect the guest started (GameFx).</summary>
		FxNode,
		/// <summary>An effect batch material made (FableFx.LookupMaterial), its texture included.</summary>
		FxMaterial,
		/// <summary>An effect texture made from its image (FableFx.LoadTexture), any preparation included.</summary>
		FxTexture,
		/// <summary>An effect surface built from arrays rather than written in place.</summary>
		FxSurfaceBuild,
		/// <summary>An effect surface written in place: the value is the bytes handed to the renderer.</summary>
		FxUpload,
		/// <summary>The slowest single effect simulation step of the frame (a maximum, not a sum).</summary>
		FxSimWorst,
		/// <summary>An entity node created (GameEntities).</summary>
		EntityCreate,
		/// <summary>The main thread's wait for the pose folds at the end of the entity loop.</summary>
		PoseWait,
		/// <summary>A pose fold the main thread ran itself because no pool thread had started it. Not
		/// recorded yet: no site notes it, so the hitch line does not show it.</summary>
		PoseFoldInline,
		/// <summary>A clip decoded on the main thread.</summary>
		ClipDecode,
		/// <summary>A clip decode sent to the thread pool.</summary>
		ClipDecodeQueued,
		Count,
	}

	private static readonly long[] _count = new long[(int)Kind.Count];
	private static readonly long[] _value = new long[(int)Kind.Count];
	private static readonly long[] _countAtStart = new long[(int)Kind.Count];
	private static readonly long[] _valueAtStart = new long[(int)Kind.Count];
	private static readonly long[] _lastCount = new long[(int)Kind.Count];
	private static readonly long[] _lastValue = new long[(int)Kind.Count];
	private static long _worstSim;
	private static long _lastWorstSim;

	/// <summary>One event and the stopwatch ticks it took (0 when untimed).</summary>
	public static void Note(Kind kind, long ticks = 0)
	{
		Interlocked.Increment(ref _count[(int)kind]);
		if (ticks != 0)
		{
			Interlocked.Add(ref _value[(int)kind], ticks);
		}
	}

	/// <summary>One event since <paramref name="fromTicks"/> (a <see cref="Stopwatch.GetTimestamp"/>).</summary>
	public static void Since(Kind kind, long fromTicks) => Note(kind, Stopwatch.GetTimestamp() - fromTicks);

	/// <summary>One upload of <paramref name="bytes"/> bytes.</summary>
	public static void Upload(long bytes) => Note(Kind.FxUpload, bytes);

	/// <summary>An effect simulation step's ticks, kept if it is the frame's slowest. Main thread.</summary>
	public static void Sim(long ticks)
	{
		if (ticks > _worstSim)
		{
			_worstSim = ticks;
		}
	}

	/// <summary>Closes the frame's window; called by the frame-phase recorder at each frame start.</summary>
	public static void EndFrame()
	{
		for (int i = 0; i < (int)Kind.Count; i++)
		{
			long count = Interlocked.Read(ref _count[i]);
			long value = Interlocked.Read(ref _value[i]);
			_lastCount[i] = count - _countAtStart[i];
			_lastValue[i] = value - _valueAtStart[i];
			_countAtStart[i] = count;
			_valueAtStart[i] = value;
		}

		_lastWorstSim = _worstSim;
		_worstSim = 0;
	}

	/// <summary>The finished frame's events for the hitch line, "none" when there were none.</summary>
	public static string LastFrameSummary()
	{
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < (int)Kind.Count; i++)
		{
			var kind = (Kind)i;
			if (kind == Kind.FxSimWorst)
			{
				if (_lastWorstSim > 0)
				{
					Append(text, $"FxSimWorst {Ms(_lastWorstSim):0.0} ms");
				}

				continue;
			}

			long count = _lastCount[i];
			if (count == 0)
			{
				continue;
			}

			long value = _lastValue[i];
			if (kind == Kind.FxUpload)
			{
				Append(text, $"FxUpload {count} ({value / 1024.0:0} KB)");
			}
			else if (value > 0)
			{
				Append(text, $"{kind} {count} ({Ms(value):0.0} ms)");
			}
			else
			{
				Append(text, $"{kind} {count}");
			}
		}

		return text.Length == 0 ? "none" : text.ToString();
	}

	private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

	private static void Append(System.Text.StringBuilder text, string part)
	{
		text.Append(text.Length == 0 ? string.Empty : " ").Append(part);
	}
}
