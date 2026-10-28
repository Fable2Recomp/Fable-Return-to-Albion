using System;

namespace Refii.AndroidRuntime;

/// <summary>Starts the existing entity reader early enough to overlap Android UI and world work.</summary>
/// <remarks>This schedules a read, never a guest step. The existing worker owns pending-result
/// admission, list reuse and completion; GameEntities consumes that result in the same frame.</remarks>
internal static class AndroidEntityFeed
{
    /// <summary>Samples the guest frame before deciding whether its entities need a new read.</summary>
    /// <param name="forceFeed">The existing presentation diagnostic requests a read every frame.</param>
    /// <param name="sampleFrame">Publishes the current guest frame without executing the guest.</param>
    /// <param name="frameChanged">Reads the newly published change flag.</param>
    /// <param name="kickWalk">Requests the existing worker; pending or disabled requests are ignored there.</param>
    internal static void SampleAndKick(bool forceFeed, Action sampleFrame,
        Func<bool> frameChanged, Action kickWalk)
    {
        sampleFrame();
        if (frameChanged() || forceFeed) kickWalk();
    }
}
