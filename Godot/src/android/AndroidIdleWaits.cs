using System.Collections.Generic;
using System.Globalization;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// How the native threads wait between bits of work: the guest's short sleeps, the game thread's wait
/// for its next tick, the frame-completion poll, RexNet's event pump while nothing arrives, and the
/// guest's alertable and multi-object waits.
/// </summary>
/// <remarks>
/// Each setting becomes the environment variable the native code reads once, when the thread that
/// uses it first runs; AndroidGameHost sets them in _Ready, before the guest boots. A setting at its
/// default sets nothing, so the native default stands. Every default is the behaviour before these
/// settings existed; the others trade a little lateness for fewer wake-ups (heat).
/// </remarks>
public static class AndroidIdleWaits
{
    /// <summary>A guest sleep under 1 ms lasts as asked instead of becoming a yield (xthread.cpp, Delay).</summary>
    public const string ShortSleepsKey = "android_guest_short_sleeps";

    /// <summary>
    /// Whether a guest yield that keeps spinning sleeps instead (guest_yield_backoff.cpp; the native default). False sets
    /// REFII_YIELD_BACKOFF=0: every guest yield stays a plain yield, so a waiting thread stays busy and its core's clock up.
    /// </summary>
    public const string YieldBackoffKey = "android_guest_yield_backoff";

    /// <summary>The longest sleep in the game thread's wait for its next tick, in microseconds (the SwitchToThread hook).</summary>
    public const string TickWaitKey = "android_guest_tick_wait_us";

    /// <summary>The frame-completion poll's period, in milliseconds (host_bridge.cpp, Fable2Initialized).</summary>
    public const string CompletionKey = "android_guest_completion_ms";

    /// <summary>RexNet's event-pump wait after two seconds without an event, in milliseconds (ffi.rs, rexnet_wait_event).</summary>
    public const string RexNetIdleKey = "android_rexnet_idle_wait_ms";

    /// <summary>
    /// How often, in milliseconds, a guest thread in an alertable or multi-object wait looks again
    /// (threading_posix.cpp, BlockingWaitCheck). 1 is the old polling, every millisecond; from 2 the
    /// wait sleeps until it is signalled or sent an APC, looking again only that often.
    /// </summary>
    public const string WaitCheckKey = "android_guest_wait_check_ms";

    public const int TickWaitDefault = 500, TickWaitMax = 8000;
    public const int CompletionDefault = 1, CompletionMax = 8;
    public const int RexNetIdleDefault = 16, RexNetIdleMax = 1000;
    public const int WaitCheckDefault = 1, WaitCheckMax = 100;

    /// <summary>The environment variables <paramref name="values"/> asks for; none for a setting at its default.</summary>
    /// <remarks>A number outside its range is clamped into it, and one that does not parse is the default.</remarks>
    public static IReadOnlyList<(string Name, string Value)> Environment(IReadOnlyDictionary<string, string> values)
    {
        var variables = new List<(string, string)>();
        if (!Engine_RefiiConfig.Bool(values, YieldBackoffKey, true))
            variables.Add(("REFII_YIELD_BACKOFF", "0"));
        if (Engine_RefiiConfig.Bool(values, ShortSleepsKey, false))
            variables.Add(("REFII_GUEST_SHORT_SLEEPS", "1"));
        Add(variables, "REFII_TICK_WAIT_SLEEP_US", Clamped(values, TickWaitKey, TickWaitDefault, TickWaitMax), TickWaitDefault);
        Add(variables, "REFII_COMPLETION_PERIOD_MS", Clamped(values, CompletionKey, CompletionDefault, CompletionMax), CompletionDefault);
        Add(variables, "REFII_REXNET_IDLE_WAIT_MS", Clamped(values, RexNetIdleKey, RexNetIdleDefault, RexNetIdleMax), RexNetIdleDefault);
        Add(variables, "REFII_GUEST_WAIT_CHECK_MS", Clamped(values, WaitCheckKey, WaitCheckDefault, WaitCheckMax), WaitCheckDefault);
        return variables;
    }

    /// <summary>The key's whole number, between its default (the least) and <paramref name="max"/>.</summary>
    public static int Clamped(IReadOnlyDictionary<string, string> values, string key, int fallback, int max) =>
        System.Math.Clamp(Engine_RefiiConfig.Int(values, key, fallback), fallback, max);

    private static void Add(List<(string, string)> variables, string name, int value, int fallback)
    {
        if (value != fallback)
            variables.Add((name, value.ToString(CultureInfo.InvariantCulture)));
    }
}
