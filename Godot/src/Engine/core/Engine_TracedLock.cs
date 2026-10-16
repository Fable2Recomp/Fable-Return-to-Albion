using System.Threading;

namespace Refii.Core;

/// <summary>
/// A lock the in-game Tracy capture can see — waits, acquisitions and holds — for the gates
/// Godot's main and render threads share. `using (Gate.Hold()) { … }` where `lock (Gate)` was.
/// </summary>
/// <remarks>
/// Monitor underneath, so re-entry by the owning thread still works; only the outermost
/// acquisition is reported, as Tracy's lock model is not recursive. Tracy counts every reported
/// acquisition against a release, so the owner remembers which one it reported. Registered with
/// Tracy on first use after the profiler starts; until <see cref="Hooks"/> is installed (Tracy on)
/// this is a plain Monitor lock.
/// </remarks>
public sealed class Engine_TracedLock
{
	/// <summary>The profiler's lock calls, installed by the session when Tracy is on.</summary>
	public interface IHooks
	{
		ulong Announce(string name);

		bool Before(ulong lockHandle);

		void After(ulong lockHandle);

		void AfterUnlock(ulong lockHandle);
	}

	public static IHooks? Hooks;

	private readonly object _monitor = new();
	private readonly string _name;
	private ulong _handle;
	private ulong _held;
	private int _depth;

	public Engine_TracedLock(string name)
	{
		_name = name;
	}

	public Scope Hold()
	{
		IHooks? hooks = Hooks;
		if (hooks is null || Monitor.IsEntered(_monitor))
		{
			Monitor.Enter(_monitor);
			_depth++;
			return new Scope(this);
		}

		ulong handle = Volatile.Read(ref _handle);
		if (handle == 0)
		{
			handle = hooks.Announce(_name);
			if (handle != 0)
			{
				ulong raced = Interlocked.CompareExchange(ref _handle, handle, 0);
				handle = raced != 0 ? raced : handle;
			}
		}

		bool recorded = handle != 0 && hooks.Before(handle);
		Monitor.Enter(_monitor);
		if (recorded)
		{
			hooks.After(handle);
		}

		_held = handle;
		_depth++;
		return new Scope(this);
	}

	private void Release()
	{
		ulong held = 0;
		if (--_depth == 0)
		{
			held = _held;
			_held = 0;
		}

		Monitor.Exit(_monitor);
		if (held != 0)
		{
			Hooks?.AfterUnlock(held);
		}
	}

	public readonly struct Scope : System.IDisposable
	{
		private readonly Engine_TracedLock _owner;

		internal Scope(Engine_TracedLock owner)
		{
			_owner = owner;
		}

		public void Dispose() => _owner.Release();
	}
}
