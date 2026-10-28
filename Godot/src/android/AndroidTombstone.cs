using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.AndroidRuntime;

/// <summary>
/// The readable part of a native crash's tombstone: the signal, the abort message and the
/// crashing thread's backtrace, as text for <c>last-exit.txt</c>.
/// </summary>
/// <remarks>
/// <c>ApplicationExitInfo.getTraceInputStream</c> hands a native crash over as Android's
/// <c>tombstone.proto</c>, which nothing on a player's phone can read. Only the handful of
/// fields below are decoded, by hand: the wire format is varints and length-delimited runs, and
/// a field this reader does not know is skipped by its wire type, so a newer Android's additions
/// cost nothing. A malformed tombstone gives null, never an exception.
/// </remarks>
public static class AndroidTombstone
{
    /// <summary>One frame of the crashing thread.</summary>
    /// <param name="RelativePc">The pc relative to its file's load base, what symbolizers take.</param>
    /// <param name="File">The file's name, without its directory.</param>
    /// <param name="Function">The nearest symbol, empty when stripped.</param>
    /// <param name="Offset">The pc's offset into <paramref name="Function"/>.</param>
    public sealed record Frame(ulong RelativePc, string File, string Function, ulong Offset);

    /// <summary>What a tombstone says about the crash.</summary>
    /// <param name="Signal">The signal's name, SIGSEGV or SIGABRT.</param>
    /// <param name="Code">The signal code's name, SEGV_MAPERR or SI_TKILL.</param>
    /// <param name="FaultAddress">The fault address, when the signal has one.</param>
    /// <param name="AbortMessage">What abort was told, often empty.</param>
    /// <param name="Causes">debuggerd's own guesses: "null pointer dereference" and the like.</param>
    /// <param name="ThreadId">The crashing thread.</param>
    /// <param name="ThreadName">Its name: a guest thread is named as on the 360, "Permanent Bank".</param>
    /// <param name="Frames">Its backtrace, innermost first.</param>
    public sealed record Report(string Signal, string Code, ulong? FaultAddress, string AbortMessage,
        IReadOnlyList<string> Causes, int ThreadId, string ThreadName, IReadOnlyList<Frame> Frames);

    // tombstone.proto field numbers.
    private const int TombstoneTid = 6, TombstoneSignal = 10, TombstoneAbort = 14, TombstoneCauses = 15, TombstoneThreads = 16;
    private const int SignalName = 2, SignalCodeName = 4, SignalHasFault = 8, SignalFault = 9;
    private const int CauseText = 1;
    private const int MapKey = 1, MapValue = 2;
    private const int ThreadIdField = 1, ThreadNameField = 2, ThreadBacktrace = 4;
    private const int FrameRelativePc = 1, FrameFunction = 4, FrameOffset = 5, FrameFile = 6;

    /// <summary>A field as read: its number, a varint or fixed value, or its bytes.</summary>
    private readonly record struct Field(int Number, ulong Value, int Start, int Length);

    /// <summary>Reads the crash out of a tombstone; null when the bytes are not one.</summary>
    public static Report? Decode(byte[] data)
    {
        try
        {
            List<Field> top = Fields(data, 0, data.Length);
            int tid = (int)First(top, TombstoneTid).Value;
            string signal = "", code = "";
            ulong? fault = null;
            if (FindBytes(top, TombstoneSignal) is { } signalField)
            {
                List<Field> fields = Fields(data, signalField.Start, signalField.Length);
                signal = Text(data, fields, SignalName);
                code = Text(data, fields, SignalCodeName);
                if (First(fields, SignalHasFault).Value != 0) fault = First(fields, SignalFault).Value;
            }
            string abort = Text(data, top, TombstoneAbort);
            var causes = new List<string>();
            foreach (Field cause in top.FindAll(field => field.Number == TombstoneCauses && field.Length >= 0))
                causes.Add(Text(data, Fields(data, cause.Start, cause.Length), CauseText));
            string threadName = "";
            var frames = new List<Frame>();
            foreach (Field entry in top.FindAll(field => field.Number == TombstoneThreads && field.Length >= 0))
            {
                List<Field> pair = Fields(data, entry.Start, entry.Length);
                if ((int)First(pair, MapKey).Value != tid || FindBytes(pair, MapValue) is not { } threadField) continue;
                List<Field> thread = Fields(data, threadField.Start, threadField.Length);
                threadName = Text(data, thread, ThreadNameField);
                foreach (Field frame in thread.FindAll(field => field.Number == ThreadBacktrace && field.Length >= 0))
                {
                    List<Field> parts = Fields(data, frame.Start, frame.Length);
                    string file = Text(data, parts, FrameFile);
                    frames.Add(new Frame(First(parts, FrameRelativePc).Value, file[(file.LastIndexOf('/') + 1)..],
                        Text(data, parts, FrameFunction), First(parts, FrameOffset).Value));
                }
                break;
            }
            if (signal.Length == 0 && frames.Count == 0) return null;
            return new Report(signal, code, fault, abort, causes, tid, threadName, frames);
        }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The report as <c>last-exit.txt</c>'s crash section, at most <paramref name="maxFrames"/> frames.</summary>
    public static string Format(Report report, int maxFrames = 48)
    {
        var text = new StringBuilder();
        text.Append("signal: ").Append(report.Signal);
        if (report.Code.Length > 0) text.Append(" (").Append(report.Code).Append(')');
        if (report.FaultAddress is { } fault) text.Append(", fault address 0x").Append(fault.ToString("x", CultureInfo.InvariantCulture));
        text.AppendLine();
        if (report.AbortMessage.Length > 0) text.Append("abort message: ").AppendLine(report.AbortMessage);
        foreach (string cause in report.Causes) text.Append("cause: ").AppendLine(cause);
        text.Append("thread: ").Append(report.ThreadId).Append(" \"").Append(report.ThreadName).AppendLine("\"");
        for (int i = 0; i < report.Frames.Count && i < maxFrames; i++)
        {
            Frame frame = report.Frames[i];
            text.Append("  #").Append(i.ToString("00", CultureInfo.InvariantCulture))
                .Append(" pc ").Append(frame.RelativePc.ToString("x16", CultureInfo.InvariantCulture))
                .Append("  ").Append(frame.File);
            if (frame.Function.Length > 0) text.Append("  ").Append(frame.Function).Append('+').Append(frame.Offset);
            text.AppendLine();
        }
        if (report.Frames.Count > maxFrames) text.Append("  (").Append(report.Frames.Count - maxFrames).AppendLine(" more)");
        return text.ToString();
    }

    /// <summary>The fields of the message in <paramref name="data"/>[<paramref name="start"/>..+<paramref name="length"/>].</summary>
    private static List<Field> Fields(byte[] data, int start, int length)
    {
        var fields = new List<Field>();
        int end = checked(start + length), at = start;
        if (end > data.Length) throw new ArgumentException("message runs past the data");
        while (at < end)
        {
            ulong key = Varint(data, ref at);
            int number = (int)(key >> 3);
            switch ((int)(key & 7))
            {
                case 0: fields.Add(new Field(number, Varint(data, ref at), 0, -1)); break;
                case 1: fields.Add(new Field(number, BitConverter.ToUInt64(data, at), 0, -1)); at = checked(at + 8); break;
                case 2:
                    int size = checked((int)Varint(data, ref at));
                    if (size < 0 || at + size > end) throw new ArgumentException("field runs past its message");
                    fields.Add(new Field(number, 0, at, size));
                    at += size;
                    break;
                case 5: fields.Add(new Field(number, BitConverter.ToUInt32(data, at), 0, -1)); at = checked(at + 4); break;
                default: throw new FormatException("unknown wire type");
            }
        }
        return fields;
    }

    private static ulong Varint(byte[] data, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            byte next = data[at++];
            value |= (ulong)(next & 0x7f) << shift;
            if (next < 0x80) return value;
        }
        throw new FormatException("varint too long");
    }

    private static Field First(List<Field> fields, int number) =>
        fields.Find(field => field.Number == number && field.Length < 0);

    private static Field? FindBytes(List<Field> fields, int number)
    {
        int index = fields.FindIndex(field => field.Number == number && field.Length >= 0);
        return index < 0 ? null : fields[index];
    }

    private static string Text(byte[] data, List<Field> fields, int number) =>
        FindBytes(fields, number) is { } field ? Encoding.UTF8.GetString(data, field.Start, field.Length) : "";
}
