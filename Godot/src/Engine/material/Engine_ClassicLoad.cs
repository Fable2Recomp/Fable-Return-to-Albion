using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's share of the region load hold (GameWorld): the guest stays parked
/// until every shader the region draws with has compiled and every material is on its final
/// shape, so nothing is composed, compiled or rebound on the main thread once play starts.
/// </summary>
/// <remarks>
/// What the hold waits for, in order: the props streamed and their vertex bakes and ambient
/// variants applied (the streamer), every instance's transfer pair bound, every composition the
/// pool took compiled, and then the light-group warm (<see cref="Engine_ClassicLitInstances.PlanWarm"/>).
/// The guest states its lights only once the world is activated, after the hold, so the warm
/// builds for every placed fixture that could reach an instance; a light the guest leaves out
/// costs only the build. Mist variants are left to play: a mist is stated by script.
/// </remarks>
public static class Engine_ClassicLoad
{
    /// <summary>A region is held for its load; variant builds take no frame budget.</summary>
    public static bool Holding { get; private set; }

    /// <summary>A hold has ended since the run began: what is built from here on is built during play.</summary>
    public static bool Released { get; private set; }

    private static Node? _region;
    private static bool _planned;
    private static readonly List<(Vector3 Centre, float Reach)> _fixtures = new();

    /// <summary>A frame's share of the warm while held; nothing is on screen.</summary>
    private static readonly long WarmBudgetTicks = System.Diagnostics.Stopwatch.Frequency / 20;

    public static void Begin(Node region)
    {
        Holding = Engine_ClassicMaterial.Active;
        _region = region;
        _planned = false;
    }

    public static void End()
    {
        Released |= Holding;
        Holding = false;
        _region = null;
        _planned = false;
        Engine_ClassicLitInstances.ClearWarm();
    }

    /// <summary>
    /// One held frame: what the classic side still waits for, or empty when it is done.
    /// <paramref name="worldSettled"/>: the props, their bakes and the entities are in.
    /// </summary>
    public static string Pump(bool worldSettled)
    {
        if (!Holding || _region is null || !GodotObject.IsInstanceValid(_region))
        {
            return string.Empty;
        }
        if (!worldSettled)
        {
            return "the world";
        }
        if (Engine_ClassicTransfer.PendingCount != 0)
        {
            return $"{Engine_ClassicTransfer.PendingCount} transfer pair(s)";
        }
        if (!_planned)
        {
            if (Engine_ClassicShaders.Compiling != 0)
            {
                return $"{Engine_ClassicShaders.Compiling} shader(s) compiling";
            }
            _planned = true;
            Engine_ClassicPlacedLights.Reaches(_region, _fixtures);
            Engine_ClassicLitInstances.PlanWarm(_fixtures);
            GD.Print($"[classic] load warm: {Engine_ClassicLitInstances.WarmPending} light-group shape(s) " +
                $"for {_fixtures.Count} placed light(s)");
        }
        Engine_ClassicLitInstances.PumpWarm(WarmBudgetTicks);
        if (Engine_ClassicLitInstances.WarmPending != 0)
        {
            return $"{Engine_ClassicLitInstances.WarmPending} light-group shape(s), " +
                $"{Engine_ClassicShaders.Compiling} shader(s) compiling";
        }
        if (Engine_ClassicShaders.Compiling != 0)
        {
            return $"{Engine_ClassicShaders.Compiling} shader(s) compiling";
        }
        return string.Empty;
    }
}
