namespace Refii.Lighting;

/// <summary>
/// Whether the post chain's own sources (the sun's beams and disc, <see cref="Engine_PostFxSunSources"/>,
/// and the glow sprites, <see cref="Refii.Fx.Engine_PostFxHalos"/>) can go unbuilt this frame: on a
/// phone, while the chain does not composite.
/// </summary>
/// <remarks>
/// WHY. Both are read by the chain alone (Engine_PostFxEffect, after it has found its colour layer
/// usable), so where it refuses the layer they are never drawn. On the Mobile renderer without MSAA,
/// every phone preset, that is always (<see cref="Engine_PostFxRoute"/>). Building them anyway was a
/// hitch on entering a town: on the S10e one frame spent 271 ms loading the sun's textures (the path
/// index, the read and decode, and the render thread's texture) and 81 ms decoding, resizing and
/// re-stacking halo sprites, all for layers nothing showed.
///
/// WHEN. The chain off, or its route anything but Usable: Unusable, or Unknown before it has looked
/// (a phone with MSAA staging would find it Usable, and builds from the next frame). The skip leaves
/// the publishers' state as it was, so the frame the gate opens builds what it would have.
/// The desktop never skips.
/// </remarks>
public static class Engine_PostFxUnseen
{
    /// <summary>Whether this frame's sun sources and halos may be left unbuilt.</summary>
    public static bool Now =>
        Refii.Core.Engine_Platform.IsAndroid && Skips(Engine_PostFxParams.Enabled, Engine_PostFxRoute.Current);

    /// <summary>The gate itself: nothing the chain would read reaches the screen. Pure.</summary>
    /// <param name="chainEnabled">Whether the chain is on (<see cref="Engine_PostFxParams.Enabled"/>).</param>
    /// <param name="route">The route view 0's chain last published.</param>
    public static bool Skips(bool chainEnabled, Engine_PostFxRoute.State route) =>
        !chainEnabled || route != Engine_PostFxRoute.State.Usable;
}
