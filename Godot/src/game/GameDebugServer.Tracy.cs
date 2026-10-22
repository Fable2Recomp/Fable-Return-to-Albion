using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// THE GAME'S TRACY CAPTURE OVER THE DEBUG CHANNEL: the same in-process capture and the same
/// queries as the F4 Tracy tabs, for external tools (tools/refii_mcp: refii_tracy, refii_trace).
/// </summary>
/// <remarks>
/// `{"op": "tracy", "action": ...}`. Actions:
///   status · start [limitMB, guestStacks] · stop · discard · save [path] ·
///   frames [last | first, count] · threads ·
///   zones [top, sort: self|total|p95|max|count|mean] · find zone [slowest] ·
///   samples [mode: functions|top-down|bottom-up, top, depth, minPercent] ·
///   locks · memory [pool, top] · plots [plot, points] · messages [last].
/// Ranges, where they apply: the whole capture, `lastSeconds`, or `frame` (an index from frames);
/// `thread` (a name or part of one) narrows zones, find and samples to one thread. Times in the
/// replies are milliseconds from the capture's start.
///
/// Saving compresses on a worker thread, as the F6 tab does; while it runs the capture is held,
/// so every action but status answers "saving".
/// </remarks>
public partial class GameDebugServer
{
	private static Task<bool>? s_tracySave;
	private static string s_tracySavePath = string.Empty;

	private static Dictionary<string, object?> AnswerTracy(JsonElement root)
	{
		string action = Text(root, "action", "status");
		if (!GameTracy.Enabled)
		{
			return Refused(GameTracy.Unavailable
				? "Tracy is unavailable in this session: tracy is on but the profiler's library could not be loaded"
				: "Tracy is off in this session: set tracy = true in refii.toml (read once at launch) and restart");
		}

		if (s_tracySave is { IsCompleted: false })
		{
			return action == "status"
				? Ok(new Dictionary<string, object?> { ["saving"] = s_tracySavePath })
				: Refused($"saving {s_tracySavePath}; ask status until it finishes");
		}

		HostTracy.Status status = HostTracy.GetStatus();
		switch (action)
		{
			case "status":
				return Ok(Describe(status));

			case "start":
			{
				long limit = (long)Number(root, "limitMB", 2048) * 1024 * 1024;
				bool guest = Flag(root, "guestStacks", true);
				return HostTracy.Start(limit, GameTracy.Advanced, guest)
					? Ok(Describe(HostTracy.GetStatus()))
					: Refused("the capture did not start (one is already live, or Tracy's client is not running)");
			}

			case "stop":
				HostTracy.Stop();
				return Ok(Describe(HostTracy.GetStatus()));

			case "discard":
				HostTracy.Discard();
				return Ok(new Dictionary<string, object?> { ["discarded"] = true });

			case "save":
			{
				if (status.Capture != HostTracy.CaptureState.Stopped)
				{
					return Refused("stop the capture first");
				}

				string path = Text(root, "path", string.Empty);
				s_tracySavePath = path.Length != 0 ? path : GameTracy.NewCapturePath("mcp");
				string target = s_tracySavePath;
				s_tracySave = Task.Run(() => HostTracy.Save(target));
				return Ok(new Dictionary<string, object?> { ["saving"] = target });
			}
		}

		if (status.Capture == HostTracy.CaptureState.None)
		{
			return Refused("no capture: start one (action start), or from the F4 Tracy tab");
		}

		(long t0, long t1) = Range(root, status);
		ulong thread = ThreadNamed(Text(root, "thread", string.Empty));
		var names = HostTracy.Threads().ToDictionary(t => t.Id, t => t.Name);
		double Ms(long ns) => System.Math.Round(ns / 1e6, 3);
		double At(long ns) => System.Math.Round((ns - status.FirstNs) / 1e6, 3);
		string Thread(ulong id) => names.TryGetValue(id, out string? name) ? name : id.ToString();

		switch (action)
		{
			case "frames":
			{
				ulong total = HostTracy.Frames(0, 0).Total;
				int count = (int)Number(root, "last", Number(root, "count", 120));
				ulong first = root.TryGetProperty("first", out JsonElement f) && f.TryGetUInt64(out ulong given)
					? given
					: total > (ulong)count ? total - (ulong)count : 0;
				(long[] start, long[] end, int n, _) = HostTracy.Frames(first, count);
				var frames = new List<object>();
				for (int i = 0; i < n; ++i)
				{
					frames.Add(new Dictionary<string, object?> { ["index"] = first + (ulong)i, ["at"] = At(start[i]), ["ms"] = Ms(end[i] - start[i]) });
				}

				return Ok(new Dictionary<string, object?> { ["total"] = total, ["frames"] = frames });
			}

			case "threads":
				return Ok(new Dictionary<string, object?>
				{
					["threads"] = HostTracy.Threads().Select(t => new Dictionary<string, object?>
					{
						["name"] = t.Name, ["id"] = t.Id, ["zones"] = t.HasZones, ["samples"] = t.HasSamples,
						["contextSwitches"] = t.HasContextSwitches,
					}).ToList(),
				});

			case "zones":
			{
				bool whole = t0 == long.MinValue && thread == 0;
				List<HostTracy.ZoneStat> stats = whole ? HostTracy.ZoneStats() : HostTracy.ZoneStats(thread, t0, t1);
				string sort = Text(root, "sort", "self");
				IEnumerable<HostTracy.ZoneStat> ordered = sort switch
				{
					"total" => stats.OrderByDescending(s => s.Total),
					"p95" => stats.OrderByDescending(s => s.P95),
					"max" => stats.OrderByDescending(s => s.Max),
					"count" => stats.OrderByDescending(s => s.Count),
					"mean" => stats.OrderByDescending(s => s.Total / (double)System.Math.Max(1UL, s.Count)),
					_ => stats.OrderByDescending(s => s.SelfTotal),
				};
				var rows = new List<object>();
				foreach (HostTracy.ZoneStat stat in ordered.Take((int)Number(root, "top", 30)))
				{
					HostTracy.SourceLocation location = HostTracy.Location(stat.SrcLoc);
					rows.Add(new Dictionary<string, object?>
					{
						["zone"] = location.Name, ["count"] = stat.Count, ["totalMs"] = Ms(stat.Total), ["selfMs"] = Ms(stat.SelfTotal),
						["meanMs"] = Ms(stat.Total / (long)System.Math.Max(1UL, stat.Count)), ["medianMs"] = Ms(stat.Median),
						["p95Ms"] = Ms(stat.P95), ["maxMs"] = Ms(stat.Max),
						["where"] = location.File.Length != 0 ? $"{location.Function} {location.File}:{location.Line}" : location.Function,
					});
				}

				return Ok(new Dictionary<string, object?> { ["sort"] = sort, ["zones"] = rows, ["of"] = stats.Count });
			}

			case "find":
			{
				string wanted = Text(root, "zone", string.Empty);
				HostTracy.ZoneStat? match = null;
				foreach (HostTracy.ZoneStat stat in HostTracy.ZoneStats())
				{
					string name = HostTracy.Location(stat.SrcLoc).Name;
					if (name == wanted)
					{
						match = stat;
						break;
					}

					if (match is null && name.Contains(wanted, System.StringComparison.OrdinalIgnoreCase))
					{
						match = stat;
					}
				}

				if (match is not { } found || wanted.Length == 0)
				{
					return Refused($"no zone named like '{wanted}'");
				}

				HostTracy.FindZoneResult result = HostTracy.FindZone(found.SrcLoc, thread, t0, t1, 24, true, (int)Number(root, "slowest", 10));
				return Ok(new Dictionary<string, object?>
				{
					["zone"] = HostTracy.Location(found.SrcLoc).Name, ["count"] = result.Count, ["totalMs"] = Ms(result.Total),
					["minMs"] = Ms(result.Min), ["medianMs"] = Ms(result.Median), ["p95Ms"] = Ms(result.P95), ["maxMs"] = Ms(result.Max),
					["histogramLogScale"] = result.Histogram,
					["slowest"] = result.Slowest.Select(i => new Dictionary<string, object?>
					{
						["at"] = At(i.Start), ["ms"] = Ms(i.End - i.Start), ["thread"] = Thread(i.Thread),
					}).ToList(),
				});
			}

			case "samples":
			{
				string mode = Text(root, "mode", "functions");
				int top = (int)Number(root, "top", 30);
				if (mode == "functions")
				{
					double all = System.Math.Max(1UL, status.Samples);
					return Ok(new Dictionary<string, object?>
					{
						["samples"] = status.Samples,
						["functions"] = HostTracy.SampledSymbols(top).Select(s => new Dictionary<string, object?>
						{
							["function"] = s.Name, ["self"] = s.Exclusive, ["selfPercent"] = System.Math.Round(s.Exclusive * 100.0 / all, 2),
							["total"] = s.Inclusive, ["totalPercent"] = System.Math.Round(s.Inclusive * 100.0 / all, 2),
							["where"] = s.File.Length != 0 ? $"{s.File}:{s.Line}" : string.Empty,
						}).ToList(),
					});
				}

				(List<HostTracy.TreeNode> nodes, ulong samples) = HostTracy.SampleTree(thread, t0, t1, mode == "bottom-up", 1, 20000);
				int depth = (int)Number(root, "depth", 8);
				double minPercent = Number(root, "minPercent", 1.0);
				var lines = new List<string>();
				foreach (HostTracy.TreeNode node in nodes)
				{
					double percent = node.Total * 100.0 / System.Math.Max(1UL, samples);
					if (node.Depth < depth && percent >= minPercent && lines.Count < top * 10)
					{
						lines.Add($"{new string(' ', node.Depth * 2)}{percent,6:0.0}%  self {node.Self * 100.0 / System.Math.Max(1UL, samples):0.0}%  {node.Name}");
					}
				}

				return Ok(new Dictionary<string, object?> { ["mode"] = mode, ["samples"] = samples, ["tree"] = lines });
			}

			case "locks":
				return Ok(new Dictionary<string, object?>
				{
					["locks"] = HostTracy.Locks(t0, t1).OrderByDescending(l => l.WaitTotal).Select(l => new Dictionary<string, object?>
					{
						["lock"] = l.Name, ["threads"] = l.Threads, ["acquired"] = l.Acquired, ["contended"] = l.Contended,
						["waitMs"] = Ms(l.WaitTotal), ["longestWaitMs"] = Ms(l.WaitMax), ["longestWaitAt"] = At(l.WorstWaitAt),
						["holdMs"] = Ms(l.HoldTotal), ["longestHoldMs"] = Ms(l.HoldMax),
					}).ToList(),
				});

			case "memory":
			{
				string pool = Text(root, "pool", string.Empty);
				var reply = new Dictionary<string, object?>
				{
					["pools"] = HostTracy.MemoryPools().Select(p => new Dictionary<string, object?>
					{
						["pool"] = p.Name, ["allocations"] = p.Allocations, ["live"] = p.Live,
						["liveMB"] = System.Math.Round(p.Bytes / 1048576.0, 2), ["peakMB"] = System.Math.Round(p.Peak / 1048576.0, 2),
					}).ToList(),
				};
				if (pool.Length != 0)
				{
					reply["largest"] = HostTracy.LargestAllocations(pool, (int)Number(root, "top", 20)).Select(a => new Dictionary<string, object?>
					{
						["id"] = $"0x{a.Id:X}", ["kb"] = System.Math.Round(a.Size / 1024.0, 1), ["at"] = At(a.Time),
					}).ToList();
				}

				return Ok(reply);
			}

			case "plots":
			{
				string wanted = Text(root, "plot", string.Empty);
				var rows = new List<object>();
				foreach (HostTracy.PlotInfo plot in HostTracy.Plots())
				{
					var row = new Dictionary<string, object?>
					{
						["plot"] = plot.Name, ["points"] = plot.Count, ["min"] = plot.Min, ["max"] = plot.Max,
					};
					if (wanted.Length != 0 && plot.Name.Contains(wanted, System.StringComparison.OrdinalIgnoreCase))
					{
						int points = (int)Number(root, "points", 200);
						var time = new long[points];
						var value = new double[points];
						int n = HostTracy.PlotData(plot.Index, t0, t1, time, value);
						row["series"] = Enumerable.Range(0, n).Select(i => new[] { At(time[i]), value[i] }).ToList();
					}

					rows.Add(row);
				}

				return Ok(new Dictionary<string, object?> { ["plots"] = rows });
			}

			case "messages":
			{
				int last = (int)Number(root, "last", 50);
				ulong total = HostTracy.Messages(0, 0).Total;
				ulong first = total > (ulong)last ? total - (ulong)last : 0;
				return Ok(new Dictionary<string, object?>
				{
					["total"] = total,
					["messages"] = HostTracy.Messages(first, last).Messages.Select(m => new Dictionary<string, object?>
					{
						["at"] = At(m.Time), ["thread"] = Thread(m.Thread), ["text"] = m.Text,
					}).ToList(),
				});
			}
		}

		return Refused($"unknown tracy action '{action}' — status, start, stop, discard, save, frames, threads, zones, find, samples, locks, memory, plots, messages");
	}

	private static Dictionary<string, object?> Describe(HostTracy.Status status) => new()
	{
		["state"] = status.Capture.ToString().ToLowerInvariant(),
		["frames"] = status.Frames,
		["zones"] = status.Zones,
		["seconds"] = System.Math.Round(System.Math.Max(0, status.LastNs - status.FirstNs) / 1e9, 3),
		["samples"] = status.Samples,
		["contextSwitches"] = status.ContextSwitches != 0,
		["advanced"] = status.SystemTracing != 0,
		["workerMB"] = status.MemoryBytes / 1048576,
		["limitMB"] = status.MemoryLimit / 1048576,
		["failure"] = status.Failure,
		["lastSave"] = s_tracySave is { IsCompleted: true } done ? (done.Result ? s_tracySavePath : "failed: " + s_tracySavePath) : null,
		["capturesDir"] = GameTracy.CapturesDirectory,
	};

	/// <summary>The whole capture, the last N seconds, or one frame by index.</summary>
	private static (long T0, long T1) Range(JsonElement root, HostTracy.Status status)
	{
		if (root.TryGetProperty("lastSeconds", out JsonElement seconds) && seconds.TryGetDouble(out double s))
		{
			return (status.LastNs - (long)(s * 1e9), status.LastNs);
		}

		if (root.TryGetProperty("frame", out JsonElement frame) && frame.TryGetUInt64(out ulong index))
		{
			(long[] start, long[] end, int n, _) = HostTracy.Frames(index, 1);
			if (n == 1)
			{
				return (start[0], end[0]);
			}
		}

		return (long.MinValue, long.MaxValue);
	}

	private static ulong ThreadNamed(string wanted)
	{
		if (wanted.Length == 0)
		{
			return 0;
		}

		HostTracy.ThreadInfo? partial = null;
		foreach (HostTracy.ThreadInfo thread in HostTracy.Threads())
		{
			if (thread.Name == wanted)
			{
				return thread.Id;
			}

			if (partial is null && thread.Name.Contains(wanted, System.StringComparison.OrdinalIgnoreCase))
			{
				partial = thread;
			}
		}

		return partial?.Id ?? ulong.MaxValue;  // no such thread: matches nothing rather than everything
	}

	private static string Text(JsonElement root, string name, string fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? fallback
			: fallback;

	private static double Number(JsonElement root, string name, double fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double number) ? number : fallback;

	private static bool Flag(JsonElement root, string name, bool fallback) =>
		root.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
			? value.GetBoolean()
			: fallback;

	private static Dictionary<string, object?> Refused(string error) =>
		new() { ["ok"] = false, ["error"] = error, ["pid"] = Godot.OS.GetProcessId(), ["boot"] = BootId };
}
