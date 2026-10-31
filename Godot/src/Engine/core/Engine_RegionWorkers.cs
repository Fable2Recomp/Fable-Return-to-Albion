using System;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.Core;

/// <summary>Tracks asset producers through their last publication without retaining their scene owner.</summary>
/// <remarks>Retirement stops new work and completion publication, but never disposes borrowed
/// resources or cancels an executing decoder. The global count includes detached prefetch workers.</remarks>
public sealed class Engine_RegionWorkers
{
    private readonly object _gate = new();
    private bool _retired;
    private int _active;
    private static int _globalActive;

    /// <summary>All tracked region producers, including work scheduled but not yet executing.</summary>
    public static int GlobalActive => Volatile.Read(ref _globalActive);

    /// <summary>This owner's producers; completion application on the main thread is not counted.</summary>
    public int Active { get { lock (_gate) return _active; } }

    /// <summary>Whether the owner has closed its publication channel.</summary>
    public bool Retired { get { lock (_gate) return _retired; } }

    /// <summary>Schedules one producer, counting before scheduling and releasing after its final publication.</summary>
    /// <param name="work">An existing asset producer, with its own error reporting.</param>
    /// <param name="schedule">An optional native-free test scheduler; production uses Task.Run.</param>
    /// <returns>False if retirement already closed the owner.</returns>
    internal bool TryQueue(Action work, Func<Action, Task>? schedule = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_retired) return false;
            ++_active;
            Interlocked.Increment(ref _globalActive);
        }
        int finished = 0;
        void FinishOnce() { if (Interlocked.Exchange(ref finished, 1) == 0) Finish(); }
        try
        {
            (schedule ?? Task.Run)(() =>
            {
                try { work(); }
                finally { FinishOnce(); }
            });
        }
        catch { FinishOnce(); throw; }
        return true;
    }

    /// <summary>Publishes a managed completion only while the scene owner is still accepting work.</summary>
    /// <param name="publish">A short managed queue operation; it must not call Godot or perform decoding.</param>
    /// <returns>False for a departing owner, leaving result ownership with the producer.</returns>
    public bool TryPublish(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_gate)
        {
            if (_retired) return false;
            publish();
            return true;
        }
    }

    /// <summary>Closes new work and atomically drops this owner's already queued borrowed completions.</summary>
    /// <param name="dropQueued">An optional managed queue drain, without native resource disposal.</param>
    public void Retire(Action? dropQueued = null)
    {
        lock (_gate)
        {
            _retired = true;
            dropQueued?.Invoke();
        }
    }

    /// <summary>Releases a producer after all its publication attempts have finished.</summary>
    private void Finish()
    {
        lock (_gate)
        {
            --_active;
            Interlocked.Decrement(ref _globalActive);
        }
    }
}
