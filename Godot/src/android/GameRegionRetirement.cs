using System;

namespace Refii.Game;

/// <summary>The next main-thread action at an Android region-retirement boundary.</summary>
public enum RegionRetirementWork { None, ClearAndCollect, Collect, Complete, Failed }

/// <summary>Native-free retirement ordering: quiescent producers, a deletion frame, bounded collection passes and a final frame.</summary>
/// <remarks>Frame separation permits queued native deletion/render retirement; it does not prove
/// allocations were reclaimed. Failure never authorizes a new overlapping region build.</remarks>
public sealed class GameRegionRetirement
{
    /// <summary>Maximum collection/finalizer passes, allowing mesh/material/texture wrapper chains to weaken in turn.</summary>
    public const int CollectionPasses = 3;
    /// <summary>Finite active-application deadline; a timeout is an explicit failure requiring recovery.</summary>
    /// <remarks>Room for the last pass's compaction on a phone out of memory, where the heap has to come back from swap first.</remarks>
    public const double TimeoutSeconds = 120;
    private readonly double _started;
    private ulong _minimumFrame;
    private string _stage = "waiting-workers";
    private int _passes;
    private int _workers;
    private double _elapsed;
    private string _error = string.Empty;
    private double _lastClock, _pauseStarted, _excludedPauseSeconds;
    private bool _applicationPaused;

    /// <summary>A value-only observation, safe to serialize without retaining a departing scene.</summary>
    /// <param name="Stage">Current retirement stage.</param><param name="ElapsedSeconds">Measured boundary wall time.</param>
    /// <param name="Workers">Outstanding producers or the abandoned region builder.</param>
    /// <param name="FinalizerPasses">Completed collection/finalizer passes.</param><param name="NewBuildBlocked">Whether starting a new build remains forbidden.</param>
    /// <param name="Error">An explicit failure, otherwise empty.</param>
    /// <param name="ApplicationPaused">Whether rendered retirement epochs currently wait for application resume.</param>
    /// <param name="ExcludedPauseSeconds">Observed background time excluded from the active deadline.</param>
    public readonly record struct Snapshot(string Stage, double ElapsedSeconds, int Workers,
        int FinalizerPasses, bool NewBuildBlocked, string Error, bool ApplicationPaused, double ExcludedPauseSeconds);

    /// <summary>Starts the gate immediately after outgoing nodes were queued for deletion.</summary>
    /// <param name="frame">Current rendered frame.</param><param name="seconds">Monotonic wall time.</param>
    /// <param name="applicationPaused">Whether this boundary begins while the application is already paused.</param>
    public GameRegionRetirement(ulong frame, double seconds, bool applicationPaused = false)
    {
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        _started = seconds;
        _lastClock = seconds;
        _applicationPaused = applicationPaused;
        _pauseStarted = seconds;
        _minimumFrame = frame + 1;
    }

    /// <summary>Whether the collection pass this gate asks for next is its last.</summary>
    public bool NextCollectionIsLast => _passes == CollectionPasses - 1;

    /// <summary>The last observed immutable state.</summary>
    public Snapshot Status => new(_stage, _elapsed, _workers, _passes, _stage != "complete", _error,
        _applicationPaused, _excludedPauseSeconds + (_applicationPaused ? _lastClock - _pauseStarted : 0));

    /// <summary>Excludes background intervals without restarting or extending the active-work allowance.</summary>
    /// <param name="paused">Actual application pause/resume state; repeated notifications are idempotent.</param>
    /// <param name="seconds">Monotonic wall time at this notification.</param>
    /// <returns>A terminal failure for an invalid clock, otherwise no work.</returns>
    public RegionRetirementWork SetApplicationPaused(bool paused, double seconds)
    {
        if (_stage is "complete" or "failed") return RegionRetirementWork.None;
        if (!ObserveClock(seconds)) return Fail("invalid retirement pause clock");
        if (paused == _applicationPaused) return RegionRetirementWork.None;
        if (paused) _pauseStarted = seconds;
        else _excludedPauseSeconds += seconds - _pauseStarted;
        _applicationPaused = paused;
        return RegionRetirementWork.None;
    }

    /// <summary>Keeps the native preload fence only when a new destination exists and can inherit it.</summary>
    /// <remarks>The guest main loop can wait on that fence before advancing an early level-change state.
    /// Retirement prevents overlapping host builds; it cannot hold every guest phase until a destination exists.</remarks>
    /// <param name="inGame">Whether the guest remains in gameplay.</param>
    /// <param name="currentKey">The current nonempty level's key, or null before a destination exists.</param>
    /// <param name="departingKey">The level key recorded at teardown.</param>
    /// <param name="fullLoadState">A discovered full-load command's state, otherwise null (including load-save).</param>
    /// <returns>True only when keeping the fence cannot prevent discovery of the destination.</returns>
    public static bool PreservePreloadHold(bool inGame, uint? currentKey, uint departingKey, uint? fullLoadState) =>
        inGame && currentKey is { } key && (departingKey == 0 || key != departingKey) &&
        (fullLoadState is null || fullLoadState >= 3);

    /// <summary>Waits another deletion frame when an abandoned worker produced a late detached root.</summary>
    /// <param name="frame">Frame in which that root was queued for deletion.</param>
    public void RequireDeletionFrame(ulong frame) => _minimumFrame = Math.Max(_minimumFrame, frame + 1);

    /// <summary>Advances one main-thread observation without waiting or invoking native APIs.</summary>
    /// <param name="frame">Current process frame.</param><param name="seconds">Monotonic wall time.</param>
    /// <param name="workers">Nonnegative executing/scheduled producer count.</param>
    /// <param name="collectionFinished">Whether the current background finalizer pass completed.</param>
    /// <param name="collectionError">Failure from that pass, otherwise empty.</param>
    /// <returns>A single action to execute; completed and failed gates never repeat an action.</returns>
    public RegionRetirementWork Advance(ulong frame, double seconds, int workers,
        bool collectionFinished, string collectionError = "")
    {
        if (_stage is "complete" or "failed") return RegionRetirementWork.None;
        _workers = Math.Max(0, workers);
        if (!ObserveClock(seconds) || workers < 0)
            return Fail("invalid retirement clock or worker count");
        // A blocking compaction holds the main thread, so its finish is first seen late: work that is done is not timed out.
        bool lastPassDone = _passes == CollectionPasses || (_stage == "collecting" && NextCollectionIsLast && collectionFinished);
        if (_elapsed >= TimeoutSeconds && !lastPassDone)
            return Fail($"retirement timed out during {_stage} after {_elapsed:0.00}s ({_workers} worker(s), {_passes} pass(es))");
        if (_stage != "waiting-workers" && workers != 0)
            return Fail("a region producer resumed after retirement was quiescent");
        // A running background finalizer may complete or fail while paused. Preserve that
        // observation, but do not start another cache/frame operation until resume.
        if (_applicationPaused && _stage != "collecting") return RegionRetirementWork.None;
        if (_stage == "waiting-workers")
        {
            if (workers != 0 || frame < _minimumFrame) return RegionRetirementWork.None;
            _stage = "collecting";
            return RegionRetirementWork.ClearAndCollect;
        }
        if (_stage == "collecting")
        {
            if (!collectionFinished) return RegionRetirementWork.None;
            if (collectionError.Length != 0) return Fail(collectionError);
            ++_passes;
            _minimumFrame = frame + 1;
            _stage = "waiting-frame";
            return RegionRetirementWork.None;
        }
        if (frame < _minimumFrame) return RegionRetirementWork.None;
        if (_passes == CollectionPasses)
        {
            _stage = "complete";
            return RegionRetirementWork.Complete;
        }
        _stage = "collecting";
        return RegionRetirementWork.Collect;
    }

    /// <summary>Records one terminal diagnostic without allowing the next build.</summary>
    private RegionRetirementWork Fail(string error)
    {
        _stage = "failed";
        _error = error;
        return RegionRetirementWork.Failed;
    }

    /// <summary>Validates monotonic observations and measures active time without native lifecycle calls.</summary>
    private bool ObserveClock(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < _lastClock) return false;
        _lastClock = seconds;
        _elapsed = seconds - _started - _excludedPauseSeconds - (_applicationPaused ? seconds - _pauseStarted : 0);
        return true;
    }
}
