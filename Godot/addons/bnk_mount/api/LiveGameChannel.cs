#if TOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Refii.EditorTools;

/// <summary>
/// The editor's client for the RUNNING game's debug channel (<see cref="Refii.Game.GameDebugServer"/>),
/// so a modder's Lua reaches the game VM without a relaunch.
///
/// The protocol, as the server speaks it: the game publishes <c>{pid, port, boot}</c> at
/// <see cref="Refii.Game.GameDebugServer.HandshakePath"/> and removes it on exit; a client reads
/// the file on EVERY request (never cached), refuses a dead pid, connects to 127.0.0.1:port and
/// sends one JSON object per line; the reply is one JSON object per line and names the run it
/// came from (<c>boot</c>). A chunk posted with <c>{"op":"lua","chunk":"@game ..."}</c> is
/// queued for the guest tick pump and the reply carries <c>seqBefore</c>; the outcome is the
/// first <c>{"op":"call-result"}</c> whose <c>seq</c> is greater than that.
///
/// Every call here is synchronous and bounded by its timeout — the editor is blocked for at
/// most that long. The liveness probe never signals the process.
/// </summary>
public static class LiveGameChannel
{
	/// <summary>
	/// The longest chunk sent on the <c>debug-gamelua:</c> path. The game takes any length
	/// (<c>RunGameLua</c> in src/vfs/src/hooks/guest_tick_pump.cpp puts a chunk that does not fit its
	/// frame in a guest heap block); this bound keeps one request line of the debug channel sane.
	/// </summary>
	public const int MaxChunkBytes = 1024 * 1024;

	/// <summary>The default wait for a queued chunk's result: the pump drains once per tick.</summary>
	public const double DefaultRunTimeoutSeconds = 3.0;

	private const double DefaultAskTimeoutSeconds = 2.0;

	/// <summary>Who published the channel: the pid to check, the port to call, the run's identity.</summary>
	public sealed record Handshake(int Pid, int Port, string Boot);

	/// <summary>The boot id last answered, so "the game restarted since the last run" can be said.</summary>
	public static string? LastBoot { get; private set; }

	public static string HandshakePath => Refii.Game.GameDebugServer.HandshakePath;

	/// <summary>The handshake as written, or null when there is none or it is unreadable.</summary>
	public static Handshake? ReadHandshake()
	{
		try
		{
			if (!File.Exists(HandshakePath))
			{
				return null;
			}

			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(HandshakePath));
			JsonElement root = document.RootElement;
			if (!root.TryGetProperty("pid", out JsonElement pid) ||
				!root.TryGetProperty("port", out JsonElement port))
			{
				return null;
			}

			string boot = root.TryGetProperty("boot", out JsonElement b) ? b.GetString() ?? string.Empty : string.Empty;
			return new Handshake(pid.GetInt32(), port.GetInt32(), boot);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>A game is publishing a channel and its pid is alive.</summary>
	public static bool IsGameRunning() => ReadHandshake() is { } handshake && IsAlive(handshake.Pid);

	/// <summary>
	/// Whether the pid is still running. A handshake outlives a game killed with SIGKILL, so
	/// the file alone proves nothing. On Linux this reads /proc; elsewhere it asks the OS for
	/// the process — never a signal, which on Windows would be TerminateProcess.
	/// </summary>
	public static bool IsAlive(int pid)
	{
		if (pid <= 0)
		{
			return false;
		}

		if (OperatingSystem.IsLinux())
		{
			return Directory.Exists("/proc/" + pid);
		}

		try
		{
			using var process = System.Diagnostics.Process.GetProcessById(pid);
			return !process.HasExited;
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>
	/// One request, one reply, against the game the handshake names right now. A fresh
	/// handshake read and a fresh connection every time. Returns null and the reason when no
	/// live game answered; a reply with <c>ok:false</c> is still returned, the caller reads
	/// its <c>error</c>.
	/// </summary>
	public static JsonElement? Ask(Dictionary<string, object?> request, double timeoutSeconds, out string error)
	{
		error = string.Empty;
		Handshake? handshake = ReadHandshake();
		if (handshake is null)
		{
			error = $"no game is publishing a debug channel (no handshake at {HandshakePath})";
			return null;
		}

		if (!IsAlive(handshake.Pid))
		{
			error = $"the game that published the channel (pid {handshake.Pid}) is gone — stale handshake";
			return null;
		}

		int timeoutMs = Math.Max(1, (int)(timeoutSeconds * 1000));
		try
		{
			using var cts = new CancellationTokenSource(timeoutMs);
			using var client = new TcpClient();
			try
			{
				client.ConnectAsync(IPAddress.Loopback, handshake.Port, cts.Token).AsTask().GetAwaiter().GetResult();
			}
			catch (Exception ex) when (ex is OperationCanceledException || cts.IsCancellationRequested)
			{
				error = $"pid {handshake.Pid} publishes port {handshake.Port} but did not accept within {timeoutSeconds:0.#}s";
				return null;
			}

			using NetworkStream stream = client.GetStream();
			stream.ReadTimeout = timeoutMs;
			stream.WriteTimeout = timeoutMs;

			byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request) + "\n");
			stream.Write(line, 0, line.Length);
			stream.Flush();

			// The reply is one line; the server writes it from its next frame.
			var received = new MemoryStream();
			var buffer = new byte[65536];
			DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (true)
			{
				int read = stream.Read(buffer, 0, buffer.Length);
				if (read <= 0)
				{
					break;
				}

				received.Write(buffer, 0, read);
				if (Array.IndexOf(buffer, (byte)'\n', 0, read) >= 0)
				{
					break;
				}

				if (DateTime.UtcNow > deadline)
				{
					error = $"pid {handshake.Pid} publishes port {handshake.Port} but did not answer within {timeoutSeconds:0.#}s";
					return null;
				}
			}

			string text = Encoding.UTF8.GetString(received.ToArray());
			int newline = text.IndexOf('\n');
			string replyLine = (newline >= 0 ? text[..newline] : text).Trim();
			if (replyLine.Length == 0)
			{
				error = $"pid {handshake.Pid} publishes port {handshake.Port} but closed without answering";
				return null;
			}

			using JsonDocument document = JsonDocument.Parse(replyLine);
			JsonElement reply = document.RootElement.Clone();
			if (reply.TryGetProperty("boot", out JsonElement boot) && boot.GetString() is { } bootId)
			{
				LastBoot = bootId;
			}

			return reply;
		}
		catch (Exception failure)
		{
			error = $"{Innermost(failure).Message} — pid {handshake.Pid} publishes port {handshake.Port} but did not answer";
			return null;
		}
	}

	/// <summary>
	/// Runs <paramref name="chunk"/> in the GAME VM — where the shipped scripts and the mods'
	/// Lua live — and waits for its outcome. The guest answers one bit (the chunk ran without
	/// error, or not) and a note; a Lua error inside the chunk is reported as not ok, and
	/// <c>assert(...)</c> is how a value is read out.
	/// </summary>
	public static (bool Ok, string Note) RunGameLua(string chunk, double timeoutSeconds = DefaultRunTimeoutSeconds)
	{
		if (string.IsNullOrWhiteSpace(chunk))
		{
			return (false, "nothing to run");
		}

		int bytes = Encoding.UTF8.GetByteCount(chunk);
		if (bytes > MaxChunkBytes)
		{
			return (false, $"too long for a live run ({bytes} bytes; limit {MaxChunkBytes} bytes)");
		}

		string? bootBefore = LastBoot;
		JsonElement? posted = Ask(
			new Dictionary<string, object?> { ["op"] = "lua", ["chunk"] = "@game " + chunk },
			DefaultAskTimeoutSeconds, out string error);
		if (posted is not { } post)
		{
			return (false, error);
		}

		if (!IsOk(post))
		{
			return (false, ErrorOf(post));
		}

		ulong seqBefore = post.TryGetProperty("seqBefore", out JsonElement s) && s.TryGetUInt64(out ulong before) ? before : 0;
		string restarted = bootBefore is not null && LastBoot is not null && bootBefore != LastBoot
			? " (the game restarted since the last run)"
			: string.Empty;

		(bool ran, string note) = WaitForCallResult(seqBefore, timeoutSeconds);
		return (ran, note + restarted);
	}

	/// <summary>
	/// The outcome of the chunk queued when the guest's debug-call counter stood at
	/// <paramref name="seqBefore"/>: the first <c>call-result</c> numbered after it. The guest
	/// answers one bit, the chunk ran without error or not, and a note.
	/// </summary>
	private static (bool Ran, string Note) WaitForCallResult(ulong seqBefore, double timeoutSeconds)
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(0.1, timeoutSeconds));
		while (DateTime.UtcNow < deadline)
		{
			Thread.Sleep(100);
			JsonElement? polled = Ask(
				new Dictionary<string, object?> { ["op"] = "call-result" },
				DefaultAskTimeoutSeconds, out string error);
			if (polled is not { } result)
			{
				return (false, error);
			}

			if (!IsOk(result))
			{
				return (false, ErrorOf(result));
			}

			ulong seq = result.TryGetProperty("seq", out JsonElement q) && q.TryGetUInt64(out ulong value) ? value : 0;
			if (seq <= seqBefore)
			{
				continue;
			}

			// r3 is the guest's one bit, as hex; the note says why when it is 0.
			bool ran = result.TryGetProperty("r3", out JsonElement r3) &&
				Convert.ToUInt64(r3.GetString() ?? "0", 16) != 0;
			string note = result.TryGetProperty("note", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
			return (ran, note);
		}

		return (false, $"posted but no result within {timeoutSeconds:0.#}s — the guest tick pump is not draining " +
			"(front end down, or the guest is parked)");
	}

	/// <summary>One of the mods' Lua errors as the running game kept it: its number, when, whose, what.</summary>
	public sealed record ModError(long Seq, DateTime Time, string Mod, string Text);

	/// <summary>
	/// The mods' Lua errors the running game has kept that are numbered after
	/// <paramref name="since"/> (0: all of them), oldest first, and in <paramref name="latest"/> the
	/// newest number — the <paramref name="since"/> of the next ask. Null, with the reason, when no
	/// game answered.
	/// </summary>
	public static List<ModError>? ModErrors(long since, out long latest, out string error)
	{
		latest = since;
		JsonElement? reply = Ask(
			new Dictionary<string, object?> { ["op"] = "mod-errors", ["since"] = since },
			DefaultAskTimeoutSeconds, out error);
		if (reply is not { } r)
		{
			return null;
		}

		if (!IsOk(r))
		{
			error = ErrorOf(r);
			return null;
		}

		if (r.TryGetProperty("seq", out JsonElement seq) && seq.TryGetInt64(out long newest))
		{
			latest = newest;
		}

		var errors = new List<ModError>();
		if (r.TryGetProperty("errors", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement e in list.EnumerateArray())
			{
				long number = e.TryGetProperty("seq", out JsonElement n) && n.TryGetInt64(out long v) ? v : 0;
				DateTime time = e.TryGetProperty("time", out JsonElement t) &&
					DateTime.TryParse(t.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when)
					? when
					: DateTime.Now;
				errors.Add(new ModError(number, time, Text(e, "mod"), Text(e, "text")));
			}
		}

		return errors;
	}

	/// <summary>The longest a remount is waited for: its passes walk every bank again.</summary>
	public const double RemountTimeoutSeconds = 180.0;

	/// <summary>The longest the re-run init chunk is waited for once queued.</summary>
	private const double RemountLuaTimeoutSeconds = 10.0;

	/// <summary>What a live remount did, as the game reported it.</summary>
	/// <param name="Ok">The mods were folded in again and the game now serves them.</param>
	/// <param name="Error">Why not, when not.</param>
	/// <param name="Refreshed">What took the change live.</param>
	/// <param name="Relaunch">What changed but reaches the game only on a relaunch, each "kind: why (files)".</param>
	/// <param name="Mods">The mods the game folded in, lowest priority first.</param>
	/// <param name="ModsRoot">The folder the game reads mods from.</param>
	/// <param name="Changed">How many mod-contributed files changed.</param>
	/// <param name="LuaRan">With Lua requested: whether the chunk ran; null when it was not run.</param>
	/// <param name="LuaNote">What happened to the Lua.</param>
	/// <param name="Warning">A pass that failed while the mount still came up.</param>
	public sealed record RemountResult(
		bool Ok, string Error, IReadOnlyList<string> Refreshed, IReadOnlyList<string> Relaunch,
		IReadOnlyList<string> Mods, string ModsRoot, int Changed, bool? LuaRan, string LuaNote, string Warning);

	/// <summary>
	/// Folds the installed mods into the running game again (<c>mods-remount</c>) and, with
	/// <paramref name="lua"/>, runs the re-composed init chunk in the game VM and waits for its
	/// outcome. Blocks for as long as the remount takes — up to <see cref="RemountTimeoutSeconds"/>;
	/// call it off the editor's main thread.
	/// </summary>
	public static RemountResult Remount(bool lua)
	{
		JsonElement? reply = Ask(
			new Dictionary<string, object?> { ["op"] = "mods-remount", ["lua"] = lua },
			RemountTimeoutSeconds, out string error);
		if (reply is not { } r)
		{
			return Failed(error);
		}

		if (!IsOk(r))
		{
			return Failed(ErrorOf(r));
		}

		var relaunch = new List<string>();
		if (r.TryGetProperty("relaunch", out JsonElement kinds) && kinds.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement kind in kinds.EnumerateArray())
			{
				List<string> paths = Strings(kind, "paths");
				relaunch.Add($"{Text(kind, "kind")}: {Text(kind, "reason")}" +
					(paths.Count == 0
						? string.Empty
						: $" ({string.Join(", ", paths.GetRange(0, Math.Min(6, paths.Count)))}" +
						  (paths.Count > 6 ? $", +{paths.Count - 6} more)" : ")")));
			}
		}

		int changed = r.TryGetProperty("changed", out JsonElement c) && c.ValueKind == JsonValueKind.Array
			? c.GetArrayLength()
			: 0;

		bool? luaRan = null;
		string luaNote = string.Empty;
		if (r.TryGetProperty("lua", out JsonElement luaReply))
		{
			luaNote = Text(luaReply, "note");
			bool queued = luaReply.TryGetProperty("queued", out JsonElement q) && q.ValueKind == JsonValueKind.True;
			if (lua && queued)
			{
				ulong seqBefore = luaReply.TryGetProperty("seqBefore", out JsonElement s) && s.TryGetUInt64(out ulong b) ? b : 0;
				(bool ran, string note) = WaitForCallResult(seqBefore, RemountLuaTimeoutSeconds);
				luaRan = ran;
				luaNote = ran
					? luaNote
					: $"the chunk did not run cleanly: {note} — see the error list";
			}
			else if (lua)
			{
				luaRan = false;
			}
		}

		return new RemountResult(
			true, string.Empty, Strings(r, "refreshed"), relaunch, Strings(r, "mods"),
			Text(r, "modsRoot"), changed, luaRan, luaNote, Text(r, "warning"));
	}

	private static RemountResult Failed(string error) => new(
		false, error, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
		string.Empty, 0, null, string.Empty, string.Empty);

	private static string Text(JsonElement element, string name) =>
		element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;

	private static List<string> Strings(JsonElement element, string name)
	{
		var strings = new List<string>();
		if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
				{
					strings.Add(text);
				}
			}
		}

		return strings;
	}

	private static bool IsOk(JsonElement reply) =>
		reply.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;

	private static string ErrorOf(JsonElement reply) =>
		reply.TryGetProperty("error", out JsonElement error) ? error.GetString() ?? "refused" : "refused";

	private static Exception Innermost(Exception error)
	{
		while (error.InnerException is { } inner)
		{
			error = inner;
		}
		return error;
	}
}
#endif
