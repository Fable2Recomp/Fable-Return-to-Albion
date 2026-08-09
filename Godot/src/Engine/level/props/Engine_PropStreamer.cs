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

    private readonly List<MultiMeshInstance3D> _pending = new();

    private readonly Stopwatch _watch = new();

    private int _cursor;

    private int _loaded;

    private int _failed;

    private bool _reported;

    [Export] public double MillisecondsPerFrame { get; set; } = 8.0;

    [Export] public bool Enabled { get; set; } = true;

    public static void ClearCache() => Cache.Clear();

    public override void _Ready()
    {
        _pending.Clear();
        _cursor = 0;
        _loaded = 0;
        _failed = 0;
        _reported = false;

        foreach (Node child in GetChildren())
        {
            if (child is MultiMeshInstance3D instance &&
                instance.Multimesh is not null &&
                instance.Multimesh.Mesh is null &&
                instance.HasMeta(ModelPathMeta))
            {
                _pending.Add(instance);
            }
        }

        _pending.Sort((a, b) =>
            b.Multimesh.InstanceCount.CompareTo(a.Multimesh.InstanceCount));

        SetProcess(_pending.Count != 0);
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
            MultiMeshInstance3D instance = _pending[_cursor++];
            string path = instance.GetMeta(ModelPathMeta).AsString();

            ArrayMesh? mesh = Resolve(path);
            if (mesh is null)
            {
                _failed++;
            }
            else if (instance.Multimesh is not null)
            {
                instance.Multimesh.Mesh = mesh;
                _loaded++;
            }

            if (_watch.Elapsed.TotalMilliseconds >= MillisecondsPerFrame)
            {
                return;
            }
        }

        SetProcess(false);

        if (!_reported)
        {
            _reported = true;
            GD.Print($"[props] streamed {_loaded} model(s)" +
                (_failed == 0 ? string.Empty : $", {_failed} failed"));
        }
    }

    public static ArrayMesh? Resolve(string virtualPath)
    {
        if (virtualPath.Length == 0)
        {
            return null;
        }

        if (Cache.TryGetValue(virtualPath, out ArrayMesh? cached))
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
