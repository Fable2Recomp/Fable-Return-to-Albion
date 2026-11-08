using System;
using System.Collections.Generic;

using Godot;

namespace Refii.Titles.Fable2.PubGames;

/// <summary>One entry of a sprite's frame: what stands at a depth, and where and how it is drawn.</summary>
/// <param name="Name">The instance name the SWF gives it, or null for none.</param>
/// <param name="Matrix">The placement's transform, or null to leave the clip's as it is.</param>
/// <param name="Tint">The placement's modulate, or null to leave the clip's as it is.</param>
internal readonly record struct SwfPlacement(
    int Depth, int Character, string? Name, Transform2D? Matrix, Color? Tint);

/// <summary>
/// A sprite's timeline as managed arrays: each frame's placements, read once from the table
/// <see cref="Engine_SwfScene"/> bakes into the scene's metadata.
/// </summary>
/// <remarks>
/// The table was read through Godot wrappers on every frame of every playing clip — an Array per
/// frame, a Dictionary per placement, a packed array per matrix and tint — and each was a new
/// tracked, finalizable object. On the S10e that was the whole main thread on Spinnerbox and
/// Fortune's Tower (3–5 fps, a ~165 ms gen1 collection every second).
/// </remarks>
internal sealed class SwfFrameTable
{
    private readonly SwfPlacement[][] _frames;

    /// <summary>Each frame's depths, sorted, for asking whether a frame places anything at one.</summary>
    private readonly int[][] _depths;

    private SwfFrameTable(SwfPlacement[][] frames)
    {
        _frames = frames;
        _depths = new int[frames.Length][];
        for (var f = 0; f < frames.Length; f++)
        {
            var depths = new int[frames[f].Length];
            for (var p = 0; p < depths.Length; p++)
            {
                depths[p] = frames[f][p].Depth;
            }

            Array.Sort(depths);
            _depths[f] = depths;
        }
    }

    public int Count => _frames.Length;

    /// <summary>Frame <paramref name="frame"/>'s placements, in the table's order (1-based).</summary>
    public ReadOnlySpan<SwfPlacement> Placements(int frame) => _frames[frame - 1];

    /// <summary>True when frame <paramref name="frame"/> places something at <paramref name="depth"/> (1-based).</summary>
    public bool Places(int frame, int depth) => Array.BinarySearch(_depths[frame - 1], depth) >= 0;

    /// <summary>
    /// The table as <see cref="Engine_SwfScene"/> writes it: an Array of frames, each an Array of
    /// placement Dictionaries (depth, character, and an optional name, matrix and tint).
    /// </summary>
    public static SwfFrameTable From(Godot.Collections.Array table)
    {
        var frames = new SwfPlacement[table.Count][];
        for (var f = 0; f < frames.Length; f++)
        {
            using Godot.Collections.Array placements = table[f].AsGodotArray();
            var frame = new SwfPlacement[placements.Count];
            for (var p = 0; p < frame.Length; p++)
            {
                using Godot.Collections.Dictionary place = placements[p].AsGodotDictionary();
                frame[p] = Placement(place);
            }

            frames[f] = frame;
        }

        return new SwfFrameTable(frames);
    }

    /// <summary>A table of the given frames, for tests.</summary>
    internal static SwfFrameTable Of(params SwfPlacement[][] frames) => new(frames);

    private static SwfPlacement Placement(Godot.Collections.Dictionary place)
    {
        string? name = place.TryGetValue(Engine_SwfScene.PlaceName, out Variant n) ? n.AsString() : null;

        Transform2D? matrix = null;
        if (place.TryGetValue(Engine_SwfScene.PlaceMatrix, out Variant mv))
        {
            float[] m = mv.AsFloat32Array();
            if (m.Length >= 6)
            {
                matrix = new Transform2D(
                    new Vector2(m[0], m[1]), new Vector2(m[2], m[3]), new Vector2(m[4], m[5]));
            }
        }

        Color? tint = null;
        if (place.TryGetValue(Engine_SwfScene.PlaceTint, out Variant tv))
        {
            float[] t = tv.AsFloat32Array();
            if (t.Length >= 4)
            {
                tint = new Color(t[0], t[1], t[2], t[3]);
            }
        }

        return new SwfPlacement(
            place[Engine_SwfScene.PlaceDepth].AsInt32(),
            place[Engine_SwfScene.PlaceCharacter].AsInt32(),
            name,
            matrix,
            tint);
    }

    /// <summary>A timeline's labels, each the frame it names, read once from the scene's Dictionary.</summary>
    public static Dictionary<string, int> LabelsFrom(Godot.Collections.Dictionary labels)
    {
        var frames = new Dictionary<string, int>(labels.Count, StringComparer.Ordinal);
        foreach ((Variant label, Variant frame) in labels)
        {
            frames[label.AsString()] = frame.AsInt32();
        }

        return frames;
    }
}

/// <summary>
/// The scene's metadata names as StringNames, made once.
/// </summary>
/// <remarks>
/// <c>HasMeta</c> and <c>GetMeta</c> take a StringName, and a string converts to a new one on every
/// call — a tracked, finalizable wrapper, as many per frame as there are clips asking.
/// </remarks>
internal static class SwfSceneMeta
{
    public static readonly StringName Character = Engine_SwfScene.CharacterMeta;
    public static readonly StringName Depth = Engine_SwfScene.DepthMeta;
    public static readonly StringName Timeline = Engine_SwfScene.TimelineMeta;
    public static readonly StringName FrameCount = Engine_SwfScene.FrameCountMeta;
    public static readonly StringName Stops = Engine_SwfScene.StopFramesMeta;
    public static readonly StringName Labels = Engine_SwfScene.LabelsMeta;
}
