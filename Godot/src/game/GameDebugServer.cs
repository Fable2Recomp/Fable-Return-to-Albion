using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Godot;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// A local debug channel into the RUNNING game — one newline-delimited JSON request per line,
/// one response per line, on 127.0.0.1.
///
/// WHY. Every diagnostic in this project is a `GD.Print` decided before the run: a question
/// nobody anticipated costs an edit, a package build, a codegen and a load — ten minutes to ask
/// one thing, which is why so much of the logging is speculative and so much of it is noise.
/// And nothing here could SEE: every visual defect on these screens was found by a person
/// looking at the game and describing it, while the logs said `slide=1 contents.x=0` and left
/// the geometry to be inferred.
///
/// This makes the running game answerable instead. `shot` writes a PNG of the window, `state`
/// says which screen the guest has up and what the owned list holds, `press` drives the pad,
/// and `elements` dumps a presentation's tree with the positions and opacities that a page bug
/// actually lives in.
///
/// The `threads`, `objects`, `perf`, `exports`, `cvars`, `cvar-set`, `vfs` and `sockets` ops
/// reach through the host bridge into the ReXGlue runtime itself — guest backtraces, the kernel
/// handle table, live counters, the export tables, the cvar registry and the VFS — so a frozen
/// run names its own stuck thread instead of needing gdb as a parent.
///
/// OFF UNLESS ASKED, AND LOOPBACK ONLY. `REFII_DEBUG_PORT` turns it on; it binds 127.0.0.1 and
/// nothing else, so it is not reachable from another machine. A shipped run never sets it.
///
/// The socket is served from `_Process`, on Godot's own thread, because everything it reads —
/// the scene tree, the viewport, the UI model — belongs to that thread. There is no locking
/// here and there must not need to be.
/// </summary>
public partial class GameDebugServer : Node
{
	/// <summary>`REFII_DEBUG_PORT=1` (or a port number) turns the channel on.</summary>
	public static bool Wanted =>
		(System.Environment.GetEnvironmentVariable("REFII_DEBUG_PORT") ?? string.Empty).Length != 0;

	/// <summary>
	/// The first port to try. A FIXED port was the original design and it was wrong — see
	/// <see cref="HandshakePath"/>.
	///
	/// THE VARIABLE MEANS TWO THINGS AND ONLY ONE OF THEM IS A PORT. `REFII_DEBUG_PORT=1` is
	/// how a run says "on" — and parsing that as a port number asked to bind port 1, which is
	/// privileged, so the scan walked 1..16 and gave up: `no free port in 1..16`. Anything
	/// below the unprivileged floor is a flag, not a port.
	/// </summary>
	private const int DefaultPort = 8787;

	private const int FirstUnprivilegedPort = 1024;

	private static int FirstPort =>
		int.TryParse(System.Environment.GetEnvironmentVariable("REFII_DEBUG_PORT"), out int port)
		&& port >= FirstUnprivilegedPort && port < 65536
			? port
			: DefaultPort;

	/// <summary>
	/// Where this process publishes {pid, port, boot} so a client can find THIS game and no
	/// other.
	///
	/// A FIXED PORT PLUS A RESTARTING GAME IS A STALENESS BUG WAITING TO HAPPEN, and this
	/// project has already paid for that class of mistake twice: months-old logs read as "the
	/// guest logged nothing", and — this session — a `pkill` whose pattern matched its own
	/// command line, leaving a game running for twenty minutes while I believed it dead. On a
	/// fixed port that zombie keeps the socket, the NEW game fails to bind and silently has no
	/// channel, and every question I ask is answered by the corpse. Screenshots of a dead run,
	/// button presses into a dead run, and a completely coherent wrong conclusion.
	///
	/// So the port is not fixed and not assumed: each process takes the first free one, writes
	/// its identity here, and removes the file on the way out. A client reads the file, checks
	/// the pid is alive, and compares the boot id it saw last — so "the game restarted under
	/// you" is a thing it can SAY rather than something it cannot notice.
	/// </summary>
	public static string HandshakePath =>
		System.Environment.GetEnvironmentVariable("REFII_DEBUG_HANDSHAKE") is { Length: > 0 } path
			? path
			: System.IO.Path.Combine(
				System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
				".cache", "refii-scratch", "refii-debug.json");

	/// <summary>Random per process: what tells one run from the next on the same port.</summary>
	private static readonly string BootId = System.Guid.NewGuid().ToString("N")[..12];

	private static readonly System.Diagnostics.Stopwatch Uptime =
		System.Diagnostics.Stopwatch.StartNew();

	private int _port;

	private TcpServer? _server;
	private readonly List<StreamPeerTcp> _clients = new();

	/// <summary>Buttons a `press` asked for, held for a few frames; OR'd in by <see cref="GameInput"/>.</summary>
	private static uint _pressed;

	private static int _pressFrames;

	/// <summary>Stick and trigger values a `press` asked for, XInput-scaled, held like the
	/// buttons; merged by <see cref="GameInput"/> against the real devices.</summary>
	private static int _stickLX, _stickLY, _stickRX, _stickRY;

	private static uint _trigL, _trigR;

	private static int _stickFrames;

	/// <summary>Consumed once per input frame alongside <see cref="Buttons"/>.</summary>
	public static bool Sticks(out int lx, out int ly, out int rx, out int ry,
		out uint lt, out uint rt)
	{
		bool live = _stickFrames > 0 && --_stickFrames >= 0;
		lx = live ? _stickLX : 0;
		ly = live ? _stickLY : 0;
		rx = live ? _stickRX : 0;
		ry = live ? _stickRY : 0;
		lt = live ? _trigL : 0;
		rt = live ? _trigR : 0;
		return live;
	}

	// ---- input recording and replay ----

	/// <summary>One published pad frame, raw and pre-mask: the masks are game-state driven
	/// and re-apply naturally on replay.</summary>
	public readonly record struct PadFrame(
		uint Buttons, uint Lt, uint Rt, int Lx, int Ly, int Rx, int Ry);

	private static List<PadFrame>? _recording;

	private static PadFrame[]? _replay;

	private static int _replayAt;

	public static void RecordInputFrame(uint buttons, uint lt, uint rt, int lx, int ly,
		int rx, int ry)
	{
		if (_recording is { } tape && tape.Count < 4_000_000)
		{
			tape.Add(new PadFrame(buttons, lt, rt, lx, ly, rx, ry));
		}
	}

	public static bool ReplayFrame(out PadFrame frame)
	{
		frame = default;
		if (_replay is not { } tape || _replayAt >= tape.Length)
		{
			_replay = null;
			return false;
		}

		frame = tape[_replayAt++];
		return true;
	}

	// ---- flight recorder ----

	/// <summary>
	/// Once a second, one compact line into user://logs/refii_flight.jsonl: what the run was
	/// doing. After a freeze or a silent death the LAST LINE says where it stood, and the
	/// thread r1/lr snapshot lets the arena reader walk guest stacks with no channel — the one
	/// gap frozen-run backtraces had. Rotates once at ~2 MB; a run that lives for hours keeps
	/// its most recent history.
	/// </summary>
	private double _flightDue;

	private void RecordFlight(double delta)
	{
		_flightDue -= delta;
		if (_flightDue > 0)
		{
			return;
		}

		_flightDue = 1.0;
		try
		{
			string path = ProjectSettings.GlobalizePath("user://logs/refii_flight.jsonl");
			if (System.IO.File.Exists(path) &&
				new System.IO.FileInfo(path).Length > 2_000_000)
			{
				System.IO.File.Move(path, path + ".1", overwrite: true);
			}

			var line = new Dictionary<string, object?>
			{
				["t"] = System.DateTime.UtcNow.ToString("HH:mm:ss"),
				["up"] = (int)Uptime.Elapsed.TotalSeconds,
				["boot"] = BootId,
				["guest"] = HostBridgeUp(),
			};
			if (Engine_UiSource.Current.GetInGameScreens() is { } screens)
			{
				line["screen"] = screens.Current.ToString();
			}

			var threads = new List<string>();
			foreach (Refii.Host.HostBridge.GuestThreadInfo info in
				Refii.Host.HostBridge.GetGuestThreads())
			{
				if (info.IsGuestThread)
				{
					threads.Add($"{info.Tid}:{info.R1:X8}:{info.Lr:X8}");
				}
			}

			if (threads.Count > 0)
			{
				line["threads"] = threads;
			}

			System.IO.File.AppendAllText(path, JsonSerializer.Serialize(line) + "\n");
		}
		catch (System.Exception)
		{
			// The recorder must never take the game down; a missed beat is a missed beat.
		}
	}

	/// <summary>The pad the debug channel is holding this frame, or 0.</summary>
	public static uint Buttons() => _pressFrames > 0 && --_pressFrames >= 0 ? _pressed : 0u;

	public override void _Ready()
	{
		AddChild(new Refii.Core.Engine_FrameStartNode { Name = "FrameStart" });
		AddChild(new Refii.Core.Engine_FrameEndNode { Name = "FrameEnd" });
		if (!Wanted)
		{
			return;
		}

		// The first FREE port, not a fixed one. A port already in use almost always means a
		// previous instance is still alive — which is worth saying, because that instance is
		// exactly what a client would otherwise be talking to.
		_server = new TcpServer();
		int first = FirstPort;
		for (int port = first; port < first + 16; ++port)
		{
			if (_server.Listen((ushort)port, "127.0.0.1") == Error.Ok)
			{
				_port = port;
				break;
			}
		}

		if (_port == 0)
		{
			_server = null;
			GD.Print($"[debug-server] no free port in {first}..{first + 15} — no debug channel " +
				"this run (is an older instance still running?)");
			return;
		}

		if (_port != first)
		{
			GD.Print($"[debug-server] port {first} was taken — using {_port}. Something else is " +
				"holding it; if that is an older game, it is still answering questions.");
		}

		Publish();
		GD.Print($"[debug-server] listening on 127.0.0.1:{_port} (pid {OS.GetProcessId()}, " +
			$"boot {BootId}) — handshake at {HandshakePath}");
	}

	/// <summary>
	/// Writes {pid, port, boot} so a client can find THIS process — plus, once the guest is up,
	/// the arena identity (see HostBridge.GetGuestArenaInfo). The arena goes in the FILE, not
	/// only in channel replies, because its consumer is the out-of-process reader whose whole
	/// purpose is the frozen run this thread can no longer answer for.
	/// </summary>
	private void Publish()
	{
		try
		{
			var handshake = new Dictionary<string, object?>
			{
				["pid"] = OS.GetProcessId(),
				["port"] = _port,
				["boot"] = BootId,
				["started"] = _started,
			};
			if (Refii.Host.HostBridge.GetGuestArenaInfo() is { } arena)
			{
				handshake["arena"] = arena.Name;
				handshake["membase"] = arena.Membase;
				_arenaPublished = true;
			}

			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(HandshakePath)!);
			System.IO.File.WriteAllText(HandshakePath, JsonSerializer.Serialize(handshake));
		}
		catch (System.Exception error)
		{
			GD.Print($"[debug-server] could not write {HandshakePath}: {error.Message}");
		}
	}

	/// <summary>Stable across republishes: the handshake must not look like a new run.</summary>
	private readonly string _started = System.DateTime.UtcNow.ToString("O");

	/// <summary>The arena exists only once the guest is up, which is after _Ready — so each
	/// frame until it is published, Publish() is retried from _Process.</summary>
	private bool _arenaPublished;

	public override void _ExitTree()
	{
		foreach (StreamPeerTcp client in _clients)
		{
			client.DisconnectFromHost();
		}

		_clients.Clear();
		_server?.Stop();
		_server = null;

		// Take the handshake with it, but ONLY if it is still ours — another game may have
		// started and published over it, and removing that one would strand a live channel.
		try
		{
			if (System.IO.File.Exists(HandshakePath) &&
				System.IO.File.ReadAllText(HandshakePath).Contains(BootId, System.StringComparison.Ordinal))
			{
				System.IO.File.Delete(HandshakePath);
			}
		}
		catch (System.Exception)
		{
			// A stale handshake is survivable: the client checks the pid is alive.
		}
	}

	public override void _Process(double delta)
	{
		using (Refii.Core.Engine_FramePhases.Measure("gamedebugserver"))
		{
			ProcessBody(delta);
		}
	}

	private void ProcessBody(double delta)
	{
		if (_server is null)
		{
			return;
		}

		while (_server.IsConnectionAvailable())
		{
			_clients.Add(_server.TakeConnection());
		}

		if (!_arenaPublished)
		{
			Publish();
		}

		RecordFlight(delta);

		ServeWaits();

		for (int i = _clients.Count - 1; i >= 0; --i)
		{
			StreamPeerTcp client = _clients[i];
			client.Poll();
			if (client.GetStatus() != StreamPeerTcp.Status.Connected)
			{
				_clients.RemoveAt(i);
				continue;
			}

			int available = client.GetAvailableBytes();
			if (available <= 0)
			{
				continue;
			}

			string text = Encoding.UTF8.GetString(client.GetData(available)[1].AsByteArray());
			foreach (string line in text.Split('\n'))
			{
				string request = line.Trim();
				if (request.Length != 0)
				{
					Respond(client, request);
				}
			}
		}
	}


	/// <summary>rex XObject::Type, by value — the names the `objects` op renders.</summary>
	private static readonly string[] KernelObjectTypes =
	{
		"Undefined", "Enumerator", "Event", "File", "IOCompletion", "Module", "Mutant",
		"NotifyListener", "Semaphore", "Session", "Socket", "SymbolicLink", "Thread", "Timer",
	};

	/// <summary>rex cvar FlagType and Lifecycle, by value.</summary>
	private static readonly string[] CvarTypes =
	{
		"bool", "int32", "int64", "uint32", "uint64", "double", "string", "command",
	};

	private static readonly string[] CvarLifecycles = { "init-only", "hot-reload", "requires-restart" };

	/// <summary>The marker a deferred op returns: no reply now, ServeWaits sends it later.</summary>
	private static readonly Dictionary<string, object?> Deferred = new() { ["__deferred"] = true };

	private void Respond(StreamPeerTcp client, string request)
	{
		string reply;
		try
		{
			Dictionary<string, object?> answered = Answer(client, request);
			if (ReferenceEquals(answered, Deferred))
			{
				return;
			}

			reply = JsonSerializer.Serialize(answered);
		}
		catch (System.Exception error)
		{
			// A bad request must never take the game down with it — this is a debug channel.
			reply = JsonSerializer.Serialize(new Dictionary<string, object?>
			{
				["ok"] = false,
				["error"] = error.Message,
			});
		}

		client.PutData(Encoding.UTF8.GetBytes(reply + "\n"));
	}

	/// <summary>One request: `{"op": "...", ...}`. Unknown ops list what there is.</summary>
	private Dictionary<string, object?> Answer(StreamPeerTcp client, string request)
	{
		using JsonDocument document = JsonDocument.Parse(request);
		JsonElement root = document.RootElement;
		string op = root.TryGetProperty("op", out JsonElement opValue)
			? opValue.GetString() ?? string.Empty
			: string.Empty;

		switch (op)
		{
			case "ping":
			{
				var pong = new Dictionary<string, object?> { ["guest"] = HostBridgeUp() };
				if (Refii.Host.HostBridge.GetGuestArenaInfo() is { } arena)
				{
					pong["arena"] = arena.Name;
					pong["membase"] = arena.Membase;
				}

				return Ok(pong);
			}

			case "state":
				return Ok(State());

			case "rows":
				return Ok(Rows());

			case "frontend":
				return Ok(FrontEnd());

			case "wait":
			{
				// THE WAIT BELONGS TO THE GAME, NOT TO A SHELL LOOP. Driving through the
				// channel kept regressing into scripted sleeps because boot, load and settle
				// take minutes — and those blind sequences pressed into gameplay more than
				// once. The game knows its own state; a caller names the condition and this
				// answers when it holds, so one call replaces a polling loop that could not
				// see what it was waiting for.
				string until = root.TryGetProperty("for", out JsonElement f)
					? f.GetString() ?? string.Empty
					: string.Empty;
				double timeout = root.TryGetProperty("timeout", out JsonElement t)
					? t.GetDouble()
					: 120.0;
				uint wantScreen = root.TryGetProperty("screen", out JsonElement w)
					? (uint)w.GetInt64()
					: 0u;
				// Null means DEFERRED: the reply goes out from ServeWaits when the condition
				// holds. Signalled upward with a marker the responder recognises.
				string logMatch = root.TryGetProperty("match", out JsonElement lm)
					? lm.GetString() ?? string.Empty
					: string.Empty;
				return Wait(client, until, wantScreen, timeout, logMatch) ?? Deferred;
			}

			case "peek":
			{
				// Raw guest words. The microscope: a pointer chain's next hop, without another
				// native rebuild. Loopback-only like everything here; read-only by construction.
				uint addr = root.TryGetProperty("addr", out JsonElement a)
					? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
					: 0u;
				uint count = root.TryGetProperty("words", out JsonElement n)
					? (uint)n.GetInt64()
					: 8u;
				uint[] words = Refii.Host.HostBridge.Peek(addr, count);
				var hex = new List<string>(words.Length);
				foreach (uint word in words)
				{
					hex.Add($"{word:X8}");
				}

				return Ok(new Dictionary<string, object?>
				{
					["addr"] = $"{addr:X8}",
					["words"] = hex,
				});
			}

			case "gdb":
			{
				// A GDB record by hash, named and with its fields, from every database the
				// equipment indexes hold. What a guest-stated hash IS, without a rebuild.
				uint hash = root.TryGetProperty("hash", out JsonElement h)
					? System.Convert.ToUInt32(h.GetString() ?? "0", 16)
					: 0u;
				// `name` searches every database's record names (case-insensitive substring)
				// and answers with the matches, hash and name, instead of one record's fields.
				if (root.TryGetProperty("name", out JsonElement nm) && nm.ValueKind == JsonValueKind.String)
				{
					string wanted = nm.GetString() ?? string.Empty;
					var matches = new List<string>();
					if (Refii.Gdb.Engine_GdbCatalog.All is { } catalog)
					{
						foreach (Refii.Gdb.GdbView view in catalog.Views)
						{
							foreach ((uint recordHash, string recordName) in view.RecordNames)
							{
								if (recordName.Contains(wanted, System.StringComparison.OrdinalIgnoreCase) && matches.Count < 200)
								{
									matches.Add($"{recordHash:X8} {recordName}");
								}
							}
						}
					}

					return Ok(new Dictionary<string, object?> { ["name"] = wanted, ["matches"] = matches });
				}

				var found = new List<object?>();
				var databases = new List<(string, Refii.Gdb.GdbMulti?)>
				{
					("all", Refii.Gdb.Engine_GdbCatalog.All),
					("clothing", Refii.Equipment.Engine_ClothingIndex.Database),
					("items", Refii.Equipment.Engine_ItemIndex.Database),
				};
				foreach ((string label, Refii.Gdb.GdbMulti? multi) in databases)
				{
					if (multi is null || !multi.TryLookup(hash, out Refii.Gdb.GdbCursor cursor))
					{
						continue;
					}

					var fields = new List<string>();
					foreach ((uint fieldHash, Refii.Gdb.GdbFieldType type, uint raw) in multi.InheritedFields(cursor))
					{
						string fieldName = multi.NameOf(fieldHash);
						string value = type is Refii.Gdb.GdbFieldType.Reference or Refii.Gdb.GdbFieldType.ResourceReference
							? $"{raw:X8} '{multi.NameOf(raw)}'"
							: $"{raw:X8}";
						fields.Add($"{(fieldName.Length != 0 ? fieldName : fieldHash.ToString("X8"))} [{type}] = {value}");
					}

					found.Add(new Dictionary<string, object?>
					{
						["database"] = label,
						["name"] = multi.NameOf(hash),
						["fields"] = fields,
					});
				}

				Refii.Equipment.ItemDetail? detail = Refii.Entities.Engine_HiddenClusters.DetailFor(hash);
				return Ok(new Dictionary<string, object?>
				{
					["hash"] = $"{hash:X8}",
					["records"] = found,
					["clustersCovered"] = detail?.ClustersCovered,
					["femaleClustersCovered"] = detail?.FemaleClustersCovered,
					["openClustersCovered"] = detail?.OpenClustersCovered,
					["bodyAreasCovered"] = detail is null ? null : $"{detail.BodyAreasCovered:X8}",
					["wornModel"] = detail?.WornModelPath,
				});
			}

			case "threads":
			{
				// Every kernel-registered thread with a guest backtrace, innermost first —
				// the freeze instrument. Raw addresses, hex: IDA owns naming them.
				bool withBacktrace = !root.TryGetProperty("bt", out JsonElement b) || b.GetBoolean();
				var threads = new List<Dictionary<string, object?>>();
				foreach (Refii.Host.HostBridge.GuestThreadInfo info in
					Refii.Host.HostBridge.GetGuestThreads())
				{
					var entry = new Dictionary<string, object?>
					{
						["tid"] = info.Tid,
						["name"] = info.Name,
						["guest"] = info.IsGuestThread,
						["main"] = info.IsMainThread,
						["running"] = info.Running,
						["priority"] = info.Priority,
						["cpu"] = info.Cpu,
						["r1"] = $"{info.R1:X8}",
						["lr"] = $"{info.Lr:X8}",
					};
					if (withBacktrace && info.IsGuestThread)
					{
						var frames = new List<string>();
						foreach (uint frame in Refii.Host.HostBridge.GetGuestBacktrace(info.Tid))
						{
							frames.Add($"{frame:X8}");
						}

						entry["backtrace"] = frames;
					}

					threads.Add(entry);
				}

				return Ok(new Dictionary<string, object?> { ["threads"] = threads });
			}

			case "objects":
			{
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				string wantType = root.TryGetProperty("type", out JsonElement ty)
					? ty.GetString() ?? string.Empty
					: string.Empty;
				var objects = new List<Dictionary<string, object?>>();
				List<Refii.Host.HostBridge.KernelObjectInfo> table =
					Refii.Host.HostBridge.GetKernelObjects(out uint total);
				foreach (Refii.Host.HostBridge.KernelObjectInfo info in table)
				{
					string typeName = info.Type < (uint)KernelObjectTypes.Length
						? KernelObjectTypes[info.Type]
						: info.Type.ToString();
					if (wantType.Length != 0 &&
						!typeName.Equals(wantType, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					if (match.Length != 0 &&
						!info.Name.Contains(match, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					objects.Add(new Dictionary<string, object?>
					{
						["handle"] = $"{info.Handle:X8}",
						["type"] = typeName,
						["name"] = info.Name,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["total"] = total,
					["listed"] = objects.Count,
					["objects"] = objects,
				});
			}

			case "perf":
			{
				// LIVE values: run totals and gauges, never per-frame rates — the snapshot
				// rotation lives in the compiled-out renderer. Rates are deltas between reads.
				var counters = new Dictionary<string, object?>();
				foreach ((string name, long value) in Refii.Host.HostBridge.GetPerfCounters())
				{
					counters[name] = value;
				}

				// With FrameMark running each host frame, snapshots mean "last frame".
				var lastFrame = new Dictionary<string, object?>();
				foreach ((string name, long value) in Refii.Host.HostBridge.GetPerfSnapshots())
				{
					lastFrame[name] = value;
				}

				return Ok(new Dictionary<string, object?>
				{
					["counters"] = counters,
					["lastFrame"] = lastFrame,
				});
			}

			case "exports":
			{
				bool unimplemented = root.TryGetProperty("unimplemented", out JsonElement u) &&
					u.GetBoolean();
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				int limit = root.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : 200;
				var exports = new List<Dictionary<string, object?>>();
				uint total = 0;
				for (uint first = 0; exports.Count < limit;)
				{
					List<Refii.Host.HostBridge.KernelExportInfo> page =
						Refii.Host.HostBridge.GetKernelExports(first, unimplemented, out total);
					if (page.Count == 0)
					{
						break;
					}

					foreach (Refii.Host.HostBridge.KernelExportInfo info in page)
					{
						if (exports.Count >= limit)
						{
							break;
						}

						if (match.Length != 0 && !info.Name.Contains(
								match, System.StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}

						exports.Add(new Dictionary<string, object?>
						{
							["name"] = info.Name,
							["ordinal"] = info.Ordinal,
							["implemented"] = info.Implemented,
							["variable"] = info.IsVariable,
						});
					}

					first += (uint)page.Count;
					if (first >= total)
					{
						break;
					}
				}

				return Ok(new Dictionary<string, object?>
				{
					["total"] = total,
					["listed"] = exports.Count,
					["exports"] = exports,
				});
			}

			case "cvars":
			{
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				var cvars = new List<Dictionary<string, object?>>();
				int count = Refii.Host.HostBridge.GetCvarCount();
				for (int i = 0; i < count; ++i)
				{
					if (Refii.Host.HostBridge.GetCvar(i) is not { } info)
					{
						continue;
					}

					if (match.Length != 0 &&
						!info.Name.Contains(match, System.StringComparison.OrdinalIgnoreCase) &&
						!info.Category.Contains(match, System.StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}

					cvars.Add(new Dictionary<string, object?>
					{
						["name"] = info.Name,
						["value"] = info.Value,
						["default"] = info.Default,
						["category"] = info.Category,
						["type"] = info.Type < (uint)CvarTypes.Length
							? CvarTypes[info.Type]
							: info.Type.ToString(),
						["lifecycle"] = info.Lifecycle < (uint)CvarLifecycles.Length
							? CvarLifecycles[info.Lifecycle]
							: info.Lifecycle.ToString(),
						["modified"] = info.Modified,
						["debugOnly"] = info.DebugOnly,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["count"] = cvars.Count,
					["cvars"] = cvars,
				});
			}

			case "cvar-set":
			{
				string name = root.TryGetProperty("name", out JsonElement n)
					? n.GetString() ?? string.Empty
					: string.Empty;
				string value = root.TryGetProperty("value", out JsonElement v)
					? v.GetString() ?? string.Empty
					: string.Empty;
				if (name.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "cvar-set needs a name",
					};
				}

				// The registry's own validator answers — lifecycle, range, allowed values.
				bool accepted = Refii.Host.HostBridge.SetCvar(name, value);
				return Ok(new Dictionary<string, object?>
				{
					["name"] = name,
					["accepted"] = accepted,
				});
			}

			case "vfs":
			{
				string path = root.TryGetProperty("path", out JsonElement p)
					? p.GetString() ?? string.Empty
					: string.Empty;
				if (path.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "vfs needs a path",
					};
				}

				if (Refii.Host.HostBridge.VfsResolve(path) is not { } resolved)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "no VFS — the guest is not up",
					};
				}

				return Ok(new Dictionary<string, object?>
				{
					["path"] = path,
					["found"] = resolved.Found,
					["rootIsLink"] = resolved.RootIsLink,
					["absolute"] = resolved.AbsolutePath,
					["linkTarget"] = resolved.LinkTarget,
					["size"] = resolved.Size,
					["attributes"] = $"{resolved.Attributes:X}",
				});
			}

			case "sockets":
			{
				var sockets = new List<Dictionary<string, object?>>();
				foreach ((uint handle, uint port, ulong native) in
					Refii.Host.HostBridge.GetGuestSockets())
				{
					sockets.Add(new Dictionary<string, object?>
					{
						["handle"] = $"{handle:X8}",
						["boundPort"] = port,
						["nativeHandle"] = native,
					});
				}

				return Ok(new Dictionary<string, object?> { ["sockets"] = sockets });
			}

			case "menu":
			{
				// The guest's own menu verbs, not synthesized pad input — the stick latch and
				// edge detection stay the guest's problem.
				string verb = root.TryGetProperty("verb", out JsonElement v)
					? v.GetString() ?? string.Empty
					: string.Empty;
				bool held = root.TryGetProperty("held", out JsonElement h) && h.GetBoolean();
				uint result;
				switch (verb)
				{
					case "up":
						result = Refii.Host.HostBridge.MenuScroll(
							Engine_UiModel.MenuStick.Up, held);
						break;
					case "down":
						result = Refii.Host.HostBridge.MenuScroll(
							Engine_UiModel.MenuStick.Down, held);
						break;
					case "left":
						result = Refii.Host.HostBridge.MenuScroll(
							Engine_UiModel.MenuStick.Left, held);
						break;
					case "right":
						result = Refii.Host.HostBridge.MenuScroll(
							Engine_UiModel.MenuStick.Right, held);
						break;
					case "select":
						result = Refii.Host.HostBridge.MenuSelect();
						break;
					case "action":
					{
						uint code = root.TryGetProperty("code", out JsonElement c)
							? (uint)c.GetInt64()
							: 36u;
						bool atRoot = root.TryGetProperty("atRoot", out JsonElement r2) &&
							r2.GetBoolean();
						result = Refii.Host.HostBridge.ScreenAction(code, atRoot);
						break;
					}
					case "event":
					{
						string name = root.TryGetProperty("name", out JsonElement n)
							? n.GetString() ?? string.Empty
							: string.Empty;
						int type = root.TryGetProperty("type", out JsonElement t2)
							? t2.GetInt32()
							: 0;
						uint id = root.TryGetProperty("id", out JsonElement i2)
							? (uint)i2.GetInt64()
							: 0u;
						result = Refii.Host.HostBridge.MenuEvent(name, type, id);
						break;
					}
					default:
						return new Dictionary<string, object?>
						{
							["ok"] = false,
							["error"] = "verb must be up/down/left/right/select/action/event",
						};
				}

				return Ok(new Dictionary<string, object?> { ["verb"] = verb, ["result"] = result });
			}

			case "action":
			{
				string name = root.TryGetProperty("name", out JsonElement n)
					? n.GetString() ?? string.Empty
					: string.Empty;
				if (name.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "action needs a name",
					};
				}

				return Ok(new Dictionary<string, object?>
				{
					["queued"] = Refii.Host.HostBridge.PostFrontEndAction(name),
				});
			}

			case "call":
			{
				// debug-call on the guest's tick thread; the caller polls call-result until
				// seq passes the one returned here.
				string addr = root.TryGetProperty("addr", out JsonElement a)
					? a.GetString() ?? string.Empty
					: string.Empty;
				var pieces = new List<string> { addr };
				if (root.TryGetProperty("args", out JsonElement args))
				{
					foreach (JsonElement arg in args.EnumerateArray())
					{
						pieces.Add(arg.ValueKind == JsonValueKind.String
							? arg.GetString() ?? "0"
							: ((ulong)arg.GetInt64()).ToString("X"));
					}
				}

				ulong before = Refii.Host.HostBridge.DebugCallResult()?.Seq ?? 0;
				uint queued = Refii.Host.HostBridge.PostFrontEndAction(
					"debug-call:" + string.Join(":", pieces));
				return Ok(new Dictionary<string, object?>
				{
					["queued"] = queued, ["seqBefore"] = before,
				});
			}

			case "lua":
			{
				string chunk = root.TryGetProperty("chunk", out JsonElement c)
					? c.GetString() ?? string.Empty
					: string.Empty;
				if (chunk.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "lua needs a chunk",
					};
				}

				ulong before = Refii.Host.HostBridge.DebugCallResult()?.Seq ?? 0;
				uint queued = Refii.Host.HostBridge.PostFrontEndAction("debug-lua:" + chunk);
				return Ok(new Dictionary<string, object?>
				{
					["queued"] = queued, ["seqBefore"] = before,
				});
			}

			case "call-result":
			{
				if (Refii.Host.HostBridge.DebugCallResult() is not { } result)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "no bridge",
					};
				}

				return Ok(new Dictionary<string, object?>
				{
					["seq"] = result.Seq,
					["addr"] = $"{result.Addr:X8}",
					["r3"] = $"{result.R3:X}",
					["note"] = result.Note,
				});
			}

			case "poke":
			{
				uint addr = root.TryGetProperty("addr", out JsonElement a)
					? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
					: 0u;
				uint value = root.TryGetProperty("value", out JsonElement v)
					? System.Convert.ToUInt32(v.GetString() ?? "0", 16)
					: 0u;
				bool wrote = Refii.Host.HostBridge.Poke(addr, value);
				return Ok(new Dictionary<string, object?>
				{
					["addr"] = $"{addr:X8}",
					["wrote"] = wrote,
					["refused"] = wrote ? null : "page is untracked or not writable",
				});
			}

			case "heap":
			{
				uint addr = root.TryGetProperty("addr", out JsonElement a)
					? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
					: 0u;
				if (Refii.Host.HostBridge.HeapLookup(addr) is not { } info)
				{
					return Ok(new Dictionary<string, object?>
					{
						["addr"] = $"{addr:X8}", ["heap"] = false,
					});
				}

				return Ok(new Dictionary<string, object?>
				{
					["addr"] = $"{addr:X8}",
					["heap"] = true,
					["heapBase"] = $"{info.HeapBase:X8}",
					["heapSize"] = info.HeapSize,
					["pageSize"] = info.PageSize,
					["protect"] = info.Protect,
					["writable"] = (info.Protect & 2u) != 0,
				});
			}

			case "probe":
			{
				string what = root.TryGetProperty("do", out JsonElement d)
					? d.GetString() ?? string.Empty
					: string.Empty;
				if (what == "set")
				{
					uint addr = root.TryGetProperty("addr", out JsonElement a)
						? System.Convert.ToUInt32(a.GetString() ?? "0", 16)
						: 0u;
					int slot = Refii.Host.HostBridge.ProbeSet(addr);
					return Ok(new Dictionary<string, object?>
					{
						["slot"] = slot,
						["error"] = slot switch
						{
							-1 => "all 16 slots armed — clear one",
							-2 => "not a recompiled function start",
							-3 => "guest not up",
							_ => null,
						},
					});
				}

				if (what == "clear")
				{
					int slot = root.TryGetProperty("slot", out JsonElement s)
						? s.GetInt32()
						: -1;
					return Ok(new Dictionary<string, object?>
					{
						["cleared"] = Refii.Host.HostBridge.ProbeClear(slot),
					});
				}

				// read / list
				var probes = new List<Dictionary<string, object?>>();
				for (int slot = 0; slot < 16; ++slot)
				{
					if (Refii.Host.HostBridge.ProbeRead(slot) is { } state && state.Addr != 0)
					{
						var argsHex = new List<string>();
						foreach (ulong arg in state.LastArgs)
						{
							argsHex.Add($"{arg:X}");
						}

						probes.Add(new Dictionary<string, object?>
						{
							["slot"] = slot,
							["addr"] = $"{state.Addr:X8}",
							["hits"] = state.Hits,
							["lastLr"] = $"{state.LastLr:X8}",
							["lastArgs"] = argsHex,
						});
					}
				}

				return Ok(new Dictionary<string, object?> { ["probes"] = probes });
			}

			case "guestlog":
			{
				// The EMBEDDED guest's own segmented logs beside the executable — the ones the
				// usual location does not hold. `which` = guest (refii_*.log*) or launcher.
				string which = root.TryGetProperty("which", out JsonElement w)
					? w.GetString() ?? "guest"
					: "guest";
				int lines = root.TryGetProperty("lines", out JsonElement n) ? n.GetInt32() : 40;
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				string logs = System.IO.Path.Combine(
					System.IO.Path.GetDirectoryName(OS.GetExecutablePath())!, "logs");
				string[] files;
				try
				{
					files = which == "launcher"
						? System.IO.Directory.GetFiles(logs, "refii-launcher.log*")
						: System.IO.Directory.GetFiles(logs, "refii_*.log*");
				}
				catch (System.Exception error)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = error.Message, ["dir"] = logs,
					};
				}

				// Newest FILE first, then its segments oldest-to-newest within the tail.
				System.Array.Sort(files, (x, y) =>
					System.IO.File.GetLastWriteTimeUtc(y)
						.CompareTo(System.IO.File.GetLastWriteTimeUtc(x)));
				var kept = new List<string>();
				foreach (string file in files)
				{
					if (kept.Count >= lines)
					{
						break;
					}

					kept.AddRange(TailOf(file, lines - kept.Count, match));
				}

				return Ok(new Dictionary<string, object?> { ["dir"] = logs, ["lines"] = kept });
			}

			case "mark":
			{
				string text = root.TryGetProperty("text", out JsonElement t)
					? t.GetString() ?? string.Empty
					: string.Empty;
				GD.Print($"[mark] {text}");
				Refii.Host.HostBridge.TraceMessage($"mark: {text}");
				return Ok(new Dictionary<string, object?> { ["marked"] = text });
			}

			case "lights":
			{
				// THE NEAREST PLACED LIGHTS, as Godot holds them: what a fixture states and
				// whether it is visible, so "the lamp lights nothing" is answered by its numbers
				// (range, energy, fade) rather than by a screenshot.
				int take = root.TryGetProperty("count", out JsonElement lc) && lc.ValueKind == JsonValueKind.Number
					? System.Math.Clamp(lc.GetInt32(), 1, 64) : 12;
				var snapshot = new List<Light3D>();
				Refii.Lighting.Engine_LevelLights.SnapshotLights(snapshot);
				Vector3 eye = GetViewport()?.GetCamera3D()?.GlobalPosition ?? Vector3.Zero;
				var rows = new List<(float Distance, Light3D Light)>();
				foreach (Light3D light in snapshot)
				{
					if (light.IsInsideTree())
					{
						rows.Add((eye.DistanceTo(light.GlobalPosition), light));
					}
				}

				rows.Sort((a, b) => a.Distance.CompareTo(b.Distance));
				var described = new List<string>();
				foreach ((float distance, Light3D light) in rows.GetRange(0, System.Math.Min(take, rows.Count)))
				{
					float range = light is OmniLight3D omni ? omni.OmniRange : light is SpotLight3D spot ? spot.SpotRange : 0f;
					Vector3 p = light.GlobalPosition;
					described.Add(
						$"{light.Name} d={distance:0.0} at=({p.X:0.0},{p.Y:0.0},{p.Z:0.0}) range={range:0.0} " +
						$"energy={light.LightEnergy:0.00} colour=({light.LightColor.R:0.00},{light.LightColor.G:0.00},{light.LightColor.B:0.00}) " +
						$"fade={(light.DistanceFadeEnabled ? $"{light.DistanceFadeBegin:0}+{light.DistanceFadeLength:0}" : "off")} " +
						$"visible={light.Visible} shadow={light.ShadowEnabled}");
				}

				return Ok(new Dictionary<string, object?>
				{
					["total"] = snapshot.Count,
					["eye"] = $"({eye.X:0.0},{eye.Y:0.0},{eye.Z:0.0})",
					["lights"] = described,
				});
			}

			case "render":
			{
				// LIVE LEVERS for bisecting a frame: each is optional; the reply is the state.
				if (root.TryGetProperty("shadowBudget", out JsonElement sb) && sb.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LightShadowBudget.Budget = sb.GetInt32();
				}

				if (root.TryGetProperty("lightBudget", out JsonElement lb) && lb.ValueKind == JsonValueKind.Number)
				{
					Refii.Lighting.Engine_LightShadowBudget.LightBudget = lb.GetInt32();
				}

				if (root.TryGetProperty("sunSplits", out JsonElement sp))
				{
					Refii.Lighting.Engine_LevelLighting.SunSplitsOverride = sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : null;
				}

				if (root.TryGetProperty("sunShadow", out JsonElement ss))
				{
					Refii.Lighting.Engine_LevelLighting.SunShadowOverride = ss.ValueKind == JsonValueKind.Null ? null : ss.GetBoolean();
				}

				if (root.TryGetProperty("reflections", out JsonElement rf) && rf.ValueKind != JsonValueKind.Null)
				{
					Refii.Level.Engine_WaterReflection.Enabled = rf.GetBoolean();
				}

				if (root.TryGetProperty("reflectionCadence", out JsonElement rc) && rc.ValueKind == JsonValueKind.Number)
				{
					Refii.Level.Engine_WaterReflection.Cadence = rc.GetInt32();
				}

				if (root.TryGetProperty("fx", out JsonElement fx) && fx.ValueKind != JsonValueKind.Null)
				{
					Refii.Fx.Engine_FxDisplacement.Current?.SetEnabled(fx.GetBoolean());
				}

				if (root.TryGetProperty("feedEveryFrame", out JsonElement fe) && fe.ValueKind != JsonValueKind.Null)
				{
					GameEntities.FeedEveryFrame = fe.GetBoolean();
				}

				if (root.TryGetProperty("entities", out JsonElement en) && en.ValueKind != JsonValueKind.Null)
				{
					if (GameEntities.Current is { } entities)
					{
						entities.Visible = en.GetBoolean();
					}
				}

				if (root.TryGetProperty("uiViewport", out JsonElement uv) && uv.ValueKind != JsonValueKind.Null)
				{
					foreach (Node found in GetTree().Root.FindChildren("UiViewport", "SubViewport", true, false))
					{
						if (found is SubViewport ui)
						{
							ui.RenderTargetUpdateMode = uv.GetBoolean() ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
						}
					}
				}

				if (root.TryGetProperty("lights", out JsonElement li) && li.ValueKind != JsonValueKind.Null)
				{
					Refii.Lighting.Engine_LightShadowBudget.LightsEnabled = li.GetBoolean();
				}

				if (root.TryGetProperty("shadowReach", out JsonElement sr) && sr.ValueKind != JsonValueKind.Null)
				{
					Refii.Lighting.Engine_ShadowReach.Enabled = sr.GetBoolean();
				}

				return Ok(new Dictionary<string, object?>
				{
					["shadowReach"] = Refii.Lighting.Engine_ShadowReach.Enabled,
					["shadowSuppressed"] = Refii.Lighting.Engine_ShadowReach.Suppressed,
					["shadowConsidered"] = Refii.Lighting.Engine_ShadowReach.Considered,
					["shadowBudget"] = Refii.Lighting.Engine_LightShadowBudget.Budget,
					["lightBudget"] = Refii.Lighting.Engine_LightShadowBudget.LightBudget,
					["hero"] = GameEntities.HeroPosition is { } hero
						? $"({hero.X:0.0}, {hero.Y:0.0}, {hero.Z:0.0})" : null,
					["heroLocal"] = GameEntities.HeroPosition is { } heroWorld &&
						Refii.Level.Engine_SkyEnvironmentNode.Current?.GetParent() is Node3D region
						? region.GlobalTransform.AffineInverse() * heroWorld is { } local
							? $"({local.X:0.0}, {local.Y:0.0}, {local.Z:0.0})" : null
						: null,
					["painted"] = Refii.Level.Engine_SkyEnvironmentNode.Current?.Painted?.Describe(),
					["paintedWeight"] = Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting?.PaintedWeight,
					["sunShadow"] = Refii.Lighting.Engine_LevelLighting.SunShadowOverride,
					["sunSplits"] = Refii.Lighting.Engine_LevelLighting.SunSplitsOverride,
					["reflections"] = Refii.Level.Engine_WaterReflection.Enabled,
					["reflectionCadence"] = Refii.Level.Engine_WaterReflection.Cadence,
				});
			}

			case "entity":
			{
				// One drawn entity, as the feed sees it: stated parts with their model names, the
				// rig's bone mapping, and the skeleton's current hold state.
				uint address = root.TryGetProperty("addr", out JsonElement ad) && ad.ValueKind == JsonValueKind.String
					? System.Convert.ToUInt32(ad.GetString(), 16)
					: 0u;
				if (address == 0u)
				{
					return Ok(new Dictionary<string, object?> { ["animators"] = GameEntities.Current?.DescribeAnimators() });
				}
				uint[] morph = Refii.Host.HostBridge.GetEntityMorphParts(address, out int built);
				uint[] meshes = Refii.Host.HostBridge.GetEntityMeshParts(address, out int total);
				var parts = new List<object?>();
				foreach (uint part in morph.Concat(meshes))
				{
					parts.Add(new Dictionary<string, object?>
					{
						["hash"] = $"{part:X8}",
						["path"] = Refii.Level.Engine_PropModelIndex.Resolve(part, System.Array.Empty<string>()),
					});
				}

				if (root.TryGetProperty("trace", out JsonElement traceFrames) &&
					traceFrames.ValueKind == JsonValueKind.Number)
				{
					GameEntities.Current?.BeginTrace(address, traceFrames.GetInt32());
					return Ok(new Dictionary<string, object?> { ["tracing"] = $"{address:X8}" });
				}

				if (root.TryGetProperty("traceReport", out JsonElement wantTrace) &&
					wantTrace.ValueKind == JsonValueKind.True)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["trace"] = GameEntities.Current?.TraceReport(),
					});
				}

				if (root.TryGetProperty("bones", out JsonElement wantBones) &&
					wantBones.ValueKind == JsonValueKind.True)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["rig"] = GameEntities.Current?.DescribeBones(address),
					});
				}

				if (root.TryGetProperty("surfaces", out JsonElement wantSurfaces) &&
					wantSurfaces.ValueKind == JsonValueKind.True)
				{
					return Ok(new Dictionary<string, object?>
					{
						["address"] = $"{address:X8}",
						["surfaces"] = GameEntities.Current?.DescribeSurfaces(address),
					});
				}

				Refii.Host.HostBridge.BonePose? pose = Refii.Host.HostBridge.GetEntityPose(address);
				return Ok(new Dictionary<string, object?>
				{
					["address"] = $"{address:X8}",
					["morphBuilt"] = built,
					["meshTotal"] = total,
					["parts"] = parts,
					["attachments"] = GameEntities.Current?.DescribeAttachments(address),
					["poseBones"] = pose?.BoneCount,
					["poseSpace"] = pose?.Space,
					["poseTorn"] = pose?.Torn,
					["rig"] = GameEntities.Current?.DescribeRig(address),
					["leaves"] = GameEntities.Current?.DescribeLeaves(address),
				});
			}

			case "metrics":
			{
				// The HOST's own cost, which the guest counters cannot see. The viewport's own
				// CPU/GPU render measurement is switched on at the first ask and stays on; the
				// visible/shadow split says whether shadow passes are the multiplier.
				Viewport? viewport = GetViewport();
				Rid viewportRid = viewport?.GetViewportRid() ?? default;
				double renderCpuMs = 0, renderGpuMs = 0;
				double visibleCalls = 0, shadowCalls = 0, visiblePrims = 0, shadowPrims = 0, visibleObjects = 0, shadowObjects = 0;
				if (viewport is not null)
				{
					RenderingServer.ViewportSetMeasureRenderTime(viewportRid, true);
					renderCpuMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewportRid);
					renderGpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewportRid);
					visibleCalls = RenderingServer.ViewportGetRenderInfo(viewportRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
					shadowCalls = RenderingServer.ViewportGetRenderInfo(viewportRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame);
					visiblePrims = RenderingServer.ViewportGetRenderInfo(viewportRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame);
					shadowPrims = RenderingServer.ViewportGetRenderInfo(viewportRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.PrimitivesInFrame);
					visibleObjects = RenderingServer.ViewportGetRenderInfo(viewportRid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame);
					shadowObjects = RenderingServer.ViewportGetRenderInfo(viewportRid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.ObjectsInFrame);
				}

				// EVERY viewport, because the process total is far more than the main one draws.
				var viewports = new List<object?>();
				foreach (Node found in GetTree().Root.FindChildren("*", "SubViewport", true, false))
				{
					if (found is not SubViewport sub)
					{
						continue;
					}

					Rid rid = sub.GetViewportRid();
					RenderingServer.ViewportSetMeasureRenderTime(rid, true);
					viewports.Add(new Dictionary<string, object?>
					{
						["path"] = sub.GetPath().ToString(),
						["size"] = $"{sub.Size.X}x{sub.Size.Y}",
						["update"] = sub.RenderTargetUpdateMode.ToString(),
						["own3d"] = sub.OwnWorld3D,
						["cpuMs"] = RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid),
						["gpuMs"] = RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid),
						["visibleCalls"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
						["visiblePrims"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame),
						["shadowCalls"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.DrawCallsInFrame),
						["shadowPrims"] = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Shadow, RenderingServer.ViewportRenderInfo.PrimitivesInFrame),
					});
				}

				// WHAT ELSE RUNS ON THE MAIN THREAD: node classes with process, physics process or
				// INTERNAL process enabled — Skeleton3D, AnimationPlayer and particles do their
				// per-frame work as internal process, which no callback of ours can time.
				var census = new Dictionary<string, int>();
				foreach (Node found in GetTree().Root.FindChildren("*", "", true, false))
				{
					string kind = found.IsProcessingInternal() ? "internal:" : found.IsProcessing() ? "process:" : found.IsPhysicsProcessing() ? "physics:" : string.Empty;
					if (kind.Length == 0)
					{
						continue;
					}

					string key = kind + found.GetType().Name;
					census[key] = census.TryGetValue(key, out int have) ? have + 1 : 1;
				}

				var lights = new List<Light3D>();
				Refii.Lighting.Engine_LevelLights.SnapshotLights(lights);
				int shadowLights = lights.Count(l => l.ShadowEnabled);

				return Ok(new Dictionary<string, object?>
				{
					["phases"] = Refii.Core.Engine_FramePhases.Report(),
					["processing"] = census,
					["feedFramesSkipped"] = GameEntities.FeedFramesSkipped,
					["fxFrozen"] = Refii.Fx.FableFx.FrozenThisFrame,
					["poses"] = new Dictionary<string, object?>
					{
						["heldLocal"] = Refii.Entities.Engine_EntitySkeleton.LocalPosesHeld,
						["stuckLocalRigs"] = Refii.Entities.Engine_EntitySkeleton.LocalStuckRigs,
						["mixedHeld"] = Refii.Entities.Engine_EntitySkeleton.MixedPosesHeld,
						["flagLocalDataModel"] = Refii.Entities.Engine_EntitySkeleton.FlagLocalDataModel,
						["jumps"] = Refii.Entities.Engine_EntitySkeleton.PoseJumps,
						["changed"] = Refii.Entities.Engine_EntitySkeleton.PoseChanges,
						["applied"] = Refii.Entities.Engine_EntitySkeleton.PoseApplies,
						["implausible"] = Refii.Entities.Engine_EntitySkeleton.ImplausiblePoses,
						["torn"] = Refii.Entities.Engine_EntitySkeleton.TornPoses,
						["countMismatch"] = Refii.Entities.Engine_EntitySkeleton.CountMismatch,
						["neverPosed"] = Refii.Entities.Engine_EntitySkeleton.NeverPosed,
						["mostActive"] = GameEntities.Current?.MostActiveRigs(),
						["attachmentsPlaced"] = Refii.Entities.Engine_EntityAttachments.Placed,
						["attachmentsUnresolved"] = Refii.Entities.Engine_EntityAttachments.Unresolved,
						["attachmentsResolves"] = Refii.Entities.Engine_EntityAttachments.Resolves,
						["clipDrivenFrames"] = Refii.Animations.Engine_ClipAnimator.ClipDrivenFrames,
						["heldNoPrimary"] = Refii.Animations.Engine_ClipAnimator.HeldNoPrimaryFrames,
						["fallbackUnknownClip"] = Refii.Animations.Engine_ClipAnimator.FallbackUnknownClip,
						["fallbackProcedural"] = Refii.Animations.Engine_ClipAnimator.FallbackProcedural,
						["fallbackRagdoll"] = Refii.Animations.Engine_ClipAnimator.FallbackRagdoll,
						["fallbackNoLeaves"] = Refii.Animations.Engine_ClipAnimator.FallbackNoLeaves,
					},
					["frameSetupCpuMs"] = RenderingServer.GetFrameSetupTimeCpu(),
					["viewports"] = viewports,
					["renderCpuMs"] = renderCpuMs,
					["renderGpuMs"] = renderGpuMs,
					["visible"] = new Dictionary<string, object?> { ["drawCalls"] = visibleCalls, ["primitives"] = visiblePrims, ["objects"] = visibleObjects },
					["shadow"] = new Dictionary<string, object?> { ["drawCalls"] = shadowCalls, ["primitives"] = shadowPrims, ["objects"] = shadowObjects },
					["placedLights"] = lights.Count,
					["shadowLights"] = shadowLights,
					["fps"] = Performance.GetMonitor(Performance.Monitor.TimeFps),
					["frameMs"] = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0,
					["physicsMs"] =
						Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0,
					["drawCalls"] =
						Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame),
					["primitives"] =
						Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame),
					["videoMemMB"] =
						Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576.0,
					["nodes"] = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount),
					["staticMemMB"] =
						Performance.GetMonitor(Performance.Monitor.MemoryStatic) / 1048576.0,
				});
			}

			case "input-record":
			{
				string what = root.TryGetProperty("do", out JsonElement d)
					? d.GetString() ?? string.Empty
					: string.Empty;
				if (what == "start")
				{
					_recording = new List<PadFrame>();
					return Ok(new Dictionary<string, object?> { ["recording"] = true });
				}

				if (what == "stop")
				{
					List<PadFrame>? tape = _recording;
					_recording = null;
					if (tape is null || tape.Count == 0)
					{
						return new Dictionary<string, object?>
						{
							["ok"] = false, ["error"] = "nothing recorded",
						};
					}

					string path = root.TryGetProperty("path", out JsonElement p) &&
						p.GetString() is { Length: > 0 } given
							? given
							: ProjectSettings.GlobalizePath(
								$"user://logs/pad-{System.DateTime.Now:HHmmss}.jsonl");
					var text = new StringBuilder();
					foreach (PadFrame frame in tape)
					{
						text.AppendLine(JsonSerializer.Serialize(frame));
					}

					System.IO.File.WriteAllText(path, text.ToString());
					return Ok(new Dictionary<string, object?>
					{
						["frames"] = tape.Count, ["path"] = path,
					});
				}

				return new Dictionary<string, object?>
				{
					["ok"] = false, ["error"] = "do must be start or stop",
				};
			}

			case "input-replay":
			{
				string path = root.TryGetProperty("path", out JsonElement p)
					? p.GetString() ?? string.Empty
					: string.Empty;
				try
				{
					var frames = new List<PadFrame>();
					foreach (string line in System.IO.File.ReadLines(path))
					{
						if (line.Length != 0)
						{
							frames.Add(JsonSerializer.Deserialize<PadFrame>(line));
						}
					}

					_replay = frames.ToArray();
					_replayAt = 0;
					return Ok(new Dictionary<string, object?> { ["frames"] = frames.Count });
				}
				catch (System.Exception error)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = error.Message,
					};
				}
			}

			case "elements":
			{
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				int limit = root.TryGetProperty("limit", out JsonElement l) ? l.GetInt32() : 40;
				if (match.Length == 0)
				{
					return new Dictionary<string, object?>
					{
						["ok"] = false, ["error"] = "elements needs a match",
					};
				}

				// The RESOLVED state of matching elements across every live book — active,
				// drawn, opacity, node position. The ground truth a bound-but-ineffective
				// channel write is caught against.
				return Ok(new Dictionary<string, object?>
				{
					["elements"] = Engine_UiOwnedBook.DescribeLive(match, limit),
				});
			}

			case "log":
			{
				int lines = root.TryGetProperty("lines", out JsonElement n) ? n.GetInt32() : 40;
				string match = root.TryGetProperty("match", out JsonElement m)
					? m.GetString() ?? string.Empty
					: string.Empty;
				return Ok(Tail(lines, match));
			}

			case "hud":
			{
				string match = root.TryGetProperty("match", out JsonElement hm) ? hm.GetString() ?? string.Empty : string.Empty;
				Refii.Ui.Engine_UiModel.SpellAssignState spells = Refii.Host.HostBridge.GetSpellAssign();
				return Ok(new Dictionary<string, object?>
				{
					["pieces"] = Refii.Ui.Engine_HudView.Current?.Describe(match),
					["spell"] = new Dictionary<string, object?>
					{
						["known"] = spells.Known,
						["assigned"] = spells.Assigned,
						["offered"] = spells.Offered,
						["abilitiesMode"] = spells.AbilitiesMode,
						["wheelOpen"] = spells.WheelOpen,
					},
				});
			}

			case "camera":
			{
				GameWorld? world = GetTree().Root.FindChild("GameWorld", true, false) as GameWorld;
				if (root.TryGetProperty("trace", out JsonElement ct) && ct.ValueKind == JsonValueKind.Number)
				{
					world?.BeginCameraTrace(ct.GetInt32());
					return Ok(new Dictionary<string, object?> { ["tracing"] = world is not null });
				}

				return Ok(new Dictionary<string, object?> { ["trace"] = world?.CameraTraceReport() });
			}

			case "shot":
			{
				string path = root.TryGetProperty("path", out JsonElement p)
					? p.GetString() ?? string.Empty
					: string.Empty;

				// A CLOSE-UP: `addr` frames that entity in a private viewport. Call once with
				// `arm` to queue the render, then again with `path` a frame later to save it.
				if (root.TryGetProperty("addr", out JsonElement ca) && ca.ValueKind == JsonValueKind.String)
				{
					uint who = System.Convert.ToUInt32(ca.GetString(), 16);
					if (root.TryGetProperty("arm", out JsonElement arm) && arm.ValueKind == JsonValueKind.True)
					{
						string bone = root.TryGetProperty("bone", out JsonElement bn) ? bn.GetString() ?? "Shadow_Head" : "Shadow_Head";
						float distance = root.TryGetProperty("distance", out JsonElement di) ? (float)di.GetDouble() : 0.6f;
						int size = root.TryGetProperty("size", out JsonElement sz) ? sz.GetInt32() : 512;
						bool armed = GameEntities.Current?.ArmCloseup(who, bone, distance, size) ?? false;
						return Ok(new Dictionary<string, object?> { ["armed"] = armed });
					}

					Image? image = GameEntities.Current?.CloseupImage();
					if (image is null || path.Length == 0)
					{
						return Ok(new Dictionary<string, object?> { ["error"] = "not armed" });
					}

					System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
					return Ok(new Dictionary<string, object?>
					{
						["path"] = path,
						["saved"] = image.SavePng(path) == Error.Ok,
						["width"] = image.GetWidth(),
					});
				}

				return Ok(Shot(path));
			}

			case "press":
			{
				uint mask = root.TryGetProperty("buttons", out JsonElement b)
					? (uint)b.GetInt64()
					: 0u;
				int frames = root.TryGetProperty("frames", out JsonElement f) ? f.GetInt32() : 8;
				_pressed = mask;
				_pressFrames = frames < 1 ? 1 : frames;

				// Sticks and triggers ride the same hold: -1..1 floats, XInput-scaled here.
				static int Scale(JsonElement parent, string name) =>
					parent.TryGetProperty(name, out JsonElement axis)
						? (int)(Mathf.Clamp((float)axis.GetDouble(), -1f, 1f) * 32767f)
						: 0;
				_stickLX = Scale(root, "lx");
				_stickLY = Scale(root, "ly");
				_stickRX = Scale(root, "rx");
				_stickRY = Scale(root, "ry");
				_trigL = root.TryGetProperty("lt", out JsonElement lt)
					? (uint)(Mathf.Clamp((float)lt.GetDouble(), 0f, 1f) * 255f)
					: 0u;
				_trigR = root.TryGetProperty("rt", out JsonElement rt)
					? (uint)(Mathf.Clamp((float)rt.GetDouble(), 0f, 1f) * 255f)
					: 0u;
				_stickFrames = _stickLX != 0 || _stickLY != 0 || _stickRX != 0 ||
					_stickRY != 0 || _trigL != 0 || _trigR != 0
						? _pressFrames
						: 0;
				return Ok(new Dictionary<string, object?>
				{
					["buttons"] = mask,
					["frames"] = _pressFrames,
					["sticks"] = _stickFrames > 0,
				});
			}

			default:
				return new Dictionary<string, object?>
				{
					["ok"] = false,
					["error"] = $"unknown op '{op}'",
					["ops"] = new[]
					{
						"ping", "state", "rows", "frontend", "elements", "peek", "threads",
						"objects", "perf", "exports", "cvars", "cvar-set", "vfs", "sockets",
						"menu", "action", "call", "lua", "call-result", "poke", "heap",
						"probe", "guestlog", "mark", "metrics", "input-record", "input-replay",
						"wait", "log", "shot", "press", "gdb",
					},
				};
		}
	}

	/// <summary>
	/// Every response carries WHO ANSWERED IT. Without this a client cannot tell a fresh game
	/// from the previous one on the same port, and a stale answer looks exactly like a current
	/// one — the failure this whole handshake exists to prevent.
	/// </summary>
	private static Dictionary<string, object?> Ok(Dictionary<string, object?> body)
	{
		body["ok"] = true;
		body["pid"] = OS.GetProcessId();
		body["boot"] = BootId;
		body["uptime"] = (int)Uptime.Elapsed.TotalSeconds;
		return body;
	}

	private static bool HostBridgeUp() => Refii.Host.HostBridge.TryGetAbiVersion() is not null;

	/// <summary>What the guest states about the UI right now — the reading, not an inference.</summary>
	private static Dictionary<string, object?> State()
	{
		var state = new Dictionary<string, object?> { ["guest"] = HostBridgeUp() };
		if (Engine_UiSource.Current.GetInGameScreens() is { } screens)
		{
			state["screen"] = screens.Current.ToString();
			state["screenId"] = (int)screens.Current;

			var owned = new Dictionary<string, object?>();
			foreach (KeyValuePair<uint, int> entry in screens.Owned)
			{
				owned[entry.Value.ToString()] = $"{entry.Key:X8}";
			}

			state["owned"] = owned;
		}
		else
		{
			state["screen"] = "no manager";
		}

		if (Engine_UiSource.Current.GetPauseState() is { } pause)
		{
			state["pauseScreenId"] = pause.ScreenId;
			state["modalId"] = pause.ModalId;
			state["modalMode"] = pause.ModalMode;
			state["tab"] = pause.CurrentTab;
		}

		state["captureScreen"] = Engine_UiSource.Current.MenuScreenId();

		// The world-map region sample as the C# side reads it RIGHT NOW — which is the half of
		// the round-trip a blank region page cannot distinguish on its own: a nameless sample
		// is the pump never publishing; a named one that stays blank is the match refusing it.
		Refii.Host.HostBridge.WorldMapRegion region = Refii.Host.HostBridge.GetWorldMapRegion();
		// Count discriminates the halves: 7 with no name means the pump's walk works and the
		// WANTED index never matched (the request side); 0 means the walk itself is failing.
		state["regionCount"] = region.Count;
		state["regionKnown"] = region.Name.Length != 0;
		state["regionIndex"] = region.Index;
		state["regionName"] = region.Name;

		// Two of the eleven provider fields, verbatim: empty here with a NAMED sample means the
		// pump's provider calls return nothing; text here with a blank page means the values
		// channel loses them between the bridge and the book.
		state["regionDistance"] = region.Distance;
		state["regionTravel"] = region.Travel;
		return state;
	}

	/// <summary>The spawn capture, as the guest last stated it.</summary>
	private static Dictionary<string, object?> Rows()
	{
		var rows = new List<Dictionary<string, object?>>();
		List<Engine_UiModel.MenuRow> captured =
			Engine_UiSource.Current.GetMenuRows(out ulong generation);
		foreach (Engine_UiModel.MenuRow row in captured)
		{
			rows.Add(new Dictionary<string, object?>
			{
				["name"] = row.Name,
				["parent"] = row.Parent,
				["label"] = row.Label,
				["id"] = row.Id,
				["eventType"] = row.EventType,
				["rating"] = row.Rating,
			});
		}

		return new Dictionary<string, object?>
		{
			["generation"] = generation,
			["screen"] = Engine_UiSource.Current.MenuScreenId(),
			["rows"] = rows,
		};
	}

	/// <summary>One caller's outstanding wait: answered from _Process when its condition holds.
	/// A class, not a record: a log wait carries a read offset that advances every frame.</summary>
	private sealed class PendingWait
	{
		public PendingWait(StreamPeerTcp client, string until, uint wantScreen,
			System.DateTime deadline, string logMatch, long logOffset)
		{
			Client = client;
			Until = until;
			WantScreen = wantScreen;
			Deadline = deadline;
			LogMatch = logMatch;
			LogOffset = logOffset;
		}

		public StreamPeerTcp Client { get; }
		public string Until { get; }
		public uint WantScreen { get; }
		public System.DateTime Deadline { get; }
		public string LogMatch { get; }
		public long LogOffset;
	}

	private readonly List<PendingWait> _waits = new();

	/// <summary>
	/// DEFERRED, NEVER BLOCKING: this server is served from `_Process` on Godot's main thread,
	/// so a wait that slept here would freeze the game for its own duration — the tool built to
	/// watch the game must not stop it. The condition is recorded and re-checked once per frame;
	/// the reply goes out on the frame it first holds.
	/// </summary>
	private Dictionary<string, object?>? Wait(
		StreamPeerTcp client, string until, uint wantScreen, double timeout, string logMatch = "")
	{
		if (until.Length == 0)
		{
			return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "wait needs 'for'" };
		}

		long offset = 0;
		if (until == "log")
		{
			if (logMatch.Length == 0)
			{
				return new Dictionary<string, object?>
				{
					["ok"] = false, ["error"] = "wait for='log' needs 'match'",
				};
			}

			// From HERE forward: a wait must never be satisfied by history.
			try
			{
				offset = new System.IO.FileInfo(
					ProjectSettings.GlobalizePath("user://logs/godot.log")).Length;
			}
			catch (System.Exception)
			{
				offset = 0;
			}
		}

		_waits.Add(new PendingWait(
			client, until, wantScreen,
			System.DateTime.UtcNow.AddSeconds(System.Math.Clamp(timeout, 1.0, 570.0)),
			logMatch, offset));
		return null;   // answered later, from ServeWaits
	}

	/// <summary>New godot.log bytes since the wait began, scanned for its match.</summary>
	private static bool LogMatched(PendingWait wait)
	{
		try
		{
			string path = ProjectSettings.GlobalizePath("user://logs/godot.log");
			using var stream = new System.IO.FileStream(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
				System.IO.FileShare.ReadWrite);
			if (stream.Length <= wait.LogOffset)
			{
				return false;
			}

			stream.Seek(wait.LogOffset, System.IO.SeekOrigin.Begin);
			using var reader = new System.IO.StreamReader(stream);
			string added = reader.ReadToEnd();
			wait.LogOffset = stream.Length;
			return added.Contains(wait.LogMatch, System.StringComparison.OrdinalIgnoreCase);
		}
		catch (System.Exception)
		{
			return false;
		}
	}

	/// <summary>Once per frame: answer every wait whose condition holds or whose time is up.</summary>
	private void ServeWaits()
	{
		for (int i = _waits.Count - 1; i >= 0; --i)
		{
			PendingWait wait = _waits[i];
			if (wait.Client.GetStatus() != StreamPeerTcp.Status.Connected)
			{
				_waits.RemoveAt(i);
				continue;
			}

			bool met = wait.Until switch
			{
				// The title's armed byte — the press that "does not land" is this being false.
				"frontend-armed" => Refii.Host.HostBridge.TryGetFrontEnd(
					out _, out _, out bool canPressA, out _, out _, out _) && canPressA,

				// The front end exists at all (title or menu).
				"frontend" => Refii.Host.HostBridge.TryGetFrontEnd(
					out _, out _, out _, out _, out _, out _),

				// The in-game screen manager exists — the world is up.
				"world" => Engine_UiSource.Current.GetInGameScreens() is not null,

				// A specific in-game screen is current (the pause root is 0).
				"screen" => Engine_UiSource.Current.GetInGameScreens() is { } screens &&
					(uint)screens.Current == wait.WantScreen,

				// No screen is up — back in gameplay.
				"gameplay" => Engine_UiSource.Current.GetInGameScreens() is { } up &&
					up.Current == Engine_UiModel.InGameScreen.None,

				// A line containing the match arrived in godot.log AFTER the wait began.
				"log" => LogMatched(wait),

				_ => true,
			};

			bool expired = System.DateTime.UtcNow >= wait.Deadline;
			if (!met && !expired)
			{
				continue;
			}

			var reply = new Dictionary<string, object?>
			{
				["for"] = wait.Until,
				["met"] = met,
			};
			if (!met)
			{
				reply["timeout"] = true;
			}

			wait.Client.PutData(Encoding.UTF8.GetBytes(
				JsonSerializer.Serialize(Ok(reply)) + "\n"));
			_waits.RemoveAt(i);
		}
	}

	/// <summary>
	/// THE FRONT END, which `state` cannot describe: it is a different GUI object from the
	/// in-game screen manager, so a run parked on the title or the gender card reports "no
	/// manager" and nothing more. That is precisely the state an unattended run gets stuck in,
	/// and the one this could not see.
	/// </summary>
	private static Dictionary<string, object?> FrontEnd()
	{
		if (!Refii.Host.HostBridge.TryGetFrontEnd(
				out Engine_UiModel.FrontEndScreen screen, out uint state, out bool canPressA,
				out uint cursor, out int sceneSlide, out int slideCount))
		{
			return new Dictionary<string, object?> { ["up"] = false };
		}

		return new Dictionary<string, object?>
		{
			["up"] = true,
			["screen"] = screen.ToString(),
			["state"] = state,

			// The title's A is gated by an armed byte; a press that "does not land" is usually
			// this being false rather than the press being lost.
			["canPressA"] = canPressA,
			["cursor"] = cursor,

			// Slide 1 on the main menu is `PressStart` — the row list is not up yet.
			["slide"] = sceneSlide,
			["slideCount"] = slideCount,
		};
	}

	/// <summary>The filtered tail of ONE file, read shared — its writer is still writing.</summary>
	private static List<string> TailOf(string path, int take, string match)
	{
		var kept = new List<string>();
		try
		{
			using var stream = new System.IO.FileStream(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
				System.IO.FileShare.ReadWrite);
			using var reader = new System.IO.StreamReader(stream);
			while (reader.ReadLine() is { } line)
			{
				if (match.Length != 0 &&
					!line.Contains(match, System.StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				kept.Add(line);
				if (kept.Count > 4000)
				{
					kept.RemoveRange(0, 2000);
				}
			}
		}
		catch (System.Exception)
		{
			// A rotating segment can vanish mid-read; the other segments still answer.
		}

		if (kept.Count > take)
		{
			kept.RemoveRange(0, kept.Count - take);
		}

		return kept;
	}

	/// <summary>
	/// The tail of the game's own log, so a question can be asked without leaving the channel.
	/// `match` filters to lines containing it, which is what makes 40 lines enough.
	/// </summary>
	private static Dictionary<string, object?> Tail(int lines, string match)
	{
		string path = ProjectSettings.GlobalizePath("user://logs/godot.log");
		var kept = new List<string>();
		try
		{
			// Read-shared: the engine is writing this file while we read it.
			using var stream = new System.IO.FileStream(
				path, System.IO.FileMode.Open, System.IO.FileAccess.Read,
				System.IO.FileShare.ReadWrite);
			using var reader = new System.IO.StreamReader(stream);
			while (reader.ReadLine() is { } line)
			{
				if (match.Length != 0 &&
					!line.Contains(match, System.StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				kept.Add(line);
				if (kept.Count > 4000)
				{
					kept.RemoveRange(0, 2000);
				}
			}
		}
		catch (System.Exception error)
		{
			return new Dictionary<string, object?> { ["error"] = error.Message, ["path"] = path };
		}

		int take = lines < 1 ? 1 : lines;
		if (kept.Count > take)
		{
			kept.RemoveRange(0, kept.Count - take);
		}

		return new Dictionary<string, object?> { ["path"] = path, ["lines"] = kept };
	}

	/// <summary>
	/// A PNG of the game window. Written where the caller asks — never into the package or the
	/// repo, because these are frames of the player's own game data.
	/// </summary>
	private Dictionary<string, object?> Shot(string path)
	{
		if (path.Length == 0)
		{
			return new Dictionary<string, object?> { ["error"] = "no path" };
		}

		if (!IsInsideTree() || GetWindow() is not { } window)
		{
			return new Dictionary<string, object?> { ["error"] = "no window" };
		}

		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
		Image image = window.GetTexture().GetImage();
		Error error = image.SavePng(path);
		return new Dictionary<string, object?>
		{
			["path"] = path,
			["width"] = image.GetWidth(),
			["height"] = image.GetHeight(),
			["saved"] = error == Error.Ok,
		};
	}
}
