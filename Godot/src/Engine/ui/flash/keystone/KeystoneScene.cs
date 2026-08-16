using System;
using System.Collections.Generic;

using Godot;

using Refii.FlashGames;

namespace Refii.Ui.Flash.Keystone;

public sealed class KeystoneScene
{
    public const string GameName = "Keystone";

    public const string ScenePath = "res://levels/pubgames/Keystone.tscn";

    private readonly Dictionary<Node2D, KeystoneSceneClip> _clips = new();
    private readonly Dictionary<int, Node2D> _templatesById = new();
    private readonly Dictionary<string, Node2D> _templatesByName = new(StringComparer.Ordinal);
    private readonly HashSet<KeystoneSceneClip> _ticking = new();
    private readonly List<KeystoneSceneClip> _tickScratch = new();

    private KeystoneScene(Node2D node)
    {
        Node = node;
        StageSize = new Vector2(
            node.HasMeta(Engine_SwfScene.StageWidthMeta)
                ? (float)node.GetMeta(Engine_SwfScene.StageWidthMeta)
                : 640f,
            node.HasMeta(Engine_SwfScene.StageHeightMeta)
                ? (float)node.GetMeta(Engine_SwfScene.StageHeightMeta)
                : 480f);
        FrameRate = node.HasMeta(Engine_SwfScene.FrameRateMeta)
            ? (float)node.GetMeta(Engine_SwfScene.FrameRateMeta)
            : 24f;

        Node2D? library = node.GetNodeOrNull<Node2D>(Engine_SwfScene.LibraryName);
        if (library is not null)
        {
            foreach (Node child in library.GetChildren())
            {
                if (child is not Node2D template)
                {
                    continue;
                }

                _templatesByName[template.Name] = template;
                if (template.HasMeta(Engine_SwfScene.CharacterMeta))
                {
                    _templatesById.TryAdd((int)template.GetMeta(Engine_SwfScene.CharacterMeta), template);
                }
            }
        }

        Root = Wrap(node, null, node.Name, 0);
        WakeTimelines(node, library);
    }

    public Node2D Node { get; }

    public KeystoneSceneClip Root { get; }

    public Vector2 StageSize { get; }

    public float FrameRate { get; }

    public static KeystoneScene? Load()
    {
        string key = Engine_SwfAssets.SceneKey(GameName);
        if (key.Length == 0)
        {
            GD.PushWarning($"[pubgame] {GameName} is not in the mount");
            return null;
        }

        Node2D? node = LoadProjectScene(key);
        if (node is null)
        {
            PackedScene? built = Engine_SwfAssets.BuildScene(GameName);
            if (built is null)
            {
                GD.PushWarning($"[pubgame] {GameName}: the scene did not build");
                return null;
            }

            WriteProjectScene(built);
            node = built.Instantiate<Node2D>();
        }

        Engine_SwfAssets.SaveCaches(GameName);
        return new KeystoneScene(node);
    }

    private static Node2D? LoadProjectScene(string key)
    {
        if (!Godot.FileAccess.FileExists(ScenePath))
        {
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var packed = ResourceLoader.Load<PackedScene>(ScenePath, "PackedScene", ResourceLoader.CacheMode.Replace);
        if (packed is null)
        {
            GD.PushWarning($"[pubgame] {ScenePath} did not load; rebuilding from the mount");
            return null;
        }

        double loaded = clock.Elapsed.TotalMilliseconds;
        var node = packed.Instantiate<Node2D>();
        GD.Print($"[pubgame] {ScenePath}: read {loaded:0} ms, instantiate {clock.Elapsed.TotalMilliseconds - loaded:0} ms");
        string built = node.HasMeta(Engine_SwfScene.BuildMeta)
            ? node.GetMeta(Engine_SwfScene.BuildMeta).AsString()
            : string.Empty;
        if (built == key)
        {
            return node;
        }

        GD.Print($"[pubgame] {ScenePath} was built from '{built}', the mount holds '{key}'; rebuilding");
        node.Free();
        return null;
    }

    private static void WriteProjectScene(PackedScene scene)
    {
        if (!OS.HasFeature("editor"))
        {
            return;
        }

        try
        {
            string absolute = ProjectSettings.GlobalizePath(ScenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
            Error saved = ResourceSaver.Save(scene, ScenePath);
            if (saved == Error.Ok)
            {
                GD.Print($"[pubgame] wrote {ScenePath}");
            }
            else
            {
                GD.PushWarning($"[pubgame] {ScenePath}: not written ({saved})");
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[pubgame] {ScenePath}: {exception.Message}");
        }
    }

    public void Advance(double deltaSeconds)
    {
        if (_ticking.Count == 0)
        {
            return;
        }

        _tickScratch.Clear();
        _tickScratch.AddRange(_ticking);
        foreach (KeystoneSceneClip clip in _tickScratch)
        {
            clip.Tick(deltaSeconds);
        }
    }

    internal Node2D? Template(string exportName) =>
        _templatesByName.GetValueOrDefault(Engine_SwfScene.SafeName(exportName));

    internal Node2D? Template(int characterId) => _templatesById.GetValueOrDefault(characterId);

    internal KeystoneSceneClip Wrap(Node2D node, KeystoneSceneClip? parent, string name, int depth)
    {
        if (_clips.TryGetValue(node, out KeystoneSceneClip? existing))
        {
            return existing;
        }

        var clip = new KeystoneSceneClip(this, node, parent, name, depth);
        _clips[node] = clip;
        return clip;
    }

    internal KeystoneSceneClip? Existing(Node2D node) => _clips.GetValueOrDefault(node);

    internal void Forget(Node2D node)
    {
        if (_clips.Remove(node, out KeystoneSceneClip? clip))
        {
            _ticking.Remove(clip);
        }
    }

    internal void SetTicking(KeystoneSceneClip clip, bool ticking)
    {
        if (ticking)
        {
            _ticking.Add(clip);
        }
        else
        {
            _ticking.Remove(clip);
        }
    }

    internal void WakeTimelines(Node node, Node? skip)
    {
        if (node == skip)
        {
            return;
        }

        if (node is Node2D node2D && node2D.HasMeta(Engine_SwfScene.TimelineMeta))
        {
            if (!_clips.ContainsKey(node2D))
            {
                var name = node2D.Name.ToString();
                int depth = node2D.HasMeta(Engine_SwfScene.DepthMeta)
                    ? (int)node2D.GetMeta(Engine_SwfScene.DepthMeta)
                    : 0;
                Wrap(node2D, null, name, depth);
            }
        }

        foreach (Node child in node.GetChildren())
        {
            WakeTimelines(child, skip);
        }
    }
}
