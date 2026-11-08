namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;

[TestSuite]
public class AndroidTombstoneTests
{
    // A tombstone.proto writer of just the fields the reader takes, plus some it must skip.
    private static void Varint(List<byte> to, ulong value)
    {
        while (value >= 0x80) { to.Add((byte)(value | 0x80)); value >>= 7; }
        to.Add((byte)value);
    }

    private static void Number(List<byte> to, int field, ulong value) { Varint(to, (ulong)field << 3); Varint(to, value); }

    private static void Bytes(List<byte> to, int field, byte[] value)
    {
        Varint(to, ((ulong)field << 3) | 2);
        Varint(to, (ulong)value.Length);
        to.AddRange(value);
    }

    private static void Text(List<byte> to, int field, string value) => Bytes(to, field, Encoding.UTF8.GetBytes(value));

    private static byte[] Frame(ulong pc, string file, string function, ulong offset)
    {
        var frame = new List<byte>();
        Number(frame, 1, pc);
        Number(frame, 2, pc + 0x7000000000);   // the absolute pc, skipped
        Text(frame, 4, function);
        Number(frame, 5, offset);
        Text(frame, 6, file);
        Text(frame, 8, "d28c414d");            // the build id, skipped
        return frame.ToArray();
    }

    private static byte[] Thread(int id, string name, params byte[][] frames)
    {
        var thread = new List<byte>();
        Number(thread, 1, (ulong)id);
        Text(thread, 2, name);
        foreach (byte[] frame in frames) Bytes(thread, 4, frame);
        var entry = new List<byte>();
        Number(entry, 1, (ulong)id);
        Bytes(entry, 2, thread.ToArray());
        return entry.ToArray();
    }

    private static byte[] Tombstone(bool fault)
    {
        var signal = new List<byte>();
        Number(signal, 1, fault ? 11UL : 6UL);
        Text(signal, 2, fault ? "SIGSEGV" : "SIGABRT");
        Text(signal, 4, fault ? "SEGV_MAPERR" : "SI_TKILL");
        if (fault) { Number(signal, 8, 1); Number(signal, 9, 0x100000034); }
        var top = new List<byte>();
        Number(top, 1, 3);                       // the architecture, skipped
        Text(top, 2, "qti/kalama/kalama:13");    // the fingerprint, skipped
        Number(top, 5, 24069);
        Number(top, 6, 24430);
        Bytes(top, 10, signal.ToArray());
        Bytes(top, 16, Thread(24069, "main", Frame(0x10, "libc.so", "syscall", 4)));
        Bytes(top, 16, Thread(24430, "Permanent Bank",
            Frame(0x527b4, "/apex/com.android.runtime/lib64/bionic/libc.so", "abort", 168),
            Frame(0xa8a234, "/data/app/~~x/lib/arm64/librexruntime.so", "rex::runtime::InvalidFunctionTrap(PPCContext&, unsigned char*)", 172),
            Frame(0x319f94c, "/data/data/org.refii/files/phone-builds/b/librefii_generated.so", "", 0)));
        Text(top, 14, fault ? "" : "Call to invalid or unregistered function");
        if (fault) { var cause = new List<byte>(); Text(cause, 1, "null pointer dereference"); Bytes(top, 15, cause.ToArray()); }
        return top.ToArray();
    }

    [TestCase]
    public void TestTheCrashingThreadsBacktraceIsRead()
    {
        AndroidTombstone.Report? report = AndroidTombstone.Decode(Tombstone(fault: false));
        AssertThat(report).IsNotNull();
        AssertThat(report!.Signal).IsEqual("SIGABRT");
        AssertThat(report.Code).IsEqual("SI_TKILL");
        AssertThat(report.FaultAddress).IsNull();
        AssertThat(report.AbortMessage).IsEqual("Call to invalid or unregistered function");
        AssertThat(report.ThreadId).IsEqual(24430);
        AssertThat(report.ThreadName).IsEqual("Permanent Bank");
        AssertThat(report.Frames.Count).IsEqual(3);
        AssertThat(report.Frames[1]).IsEqual(new AndroidTombstone.Frame(0xa8a234, "librexruntime.so",
            "rex::runtime::InvalidFunctionTrap(PPCContext&, unsigned char*)", 172));
        string text = AndroidTombstone.Format(report);
        AssertThat(text).StartsWith("signal: SIGABRT (SI_TKILL)\nabort message: Call to invalid or unregistered function\nthread: 24430 \"Permanent Bank\"\n");
        AssertThat(text).Contains("  #01 pc 0000000000a8a234  librexruntime.so  rex::runtime::InvalidFunctionTrap(PPCContext&, unsigned char*)+172\n");
        AssertThat(text).Contains("  #02 pc 000000000319f94c  librefii_generated.so\n");
    }

    [TestCase]
    public void TestAFaultSaysItsAddressAndCause()
    {
        AndroidTombstone.Report report = AndroidTombstone.Decode(Tombstone(fault: true))!;
        AssertThat(report.FaultAddress).IsEqual(0x100000034UL);
        AssertThat(report.Causes).ContainsExactly("null pointer dereference");
        AssertThat(AndroidTombstone.Format(report)).StartsWith("signal: SIGSEGV (SEGV_MAPERR), fault address 0x100000034\ncause: null pointer dereference\n");
    }

    [TestCase]
    public void TestFramesPastTheLimitAreCounted()
    {
        AndroidTombstone.Report report = AndroidTombstone.Decode(Tombstone(fault: false))!;
        AssertThat(AndroidTombstone.Format(report, maxFrames: 1)).EndsWith("  (2 more)\n");
    }

    [TestCase]
    public void TestBytesThatAreNotATombstoneGiveNull()
    {
        AssertThat(AndroidTombstone.Decode(Array.Empty<byte>())).IsNull();
        AssertThat(AndroidTombstone.Decode(Encoding.UTF8.GetBytes("----- pid 4321 at 2026-10-29 -----\nANR traces"))).IsNull();
        byte[] cut = Tombstone(fault: false);
        AssertThat(AndroidTombstone.Decode(cut[..(cut.Length / 2)])).IsNull();
    }

    [TestCase]
    public void TestTheLastExitRecordCarriesTheDecodedCrash()
    {
        string trace = Path.Combine(Refii.Tests.TestTemp.Root, "refii-tombstone-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllBytes(trace, Tombstone(fault: false));
            AssertThat(AndroidLastExit.DescribeCrash(trace)).StartsWith("signal: SIGABRT");
            File.WriteAllText(trace, "not a tombstone");
            AssertThat(AndroidLastExit.DescribeCrash(trace)).IsEqual("");
            AssertThat(AndroidLastExit.DescribeCrash(trace + ".missing")).IsEqual("");
        }
        finally { File.Delete(trace); }
    }
}
