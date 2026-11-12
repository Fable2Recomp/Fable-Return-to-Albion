namespace Refii.BuildCache;

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Execution metrics returned by <see cref="IncrementalZipPacker.PackDirectory"/>.
/// </summary>
/// <param name="TotalEntries">Total entries written (or verified) in the archive.</param>
/// <param name="CompressedEntries">Number of new or modified entries compressed during this run.</param>
/// <param name="ReusedStreamEntries">Number of unchanged entries whose raw Deflate/Stored byte streams were copied directly from the previous archive.</param>
/// <param name="NoOp">Whether the target archive already matched every entry 1:1 and required zero writes.</param>
/// <param name="ZipSizeBytes">Final size of the output <c>.zip</c> archive in bytes.</param>
public readonly record struct ZipPackStats(
    int TotalEntries,
    int CompressedEntries,
    int ReusedStreamEntries,
    bool NoOp,
    long ZipSizeBytes);

/// <summary>
/// Multi-core PKZIP + ZIP64 archive builder with incremental raw Deflate stream reuse.
/// </summary>
/// <remarks>
/// Standard <c>cmake -E tar cf --format=zip</c> runs single-threaded from scratch (~310s for 25,720 files).
/// <see cref="IncrementalZipPacker"/> maintains a binary sidecar index (<c>&lt;zipPath&gt;.idx</c>) mapping each
/// archive path to its content SHA-256, CRC-32, compression method, and raw payload byte range inside the previous
/// <c>.zip</c>. On incremental builds:
/// <list type="bullet">
/// <item><description>0 files changed: instant <c>0.00s</c> no-op after tree hash check.</description></item>
/// <item><description>1..K files changed: compresses only the K changed files across all CPU cores while splicing the unchanged raw Deflate byte ranges directly from the previous <c>.zip</c>.</description></item>
/// <item><description>Cold build: compresses all 25,720 files in parallel across <see cref="Environment.ProcessorCount"/> cores using <c>.NET</c>'s SIMD-accelerated <c>zlib-ng</c> <see cref="DeflateStream"/>.</description></item>
/// </list>
/// </remarks>
public static class IncrementalZipPacker
{
    private const uint SidecarMagic = 0x31495A52u; // "RZI1"
    private const uint LocalFileHeaderSig = 0x04034B50u;
    private const uint CentralDirHeaderSig = 0x02014B50u;
    private const uint Zip64EocdSig = 0x06064B50u;
    private const uint Zip64EocdLocatorSig = 0x07064B50u;
    private const uint EocdSig = 0x06054B50u;

    private const ushort Utf8Flag = 0x0800;
    private const ushort MethodStore = 0;
    private const ushort MethodDeflate = 8;
    private const long InMemorySpillThreshold = 16L * 1024L * 1024L; // 16 MiB

    private sealed class IndexedZipEntry
    {
        public required string ArchivePath { get; init; }
        public required long UncompressedSize { get; init; }
        public required uint UnixMode { get; init; }
        public required byte[] Sha256 { get; init; }
        public required uint Crc32 { get; init; }
        public required ushort Method { get; init; }
        public required long CompressedSize { get; init; }
        public required long RawDataOffset { get; init; }
        public required ushort DosTime { get; set; }
        public required ushort DosDate { get; set; }
    }

    private sealed class PreparedEntry
    {
        public required string ArchivePath { get; init; }
        public required byte[] NameBytes { get; init; }
        public required long UncompressedSize { get; init; }
        public required uint UnixMode { get; init; }
        public required byte[] Sha256 { get; init; }
        public required ushort DosTime { get; set; }
        public required ushort DosDate { get; set; }

        public bool ReusedFromOldZip;
        public long OldRawDataOffset;

        public uint Crc32;
        public ushort Method;
        public long CompressedSize;
        public byte[]? InMemoryPayload;
        public string? SpilledTempFile;
        public long NewLocalHeaderOffset;
        public long NewRawDataOffset;
    }

    /// <summary>
    /// Packs <paramref name="appDir"/> into <paramref name="zipPath"/> with every entry rooted under
    /// <c>&lt;archiveRootPrefix&gt;/</c> (e.g. <c>Fable 2 Return to Albion/</c>).
    /// </summary>
    /// <param name="appDir">Directory whose contents form the archive.</param>
    /// <param name="archiveRootPrefix">Top-level folder prefix written inside the zip.</param>
    /// <param name="zipPath">Output <c>.zip</c> path.</param>
    /// <param name="hashIndex">Shared persistent file hash index.</param>
    /// <param name="forceFullRebuild">When <see langword="true"/>, ignores any existing sidecar index and recompresses all entries.</param>
    /// <returns>Detailed <see cref="ZipPackStats"/> for diagnostics and verification.</returns>
    public static ZipPackStats PackDirectory(
        string appDir,
        string archiveRootPrefix,
        string zipPath,
        FileHashIndex hashIndex,
        bool forceFullRebuild = false)
    {
        if (!Directory.Exists(appDir))
        {
            throw new DirectoryNotFoundException($"Cannot pack missing directory: {appDir}");
        }

        var sw = Stopwatch.StartNew();
        string cleanPrefix = archiveRootPrefix.Trim('/').Trim('\\');
        string sidecarPath = $"{zipPath}.idx";

        List<ScannedEntry> scanned = FileHashIndex.ScanDirectory(appDir, ignoreAbsoluteSymlinks: true);
        var sha256List = new byte[scanned.Count][];

        Parallel.For(0, scanned.Count, i =>
        {
            ScannedEntry e = scanned[i];
            sha256List[i] = e.SymlinkTarget is not null
                ? SHA256.HashData(Encoding.UTF8.GetBytes(e.SymlinkTarget))
                : hashIndex.GetOrComputeFileSha256(e.FullPath, e.Length, e.LastWriteUtcTicks);
        });

        bool bypassCache = forceFullRebuild ||
                           string.Equals(Environment.GetEnvironmentVariable("REFII_NO_BUILD_CACHE"), "1", StringComparison.Ordinal);

        Dictionary<string, IndexedZipEntry>? oldIndex = null;
        if (!bypassCache && File.Exists(zipPath))
        {
            var zipInfo = new FileInfo(zipPath);
            oldIndex = TryLoadSidecar(sidecarPath, zipInfo.Length, zipInfo.LastWriteTimeUtc.Ticks);
        }

        // Fast no-op check: if old zip is valid and every entry matches 1:1 in path, size, mode, and SHA-256.
        if (oldIndex is not null && oldIndex.Count == scanned.Count)
        {
            bool allIdentical = true;
            for (int i = 0; i < scanned.Count; i++)
            {
                ScannedEntry s = scanned[i];
                string archivePath = string.IsNullOrEmpty(cleanPrefix) ? s.RelativePath : $"{cleanPrefix}/{s.RelativePath}";
                if (!oldIndex.TryGetValue(archivePath, out IndexedZipEntry? prev) ||
                    prev.UncompressedSize != s.Length ||
                    prev.UnixMode != s.UnixMode ||
                    !prev.Sha256.AsSpan().SequenceEqual(sha256List[i]))
                {
                    allIdentical = false;
                    break;
                }
            }

            if (allIdentical)
            {
                long existingLen = new FileInfo(zipPath).Length;
                sw.Stop();
                Console.WriteLine(
                    $"[zip-cache] HIT ({scanned.Count} entries unchanged, {sw.Elapsed.TotalMilliseconds:F0} ms) - " +
                    $"kept {zipPath} ({existingLen / (1024 * 1024)} MB).");
                return new ZipPackStats(scanned.Count, 0, scanned.Count, NoOp: true, existingLen);
            }
        }

        var prepared = new PreparedEntry[scanned.Count];
        var toCompressIndices = new List<int>();
        int reusedCount = 0;

        for (int i = 0; i < scanned.Count; i++)
        {
            ScannedEntry s = scanned[i];
            string archivePath = string.IsNullOrEmpty(cleanPrefix) ? s.RelativePath : $"{cleanPrefix}/{s.RelativePath}";
            (ushort dosDate, ushort dosTime) = ToDosDateTime(s.LastWriteUtcTicks);

            var item = new PreparedEntry
            {
                ArchivePath = archivePath,
                NameBytes = Encoding.UTF8.GetBytes(archivePath),
                UncompressedSize = s.Length,
                UnixMode = s.UnixMode,
                Sha256 = sha256List[i],
                DosDate = dosDate,
                DosTime = dosTime,
            };

            if (oldIndex is not null &&
                oldIndex.TryGetValue(archivePath, out IndexedZipEntry? prev) &&
                prev.UncompressedSize == s.Length &&
                prev.Sha256.AsSpan().SequenceEqual(sha256List[i]))
            {
                item.ReusedFromOldZip = true;
                item.OldRawDataOffset = prev.RawDataOffset;
                item.Crc32 = prev.Crc32;
                item.Method = prev.Method;
                item.CompressedSize = prev.CompressedSize;
                item.DosDate = prev.DosDate;
                item.DosTime = prev.DosTime;
                reusedCount++;
            }
            else
            {
                toCompressIndices.Add(i);
            }

            prepared[i] = item;
        }

        string zipParent = Path.GetDirectoryName(Path.GetFullPath(zipPath))!;
        Directory.CreateDirectory(zipParent);
        string spillDir = Path.Combine(zipParent, $".zip-spill-{Environment.ProcessId}-{Guid.NewGuid():N}");
        string tempZipPath = $"{zipPath}.{Environment.ProcessId}.tmp";

        try
        {
            if (toCompressIndices.Count > 0)
            {
                int spillCounter = 0;
                Parallel.ForEach(toCompressIndices, idx =>
                {
                    CompressSingleEntry(scanned[idx], prepared[idx], spillDir, ref spillCounter);
                });
            }

            using (FileStream? oldZipStream = (reusedCount > 0 && File.Exists(zipPath))
                ? new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 262144)
                : null)
            using (var outZip = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 262144))
            {
                byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(262144);
                try
                {
                    for (int i = 0; i < prepared.Length; i++)
                    {
                        PreparedEntry entry = prepared[i];
                        entry.NewLocalHeaderOffset = outZip.Position;
                        WriteLocalFileHeader(outZip, entry);
                        entry.NewRawDataOffset = outZip.Position;

                        if (entry.CompressedSize > 0)
                        {
                            if (entry.ReusedFromOldZip)
                            {
                                CopyExactRange(oldZipStream!, entry.OldRawDataOffset, outZip, entry.CompressedSize, copyBuffer);
                            }
                            else if (entry.InMemoryPayload is not null)
                            {
                                outZip.Write(entry.InMemoryPayload, 0, (int)entry.CompressedSize);
                                entry.InMemoryPayload = null;
                            }
                            else if (entry.SpilledTempFile is not null)
                            {
                                using var spillIn = new FileStream(entry.SpilledTempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 131072);
                                CopyExactStream(spillIn, outZip, entry.CompressedSize, copyBuffer);
                            }
                        }
                    }

                    long cdStartOffset = outZip.Position;
                    for (int i = 0; i < prepared.Length; i++)
                    {
                        WriteCentralDirectoryHeader(outZip, prepared[i]);
                    }

                    long cdSize = outZip.Position - cdStartOffset;
                    WriteEndOfCentralDirectory(outZip, prepared.Length, cdSize, cdStartOffset);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(copyBuffer);
                }
            }

            File.Move(tempZipPath, zipPath, overwrite: true);
            var finalInfo = new FileInfo(zipPath);
            SaveSidecar(sidecarPath, finalInfo.Length, finalInfo.LastWriteTimeUtc.Ticks, prepared);

            sw.Stop();
            Console.WriteLine(
                $"[zip-cache] Packed {zipPath} ({finalInfo.Length / (1024 * 1024)} MB) in {sw.Elapsed.TotalSeconds:F2}s " +
                $"(compressed: {toCompressIndices.Count}, reused raw streams: {reusedCount}, total: {prepared.Length}).");

            return new ZipPackStats(
                TotalEntries: prepared.Length,
                CompressedEntries: toCompressIndices.Count,
                ReusedStreamEntries: reusedCount,
                NoOp: false,
                ZipSizeBytes: finalInfo.Length);
        }
        finally
        {
            try
            {
                if (File.Exists(tempZipPath))
                {
                    File.Delete(tempZipPath);
                }

                if (Directory.Exists(spillDir))
                {
                    Directory.Delete(spillDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static void CompressSingleEntry(
        ScannedEntry source,
        PreparedEntry target,
        string spillDir,
        ref int spillCounter)
    {
        if (source.SymlinkTarget is not null)
        {
            byte[] linkBytes = Encoding.UTF8.GetBytes(source.SymlinkTarget);
            target.Crc32 = Crc32Fast.Compute(linkBytes);
            target.Method = MethodStore;
            target.CompressedSize = linkBytes.Length;
            target.InMemoryPayload = linkBytes;
            return;
        }

        if (source.Length == 0)
        {
            target.Crc32 = 0u;
            target.Method = MethodStore;
            target.CompressedSize = 0;
            target.InMemoryPayload = Array.Empty<byte>();
            return;
        }

        if (source.Length <= InMemorySpillThreshold)
        {
            byte[] raw = File.ReadAllBytes(source.FullPath);
            target.Crc32 = Crc32Fast.Compute(raw);

            // Very tiny files rarely shrink after Deflate block headers.
            if (raw.Length <= 32)
            {
                target.Method = MethodStore;
                target.CompressedSize = raw.Length;
                target.InMemoryPayload = raw;
                return;
            }

            using var ms = new MemoryStream(Math.Min(raw.Length, Math.Max(256, raw.Length / 2)));
            using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(raw, 0, raw.Length);
            }

            if (ms.Length < raw.Length)
            {
                target.Method = MethodDeflate;
                target.CompressedSize = ms.Length;
                target.InMemoryPayload = ms.ToArray();
            }
            else
            {
                target.Method = MethodStore;
                target.CompressedSize = raw.Length;
                target.InMemoryPayload = raw;
            }

            return;
        }

        // Large file (> 16 MiB): stream CRC-32 + Deflate to disk to keep heap footprint small.
        Directory.CreateDirectory(spillDir);
        int id = Interlocked.Increment(ref spillCounter);
        string spillPath = Path.Combine(spillDir, $"entry_{id}.deflate");

        uint crc = 0u;
        byte[] buf = ArrayPool<byte>.Shared.Rent(131072);
        try
        {
            using (var input = new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 131072))
            using (var spillOut = new FileStream(spillPath, FileMode.Create, FileAccess.Write, FileShare.None, 131072))
            using (var deflate = new DeflateStream(spillOut, CompressionLevel.Optimal, leaveOpen: true))
            {
                int read;
                while ((read = input.Read(buf, 0, buf.Length)) > 0)
                {
                    crc = Crc32Fast.Update(crc, buf.AsSpan(0, read));
                    deflate.Write(buf, 0, read);
                }
            }

            long deflatedLen = new FileInfo(spillPath).Length;
            target.Crc32 = crc;
            if (deflatedLen < source.Length)
            {
                target.Method = MethodDeflate;
                target.CompressedSize = deflatedLen;
                target.SpilledTempFile = spillPath;
            }
            else
            {
                File.Delete(spillPath);
                target.Method = MethodStore;
                target.CompressedSize = source.Length;
                target.SpilledTempFile = source.FullPath;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private static void WriteLocalFileHeader(Stream stream, PreparedEntry entry)
    {
        bool zip64 = entry.UncompressedSize >= 0xFFFFFFFFL || entry.CompressedSize >= 0xFFFFFFFFL;
        ushort extraLen = zip64 ? (ushort)20 : (ushort)0;
        Span<byte> header = stackalloc byte[30];

        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], LocalFileHeaderSig);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], zip64 ? (ushort)45 : (ushort)20);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], Utf8Flag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], entry.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..12], entry.DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], entry.DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..18], entry.Crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..22], zip64 ? 0xFFFFFFFFu : (uint)entry.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..26], zip64 ? 0xFFFFFFFFu : (uint)entry.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..28], (ushort)entry.NameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..30], extraLen);

        stream.Write(header);
        stream.Write(entry.NameBytes);

        if (zip64)
        {
            Span<byte> extra = stackalloc byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(extra[0..2], 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..4], 16);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[4..12], (ulong)entry.UncompressedSize);
            BinaryPrimitives.WriteUInt64LittleEndian(extra[12..20], (ulong)entry.CompressedSize);
            stream.Write(extra);
        }
    }

    private static void WriteCentralDirectoryHeader(Stream stream, PreparedEntry entry)
    {
        bool uncomp64 = entry.UncompressedSize >= 0xFFFFFFFFL;
        bool comp64 = entry.CompressedSize >= 0xFFFFFFFFL;
        bool offset64 = entry.NewLocalHeaderOffset >= 0xFFFFFFFFL;
        bool zip64 = uncomp64 || comp64 || offset64;

        ushort zip64PayloadBytes = (ushort)((uncomp64 ? 8 : 0) + (comp64 ? 8 : 0) + (offset64 ? 8 : 0));
        ushort extraLen = zip64 ? (ushort)(4 + zip64PayloadBytes) : (ushort)0;

        ushort specVer = zip64 ? (ushort)45 : (ushort)20;
        ushort versionMadeBy = entry.UnixMode != 0u ? (ushort)((3 << 8) | specVer) : specVer;
        uint externalAttrs = entry.UnixMode != 0u ? (entry.UnixMode << 16) : 0u;

        Span<byte> header = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], CentralDirHeaderSig);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..6], versionMadeBy);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..8], specVer);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..10], Utf8Flag);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..12], entry.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..14], entry.DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..16], entry.DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], entry.Crc32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], comp64 ? 0xFFFFFFFFu : (uint)entry.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], uncomp64 ? 0xFFFFFFFFu : (uint)entry.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..30], (ushort)entry.NameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..32], extraLen);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], 0); // Comment length
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], 0); // Disk number start
        BinaryPrimitives.WriteUInt16LittleEndian(header[36..38], 0); // Internal attributes
        BinaryPrimitives.WriteUInt32LittleEndian(header[38..42], externalAttrs);
        BinaryPrimitives.WriteUInt32LittleEndian(header[42..46], offset64 ? 0xFFFFFFFFu : (uint)entry.NewLocalHeaderOffset);

        stream.Write(header);
        stream.Write(entry.NameBytes);

        if (zip64)
        {
            Span<byte> extra = stackalloc byte[28];
            BinaryPrimitives.WriteUInt16LittleEndian(extra[0..2], 0x0001);
            BinaryPrimitives.WriteUInt16LittleEndian(extra[2..4], zip64PayloadBytes);
            int cursor = 4;
            if (uncomp64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(extra.Slice(cursor, 8), (ulong)entry.UncompressedSize);
                cursor += 8;
            }

            if (comp64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(extra.Slice(cursor, 8), (ulong)entry.CompressedSize);
                cursor += 8;
            }

            if (offset64)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(extra.Slice(cursor, 8), (ulong)entry.NewLocalHeaderOffset);
                cursor += 8;
            }

            stream.Write(extra[..cursor]);
        }
    }

    private static void WriteEndOfCentralDirectory(Stream stream, int entryCount, long cdSize, long cdStartOffset)
    {
        bool needZip64 = entryCount >= 0xFFFF || cdSize >= 0xFFFFFFFFL || cdStartOffset >= 0xFFFFFFFFL;
        if (needZip64)
        {
            long zip64EocdOffset = stream.Position;
            Span<byte> z64 = stackalloc byte[56];
            BinaryPrimitives.WriteUInt32LittleEndian(z64[0..4], Zip64EocdSig);
            BinaryPrimitives.WriteUInt64LittleEndian(z64[4..12], 44UL);
            BinaryPrimitives.WriteUInt16LittleEndian(z64[12..14], 45);
            BinaryPrimitives.WriteUInt16LittleEndian(z64[14..16], 45);
            BinaryPrimitives.WriteUInt32LittleEndian(z64[16..20], 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(z64[20..24], 0u);
            BinaryPrimitives.WriteUInt64LittleEndian(z64[24..32], (ulong)entryCount);
            BinaryPrimitives.WriteUInt64LittleEndian(z64[32..40], (ulong)entryCount);
            BinaryPrimitives.WriteUInt64LittleEndian(z64[40..48], (ulong)cdSize);
            BinaryPrimitives.WriteUInt64LittleEndian(z64[48..56], (ulong)cdStartOffset);
            stream.Write(z64);

            Span<byte> loc = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(loc[0..4], Zip64EocdLocatorSig);
            BinaryPrimitives.WriteUInt32LittleEndian(loc[4..8], 0u);
            BinaryPrimitives.WriteUInt64LittleEndian(loc[8..16], (ulong)zip64EocdOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(loc[16..20], 1u);
            stream.Write(loc);
        }

        Span<byte> eocd = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[0..4], EocdSig);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[4..6], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[6..8], 0);
        ushort count16 = entryCount >= 0xFFFF ? (ushort)0xFFFF : (ushort)entryCount;
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[8..10], count16);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[10..12], count16);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[12..16], cdSize >= 0xFFFFFFFFL ? 0xFFFFFFFFu : (uint)cdSize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd[16..20], cdStartOffset >= 0xFFFFFFFFL ? 0xFFFFFFFFu : (uint)cdStartOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd[20..22], 0);
        stream.Write(eocd);
    }

    private static void CopyExactRange(FileStream source, long sourceOffset, Stream dest, long byteCount, byte[] buffer)
    {
        source.Position = sourceOffset;
        CopyExactStream(source, dest, byteCount, buffer);
    }

    private static void CopyExactStream(Stream source, Stream dest, long byteCount, byte[] buffer)
    {
        long remaining = byteCount;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected EOF while splicing compressed ZIP stream.");
            }

            dest.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static (ushort DosDate, ushort DosTime) ToDosDateTime(long utcTicks)
    {
        DateTime dt = new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime();
        int year = Math.Clamp(dt.Year, 1980, 2107);
        ushort dosDate = (ushort)(((year - 1980) << 9) | (dt.Month << 5) | dt.Day);
        ushort dosTime = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
        return (dosDate, dosTime);
    }

    private static Dictionary<string, IndexedZipEntry>? TryLoadSidecar(
        string sidecarPath,
        long expectedZipLength,
        long expectedZipTicks)
    {
        if (!File.Exists(sidecarPath))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != SidecarMagic)
            {
                return null;
            }

            long zipLen = reader.ReadInt64();
            long zipTicks = reader.ReadInt64();
            if (zipLen != expectedZipLength || zipTicks != expectedZipTicks)
            {
                return null;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 2_000_000)
            {
                return null;
            }

            var map = new Dictionary<string, IndexedZipEntry>(count, StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                string archivePath = reader.ReadString();
                long uncomp = reader.ReadInt64();
                uint unixMode = reader.ReadUInt32();
                byte[] sha = reader.ReadBytes(32);
                uint crc = reader.ReadUInt32();
                ushort method = reader.ReadUInt16();
                long comp = reader.ReadInt64();
                long rawOffset = reader.ReadInt64();
                ushort dosTime = reader.ReadUInt16();
                ushort dosDate = reader.ReadUInt16();

                if (sha.Length != 32 || rawOffset < 0 || rawOffset + comp > expectedZipLength)
                {
                    return null;
                }

                map[archivePath] = new IndexedZipEntry
                {
                    ArchivePath = archivePath,
                    UncompressedSize = uncomp,
                    UnixMode = unixMode,
                    Sha256 = sha,
                    Crc32 = crc,
                    Method = method,
                    CompressedSize = comp,
                    RawDataOffset = rawOffset,
                    DosTime = dosTime,
                    DosDate = dosDate,
                };
            }

            return map;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveSidecar(
        string sidecarPath,
        long zipLength,
        long zipTicks,
        PreparedEntry[] entries)
    {
        string tmp = $"{sidecarPath}.{Environment.ProcessId}.tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(SidecarMagic);
                writer.Write(zipLength);
                writer.Write(zipTicks);
                writer.Write(entries.Length);
                foreach (PreparedEntry e in entries)
                {
                    writer.Write(e.ArchivePath);
                    writer.Write(e.UncompressedSize);
                    writer.Write(e.UnixMode);
                    writer.Write(e.Sha256);
                    writer.Write(e.Crc32);
                    writer.Write(e.Method);
                    writer.Write(e.CompressedSize);
                    writer.Write(e.NewRawDataOffset);
                    writer.Write(e.DosTime);
                    writer.Write(e.DosDate);
                }
            }

            File.Move(tmp, sidecarPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
