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
            GD.PushWarning($"[anim] no {Engine_AnimBank.TocExtension} in the mounted archives");
            return false;
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

    public static bool BindingsReady => _boundTried;

    public static void BeginBindings()
    {
        lock (Gate)
        {
            if (!_ready || _boundTried || _bindingTask is not null)
            {
                return;
            }

            var blobs = new List<byte[]>();
            foreach (string path in GdbPaths())
            {
                byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(path);
                if (bytes is not null && bytes.Length != 0)
                {
                    blobs.Add(bytes);
                }
            }

            if (blobs.Count == 0)
            {
                return;
            }

            _bindingTask = System.Threading.Tasks.Task.Run(() => ScanBlobs(blobs));
        }
    }

    private static void ScanBlobs(List<byte[]> blobs)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var seen = new HashSet<(uint Model, int Clip, uint Key)>();
        int scanned = 0;
        int parsed = 0;

        foreach (byte[] bytes in blobs)
        {
            if (!Gdb.GdbView.TryParse(bytes, out Gdb.GdbView view, out _))
            {
                continue;
            }

            parsed++;
            scanned += Engine_AnimGdbScan.Scan(view, IndexByKey, Clips, Bindings, seen);
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

        GD.Print($"[anim] scanned {parsed}/{blobs.Count} gdb file(s) in " +
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
