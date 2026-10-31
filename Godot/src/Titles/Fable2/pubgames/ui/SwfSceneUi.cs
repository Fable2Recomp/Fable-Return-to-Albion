using System;
using System.Collections.Generic;

using Godot;

using Refii.Titles.Fable2.PubGames;

namespace Refii.Titles.Fable2.PubGames;

/// <summary>
/// A pub game's Flash stage as a Godot scene, presented to the game as a MovieClip tree.
/// </summary>
/// <remarks>
/// GAME-AGNOSTIC ON PURPOSE, and it is the scene that differs rather than the code. Every clip a
/// game addresses is a node in <c>res://levels/pubgames/&lt;game&gt;.tscn</c>, baked from that
/// game's own <c>game.swf</c> by <see cref="Engine_SwfAssets.BuildScene"/> — so Keystone's arch
/// and Mage Dice's reels are two entirely different trees, and everything here is the same
/// lookup either way. <see cref="SwfSceneClip"/> answers <c>GetChild("reel3")</c> exactly as it
/// answers <c>GetChild("stone7")</c>.
///
/// This was <c>KeystoneScene</c>, whose only game-specific content was two constants naming the
/// game and its scene path.
/// </remarks>
public sealed class SwfSceneUi
{
    /// <summary>Where built stages are kept. Gitignored — they are outputs, not source.</summary>
    public const string ScenesFolder = "res://levels/pubgames";

    /// <summary>
    /// The scene a game's stage is written to.
    /// </summary>
    /// <remarks>
    /// KEYED BY THE MOUNT'S SPELLING, not a display name. <see cref="Engine_SwfAssets"/> derives
    /// its folder from the game-data directory by uppercasing the first character and nothing
    /// else, so <c>keystone</c> is <c>Keystone</c> and <c>magedice</c> is <c>Magedice</c>. Taking
    /// the same string for both means there is never a second table mapping one to the other.
    /// </remarks>
    public static string ScenePathFor(string game) => $"{ScenesFolder}/{game}.tscn";

    private readonly Dictionary<Node2D, SwfSceneClip> _clips = new();
    private readonly Dictionary<int, Node2D> _templatesById = new();
    private readonly Dictionary<string, Node2D> _templatesByName = new(StringComparer.Ordinal);

    /// <summary>
    /// A sprite's timeline, stop frames and labels, one per character, shared by every clip of it.
    /// </summary>
    /// <remarks>
    /// They were metadata on the library's templates, and <c>Node.duplicate</c> deep-copies
    /// metadata: every clip attached from a template (tens of thousands in a Keystone session) took
    /// its own copy of the whole timeline, held past the board by the clip's C# array. On the S10e
    /// that was most of the ~280 MB a board left behind.
    /// </remarks>
    private readonly Dictionary<int, SharedTimeline> _timelines = new();

    internal sealed record SharedTimeline(
        Godot.Collections.Array? Table, int[] Stops, Godot.Collections.Dictionary? Labels);
    private readonly HashSet<SwfSceneClip> _ticking = new();
    private readonly List<SwfSceneClip> _tickScratch = new();

    private SwfSceneUi(string game, Node2D node)
    {
        GameName = game;
        ScenePath = ScenePathFor(game);
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

        if (library is not null)
        {
            LiftTimelines(library);
        }

        Root = Wrap(node, null, node.Name, 0);
        WakeTimelines(node, library);
    }

    public Node2D Node { get; }

    public SwfSceneClip Root { get; }

    public Vector2 StageSize { get; }

    public float FrameRate { get; }

    /// <summary>The mount key this stage was built from — "Keystone", "Magedice".</summary>
    public string GameName { get; }

    /// <summary>Where this game's built stage lives.</summary>
    public string ScenePath { get; }

    /// <summary>
    /// The game's stage, read from the project or baked from its SWF.
    /// </summary>
    /// <param name="game">
    /// The mount key — see <see cref="ScenePathFor"/>. Must be the spelling
    /// <see cref="Engine_SwfAssets"/> indexed the game under, or the mount will not have it.
    /// </param>
    public static SwfSceneUi? Load(string game)
    {
        string key = Engine_SwfAssets.SceneKey(game);
        if (key.Length == 0)
        {
            GD.PushWarning($"[pubgame] {game} is not in the mount");
            return null;
        }

        string path = ScenePathFor(game);
        Node2D? node = LoadProjectScene(path, key);
        if (node is null)
        {
            PackedScene? built = Engine_SwfAssets.BuildScene(game);
            if (built is null)
            {
                GD.PushWarning($"[pubgame] {game}: the scene did not build");
                return null;
            }

            WriteProjectScene(built, path);
            node = built.Instantiate<Node2D>();

            // The packed scene is a second copy of the whole stage — every node's properties,
            // the timelines, the meshes and fonts they point at — and nothing reads it again once
            // it is instantiated. Left to the finalizer, it outlived the board.
            built.Dispose();
        }

        Engine_SwfAssets.SaveCaches(game);
        return new SwfSceneUi(game, node);
    }

    private static Node2D? LoadProjectScene(string scenePath, string key)
    {
        if (!Godot.FileAccess.FileExists(scenePath))
        {
            return null;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var packed = ResourceLoader.Load<PackedScene>(scenePath, "PackedScene", ResourceLoader.CacheMode.Replace);
        if (packed is null)
        {
            GD.PushWarning($"[pubgame] {scenePath} did not load; rebuilding from the mount");
            return null;
        }

        double loaded = clock.Elapsed.TotalMilliseconds;
        var node = packed.Instantiate<Node2D>();
        packed.Dispose();
        GD.Print($"[pubgame] {scenePath}: read {loaded:0} ms, instantiate {clock.Elapsed.TotalMilliseconds - loaded:0} ms");
        string built = node.HasMeta(Engine_SwfScene.BuildMeta)
            ? node.GetMeta(Engine_SwfScene.BuildMeta).AsString()
            : string.Empty;
        if (built == key)
        {
            return node;
        }

        GD.Print($"[pubgame] {scenePath} was built from '{built}', the mount holds '{key}'; rebuilding");
        node.Free();
        return null;
    }

    private static void WriteProjectScene(PackedScene scene, string scenePath)
    {
        if (!OS.HasFeature("editor"))
        {
            return;
        }

        try
        {
            string absolute = ProjectSettings.GlobalizePath(scenePath);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute)!);
            Error saved = ResourceSaver.Save(scene, scenePath);
            if (saved == Error.Ok)
            {
                GD.Print($"[pubgame] wrote {scenePath}");
            }
            else
            {
                GD.PushWarning($"[pubgame] {scenePath}: not written ({saved})");
            }
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[pubgame] {scenePath}: {exception.Message}");
        }
    }

    /// <summary>True once <see cref="Release"/> has run; the clips are not to be touched after.</summary>
    internal bool Released { get; private set; }

    /// <summary>
    /// A host's <c>_Notification</c>: <see cref="Release"/> as the board is freed, when
    /// <c>pubgame_release_on_close</c> is on.
    /// </summary>
    public void ReleaseOnPredelete(int what)
    {
        if (what == GodotObject.NotificationPredelete && Refii.Textures.Engine_TexSettings.PubGameReleaseOnClose)
        {
            Release();
        }
    }

    /// <summary>
    /// Lets go of every timeline this stage holds, for a board that is being freed.
    /// </summary>
    /// <remarks>
    /// The timelines live in C# wrappers (the shared ones here, a stage clip's own in the clip),
    /// and a wrapper keeps its native array until the finalizer runs, which on the phone it often
    /// had not by the next board. Disposing them frees the timelines with the board. Behind
    /// <c>pubgame_release_on_close</c>: a clip read after this would read a disposed array.
    /// </remarks>
    public void Release()
    {
        if (Released)
        {
            return;
        }

        Released = true;
        _ticking.Clear();
        _tickScratch.Clear();
        foreach (SwfSceneClip clip in _clips.Values)
        {
            clip.ReleaseTimeline();
        }

        foreach (SharedTimeline timeline in _timelines.Values)
        {
            timeline.Table?.Dispose();
            timeline.Labels?.Dispose();
        }

        _clips.Clear();
        _timelines.Clear();
        _templatesById.Clear();
        _templatesByName.Clear();
    }

    public void Advance(double deltaSeconds)
    {
        if (Released || _ticking.Count == 0)
        {
            return;
        }

        _tickScratch.Clear();
        _tickScratch.AddRange(_ticking);
        foreach (SwfSceneClip clip in _tickScratch)
        {
            clip.Tick(deltaSeconds);
        }
    }

    /// <summary>Moves every library sprite's timeline off its node into <see cref="_timelines"/>.</summary>
    private void LiftTimelines(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            LiftTimelines(child);
        }

        if (!node.HasMeta(Engine_SwfScene.CharacterMeta) || !node.HasMeta(Engine_SwfScene.TimelineMeta))
        {
            return;
        }

        int characterId = (int)node.GetMeta(Engine_SwfScene.CharacterMeta);
        if (!_timelines.ContainsKey(characterId))
        {
            _timelines[characterId] = new SharedTimeline(
                node.GetMeta(Engine_SwfScene.TimelineMeta).AsGodotArray(),
                node.HasMeta(Engine_SwfScene.StopFramesMeta)
                    ? node.GetMeta(Engine_SwfScene.StopFramesMeta).AsInt32Array()
                    : System.Array.Empty<int>(),
                node.HasMeta(Engine_SwfScene.LabelsMeta)
                    ? node.GetMeta(Engine_SwfScene.LabelsMeta).AsGodotDictionary()
                    : null);
        }

        node.RemoveMeta(Engine_SwfScene.TimelineMeta);
        node.RemoveMeta(Engine_SwfScene.StopFramesMeta);
        node.RemoveMeta(Engine_SwfScene.LabelsMeta);
    }

    /// <summary>The shared timeline of a library sprite, or null for a character without one.</summary>
    internal SharedTimeline? TimelineOf(int characterId) =>
        characterId >= 0 ? _timelines.GetValueOrDefault(characterId) : null;

    internal Node2D? Template(string exportName) =>
        _templatesByName.GetValueOrDefault(Engine_SwfScene.SafeName(exportName));

    internal Node2D? Template(int characterId) => _templatesById.GetValueOrDefault(characterId);

    internal SwfSceneClip Wrap(Node2D node, SwfSceneClip? parent, string name, int depth)
    {
        if (_clips.TryGetValue(node, out SwfSceneClip? existing))
        {
            return existing;
        }

        var clip = new SwfSceneClip(this, node, parent, name, depth);
        _clips[node] = clip;
        return clip;
    }

    internal SwfSceneClip? Existing(Node2D node) => _clips.GetValueOrDefault(node);

    internal void Forget(Node2D node)
    {
        if (_clips.Remove(node, out SwfSceneClip? clip))
        {
            _ticking.Remove(clip);
        }
    }

    internal void SetTicking(SwfSceneClip clip, bool ticking)
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
