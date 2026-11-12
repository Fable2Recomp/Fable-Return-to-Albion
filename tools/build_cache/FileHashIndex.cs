namespace Refii.BuildCache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

/// <summary>
/// Represents a single filesystem entry discovered during deterministic tree hashing or staging.
/// </summary>
/// <param name="RelativePath">Forward-slash normalized path relative to the scanned root.</param>
/// <param name="FullPath">Absolute host filesystem path.</param>
/// <param name="Length">Uncompressed byte length for regular files; UTF-8 target length for symlinks.</param>
/// <param name="LastWriteUtcTicks">UTC last-write timestamp in 100ns ticks.</param>
/// <param name="UnixMode">POSIX mode word (e.g. <c>0100755</c>, <c>0100644</c>, <c>0120777</c>), or <c>0</c> on Windows regular files.</param>
/// <param name="SymlinkTarget">Relative link target when the entry is a symbolic link; otherwise <see langword="null"/>.</param>
public readonly record struct ScannedEntry(
    string RelativePath,
    string FullPath,
    long Length,
    long LastWriteUtcTicks,
    uint UnixMode,
    string? SymlinkTarget);

/// <summary>
/// Persistent two-tier file hash index modelled on Git's index and Turborepo's file digest cache.
/// </summary>
/// <remarks>
/// Re-reading and hashing 25,000+ SDK, Zig, and asset files on every build wastes gigabytes of I/O.
/// This index caches <c>(fullPath, byteLength, lastWriteUtcTicks) -&gt; SHA-256</c> on disk while still
/// falling back to full SHA-256 hashing whenever a file's size or timestamp changes:
/// <list type="bullet">
/// <item><description>Unchanged <c>(size, mtime)</c>: <c>O(1)</c> dictionary lookup with zero file reads.</description></item>
/// <item><description>Bumped <c>mtime</c> with identical bytes (e.g. <c>git checkout</c>): re-hashes once, produces the exact same content SHA-256, and preserves downstream task cache hits.</description></item>
/// <item><description>In-place same-size byte modification: updated <c>mtime</c> forces re-hash and invalidates downstream caches.</description></item>
/// </list>
/// </remarks>
public sealed class FileHashIndex
{
    private const uint Magic = 0x31494846u; // "FHI1"
    private const int Sha256Length = 32;

    private readonly struct Entry(long length, long ticks, byte[] sha256)
    {
        public readonly long Length = length;
        public readonly long LastWriteUtcTicks = ticks;
        public readonly byte[] Sha256 = sha256;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries;
    private volatile bool _dirty;

    /// <summary>
    /// Number of full SHA-256 file reads performed since this index instance was loaded.
    /// Exposed so unit and feature tests can assert zero redundant disk reads on warm runs.
    /// </summary>
    public int HashComputations => _hashComputations;
    private int _hashComputations;

    /// <summary>
    /// Initializes an empty <see cref="FileHashIndex"/>.
    /// </summary>
    public FileHashIndex()
    {
        _entries = new ConcurrentDictionary<string, Entry>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    /// <summary>
    /// Loads a persisted <see cref="FileHashIndex"/> from <paramref name="indexPath"/>, or returns a fresh
    /// empty index if the file does not exist or is unreadable.
    /// </summary>
    /// <param name="indexPath">Path to the binary index file.</param>
    /// <returns>Loaded or newly initialized <see cref="FileHashIndex"/>.</returns>
    public static FileHashIndex Load(string indexPath)
    {
        var index = new FileHashIndex();
        if (!File.Exists(indexPath))
        {
            return index;
        }

        try
        {
            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (reader.ReadUInt32() != Magic)
            {
                return index;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > 2_000_000)
            {
                return index;
            }

            for (int i = 0; i < count; i++)
            {
                string path = reader.ReadString();
                long length = reader.ReadInt64();
                long ticks = reader.ReadInt64();
                byte[] hash = reader.ReadBytes(Sha256Length);
                if (hash.Length != Sha256Length)
                {
                    break;
                }

                index._entries[path] = new Entry(length, ticks, hash);
            }
        }
        catch
        {
            // A truncated or stale cache index is never fatal; we rebuild entries on demand.
            index._entries.Clear();
        }

        return index;
    }

    /// <summary>
    /// Atomically persists the index to <paramref name="indexPath"/> if any entries were added or updated.
    /// </summary>
    /// <param name="indexPath">Destination binary index path.</param>
    public void SaveIfDirty(string indexPath)
    {
        if (!_dirty)
        {
            return;
        }

        string? parent = Path.GetDirectoryName(indexPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string tempPath = $"{indexPath}.{Environment.ProcessId}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                ICollection<KeyValuePair<string, Entry>> snapshot = _entries.ToArray();
                writer.Write(snapshot.Count);
                foreach (KeyValuePair<string, Entry> kvp in snapshot)
                {
                    writer.Write(kvp.Key);
                    writer.Write(kvp.Value.Length);
                    writer.Write(kvp.Value.LastWriteUtcTicks);
                    writer.Write(kvp.Value.Sha256);
                }
            }

            File.Move(tempPath, indexPath, overwrite: true);
            _dirty = false;
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore best-effort cleanup failure.
            }
        }
    }

    /// <summary>
    /// Returns the 32-byte SHA-256 digest of <paramref name="fullPath"/>, reusing the cached digest
    /// when <paramref name="length"/> and <paramref name="lastWriteUtcTicks"/> match.
    /// </summary>
    /// <param name="fullPath">Normalized absolute path to the file.</param>
    /// <param name="length">Current file length in bytes.</param>
    /// <param name="lastWriteUtcTicks">Current UTC last-write timestamp in ticks.</param>
    /// <returns>32-byte SHA-256 hash array.</returns>
    public byte[] GetOrComputeFileSha256(string fullPath, long length, long lastWriteUtcTicks)
    {
        if (_entries.TryGetValue(fullPath, out Entry existing) &&
            existing.Length == length &&
            existing.LastWriteUtcTicks == lastWriteUtcTicks)
        {
            return existing.Sha256;
        }

        byte[] computed;
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536))
        {
            computed = SHA256.HashData(stream);
        }

        System.Threading.Interlocked.Increment(ref _hashComputations);
        _entries[fullPath] = new Entry(length, lastWriteUtcTicks, computed);
        _dirty = true;
        return computed;
    }

    /// <summary>
    ///Convenience overload inspecting <see cref="FileInfo"/> metadata for <paramref name="fullPath"/>.
    /// </summary>
    /// <param name="fullPath">Path to hash.</param>
    /// <returns>32-byte SHA-256 hash array.</returns>
    public byte[] GetOrComputeFileSha256(string fullPath)
    {
        string normalized = Path.GetFullPath(fullPath);
        var info = new FileInfo(normalized);
        return GetOrComputeFileSha256(normalized, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    /// <summary>
    /// Scans <paramref name="rootDir"/> recursively, pruning excluded directories before descending into them.
    /// </summary>
    /// <param name="rootDir">Root directory to scan.</param>
    /// <param name="shouldIncludeDirectory">Optional predicate receiving <c>(relativeDirPath, directoryName)</c>; returning <see langword="false"/> skips the entire subtree.</param>
    /// <param name="shouldIncludeFile">Optional predicate receiving <c>(relativeFilePath, fileName)</c>; returning <see langword="false"/> omits the file.</param>
    /// <param name="ignoreAbsoluteSymlinks">When <see langword="true"/>, symbolic links pointing to absolute host paths are skipped.</param>
    /// <returns>Sorted list of <see cref="ScannedEntry"/> records.</returns>
    public static List<ScannedEntry> ScanDirectory(
        string rootDir,
        Func<string, string, bool>? shouldIncludeDirectory = null,
        Func<string, string, bool>? shouldIncludeFile = null,
        bool ignoreAbsoluteSymlinks = true)
    {
        var results = new List<ScannedEntry>();
        if (!Directory.Exists(rootDir))
        {
            return results;
        }

        string fullRoot = Path.GetFullPath(rootDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var stack = new Stack<(string FullDir, string RelPrefix)>();
        stack.Push((fullRoot, string.Empty));

        while (stack.Count > 0)
        {
            (string currentDir, string relPrefix) = stack.Pop();
            var dirInfo = new DirectoryInfo(currentDir);

            FileSystemInfo[] items;
            try
            {
                items = dirInfo.GetFileSystemInfos();
            }
            catch
            {
                continue;
            }

            foreach (FileSystemInfo item in items)
            {
                string name = item.Name;
                string relPath = relPrefix.Length == 0 ? name : $"{relPrefix}/{name}";
                string? linkTarget = item.LinkTarget;

                if (linkTarget is not null)
                {
                    if (Path.IsPathRooted(linkTarget))
                    {
                        if (ignoreAbsoluteSymlinks)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        // Relative symlink: preserve as link entry without recursing into target.
                        if (shouldIncludeFile is not null && !shouldIncludeFile(relPath, name))
                        {
                            continue;
                        }

                        int targetByteCount = Encoding.UTF8.GetByteCount(linkTarget);
                        results.Add(new ScannedEntry(
                            relPath,
                            item.FullName,
                            targetByteCount,
                            item.LastWriteTimeUtc.Ticks,
                            0xA1FFu, // 0120777 (S_IFLNK | rwxrwxrwx)
                            linkTarget));
                        continue;
                    }
                }

                if ((item.Attributes & FileAttributes.Directory) != 0)
                {
                    if (shouldIncludeDirectory is not null && !shouldIncludeDirectory(relPath, name))
                    {
                        continue;
                    }

                    stack.Push((item.FullName, relPath));
                }
                else if (item is FileInfo fileInfo)
                {
                    if (shouldIncludeFile is not null && !shouldIncludeFile(relPath, name))
                    {
                        continue;
                    }

                    uint unixMode = GetPortableUnixMode(fileInfo);
                    results.Add(new ScannedEntry(
                        relPath,
                        fileInfo.FullName,
                        fileInfo.Length,
                        fileInfo.LastWriteTimeUtc.Ticks,
                        unixMode,
                        null));
                }
            }
        }

        results.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return results;
    }

    /// <summary>
    /// Computes the SHA-256 content hashes of all <paramref name="entries"/> in parallel and folds them
    /// into <paramref name="manifestHash"/>.
    /// </summary>
    /// <param name="label">Logical tree label mixed into the manifest hash so distinct trees never collide.</param>
    /// <param name="entries">Sorted entries produced by <see cref="ScanDirectory"/>.</param>
    /// <param name="manifestHash">Incremental SHA-256 hash builder.</param>
    /// <returns>Array of 32-byte content (or symlink target) SHA-256 hashes aligned 1:1 with <paramref name="entries"/>.</returns>
    public byte[][] AppendEntriesToManifestHash(
        string label,
        IReadOnlyList<ScannedEntry> entries,
        IncrementalHash manifestHash)
    {
        var hashes = new byte[entries.Count][];
        Parallel.For(0, entries.Count, i =>
        {
            ScannedEntry entry = entries[i];
            if (entry.SymlinkTarget is not null)
            {
                hashes[i] = SHA256.HashData(Encoding.UTF8.GetBytes(entry.SymlinkTarget));
            }
            else
            {
                hashes[i] = GetOrComputeFileSha256(entry.FullPath, entry.Length, entry.LastWriteUtcTicks);
            }
        });

        AppendString(manifestHash, $"TREE:{label}:{entries.Count}\n");
        Span<byte> modeBytes = stackalloc byte[4];
        for (int i = 0; i < entries.Count; i++)
        {
            ScannedEntry e = entries[i];
            AppendString(manifestHash, e.RelativePath);
            manifestHash.AppendData(e.SymlinkTarget is not null ? "L"u8 : "F"u8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(modeBytes, e.UnixMode);
            manifestHash.AppendData(modeBytes);
            manifestHash.AppendData(hashes[i]);
        }

        return hashes;
    }

    /// <summary>
    /// Appends a length-prefixed UTF-8 string to <paramref name="hash"/>.
    /// </summary>
    /// <param name="hash">Target incremental hash.</param>
    /// <param name="value">String value to append.</param>
    public static void AppendString(IncrementalHash hash, string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        Span<byte> len = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, utf8.Length);
        hash.AppendData(len);
        hash.AppendData(utf8);
    }

    /// <summary>
    /// Reads portable POSIX permission bits on Unix hosts while returning <c>0</c> on Windows.
    /// </summary>
    /// <param name="fileInfo">File metadata handle.</param>
    /// <returns>POSIX file mode word including <c>S_IFREG</c> (<c>0100000</c>), or <c>0</c> on Windows.</returns>
    public static uint GetPortableUnixMode(FileInfo fileInfo)
    {
        if (OperatingSystem.IsWindows())
        {
            return 0u;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(fileInfo.FullName);
            bool executable = (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
            return executable ? 0x81EDu : 0x81A4u; // 0100755 vs 0100644
        }
        catch
        {
            return 0x81A4u;
        }
    }
}
