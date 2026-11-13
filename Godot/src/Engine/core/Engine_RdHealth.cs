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

		if (!RunningInTestHost)
		{
			// Single error line for the entire session after device loss.
			GD.PushError($"[rd-health] GPU device lost / RD resource creation failed ({reason}); latching circuit breaker.");
		}

		return true;
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
}
