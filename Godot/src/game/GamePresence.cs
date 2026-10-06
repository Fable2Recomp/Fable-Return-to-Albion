using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using Refii.Core;
using Refii.Host;
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
/// The game's name and artwork belong to the Discord application in the developer portal, not
/// to us: the application is named "Fable 2: Return to Albion" and carries one art asset keyed
/// `logo`. Its ID goes in `discord_application_id` in refii.toml, or <see cref="DefaultApplicationId"/>.
/// </remarks>
public partial class GamePresence : Node
{
	/// <summary>The project's Discord application, "Fable 2: Return to Albion".</summary>
	private const string DefaultApplicationId = "1556952543400173668";

	private const string LargeImageKey = "logo";

	private const string LargeImageText = "Fable 2: Return to Albion";

	/// <summary>How often the shown text is re-derived; a change is sent at once.</summary>
	private const double PollSeconds = 2.0;

	/// <summary>How long after a failed or dropped connection before the next attempt.</summary>
	private const double RetrySeconds = 15.0;

	private readonly DiscordIpc _discord = new();

	private string _applicationId = string.Empty;

	private double _poll;

	private double _sinceAttempt = RetrySeconds;

	private (string Details, string State) _shown;

	private long _startedUnix;

	public override void _Ready()
	{
		IReadOnlyDictionary<string, string> config = Engine_RefiiConfig.Read(out _);
		bool enabled = Engine_RefiiConfig.Bool(config, "discord_presence", true);
		_applicationId = Engine_RefiiConfig.String(
			config, "discord_application_id", DefaultApplicationId).Trim();

		if (!enabled || _applicationId.Length == 0)
		{
			GD.Print(enabled
				? "[presence] discord_application_id is blank; Discord presence off"
				: "[presence] discord_presence = false");
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
			if (!_discord.Connect(_applicationId, out string why))
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

		(string Details, string State) wanted = Describe();
		if (wanted == _shown)
		{
			return;
		}

		if (_discord.SetActivity(wanted.Details, wanted.State, _startedUnix))
		{
			_shown = wanted;
			GD.Print($"[presence] {wanted.Details} — {wanted.State}");
		}
	}

	/// <summary>The two lines Discord shows under the game's name.</summary>
	private static (string Details, string State) Describe()
	{
		if (HostBridge.TryGetAbiVersion() is null)
		{
			return ("Starting up", string.Empty);
		}

		if (!HostBridge.IsInGame)
		{
			return ("At the main menu", string.Empty);
		}

		if (GameWhereabouts.Current is { } place && place.DisplayName.Length > 0)
		{
			return (place.DisplayName, "Exploring Albion");
		}

		return ("Travelling", "Exploring Albion");
	}

	/// <summary>
	/// The Discord IPC client: one connection, a handshake, then SET_ACTIVITY frames. Every
	/// frame is a little-endian opcode and length followed by JSON. Responses are drained on a
	/// background task and discarded; an error there drops the connection.
	/// </summary>
	private sealed class DiscordIpc : IDisposable
	{
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
				return true;
			}

			// No endpoint at all is the ordinary case — Discord is not running — and `why`
			// stays empty so the caller does not log it every retry.
			return false;
		}

		public bool SetActivity(string details, string state, long startedUnix)
		{
			var activity = new Dictionary<string, object>
			{
				["details"] = details,
				["timestamps"] = new Dictionary<string, object> { ["start"] = startedUnix },
				["assets"] = new Dictionary<string, object>
				{
					["large_image"] = LargeImageKey,
					["large_text"] = LargeImageText,
				},
			};
			if (state.Length > 0)
			{
				activity["state"] = state;
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

		/// <summary>Reads and discards Discord's replies so its send buffer never fills.</summary>
		private void Drain(Stream stream)
		{
			try
			{
				while (ReferenceEquals(stream, _stream))
				{
					ReadFrame(stream);
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
