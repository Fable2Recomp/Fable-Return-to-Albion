using System.Threading.Tasks;
using Godot;
using Refii.Host;
using Refii.Mods;
using Refii.Ui;

namespace Refii.Game;

/// <summary>
/// Both co-op players run the same mods, or the session is refused.
/// </summary>
/// <remarks>
/// THE RECORD IS STATE, LIKE EVERYTHING ELSE ON THE CHANNEL. Each side restates its
/// <see cref="ModFingerprint"/> (<c>mods=…</c>) on every co-op tick, and each side compares the
/// peer's stated digest with its own once the guest says a peer is in the session
/// (<see cref="CoopSession.PeerStanding"/>: the host's client-joined byte, the joiner's accept
/// byte). Before that the role bytes do not yet say who is who — a joiner's own solo game reads
/// as a host — so a record that arrives early is only stored.
///
/// A MISMATCH IS REFUSED ON BOTH MACHINES. The diff is said on the multiplayer row's page (never
/// a box) and the session is left the guest's own way, the pump's <c>CoopLeave</c>: on the host
/// that drops the joiner, on the joiner it leaves the host. Either side refusing ends the
/// session, so a peer whose build does not check still cannot play on with different mods. A
/// peer that talks on the channel but never states its mods is an older build and is refused
/// the same way.
/// </remarks>
public partial class GameUi
{
	/// <summary>The pump action that leaves the co-op session as the guest's own Reject does.</summary>
	private const string CoopLeaveAction = "CoopLeave";

	/// <summary>
	/// Channel messages a standing peer may send without its mods record before it is taken for
	/// a build that never states one. It restates every half second beside several other
	/// records, so a peer that does state it is never this far behind.
	/// </summary>
	private const int CoopModsUnstatedLimit = 20;

	/// <summary>
	/// This instance's set, hashed off the main thread from the first frame on. Kept for the
	/// process: the mods are mounted once, when the game starts, so the set a session runs is
	/// the set the process started with.
	/// </summary>
	private Task<ModFingerprint>? _coopModsOwn;

	private bool _coopModsFaultSaid;

	/// <summary>The peer's last stated set, or null.</summary>
	private ModFingerprint? _coopModsPeer;

	/// <summary>The digest last logged as agreeing, so the agreement is said once.</summary>
	private string _coopModsAgreedSaid = string.Empty;

	/// <summary>Whether this session was refused; nothing is checked again until it resets.</summary>
	private bool _coopModsRefused;

	/// <summary>Messages from the standing peer before its first mods record.</summary>
	private int _coopModsUnstated;

	/// <summary>The own set once hashed, or null while it is still being hashed.</summary>
	private ModFingerprint? CoopModsOwn()
	{
		_coopModsOwn ??= Task.Run(() => ModFingerprint.Current());
		if (_coopModsOwn.IsFaulted && !_coopModsFaultSaid)
		{
			// Said, not hidden: with no set to state, every peer refuses this one.
			_coopModsFaultSaid = true;
			GD.PushError($"[game-ui] co-op — the mod set could not be read: {_coopModsOwn.Exception?.GetBaseException().Message}");
		}

		return _coopModsOwn.IsCompletedSuccessfully ? _coopModsOwn.Result : null;
	}

	/// <summary>Restates this instance's set to the peer; nothing until it is hashed.</summary>
	private void SendCoopMods()
	{
		if (CoopModsOwn() is { } own)
		{
			HostBridge.CoopSend(own.Encode());
		}
	}

	/// <summary>
	/// Takes one channel message. True when it was the peer's mods record (stored); any other
	/// message from a standing peer counts toward <see cref="CoopModsUnstatedLimit"/>.
	/// </summary>
	private bool TakeCoopMods(string message)
	{
		if (!message.StartsWith(ModFingerprint.WirePrefix, System.StringComparison.Ordinal))
		{
			if (CoopSession.PeerStanding && _coopModsPeer is null)
			{
				++_coopModsUnstated;
			}

			return false;
		}

		if (ModFingerprint.TryDecode(message, out ModFingerprint? peer) && peer is not null)
		{
			_coopModsPeer = peer;
		}

		return true;
	}

	/// <summary>
	/// Compares the sets once a peer stands in the session, and refuses a mismatch. Called once a
	/// frame after the inbox is drained.
	/// </summary>
	private void CheckCoopMods(uint role)
	{
		// Starts the hash on the first frame, long before a join, so a first-time hash of a large
		// package is not still running when the peer starts counting.
		ModFingerprint? own = CoopModsOwn();
		if (_coopModsRefused || !CoopSession.PeerStanding || role == 0u || own is null)
		{
			return;
		}

		string self = role == 1u ? "host" : "joiner";
		string other = role == 1u ? "joiner" : "host";
		if (_coopModsPeer is not { } peer)
		{
			if (_coopModsUnstated >= CoopModsUnstatedLimit)
			{
				RefuseCoopForMods(
					$"the {other}'s Refii does not state its mods",
					$"Co-op refused: the {other}'s Refii does not state its mods, so it is an older build.\n\n" +
					"Both players need the same Refii build and the same mods.");
			}

			return;
		}

		if (peer.Digest() == own.Digest())
		{
			if (_coopModsAgreedSaid != own.Digest())
			{
				_coopModsAgreedSaid = own.Digest();
				GD.Print($"[game-ui] co-op — mods agree with the {other} ({own.Mods.Count} enabled, digest {own.Digest()})");
			}

			return;
		}

		string diff = own.Describe(peer, self, other);
		RefuseCoopForMods(
			$"digest {own.Digest()} here, {peer.Digest()} on the {other}: {diff}",
			$"Co-op refused: your mods and the {other}'s differ.\n\n{Capitalised(diff)}.\n\n" +
			"Both players need the same mods, enabled in the same order.");
	}

	/// <summary>Says the refusal on the multiplayer row's page and leaves the session; once a session.</summary>
	private void RefuseCoopForMods(string logged, string said)
	{
		_coopModsRefused = true;
		GD.Print($"[game-ui] co-op — REFUSED: {logged}; leaving the session");
		Engine_UiNetworkSource.Say(said, hold: true);
		HostBridge.PostFrontEndAction(CoopLeaveAction);
	}

	/// <summary>Drops the peer's set and the verdict, so the next session is checked afresh.</summary>
	private void ResetCoopMods()
	{
		_coopModsPeer = null;
		_coopModsAgreedSaid = string.Empty;
		_coopModsRefused = false;
		_coopModsUnstated = 0;
	}

	private static string Capitalised(string text) =>
		text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
