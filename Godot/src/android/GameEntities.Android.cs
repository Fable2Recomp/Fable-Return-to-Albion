namespace Refii.Game;

public partial class GameEntities
{
    /// <summary>Under the loading screen the builds are all that is left to wait for and the guest is
    /// held, so more of them run at once (about 4-5 s off a level change on a phone). Desktop and phone
    /// alike: builds already run three at a time, so this adds no sharing that was not there, and the
    /// statics `Engine_BnkMeshLoader._Load` reaches were audited for it (pr29 fixb item 8).</summary>
    private const int MaxInFlightLoadBuilds = 10;

}
