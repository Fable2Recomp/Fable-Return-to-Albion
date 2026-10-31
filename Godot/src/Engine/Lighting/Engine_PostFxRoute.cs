using System;
using System.Text.Json;
using System.Threading;

namespace Refii.Lighting;

/// <summary>
/// Whether the post chain can reach the scene's colour layer, as view 0's chain last found it:
/// the gate of the layers that only reach the screen through that chain, the additive effects
/// layer (<see cref="Refii.Fx.Engine_FxAdditiveLayer"/>) and the targeting highlight
/// (<see cref="Refii.Entities.Engine_EntityHighlightLayer"/>).
/// </summary>
/// <remarks>
/// On the Mobile renderer without MSAA (the phone presets) the chain never runs: its colour layer is A2B10G10R10 (`hdr_2d` off) or
/// RGBA16F without the storage bit, and without MSAA there is no staging route
/// (<see cref="Engine_PostFxModel.ChooseColourRoute"/>). The layers used to be gated only on the
/// chain being enabled, so on Android every ONE/ONE particle went to a half-size viewport nothing
/// composited, and the fire glow and sparks were never seen. Until a chain has inspected its layer
/// the route is unknown and the layers stay shut, so the particles draw in the scene, as they do
/// without the chain; they move to the layer on the frame after the chain first finds it usable.
///
/// Written on the render thread by the chain, read on the main thread: one int, and the owner
/// under a lock only when it changes.
/// </remarks>
public static class Engine_PostFxRoute
{
    /// <summary>What the last inspection of view 0's colour layer said.</summary>
    public enum State
    {
        /// <summary>No chain has inspected a colour layer (none attached, or not yet rendered).</summary>
        Unknown,

        /// <summary>The chain runs: direct storage or MSAA staging.</summary>
        Usable,

        /// <summary>The chain refuses the colour layer and keeps neutral exposure.</summary>
        Unusable,
    }

    /// <summary>The debug channel's `render` lever (`fxLegacyLayers`).</summary>
    public const string LegacyRequestKey = "fxLegacyLayers";

    private static readonly object Gate = new();
    private static object? _owner;
    private static int _state;

    /// <summary>
    /// `render {"fxLegacyLayers": true}`: the layers open whenever the chain is enabled, whatever
    /// the route, as before. For A/B only; on Mobile it hides the additive effects again.
    /// </summary>
    public static bool LegacyLayers { get; set; }

    /// <summary>The route as last published.</summary>
    public static State Current => (State)Volatile.Read(ref _state);

    /// <summary>Whether a layer the chain composites reaches the screen this frame.</summary>
    public static bool LayersReachScreen => Allows(LegacyLayers, Current);

    /// <summary>The gate itself. Pure.</summary>
    /// <param name="legacy">The A/B lever: the old gate, the chain being enabled, only.</param>
    /// <param name="state">The published route.</param>
    public static bool Allows(bool legacy, State state) => legacy || state == State.Usable;

    /// <summary>The state a chain's route stands for. Pure.</summary>
    public static State Of(Engine_PostFxModel.ColourRoute route) =>
        route == Engine_PostFxModel.ColourRoute.Unusable ? State.Unusable : State.Usable;

    /// <summary>Render thread: <paramref name="owner"/>'s chain found <paramref name="route"/>.</summary>
    internal static void Publish(object owner, Engine_PostFxModel.ColourRoute route)
    {
        int state = (int)Of(route);
        if (ReferenceEquals(Volatile.Read(ref _owner), owner) && Volatile.Read(ref _state) == state)
        {
            return;
        }

        lock (Gate)
        {
            Volatile.Write(ref _owner, owner);
            Volatile.Write(ref _state, state);
        }
    }

    /// <summary>
    /// <paramref name="owner"/>'s chain is going away; the route is unknown again unless another
    /// chain has published since (a level change builds the next one before the last is freed).
    /// </summary>
    internal static void Withdraw(object owner)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }

            Volatile.Write(ref _owner, null);
            Volatile.Write(ref _state, (int)State.Unknown);
        }
    }

    /// <summary>Validates the optional lever before the debug endpoint mutates rendering state.</summary>
    /// <returns>The requested flag or null without changing process state.</returns>
    internal static bool? ReadRequest(JsonElement request)
    {
        if (!request.TryGetProperty(LegacyRequestKey, out JsonElement value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ArgumentException(LegacyRequestKey + " must be a boolean.");
        return value.GetBoolean();
    }
}
