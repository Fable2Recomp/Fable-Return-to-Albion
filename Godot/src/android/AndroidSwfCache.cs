using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using Refii.Core;

namespace Refii.Textures;

/// <summary>What a phone does differently with the pub games' cache folder: only this build's
/// files stay, and the boards' triangles are stored deflated.</summary>
/// <remarks>
/// Every name in the folder carries the build, so each new build starts the folder again and the
/// last one's files are never read. Almost all of it is the boards' triangles, 88 to 280 MB a
/// board: unindexed, with colours or UVs per corner, and 70% smaller deflated.
/// </remarks>
public static class AndroidSwfCache
{
    private const int FileBuffer = 1 << 16;

    private const int HashDigits = 16, StampDigits = 8;

    private static int _swept;

    /// <summary>Whether a board's triangles, meshes, images and parsed shapes go when the player
    /// leaves it, to be read back from this folder at the next open. They were kept for the rest of
    /// the process: hundreds of MB a board. The low-memory tier only, for the re-open's wait.</summary>
    public static bool ReleasesBoards =>
        ReleasesBoardsFor(Engine_Platform.IsAndroid, () => Refii.Game.AndroidMemoryRelease.LowRamMode);

    /// <summary>The rule behind <see cref="ReleasesBoards"/>; the tier is not asked off a phone.</summary>
    internal static bool ReleasesBoardsFor(bool android, Func<bool> lowRam) => android && lowRam();

    /// <summary>The managed bytes of one part's arrays: positions, colours (16 bytes each in memory)
    /// and UVs.</summary>
    internal static long PartBytes(int vertices, bool colours, bool uvs) =>
        (long)vertices * (8 + (colours ? 16 : 0) + (uvs ? 8 : 0));

    /// <summary>Deletes, on a worker and once per process, what other builds left in the folder.
    /// Does nothing off Android, where <paramref name="directory"/> is not even asked: a mount can
    /// run with no engine behind it, in a tool or a test, and the path comes from Godot.</summary>
    public static void SweepOtherBuilds(Func<string> directory, string buildStamp)
    {
        if (!Engine_Platform.IsAndroid || Interlocked.Exchange(ref _swept, 1) != 0)
        {
            return;
        }

        string folder = directory();
        Task.Run(() =>
        {
            (int files, long bytes) = DeleteOtherBuilds(folder, buildStamp);
            if (files != 0)
            {
                GD.Print($"[swf] {files} cache file(s) of other builds deleted, {bytes / (1024 * 1024)} MB");
            }
        });
    }

    /// <summary>Deletes the files whose name gives a build other than <paramref name="buildStamp"/>.
    /// A file this cannot read a build from is left alone, and so is one that cannot be deleted.</summary>
    /// <returns>How many files went and their size.</returns>
    internal static (int Files, long Bytes) DeleteOtherBuilds(string directory, string buildStamp)
    {
        int files = 0;
        long bytes = 0;
        try
        {
            if (!Directory.Exists(directory))
            {
                return (0, 0);
            }

            foreach (string path in Directory.EnumerateFiles(directory))
            {
                string? stamp = BuildStampOf(Path.GetFileName(path));
                if (stamp is null || stamp == buildStamp)
                {
                    continue;
                }

                try
                {
                    long size = new FileInfo(path).Length;
                    File.Delete(path);
                    files++;
                    bytes += size;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return (files, bytes);
    }

    /// <summary>The build in a cache file's name, or null if it is not one of the three kinds:
    /// <c>astc_&lt;hash&gt;_&lt;format&gt;_&lt;size&gt;_&lt;build&gt;.bin</c>,
    /// <c>&lt;game&gt;_&lt;hash&gt;_&lt;build&gt;_shapes.bin</c> and
    /// <c>&lt;pack&gt;_&lt;hash&gt;_&lt;build&gt;_&lt;face&gt;.res</c>, each perhaps still a temporary file.</summary>
    internal static string? BuildStampOf(string name)
    {
        if (name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            int bin = name.LastIndexOf(".bin.", StringComparison.Ordinal);
            if (bin < 0)
            {
                return null;
            }

            name = name[..(bin + ".bin".Length)];
        }

        if (name.StartsWith("astc_", StringComparison.Ordinal))
        {
            int start = name.Length - ".bin".Length - StampDigits;
            return name.EndsWith(".bin", StringComparison.Ordinal) && start > 0 && name[start - 1] == '_' &&
                IsHex(name.AsSpan(start, StampDigits))
                ? name.Substring(start, StampDigits)
                : null;
        }

        if (!name.EndsWith("_shapes.bin", StringComparison.Ordinal) && !name.EndsWith(".res", StringComparison.Ordinal))
        {
            return null;
        }

        // The first "_<hash>_<build>_": a game's or a face's own name may hold underscores.
        for (int i = 0; i + HashDigits + StampDigits + 3 <= name.Length; i++)
        {
            int build = i + 1 + HashDigits + 1;
            if (name[i] == '_' && name[build - 1] == '_' && name[build + StampDigits] == '_' &&
                IsHex(name.AsSpan(i + 1, HashDigits)) && IsHex(name.AsSpan(build, StampDigits)))
            {
                return name.Substring(build, StampDigits);
            }
        }

        return null;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Opens a board's triangles for reading: inflated on a phone, the file itself elsewhere.</summary>
    public static Stream OpenParts(string path) => OpenParts(path, Engine_Platform.IsAndroid);

    /// <summary>Creates a board's triangles file: deflated on a phone, the file itself elsewhere.</summary>
    public static Stream CreateParts(string path) => CreateParts(path, Engine_Platform.IsAndroid);

    internal static Stream OpenParts(string path, bool deflated)
    {
        var file = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read, FileBuffer);
        return deflated ? new DeflateStream(file, CompressionMode.Decompress) : file;
    }

    internal static Stream CreateParts(string path, bool deflated)
    {
        var file = new FileStream(path, FileMode.Create, System.IO.FileAccess.Write, FileShare.Read, FileBuffer);
        return deflated ? new DeflateStream(file, CompressionLevel.Fastest) : file;
    }
}
