using Refii.Host;
using Refii.Level;

namespace Refii.Game;

/// <summary>
/// ONE DYNAMIC WEATHER ONLINE: the host's. The host states its decided weather with the level it is for
/// (<see cref="Engine_DynamicWeatherChoice.Statement"/>) when it changes and once a second after (the channel
/// drops datagrams); the joiner decides nothing while the session stands and shows the host's statement for its
/// level. Each player's own switches only show or hide the dynamic sky on their machine.
/// </summary>
public partial class GameUi
{
    private const string WeatherPrefix = "weather=";
    private const double WeatherRestateSeconds = 1.0;

    private double _weatherRestate;
    private int _weatherSent = -1;

    /// <summary>Once a frame: the host's statement out; a joiner marked as told.</summary>
    private void DriveCoopWeather(double delta)
    {
        bool online = CoopSession.PeerStanding && !CoopSession.LocalCoop;
        Engine_DynamicWeatherChoice.Remote = online && CoopSession.IsClient;
        if (!online || CoopSession.IsClient)
        {
            _weatherSent = -1;
            return;
        }

        _weatherRestate -= delta;
        if (_weatherSent == Engine_DynamicWeatherChoice.Generation && _weatherRestate > 0.0)
        {
            return;
        }

        _weatherRestate = WeatherRestateSeconds;
        _weatherSent = Engine_DynamicWeatherChoice.Generation;
        HostBridge.CoopSend(WeatherPrefix + Engine_DynamicWeatherChoice.Statement);
    }

    /// <summary>The host's weather statement, taken on a joiner.</summary>
    private static bool TakeWeatherMessage(string message)
    {
        if (!message.StartsWith(WeatherPrefix, System.StringComparison.Ordinal))
        {
            return false;
        }

        if (CoopSession.IsClient)
        {
            Engine_DynamicWeatherChoice.State(message[WeatherPrefix.Length..]);
        }

        return true;
    }
}
