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
/// pool took compiled, every light-group shader of every base material composed
/// (<see cref="Engine_ClassicLitInstances.PlanShaderWarm"/>: BLT 1/2/4/7 on all three passes,
/// whatever light later reaches it), and then the fixtures' light-group shapes
/// (<see cref="Engine_ClassicLitInstances.PlanWarm"/>).
/// The guest states its lights only once the world is activated, after the hold, so the warm
/// builds for every placed fixture that could reach an instance; a light the guest leaves out
/// costs only the build. Mist variants are left to play: a mist is stated by script.
/// </remarks>
public static class Engine_ClassicLoad
{
    /// <summary>A region is held for its load; variant builds take no frame budget.</summary>
    public static bool Holding { get; private set; }

    /// <summary>A hold and its settle have ended since the run began: what is built from here on is built during play.</summary>
    public static bool Released { get; private set; }

    /// <summary>
    /// After the hold: the guest is let go so it states its lights (it does so only once the world
    /// is activated), then frozen under the cover while the light-group shapes it states are built,
    /// until <see cref="QuietFrames"/> frames in a row build no variant, compile nothing and create
    /// no pipeline. Nothing of the region's first light assignment is built during play.
    /// </summary>
    public static bool Settling { get; private set; }

    /// <summary>The session takes no steps: settling, and the guest has stated its lights.</summary>
    public static bool FreezeGuest => Settling && _lightsStated;

    /// <summary>Variant builds take no frame budget: nothing is on screen.</summary>
    public static bool Unbudgeted => Holding || Settling;

    private const int QuietFrames = 30;

    /// <summary>A settle that never goes quiet hands the game back after this many frames.</summary>
    private const int SettleFrameLimit = 1800;

    /// <summary>Frames carrying lights before the guest counts as having stated them: one lit
    /// assignment round (Engine_ClassicLitInstances' eight slices) and a margin.</summary>
    private const int StatedFrames = 10;

    private static bool _lightsStated;
    private static int _litFrames;
    private static int _builtAtRelease;
    private static int _settleFrames;
    private static int _quiet;
    private static int _builtMark;
    private static double _pipelinesMark;
    private static long _settleSince;

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
        Abandon();
    }

    public static void End()
    {
        if (Holding)
        {
            Settling = true;
            _lightsStated = false;
            _litFrames = 0;
            _builtAtRelease = Engine_ClassicMaterial.VariantsBuilt;
            _settleFrames = 0;
            _quiet = 0;
            _builtMark = Engine_ClassicMaterial.VariantsBuilt;
            _pipelinesMark = Pipelines();
            _settleSince = System.Diagnostics.Stopwatch.GetTimestamp();
        }
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
            Engine_ClassicLitInstances.PlanShaderWarm();
            GD.Print($"[classic] load warm: {Engine_ClassicLitInstances.WarmPending} light-group shape(s) " +
                $"for {_fixtures.Count} placed light(s), light-group shaders for " +
                $"{Engine_ClassicLitInstances.ShaderWarmPending} base material(s)");
        }
        Engine_ClassicLitInstances.PumpShaderWarm(WarmBudgetTicks);
        if (Engine_ClassicLitInstances.ShaderWarmPending != 0)
        {
            return $"light-group shaders for {Engine_ClassicLitInstances.ShaderWarmPending} base material(s), " +
                $"{Engine_ClassicShaders.Compiling} shader(s) compiling";
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

    /// <summary>A region going away mid-settle: the guest is not held for a world that is gone.</summary>
    public static void Abandon()
    {
        Settling = false;
        _lightsStated = false;
    }

    /// <summary>
    /// One settle frame, after the frame's light assignment (Engine_ClassicPath). Lights count
    /// as stated on the first feed frame that carries any; from then on the guest is frozen.
    /// </summary>
    public static void PumpSettle()
    {
        if (!Settling)
        {
            return;
        }
        ++_settleFrames;
        if (!_lightsStated)
        {
            _litFrames = Engine_ClassicLights.Counts.Lights > 0 ? _litFrames + 1 : 0;
            _lightsStated = _litFrames >= StatedFrames;
            _builtMark = Engine_ClassicMaterial.VariantsBuilt;
            _pipelinesMark = Pipelines();
            if (_settleFrames < SettleFrameLimit)
            {
                return;
            }
        }
        int built = Engine_ClassicMaterial.VariantsBuilt;
        double pipelines = Pipelines();
        bool quiet = built == _builtMark && pipelines == _pipelinesMark &&
            Engine_ClassicShaders.Compiling == 0 && Engine_ClassicTransfer.PendingCount == 0;
        _builtMark = built;
        _pipelinesMark = pipelines;
        _quiet = quiet ? _quiet + 1 : 0;
        if (_quiet < QuietFrames && _settleFrames < SettleFrameLimit)
        {
            return;
        }
        double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - _settleSince) / (double)System.Diagnostics.Stopwatch.Frequency;
        GD.Print(_quiet >= QuietFrames
            ? $"[classic] settled in {seconds:0.00} s ({_settleFrames} frame(s)): {built - _builtAtRelease} light-group variant(s) " +
                "built for the stated lights under the cover, the guest resumes"
            : $"[classic] settle did not go quiet after {_settleFrames} frame(s) ({seconds:0.00} s): handing the game back anyway");
        Settling = false;
        _lightsStated = false;
        Released = true;
    }

    private static double Pipelines() =>
        Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSurface) +
        Performance.GetMonitor(Performance.Monitor.PipelineCompilationsDraw) +
        Performance.GetMonitor(Performance.Monitor.PipelineCompilationsSpecialization) +
        Performance.GetMonitor(Performance.Monitor.PipelineCompilationsMesh);
}
