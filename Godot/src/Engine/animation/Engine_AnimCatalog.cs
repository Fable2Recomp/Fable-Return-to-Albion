using System.Collections.Generic;
using Godot;
using Refii.Assets;
using Refii.Models;

namespace Refii.Animations;

public static class Engine_AnimCatalog
{
    private static readonly Engine_AnimDataFile Data = new();

    private static readonly Engine_AnimDecoder Decoder = new(Data);

    private static readonly List<AnimClip> Clips = new();

    private static readonly Dictionary<uint, AnimClip> ByKey = new();

    private static readonly Dictionary<uint, int> IndexByKey = new();

    private static readonly List<ModelAnimationBinding> Bindings = new();

    private static readonly Dictionary<uint, List<ModelAnimationBinding>> BindingsByModel = new();

    private static bool _tried;

    private static bool _ready;

    private static volatile bool _boundTried;

    private static System.Threading.Tasks.Task? _bindingTask;

    private static System.Func<IEnumerable<string>>? _fileSource;

    public static void SetFileSource(System.Func<IEnumerable<string>>? source)
    {
        if (_fileSource == source)
        {
            return;
        }

        Reset();
        _fileSource = source;
    }

    private static byte[]? ReadAnimFile(string exactPath, string extension)
    {
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(exactPath);
        if (bytes is not null)
        {
            return bytes;
        }

        foreach (string path in AllFilePaths())
        {
            if (!path.EndsWith(extension, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bytes = Engine_BnkMeshLoader.ReadBytes(path);
            if (bytes is not null)
            {
                GD.Print($"[anim] resolved {extension} as {path}");
                return bytes;
            }
        }

        return null;
    }

    private static IEnumerable<string> AllFilePaths()
    {
        if (_fileSource is not null)
        {
            foreach (string path in _fileSource())
            {
                yield return path;
            }

            yield break;
        }

        Engine_GameAssets? assets = Engine_GameAssets.Instance;
        if (assets is null)
        {
            yield break;
        }

        foreach (Bnk.BnkFile file in assets.EnumerateAllFiles())
        {
            yield return file.VirtualPath;
        }
    }

    public static IReadOnlyList<AnimClip> AllClips => Clips;

    /// <summary>The clips catalogued and the animation data file held whole, for the heap census.</summary>
    internal static (long Clips, long Bytes) DataCensus() => (Clips.Count, Data.Size);

    public static Engine_AnimDecoder ActiveDecoder => Decoder;

    public static bool IsReady => _ready;

    private static readonly object Gate = new();

    public static void Reset()
    {
        lock (Gate)
        {
            _tried = false;
            _ready = false;
            _boundTried = false;
            _bindingTask = null;
            Clips.Clear();
            ByKey.Clear();
            IndexByKey.Clear();
            Bindings.Clear();
            BindingsByModel.Clear();
            Data.Close();
            Decoder.ClearCache();
        }
    }

    public static void Prewarm()
    {
        if (_boundTried)
        {
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                EnsureBindings();
            }
            catch (System.Exception exception)
            {
                GD.PushWarning($"[anim] prewarm failed: {exception.Message}");
            }
        });
    }

    public static bool EnsureLoaded()
    {
        lock (Gate)
        {
            return EnsureLoadedLocked();
        }
    }

    private static bool EnsureLoadedLocked()
    {
        if (_tried)
        {
            return _ready;
        }

        byte[]? toc = ReadAnimFile(Engine_AnimBank.TocPath, Engine_AnimBank.TocExtension);
        if (toc is null)
        {
            // No table of contents: the clips are loose AnimFile v8 files (Fable 3's .csa), each
            // carrying its own header and skeleton, keyed by the hash of its path.
            _tried = true;
            return LoadCsaClipsLocked();
        }

        _tried = true;

        if (!Engine_AnimBank.TryParse(toc, out List<AnimClip> parsed))
        {
            return false;
        }

        byte[]? blob = ReadAnimFile(
            Engine_AnimDataFile.DataPath, Engine_AnimDataFile.DataExtension);

        if (blob is null)
        {
            GD.PushWarning($"[anim] no {Engine_AnimDataFile.DataExtension} in the mounted archives");
            return false;
        }

        if (!Data.Open(blob))
        {
            return false;
        }

        Clips.AddRange(parsed);
        for (int i = 0; i < Clips.Count; i++)
        {
            ByKey[Clips[i].Key0] = Clips[i];
            IndexByKey[Clips[i].Key0] = i;
        }

        _ready = true;
        GD.Print($"[anim] catalog ready with {Clips.Count} clip(s)");
        BeginBindings();
        return true;
    }

    /// <summary>
    /// The catalog from loose AnimFile v8 clips (Fable 3's .csa, Engine_CsaFile): every one is
    /// read, parsed and laid end to end in one blob the decoder addresses by offset, exactly as
    /// Fable 2's animation_data is. The guest keys a clip by the hash of its bank path
    /// (sub_82D3AD68 opens the VFS entry by that hash; sub_8312F3D8 hashes the path), so Key0 is
    /// Engine_GameHash.ResourcePath of the virtual path, the value a clip leaf carries at +48 and
    /// RefiiHostGetEntityAnimState exports. The tracks are the clip's own skeleton, named by hash
    /// as Engine_MdlEngine47 names the model's bones.
    /// </summary>
    /// <remarks>
    /// The same path is shipped twice, in fable2_anims.bnk and a low-memory fable2_lmanims.bnk
    /// variant with fewer tiers and bones (hero spell: 189 bones against 35). The two hash alike,
    /// so of two parses of one path the one with more bones is kept; the guest's tier choice
    /// between them is its memory budget, not a different animation.
    /// </remarks>
    private static bool LoadCsaClipsLocked()
    {
        var chosen = new Dictionary<uint, (string Path, byte[] Bytes, Engine_CsaFile.Clip Clip)>();
        int seen = 0;
        int rejected = 0;

        foreach (string path in AllFilePaths())
        {
            if (!path.EndsWith(Engine_CsaFile.Extension, System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            seen++;
            byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(path);
            string error = "unreadable";
            if (bytes is null || !Engine_CsaFile.TryParse(bytes, out Engine_CsaFile.Clip parsed, out error))
            {
                rejected++;
                if (rejected <= 8)
                {
                    GD.PushWarning($"[anim] {path}: {error}");
                }

                continue;
            }

            uint key = Refii.Core.Engine_GameHash.ResourcePath(path);
            if (chosen.TryGetValue(key, out var existing) && existing.Clip.Bones.Count >= parsed.Bones.Count)
            {
                continue;
            }

            chosen[key] = (path, bytes, parsed);
        }

        if (chosen.Count == 0)
        {
            GD.PushWarning($"[anim] no {Engine_AnimBank.TocExtension} and no {Engine_CsaFile.Extension} in the mounted archives");
            return false;
        }

        long total = 0;
        foreach (var entry in chosen.Values)
        {
            total += entry.Bytes.Length;
        }

        var blob = new byte[total];
        int cursor = 0;
        foreach ((uint key, (string path, byte[] bytes, Engine_CsaFile.Clip parsed)) in chosen)
        {
            System.Buffer.BlockCopy(bytes, 0, blob, cursor, bytes.Length);

            var clip = new AnimClip
            {
                Key0 = key,
                Key1 = parsed.Tiers.Count != 0 ? parsed.Tiers[^1].Hash : 0u,
                DataOffset = (uint)cursor,
                DataSizeBytes = (uint)bytes.Length,
                TocFrameCount = (uint)parsed.FrameCount,
                Fps = parsed.Fps,
                Name = System.IO.Path.GetFileNameWithoutExtension(path.Replace('\\', '/')),
            };

            foreach (Engine_CsaFile.Event csaEvent in parsed.Events)
            {
                clip.Events.Add(new AnimEvent { Time = csaEvent.Time, Name = csaEvent.Name });
            }

            var bones = new List<AnimTrackBone>(parsed.Bones.Count);
            foreach (Engine_CsaFile.Bone bone in parsed.Bones)
            {
                bones.Add(new AnimTrackBone { Name = Engine_CsaFile.BoneName(bone.NameHash), Parent = bone.Parent });
            }

            clip.TrackMap = bones;
            Clips.Add(clip);
            cursor += bytes.Length;
        }

        if (!Data.Open(blob))
        {
            Clips.Clear();
            return false;
        }

        for (int i = 0; i < Clips.Count; i++)
        {
            ByKey[Clips[i].Key0] = Clips[i];
            IndexByKey[Clips[i].Key0] = i;
        }

        // The model-to-clip bindings come from Fable 2's GDB AnimationPairs scan; Fable 3's are
        // not read yet, so the editor's per-model clip list stays empty and the live feed, which
        // names its clips by key, needs none.
        _boundTried = true;
        _ready = true;
        GD.Print($"[anim] catalog ready with {Clips.Count} AnimFile v8 clip(s) from {seen} file(s), " +
            $"{rejected} rejected, {total} bytes");
        return true;
    }

    public static bool BindingsReady => _boundTried;

    private static int _prewarming;

    /// <summary>
    /// Prewarms the animation TOC and GDB model-binding index asynchronously on a background thread
    /// as soon as banks finish mounting, removing ~2.5s of synchronous GDB scanning from the first level build.
    /// </summary>
    public static void BeginPrewarm()
    {
        if (_boundTried || System.Threading.Interlocked.CompareExchange(ref _prewarming, 1, 0) != 0)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (EnsureLoaded())
                {
                    BeginBindings();
                }
            }
            catch (System.Exception)
            {
            }
            finally
            {
                System.Threading.Volatile.Write(ref _prewarming, 0);
            }
        });
    }

    public static void BeginBindings()
    {
        lock (Gate)
        {
            if (!_ready || _boundTried || _bindingTask is not null)
            {
                return;
            }

            var paths = new List<string>(GdbPaths());
            if (paths.Count == 0)
            {
                return;
            }

            _bindingTask = System.Threading.Tasks.Task.Run(() => ScanFiles(paths));
        }
    }

    /// <summary>One file in memory at a time: all of them together are about 30 MB, held for seconds.</summary>
    private static void ScanFiles(List<string> paths)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var seen = new HashSet<(uint Model, int Clip, uint Key)>();
        int scanned = 0;
        int parsed = 0;
        int read = 0;

        foreach (string path in paths)
        {
            // One file's read failing skips that file: a throw here would fault the task, and every
            // later EnsureBindings would rethrow it for the rest of the session.
            byte[]? bytes;
            try
            {
                bytes = Engine_BnkMeshLoader.ReadBytes(path);
            }
            catch (System.Exception exception)
            {
                GD.PushWarning($"[anim] {path}: read threw {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            read++;
            if (!Gdb.GdbView.TryParseShared(bytes, out Gdb.GdbView view, out _))
            {
                continue;
            }

            parsed++;
            scanned += Engine_AnimGdbScan.Scan(view, IndexByKey, Clips, Bindings, seen);
        }

        Engine_AnimGdbScan.ReleaseScratch();

        if (read == 0)
        {
            // Nothing to read yet: as if the scan had not started, so a later call tries again.
            lock (Gate)
            {
                _bindingTask = null;
            }

            return;
        }

        foreach (ModelAnimationBinding binding in Bindings)
        {
            if (!BindingsByModel.TryGetValue(
                    binding.ModelPathHash, out List<ModelAnimationBinding>? list))
            {
                list = new List<ModelAnimationBinding>();
                BindingsByModel[binding.ModelPathHash] = list;
            }

            list.Add(binding);
        }

        _boundTried = true;

        GD.Print($"[anim] scanned {parsed}/{read} gdb file(s) in " +
            $"{timer.ElapsedMilliseconds} ms off-thread");
        GD.Print($"[anim] {Bindings.Count} model binding(s) from {scanned} reference(s)");
    }

    public static bool EnsureBindings()
    {
        if (_boundTried)
        {
            return Bindings.Count != 0;
        }

        if (!EnsureLoaded())
        {
            return false;
        }

        BeginBindings();

        System.Threading.Tasks.Task? scan;
        lock (Gate)
        {
            scan = _bindingTask;
        }

        scan?.Wait();
        return Bindings.Count != 0;
    }

    private static IEnumerable<string> GdbPaths()
    {
        foreach (string path in AllFilePaths())
        {
            if (path.EndsWith(".gdb", System.StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    public static IReadOnlyList<ModelAnimationBinding> AllBindings =>
        _boundTried ? Bindings : System.Array.Empty<ModelAnimationBinding>();

    public static IReadOnlyList<ModelAnimationBinding> BindingsForModelHash(uint modelHash)
    {
        if (!EnsureBindings())
        {
            return System.Array.Empty<ModelAnimationBinding>();
        }

        return BindingsByModel.TryGetValue(modelHash, out List<ModelAnimationBinding>? list)
            ? list
            : System.Array.Empty<ModelAnimationBinding>();
    }

    public static AnimClip? ClipAt(int index) =>
        index >= 0 && index < Clips.Count ? Clips[index] : null;

    public static AnimClip? Find(uint key0)
    {
        if (!EnsureLoaded())
        {
            return null;
        }

        return ByKey.TryGetValue(key0, out AnimClip? clip) ? clip : null;
    }

}
