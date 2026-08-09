using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using Refii.Models;

namespace Refii.Level;

[Tool]
public partial class Engine_PropStreamer : Node3D
{
    public const string ModelPathMeta = "model_path";

    private static readonly Dictionary<string, ArrayMesh> Cache = new();

    private static readonly List<Engine_PropStreamer> Live = new();

    private readonly List<GeometryInstance3D> _pending = new();

    private readonly Stopwatch _watch = new();

    private int _cursor;

    private int _loaded;

    private int _failed;

    private bool _reported;

    [Export] public double MillisecondsPerFrame { get; set; } = 25.0;

    [Export] public bool Enabled { get; set; } = true;

    public int Total => _pending.Count;

    public int Done => _cursor;

    public static event Action<int, int>? ProgressChanged;

    public static void ClearCache() => Cache.Clear();

    public static void PublishProgress()
    {
        TryGetProgress(out int done, out int total);
        ProgressChanged?.Invoke(done, total);
    }

    public static bool TryGetProgress(out int done, out int total)
    {
        done = 0;
        total = 0;

        for (int i = Live.Count - 1; i >= 0; i--)
        {
            Engine_PropStreamer streamer = Live[i];
            if (!GodotObject.IsInstanceValid(streamer))
            {
                Live.RemoveAt(i);
                continue;
            }

            done += streamer.Done;
            total += streamer.Total;
        }

        return total > 0;
    }

    public override void _Ready()
    {
        if (!Live.Contains(this))
        {
            Live.Add(this);
        }

        _pending.Clear();
        _cursor = 0;
        _loaded = 0;
        _failed = 0;
        _reported = false;

        foreach (Node child in GetChildren())
        {
            if (child is MultiMeshInstance3D multi &&
                multi.Multimesh is not null &&
                multi.Multimesh.Mesh is null &&
                multi.HasMeta(ModelPathMeta))
            {
                _pending.Add(multi);
            }
            else if (child is MeshInstance3D single &&
                single.Mesh is null &&
                single.HasMeta(ModelPathMeta))
            {
                _pending.Add(single);
            }
        }

        _pending.Sort((a, b) => InstanceCount(b).CompareTo(InstanceCount(a)));

        SetProcess(_pending.Count != 0);
        PublishProgress();
    }

    public override void _ExitTree()
    {
        Live.Remove(this);
        PublishProgress();
    }

    public override void _Process(double delta)
    {
        if (!Enabled)
        {
            return;
        }

        _watch.Restart();

        while (_cursor < _pending.Count)
        {
            GeometryInstance3D instance = _pending[_cursor++];
            Step(instance);

            if (_watch.Elapsed.TotalMilliseconds >= MillisecondsPerFrame)
            {
                PublishProgress();
                return;
            }
        }

        SetProcess(false);
        PublishProgress();

        if (!_reported)
        {
            _reported = true;
            GD.Print($"[props] streamed {_loaded} model(s)" +
                (_failed == 0 ? string.Empty : $", {_failed} failed"));
        }
    }

    private static int InstanceCount(GeometryInstance3D instance) =>
        instance is MultiMeshInstance3D multi && multi.Multimesh is not null
            ? multi.Multimesh.InstanceCount
            : 1;

    private void Step(GeometryInstance3D instance)
    {
        string path = string.Empty;

        try
        {
            if (!GodotObject.IsInstanceValid(instance))
            {
                return;
            }

            path = instance.GetMeta(ModelPathMeta).AsString();

            ArrayMesh? mesh = Resolve(path);
            if (mesh is null)
            {
                _failed++;
                if (_failed <= 8)
                {
                    GD.Print($"[props] stream failed: {path}");
                }

                return;
            }

            if (instance is MultiMeshInstance3D multi && multi.Multimesh is not null)
            {
                multi.Multimesh.Mesh = mesh;

                Aabb local = mesh.GetAabb();
                float reach = MathF.Max(local.Position.Length(), local.End.Length());
                if (float.IsFinite(reach) && reach > Engine_LevelProps.ModelReach)
                {
                    multi.Multimesh.CustomAabb = multi.Multimesh.CustomAabb.Grow(reach);
                }
            }
            else if (instance is MeshInstance3D single)
            {
                single.Mesh = mesh;
            }

            _loaded++;
        }
        catch (Exception exception)
        {
            _failed++;
            if (_failed <= 8)
            {
                GD.Print($"[props] stream error on '{path}': {exception.Message}");
            }
        }
    }

    public static ArrayMesh? Resolve(string virtualPath)
    {
        if (virtualPath.Length == 0)
        {
            return null;
        }

        if (Cache.TryGetValue(virtualPath, out ArrayMesh? cached) &&
            GodotObject.IsInstanceValid(cached))
        {
            return cached;
        }

        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(virtualPath);
        if (bytes is null)
        {
            return null;
        }

        ArrayMesh? mesh = Engine_MdlMesh.TryCreateMesh(
            bytes, virtualPath, out _, out _);

        if (mesh is not null)
        {
            mesh.ResourceName = Engine_LevelProps.LeafOf(virtualPath);
            Cache[virtualPath] = mesh;
        }

        return mesh;
    }
}
