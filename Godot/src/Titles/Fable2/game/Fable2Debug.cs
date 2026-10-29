using System.Collections.Generic;
using System.Text.Json;

namespace Refii.Titles.Fable2;

using Refii.Host;

/// <summary>
/// Fable 2's own debug-channel ops (<see cref="Refii.Core.Engine_TitleGame.DebugRequest"/>): the
/// Golden Cock's interaction and the co-op stack.
/// </summary>
public static class Fable2Debug
{
	public static readonly IReadOnlyList<string> Ops = new[] { "goldencock", "coop" };

	public static Dictionary<string, object?>? Request(string op, JsonElement request) => op switch
	{
		"goldencock" => GoldenCock(),
		"coop" => Coop(),
		_ => null,
	};

	private static Dictionary<string, object?> GoldenCock()
	{
		var state = GameGoldenCockCollision.Diagnostics();
		state["interaction"] = Fable2Bridge.GetGoldenCockInteraction();
		state["menuOpen"] = GameGoldenCock.OwnsInput;
		return state;
	}

	private static Dictionary<string, object?> Coop()
	{
		// THE CO-OP STACK IN ONE READ: role and phase, both bodies, what each player
		// holds, and the HeroState versions on every side of the protocol — the answer
		// that took four logs to stitch together.
		(uint one, uint two) = Refii.Host.HostBridge.CoopPlayerEntities();
		var held = new Dictionary<string, object?>();
		for (uint player = 1u; player <= 2u; ++player)
		{
			held[$"player{player}"] = Fable2Bridge.CoopHeldUids(player) is { } ids
				? new Dictionary<string, object?>
				{
					["melee"] = $"{ids.Melee:X16}",
					["ranged"] = $"{ids.Ranged:X16}",
				}
				: null;
		}

		Fable2Bridge.CoopSessionWords? words = Fable2Bridge.CoopSessionRaw();
		return (new Dictionary<string, object?>
		{
			["side"] = CoopSession.Side.ToString(),
			["phase"] = CoopSession.State.ToString(),
			["localCoop"] = CoopSession.LocalCoop,
			["words"] = words is { } w
				? new Dictionary<string, object?>
				{
					["session"] = w.Session, ["host"] = w.Host, ["client"] = w.Client,
					["clientJoined"] = w.ClientJoined, ["peers"] = w.PeerCount,
				}
				: null,
			["playerOne"] = $"{one:X8}",
			["playerTwo"] = $"{two:X8}",
			["ownHero"] = Fable2PageProviders.CoopOwnHero,
			["heroAvailable"] = Fable2PageProviders.CoopHeroAvailable,
			["names"] = new Dictionary<string, object?>
			{
				["one"] = Fable2PageProviders.CoopPlayerOneName,
				["two"] = Fable2PageProviders.CoopPlayerTwoName,
			},
			["held"] = held,
			// The players' mod values (core/player_state.h): owners, versions, pending writes, keys.
			["playerState"] = Refii.Host.HostBridge.PlayerStateDescribe(),
			["heroState"] = new Dictionary<string, object?>
			{
				["localKnown"] = CoopHeroKit.Local.Known,
				["remoteKnown"] = CoopHeroKit.Remote.Known,
				["chosenSaveRead"] = CoopHeroKit.ChosenSaveRead,
				["sentVersion"] = CoopHeroState.Version,
				["peerAckedOurs"] = CoopHeroState.AckedVersion,
				["receivedVersion"] = CoopHeroState.ReceivedVersion,
				["ackedToPeer"] = CoopHeroState.AckedToPeer,
				["ackDue"] = CoopHeroState.AckDue,
				["appliedPlayer1"] = Fable2Bridge.HeroStateApplied(1u),
				["appliedPlayer2"] = Fable2Bridge.HeroStateApplied(2u),
			},
		});
	}
}
