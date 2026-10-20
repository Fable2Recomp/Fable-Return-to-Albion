using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// PLAYER TWO'S HOUSEHOLD ONLINE, BOTH WAYS. Player two's household lives on the host's copy of
/// hero two (household_persist.cpp); the joiner's estate screen, gifts and furniture work on a
/// mirror, so each change they make for hero two comes out of the bridge as an intent
/// (<see cref="HostBridge.TakeEstateIntent"/>) and goes to the host, which runs it for its hero two;
/// the host's answers (a gift kept) come back the same way. Either side runs what it receives as
/// the pump's <c>Estate:</c> action. The channel drops datagrams: an intent is numbered and
/// restated (<c>estate.do=</c>) until the peer acknowledges it (<c>estate.ack=</c>), and each side
/// runs the peer's numbers once, in order.
/// </summary>
public partial class GameUi
{
	private const string EstateDoPrefix = "estate.do=";
	private const string EstateAckPrefix = "estate.ack=";
	private const double EstateRestateSeconds = 0.5;

	private readonly List<(uint Seq, string Text)> _estateOut = new();
	private uint _estateSeq;
	private double _estateRestate;

	/// <summary>The receiving side: the highest of the peer's numbers run this session (from 1).</summary>
	private uint _estateRan;

	/// <summary>Once a frame: the joiner's intents out, restated until acknowledged.</summary>
	private void DriveCoopEstate(double delta)
	{
		while (HostBridge.TakeEstateIntent() is { } intent)
		{
			if (CoopSession.PeerStanding && !CoopSession.LocalCoop)
			{
				_estateOut.Add((++_estateSeq, intent));
				_estateRestate = 0.0;
			}
		}

		if (!CoopSession.PeerStanding)
		{
			_estateOut.Clear();
			_estateSeq = 0u;
			_estateRan = 0u;
			return;
		}

		_estateRestate -= delta;
		if (_estateOut.Count == 0 || _estateRestate > 0.0)
		{
			return;
		}

		_estateRestate = EstateRestateSeconds;
		foreach ((uint seq, string text) in _estateOut)
		{
			HostBridge.CoopSend(EstateDoPrefix + seq.ToString(CultureInfo.InvariantCulture) + "|" + text);
		}
	}

	/// <summary>A household message from the peer: its intent (run here) or its ack of ours.</summary>
	private bool TakeEstateMessage(string message)
	{
		if (message.StartsWith(EstateDoPrefix, System.StringComparison.Ordinal))
		{
			string body = message[EstateDoPrefix.Length..];
			int bar = body.IndexOf('|');
			if (bar > 0 &&
				uint.TryParse(body[..bar], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint seq))
			{
				// Run in order, once each: a number past the next one waits for its restatement.
				if (seq == _estateRan + 1u)
				{
					_estateRan = seq;
					HostBridge.PostFrontEndAction("Estate:" + body[(bar + 1)..]);
					GD.Print($"[coop-estate] the peer's {body[(bar + 1)..]} runs here");
				}

				if (seq <= _estateRan)
				{
					HostBridge.CoopSend(EstateAckPrefix + seq.ToString(CultureInfo.InvariantCulture));
				}
			}

			return true;
		}

		if (message.StartsWith(EstateAckPrefix, System.StringComparison.Ordinal))
		{
			if (uint.TryParse(message[EstateAckPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint seq))
			{
				_estateOut.RemoveAll(entry => entry.Seq <= seq);
			}

			return true;
		}

		return false;
	}
}
