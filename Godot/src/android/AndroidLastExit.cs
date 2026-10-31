using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// Why the previous run ended, from Android's own record: <c>logs/last-exit.txt</c>, a native
/// crash's tombstone or an ANR's traces beside it, and one line in the game log.
/// </summary>
/// <remarks>
/// A run that dies in native code leaves nothing of its own: its godot.log just stops.
/// <c>ActivityManager.getHistoricalProcessExitReasons</c> (API 30) keeps the reason, signal,
/// memory and, for a native crash or an ANR, the trace. It is read once a process, at start,
/// through Godot's JavaClassWrapper and the built-in AndroidRuntime plugin's activity, so no
/// Java of the project's own is involved. The previous record is kept as
/// <c>last-exit.prev.txt</c>. A native crash's tombstone is decoded into the record too
/// (<see cref="AndroidTombstone"/>): the signal and the crashing thread's backtrace, readable
/// without tools. Nothing here may stop the game starting: every failure is a warning.
/// </remarks>
public static class AndroidLastExit
{
    /// <summary>The newest exits, in <c>logs/</c>.</summary>
    public const string FileName = "last-exit.txt";
    /// <summary>The record before it.</summary>
    public const string PreviousFileName = "last-exit.prev.txt";
    /// <summary>The newest exit's trace: a tombstone (protobuf) after a native crash, text after an ANR.</summary>
    public const string TraceFileName = "last-exit-trace.bin";
    /// <summary>The trace before it.</summary>
    public const string PreviousTraceFileName = "last-exit-trace.prev.bin";

    /// <summary>How many exits Android is asked for.</summary>
    public const int Kept = 3;

    /// <summary>ApplicationExitInfo.REASON_* values.</summary>
    public const int ReasonExitSelf = 1, ReasonSignaled = 2, ReasonCrashNative = 5, ReasonAnr = 6;

    private const string KeyPrefix = "key: ";
    private static int _read;

    /// <summary>One ApplicationExitInfo.</summary>
    /// <param name="Reason">REASON_*.</param>
    /// <param name="Status">The signal after a native crash or a kill, the exit code after an exit.</param>
    /// <param name="Importance">RunningAppProcessInfo.IMPORTANCE_* when it ended.</param>
    /// <param name="PssKb">Its last proportional set size, in kB.</param>
    /// <param name="RssKb">Its last resident set size, in kB.</param>
    /// <param name="TimestampMs">When it ended, milliseconds since 1970 UTC.</param>
    /// <param name="Description">The system's own words, often empty.</param>
    /// <param name="Pid">The process.</param>
    /// <param name="ProcessName">The process's name; the app and a service have different ones.</param>
    /// <param name="Detail">Android's toString of the whole record.</param>
    public sealed record Exit(int Reason, int Status, int Importance, long PssKb, long RssKb, long TimestampMs,
        string Description, int Pid, string ProcessName, string Detail);

    /// <summary>The name of ApplicationExitInfo.REASON_<paramref name="reason"/>, without the prefix.</summary>
    public static string ReasonName(int reason) => reason switch
    {
        0 => "UNKNOWN",
        1 => "EXIT_SELF",
        2 => "SIGNALED",
        3 => "LOW_MEMORY",
        4 => "CRASH",
        5 => "CRASH_NATIVE",
        6 => "ANR",
        7 => "INITIALIZATION_FAILURE",
        8 => "PERMISSION_CHANGE",
        9 => "EXCESSIVE_RESOURCE_USAGE",
        10 => "USER_REQUESTED",
        11 => "USER_STOPPED",
        12 => "DEPENDENCY_DIED",
        13 => "OTHER",
        14 => "FREEZER",
        15 => "PACKAGE_STATE_CHANGE",
        16 => "PACKAGE_UPDATED",
        _ => "REASON_" + reason.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The name of RunningAppProcessInfo.IMPORTANCE_<paramref name="importance"/>.</summary>
    public static string ImportanceName(int importance) => importance switch
    {
        100 => "FOREGROUND",
        125 => "FOREGROUND_SERVICE",
        130 or 230 => "PERCEPTIBLE",
        150 or 325 => "TOP_SLEEPING",
        170 or 350 => "CANT_SAVE_STATE",
        200 => "VISIBLE",
        300 => "SERVICE",
        400 => "CACHED",
        500 => "EMPTY",
        1000 => "GONE",
        _ => importance.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>What <paramref name="status"/> means for <paramref name="reason"/>: a signal, an exit code or neither.</summary>
    public static string StatusText(int reason, int status) => reason switch
    {
        ReasonCrashNative or ReasonSignaled => "signal " + status.ToString(CultureInfo.InvariantCulture),
        ReasonExitSelf => "exit " + status.ToString(CultureInfo.InvariantCulture),
        _ => "status " + status.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Whether Android keeps a trace for <paramref name="reason"/>.</summary>
    public static bool HasTrace(int reason) => reason is ReasonCrashNative or ReasonAnr;

    /// <summary>Identifies one exit, so the same record read by two runs is written once.</summary>
    public static string Key(Exit exit) => exit.Pid.ToString(CultureInfo.InvariantCulture) + "@" +
        exit.TimestampMs.ToString(CultureInfo.InvariantCulture);

    /// <summary>The exit's time, UTC, as the flight recorder states its own.</summary>
    public static string FormatTime(long timestampMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(timestampMs).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The game log's line: <c>[android] last exit: CRASH_NATIVE (signal 11) at ...</c>.</summary>
    public static string Summary(Exit exit)
    {
        var line = new StringBuilder("[android] last exit: ")
            .Append(ReasonName(exit.Reason)).Append(" (").Append(StatusText(exit.Reason, exit.Status)).Append(") at ")
            .Append(FormatTime(exit.TimestampMs))
            .Append(", ").Append(exit.ProcessName).Append(" pid ").Append(exit.Pid)
            .Append(", ").Append(ImportanceName(exit.Importance))
            .Append(", pss ").Append(exit.PssKb / 1024).Append(" MB, rss ").Append(exit.RssKb / 1024).Append(" MB");
        if (exit.Description.Length > 0) line.Append(": ").Append(exit.Description);
        return line.ToString();
    }

    /// <summary>The text of <see cref="FileName"/>: the newest exit's key, then each exit, newest first.</summary>
    public static string Format(IReadOnlyList<Exit> exits, string traceNote)
    {
        var text = new StringBuilder();
        if (exits.Count == 0) return "No exit recorded by Android.\n";
        text.Append(KeyPrefix).AppendLine(Key(exits[0]));
        for (int i = 0; i < exits.Count; i++)
        {
            Exit exit = exits[i];
            text.AppendLine()
                .Append("[").Append(i).Append("] ").AppendLine(i == 0 ? "newest" : "earlier")
                .Append("reason: ").Append(ReasonName(exit.Reason)).Append(" (").Append(exit.Reason).AppendLine(")")
                .Append("status: ").AppendLine(StatusText(exit.Reason, exit.Status))
                .Append("importance: ").Append(ImportanceName(exit.Importance)).Append(" (").Append(exit.Importance).AppendLine(")")
                .Append("pss: ").Append(exit.PssKb).AppendLine(" kB")
                .Append("rss: ").Append(exit.RssKb).AppendLine(" kB")
                .Append("time: ").Append(FormatTime(exit.TimestampMs)).Append(" (").Append(exit.TimestampMs).AppendLine(")")
                .Append("process: ").Append(exit.ProcessName).Append(" pid ").Append(exit.Pid).AppendLine()
                .Append("description: ").AppendLine(exit.Description)
                .Append("android: ").AppendLine(exit.Detail);
        }
        if (traceNote.Length > 0) text.AppendLine().Append("trace: ").AppendLine(traceNote);
        return text.ToString();
    }

    /// <summary>
    /// Makes room for a new record in <paramref name="logs"/>: the current one becomes the previous
    /// one, its trace with it. False, and nothing moved, when <paramref name="key"/> is the exit
    /// already recorded.
    /// </summary>
    public static bool Rotate(string logs, string key)
    {
        string current = Path.Combine(logs, FileName);
        if (File.Exists(current))
        {
            using (var reader = new StreamReader(current))
            {
                if (reader.ReadLine() == KeyPrefix + key) return false;
            }
            File.Move(current, Path.Combine(logs, PreviousFileName), true);
        }
        string trace = Path.Combine(logs, TraceFileName);
        if (File.Exists(trace)) File.Move(trace, Path.Combine(logs, PreviousTraceFileName), true);
        else if (File.Exists(Path.Combine(logs, PreviousTraceFileName))) File.Delete(Path.Combine(logs, PreviousTraceFileName));
        return true;
    }

    /// <summary>Reads and records the previous run's exit, once a process, on Android only. Never throws.</summary>
    /// <remarks>On the main thread: JavaClassWrapper needs a thread the JVM knows.</remarks>
    public static void RecordOnce()
    {
        if (Interlocked.Exchange(ref _read, 1) != 0) return;
        try
        {
            if (!Engine_Platform.IsAndroid || OS.GetName() != "Android") return;
            Record(Path.Combine(OS.GetUserDataDir(), "logs"));
        }
        catch (Exception error)
        {
            GD.PushWarning("[android] last exit not read: " + error.Message);
        }
    }

    private static void Record(string logs)
    {
        if (!Engine.HasSingleton("AndroidRuntime")) throw new InvalidOperationException("no AndroidRuntime plugin");
        GodotObject activity = Engine.GetSingleton("AndroidRuntime").Call("getActivity").AsGodotObject()
            ?? throw new InvalidOperationException("no activity");
        string package = Text(Java(activity, "getPackageName"));
        GodotObject manager = Java(activity, "getSystemService", "activity").AsGodotObject()
            ?? throw new InvalidOperationException("no ActivityManager");
        GodotObject list = Java(manager, "getHistoricalProcessExitReasons", package, 0, Kept).AsGodotObject()
            ?? throw new InvalidOperationException("no exit list");
        int count = Math.Min(Kept, Java(list, "size").AsInt32());
        var exits = new List<Exit>(count);
        var infos = new List<GodotObject>(count);
        for (int i = 0; i < count; i++)
        {
            GodotObject? info = Java(list, "get", i).AsGodotObject();
            if (info == null) continue;
            infos.Add(info);
            exits.Add(new Exit(
                Java(info, "getReason").AsInt32(), Java(info, "getStatus").AsInt32(), Java(info, "getImportance").AsInt32(),
                Java(info, "getPss").AsInt64(), Java(info, "getRss").AsInt64(), Java(info, "getTimestamp").AsInt64(),
                Text(Java(info, "getDescription")), Java(info, "getPid").AsInt32(), Text(Java(info, "getProcessName")),
                Text(Java(info, "toString"))));
        }
        Directory.CreateDirectory(logs);
        if (exits.Count == 0)
        {
            GD.Print("[android] last exit: none recorded by Android");
            return;
        }
        if (!Rotate(logs, Key(exits[0])))
        {
            GD.Print(Summary(exits[0]) + " (recorded before)");
            return;
        }
        string traceNote = "";
        if (HasTrace(exits[0].Reason))
        {
            try { traceNote = SaveTrace(infos[0], Path.Combine(logs, TraceFileName)); }
            catch (Exception error) { traceNote = "not saved: " + error.Message; }
        }
        string crash = exits[0].Reason == ReasonCrashNative ? DescribeCrash(Path.Combine(logs, TraceFileName)) : "";
        File.WriteAllText(Path.Combine(logs, FileName), Format(exits, traceNote) + (crash.Length > 0 ? "\ncrash:\n" + crash : ""));
        GD.Print(Summary(exits[0]) + (traceNote.Length > 0 ? " [trace " + traceNote + "]" : ""));
        if (crash.Length > 0) GD.Print("[android] last crash:\n" + crash);
    }

    /// <summary>The saved tombstone's signal and crashing thread as text; empty when there is none to read.</summary>
    public static string DescribeCrash(string tracePath)
    {
        try
        {
            if (!File.Exists(tracePath)) return "";
            return AndroidTombstone.Decode(File.ReadAllBytes(tracePath)) is { } report ? AndroidTombstone.Format(report) : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>Copies the exit's trace stream into <paramref name="path"/> in Java, with android.os.FileUtils.copy (API 29).</summary>
    private static string SaveTrace(GodotObject info, string path)
    {
        GodotObject? input = Java(info, "getTraceInputStream").AsGodotObject();
        if (input == null) return "none kept by Android";
        GodotObject? output = null;
        try
        {
            output = Java(JavaClassWrapper.Wrap("java.io.FileOutputStream"), "FileOutputStream", path).AsGodotObject()
                ?? throw new IOException("cannot open " + path);
            long copied = Java(JavaClassWrapper.Wrap("android.os.FileUtils"), "copy", input, output).AsInt64();
            return $"{TraceFileName}, {copied} bytes";
        }
        finally
        {
            if (output != null) Java(output, "close");
            Java(input, "close");
        }
    }

    /// <summary>Calls a Java method through JavaClassWrapper, turning a Java exception into a .NET one.</summary>
    private static Variant Java(GodotObject target, string method, params Variant[] arguments)
    {
        Variant result = target.Call(method, arguments);
        if (JavaClassWrapper.GetException() is { } exception)
            throw new IOException($"{method}: {Text(exception.Call("toString"))}");
        return result;
    }

    private static string Text(Variant value) => value.VariantType == Variant.Type.Nil ? "" : value.AsString();
}
