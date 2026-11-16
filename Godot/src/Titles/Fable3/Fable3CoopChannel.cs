namespace Refii.Titles.Fable3;

using Refii.Host;
using Refii.Level;

/// <summary>
/// REFII'S CO-OP CHANNEL ON FABLE 3: what the runtime owes the peer goes out every frame, and what the
/// peer sent comes in, while the game is in an online session (the payload states it from
/// Network.IsInLiveGame: HostBridge.PlayerOwned 1 or 2). Fable 3's own co-op traffic is the game's;
/// this carries only Refii's lines (the players' mod values, relayed events, the route).
/// </summary>
public partial class Fable3CoopChannel : Godot.Node
{
	private readonly Engine_CoopWeather _weather = new();

	public override void _Process(double delta)
	{
		bool online = HostBridge.PlayerOwned() is 1u or 2u;
		bool client = HostBridge.PlayerOwned() == 2u;
		if (HostBridge.IsInGame)
		{
			_weather.Drive(delta, online, client,
				(remote, seconds, level, weather, previous) => HostBridge.PostModCommand(
					$"dynamic_weather {(remote ? 1 : 0)} {seconds} {Word(level)} {Word(weather)} {Word(previous)}"),
				HostBridge.CoopSend);
		}

		// Drained every frame, read only online: a line from a peer that has gone belongs to no one.
		while (HostBridge.CoopReceive() is { } message)
		{
			if (online && !Engine_CoopWeather.Take(message, client))
			{
				HostBridge.PlayerStateReceive(message);
			}
		}

		string[] lines = HostBridge.PlayerStateOutgoing();
		if (!online)
		{
			return;
		}

		foreach (string line in lines)
		{
			HostBridge.CoopSend(line);
		}
	}

	private static string Word(string text) => text.Length == 0 ? "-" : text.Replace(' ', '_');
}
