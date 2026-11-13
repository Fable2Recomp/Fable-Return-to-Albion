using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>
/// A file's identity for a re-read: whether it is there, its write time and its length. One stat,
/// where a read parses the whole of refii.toml.
/// </summary>
public readonly record struct AndroidConfigStamp(bool Exists, long WriteTicksUtc, long Length)
{
    /// <summary>The stamp of <paramref name="path"/> now; a missing or unreadable file has none.</summary>
    public static AndroidConfigStamp Of(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new AndroidConfigStamp(true, info.LastWriteTimeUtc.Ticks, info.Length) : default;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return default;
        }
    }

    /// <summary>
    /// Whether the file must be read again: asked to (a write here), or it moved since
    /// <paramref name="read"/> was taken. A missing file is always read, so its error is the read's.
    /// </summary>
    public static bool Reread(AndroidConfigStamp read, AndroidConfigStamp now, bool force) =>
        force || !now.Exists || now != read;
}

/// <summary>
/// How often the Android host does its once-a-second and status work: the live settings are looked
/// at every second (a stat, unless the file moved), and android-status.json is written every
/// <see cref="StatusSeconds"/> — at once for a new failure, within a second for a new phase.
/// </summary>
public static class AndroidStatusCadence
{
    /// <summary>The live settings' (frame cap, render scale) check, in seconds.</summary>
    public const double RefreshSeconds = 1.0;

    /// <summary>The status file's steady interval, in seconds.</summary>
    public const double StatusSeconds = 5.0;

    /// <summary>The closest two status writes come for a phase change: never more often than the old once a second.</summary>
    public const double PhaseSeconds = 1.0;

    /// <summary>Whether the status is written now, <paramref name="sinceWritten"/> seconds after the last.</summary>
    public static bool StatusDue(double sinceWritten, bool phaseChanged, bool failureChanged) =>
        failureChanged || sinceWritten >= StatusSeconds || (phaseChanged && sinceWritten >= PhaseSeconds);
}

/// <summary>
/// Writes android-status.json off the main thread, atomically (a temporary file moved over it),
/// one write at a time; a snapshot posted while one is written waits, and a newer one replaces it.
/// </summary>
/// <remarks>The posted text is built from a detached snapshot taken on the main thread, so the
/// writer reads nothing the game changes.</remarks>
public sealed class AndroidStatusWriter
{
    private readonly string _path;
    private Func<string>? _pending;
    private int _running;
    private Exception? _error;

    /// <param name="path">The status file; its temporary sits beside it.</param>
    public AndroidStatusWriter(string path) => _path = path;

    /// <summary>The status file.</summary>
    public string Path => _path;

    /// <summary>Whether a write is queued or under way.</summary>
    public bool Busy => Volatile.Read(ref _running) != 0;

    /// <summary>Queues <paramref name="text"/> (run on the writer) in place of any snapshot not yet written.</summary>
    public void Post(Func<string> text)
    {
        Interlocked.Exchange(ref _pending, text);
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
            Task.Run(Drain);
    }

    /// <summary>The first error a write met since the last call, or null.</summary>
    public Exception? TakeError() => Interlocked.Exchange(ref _error, null);

    /// <summary>Waits for the queued writes, up to <paramref name="milliseconds"/>; whether they finished.</summary>
    public bool WaitIdle(int milliseconds) => SpinWait.SpinUntil(() => !Busy, milliseconds);

    private void Drain()
    {
        while (true)
        {
            Func<string>? next = Interlocked.Exchange(ref _pending, null);
            if (next is null)
            {
                Volatile.Write(ref _running, 0);
                // A post between the take and the release found the writer running: this one takes it.
                if (Volatile.Read(ref _pending) is null || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                    return;
                continue;
            }

            try
            {
                Write(_path, next());
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
            }
        }
    }

    /// <summary>Writes <paramref name="text"/> beside <paramref name="path"/> and moves it over, so a reader never sees half a file.</summary>
    public static void Write(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
