using System;
using System.Collections.Generic;

using Godot;

using Refii.Titles.Fable2.PubGames;
using Refii.Titles.Fable2.PubGames.Framework;

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
        SwfFrameTable Table, int[] Stops, Dictionary<string, int>? Labels);
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
                if (template.HasMeta(SwfSceneMeta.Character))
                {
                    _templatesById.TryAdd((int)template.GetMeta(SwfSceneMeta.Character), template);
                }
            }
        }

        if (library is not null)
        {
            LiftTimelines(library);
        }

        Root = Wrap(node, null, node.Name, 0);
        WakeTimelines(node, library);
        _prompts = new SwfScenePrompts(PromptFaces(node, library));
        _prompts.Track(node, library);
    }

    /// <summary>A stage already instantiated, for tests.</summary>
    internal static SwfSceneUi FromStage(string game, Node2D node) => new(game, node);

    /// <summary>The stage's button prompts, drawn in the player's glyphs.</summary>
    private readonly SwfScenePrompts _prompts;

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
        if (what != GodotObject.NotificationPredelete)
        {
            return;
        }

        // Kept copies are out of the tree, so the board's freeing does not reach them.
        FreeSpares();
        if (Refii.Textures.Engine_TexSettings.PubGameReleaseOnClose)
        {
            Release();
        }
    }

    /// <summary>
    /// Lets go of every timeline this stage holds, for a board that is being freed.
    /// </summary>
    /// <remarks>
    /// The timelines lived in C# wrappers (the shared ones here, a stage clip's own in the clip),
    /// and a wrapper keeps its native array until the finalizer runs, which on the phone it often
    /// had not by the next board. They are managed tables now (<see cref="SwfFrameTable"/>), and
    /// the native arrays are let go of as they are read; this still stops every clip and drops the
    /// stage's references. Behind <c>pubgame_release_on_close</c>: a clip is not to be read after this.
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

        _clips.Clear();
        _timelines.Clear();
        _templatesById.Clear();
        _templatesByName.Clear();
        _attachWalks.Clear();
        _nodeNames.Clear();
        _prompts.Clear();
        FreeSpares();
    }

    public void Advance(double deltaSeconds)
    {
        if (Released)
        {
            return;
        }

        _prompts.Refresh();
        if (_ticking.Count == 0)
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

        if (!node.HasMeta(SwfSceneMeta.Character) || !node.HasMeta(SwfSceneMeta.Timeline))
        {
            return;
        }

        int characterId = (int)node.GetMeta(SwfSceneMeta.Character);
        if (!_timelines.ContainsKey(characterId))
        {
            _timelines[characterId] = new SharedTimeline(
                TableOf(node)!,
                node.HasMeta(SwfSceneMeta.Stops)
                    ? node.GetMeta(SwfSceneMeta.Stops).AsInt32Array()
                    : System.Array.Empty<int>(),
                LabelsOf(node));
        }

        node.RemoveMeta(SwfSceneMeta.Timeline);
        node.RemoveMeta(SwfSceneMeta.Stops);
        node.RemoveMeta(SwfSceneMeta.Labels);
    }

    /// <summary>A node's own timeline as a managed table, or null for a node without one.</summary>
    internal static SwfFrameTable? TableOf(Node node)
    {
        if (!node.HasMeta(SwfSceneMeta.Timeline))
        {
            return null;
        }

        using Godot.Collections.Array table = node.GetMeta(SwfSceneMeta.Timeline).AsGodotArray();
        return SwfFrameTable.From(table);
    }

    /// <summary>A node's own labels, each the frame it names, or null for a node without them.</summary>
    internal static Dictionary<string, int>? LabelsOf(Node node)
    {
        if (!node.HasMeta(SwfSceneMeta.Labels))
        {
            return null;
        }

        using Godot.Collections.Dictionary labels = node.GetMeta(SwfSceneMeta.Labels).AsGodotDictionary();
        return SwfFrameTable.LabelsFrom(labels);
    }

    /// <summary>
    /// The prompt shapes the stage's `button` symbol files under its labels
    /// (<see cref="PubGamePrompts.Faces"/>): its timeline is a library sprite's, or a stage clip's own.
    /// </summary>
    private Dictionary<int, string> PromptFaces(Node2D stage, Node? library)
    {
        foreach (SharedTimeline timeline in _timelines.Values)
        {
            if (FacesOf(timeline.Table, timeline.Labels) is { Count: > 0 } faces)
            {
                return faces;
            }
        }

        return StageFaces(stage, library) ?? new Dictionary<int, string>();
    }

    private static Dictionary<int, string>? StageFaces(Node node, Node? library)
    {
        if (node == library)
        {
            return null;
        }

        if (node.HasMeta(SwfSceneMeta.Labels) && node.HasMeta(SwfSceneMeta.Timeline))
        {
            if (FacesOf(TableOf(node), LabelsOf(node)) is { Count: > 0 } faces)
            {
                return faces;
            }
        }

        foreach (Node child in node.GetChildren())
        {
            if (StageFaces(child, library) is { } faces)
            {
                return faces;
            }
        }

        return null;
    }

    /// <summary>A sprite's prompt shapes from its timeline, or null for a sprite that is not the prompt symbol.</summary>
    private static Dictionary<int, string>? FacesOf(SwfFrameTable? table, Dictionary<string, int>? labels)
    {
        if (table is null || labels is null || !PubGamePrompts.IsPromptSymbol(labels.ContainsKey))
        {
            return null;
        }

        var frames = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string label in PubGamePrompts.Labels)
        {
            if (labels.TryGetValue(label, out int frame))
            {
                frames[label] = frame;
            }
        }

        return PubGamePrompts.Faces(frames, frame =>
        {
            var characters = new List<int>();
            if (frame < 1 || frame > table.Count)
            {
                return characters;
            }

            foreach (SwfPlacement place in table.Placements(frame))
            {
                characters.Add(place.Character);
            }

            return characters;
        });
    }

    /// <summary>
    /// Wakes the timelines of a clip just attached from <paramref name="template"/>, and takes its
    /// prompt shapes.
    /// </summary>
    /// <remarks>
    /// Each walks the whole copy, and every node a walk visits gets a C# wrapper; a board that swaps
    /// the character at a depth every frame paid both walks for every swap. What either finds
    /// depends only on the template, so each is asked of the template once, and a copy is walked
    /// only for what it holds.
    /// </remarks>
    internal void Attached(Node2D template, Node2D copy)
    {
        if (!_attachWalks.TryGetValue(template, out (bool Timelines, bool Prompts) walks))
        {
            walks = (HoldsTimeline(template), _prompts.Holds(template));
            _attachWalks[template] = walks;
        }

        if (walks.Timelines)
        {
            WakeTimelines(copy, null);
        }

        if (walks.Prompts)
        {
            _prompts.Track(copy, null);
        }
    }

    /// <summary>Bumped by every <see cref="SwfSceneClip.SetTint"/>, which reaches into the descendants it tints.</summary>
    internal int TintGeneration { get; set; }

    /// <summary>
    /// Copies of static templates let go of by a timeline, kept out of the tree for the template's
    /// next placement, by template.
    /// </summary>
    /// <remarks>
    /// A copy is kept only where the next placement cannot tell it from a fresh one: its template's
    /// whole subtree is static (<see cref="Spare"/>), the game was never handed its clip and no clip
    /// was made for anything under it, and no tint has been set since it was copied. Placing it
    /// again puts the template's transform and colour back, the only things a placement writes.
    /// </remarks>
    private readonly Dictionary<Node2D, Stack<Node2D>> _spares = new();

    /// <summary>Whether a template's copies may be kept: nothing in its subtree changes by itself or is a prompt or text.</summary>
    private readonly Dictionary<Node2D, bool> _spareTemplates = new();

    internal bool Spare(Node2D template)
    {
        if (!_spareTemplates.TryGetValue(template, out bool spare))
        {
            spare = !_prompts.Holds(template) && Static(template);
            _spareTemplates[template] = spare;
        }

        return spare;
    }

    /// <summary>No timeline (its own or its character's), no frames to play, no animation and no text anywhere under <paramref name="node"/>.</summary>
    private bool Static(Node node)
    {
        if (node is AnimationPlayer or Label || node.HasMeta(SwfSceneMeta.Timeline) ||
            (node.HasMeta(SwfSceneMeta.FrameCount) && (int)node.GetMeta(SwfSceneMeta.FrameCount) > 1) ||
            (node.HasMeta(SwfSceneMeta.Character) && TimelineOf((int)node.GetMeta(SwfSceneMeta.Character)) is not null))
        {
            return false;
        }

        foreach (Node child in node.GetChildren())
        {
            if (!Static(child))
            {
                return false;
            }
        }

        return true;
    }

    internal Node2D? TakeSpare(Node2D template) =>
        _spares.TryGetValue(template, out Stack<Node2D>? kept) && kept.TryPop(out Node2D? copy) ? copy : null;

    internal void KeepSpare(Node2D template, Node2D copy)
    {
        if (!_spares.TryGetValue(template, out Stack<Node2D>? kept))
        {
            kept = new Stack<Node2D>();
            _spares[template] = kept;
        }

        kept.Push(copy);
    }

    /// <summary>Frees every kept copy: they are out of the tree, so freeing the board does not reach them.</summary>
    internal void FreeSpares()
    {
        foreach (Stack<Node2D> kept in _spares.Values)
        {
            foreach (Node2D copy in kept)
            {
                if (GodotObject.IsInstanceValid(copy))
                {
                    copy.QueueFree();
                }
            }
        }

        _spares.Clear();
    }

    /// <summary>
    /// The node name of a clip attached as <paramref name="instanceName"/>, made once per name: a
    /// string set as a node's name is a new tracked StringName every time.
    /// </summary>
    internal StringName NodeName(string instanceName)
    {
        if (!_nodeNames.TryGetValue(instanceName, out StringName? name))
        {
            name = Engine_SwfScene.SafeName(instanceName);
            _nodeNames[instanceName] = name;
        }

        return name;
    }

    private readonly Dictionary<string, StringName> _nodeNames = new(StringComparer.Ordinal);

    /// <summary>Whether a template's copies hold a timeline still on its nodes, and a prompt shape: what <see cref="Attached"/> walks them for.</summary>
    private readonly Dictionary<Node2D, (bool Timelines, bool Prompts)> _attachWalks = new();

    private static bool HoldsTimeline(Node node)
    {
        if (node is Node2D && node.HasMeta(SwfSceneMeta.Timeline))
        {
            return true;
        }

        foreach (Node child in node.GetChildren())
        {
            if (HoldsTimeline(child))
            {
                return true;
            }
        }

        return false;
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

        if (node is Node2D node2D && node2D.HasMeta(SwfSceneMeta.Timeline))
        {
            if (!_clips.ContainsKey(node2D))
            {
                var name = node2D.Name.ToString();
                int depth = node2D.HasMeta(SwfSceneMeta.Depth)
                    ? (int)node2D.GetMeta(SwfSceneMeta.Depth)
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
