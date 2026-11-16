namespace Refii.Titles.Fable2;

using Refii.Host;
using Refii.Level;
using Refii.Game;

/// <summary>
/// Fable 2's side of the one online weather (<see cref="Engine_CoopWeather"/>): the co-op session's roles,
/// and the mod event as the guest tick's "ModWeather:&lt;from_host&gt;|&lt;seconds&gt;|&lt;level&gt;|&lt;weather&gt;|&lt;previous&gt;"
/// pump action (guest_tick_pump.cpp).
/// </summary>
public partial class GameUi
{
    private const string ModWeatherAction = "ModWeather:";

    private readonly Engine_CoopWeather _coopWeather = new();

    /// <summary>Once a frame: the weather in effect stated to the mod bus; the host's statement out; a joiner marked as told.</summary>
    private void DriveCoopWeather(double delta)
    {
        bool online = CoopSession.PeerStanding && !CoopSession.LocalCoop;
        _coopWeather.Drive(delta, online, CoopSession.IsClient,
            (remote, seconds, level, weather, previous) => HostBridge.PostFrontEndAction(
                ModWeatherAction + (remote ? "1" : "0") + "|" + seconds + "|" + level + "|" + weather + "|" + previous),
            HostBridge.CoopSend);
    }

    /// <summary>The host's weather statement, taken on a joiner.</summary>
    private static bool TakeWeatherMessage(string message) => Engine_CoopWeather.Take(message, CoopSession.IsClient);
}
