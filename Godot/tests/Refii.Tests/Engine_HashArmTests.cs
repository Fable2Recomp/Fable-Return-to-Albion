using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Model = Refii.Tests.ShaModel.Engine_Hash;
using ModelArm = Refii.Tests.ShaModel.System.Runtime.Intrinsics.Arm.Sha256;
using RealArm = System.Runtime.Intrinsics.Arm.Sha256;

namespace Refii.Tests;

/// <summary>
/// Engine_Hash's <c>CompressArm</c> on a machine without the instructions. The project compiles
/// Engine_Hash.cs a second time, unchanged but for its namespace (see Refii.Tests.csproj), and in that
/// namespace the names it calls resolve to the model in <c>Engine_HashArmModel.cs</c>.
/// </summary>
/// <remarks>This proves the rounds and the schedule are right if the instructions do what the manual
/// says and .NET passes the operands in the order its parameters are named. It is not a run on the
/// CPU: <see cref="TheModelIsTheInstructions"/> and Engine_HashTests are, on an ARM64 test host.</remarks>
[TestSuite]
public sealed class Engine_HashArmTests
{
    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string ModelDigest(ReadOnlySpan<byte> data)
    {
        using var sha = new Model.ManagedSha256();
        sha.Append(data);
        return Convert.ToHexString(sha.Finish());
    }

    /// <summary>All the padding cases, then lengths that cross many blocks.</summary>
    [TestCase]
    public void TheArmPathMatchesThePlatform()
    {
        long before = ModelArm.Rounds;
        int differ = 0;
        for (int length = 0; length <= 300; length++)
        {
            byte[] data = Data(length, length);
            if (ModelDigest(data) != Convert.ToHexString(SHA256.HashData(data))) differ++;
        }

        AssertThat(differ).IsEqual(0);
        foreach (int length in new[] { 1000, 4096, 65536, 1_000_003 })
        {
            byte[] data = Data(length, length);
            AssertThat(ModelDigest(data)).IsEqual(Convert.ToHexString(SHA256.HashData(data)));
        }

        // It did run through the instructions: 16 four-round steps a block.
        AssertThat(ModelArm.IsSupported).IsTrue();
        AssertThat(ModelArm.Rounds - before).IsGreater(100_000L);
    }

    /// <summary>One block is exactly sixteen uses of each hash instruction, twelve of each schedule one.</summary>
    [TestCase]
    public void ABlockIsSixteenSteps()
    {
        long rounds = ModelArm.Rounds, schedules = ModelArm.Schedules;
        // 55 bytes and its padding fit one block.
        ModelDigest(new byte[55]);
        AssertThat(ModelArm.Rounds - rounds).IsEqual(16L);
        AssertThat(ModelArm.Schedules - schedules).IsEqual(12L);
    }

    /// <summary>FIPS 180-4's own examples, which owe nothing to the platform's library.</summary>
    [TestCase]
    public void TheStandardsVectors()
    {
        AssertThat(ModelDigest(Encoding.ASCII.GetBytes("abc")))
            .IsEqual("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
        AssertThat(ModelDigest(Encoding.ASCII.GetBytes("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq")))
            .IsEqual("248D6A61D20638B8E5C026930C3E6039A33CE45964FF2167F6ECEDD419DB06C1");
        AssertThat(ModelDigest(Array.Empty<byte>()))
            .IsEqual("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
        var million = new byte[1_000_000];
        million.AsSpan().Fill((byte)'a');
        AssertThat(ModelDigest(million))
            .IsEqual("CDC76E5C9914FB9281A1C7E284D73E67F1809A48A497200E046D39CCC7112CD0");
    }

    /// <summary>The digests the [TEMP-sha] line in AndroidTextureUpload should print on a phone, with
    /// the hardware path in use there.</summary>
    [TestCase]
    public void TheDigestsAPhoneShouldPrint()
    {
        AssertThat(ModelDigest("abc"u8)).IsEqual(Convert.ToHexString(SHA256.HashData("abc"u8)));
        AssertThat(ModelDigest(new byte[1000])).IsEqual("541B3E9DAA09B20BF85FA273E5CBD3E80185AA4EC298E765DB87742B70138A53");
    }

    /// <summary>Fed in pieces, across block boundaries, as the texture cache's incremental key does;
    /// and through the stream and incremental fronts.</summary>
    [TestCase]
    public void PiecesAndWholesAgree()
    {
        byte[] data = Data(10_000, 3);
        string expected = Convert.ToHexString(SHA256.HashData(data));
        foreach (int piece in new[] { 1, 3, 63, 64, 65, 127, 4096 })
        {
            using var sha = new Model.ManagedSha256();
            for (int at = 0; at < data.Length; at += piece)
                sha.Append(data.AsSpan(at, Math.Min(piece, data.Length - at)));
            AssertThat(Convert.ToHexString(sha.Finish())).IsEqual(expected);
        }

        AssertThat(Model.UsesManaged).IsTrue();
        AssertThat(Convert.ToHexString(Model.Sha256(data))).IsEqual(expected);
        using var stream = new MemoryStream(data);
        AssertThat(Convert.ToHexString(Model.Sha256(stream))).IsEqual(expected);
        using Model.Incremental incremental = Model.CreateIncrementalSha256();
        incremental.AppendData(data.AsSpan(0, 100));
        incremental.AppendData(data.AsSpan(100));
        AssertThat(Convert.ToHexString(incremental.GetHashAndReset())).IsEqual(expected);
    }

    /// <summary>A model named as .NET names its parameters: the order in CompressArm's calls means the
    /// same thing to both. <c>Vd</c> is the first parameter of each.</summary>
    [TestCase]
    public void TheModelHasDotNetsOperandOrder()
    {
        foreach (string name in new[] { "HashUpdate1", "HashUpdate2", "ScheduleUpdate0", "ScheduleUpdate1" })
        {
            string real = string.Join(",", typeof(RealArm).GetMethod(name)!.GetParameters().Select(p => p.Name));
            string model = string.Join(",", typeof(ModelArm).GetMethod(name)!.GetParameters().Select(p => p.Name));
            AssertThat($"{name}({model})").IsEqual($"{name}({real})");
        }
    }

    /// <summary>Where the instructions exist, the model must be them. Skipped elsewhere, which on an
    /// x64 run means the model above is still only the manual's word.</summary>
    [TestCase]
    public void TheModelIsTheInstructions()
    {
        if (!RealArm.IsSupported)
        {
            Console.WriteLine("TheModelIsTheInstructions: this CPU has no SHA-256 instructions; skipped");
            return;
        }

        byte[] bytes = Data(16 * 4 * 2000, 4);
        ReadOnlySpan<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes);
        for (int i = 0; i + 12 <= words.Length; i += 12)
        {
            Vector128<uint> a = Vector128.Create<uint>(words[i..]), b = Vector128.Create<uint>(words[(i + 4)..]), c = Vector128.Create<uint>(words[(i + 8)..]);
            AssertThat(ModelArm.HashUpdate1(a, b, c) == RealArm.HashUpdate1(a, b, c)).IsTrue();
            AssertThat(ModelArm.HashUpdate2(a, b, c) == RealArm.HashUpdate2(a, b, c)).IsTrue();
            AssertThat(ModelArm.ScheduleUpdate0(a, b) == RealArm.ScheduleUpdate0(a, b)).IsTrue();
            AssertThat(ModelArm.ScheduleUpdate1(a, b, c) == RealArm.ScheduleUpdate1(a, b, c)).IsTrue();
        }
    }
}
