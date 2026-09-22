using System.Collections.Generic;
using System.Threading;

namespace Refii.Core;

/// <summary>
/// A census of what we tell the renderer to move each frame: every write of an instance
/// transform, a MultiMesh instance transform or a custom bounds box into the main scene, by source.
/// </summary>
/// <remarks>
/// Godot's scene cull (`renderer_scene_cull.cpp`, `_update_instance`) reacts to each such write:
/// every light the instance is paired with is marked shadow-dirty before any bounds compare, and
/// a changed bounds box moves the BVH leaf and re-pairs the instance against every volume in
/// /// reach. The render thread's profile shows the pairing cost but cannot say which of our writes
/// /// caused it; this can.
///
/// Counted, never sampled: a counter is one interlocked add at the write site. Frame totals are
/// taken at the frame boundary by <see cref="EndFrame"/>, which the frame-phase recorder calls.
/// </remarks>
public static class Engine_InstanceUpdates
{
	/// <summary>The sources, fixed so the counters are plain arrays.</summary>
	public enum Source
	{
		/// <summary>A live entity node's transform (GameEntities).</summary>
		EntityTransform,
		/// <summary>A live entity's custom bounds restated from its bone cloud.</summary>
		EntityExtent,
		/// <summary>A married static twin driven to its entity's place (Engine_StaticTwins).</summary>
		TwinDriven,
		/// <summary>An entity batch slot written (Engine_EntityBatches).</summary>
		BatchSlot,
		/// <summary>A prop batch's bounds grown or its twin re-bounded (props).</summary>
		PropBounds,
		/// <summary>A shadow proxy or prop shadow twin synced.</summary>
		ShadowTwin,
		/// <summary>An FX instance placed (FableFx, the FX system).</summary>
		Fx,
		/// <summary>A placed light moved or re-ranged (lights, flicker range writes excluded).</summary>
		Light,
		/// <summary>Sky, weather, water: the per-frame environment movers.</summary>
		Environment,
		/// <summary>Anything else that named no source.</summary>
		Other,
		Count,
	}

	private static readonly long[] _totals = new long[(int)Source.Count];
	private static readonly long[] _atFrameStart = new long[(int)Source.Count];
	private static readonly double[] _lastFrame = new double[(int)Source.Count];
	private static readonly double[] _average = new double[(int)Source.Count];
	private static long _frames;

	/// <summary>One write of a transform or bounds box into the scene.</summary>
	public static void Note(Source source) => Interlocked.Increment(ref _totals[(int)source]);

	/// <summary>Several writes at once (a batch rewritten slot by slot).</summary>
	public static void Note(Source source, int count)
	{
		if (count > 0)
		{
			Interlocked.Add(ref _totals[(int)source], count);
		}
	}

	/// <summary>
	/// Closes the frame's window: the counts since the last call become the frame's, and feed a
	/// decaying average (1/60 per frame) so a one-second read is steady.
	/// </summary>
	public static void EndFrame()
	{
		++_frames;
		for (int i = 0; i < (int)Source.Count; i++)
		{
			long now = Interlocked.Read(ref _totals[i]);
			double inFrame = now - _atFrameStart[i];
			_atFrameStart[i] = now;
			_lastFrame[i] = inFrame;
			_average[i] += (inFrame - _average[i]) * (1.0 / 60.0);
		}
	}

	/// <summary>Per-source run totals, last frame and decaying per-frame average.</summary>
	public static Dictionary<string, object> Report()
	{
		var report = new Dictionary<string, object> { ["frames"] = _frames };
		double lastTotal = 0.0;
		double averageTotal = 0.0;
		for (int i = 0; i < (int)Source.Count; i++)
		{
			string name = ((Source)i).ToString();
			report[name] = new Dictionary<string, object>
			{
				["total"] = Interlocked.Read(ref _totals[i]),
				["lastFrame"] = _lastFrame[i],
				["perFrame"] = System.Math.Round(_average[i], 2),
			};
			lastTotal += _lastFrame[i];
			averageTotal += _average[i];
		}

		report["lastFrameTotal"] = lastTotal;
		report["perFrameTotal"] = System.Math.Round(averageTotal, 2);
		return report;
	}

	/// <summary>The one-line form for the hitch log: sources that wrote this frame, largest first.</summary>
	public static string LastFrameSummary()
	{
		var parts = new List<(string Name, double Count)>();
		for (int i = 0; i < (int)Source.Count; i++)
		{
			if (_lastFrame[i] > 0)
			{
				parts.Add((((Source)i).ToString(), _lastFrame[i]));
			}
		}

		parts.Sort((a, b) => b.Count.CompareTo(a.Count));
		var text = new System.Text.StringBuilder();
		foreach ((string name, double count) in parts)
		{
			text.Append(text.Length == 0 ? string.Empty : " ").Append(name).Append(' ').Append(count.ToString("0"));
		}

		return text.Length == 0 ? "none" : text.ToString();
	}
}
