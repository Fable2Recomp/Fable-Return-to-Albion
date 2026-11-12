namespace Refii.BuildCache;

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;

/// <summary>
/// High-throughput IEEE 802.3 CRC-32 calculator (reflected polynomial <c>0xEDB88320</c>)
/// matching the PKZIP archive specification.
/// </summary>
/// <remarks>
/// Note that x86 SSE4.2's hardware <c>CRC32</c> instruction computes Castagnoli CRC-32C
/// (<c>0x82F63B78</c>), which is incompatible with PKZIP. On ARM64 we use the dedicated
/// IEEE 802.3 <see cref="Crc32"/> intrinsics; on all other architectures (including x86_64)
/// we use an unrolled slice-by-8 lookup table operating on 64-bit chunks (~3.5+ GB/s/core).
/// </remarks>
public static class Crc32Fast
{
    private const uint Polynomial = 0xEDB88320u;
    private static readonly uint[] Table = BuildSliceBy8Table();

    /// <summary>
    /// Computes the PKZIP IEEE 802.3 CRC-32 checksum of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Input byte span.</param>
    /// <returns>32-bit unsigned CRC-32 value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Compute(ReadOnlySpan<byte> data) => Update(0u, data);

    /// <summary>
    /// Updates a running PKZIP IEEE 802.3 CRC-32 checksum with <paramref name="data"/>.
    /// </summary>
    /// <param name="crc">Previous CRC-32 state (pass <c>0u</c> for the first chunk).</param>
    /// <param name="data">Next span of bytes to fold into the checksum.</param>
    /// <returns>Updated 32-bit unsigned CRC-32 value.</returns>
    public static unsafe uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint state = ~crc;

        if (Crc32.IsSupported)
        {
            int offset = 0;
            if (Crc32.Arm64.IsSupported)
            {
                while (offset + 8 <= data.Length)
                {
                    ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
                    state = Crc32.Arm64.ComputeCrc32(state, word);
                    offset += 8;
                }
            }

            while (offset < data.Length)
            {
                state = Crc32.ComputeCrc32(state, data[offset]);
                offset++;
            }

            return ~state;
        }

        fixed (byte* srcBase = data)
        fixed (uint* tbl = Table)
        {
            byte* p = srcBase;
            int remaining = data.Length;

            while (remaining >= 8)
            {
                uint low = Unsafe.ReadUnaligned<uint>(p) ^ state;
                uint high = Unsafe.ReadUnaligned<uint>(p + 4);

                state =
                    tbl[(7 * 256) + (byte)low] ^
                    tbl[(6 * 256) + (byte)(low >> 8)] ^
                    tbl[(5 * 256) + (byte)(low >> 16)] ^
                    tbl[(4 * 256) + (low >> 24)] ^
                    tbl[(3 * 256) + (byte)high] ^
                    tbl[(2 * 256) + (byte)(high >> 8)] ^
                    tbl[(1 * 256) + (byte)(high >> 16)] ^
                    tbl[(0 * 256) + (high >> 24)];

                p += 8;
                remaining -= 8;
            }

            while (remaining > 0)
            {
                state = tbl[(byte)(state ^ *p)] ^ (state >> 8);
                p++;
                remaining--;
            }
        }

        return ~state;
    }

    /// <summary>
    /// Computes the PKZIP IEEE 802.3 CRC-32 checksum of a stream without buffering the entire file in memory.
    /// </summary>
    /// <param name="stream">Readable input stream.</param>
    /// <returns>32-bit unsigned CRC-32 value.</returns>
    public static uint Compute(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[65536];
        uint crc = 0u;
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            crc = Update(crc, buffer[..read]);
        }

        return crc;
    }

    private static uint[] BuildSliceBy8Table()
    {
        var table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1u) != 0u ? (c >> 1) ^ Polynomial : c >> 1;
            }

            table[i] = c;
        }

        for (int slice = 1; slice < 8; slice++)
        {
            int prevBase = (slice - 1) * 256;
            int currBase = slice * 256;
            for (int i = 0; i < 256; i++)
            {
                uint prev = table[prevBase + i];
                table[currBase + i] = table[prev & 0xFFu] ^ (prev >> 8);
            }
        }

        return table;
    }
}
