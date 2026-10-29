using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Refii.Titles.Fable2.PubGames;

public readonly record struct SwfAction(int Offset, byte Code, string Name, string Operands);

public static class SwfActions
{
    private static readonly Dictionary<byte, string> Names = new()
    {
        [0x00] = "End", [0x04] = "NextFrame", [0x05] = "PreviousFrame", [0x06] = "Play",
        [0x07] = "Stop", [0x08] = "ToggleQuality", [0x09] = "StopSounds",
        [0x0A] = "Add", [0x0B] = "Subtract", [0x0C] = "Multiply", [0x0D] = "Divide",
        [0x0E] = "Equals", [0x0F] = "Less", [0x10] = "And", [0x11] = "Or", [0x12] = "Not",
        [0x13] = "StringEquals", [0x14] = "StringLength", [0x15] = "StringExtract",
        [0x17] = "Pop", [0x18] = "ToInteger", [0x1C] = "GetVariable", [0x1D] = "SetVariable",
        [0x20] = "SetTarget2", [0x21] = "StringAdd", [0x22] = "GetProperty",
        [0x23] = "SetProperty", [0x24] = "CloneSprite", [0x25] = "RemoveSprite",
        [0x26] = "Trace", [0x27] = "StartDrag", [0x28] = "EndDrag", [0x29] = "StringLess",
        [0x2A] = "Throw", [0x2B] = "CastOp", [0x2C] = "ImplementsOp",
        [0x30] = "RandomNumber", [0x31] = "MBStringLength", [0x32] = "CharToAscii",
        [0x33] = "AsciiToChar", [0x34] = "GetTime", [0x35] = "MBStringExtract",
        [0x3A] = "Delete", [0x3B] = "Delete2", [0x3C] = "DefineLocal",
        [0x3D] = "CallFunction", [0x3E] = "Return", [0x3F] = "Modulo",
        [0x40] = "NewObject", [0x41] = "DefineLocal2", [0x42] = "InitArray",
        [0x43] = "InitObject", [0x44] = "TypeOf", [0x45] = "TargetPath",
        [0x46] = "Enumerate", [0x47] = "Add2", [0x48] = "Less2", [0x49] = "Equals2",
        [0x4A] = "ToNumber", [0x4B] = "ToString", [0x4C] = "PushDuplicate",
        [0x4D] = "StackSwap", [0x4E] = "GetMember", [0x4F] = "SetMember",
        [0x50] = "Increment", [0x51] = "Decrement", [0x52] = "CallMethod",
        [0x53] = "NewMethod", [0x54] = "InstanceOf", [0x55] = "Enumerate2",
        [0x60] = "BitAnd", [0x61] = "BitOr", [0x62] = "BitXor", [0x63] = "BitLShift",
        [0x64] = "BitRShift", [0x65] = "BitURShift", [0x66] = "StrictEquals",
        [0x67] = "Greater", [0x68] = "StringGreater", [0x69] = "Extends",
        [0x81] = "GotoFrame", [0x83] = "GetUrl", [0x87] = "StoreRegister",
        [0x88] = "ConstantPool", [0x8A] = "WaitForFrame", [0x8B] = "SetTarget",
        [0x8C] = "GotoLabel", [0x8D] = "WaitForFrame2", [0x8E] = "DefineFunction2",
        [0x8F] = "Try", [0x94] = "With", [0x96] = "Push", [0x99] = "Jump",
        [0x9A] = "GetUrl2", [0x9B] = "DefineFunction", [0x9D] = "If", [0x9E] = "Call",
        [0x9F] = "GotoFrame2",
    };

    public const byte OpStop = 0x07;
    public const byte OpPlay = 0x06;

    public static bool ContainsStop(byte[] data)
    {
        bool stops = false;
        foreach (SwfAction action in Disassemble(data, 0, new List<string>()))
        {
            if (action.Code == OpStop)
            {
                stops = true;
            }
            else if (action.Code == OpPlay)
            {
                stops = false;
            }
        }

        return stops;
    }

    public static List<SwfAction> Disassemble(byte[] data, int start, List<string> constantPool)
    {
        var actions = new List<SwfAction>();
        int p = start;
        while (p < data.Length)
        {
            int offset = p;
            byte code = data[p++];
            if (code == 0)
            {
                actions.Add(new SwfAction(offset, code, "End", string.Empty));
                break;
            }

            int length = 0;
            if (code >= 0x80)
            {
                if (p + 2 > data.Length)
                {
                    break;
                }

                length = BitConverter.ToUInt16(data, p);
                p += 2;
                if (p + length > data.Length)
                {
                    break;
                }
            }

            string name = Names.TryGetValue(code, out string? known)
                ? known
                : $"Unknown0x{code:X2}";
            string operands = Describe(code, data, p, length, constantPool);
            actions.Add(new SwfAction(offset, code, name, operands));
            p += length;
        }

        return actions;
    }

    private static string Describe(
        byte code, byte[] data, int p, int length, List<string> constantPool)
    {
        switch (code)
        {
            case 0x88:
            {
                constantPool.Clear();
                if (length < 2)
                {
                    return string.Empty;
                }

                int count = BitConverter.ToUInt16(data, p);
                int q = p + 2;
                for (int i = 0; i < count && q < p + length; i++)
                {
                    constantPool.Add(ReadString(data, ref q, p + length));
                }

                return $"{constantPool.Count} constants";
            }

            case 0x96:
                return DescribePush(data, p, length, constantPool);

            case 0x8B:
            case 0x8C:
            {
                int q = p;
                return '"' + ReadString(data, ref q, p + length) + '"';
            }

            case 0x81:
                return length >= 2 ? BitConverter.ToUInt16(data, p).ToString() : string.Empty;

            case 0x99:
            case 0x9D:
                return length >= 2
                    ? $"offset {BitConverter.ToInt16(data, p)}"
                    : string.Empty;

            case 0x87:
                return length >= 1 ? $"r{data[p]}" : string.Empty;

            case 0x9B:
            {
                int q = p;
                string name = ReadString(data, ref q, p + length);
                if (q + 2 > data.Length)
                {
                    return name;
                }

                int count = BitConverter.ToUInt16(data, q);
                q += 2;
                var args = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    args.Add(ReadString(data, ref q, p + length));
                }

                return $"{name}({string.Join(", ", args)})";
            }

            case 0x8E:
            {
                int q = p;
                string name = ReadString(data, ref q, p + length);
                if (q + 2 > data.Length)
                {
                    return name;
                }

                int count = BitConverter.ToUInt16(data, q);
                return $"{name}/{count} args";
            }

            default:
                return string.Empty;
        }
    }

    private static string DescribePush(
        byte[] data, int p, int length, List<string> constantPool)
    {
        var parts = new List<string>();
        int end = p + length;
        int q = p;
        while (q < end)
        {
            byte type = data[q++];
            switch (type)
            {
                case 0:
                    parts.Add('"' + ReadString(data, ref q, end) + '"');
                    break;
                case 1:
                    parts.Add(BitConverter.ToSingle(data, q).ToString(CultureInfo.InvariantCulture));
                    q += 4;
                    break;
                case 2:
                    parts.Add("null");
                    break;
                case 3:
                    parts.Add("undefined");
                    break;
                case 4:
                    parts.Add($"r{data[q]}");
                    q += 1;
                    break;
                case 5:
                    parts.Add(data[q] != 0 ? "true" : "false");
                    q += 1;
                    break;
                case 6:
                    parts.Add(BitConverter.ToDouble(data, q).ToString(CultureInfo.InvariantCulture));
                    q += 8;
                    break;
                case 7:
                    parts.Add(BitConverter.ToInt32(data, q).ToString(CultureInfo.InvariantCulture));
                    q += 4;
                    break;
                case 8:
                    parts.Add(Constant(constantPool, data[q]));
                    q += 1;
                    break;
                case 9:
                    parts.Add(Constant(constantPool, BitConverter.ToUInt16(data, q)));
                    q += 2;
                    break;
                default:
                    return string.Join(", ", parts);
            }
        }

        return string.Join(", ", parts);
    }

    private static string Constant(List<string> pool, int index) =>
        index >= 0 && index < pool.Count ? '"' + pool[index] + '"' : $"const{index}";

    private static string ReadString(byte[] data, ref int p, int end)
    {
        int start = p;
        while (p < end && p < data.Length && data[p] != 0)
        {
            p++;
        }

        string value = Encoding.UTF8.GetString(data, start, p - start);
        if (p < data.Length)
        {
            p++;
        }

        return value;
    }
}
