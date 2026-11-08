using global::System.Numerics;
using global::System.Runtime.Intrinsics;

// What Engine_Hash.cs finds under these names when Refii.Tests.csproj compiles it in this namespace:
// the innermost namespace is looked in before the file's using directives, and before the global
// System, so its calls land here. See Engine_HashArmTests.
namespace Refii.Tests.ShaModel
{
    /// <summary>The one branch the copy takes from the platform: use the managed hash.</summary>
    internal static class Engine_Platform
    {
        public static bool IsLinuxBionic => true;
    }

    /// <summary>REV32 on 16 bytes: each 32-bit element's bytes reversed, in place.</summary>
    internal static class AdvSimd
    {
        public static Vector128<uint> ReverseElement8(Vector128<uint> value) =>
            Vector128.Create(
                BitReverse(value[0]), BitReverse(value[1]), BitReverse(value[2]), BitReverse(value[3]));

        private static uint BitReverse(uint value) =>
            global::System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value);
    }
}

namespace Refii.Tests.ShaModel.System.Runtime.Intrinsics.Arm
{
    /// <summary>SHA256H, SHA256H2, SHA256SU0 and SHA256SU1, from the pseudocode of the Arm Architecture
    /// Reference Manual for A-profile (<c>SHA256hash</c>, <c>SHAchoose</c>, <c>SHAmajority</c>,
    /// <c>SHAhashSIGMA0</c>, <c>SHAhashSIGMA1</c>). Parameter names as .NET's own class has them.</summary>
    internal static class Sha256
    {
        public static bool IsSupported => true;

        /// <summary>Uses of each hash instruction pair, and of the schedule, since the process started.</summary>
        public static long Rounds, Schedules;

        /// <summary>SHA256H Qd, Qn, Vm: <c>V[d] = SHA256hash(V[d], V[n], V[m], TRUE)</c>.</summary>
        public static Vector128<uint> HashUpdate1(Vector128<uint> hash_abcd, Vector128<uint> hash_efgh, Vector128<uint> wk)
        {
            Rounds++;
            return Hash(hash_abcd, hash_efgh, wk, part1: true);
        }

        /// <summary>SHA256H2 Qd, Qn, Vm: <c>V[d] = SHA256hash(V[n], V[d], V[m], FALSE)</c>. Note the first
        /// two swapped: X is still abcd and Y efgh.</summary>
        public static Vector128<uint> HashUpdate2(Vector128<uint> hash_efgh, Vector128<uint> hash_abcd, Vector128<uint> wk) =>
            Hash(hash_abcd, hash_efgh, wk, part1: false);

        /// <summary>SHA256SU0 Vd.4S, Vn.4S.</summary>
        public static Vector128<uint> ScheduleUpdate0(Vector128<uint> w0_3, Vector128<uint> w4_7)
        {
            Schedules++;

            // T = operand2<31:0> : operand1<127:32>
            uint[] t = { w0_3[1], w0_3[2], w0_3[3], w4_7[0] };
            var result = new uint[4];
            for (int e = 0; e < 4; e++)
            {
                uint elt = t[e];
                elt = BitOperations.RotateRight(elt, 7) ^ BitOperations.RotateRight(elt, 18) ^ (elt >> 3);
                result[e] = elt + w0_3[e];
            }

            return Vector128.Create(result[0], result[1], result[2], result[3]);
        }

        /// <summary>SHA256SU1 Vd.4S, Vn.4S, Vm.4S.</summary>
        public static Vector128<uint> ScheduleUpdate1(Vector128<uint> w0_3, Vector128<uint> w8_11, Vector128<uint> w12_15)
        {
            // T0 = operand3<31:0> : operand2<127:32>; T1 = operand3<127:64>
            uint[] t0 = { w8_11[1], w8_11[2], w8_11[3], w12_15[0] };
            uint[] t1 = { w12_15[2], w12_15[3] };
            var result = new uint[4];
            for (int e = 0; e < 2; e++)
            {
                uint elt = t1[e];
                elt = BitOperations.RotateRight(elt, 17) ^ BitOperations.RotateRight(elt, 19) ^ (elt >> 10);
                result[e] = elt + w0_3[e] + t0[e];
            }

            // T1 = result<63:0>
            t1[0] = result[0];
            t1[1] = result[1];
            for (int e = 2; e < 4; e++)
            {
                uint elt = t1[e - 2];
                elt = BitOperations.RotateRight(elt, 17) ^ BitOperations.RotateRight(elt, 19) ^ (elt >> 10);
                result[e] = elt + w0_3[e] + t0[e];
            }

            return Vector128.Create(result[0], result[1], result[2], result[3]);
        }

        /// <summary><c>SHA256hash(X, Y, W, part1)</c>: four rounds.</summary>
        private static Vector128<uint> Hash(Vector128<uint> abcd, Vector128<uint> efgh, Vector128<uint> w, bool part1)
        {
            uint[] x = { abcd[0], abcd[1], abcd[2], abcd[3] };
            uint[] y = { efgh[0], efgh[1], efgh[2], efgh[3] };
            for (int e = 0; e < 4; e++)
            {
                uint chs = (y[0] & y[1]) ^ (~y[0] & y[2]);
                uint maj = (x[0] & x[1]) ^ (x[0] & x[2]) ^ (x[1] & x[2]);
                uint sigma0 = BitOperations.RotateRight(x[0], 2) ^ BitOperations.RotateRight(x[0], 13) ^ BitOperations.RotateRight(x[0], 22);
                uint sigma1 = BitOperations.RotateRight(y[0], 6) ^ BitOperations.RotateRight(y[0], 11) ^ BitOperations.RotateRight(y[0], 25);
                uint t = y[3] + sigma1 + chs + w[e];
                x[3] += t;
                y[3] = t + sigma0 + maj;

                // (Y : X) = ROL(Y : X, 32), a 256-bit rotate with Y the high half.
                uint fromX = x[3], fromY = y[3];
                x[3] = x[2]; x[2] = x[1]; x[1] = x[0]; x[0] = fromY;
                y[3] = y[2]; y[2] = y[1]; y[1] = y[0]; y[0] = fromX;
            }

            uint[] kept = part1 ? x : y;
            return Vector128.Create(kept[0], kept[1], kept[2], kept[3]);
        }
    }
}
