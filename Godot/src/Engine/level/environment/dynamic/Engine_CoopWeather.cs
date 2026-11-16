using System;
using System.Globalization;

namespace Refii.Level;

/// <summary>
/// ONE DYNAMIC WEATHER ONLINE: the host's. The host states its decided weather with the level it is for
/// (<see cref="Engine_DynamicWeatherChoice.Statement"/>) on the co-op channel when it changes and once a
/// second after (the channel drops datagrams); the joiner decides nothing while the session stands and
/// shows the host's statement for its level. Each player's own switches only show or hide the dynamic
/// sky on their machine. Every change of the weather in effect is also stated to the mod bus
/// (dynamic_weather), through the title's own transport.
/// </summary>
public sealed class Engine_CoopWeather
{
	/// <summary>The co-op channel line's prefix.</summary>
	public const string Prefix = "weather=";

	private const double RestateSeconds = 1.0;

	private double _restate;
	private int _sent = -1;
	private int _stated;
	private string _statedToken = string.Empty;

	/// <summary>
	/// Once a frame. <paramref name="online"/>: an online session stands; <paramref name="client"/>: this
	/// machine joined it. <paramref name="stateEvent"/> carries the mod event (from_host, seconds, level,
	/// weather, previous) on the title's transport; <paramref name="send"/> sends a co-op channel line.
	/// </summary>
	public void Drive(double delta, bool online, bool client, Action<bool, string, string, string, string> stateEvent,
		Func<string, bool> send)
	{
		StateEvent(stateEvent);
		Engine_DynamicWeatherChoice.Remote = online && client;
		if (!online || client)
		{
			_sent = -1;
			return;
		}

		_restate -= delta;
		if (_sent == Engine_DynamicWeatherChoice.Generation && _restate > 0.0)
		{
			return;
		}

		_restate = RestateSeconds;
		_sent = Engine_DynamicWeatherChoice.Generation;
		send(Prefix + Engine_DynamicWeatherChoice.Statement);
	}

	/// <summary>The weather in effect changed (this machine's decision, or the host's statement adopted): the mod event.</summary>
	private void StateEvent(Action<bool, string, string, string, string> stateEvent)
	{
		int generation = Engine_DynamicWeatherChoice.Generation;
		if (generation == _stated)
		{
			return;
		}

		_stated = generation;
		string token = Engine_DynamicWeatherChoice.Token;
		stateEvent(Engine_DynamicWeatherChoice.Remote, Engine_DynamicWeatherChoice.BlendSeconds.ToString("R", CultureInfo.InvariantCulture),
			Engine_DynamicWeatherChoice.Level, token, _statedToken);
		_statedToken = token;
	}

	/// <summary>The host's weather statement off the co-op channel, adopted on a joiner. True when the line was one.</summary>
	public static bool Take(string message, bool client)
	{
		if (!message.StartsWith(Prefix, StringComparison.Ordinal))
		{
			return false;
		}

		if (client)
		{
			Engine_DynamicWeatherChoice.State(message[Prefix.Length..]);
		}

		return true;
	}
}
