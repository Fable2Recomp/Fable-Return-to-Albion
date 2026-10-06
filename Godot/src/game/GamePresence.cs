using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Godot;
using Refii.Core;
using Refii.Host;
using Refii.Launcher.Core;
using SysEnvironment = System.Environment;

namespace Refii.Game;

/// <summary>
/// Discord Rich Presence: "Playing Fable 2: Return to Albion", with the level the player is
/// standing in — Bowerstone Market, Rookridge — as the first line.
/// </summary>
/// <remarks>
/// The place is <see cref="GameWhereabouts.Current"/>, the level the guest states it is in,
/// so the line is right on every transition that reloads a level. Discord is talked to over its
/// local IPC endpoint (a Unix socket, or a named pipe on Windows); when Discord is not running
/// the node retries quietly every <see cref="RetrySeconds"/> and never holds the frame up.
///
/// JOINING. While we host a joinable session the activity carries a party and a join secret —
/// our peer id, the title id and the session id, the same material a RexNet invite carries —
/// so Discord offers "Invite to Join" in chat and "Ask to Join" on the profile. A friend who
/// accepts gets <c>ACTIVITY_JOIN</c> on their side, and <see cref="NetBridge.JoinSession"/>
/// raises the invite for them; an "Ask to Join" is approved while we are joinable, declined
/// otherwise. The joiner's guest takes it from there as it would any accepted invite.
///
/// A JOIN MAKES A FRIEND. The joiner's request carries the session id as its note, which is
/// proof it came with the secret; while that session is ours the request is accepted here, so
/// the two are friends in RexNet's own list and can find each other without Discord next time.
///
/// The game's name and artwork belong to the project's Discord application in the developer
/// portal: it is named "Fable 2: Return to Albion" and carries one art asset keyed `logo`. Its
/// ID is <see cref="ApplicationId"/> and is not a setting — the presence is the project's.
/// </remarks>
public partial class GamePresence : Node
{
	/// <summary>The project's Discord application, "Fable 2: Return to Albion".</summary>
	private const string ApplicationId = "1556952543400173668";

	private const string LargeImageKey = "logo";

	private const string LargeImageText = "Fable 2: Return to Albion";

	/// <summary>How often the shown text is re-derived; a change is sent at once.</summary>
	private const double PollSeconds = 2.0;

	/// <summary>How long after a failed or dropped connection before the next attempt.</summary>
	private const double RetrySeconds = 15.0;

	private readonly DiscordIpc _discord = new();

	private double _poll;

	private double _sinceAttempt = RetrySeconds;

	private Activity _shown;

	/// <summary>Everything Discord is told; a change in any field is sent.</summary>
	private readonly record struct Activity(
		string Details, string State, string PartyId, string JoinSecret, bool Joinable);

	private long _startedUnix;

	public override void _Ready()
	{
		IReadOnlyDictionary<string, string> config = Engine_RefiiConfig.Read(out _);
		if (!Engine_RefiiConfig.Bool(config, "discord_presence", true))
		{
			GD.Print("[presence] discord_presence = false");
			SetProcess(false);
			return;
		}

		_startedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
	}

	public override void _ExitTree()
	{
		if (_discord.Connected)
		{
			_discord.ClearActivity();
		}

		_discord.Dispose();
	}

	public override void _Process(double delta)
	{
		_poll += delta;
		_sinceAttempt += delta;
		if (_poll < PollSeconds)
		{
			return;
		}

		_poll = 0;

		if (!_discord.Connected)
		{
			if (_sinceAttempt < RetrySeconds)
			{
				return;
			}

			_sinceAttempt = 0;
			if (!_discord.Connect(ApplicationId, out string why))
			{
				if (why.Length > 0)
				{
					GD.Print("[presence] Discord not reachable: " + why);
				}

				return;
			}

			GD.Print("[presence] connected to Discord");
			_shown = default;
		}

		Activity wanted = Describe();
		if (wanted != _shown && _discord.SetActivity(wanted, _startedUnix))
		{
			_shown = wanted;
			GD.Print($"[presence] {wanted.Details} — {wanted.State}" +
				(wanted.PartyId.Length > 0 ? " (joinable)" : string.Empty));
		}

		while (_discord.Events.TryDequeue(out DiscordIpc.Event evt))
		{
			Handle(evt, wanted.Joinable);
		}

		if (wanted.PartyId.Length > 0)
		{
			BefriendJoiners(wanted.PartyId);
		}
	}

	/// <summary>
	/// Accepts the friend requests of players who joined this session through Discord: the
	/// ones whose note names our session id, which only the join secret carries.
	/// </summary>
	private static void BefriendJoiners(string sessionId)
	{
		foreach (NetBridge.Request request in NetBridge.Requests())
		{
			if (request.Note != "discord:" + sessionId)
			{
				continue;
			}

			if (NetBridge.PeerAction(request.PeerId, NetBridge.Act.AcceptRequest))
			{
				GD.Print("[presence] " + (request.DisplayName.Length > 0
					? request.DisplayName : Short(request.PeerId)) + " joined through Discord; now a friend");
			}
		}
	}

	/// <summary>A Discord join or ask-to-join, on the main thread.</summary>
	private void Handle(DiscordIpc.Event evt, bool joinable)
	{
		switch (evt.Kind)
		{
			case DiscordIpc.EventKind.Join:
			{
				string[] parts = evt.Value.Split('|');
				if (parts.Length != 3 || !uint.TryParse(parts[1],
					System.Globalization.NumberStyles.HexNumber, null, out uint titleId))
				{
					GD.Print("[presence] join secret not understood");
					return;
				}

				if (titleId != SupportedGame.TitleId)
				{
					GD.Print($"[presence] join refused: title {titleId:X8} is not ours");
					return;
				}

				// The native side also sends the host a friend request, noted with the
				// session id, which the host's shell accepts (see BefriendJoiners).
				bool sent = NetBridge.JoinSession(parts[0], titleId, parts[2]);
				GD.Print(sent
					? "[presence] joining " + Short(parts[0]) + "'s session from Discord"
					: "[presence] join from Discord failed: RexNet is not up");
				return;
			}

			case DiscordIpc.EventKind.JoinRequest:
				GD.Print($"[presence] {evt.Name} asks to join: " +
					(joinable ? "approved" : "declined, not joinable"));
				_discord.AnswerJoinRequest(evt.Value, joinable);
				return;
		}
	}

	private static string Short(string peerId) =>
		peerId.Length <= 8 ? peerId : "…" + peerId[^8..];

	/// <summary>
	/// The two lines Discord shows under the game's name, and the party and secret while we
	/// host a session others may join.
	/// </summary>
	private static Activity Describe()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return new Activity("Starting up", string.Empty, string.Empty, string.Empty, false);
		}

		string details;
		string state;
		if (!HostBridge.IsInGame)
		{
			details = "At the main menu";
			state = string.Empty;
		}
		else if (GameWhereabouts.Current is { } place && place.DisplayName.Length > 0)
		{
			details = place.DisplayName;
			state = "Exploring Albion";
		}
		else
		{
			details = "Travelling";
			state = "Exploring Albion";
		}

		// The party is the session: offered only while the game admits a joiner, so a
		// Discord "Join" never lands on a session that would refuse it.
		if (NetBridge.HostState() is { Hosting: true, Joinable: true } &&
			NetBridge.Self() is { } self &&
			NetBridge.OwnSessionId() is { } session)
		{
			string secret = self.PeerId + "|" + SupportedGame.TitleId.ToString("X8") + "|" + session;
			return new Activity(details, "Co-op, open to join", session, secret, true);
		}

		return new Activity(details, state, string.Empty, string.Empty, false);
	}

	/// <summary>
	/// The Discord IPC client: one connection, a handshake, then SET_ACTIVITY frames. Every
	/// frame is a little-endian opcode and length followed by JSON. Responses are drained on a
	/// background task and discarded; an error there drops the connection.
	/// </summary>
	private sealed class DiscordIpc : IDisposable
	{
		public enum EventKind
		{
			/// <summary>The player accepted an invite: <see cref="Event.Value"/> is the secret.</summary>
			Join,

			/// <summary>Someone asked to join: <see cref="Event.Value"/> is their user id.</summary>
			JoinRequest,
		}

		public readonly record struct Event(EventKind Kind, string Value, string Name);

		/// <summary>Discord's join events, queued by the reader for the main thread.</summary>
		public ConcurrentQueue<Event> Events { get; } = new();

		private const int OpHandshake = 0;

		private const int OpFrame = 1;

		private const int OpClose = 2;

		private readonly object _gate = new();

		private Stream? _stream;

		private volatile bool _dropped;

		public bool Connected => _stream is not null && !_dropped;

		public bool Connect(string applicationId, out string why)
		{
			why = string.Empty;
			Drop();

			foreach (string endpoint in Endpoints())
			{
				Stream? stream;
				try
				{
					stream = Open(endpoint);
				}
				catch (Exception exception) when (exception is IOException or SocketException
					or UnauthorizedAccessException or TimeoutException)
				{
					why = endpoint + ": " + exception.Message;
					continue;
				}

				if (stream is null)
				{
					continue;
				}

				try
				{
					Write(stream, OpHandshake, JsonSerializer.Serialize(
						new Dictionary<string, object> { ["v"] = 1, ["client_id"] = applicationId }));
					string reply = ReadFrame(stream, TimeSpan.FromSeconds(2));
					if (!reply.Contains("\"READY\"", StringComparison.Ordinal))
					{
						why = endpoint + ": handshake answered " + reply;
						stream.Dispose();
						continue;
					}
				}
				catch (Exception exception) when (exception is IOException or SocketException
					or TimeoutException or ObjectDisposedException)
				{
					why = endpoint + ": " + exception.Message;
					stream.Dispose();
					continue;
				}

				_stream = stream;
				_dropped = false;
				Task.Run(() => Drain(stream));
				why = string.Empty;

				// Without these Discord shows the party but tells us nothing when it is used.
				Subscribe("ACTIVITY_JOIN");
				Subscribe("ACTIVITY_JOIN_REQUEST");
				return true;
			}

			// No endpoint at all is the ordinary case — Discord is not running — and `why`
			// stays empty so the caller does not log it every retry.
			return false;
		}

		public bool SetActivity(Activity wanted, long startedUnix)
		{
			var activity = new Dictionary<string, object>
			{
				["details"] = wanted.Details,
				["timestamps"] = new Dictionary<string, object> { ["start"] = startedUnix },
				["assets"] = new Dictionary<string, object>
				{
					["large_image"] = LargeImageKey,
					["large_text"] = LargeImageText,
				},
			};
			if (wanted.State.Length > 0)
			{
				activity["state"] = wanted.State;
			}

			if (wanted.PartyId.Length > 0)
			{
				// Co-op is two players. Discord wants the current size too; the session
				// machinery does not say how many are in, so an open session reads as one.
				activity["party"] = new Dictionary<string, object>
				{
					["id"] = wanted.PartyId,
					["size"] = new[] { 1, 2 },
				};
				activity["secrets"] = new Dictionary<string, object>
				{
					["join"] = wanted.JoinSecret,
				};
			}

			return Send(new Dictionary<string, object>
			{
				["cmd"] = "SET_ACTIVITY",
				["args"] = new Dictionary<string, object>
				{
					["pid"] = SysEnvironment.ProcessId,
					["activity"] = activity,
				},
				["nonce"] = Guid.NewGuid().ToString(),
			});
		}

		/// <summary>Approves or declines an "Ask to Join" from the Discord user given.</summary>
		public void AnswerJoinRequest(string userId, bool approve) => Send(new Dictionary<string, object>
		{
			["cmd"] = approve ? "SEND_ACTIVITY_JOIN_INVITE" : "CLOSE_ACTIVITY_REQUEST",
			["args"] = new Dictionary<string, object> { ["user_id"] = userId },
			["nonce"] = Guid.NewGuid().ToString(),
		});

		private void Subscribe(string eventName) => Send(new Dictionary<string, object>
		{
			["cmd"] = "SUBSCRIBE",
			["evt"] = eventName,
			["args"] = new Dictionary<string, object>(),
			["nonce"] = Guid.NewGuid().ToString(),
		});

		public void ClearActivity() => Send(new Dictionary<string, object>
		{
			["cmd"] = "SET_ACTIVITY",
			["args"] = new Dictionary<string, object> { ["pid"] = SysEnvironment.ProcessId },
			["nonce"] = Guid.NewGuid().ToString(),
		});

		public void Dispose()
		{
			Stream? stream = _stream;
			if (stream is not null)
			{
				try
				{
					Write(stream, OpClose, "{}");
				}
				catch (Exception exception) when (exception is IOException or SocketException
					or ObjectDisposedException)
				{
				}
			}

			Drop();
		}

		private bool Send(Dictionary<string, object> command)
		{
			Stream? stream = _stream;
			if (stream is null || _dropped)
			{
				return false;
			}

			try
			{
				Write(stream, OpFrame, JsonSerializer.Serialize(command));
				return true;
			}
			catch (Exception exception) when (exception is IOException or SocketException
				or ObjectDisposedException)
			{
				GD.Print("[presence] Discord connection lost: " + exception.Message);
				Drop();
				return false;
			}
		}

		private void Drop()
		{
			_dropped = true;
			Stream? stream = _stream;
			_stream = null;
			stream?.Dispose();
		}

		private void Write(Stream stream, int op, string json)
		{
			byte[] payload = Encoding.UTF8.GetBytes(json);
			byte[] frame = new byte[8 + payload.Length];
			BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
			BitConverter.TryWriteBytes(frame.AsSpan(4, 4), payload.Length);
			payload.CopyTo(frame, 8);
			lock (_gate)
			{
				stream.Write(frame, 0, frame.Length);
				stream.Flush();
			}
		}

		/// <summary>One frame's JSON, or a timeout; only used for the handshake reply.</summary>
		private static string ReadFrame(Stream stream, TimeSpan timeout)
		{
			Task<string> read = Task.Run(() => ReadFrame(stream));
			if (!read.Wait(timeout))
			{
				throw new TimeoutException("no reply to the handshake");
			}

			return read.Result;
		}

		private static string ReadFrame(Stream stream)
		{
			byte[] header = new byte[8];
			Fill(stream, header);
			int length = BitConverter.ToInt32(header, 4);
			if (length < 0 || length > 1 << 20)
			{
				throw new IOException("bad frame length " + length);
			}

			byte[] payload = new byte[length];
			Fill(stream, payload);
			return Encoding.UTF8.GetString(payload);
		}

		private static void Fill(Stream stream, byte[] buffer)
		{
			int got = 0;
			while (got < buffer.Length)
			{
				int n = stream.Read(buffer, got, buffer.Length - got);
				if (n <= 0)
				{
					throw new IOException("Discord closed the connection");
				}

				got += n;
			}
		}

		/// <summary>
		/// Reads Discord's frames so its send buffer never fills, keeping the join events for
		/// the main thread and discarding the rest.
		/// </summary>
		private void Drain(Stream stream)
		{
			try
			{
				while (ReferenceEquals(stream, _stream))
				{
					Keep(ReadFrame(stream));
				}
			}
			catch (Exception exception) when (exception is IOException or SocketException
				or ObjectDisposedException)
			{
				if (ReferenceEquals(stream, _stream))
				{
					_dropped = true;
				}
			}
		}

		/// <summary>Queues a frame if it is a join event; malformed JSON is dropped.</summary>
		private void Keep(string json)
		{
			try
			{
				using JsonDocument doc = JsonDocument.Parse(json);
				JsonElement root = doc.RootElement;
				if (!root.TryGetProperty("evt", out JsonElement evt) || evt.ValueKind != JsonValueKind.String ||
					!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
				{
					return;
				}

				switch (evt.GetString())
				{
					case "ACTIVITY_JOIN":
						if (data.TryGetProperty("secret", out JsonElement secret) &&
							secret.ValueKind == JsonValueKind.String)
						{
							Events.Enqueue(new Event(EventKind.Join, secret.GetString() ?? string.Empty, string.Empty));
						}

						break;

					case "ACTIVITY_JOIN_REQUEST":
						if (data.TryGetProperty("user", out JsonElement user) &&
							user.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
						{
							string name = user.TryGetProperty("username", out JsonElement username) &&
								username.ValueKind == JsonValueKind.String
								? username.GetString() ?? string.Empty
								: string.Empty;
							Events.Enqueue(new Event(EventKind.JoinRequest, id.GetString() ?? string.Empty, name));
						}

						break;
				}
			}
			catch (JsonException)
			{
			}
		}

		private static Stream? Open(string endpoint)
		{
			if (OperatingSystem.IsWindows())
			{
				if (!File.Exists(endpoint))
				{
					return null;
				}

				string name = Path.GetFileName(endpoint);
				var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
				pipe.Connect(500);
				return pipe;
			}

			if (!File.Exists(endpoint))
			{
				return null;
			}

			var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			socket.Connect(new UnixDomainSocketEndPoint(endpoint));
			return new NetworkStream(socket, ownsSocket: true);
		}

		/// <summary>
		/// Where Discord listens, in the order to try: `discord-ipc-0` to `-9`, under the
		/// runtime directory and the sandboxed (Flatpak, Snap) locations on Linux.
		/// </summary>
		private static IEnumerable<string> Endpoints()
		{
			if (OperatingSystem.IsWindows())
			{
				for (int i = 0; i < 10; i++)
				{
					yield return @"\\.\pipe\discord-ipc-" + i;
				}

				yield break;
			}

			string runtime = SysEnvironment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
				?? SysEnvironment.GetEnvironmentVariable("TMPDIR")
				?? "/tmp";
			string[] roots =
			{
				runtime,
				Path.Combine(runtime, "app", "com.discordapp.Discord"),
				Path.Combine(runtime, "snap.discord"),
				Path.Combine(runtime, "snap.discord-canary"),
			};
			foreach (string root in roots)
			{
				for (int i = 0; i < 10; i++)
				{
					yield return Path.Combine(root, "discord-ipc-" + i);
				}
			}
		}
	}
}
