using Godot;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Refii.AndroidRuntime;

/// <summary>
/// <c>android_guest_prime_core</c>: the guest's game thread held on the phone's fastest cores
/// (the CPUs with the highest <c>cpuinfo_max_freq</c>) at nice -10.
/// </summary>
/// <remarks>
/// <para>WHY. The guest simulation is one thread's work: on a Galaxy S10e walking through
/// Bowerstone, the guest's "GameThread (F80…" ran at ~77% of a core with nice 0 and every CPU
/// allowed, wandering between the A76s 5 and 6 (2.13 GHz under load) and now and then onto an
/// A55, and the world ran at 0.58-0.69 of real time. cpu7 is the prime A76 (2.84 GHz) and the
/// thread was rarely on it. On a Snapdragon 8 Elite the prime cluster is two cores, and the
/// thread may use either.</para>
/// <para>WHY HERE AND NOT IN THE RUNTIME. The guest's threads are made by rexglue
/// (threading_posix.cpp), whose 360 placements (XSetThreadProcessor, one of six hardware
/// threads) are ignored by default (<c>ignore_thread_affinities</c>), so nothing there places
/// them and nothing undoes this; a change on this side needs no native rebuild. adb cannot do it
/// (SELinux refuses the shell <c>sched_setaffinity</c> on the app), but the app's own threads may
/// place each other: <c>sched_setaffinity</c> and <c>setpriority(PRIO_PROCESS, tid)</c> on a
/// thread id of this process. Android lets an app lower its own threads' nice to the display
/// priorities; a refusal (EPERM/EACCES) keeps nice 0, says so once, and the pinning still
/// stands.</para>
/// <para>WHY A SCAN. The guest names its threads as it makes them, after boot, and may make them
/// again. Every <see cref="ScanMilliseconds"/> a background thread reads
/// <c>/proc/self/task/*/comm</c> (the read <see cref="Refii.Core.Engine_OsThreads"/> makes, into
/// one reused buffer) and places each game thread it has not placed. A placed thread whose mask
/// no longer matches (Android resets a thread's mask when it moves the app between cpusets, as
/// it does going to the background and back) is placed again, silently after the first time.
/// None of it runs on Godot's main thread.</para>
/// <para><c>android_host_off_prime</c> (separate, default off): every other thread of the process
/// is kept off those cores, so the game thread has them to itself. Godot's main thread runs at
/// the same nice -10, so when the scheduler puts it on cpu7 beside the guest the two share the
/// core evenly, and a thread the game thread makes after it is placed inherits both its mask and
/// its nice (Linux copies them at clone), so it lands on the one core the game thread needs. The
/// same scan takes the prime cores out of each other thread's mask, leaving the rest of the mask
/// as it was; a thread whose mask is the prime cores alone (the game thread's children) is given
/// every other core. Their inherited nice is left as it is.</para>
/// </remarks>
internal sealed class AndroidGuestPlacement
{
    public const string Key = "android_guest_prime_core";

    /// <summary>The refii.toml key that keeps every other thread off the prime cores.</summary>
    public const string OffPrimeKey = "android_host_off_prime";

    /// <summary>The prefix of the guest's simulation thread's name ("GameThread (F80…", cut at 15 bytes).</summary>
    public const string GameThreadPrefix = "GameThread";

    /// <summary>The nice the guest's game thread is given: Godot's main thread already runs at it.</summary>
    public const int Nice = -10;

    /// <summary>How often the threads are looked at again.</summary>
    public const int ScanMilliseconds = 2000;

    private const string TaskRoot = "/proc/self/task";
    private const string CpuRoot = "/sys/devices/system/cpu";
    private const int PrioProcess = 0;
    private const int Eperm = 1;
    private const int Esrch = 3;
    private const int Eacces = 13;

    /// <summary>A <c>cpu_set_t</c> as bionic declares it on arm64: 1024 bits.</summary>
    private const int CpuSetWords = 16;

    private static readonly byte[] GameThreadBytes = System.Text.Encoding.ASCII.GetBytes(GameThreadPrefix);

    private readonly int[] _prime;
    private readonly ulong[] _wanted = new ulong[CpuSetWords];
    private readonly ulong[] _current = new ulong[CpuSetWords];
    private readonly ulong[] _others = new ulong[CpuSetWords];
    private readonly ulong[] _all = new ulong[CpuSetWords];
    private readonly bool _pinGame, _offPrime;
    private readonly byte[] _comm = new byte[32];
    private readonly HashSet<int> _placed = new();
    private readonly HashSet<int> _seen = new();
    private readonly List<int> _gone = new();
    private bool _niceRefusalLogged, _repinLogged, _scanErrorLogged, _deferLogged, _affinityRefused;
    private bool _offPrimeLogged, _offPrimeRefused;
    private int _placements, _movedOff;
    private volatile string _last = "not yet";

    [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static extern int SchedSetAffinity(int tid, nint size, ulong[] mask);

    [DllImport("libc", EntryPoint = "sched_getaffinity", SetLastError = true)]
    private static extern int SchedGetAffinity(int tid, nint size, [Out] ulong[] mask);

    [DllImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static extern int SetPriority(int which, uint who, int prio);

    private AndroidGuestPlacement(int[] prime, IEnumerable<int> all, bool pinGame, bool offPrime)
    {
        _prime = prime;
        foreach (int cpu in all)
        {
            if (cpu is >= 0 and < CpuSetWords * 64)
                _all[cpu / 64] |= 1UL << (cpu % 64);
        }

        _pinGame = pinGame;
        _offPrime = offPrime;
        foreach (int cpu in prime)
            _wanted[cpu / 64] |= 1UL << (cpu % 64);
    }

    /// <summary>Whether refii.toml asks for the placement; off when unset.</summary>
    public static bool Configured(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Whether refii.toml asks for the other threads off the prime cores; off when unset.</summary>
    public static bool OffPrimeConfigured(IReadOnlyDictionary<string, string> values) =>
        Refii.Core.Engine_RefiiConfig.Bool(values, OffPrimeKey, false);

    /// <summary>
    /// The mask a thread other than the game thread is given: its own without the prime cores, or,
    /// when its mask is the prime cores alone (inherited from the placed game thread), every CPU
    /// but those. False when it holds no prime core: nothing to do.
    /// </summary>
    public static bool WithoutPrime(ReadOnlySpan<ulong> mask, ReadOnlySpan<ulong> prime, ReadOnlySpan<ulong> all,
        Span<ulong> result)
    {
        bool held = false, other = false;
        for (int word = 0; word < result.Length; word++)
        {
            ulong m = word < mask.Length ? mask[word] : 0UL;
            ulong p = word < prime.Length ? prime[word] : 0UL;
            held |= (m & p) != 0UL;
            result[word] = m & ~p;
            other |= result[word] != 0UL;
        }

        if (!held)
            return false;
        if (other)
            return true;
        for (int word = 0; word < result.Length; word++)
        {
            ulong p = word < prime.Length ? prime[word] : 0UL;
            result[word] = (word < all.Length ? all[word] : 0UL) & ~p;
            other |= result[word] != 0UL;
        }

        return other;
    }

    /// <summary>
    /// Starts the scan when refii.toml asks for either placement on a phone with a faster cluster;
    /// null when it does not, or on a phone whose cores all run at one speed.
    /// </summary>
    public static AndroidGuestPlacement? Start(IReadOnlyDictionary<string, string> values)
    {
        bool pinGame = Configured(values), offPrime = OffPrimeConfigured(values);
        if (!Refii.Core.Engine_Platform.IsAndroid || !(pinGame || offPrime))
            return null;
        Dictionary<int, long> frequencies = ReadMaxFrequencies();
        int[] prime = PrimeCpus(frequencies);
        if (prime.Length == 0)
        {
            GD.Print("[android] guest placement: no faster cluster in cpufreq; the guest's threads stay where the scheduler puts them");
            return null;
        }

        var placement = new AndroidGuestPlacement(prime, frequencies.Keys, pinGame, offPrime);
        new Thread(placement.Run) { IsBackground = true, Name = "refii-placement" }.Start();
        return placement;
    }

    /// <summary>
    /// The CPUs whose maximum frequency is the highest, ascending; none when every CPU shares
    /// it (nothing to prefer) or none was read.
    /// </summary>
    public static int[] PrimeCpus(IReadOnlyDictionary<int, long> maxFrequencies)
    {
        long top = 0, bottom = long.MaxValue;
        foreach (long frequency in maxFrequencies.Values)
        {
            top = Math.Max(top, frequency);
            bottom = Math.Min(bottom, frequency);
        }

        if (top <= 0 || top == bottom)
            return Array.Empty<int>();
        var prime = new List<int>();
        foreach ((int cpu, long frequency) in maxFrequencies)
        {
            if (frequency == top && cpu is >= 0 and < CpuSetWords * 64)
                prime.Add(cpu);
        }

        prime.Sort();
        return prime.ToArray();
    }

    /// <summary>Whether a thread's <c>comm</c> bytes name the guest's game thread.</summary>
    public static bool IsGameThread(ReadOnlySpan<byte> comm) => comm.StartsWith(GameThreadBytes);

    /// <summary>The CPUs as the log names them: "cpu7", "cpu6,7".</summary>
    public static string Describe(IReadOnlyList<int> cpus) => "cpu" + string.Join(",", cpus);

    /// <summary>The placement for android-status.json.</summary>
    public object Report() => new
    {
        prime_cpus = Describe(_prime), game_thread = _pinGame, others_off_prime = _offPrime,
        placements = Volatile.Read(ref _placements), moved_off_prime = Volatile.Read(ref _movedOff), last = _last,
    };

    /// <summary>Each CPU's <c>cpuinfo_max_freq</c> in kHz, for the CPUs that report one.</summary>
    private static Dictionary<int, long> ReadMaxFrequencies()
    {
        var frequencies = new Dictionary<int, long>();
        for (int cpu = 0; cpu < CpuSetWords * 64; cpu++)
        {
            string directory = $"{CpuRoot}/cpu{cpu}";
            if (!Directory.Exists(directory))
                break;
            try
            {
                string path = directory + "/cpufreq/cpuinfo_max_freq";
                if (File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out long khz))
                    frequencies[cpu] = khz;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        return frequencies;
    }

    /// <summary>Whether every placement asked for has been refused for want of permission.</summary>
    private bool Refused => (!_pinGame || _affinityRefused) && (!_offPrime || _offPrimeRefused);

    private void Run()
    {
        while (!Refused)
        {
            try
            {
                Scan();
            }
            catch (Exception error)
            {
                if (!_scanErrorLogged)
                    GD.Print("[android] guest placement scan failed: " + error.Message);
                _scanErrorLogged = true;
            }

            Thread.Sleep(ScanMilliseconds);
        }
    }

    /// <summary>Places the game threads not yet placed, places again any the system moved, forgets the exited.</summary>
    private void Scan()
    {
        _seen.Clear();
        foreach (string directory in Directory.EnumerateDirectories(TaskRoot))
        {
            if (Refused)
                return;
            if (!int.TryParse(Path.GetFileName(directory), out int tid) || !ReadComm(directory, out int length))
                continue;
            if (!IsGameThread(_comm.AsSpan(0, length)))
            {
                if (_offPrime && !_offPrimeRefused)
                    KeepOffPrime(tid);
                continue;
            }

            if (!_pinGame || _affinityRefused)
                continue;
            _seen.Add(tid);
            if (!_placed.Contains(tid))
            {
                if (Place(tid, true))
                    _placed.Add(tid);
            }
            else if (!StillPlaced(tid))
            {
                if (!_repinLogged)
                    GD.Print($"[android] guest GameThread {tid} moved off {Describe(_prime)} by the system; placing again (further re-placements unlogged)");
                _repinLogged = true;
                Place(tid, false);
            }
        }

        // A thread id is reused once its thread exits: forget it, so a new thread under that id
        // is placed only if it too is a game thread.
        _gone.Clear();
        foreach (int tid in _placed)
        {
            if (!_seen.Contains(tid))
                _gone.Add(tid);
        }

        foreach (int tid in _gone)
            _placed.Remove(tid);
    }

    /// <summary>Reads one thread's name into the reused buffer, without its newline.</summary>
    private bool ReadComm(string taskDirectory, out int length)
    {
        length = 0;
        try
        {
            using SafeFileHandle handle = File.OpenHandle(taskDirectory + "/comm");
            length = RandomAccess.Read(handle, _comm, 0);
            while (length > 0 && _comm[length - 1] is (byte)'\n' or 0)
                length--;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The thread exited between the listing and the read.
            return false;
        }
    }

    /// <summary>Takes the prime cores out of one other thread's mask, when it holds them and something else.</summary>
    private void KeepOffPrime(int tid)
    {
        if (SchedGetAffinity(tid, CpuSetWords * sizeof(ulong), _current) != 0 ||
            !WithoutPrime(_current, _wanted, _all, _others))
            return;
        if (SchedSetAffinity(tid, CpuSetWords * sizeof(ulong), _others) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is Eperm or Eacces)
            {
                GD.Print($"[android] other threads off {Describe(_prime)}: sched_setaffinity refused (errno {error}); stopped");
                _offPrimeRefused = true;
            }

            return;
        }

        if (!_offPrimeLogged)
            GD.Print($"[android] other threads kept off {Describe(_prime)} ({OffPrimeKey}; further moves unlogged)");
        _offPrimeLogged = true;
        Interlocked.Increment(ref _movedOff);
    }

    /// <summary>Whether the thread's mask is still exactly the prime cluster.</summary>
    private bool StillPlaced(int tid)
    {
        if (SchedGetAffinity(tid, CpuSetWords * sizeof(ulong), _current) != 0)
            return true; // Exited, or unreadable: nothing to correct.
        return _current.AsSpan().SequenceEqual(_wanted);
    }

    /// <summary>Pins one thread to the prime cluster and raises it to <see cref="Nice"/>; false when it is gone or refused.</summary>
    /// <remarks>A mask refused for want of permission is refused for every thread of the process
    /// alike: the scan stops. Any other refusal is the moment's: EINVAL while the system has the
    /// app in a cpuset without the prime cores (the S10e did, briefly, 30 s into play, and was
    /// back in top-app's 0-7 after), so the thread is tried again at the next scan.</remarks>
    private bool Place(int tid, bool log)
    {
        if (SchedSetAffinity(tid, CpuSetWords * sizeof(ulong), _wanted) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is Eperm or Eacces)
            {
                GD.Print($"[android] guest GameThread {tid}: sched_setaffinity {Describe(_prime)} refused (errno {error}); guest placement stopped");
                _last = $"sched_setaffinity refused (errno {error})";
                _affinityRefused = true;
            }
            else if (error != Esrch)
            {
                if (!_deferLogged)
                    GD.Print($"[android] guest GameThread {tid}: sched_setaffinity {Describe(_prime)} not taken now (errno {error}); trying again each scan (further deferrals unlogged)");
                _deferLogged = true;
                _last = $"{tid} deferred (errno {error})";
            }

            return false;
        }

        string nice = $"nice {Nice}";
        if (SetPriority(PrioProcess, (uint)tid, Nice) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            nice = $"nice unchanged (errno {error})";
            if (!_niceRefusalLogged)
                GD.Print($"[android] guest placement: setpriority {Nice} refused (errno {error}); threads are pinned at their own nice");
            _niceRefusalLogged = true;
        }

        Interlocked.Increment(ref _placements);
        _last = $"{tid} -> {Describe(_prime)} {nice}";
        if (log)
            GD.Print($"[android] guest GameThread {tid} -> {Describe(_prime)} {nice}");
        return true;
    }
}
