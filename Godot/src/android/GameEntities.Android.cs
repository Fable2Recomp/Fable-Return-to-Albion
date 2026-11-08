namespace Refii.Game;

public partial class GameEntities
{
    /// <summary>Under the loading screen the builds are all that is left to wait for, so a phone runs
    /// more of them at once (about 4-5 s off a level change). Desktop keeps the usual limit.</summary>
    private static int MaxInFlightLoadBuilds => Refii.Core.Engine_Platform.IsAndroid ? 10 : MaxInFlightBuilds;

}
