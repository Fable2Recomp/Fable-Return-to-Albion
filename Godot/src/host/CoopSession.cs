using System;
using System.Collections.Generic;

namespace Refii.Host;

/// <summary>
/// The co-op session as ONE model: role, state, the two players and their hero kits, polled
/// once a frame from the guest's authoritative network manager and never from raw bytes
/// anywhere else. Fable 2 answers "which player am I" by re-reading two bytes through a copy on
/// the game object every time, tears the peer down inline on a lost connection and leaves raw
/// pointers behind; this is the per-connection identity, ownership and lifecycle a multiplayer
/// engine would have given the game, supplied from the host side.
/// </summary>
public static class CoopSession
{
	public enum Phase { Solo, Hosting, PeerJoined, PeerLost }

	public enum Role { Solo, Host, Client }

	/// <summary>One player slot: 1 is the host's hero, 2 the joiner's.</summary>
	public sealed class Player
	{
		public int Slot { get; init; }

		/// <summary>The live entity in THIS instance's world, 0 when it has none.</summary>
		public uint Entity { get; internal set; }

		/// <summary>Whether this slot is the local machine's own player.</summary>
		public bool IsLocal { get; internal set; }

		public string Name { get; internal set; } = string.Empty;

		/// <summary>The hero record: the local save or live body for the local slot, the wire for the other.</summary>
		public CoopHeroKit.Kit Kit { get; internal set; }
	}

	public static Phase State { get; private set; } = Phase.Solo;

	public static Role Side { get; private set; } = Role.Solo;

	public static Player One { get; } = new() { Slot = 1 };

	public static Player Two { get; } = new() { Slot = 2 };

	/// <summary>The local machine's player, by role: slot 1 unless this instance is the client.</summary>
	public static Player Local => Side == Role.Client ? Two : One;

	public static Player Remote => Side == Role.Client ? One : Two;

	/// <summary>Whether a peer is in the session right now (not merely a session flag).</summary>
	public static bool PeerStanding => State == Phase.PeerJoined;

	public static bool IsClient => Side == Role.Client;

	/// <summary>Raised on the frame a peer's presence begins / ends.</summary>
	public static event Action? PeerJoined;

	public static event Action? PeerLost;

	private static HostBridge.CoopSessionWords _last;

	/// <summary>Polls the bridge and advances the state machine. Call once per frame.</summary>
	public static void Poll()
	{
		if (HostBridge.CoopSessionRaw() is not { } words)
		{
			Transition(Phase.Solo, Role.Solo);
			One.Entity = 0u;
			Two.Entity = 0u;
			return;
		}

		_last = words;
		One.Entity = words.PlayerOne;
		Two.Entity = words.PlayerTwo;

		// ROLE: the guest's own reading of its bytes. Note a solo game carries {session, host}
		// too (Frontend_SetStartGameTarget sets them on every start), so "host" alone is not
		// "in co-op"; the peer's presence is the client-joined byte, or being the client.
		Role side = !words.ManagerPresent || !words.Session ? Role.Solo
			: words.Client ? Role.Client
			: Role.Host;
		bool peer = words.Session && !words.Lost &&
			(side == Role.Client || (side == Role.Host && words.ClientJoined));

		Phase next = peer ? Phase.PeerJoined
			: State == Phase.PeerJoined || State == Phase.PeerLost && words.Lost ? Phase.PeerLost
			: side == Role.Host && words.Session ? Phase.Hosting
			: Phase.Solo;
		Transition(next, side);
	}

	private static void Transition(Phase next, Role side)
	{
		Side = side;
		One.IsLocal = side != Role.Client;
		Two.IsLocal = side == Role.Client;
		if (next == State)
		{
			return;
		}

		Phase was = State;
		State = next;
		Godot.GD.Print($"[coop-session] {was} → {next} (role {side}, peers {_last.PeerCount}, " +
			$"one 0x{One.Entity:X8}, two 0x{Two.Entity:X8}" +
			$"{(_last.GameCopyPresent ? string.Empty : ", game+140 copy EMPTY")})");

		if (next == Phase.PeerJoined)
		{
			PeerJoined?.Invoke();
		}
		else if (was == Phase.PeerJoined)
		{
			// THE LIFECYCLE THE GUEST DOES NOT HAVE: everything learned about the peer goes
			// with the peer, so a later join starts clean and nothing restates a dead body.
			Remote.Name = string.Empty;
			Remote.Kit = default;
			PeerLost?.Invoke();
		}
	}

	/// <summary>Whether a player slot's entity exists in this world and the session still stands for it.</summary>
	public static bool BodyStands(Player player) =>
		player.Entity != 0u && (player.IsLocal || PeerStanding);
}
