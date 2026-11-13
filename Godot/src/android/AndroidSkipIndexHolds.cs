using System;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_skip_index_rig_props` and `android_skip_index_buildings`: what more the skip index
/// (`entity_skip_index`) holds on a phone. Both on by default; the desktop holds neither.
/// </summary>
/// <remarks>
/// RIG PROPS. A door, a sign or a lamp with dummy bones has a skeleton, so the index never held it:
/// each one walked its prologue, placement and pose gate every walk to write nothing, about 20 µs a
/// row on the S10e for some 160 of them in the Bowerstone market. Held, only its pose gate is asked
/// (whether it is in view, which the guest's 30-frame hold reads later), until the settled-prop check
/// that reads its clips again, one walk in six, or its statement changes. Most of a market's props
/// are out of view with clips the walk last read before they settled (a prop is written on its first
/// walk and then only in view): those are held too, while their gate says out of view, until the
/// reconcile; one coming into view walks its row at once.
///
/// BUILDINGS. A multi-mesh appearance (no model hash of its own) is kept off the walk's fast path
/// while the first ten reports count it, which in a market that never settles is several minutes;
/// held, its row replays only what that full prologue repeats (its zone record, its counts, its
/// interior node's layers). The same picture either way. Live: a `cvar_set` of a key applies at once.
/// </remarks>
public static class AndroidSkipIndexHolds
{
    public const string RigPropsKey = "android_skip_index_rig_props";

    public const string BuildingsKey = "android_skip_index_buildings";

    private static bool _hooked;

    /// <summary>
    /// The walks a held rigged prop's verdict lasts at most: a settled prop's settled-prop check
    /// (Engine_EntitySkipIndex.RigPropRecheckTicks, `Due(address, 6)` in its row), the walk its clips
    /// are read again; an unsettled one's (held only out of view, where its row reads no clips and
    /// writes no bones) the reconcile, the only other walk its row does something (its full
    /// prologue). Both divide the reconcile, so neither is held across it. Pure.
    /// </summary>
    public static int RigPropRecheckTicks(bool unsettled, int reconcileTicks) =>
        unsettled ? reconcileTicks : Refii.Entities.Engine_EntitySkipIndex.RigPropRecheckTicks;

    /// <summary>What a setting means: on unless it reads false. Pure.</summary>
    public static bool Holds(System.Collections.Generic.IReadOnlyDictionary<string, string> values, string key) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, key, true);

    /// <summary>Sets both from the config, and follows live changes from then on. Main thread.</summary>
    public static void Apply()
    {
        Refii.Game.GameEntities.HoldRigProps = Read(RigPropsKey);
        Refii.Game.GameEntities.HoldBuildings = Read(BuildingsKey);
        if (!_hooked)
        {
            _hooked = true;
            Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
            {
                if (string.Equals(name, RigPropsKey, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, BuildingsKey, StringComparison.OrdinalIgnoreCase))
                {
                    Apply();
                }
            };
        }
    }

    private static bool Read(string key)
    {
        try
        {
            return Holds(Refii.Core.Engine_RefiiConfig.Read(out _), key);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
