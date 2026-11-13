using System;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_debug_idle_quit_minutes` (0, off, by default): a test phone left running by a script
/// that lost its connection ends the game by itself. Armed by the first debug-channel request, so
/// play without a script is never affected; once armed, that many minutes with no request ends
/// the process. Wireless debugging can drop in the middle of a run, and a phone that charges slowly
/// drains to empty with the game open even at the idle menu cap.
/// </summary>
public static class AndroidDebugIdleQuit
{
    public const string Key = "android_debug_idle_quit_minutes";

    /// <summary>When the last request arrived (Time.GetTicksMsec), or -1 before the first.</summary>
    private static long _lastHeardMs = -1;
    private static double? _minutes;

    /// <summary>Called for every debug-channel request.</summary>
    public static void Heard() => _lastHeardMs = (long)Time.GetTicksMsec();

    /// <summary>Whether <paramref name="minutes"/> have passed since an armed last request. Pure.</summary>
    public static bool Due(double minutes, long lastHeardMs, long nowMs) =>
        minutes > 0 && lastHeardMs >= 0 && nowMs - lastHeardMs >= minutes * 60_000;

    /// <summary>Ends the process once due; called every frame by the phone host.</summary>
    public static void Tick()
    {
        double minutes = _minutes ??= ReadMinutes();
        long now = (long)Time.GetTicksMsec();
        if (!Due(minutes, _lastHeardMs, now)) return;
        GD.Print($"[android] no debug-channel request for {minutes:0.#} min ({Key}): ending the game");
        OS.Kill(OS.GetProcessId());
    }

    private static double ReadMinutes()
    {
        try
        {
            return Math.Max(0, Engine_RefiiConfig.Int(Engine_RefiiConfig.Read(out _), Key, 0));
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
