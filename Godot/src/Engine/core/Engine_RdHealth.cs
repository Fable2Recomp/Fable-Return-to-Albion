using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace Refii.Core;

/// <summary>
/// Shared RenderingDevice health gate and circuit breaker. Once a GPU allocation, pipeline,
/// canvas texture, or command submission returns an invalid RID (such as after D3D12
/// DXGI_ERROR_DEVICE_REMOVED / 0x887a0005), <see cref="IsDeviceLost"/> latches once so
/// subsequent frames halt GPU submissions cleanly and avoid multi-million-line log floods.
/// </summary>
public static class Engine_RdHealth
{
	private static readonly bool RunningInTestHost =
		AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase);

	private static int _deviceLost;
	private static long _deviceLostTimestampMs;
	private static string? _deviceLostReason;
	private static int _suppressedCount;
	private static readonly object ThrottleGate = new();
	private static readonly Dictionary<string, (long LastTickMs, int Suppressed)> Throttled =
		new(StringComparer.Ordinal);
	private static readonly object HandlerGate = new();
	private static Action? _onDeviceLost;

	/// <summary>True once a fatal RenderingDevice / GPU failure has latched in this process.</summary>
	public static bool IsDeviceLost => Volatile.Read(ref _deviceLost) != 0;

	/// <summary>True when executing inside the unit test runner (<c>testhost</c>).</summary>
	public static bool IsTestHost => RunningInTestHost;

	/// <summary>Human-readable context recorded when the circuit breaker first tripped.</summary>
	public static string? DeviceLostReason => Volatile.Read(ref _deviceLostReason);

	/// <summary>Environment tick count (ms) when the circuit breaker first tripped.</summary>
	public static long DeviceLostTimestampMs => Volatile.Read(ref _deviceLostTimestampMs);

	/// <summary>Number of subsequent operations suppressed after the initial trip.</summary>
	public static int SuppressedCount => Volatile.Read(ref _suppressedCount);

	/// <summary>Records one skipped submission after <see cref="IsDeviceLost"/> has already latched.</summary>
	public static void NoteSuppressed() => Interlocked.Increment(ref _suppressedCount);

	/// <summary>
	/// Registers a host callback invoked on the main thread when the circuit breaker first trips
	/// (e.g., to disable secondary viewport updates cleanly).
	/// </summary>
	public static void RegisterDeviceLostHandler(Action? handler)
	{
		lock (HandlerGate)
		{
			_onDeviceLost = handler;
		}
	}

	/// <summary>Resets latched health state and throttled log windows (for tests or re-init).</summary>
	public static void Reset()
	{
		Volatile.Write(ref _deviceLost, 0);
		Volatile.Write(ref _deviceLostTimestampMs, 0);
		Volatile.Write(ref _deviceLostReason, null);
		Volatile.Write(ref _suppressedCount, 0);
		lock (ThrottleGate)
		{
			Throttled.Clear();
		}
		lock (HandlerGate)
		{
			_onDeviceLost = null;
		}
	}

	/// <summary>True when fatal device loss should also halt Godot's render loop.</summary>
	public static bool ShouldHaltRenderLoopOnDeviceLost(bool isTestHost) => !isTestHost;

	/// <summary>Pure bounds check for recorded <c>ArrayMesh</c> surface indices.</summary>
	public static bool ShouldAcceptSurfaceIndex(int surface, int surfaceCount) =>
		surface >= 0 && surface < surfaceCount;

	/// <summary>
	/// Verifies that an <see cref="ArrayMesh"/> surface creation actually succeeded
	/// (<c>GetSurfaceCount() &gt; countBefore</c>); trips the circuit breaker when the RD backend
	/// rejected vertex/index buffer creation (e.g., <c>0x887a0005</c>).
	/// </summary>
	public static bool CheckArrayMeshSurfaceAdded(ArrayMesh? mesh, int countBefore, string context)
	{
		if (IsDeviceLost)
		{
			Interlocked.Increment(ref _suppressedCount);
			return false;
		}

		if (mesh is not null &&
		    GodotObject.IsInstanceValid(mesh) &&
		    mesh.GetRid().IsValid &&
		    mesh.GetSurfaceCount() > countBefore)
		{
			return true;
		}

		_ = TripDeviceLost(context, "ArrayMesh surface creation failed");
		return false;
	}

	/// <summary>
	/// Latches the GPU circuit breaker. Only the first transition emits an error log; later calls
	/// increment <see cref="SuppressedCount"/> without allocating or flooding logs.
	/// </summary>
	/// <returns>True if this call triggered the initial transition from healthy to lost.</returns>
	public static bool TripDeviceLost(string context, string? detail = null)
	{
		if (Interlocked.CompareExchange(ref _deviceLost, 1, 0) != 0)
		{
			Interlocked.Increment(ref _suppressedCount);
			return false;
		}

		string reason = string.IsNullOrEmpty(detail) ? context : $"{context}: {detail}";
		Volatile.Write(ref _deviceLostReason, reason);
		Volatile.Write(ref _deviceLostTimestampMs, System.Environment.TickCount64);

		if (ShouldHaltRenderLoopOnDeviceLost(RunningInTestHost))
		{
			// Single error line for the entire session after device loss, then halt RenderingServer
			// submissions so C++ CreateResource errors cannot flood godot.log.
			GD.PushError($"[rd-health] GPU device lost / RD resource creation failed ({reason}); latching circuit breaker.");
			TryHaltRenderLoop();
		}

		InvokeDeviceLostHandler();
		return true;
	}

	private static void InvokeDeviceLostHandler()
	{
		Action? handler;
		lock (HandlerGate)
		{
			handler = _onDeviceLost;
		}

		if (handler is null)
		{
			return;
		}

		try
		{
			if (RunningInTestHost || Engine_ServerSyncs.OnMainThread)
			{
				handler();
			}
			else
			{
				Callable.From(handler).CallDeferred();
			}
		}
		catch
		{
			// Best-effort quiesce only during device loss or teardown.
		}
	}

	private static void TryHaltRenderLoop()
	{
		try
		{
			if (Engine_ServerSyncs.OnMainThread)
			{
				RenderingServer.RenderLoopEnabled = false;
			}
			else
			{
				Callable.From(static () => RenderingServer.RenderLoopEnabled = false).CallDeferred();
			}
		}
		catch
		{
			// Ignore during shutdown or headless test hosts without a RenderingServer singleton.
		}
	}

	/// <summary>
	/// Verifies a low-level <see cref="RenderingDevice"/> RID; trips the circuit breaker when invalid.
	/// </summary>
	public static bool CheckRdValid(Rid rid, string context)
	{
		if (IsDeviceLost)
		{
			Interlocked.Increment(ref _suppressedCount);
			return false;
		}

		if (rid.IsValid)
		{
			return true;
		}

		_ = TripDeviceLost(context, "invalid RenderingDevice RID");
		return false;
	}

	/// <summary>
	/// Verifies a <see cref="RenderingServer"/> canvas texture RID, optionally probing its backing
	/// RD texture RID (<paramref name="verifyRdBacking"/> should be true on D3D12 single-threaded
	/// mode or from the render thread, avoiding synchronous <c>push_and_ret</c> stalls on Vulkan).
	/// </summary>
	public static bool CheckTextureValid(Rid canvasTextureRid, string context, bool verifyRdBacking = false)
	{
		if (IsDeviceLost)
		{
			Interlocked.Increment(ref _suppressedCount);
			return false;
		}

		if (!canvasTextureRid.IsValid)
		{
			_ = TripDeviceLost(context, "invalid canvas texture RID");
			return false;
		}

		if (verifyRdBacking && !RunningInTestHost && !RenderingServer.TextureGetRdTexture(canvasTextureRid).IsValid)
		{
			_ = TripDeviceLost(context, "RenderingServer.TextureGetRdTexture returned invalid RID");
			return false;
		}

		return true;
	}

	/// <summary>
	/// Pure evaluation helper for resource validity without invoking Godot C++ singletons.
	/// </summary>
	public static bool EvaluateResourceHealth(bool ridValid, string context, out bool firstTrip)
	{
		firstTrip = false;
		if (IsDeviceLost)
		{
			Interlocked.Increment(ref _suppressedCount);
			return false;
		}

		if (ridValid)
		{
			return true;
		}

		firstTrip = TripDeviceLost(context, "resource validation failed");
		return false;
	}

	/// <summary>
	/// Time-based log rate limiter for diagnostics that could otherwise repeat every frame.
	/// Pure managed logic safe under unit test runners.
	/// </summary>
	public static bool ShouldLogThrottled(string key, long intervalMs, long nowTickMs, out int suppressedSinceLast)
	{
		lock (ThrottleGate)
		{
			if (!Throttled.TryGetValue(key, out (long LastTickMs, int Suppressed) state))
			{
				Throttled[key] = (nowTickMs, 0);
				suppressedSinceLast = 0;
				return true;
			}

			if (nowTickMs - state.LastTickMs < intervalMs)
			{
				Throttled[key] = (state.LastTickMs, state.Suppressed + 1);
				suppressedSinceLast = 0;
				return false;
			}

			suppressedSinceLast = state.Suppressed;
			Throttled[key] = (nowTickMs, 0);
			return true;
		}
	}

	/// <summary>
	/// Clears a <see cref="Texture2Drd"/>'s RD RID on the main thread (or via <c>CallDeferred</c>
	/// when tearing down from another thread) without throwing during shutdown.
	/// </summary>
	public static void ClearTextureRdRidSafely(Texture2Drd? texture) =>
		ClearTextureRdRidSafely(texture, static t => t.TextureRdRid = default);

	/// <summary>
	/// Clears a <see cref="TextureLayeredRD"/>'s RD RID on the main thread (or via <c>CallDeferred</c>
	/// when tearing down from another thread) without throwing during shutdown.
	/// </summary>
	public static void ClearTextureRdRidSafely(TextureLayeredRD? texture) =>
		ClearTextureRdRidSafely(texture, static t => t.TextureRdRid = default);

	private static void ClearTextureRdRidSafely<T>(T? texture, Action<T> clearAction)
		where T : GodotObject
	{
		if (texture is null || !GodotObject.IsInstanceValid(texture))
		{
			return;
		}

		if (Engine_ServerSyncs.OnMainThread || !RenderingServer.IsOnRenderThread())
		{
			clearAction(texture);
			return;
		}

		Callable.From(() =>
		{
			if (GodotObject.IsInstanceValid(texture))
			{
				clearAction(texture);
			}
		}).CallDeferred();
	}

	/// <summary>
	/// Returns whether a cached <c>RenderingDevice</c> texture RID can be reused for the current
	/// viewport texture publication without re-querying <c>RenderingServer.TextureGetRdTexture</c>.
	/// </summary>
	public static bool ShouldReuseResolvedRd(
		Rid texRid,
		Vector2I size,
		ulong generation,
		Rid cachedTexRid,
		Vector2I cachedSize,
		ulong cachedGeneration,
		Rid cachedRd,
		bool rdTextureValid)
	{
		return ShouldReuseResolvedRd(
			texRid.Id, size, generation, cachedTexRid.Id, cachedSize, cachedGeneration, cachedRd.Id, rdTextureValid);
	}

	/// <summary>
	/// Value-ID overload of <see cref="ShouldReuseResolvedRd"/> testable without live native <c>Rid</c> allocation.
	/// </summary>
	public static bool ShouldReuseResolvedRd(
		ulong texRidId,
		Vector2I size,
		ulong generation,
		ulong cachedTexRidId,
		Vector2I cachedSize,
		ulong cachedGeneration,
		ulong cachedRdId,
		bool rdTextureValid)
	{
		return texRidId != 0 &&
		       cachedRdId != 0 &&
		       rdTextureValid &&
		       cachedTexRidId == texRidId &&
		       cachedSize == size &&
		       cachedGeneration == generation;
	}

	/// <summary>
	/// Resolves a canvas/viewport texture's backing <c>RenderingDevice</c> RID on the render thread,
	/// caching the result per <paramref name="texRid"/> and <paramref name="size"/> so main-thread
	/// callers never stall on <c>RenderingServer.TextureGetRdTexture</c> (<c>push_and_ret</c>).
	/// </summary>
	public static Rid ResolveRdOnRenderThread(
		Rid texRid, Vector2I size, ref Rid cachedTexRid, ref Vector2I cachedSize, ref Rid cachedRd)
	{
		ulong ignoredGeneration = 0;
		return ResolveRdOnRenderThread(
			texRid, size, 0, ref cachedTexRid, ref cachedSize, ref ignoredGeneration, ref cachedRd, rd: null);
	}

	/// <summary>
	/// Resolves a canvas/viewport texture's backing <c>RenderingDevice</c> RID on the render thread,
	/// invalidating immediately on publication <paramref name="generation"/> changes or freed RD textures.
	/// </summary>
	public static Rid ResolveRdOnRenderThread(
		Rid texRid,
		Vector2I size,
		ulong generation,
		ref Rid cachedTexRid,
		ref Vector2I cachedSize,
		ref ulong cachedGeneration,
		ref Rid cachedRd,
		RenderingDevice? rd = null)
	{
		if (!texRid.IsValid || IsDeviceLost)
		{
			cachedTexRid = default;
			cachedSize = default;
			cachedGeneration = generation;
			cachedRd = default;
			return default;
		}

		bool rdValid = cachedRd.IsValid && (rd is null || rd.TextureIsValid(cachedRd));
		if (ShouldReuseResolvedRd(
			    texRid, size, generation, cachedTexRid, cachedSize, cachedGeneration, cachedRd, rdValid))
		{
			return cachedRd;
		}

		cachedRd = RenderingServer.TextureGetRdTexture(texRid);
		cachedTexRid = texRid;
		cachedSize = size;
		cachedGeneration = generation;
		return cachedRd;
	}
}
