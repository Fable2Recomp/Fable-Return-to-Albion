using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Recompile;

/// <summary>
/// Content-addressed codegen output stamp and verifier, allowing warm Desktop builds to skip XEX decoding,
/// CFG recovery, and C++ emission when the executable, title update, manifest/config TOMLs, and generator binary
/// are all unchanged.
/// </summary>
public static class CodegenCache
{
    /// <summary>Name of the codegen stamp record stored inside the persistent generated directory.</summary>
    public const string StampFileName = "codegen-stamp.txt";

    /// <summary>Bump whenever post-codegen source transforms (<see cref="CodegenTransforms"/>) change output semantics.</summary>
    private const int TransformRevision = 1;

    /// <summary>
    /// Computes the deterministic input digest governing ReXGlue code generation for one title and executable version.
    /// </summary>
    /// <param name="gameExecutableHash">Combined SHA-256 of <c>default.xex</c> and optional <c>default.xexp</c>.</param>
    /// <param name="manifestAndConfigs">Ordered manifest and included config TOML file names and bytes.</param>
    /// <param name="generatorIdentity">Identity/hash of the generator binary (<c>refii_recompiler</c>).</param>
    /// <param name="sdkVersion">Reported ReXGlue SDK version string.</param>
    /// <returns>64-character lowercase hex SHA-256 digest.</returns>
    public static string ComputeInputDigest(
        string gameExecutableHash,
        IEnumerable<(string Name, byte[] Bytes)> manifestAndConfigs,
        string generatorIdentity,
        string sdkVersion)
    {
        using var sha = Refii.Core.Engine_Hash.CreateSha256();
        void FeedText(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        FeedText($"rev={TransformRevision}\ngame={gameExecutableHash.ToLowerInvariant()}\ngenerator={generatorIdentity}\nsdk={sdkVersion}\n");
        foreach (var (name, bytes) in manifestAndConfigs.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            FeedText($"toml {name} {bytes.Length}\n");
            if (bytes.Length > 0)
            {
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }

            FeedText("\n");
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash ?? Array.Empty<byte>()).ToLowerInvariant();
    }

    /// <summary>
    /// Records a verified snapshot of every generated file inside <paramref name="generatedDirectory"/> after codegen
    /// and post-codegen transforms have completed.
    /// </summary>
    /// <param name="generatedDirectory">Generated source directory on disk.</param>
    /// <param name="inputDigest">Expected digest returned by <see cref="ComputeInputDigest"/>.</param>
    /// <param name="ignoredFileNames">Transient file names in <paramref name="generatedDirectory"/> excluded from the snapshot.</param>
    public static void Record(
        string generatedDirectory,
        string inputDigest,
        IReadOnlySet<string>? ignoredFileNames = null)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generatedDirectory));
        if (!Directory.Exists(root))
        {
            return;
        }

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Full: path, Relative: Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(item => !ShouldIgnore(item.Relative, ignoredFileNames))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToList();

        var text = new StringBuilder();
        text.Append("digest ").Append(inputDigest).Append('\n');
        foreach (var (full, relative) in files)
        {
            var info = new FileInfo(full);
            string hash = CompileCache.HashFileMemoized(full) ?? CompileCache.HashFileDirect(full);
            text.Append("file ")
                .Append(hash).Append(' ')
                .Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(relative).Append('\n');
        }

        text.Append("end\n");
        string stampPath = Path.Combine(root, StampFileName);
        string tempPath = stampPath + ".tmp";
        File.WriteAllText(tempPath, text.ToString());
        File.Move(tempPath, stampPath, overwrite: true);
    }

    /// <summary>
    /// Verifies whether an existing <paramref name="generatedDirectory"/> matches <paramref name="expectedDigest"/>
    /// with every generated file intact and unmodified, removing any unlisted stray files.
    /// </summary>
    /// <param name="generatedDirectory">Generated source directory on disk.</param>
    /// <param name="expectedDigest">Current digest computed by <see cref="ComputeInputDigest"/>.</param>
    /// <param name="ignoredFileNames">Transient file names to ignore during stray-file cleanup.</param>
    /// <returns><c>true</c> when the cached codegen tree is complete and verified; <c>false</c> when codegen must run.</returns>
    public static bool TryReuse(
        string generatedDirectory,
        string expectedDigest,
        IReadOnlySet<string>? ignoredFileNames = null)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generatedDirectory));
            string stampPath = Path.Combine(root, StampFileName);
            if (!Directory.Exists(root) ||
                !File.Exists(stampPath) ||
                !File.Exists(Path.Combine(root, "sources.cmake")))
            {
                return false;
            }

            string[] lines = File.ReadAllLines(stampPath);
            if (lines.Length < 2 ||
                lines[^1] != "end" ||
                !lines[0].StartsWith("digest ", StringComparison.Ordinal) ||
                lines[0][7..] != expectedDigest)
            {
                return false;
            }

            StringComparer pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var expectedFiles = new HashSet<string>(pathComparer);

            for (int i = 1; i < lines.Length - 1; i++)
            {
                string line = lines[i];
                if (!line.StartsWith("file ", StringComparison.Ordinal))
                {
                    return false;
                }

                int firstSpace = line.IndexOf(' ', 5);
                if (firstSpace <= 5)
                {
                    return false;
                }

                int secondSpace = line.IndexOf(' ', firstSpace + 1);
                if (secondSpace <= firstSpace + 1)
                {
                    return false;
                }

                int thirdSpace = line.IndexOf(' ', secondSpace + 1);
                if (thirdSpace <= secondSpace + 1)
                {
                    return false;
                }

                string expectedHash = line[5..firstSpace];
                if (!long.TryParse(line[(firstSpace + 1)..secondSpace], NumberStyles.Integer, CultureInfo.InvariantCulture, out long expectedLength) ||
                    !long.TryParse(line[(secondSpace + 1)..thirdSpace], NumberStyles.Integer, CultureInfo.InvariantCulture, out long expectedTicks))
                {
                    return false;
                }

                string relative = line[(thirdSpace + 1)..];
                if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal))
                {
                    return false;
                }

                string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                var info = new FileInfo(full);
                if (!info.Exists || info.Length != expectedLength)
                {
                    return false;
                }

                long nowTicks = DateTime.UtcNow.Ticks;
                bool racyTimestamp = nowTicks - expectedTicks <= TimeSpan.TicksPerSecond * 2;
                if ((racyTimestamp || info.LastWriteTimeUtc.Ticks != expectedTicks) &&
                    CompileCache.HashFileDirect(full) != expectedHash)
                {
                    return false;
                }

                expectedFiles.Add(relative);
            }

            if (expectedFiles.Count == 0)
            {
                return false;
            }

            // Validate sources.cmake still resolves cleanly against the verified tree.
            _ = CodegenTransforms.ReadGeneratedSources(Path.Combine(root, "sources.cmake"), root);

            // Remove any unlisted stray files so stale headers or sources cannot pollute compilation.
            foreach (string existing in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
            {
                string relative = Path.GetRelativePath(root, existing).Replace('\\', '/');
                if (ShouldIgnore(relative, ignoredFileNames))
                {
                    continue;
                }

                if (!expectedFiles.Contains(relative))
                {
                    File.Delete(existing);
                }
            }

            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool ShouldIgnore(string relativePath, IReadOnlySet<string>? ignoredFileNames)
    {
        string fileName = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        if (fileName == StampFileName || fileName == StampFileName + ".tmp" || fileName == CodegenTransforms.PartitionFile)
        {
            return true;
        }

        return ignoredFileNames != null && ignoredFileNames.Contains(fileName);
    }
}
