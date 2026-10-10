using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.Launcher.Recompile;

/// <summary>
/// The player's hand on a running game code build: pause, resume, stop.
/// </summary>
/// <remarks>
/// ONE OBJECT PER BUILD, handed to <see cref="GameCodeBuild.RunAsync"/> and held by the UI
/// for as long as the build runs. The pipeline consults it at the points where work can be
/// held or abandoned without leaving anything half-written that the next build would not
/// clean up:
///
///   generate   — at every progress event the generator raises (its phase boundaries), on
///                the generator's own thread, which simply waits inside the callback
///   precompile, compile, link — every compiler process is attached here for as long as it
///                runs, and a pause FREEZES it where it is (see ProcessSuspension); the
///                compile phase also hands out no new translation units while paused
///
/// A PAUSE HOLDS, A STOP ABANDONS. Paused, the machine goes idle at once: the running
/// compilers are suspended mid-instruction and resumed from the same place, so no work is
/// lost or repeated. Stopped, the running compiles are killed, the generator unwinds at its
/// next event, and the partial output is deleted — the next build starts from nothing, which
/// is what every build does anyway. Stop also releases a pause, so a paused build does not
/// have to be resumed to be stopped.
/// </remarks>
public sealed class BuildControl : IDisposable
{
	private readonly CancellationTokenSource _stop = new();
	private readonly object _gate = new();

	// Completed while the build may run; replaced with a pending one while paused. Every
	// waiter awaits whichever instance is current, so a resume releases all of them.
	private TaskCompletionSource _resumed = Completed();

	// The compiler processes running right now, so a pause can reach them.
	private readonly HashSet<Process> _processes = new();

	private static TaskCompletionSource Completed()
	{
		var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		source.SetResult();
		return source;
	}

	/// <summary>Cancelled when a stop is requested.</summary>
	public CancellationToken StopToken => _stop.Token;

	public bool IsPaused
	{
		get
		{
			lock (_gate)
			{
				return !_resumed.Task.IsCompleted;
			}
		}
	}

	public bool IsStopping => _stop.IsCancellationRequested;

	public void Pause()
	{
		lock (_gate)
		{
			if (!_resumed.Task.IsCompleted || IsStopping)
			{
				return;
			}

			_resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			foreach (Process process in _processes)
			{
				ProcessSuspension.Suspend(process);
			}
		}
	}

	public void Resume()
	{
		lock (_gate)
		{
			if (_resumed.Task.IsCompleted)
			{
				return;
			}

			foreach (Process process in _processes)
			{
				ProcessSuspension.Resume(process);
			}
			_resumed.TrySetResult();
		}
	}

	public void Stop()
	{
		_stop.Cancel();
		// A paused build has threads parked in WaitWhilePaused and compilers frozen; the
		// cancellation wakes the async waiters by itself, and this wakes the blocking one and
		// thaws the compilers so the kill that follows the cancellation finds them running.
		Resume();
	}

	/// <summary>
	/// Puts a compiler process under this control for as long as it runs. Attached while
	/// paused, it is frozen on the spot: the unit passed the pause gate before the pause
	/// was asked for, and its compiler must not run on while everything else is held.
	/// </summary>
	internal void Attach(Process process)
	{
		lock (_gate)
		{
			_processes.Add(process);
			if (!_resumed.Task.IsCompleted)
			{
				ProcessSuspension.Suspend(process);
			}
		}
	}

	internal void Detach(Process process)
	{
		lock (_gate)
		{
			_processes.Remove(process);
		}
	}

	/// <summary>Returns once the build may continue. Throws when stopped.</summary>
	public Task WaitWhilePausedAsync(CancellationToken cancellation)
	{
		TaskCompletionSource current;
		lock (_gate)
		{
			current = _resumed;
		}

		return current.Task.WaitAsync(cancellation);
	}

	/// <summary>
	/// Blocking twin of <see cref="WaitWhilePausedAsync"/> for the generator's thread, which
	/// is inside a native call and cannot await. Never throws: a stop returns too, and the
	/// caller reads <see cref="IsStopping"/>.
	/// </summary>
	public void WaitWhilePaused()
	{
		TaskCompletionSource current;
		lock (_gate)
		{
			current = _resumed;
		}

		try
		{
			current.Task.Wait(_stop.Token);
		}
		catch (OperationCanceledException)
		{
		}
	}

	public void Dispose() => _stop.Dispose();
}
