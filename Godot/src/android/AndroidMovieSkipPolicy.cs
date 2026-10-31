using System;

namespace Refii.AndroidRuntime;

/// <summary>Times explicit skips of real native movie requests without decoding video or advancing menus.</summary>
/// <remarks>Startup logos are embedded buffers with an empty pathname. Request generation and
/// payload presence identify them just as they identify named requests; a name is not required.</remarks>
public sealed class AndroidMovieSkipPolicy
{
    private readonly bool _enabled;
    private readonly double _dwell;
    private bool _haveRequest, _attempted;
    private uint _generation;
    private double _requestedAt;

    /// <summary>Creates the configured policy; disabled policies never request a retirement.</summary>
    /// <param name="enabled">Whether the user explicitly selected movie skipping.</param>
    /// <param name="dwellSeconds">Finite monotonic dwell in the supported 0.5–60 second range.</param>
    public AndroidMovieSkipPolicy(bool enabled, double dwellSeconds)
    {
        if (!double.IsFinite(dwellSeconds) || dwellSeconds < 0.5 || dwellSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(dwellSeconds));
        _enabled = enabled;
        _dwell = dwellSeconds;
    }

    /// <summary>Returns true exactly once per real request generation after its dwell.</summary>
    /// <param name="wanted">True only for the native Wanted state; ended/absent movies are ignored.</param>
    /// <param name="generation">Native shared buffer/name request generation.</param>
    /// <param name="hasPayload">A nonzero embedded buffer size or nonempty requested pathname.</param>
    /// <param name="monotonicSeconds">Seconds from a monotonic stopwatch.</param>
    /// <returns>Whether the owner should call production FinishMovie between service steps.</returns>
    public bool ShouldFinish(bool wanted, uint generation, bool hasPayload, double monotonicSeconds)
    {
        if (!_enabled || !wanted || !hasPayload || !double.IsFinite(monotonicSeconds)) return false;
        if (!_haveRequest || generation != _generation)
        {
            _haveRequest = true;
            _generation = generation;
            _requestedAt = monotonicSeconds;
            _attempted = false;
        }
        if (_attempted || monotonicSeconds - _requestedAt < _dwell) return false;
        _attempted = true;
        return true;
    }
}
