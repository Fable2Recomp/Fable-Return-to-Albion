using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Refii.AndroidRuntime;

/// <summary>
/// <c>android_load_big_cores</c> (off by default): while a world build holds the game, the .NET pool
/// threads and Godot's workers run on the phone's faster cores only, and go back afterwards.
/// </summary>
/// <remarks>
/// <para>WHY. A level change recorded on a OnePlus 7 Pro (26 s, 82,451 samples) had those threads on
/// the A55s for 23-37% of their time while the big cores stood 53-67% busy; the scheduler spends
/// them there for power, and a load is the one time that is the wrong trade. GC on an allocating
/// thread and the background GC thread follow the thread's mask, the latter by inheriting its
/// creator's. In play the same threads are better left to the scheduler, so this is load-only.</para>
/// <para>WHICH THREADS. By <c>comm</c>. A .NET thread gets no OS name from Thread.Name on NativeAOT:
/// it keeps its creator's until something attaches it to the JVM, when ART names it "Thread-N". So
/// "Thread-N" (which is also how Godot's own attached threads read) and "WorkerThread N". Guest
/// threads are left where they are, and Android's own. A thread that still carries "VkThread" is a
/// child of the main thread that never attached, not a worker known to be one; with
/// <see cref="WorkersAndMain"/> only the main thread itself, by id, joins them.</para>
/// <para>WHAT THE SYSTEM MAY DO. The app's threads may set each other's masks
/// (<see cref="AndroidGuestPlacement"/> has done so on both phones). The kernel intersects a mask
/// with the thread's cpuset and answers EINVAL when nothing is left, and a cpuset move (going to the
/// background and back) resets every mask to the cpuset's. So: a refusal for want of permission
/// stops this for good; EINVAL tries again at the next scan; a mask read back is what is recorded;
/// and one the system changed is left as the system set it when the hold ends
/// (<see cref="LoadHoldLedger"/>).</para>
/// <para>A thread made by a placed thread inherits its mask, and with no mask of its own to go back
/// to it is given every CPU when the hold ends, whatever it is called.</para>
/// </remarks>
internal sealed class AndroidLoadPlacement
{
    public const string Key = "android_load_big_cores";

    /// <summary>The setting's values: nothing moved, the workers, the workers and the main thread.</summary>
    public const string Off = "off", Workers = "workers", WorkersAndMain = "workers_main";

    /// <summary>A phone needs this many faster cores for the workers to give up the rest.</summary>
    /// <remarks>An 8 Elite has two above six slightly slower ones: the workers would lose far more
    /// than they gain, and there are no little cores to leave. A Snapdragon 855 has four.</remarks>
    public const int MinFasterCpus = 3;

    /// <summary>How often the hold is looked at, so a release gives the threads back at once.</summary>
    public const int PollMilliseconds = 200;

    /// <summary>How often a hold looks for threads made since, and masks the system reset.</summary>
    public const int RescanMilliseconds = 1000;

    private static ReadOnlySpan<byte> ThreadDash => "Thread-"u8;
    private static ReadOnlySpan<byte> WorkerThread => "WorkerThread"u8;
    private static ReadOnlySpan<byte> DotNet => ".NET"u8;

    private readonly ulong[] _hold;
    private readonly bool _main;
    private readonly int _mainTid;
    private readonly Func<bool> _holding;
    private readonly string _cpus;
    private readonly LoadHoldLedger _ledger;
    private readonly ulong[] _current = new ulong[AndroidGuestPlacement.CpuSetWords];
    private readonly ulong[] _wanted = new ulong[AndroidGuestPlacement.CpuSetWords];
    private readonly ulong[] _placed = new ulong[AndroidGuestPlacement.CpuSetWords];
    private readonly byte[] _comm = new byte[32];
    private readonly HashSet<int> _seen = new();
    private bool _refused, _deferLogged, _scanErrorLogged;
    private int _holds, _moved, _restored;
    private volatile string _last = "not yet";

    internal AndroidLoadPlacement(ulong[] hold, ulong[] all, bool main, int mainTid, Func<bool> holding, string cpus)
    {
        _hold = hold;
        _main = main;
        _mainTid = mainTid;
        _holding = holding;
        _cpus = cpus;
        _ledger = new LoadHoldLedger(hold, all);
    }

    /// <summary>What refii.toml asks for: <see cref="Off"/> when unset or unknown.</summary>
    public static string Mode(IReadOnlyDictionary<string, string> values)
    {
        string mode = Refii.Core.Engine_RefiiConfig.String(values, Key, Off).ToLowerInvariant();
        return mode switch
        {
            Workers or "true" or "1" => Workers,
            WorkersAndMain => WorkersAndMain,
            _ => Off,
        };
    }

    /// <summary>The cores the workers keep during a hold: the faster ones when enough of them exist, or none.</summary>
    /// <param name="maxFrequencies">Each CPU's maximum frequency.</param>
    /// <param name="exclude">CPUs another setting keeps these threads off.</param>
    public static int[] HoldCpus(IReadOnlyDictionary<int, long> maxFrequencies, IReadOnlyCollection<int> exclude)
    {
        var cpus = new List<int>();
        foreach (int cpu in AndroidGuestPlacement.FasterCpus(maxFrequencies))
        {
            bool excluded = false;
            foreach (int other in exclude)
                excluded |= other == cpu;
            if (!excluded)
                cpus.Add(cpu);
        }

        return cpus.Count >= MinFasterCpus ? cpus.ToArray() : Array.Empty<int>();
    }

    /// <summary>Whether a thread's name makes it one of those moved: a .NET thread or a Godot worker.</summary>
    public static bool IsWorker(ReadOnlySpan<byte> comm)
    {
        if (comm.StartsWith(ThreadDash))
            return comm.Length > ThreadDash.Length && comm[ThreadDash.Length] is >= (byte)'0' and <= (byte)'9';
        return comm.StartsWith(WorkerThread) || comm.StartsWith(DotNet);
    }

    /// <summary>The <c>Cpus_allowed_list</c> of a <c>/proc/…/status</c> text, or an empty string.</summary>
    public static string CpusAllowedList(string status)
    {
        foreach (string line in status.Split('\n'))
        {
            if (line.StartsWith("Cpus_allowed_list:", StringComparison.Ordinal))
                return line["Cpus_allowed_list:".Length..].Trim();
        }

        return string.Empty;
    }

    /// <summary>Starts watching for holds, when refii.toml asks for it and the phone has the cores.</summary>
    /// <remarks>Called on the main thread, whose id it takes.</remarks>
    /// <param name="values">refii.toml.</param>
    /// <param name="holding">Whether a world build holds the game now; read off the main thread.</param>
    public static AndroidLoadPlacement? Start(IReadOnlyDictionary<string, string> values, Func<bool> holding)
    {
        string mode = Mode(values);
        if (!Refii.Core.Engine_Platform.IsAndroid || mode == Off)
            return null;
        Dictionary<int, long> frequencies = AndroidGuestPlacement.ReadMaxFrequencies();
        IReadOnlyCollection<int> exclude = AndroidGuestPlacement.OffPrimeConfigured(values)
            ? AndroidGuestPlacement.PrimeCpus(frequencies)
            : Array.Empty<int>();
        int[] cpus = HoldCpus(frequencies, exclude);
        if (cpus.Length == 0)
        {
            GD.Print($"[android] load placement: fewer than {MinFasterCpus} faster cores here; threads stay where the scheduler puts them");
            return null;
        }

        var placement = new AndroidLoadPlacement(AndroidGuestPlacement.MaskOf(cpus),
            AndroidGuestPlacement.MaskOf(frequencies.Keys), mode == WorkersAndMain, AndroidGuestPlacement.GetTid(),
            holding, AndroidGuestPlacement.Describe(cpus));
        new Thread(placement.Run) { IsBackground = true, Name = "refii-load-place" }.Start();
        GD.Print($"[android] load placement: {mode} on {placement._cpus} while a world build holds ({Key})");
        return placement;
    }

    /// <summary>The placement for android-status.json.</summary>
    public object Report() => new
    {
        cpus = _cpus, main_thread = _main, holds = Volatile.Read(ref _holds),
        moved = Volatile.Read(ref _moved), restored = Volatile.Read(ref _restored), refused = _refused, last = _last,
    };

    private void Run()
    {
        bool held = false;
        long scanned = 0;
        while (!_refused)
        {
            bool holding = _holding();
            long now = System.Environment.TickCount64;
            try
            {
                if (holding && (!held || now - scanned >= RescanMilliseconds))
                {
                    if (!held)
                        Interlocked.Increment(ref _holds);
                    int moved = PlaceOnce();
                    if (!held)
                    {
                        _last = $"hold {_holds}: {moved} thread(s) on {_cpus}";
                        GD.Print($"[android] load placement: {moved} thread(s) on {_cpus}; {DescribeCpuset()}");
                    }

                    scanned = now;
                }
                else if (!holding && held)
                {
                    int moved = _ledger.Count;
                    int restored = RestoreOnce();
                    _last = $"hold {_holds} ended: {restored} of {moved} thread(s) given back";
                    GD.Print($"[android] load placement: hold ended, gave back {restored} thread(s) of {moved} placed");
                }
            }
            catch (Exception error)
            {
                if (!_scanErrorLogged)
                    GD.Print("[android] load placement scan failed: " + error.Message);
                _scanErrorLogged = true;
            }

            held = holding;
            Thread.Sleep(PollMilliseconds);
        }
    }

    /// <summary>Moves the workers not yet on the hold's cores; the count moved this time.</summary>
    internal int PlaceOnce()
    {
        int moved = 0;
        _seen.Clear();
        foreach (string directory in Directory.EnumerateDirectories(AndroidGuestPlacement.TaskRoot))
        {
            if (_refused)
                break;
            if (!int.TryParse(Path.GetFileName(directory), out int tid) ||
                !AndroidGuestPlacement.ReadComm(directory, _comm, out int length))
                continue;
            _seen.Add(tid);
            if (!IsWorker(_comm.AsSpan(0, length)) && !(_main && tid == _mainTid))
                continue;
            if (AndroidGuestPlacement.SchedGetAffinity(tid, MaskBytes, _current) != 0 ||
                !_ledger.Enter(tid, _current, _wanted))
                continue;
            if (AndroidGuestPlacement.SchedSetAffinity(tid, MaskBytes, _wanted) != 0)
            {
                NoteRefusal(tid, Marshal.GetLastPInvokeError());
                continue;
            }

            // The kernel keeps only what the cpuset allows, so what it kept is what goes on record.
            _ledger.Entered(tid, _current, AndroidGuestPlacement.SchedGetAffinity(tid, MaskBytes, _placed) == 0 ? _placed : _wanted);
            moved++;
        }

        _ledger.Forget(_seen);
        Interlocked.Add(ref _moved, moved);
        return moved;
    }

    /// <summary>Gives the threads back; the count given back.</summary>
    internal int RestoreOnce()
    {
        int restored = 0;
        foreach (string directory in Directory.EnumerateDirectories(AndroidGuestPlacement.TaskRoot))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int tid) ||
                !AndroidGuestPlacement.ReadComm(directory, _comm, out int length) ||
                AndroidGuestPlacement.IsGameThread(_comm.AsSpan(0, length)))
                continue;
            if (AndroidGuestPlacement.SchedGetAffinity(tid, MaskBytes, _current) != 0 ||
                !_ledger.Leave(tid, _current, _wanted))
                continue;
            // A refusal here leaves the thread on the faster cores until the system's next reset;
            // the next hold's scan sees it as already placed.
            if (AndroidGuestPlacement.SchedSetAffinity(tid, MaskBytes, _wanted) == 0)
                restored++;
        }

        _ledger.Clear();
        Interlocked.Add(ref _restored, restored);
        return restored;
    }

    private static nint MaskBytes => AndroidGuestPlacement.CpuSetWords * sizeof(ulong);

    private void NoteRefusal(int tid, int error)
    {
        if (error is AndroidGuestPlacement.Eperm or AndroidGuestPlacement.Eacces)
        {
            GD.Print($"[android] load placement: sched_setaffinity {_cpus} refused (errno {error}); stopped");
            _last = $"sched_setaffinity refused (errno {error})";
            _refused = true;
        }
        else if (error != AndroidGuestPlacement.Esrch)
        {
            if (!_deferLogged)
                GD.Print($"[android] load placement: thread {tid}: sched_setaffinity {_cpus} not taken now (errno {error}); {DescribeCpuset()}; trying again each scan (further deferrals unlogged)");
            _deferLogged = true;
        }
    }

    /// <summary>The main thread's cpuset and what it allows, for the log.</summary>
    private string DescribeCpuset()
    {
        try
        {
            string thread = $"{AndroidGuestPlacement.TaskRoot}/{_mainTid}";
            string group = File.ReadAllText(thread + "/cpuset").Trim();
            string text = $"main thread in cpuset {group}, allowed {CpusAllowedList(File.ReadAllText(thread + "/status"))}";
            string cpus = $"/dev/cpuset{group}/cpus";
            return File.Exists(cpus) ? $"{text}, the cpuset has {File.ReadAllText(cpus).Trim()}" : text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "cpuset unreadable";
        }
    }
}

/// <summary>
/// What a hold did to each thread's mask, so that its end undoes that and nothing else.
/// </summary>
internal sealed class LoadHoldLedger
{
    private readonly ulong[] _hold, _all;
    private readonly Dictionary<int, (ulong[] Before, ulong[] Placed)> _threads = new();

    /// <param name="hold">The mask of a hold.</param>
    /// <param name="all">Every CPU, for a thread with no mask of its own to return to.</param>
    public LoadHoldLedger(ulong[] hold, ulong[] all)
    {
        _hold = hold;
        _all = all;
    }

    /// <summary>The threads on record.</summary>
    public int Count => _threads.Count;

    /// <summary>
    /// The mask a thread is moved to: its own without the slow cores, or the hold's when that
    /// leaves nothing. False when it holds no slow core: nothing to do. A thread found already on
    /// exactly the hold's mask with no record inherited it, and goes on record to be widened.
    /// </summary>
    public bool Enter(int tid, ReadOnlySpan<ulong> current, Span<ulong> wanted)
    {
        bool slow = false, kept = false;
        for (int word = 0; word < wanted.Length; word++)
        {
            ulong mask = word < current.Length ? current[word] : 0UL;
            ulong hold = word < _hold.Length ? _hold[word] : 0UL;
            slow |= (mask & ~hold) != 0UL;
            wanted[word] = mask & hold;
            kept |= wanted[word] != 0UL;
        }

        if (!slow)
        {
            if (!_threads.ContainsKey(tid) && current.SequenceEqual(_hold))
                _threads[tid] = ((ulong[])_all.Clone(), (ulong[])_hold.Clone());
            return false;
        }

        if (!kept)
            _hold.AsSpan().CopyTo(wanted);
        return true;
    }

    /// <summary>A thread was moved: the mask it had and the one the kernel kept. A thread moved
    /// again (the system reset it mid-hold) keeps its first mask on record.</summary>
    public void Entered(int tid, ReadOnlySpan<ulong> before, ReadOnlySpan<ulong> placed)
    {
        ulong[] original = _threads.TryGetValue(tid, out (ulong[] Before, ulong[] Placed) known) ? known.Before : before.ToArray();
        _threads[tid] = (original, placed.ToArray());
    }

    /// <summary>
    /// The mask a thread returns to at a hold's end. False when it is not this hold's to change: on
    /// record but changed since (the system moved it, and the system's mask stands), or off record
    /// and not on the hold's mask. Off record and on it, it inherited that after the last scan.
    /// </summary>
    public bool Leave(int tid, ReadOnlySpan<ulong> current, Span<ulong> restore)
    {
        if (_threads.TryGetValue(tid, out (ulong[] Before, ulong[] Placed) known))
        {
            if (!current.SequenceEqual(known.Placed))
                return false;
            known.Before.AsSpan().CopyTo(restore);
            return true;
        }

        if (!current.SequenceEqual(_hold))
            return false;
        _all.AsSpan().CopyTo(restore);
        return true;
    }

    /// <summary>Drops the threads no longer alive: their ids may be reused.</summary>
    public void Forget(HashSet<int> alive)
    {
        List<int>? gone = null;
        foreach (int tid in _threads.Keys)
        {
            if (!alive.Contains(tid))
                (gone ??= new List<int>()).Add(tid);
        }

        if (gone == null)
            return;
        foreach (int tid in gone)
            _threads.Remove(tid);
    }

    /// <summary>Starts over, for the next hold.</summary>
    public void Clear() => _threads.Clear();
}
