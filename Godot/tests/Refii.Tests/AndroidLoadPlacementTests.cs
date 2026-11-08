using GdUnit4;
using Refii.AndroidRuntime;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_load_big_cores`: the key, the cores, the threads and what a hold's end undoes.</summary>
[TestSuite]
public sealed class AndroidLoadPlacementTests
{
    private static readonly Dictionary<int, long> Snapdragon855 = new()
    {
        [0] = 1785600, [1] = 1785600, [2] = 1785600, [3] = 1785600,
        [4] = 2419200, [5] = 2419200, [6] = 2419200, [7] = 2841600,
    };

    private static readonly Dictionary<int, long> Snapdragon8Elite = new()
    {
        [0] = 4320000, [1] = 4320000,
        [2] = 3532800, [3] = 3532800, [4] = 3532800, [5] = 3532800, [6] = 3532800, [7] = 3532800,
    };

    private static ulong[] Mask(ulong low) => new[] { low, 0UL };

    /// <summary>Off unless refii.toml says otherwise, in the file's own spellings; the presets leave it alone.</summary>
    [TestCase]
    public void OffUnlessConfigured()
    {
        AssertThat(AndroidLoadPlacement.Mode(new Dictionary<string, string>())).IsEqual(AndroidLoadPlacement.Off);
        AssertThat(AndroidLoadPlacement.Mode(new Dictionary<string, string> { [AndroidLoadPlacement.Key] = "workers" })).IsEqual(AndroidLoadPlacement.Workers);
        AssertThat(AndroidLoadPlacement.Mode(new Dictionary<string, string> { [AndroidLoadPlacement.Key] = "\"workers_main\"" })).IsEqual(AndroidLoadPlacement.WorkersAndMain);
        AssertThat(AndroidLoadPlacement.Mode(new Dictionary<string, string> { [AndroidLoadPlacement.Key] = "true" })).IsEqual(AndroidLoadPlacement.Workers);
        AssertThat(AndroidLoadPlacement.Mode(new Dictionary<string, string> { [AndroidLoadPlacement.Key] = "nonsense" })).IsEqual(AndroidLoadPlacement.Off);
        AssertThat(Refii.Launcher.Core.GraphicsPresets.LowValues.ContainsKey(AndroidLoadPlacement.Key)).IsFalse();
        AssertThat(Refii.Launcher.Core.GraphicsPresets.HighValues.ContainsKey(AndroidLoadPlacement.Key)).IsFalse();
    }

    /// <summary>The collection thread takes every core above the slowest, on any layout with a slower cluster.</summary>
    [TestCase]
    public void TheFasterCpusAreThoseAboveTheSlowest()
    {
        AssertThat(AndroidGuestPlacement.FasterCpus(Snapdragon855)).IsEqual(new[] { 4, 5, 6, 7 });
        AssertThat(AndroidGuestPlacement.FasterCpus(Snapdragon8Elite)).IsEqual(new[] { 0, 1 });
        AssertThat(AndroidGuestPlacement.FasterCpus(new Dictionary<int, long> { [0] = 2000000, [1] = 2000000 })).IsEmpty();
        AssertThat(AndroidGuestPlacement.FasterCpus(new Dictionary<int, long>())).IsEmpty();
        AssertThat(AndroidGuestPlacement.MaskOf(new[] { 4, 5, 6, 7, 64 })[0]).IsEqual(0xF0UL);
        AssertThat(AndroidGuestPlacement.MaskOf(new[] { 4, 5, 6, 7, 64 })[1]).IsEqual(1UL);
    }

    /// <summary>The workers keep the faster cores on an 855, stay put on a phone with too few, and
    /// leave out the prime core when another setting keeps them off it.</summary>
    [TestCase]
    public void WorkersMoveOnlyWithEnoughFasterCores()
    {
        AssertThat(AndroidLoadPlacement.HoldCpus(Snapdragon855, Array.Empty<int>())).IsEqual(new[] { 4, 5, 6, 7 });
        AssertThat(AndroidLoadPlacement.HoldCpus(Snapdragon855, new[] { 7 })).IsEqual(new[] { 4, 5, 6 });
        AssertThat(AndroidLoadPlacement.HoldCpus(Snapdragon855, new[] { 6, 7 })).IsEmpty();
        AssertThat(AndroidLoadPlacement.HoldCpus(Snapdragon8Elite, Array.Empty<int>())).IsEmpty();
    }

    /// <summary>Names from a real level-change recording: .NET and Godot workers move, the rest do not.</summary>
    [TestCase]
    public void WorkersAreKnownByName()
    {
        foreach (string name in new[] { "Thread-18", "Thread-4", "WorkerThread 5", ".NET TP Worker", ".NET Long Runni" })
            AssertThat(AndroidLoadPlacement.IsWorker(Encoding.ASCII.GetBytes(name))).OverrideFailureMessage(name).IsTrue();
        foreach (string name in new[]
                 {
                     "VkThread", "GameThread (F80", "XThread55D5DCB0", "3D Engine (F800", "Audio Worker (F", "VFS - Request M",
                     "Texture Streame", "AudioTrack", "Binder:10378_1", "Filter1", "XrnmThread (F80", "Thread-", "Thread-x", "refii-load-gc",
                 })
            AssertThat(AndroidLoadPlacement.IsWorker(Encoding.ASCII.GetBytes(name))).OverrideFailureMessage(name).IsFalse();
    }

    /// <summary>A thread loses the slow cores and goes back to exactly what it had.</summary>
    [TestCase]
    public void AHoldTakesTheSlowCoresAndGivesThemBack()
    {
        var ledger = new LoadHoldLedger(Mask(0xF0), Mask(0xFF));
        ulong[] wanted = new ulong[2];

        AssertThat(ledger.Enter(10, Mask(0xFF), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0xF0UL);
        ledger.Entered(10, Mask(0xFF), Mask(0xF0));

        // A thread that was kept off the prime core stays off it.
        AssertThat(ledger.Enter(11, Mask(0x7F), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0x70UL);
        ledger.Entered(11, Mask(0x7F), Mask(0x70));

        // On the slow cores alone: it gets the hold's cores.
        AssertThat(ledger.Enter(12, Mask(0x0F), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0xF0UL);

        // Already there on purpose, narrower: nothing to do, nothing on record.
        AssertThat(ledger.Enter(13, Mask(0x30), wanted)).IsFalse();
        AssertThat(ledger.Count).IsEqual(2);

        AssertThat(ledger.Leave(10, Mask(0xF0), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0xFFUL);
        AssertThat(ledger.Leave(11, Mask(0x70), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0x7FUL);
        AssertThat(ledger.Leave(13, Mask(0x30), wanted)).IsFalse();
    }

    /// <summary>The kernel keeps what the cpuset allows: the mask read back is the one compared at the end.</summary>
    [TestCase]
    public void TheMaskTheKernelKeptIsTheOneOnRecord()
    {
        var ledger = new LoadHoldLedger(Mask(0xF0), Mask(0xFF));
        ulong[] wanted = new ulong[2];
        AssertThat(ledger.Enter(10, Mask(0x7F), wanted)).IsTrue();
        ledger.Entered(10, Mask(0x7F), Mask(0x70));
        AssertThat(ledger.Leave(10, Mask(0x70), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0x7FUL);
    }

    /// <summary>The system reset a mask mid-hold: the thread is moved again, its first mask stays on
    /// record, and if the system changed it after the last scan the system's mask stands.</summary>
    [TestCase]
    public void WhatTheSystemChangedIsNotUndone()
    {
        var ledger = new LoadHoldLedger(Mask(0xF0), Mask(0xFF));
        ulong[] wanted = new ulong[2];
        AssertThat(ledger.Enter(10, Mask(0x7F), wanted)).IsTrue();
        ledger.Entered(10, Mask(0x7F), Mask(0x70));

        AssertThat(ledger.Enter(10, Mask(0xFF), wanted)).IsTrue();
        ledger.Entered(10, Mask(0xFF), Mask(0xF0));
        AssertThat(ledger.Leave(10, Mask(0xF0), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0x7FUL);

        AssertThat(ledger.Enter(11, Mask(0xFF), wanted)).IsTrue();
        ledger.Entered(11, Mask(0xFF), Mask(0xF0));
        AssertThat(ledger.Leave(11, Mask(0x0F), wanted)).IsFalse();
    }

    /// <summary>A thread made by a placed thread inherits the hold's mask with none of its own:
    /// widened at the end, whether a scan saw it or not. A dead thread's id is forgotten.</summary>
    [TestCase]
    public void InheritedMasksAreWidenedAndDeadThreadsForgotten()
    {
        var ledger = new LoadHoldLedger(Mask(0xF0), Mask(0xFF));
        ulong[] wanted = new ulong[2];

        AssertThat(ledger.Enter(20, Mask(0xF0), wanted)).IsFalse();
        AssertThat(ledger.Count).IsEqual(1);
        AssertThat(ledger.Leave(20, Mask(0xF0), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0xFFUL);

        AssertThat(ledger.Leave(21, Mask(0xF0), wanted)).IsTrue();
        AssertThat(wanted[0]).IsEqual(0xFFUL);
        AssertThat(ledger.Leave(22, Mask(0xFF), wanted)).IsFalse();

        ledger.Entered(30, Mask(0xFF), Mask(0xF0));
        ledger.Forget(new HashSet<int> { 20 });
        AssertThat(ledger.Count).IsEqual(1);
        ledger.Clear();
        AssertThat(ledger.Count).IsEqual(0);
    }

    /// <summary>The cpuset report's parser.</summary>
    [TestCase]
    public void TheAllowedCpusAreReadFromStatus()
    {
        AssertThat(AndroidLoadPlacement.CpusAllowedList("Name:\tx\nCpus_allowed:\tff\nCpus_allowed_list:\t0-7\nMems_allowed:\t1\n")).IsEqual("0-7");
        AssertThat(AndroidLoadPlacement.CpusAllowedList("Name:\tx\n")).IsEqual(string.Empty);
    }

    /// <summary>The real system calls on real threads of this process, where they exist: a named
    /// worker moves and comes back, its child is widened, a guest's name is left alone.</summary>
    [TestCase]
    public void RealThreadsMoveAndComeBack()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var original = new ulong[AndroidGuestPlacement.CpuSetWords];
        AssertThat(AndroidGuestPlacement.SchedGetAffinity(0, Bytes, original)).IsEqual(0);
        var allowed = new List<int>();
        for (int cpu = 0; cpu < 64; cpu++)
        {
            if ((original[0] >> cpu & 1UL) != 0)
                allowed.Add(cpu);
        }

        if (allowed.Count < 3)
            return;
        ulong[] hold = AndroidGuestPlacement.MaskOf(allowed.GetRange(0, 2));
        var placement = new AndroidLoadPlacement(hold, original, false, -1, static () => true, "test");

        using var finish = new ManualResetEventSlim();
        int workerTid = StartNamed("Thread-9999", finish, out Thread worker);
        int guestTid = StartNamed("XThread55D5DCB0", finish, out Thread guest);
        try
        {
            AssertThat(placement.PlaceOnce() >= 1).IsTrue();
            AssertThat(MaskOfThread(workerTid)).IsEqual(hold);
            AssertThat(MaskOfThread(guestTid)).IsEqual(original);

            // A child of a placed thread, as on NativeAOT, where clone copies the mask and nothing
            // resets it. The desktop's CoreCLR resets a new thread's, so this one is set by hand.
            int childTid = StartNamed("VkThread", finish, out Thread child);
            AssertThat(AndroidGuestPlacement.SchedSetAffinity(childTid, Bytes, hold)).IsEqual(0);

            AssertThat(placement.RestoreOnce() >= 2).IsTrue();
            AssertThat(MaskOfThread(workerTid)).IsEqual(original);
            AssertThat(MaskOfThread(guestTid)).IsEqual(original);
            AssertThat(MaskOfThread(childTid)).IsEqual(original);
            finish.Set();
            child.Join();
        }
        finally
        {
            placement.RestoreOnce();
            finish.Set();
        }

        worker.Join();
        guest.Join();
    }

    private static int StartNamed(string name, ManualResetEventSlim finish, out Thread thread)
    {
        using var ready = new ManualResetEventSlim();
        int tid = 0;
        thread = new Thread(() =>
        {
            AndroidGuestPlacement.NameCurrentThread(name);
            tid = AndroidGuestPlacement.GetTid();
            ready.Set();
            finish.Wait();
        }) { IsBackground = true };
        thread.Start();
        ready.Wait();
        return tid;
    }

    private static nint Bytes => AndroidGuestPlacement.CpuSetWords * sizeof(ulong);

    private static ulong[] MaskOfThread(int tid)
    {
        var mask = new ulong[AndroidGuestPlacement.CpuSetWords];
        AssertThat(AndroidGuestPlacement.SchedGetAffinity(tid, Bytes, mask)).IsEqual(0);
        return mask;
    }
}
