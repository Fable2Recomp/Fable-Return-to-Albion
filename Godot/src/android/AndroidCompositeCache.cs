using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Host;

namespace Refii.Textures;

/// <summary>Finished character composites on disk, in the form they upload, across levels and launches.</summary>
/// <remarks>
/// A composite is decoded, blended, mipped, block-encoded and, on a GPU without BC, transcoded on
/// every level load, for the same few hundred images each time. A stored one is a file read.
///
/// KEYED BY WHAT IT WAS BUILT FROM: the composite's own key (its layers, weights, tints, modes),
/// what every texture it names is (where a shipped entry lies, or a mod's file's bytes), so a
/// modded texture can never answer with the form of the file it replaced, and the upload policy.
/// The folder is the build's, so a build that encodes differently never reads another's files;
/// the sweep deletes them. A composite built short of a layer, or across a remount, is not kept.
///
/// What comes back is the bytes that were uploaded when it was built, so the pixels cannot differ.
/// A file that is missing, short or wrong is a miss and the composite is built as before.
/// `android_composite_cache` (default on) turns it off.
/// </remarks>
public static class AndroidCompositeCache
{
    /// <summary>The config key that turns this off.</summary>
    public const string ConfigKey = "android_composite_cache";

    /// <summary>A 512 x 512 composite is about 350 KB as ASTC, a quarter of that a size down, and a level builds 80-130 of them.</summary>
    private const long BudgetBytes = 256L * 1048576;

    private static readonly string BuildStamp =
        typeof(AndroidCompositeCache).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];

    private static readonly object Gate = new();
    private static readonly Func<string, byte[]?> MountRead = Refii.Models.Engine_BnkMeshLoader.ReadBytes;

    private static bool _resolved;
    private static string? _directory;

    /// <summary>The folder composites are kept in, or null when this is off; desktop never has one.</summary>
    private static string? Folder
    {
        get
        {
            lock (Gate)
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _directory = ResolveFolder();
                }

                return _directory;
            }
        }
    }

    private static string? ResolveFolder()
    {
        try
        {
            if (!Engine_Platform.IsAndroid || !AndroidTextureUpload.Policy.Android ||
                !Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), ConfigKey, true))
            {
                return null;
            }

            string parent = Path.Combine(Engine_Title.Active.CacheDirectory, "composites");
            string folder = Path.Combine(parent, BuildStamp);
            var sweep = new Thread(() => Sweep(parent, folder, BudgetBytes))
            {
                IsBackground = true,
                Name = "refii-composite-sweep",
                Priority = ThreadPriority.Lowest,
            };
            sweep.Start();
            return folder;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Points the cache at a scratch folder, or null to make the platform decide again.</summary>
    internal static void UseFolderForTests(string? folder)
    {
        lock (Gate)
        {
            _resolved = folder is not null;
            _directory = folder;
        }
    }

    /// <summary>Sources read and hashed to name a composite, run total: the ones not known by where they lie.</summary>
    public static long SourcesHashed => Interlocked.Read(ref _sourcesHashed);

    private static long _sourcesHashed;

    /// <summary>The file name for a composite, or null when nothing is kept on this platform.</summary>
    /// <param name="compositeKey">The composite's in-memory key: original, slot, layers and overlays.</param>
    /// <param name="original">The texture a composite with no layers is built on.</param>
    /// <param name="layers">The layers it names.</param>
    /// <param name="overlays">The overlays it names, each with a bump.</param>
    public static string? Name(
        string compositeKey, uint original, HostBridge.GuestTextureLayer[] layers, HostBridge.GuestTextureLayer[] overlays)
    {
        if (Folder is null)
        {
            return null;
        }

        using Engine_Hash.Incremental sha = Engine_Hash.CreateIncrementalSha256();
        sha.AppendData(Encoding.UTF8.GetBytes(compositeKey));
        sha.AppendData(Encoding.UTF8.GetBytes(Engine_TextureUpload.Policy.ToString()));
        AppendSource(sha, original);
        foreach (HostBridge.GuestTextureLayer layer in layers)
        {
            AppendSource(sha, layer.Texture);
        }

        foreach (HostBridge.GuestTextureLayer overlay in overlays)
        {
            AppendSource(sha, overlay.Texture);
            AppendSource(sha, overlay.Bump);
        }

        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() + ".bin";
    }

    /// <summary>
    /// What a texture's bytes are, as of now. A shipped bank entry is named by where it lies in banks
    /// unchanged since they were stated (<see cref="AndroidTexSourceIdentity"/>), with no read; anything
    /// else, a mod's file above all, is read and hashed each time, as a remount can change it under
    /// the same name. Nothing is remembered between calls, so nothing can go stale.
    /// </summary>
    private static void AppendSource(Engine_Hash.Incremental sha, uint texture)
    {
        string? path = Engine_TexPathIndex.Resolve(texture);
        if (path is null)
        {
            sha.AppendData([0]);
            return;
        }

        if (AndroidTexSourceKeys.Lookup(path, MountRead, out BnkFileSystem? mount, out BnkFile? file) &&
            mount is not null && file is not null &&
            AndroidTexSourceIdentity.TryCompute(file, out AndroidTexSourceId id) &&
            AndroidTexSourceKeys.GenerationOf(mount) is { } generation)
        {
            sha.AppendData([1]);
            sha.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<AndroidTexSourceId>(in id)));
            sha.AppendData(MemoryMarshal.AsBytes(new ReadOnlySpan<AndroidTexSourceId>(in generation)));
            return;
        }

        Interlocked.Increment(ref _sourcesHashed);
        byte[]? bytes = MountRead(path);
        sha.AppendData([bytes is null ? (byte)2 : (byte)3]);
        if (bytes is not null)
        {
            sha.AppendData(Engine_Hash.Sha256(bytes));
        }
    }

    /// <summary>The stored composite as a new texture, or null if there is none to use.</summary>
    public static ImageTexture? TryLoad(string name)
    {
        string? folder = Folder;
        if (folder is null)
        {
            return null;
        }

        string path = Path.Combine(folder, name);
        if (!Engine_PreparedImageFile.TryReadPooled(path, out int width, out int height, out int format,
                out bool mipmaps, out byte[] payload, out int length))
        {
            return null;
        }

        try
        {
            using var image = new Image();
            image.SetData(width, height, mipmaps, (Image.Format)format, new ReadOnlySpan<byte>(payload, 0, length));
            if (image.IsEmpty())
            {
                return null;
            }

            // The sweep goes by this: what is used stays.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception) { }
            return Engine_TextureUpload.CreateTexture(image);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(payload);
        }
    }

    /// <summary>As <see cref="Engine_TextureUpload.CreateTexture"/> for a world material, keeping what it uploads under <paramref name="name"/>.</summary>
    /// <param name="image">The finished composite, left unchanged.</param>
    /// <param name="name">From <see cref="Name"/>.</param>
    public static ImageTexture UploadAndStore(Image image, string name)
    {
        Image prepared = AndroidTextureUpload.PrepareAhead(image, world: true);
        try
        {
            if (prepared.IsEmpty() || Folder is not { } folder)
            {
                return Engine_TextureUpload.CreateTexture(image, world: true);
            }

            using (var bytes = new Engine_ImageBytes(prepared))
            {
                Engine_PreparedImageFile.TryWrite(Path.Combine(folder, name), prepared.GetWidth(), prepared.GetHeight(),
                    (int)prepared.GetFormat(), prepared.HasMipmaps(), bytes.Span);
            }

            return Engine_TextureUpload.CreateTexture(prepared);
        }
        finally
        {
            if (!ReferenceEquals(prepared, image))
            {
                prepared.Dispose();
            }
        }
    }

    /// <summary>Deletes other builds' folders and the longest unused files past the budget.</summary>
    internal static void Sweep(string parent, string current, long budget)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return;
            }

            string keep = Path.GetFullPath(current);
            foreach (string directory in Directory.EnumerateDirectories(parent))
            {
                if (!string.Equals(Path.GetFullPath(directory), keep, StringComparison.Ordinal))
                {
                    try { Directory.Delete(directory, recursive: true); } catch (Exception) { }
                }
            }

            if (!Directory.Exists(current))
            {
                return;
            }

            var files = new List<FileInfo>(new DirectoryInfo(current).EnumerateFiles());
            long total = 0;
            foreach (FileInfo file in files)
            {
                total += file.Length;
            }

            if (total <= budget)
            {
                return;
            }

            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            foreach (FileInfo file in files)
            {
                if (total <= budget)
                {
                    return;
                }

                long length = file.Length;
                try
                {
                    file.Delete();
                    total -= length;
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
