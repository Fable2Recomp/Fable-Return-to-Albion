using System;
using System.Collections.Generic;

using Godot;


namespace Refii.FlashGames;

public static class Engine_SwfScene
{
    public const string TimelineName = "timeline";

    public const string LibraryName = "library";

    public const string TextName = "text";

    public const string FrameCountMeta = "swf_frames";

    public const string StopFramesMeta = "swf_stops";

    public const string LabelsMeta = "swf_labels";

    public const string TimelineMeta = "swf_timeline";

    public const string PlaceDepth = "d";
    public const string PlaceCharacter = "c";
    public const string PlaceName = "n";
    public const string PlaceMatrix = "m";
    public const string PlaceTint = "t";
    public const string PlaceBlend = "b";

    public const string CharacterMeta = "swf_character";

    public const string DepthMeta = "swf_depth";

    public const string StageWidthMeta = "swf_stage_width";

    public const string StageHeightMeta = "swf_stage_height";

    public const string FrameRateMeta = "swf_frame_rate";

    public const string BuildMeta = "swf_build";

    public const int FormatVersion = 2;

    private const float DefaultFrameRate = 24f;

    public static PackedScene? Build(
        SwfDocument document, float frameRate, Vector2 stageSize, string name, string key,
        IEnumerable<(int Id, string Name)> library,
        Func<int, IReadOnlyList<SwfVectorPart>> partsFor,
        Func<int, (Font? Font, SwfFont? Face)> fontFor)
    {
        float rate = frameRate > 0f ? frameRate : DefaultFrameRate;
        var builder = new Builder(document, rate, partsFor, fontFor);

        var root = new Node2D { Name = SafeName(name) };
        root.SetMeta(CharacterMeta, -1);
        root.SetMeta(StageWidthMeta, stageSize.X);
        root.SetMeta(StageHeightMeta, stageSize.Y);
        root.SetMeta(FrameRateMeta, rate);
        root.SetMeta(BuildMeta, key);
        builder.Timeline(root, document.RootFrames);

        var shelf = new Node2D { Name = LibraryName, Visible = false };
        root.AddChild(shelf);
        var shelved = new HashSet<int>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach ((int id, string symbol) in library)
        {
            if (!builder.IsCharacter(id) || !shelved.Add(id))
            {
                continue;
            }

            string safe = SafeName(symbol);
            if (!used.Add(safe))
            {
                safe = $"{safe}_{id}";
                used.Add(safe);
            }

            shelf.AddChild(builder.Character(id, safe));
        }

        var pending = true;
        while (pending)
        {
            pending = false;
            foreach (int id in new List<int>(builder.Referenced))
            {
                if (!builder.IsCharacter(id) || !shelved.Add(id))
                {
                    continue;
                }

                pending = true;
                shelf.AddChild(builder.Character(id, $"c{id}"));
            }
        }

        Own(root, root);

        var scene = new PackedScene();
        Error error = scene.Pack(root);
        root.Free();
        return error == Error.Ok ? scene : null;
    }

    private static void Own(Node node, Node owner)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = owner;
            Own(child, owner);
        }
    }

    public static string SafeName(string name)
    {
        if (name.Length == 0)
        {
            return "node";
        }

        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '.' or '/' or ':' or '@' or '%' or '"')
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private sealed class Builder
    {
        private readonly SwfDocument _document;
        private readonly float _frameRate;
        private readonly Func<int, IReadOnlyList<SwfVectorPart>> _partsFor;
        private readonly Func<int, (Font? Font, SwfFont? Face)> _fontFor;
        private readonly Dictionary<int, (Font? Font, SwfFont? Face)> _fonts = new();

        public Builder(
            SwfDocument document, float frameRate,
            Func<int, IReadOnlyList<SwfVectorPart>> partsFor,
            Func<int, (Font? Font, SwfFont? Face)> fontFor)
        {
            _document = document;
            _frameRate = frameRate;
            _partsFor = partsFor;
            _fontFor = fontFor;
        }

        public HashSet<int> Referenced { get; } = new();

        public bool IsCharacter(int id) =>
            _document.Sprites.ContainsKey(id) || _document.Shapes.ContainsKey(id) ||
            _document.EditTexts.ContainsKey(id);

        public Node2D Character(int characterId, string name)
        {
            var node = new Node2D { Name = SafeName(name) };
            node.SetMeta(CharacterMeta, characterId);

            if (_document.Shapes.TryGetValue(characterId, out SwfShape? shape) &&
                _document.Geometry.TryGetValue(characterId, out SwfGeometry? geometry))
            {
                Shape(node, shape, geometry);
            }
            else if (_document.EditTexts.TryGetValue(characterId, out SwfEditText? text))
            {
                Text(node, text);
            }
            else if (_document.Sprites.TryGetValue(characterId, out SwfSprite? sprite))
            {
                Timeline(node, sprite.Frames);
            }

            return node;
        }

        private sealed class Run
        {
            public required int Depth { get; init; }

            public required int CharacterId { get; init; }

            public required int FirstFrame { get; init; }

            public int LastFrame { get; set; }

            public string Name { get; set; } = string.Empty;

            public readonly List<(int Frame, SwfPlaceObject Place)> States = new();
        }

        public void Timeline(Node2D node, List<SwfFrame> frames)
        {
            List<Run> runs = Runs(frames);
            if (runs.Count == 0)
            {
                return;
            }

            NameRuns(runs);

            runs.Sort((a, b) => a.Depth != b.Depth
                ? a.Depth.CompareTo(b.Depth)
                : a.FirstFrame.CompareTo(b.FirstFrame));

            int last = frames.Count - 1;
            var tween = true;
            foreach (Run run in runs)
            {
                if (run.FirstFrame != 0 || run.LastFrame != last)
                {
                    tween = false;
                    break;
                }
            }

            if (frames.Count > 1 && !tween)
            {
                Swap(node, frames, runs);
                return;
            }

            var nodes = new Dictionary<Run, Node2D>();
            foreach (Run run in runs)
            {
                Node2D child = Character(run.CharacterId, run.Name);
                child.SetMeta(DepthMeta, run.Depth);
                Apply(child, run.States[0].Place);
                node.AddChild(child);
                nodes[run] = child;
            }

            if (frames.Count > 1)
            {
                Animate(node, frames, runs, nodes);
            }
        }

        private void Swap(Node2D node, List<SwfFrame> frames, List<Run> runs)
        {
            foreach (Run run in runs)
            {
                if (run.FirstFrame != 0)
                {
                    continue;
                }

                Node2D child = Character(run.CharacterId, run.Name);
                child.SetMeta(DepthMeta, run.Depth);
                Apply(child, run.States[0].Place);
                node.AddChild(child);
            }

            var table = new Godot.Collections.Array();
            var labels = new Godot.Collections.Dictionary();
            var stops = new List<int>();
            List<SortedDictionary<int, SwfPlaceObject>> states = FrameStates(frames);
            for (var f = 0; f < states.Count; f++)
            {
                var frame = new Godot.Collections.Array();
                foreach ((int depth, SwfPlaceObject place) in states[f])
                {
                    if (place.CharacterId < 0)
                    {
                        continue;
                    }

                    Referenced.Add(place.CharacterId);
                    var entry = new Godot.Collections.Dictionary
                    {
                        [PlaceDepth] = depth,
                        [PlaceCharacter] = place.CharacterId,
                    };

                    if (place.Name.Length != 0)
                    {
                        entry[PlaceName] = place.Name;
                    }

                    if (place.HasMatrix)
                    {
                        SwfMatrix m = place.Matrix;
                        entry[PlaceMatrix] = new[]
                        {
                            m.ScaleX, m.RotateSkew0, m.RotateSkew1, m.ScaleY,
                            m.TranslateX / SwfShapeGeometry.TwipsPerPixel,
                            m.TranslateY / SwfShapeGeometry.TwipsPerPixel,
                        };
                    }

                    if (place.HasColorTransform)
                    {
                        Color tint = Tint(place.ColorTransform);
                        entry[PlaceTint] = new[] { tint.R, tint.G, tint.B, tint.A };
                    }

                    if (place.BlendMode > 1)
                    {
                        entry[PlaceBlend] = place.BlendMode;
                    }

                    frame.Add(entry);
                }

                table.Add(frame);

                if (frames[f].Label.Length != 0)
                {
                    labels[frames[f].Label] = f + 1;
                }

                if (frames[f].Stops)
                {
                    stops.Add(f + 1);
                }
            }

            node.SetMeta(TimelineMeta, table);
            node.SetMeta(FrameCountMeta, frames.Count);
            if (labels.Count != 0)
            {
                node.SetMeta(LabelsMeta, labels);
            }

            if (stops.Count != 0)
            {
                node.SetMeta(StopFramesMeta, stops.ToArray());
            }
        }

        private static List<Run> Runs(List<SwfFrame> frames)
        {
            var runs = new List<Run>();
            var live = new Dictionary<int, Run>();
            var current = new Dictionary<int, SwfPlaceObject>();

            for (var f = 0; f < frames.Count; f++)
            {
                SwfFrame frame = frames[f];
                foreach (int depth in frame.Removes)
                {
                    current.Remove(depth);
                    if (live.Remove(depth, out Run? ended))
                    {
                        ended.LastFrame = f - 1;
                    }
                }

                foreach (SwfPlaceObject place in frame.Places)
                {
                    SwfPlaceObject effective = place;
                    if (place.IsMove && place.CharacterId < 0)
                    {
                        if (!current.TryGetValue(place.Depth, out SwfPlaceObject? previous))
                        {
                            continue;
                        }

                        effective = Merge(previous, place);
                    }
                    else if (place.CharacterId < 0)
                    {
                        continue;
                    }

                    current[place.Depth] = effective;

                    if (live.TryGetValue(place.Depth, out Run? run) &&
                        run.CharacterId == effective.CharacterId)
                    {
                        run.States.Add((f, effective));
                        continue;
                    }

                    if (run is not null)
                    {
                        run.LastFrame = f - 1;
                    }

                    run = new Run
                    {
                        Depth = place.Depth,
                        CharacterId = effective.CharacterId,
                        FirstFrame = f,
                        LastFrame = frames.Count - 1,
                        Name = effective.Name,
                    };
                    run.States.Add((f, effective));
                    live[place.Depth] = run;
                    runs.Add(run);
                }
            }

            return runs;
        }

        private static List<SortedDictionary<int, SwfPlaceObject>> FrameStates(List<SwfFrame> frames)
        {
            var states = new List<SortedDictionary<int, SwfPlaceObject>>(frames.Count);
            var current = new SortedDictionary<int, SwfPlaceObject>();

            foreach (SwfFrame frame in frames)
            {
                foreach (int depth in frame.Removes)
                {
                    current.Remove(depth);
                }

                foreach (SwfPlaceObject place in frame.Places)
                {
                    if (place.IsMove && place.CharacterId < 0)
                    {
                        if (current.TryGetValue(place.Depth, out SwfPlaceObject? previous))
                        {
                            current[place.Depth] = Merge(previous, place);
                        }

                        continue;
                    }

                    if (place.CharacterId < 0)
                    {
                        continue;
                    }

                    current[place.Depth] = place;
                }

                states.Add(new SortedDictionary<int, SwfPlaceObject>(current));
            }

            return states;
        }

        private static void NameRuns(List<Run> runs)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (Run run in runs)
            {
                string name = run.Name.Length != 0 ? run.Name : $"d{run.Depth}";
                if (!used.Add(name))
                {
                    var suffix = 2;
                    while (!used.Add($"{name}_{suffix}"))
                    {
                        suffix++;
                    }

                    name = $"{name}_{suffix}";
                }

                run.Name = name;
            }
        }

        private void Animate(
            Node2D node, List<SwfFrame> frames, List<Run> runs, Dictionary<Run, Node2D> nodes)
        {
            float step = 1f / _frameRate;
            var animation = new Animation
            {
                Length = frames.Count * step,
                Step = step,
                LoopMode = Animation.LoopModeEnum.Linear,
            };

            var stops = new List<int>();
            for (var f = 0; f < frames.Count; f++)
            {
                if (frames[f].Label.Length != 0)
                {
                    animation.AddMarker(frames[f].Label, f * step);
                }

                if (frames[f].Stops)
                {
                    stops.Add(f + 1);
                }
            }

            if (stops.Count != 0)
            {
                int stopTrack = animation.AddTrack(Animation.TrackType.Method);
                animation.TrackSetPath(stopTrack, TimelineName);
                foreach (int frame in stops)
                {
                    animation.TrackInsertKey(stopTrack, (frame - 1) * step, new Godot.Collections.Dictionary
                    {
                        ["method"] = "pause",
                        ["args"] = new Godot.Collections.Array(),
                    });
                }
            }

            foreach (Run run in runs)
            {
                string path = nodes[run].Name;

                int visible = animation.AddTrack(Animation.TrackType.Value);
                animation.TrackSetPath(visible, path + ":visible");
                animation.TrackSetInterpolationType(visible, Animation.InterpolationType.Nearest);
                animation.ValueTrackSetUpdateMode(visible, Animation.UpdateMode.Discrete);
                if (run.FirstFrame > 0)
                {
                    animation.TrackInsertKey(visible, 0f, false);
                }

                animation.TrackInsertKey(visible, run.FirstFrame * step, true);
                if (run.LastFrame < frames.Count - 1)
                {
                    animation.TrackInsertKey(visible, (run.LastFrame + 1) * step, false);
                }

                if (run.States.Count <= 1)
                {
                    continue;
                }

                bool moves = false, tints = false;
                foreach ((_, SwfPlaceObject place) in run.States)
                {
                    moves |= place.HasMatrix;
                    tints |= place.HasColorTransform;
                }

                if (moves)
                {
                    int position = ValueTrack(animation, path + ":position");
                    int rotation = ValueTrack(animation, path + ":rotation");
                    int scale = ValueTrack(animation, path + ":scale");
                    int skew = ValueTrack(animation, path + ":skew");
                    foreach ((int frame, SwfPlaceObject place) in run.States)
                    {
                        if (!place.HasMatrix)
                        {
                            continue;
                        }

                        Transform2D t = ToTransform(place.Matrix);
                        float time = frame * step;
                        animation.TrackInsertKey(position, time, t.Origin);
                        animation.TrackInsertKey(rotation, time, t.Rotation);
                        animation.TrackInsertKey(scale, time, t.Scale);
                        animation.TrackInsertKey(skew, time, t.Skew);
                    }
                }

                if (tints)
                {
                    int modulate = ValueTrack(animation, path + ":modulate");
                    foreach ((int frame, SwfPlaceObject place) in run.States)
                    {
                        if (place.HasColorTransform)
                        {
                            animation.TrackInsertKey(modulate, frame * step, Tint(place.ColorTransform));
                        }
                    }
                }
            }

            var library = new AnimationLibrary();
            library.AddAnimation(TimelineName, animation);

            var player = new AnimationPlayer { Name = TimelineName };
            player.AddAnimationLibrary(string.Empty, library);
            if (!stops.Contains(1))
            {
                player.Autoplay = TimelineName;
            }

            node.AddChild(player);

            node.SetMeta(FrameCountMeta, frames.Count);
            if (stops.Count != 0)
            {
                node.SetMeta(StopFramesMeta, stops.ToArray());
            }
        }

        private static int ValueTrack(Animation animation, string path)
        {
            int track = animation.AddTrack(Animation.TrackType.Value);
            animation.TrackSetPath(track, path);
            animation.TrackSetInterpolationType(track, Animation.InterpolationType.Nearest);
            animation.ValueTrackSetUpdateMode(track, Animation.UpdateMode.Discrete);
            return track;
        }

        private static void Apply(Node2D node, SwfPlaceObject place)
        {
            if (place.HasMatrix)
            {
                node.Transform = ToTransform(place.Matrix);
            }

            if (place.HasColorTransform)
            {
                node.Modulate = Tint(place.ColorTransform);
            }

            CanvasItemMaterial? material = MaterialFor(place.BlendMode);
            if (material is not null)
            {
                node.Material = material;
            }
        }

        private static Color Tint(SwfColorTransform t) =>
            new(
                Mathf.Clamp(t.RedMultiply, 0f, 1f),
                Mathf.Clamp(t.GreenMultiply, 0f, 1f),
                Mathf.Clamp(t.BlueMultiply, 0f, 1f),
                Mathf.Clamp(t.AlphaMultiply, 0f, 1f));

        private static SwfPlaceObject Merge(SwfPlaceObject previous, SwfPlaceObject move) =>
            new()
            {
                Depth = previous.Depth,
                CharacterId = previous.CharacterId,
                IsMove = true,
                HasMatrix = move.HasMatrix || previous.HasMatrix,
                Matrix = move.HasMatrix ? move.Matrix : previous.Matrix,
                HasColorTransform = move.HasColorTransform || previous.HasColorTransform,
                ColorTransform = move.HasColorTransform ? move.ColorTransform : previous.ColorTransform,
                Ratio = move.Ratio >= 0 ? move.Ratio : previous.Ratio,
                Name = move.Name.Length != 0 ? move.Name : previous.Name,
                ClipDepth = move.ClipDepth >= 0 ? move.ClipDepth : previous.ClipDepth,
                BlendMode = move.BlendMode > 0 ? move.BlendMode : previous.BlendMode,
            };

        private void Shape(Node2D node, SwfShape shape, SwfGeometry geometry)
        {
            IReadOnlyList<SwfVectorPart> parts = _partsFor(shape.Id);
            for (var i = 0; i < parts.Count; i++)
            {
                SwfVectorPart part = parts[i];
                var instance = new MeshInstance2D
                {
                    Name = $"f{part.FirstFill}",
                    Mesh = part.Mesh,
                    Texture = part.Texture,
                    TextureRepeat = part.Repeat
                        ? CanvasItem.TextureRepeatEnum.Enabled
                        : CanvasItem.TextureRepeatEnum.Disabled,
                };
                node.AddChild(instance);
            }
        }

        private void Text(Node2D node, SwfEditText text)
        {
            float pixels = SwfShapeGeometry.TwipsPerPixel;
            // Wrapping is set before the text and the placement, and the size last. Outside the
            // tree a Control caches its minimum size the first time its geometry is set and never
            // refreshes it (`update_minimum_size` returns early until it is in the tree), so every
            // later size clamps to it; with wrapping still off that minimum is the text on one
            // line, wider than the authored field.
            var label = new Label
            {
                Name = TextName,
                AutowrapMode = text.WordWrap ? TextServer.AutowrapMode.Word : TextServer.AutowrapMode.Off,
                ClipText = false,
                HorizontalAlignment = text.Align switch
                {
                    SwfTextAlign.Right => HorizontalAlignment.Right,
                    SwfTextAlign.Center => HorizontalAlignment.Center,
                    SwfTextAlign.Justify => HorizontalAlignment.Fill,
                    _ => HorizontalAlignment.Left,
                },
                Text = SwfTextRenderer.PlainText(text.InitialText, text.Html),
                Position = new Vector2(text.Bounds.XMin + text.LeftMargin, text.Bounds.YMin) / pixels,
            };

            if (text.FontHeight > 0)
            {
                label.AddThemeFontSizeOverride("font_size", Math.Max(1, Mathf.RoundToInt(text.FontHeight / pixels)));
            }

            label.AddThemeColorOverride("font_color", ToColor(text.Color));

            if (!_fonts.TryGetValue(text.FontId, out (Font? Font, SwfFont? Face) font))
            {
                font = _fontFor(text.FontId);
                _fonts[text.FontId] = font;
            }

            if (font.Font is not null)
            {
                label.AddThemeFontOverride("font", font.Font);
            }

            if (font.Face is not null && text.FontHeight > 0)
            {
                float leading = Engine_SwfFont.LeadingFor(font.Face, text.FontHeight, text.Leading);
                if (leading > 0f)
                {
                    label.AddThemeConstantOverride("line_spacing", Mathf.RoundToInt(leading));
                }
            }

            label.Size = new Vector2(
                Math.Max(text.Bounds.Width - text.LeftMargin - text.RightMargin, 0), text.Bounds.Height) / pixels;

            node.AddChild(label);
        }

        private const int BlendMultiply = 3;
        private const int BlendScreen = 4;
        private const int BlendLighten = 5;
        private const int BlendAdd = 8;
        private const int BlendSubtract = 9;

        private static CanvasItemMaterial? MaterialFor(int blendMode)
        {
            CanvasItemMaterial.BlendModeEnum mode = blendMode switch
            {
                BlendMultiply => CanvasItemMaterial.BlendModeEnum.Mul,
                BlendScreen => CanvasItemMaterial.BlendModeEnum.Add,
                BlendLighten => CanvasItemMaterial.BlendModeEnum.Add,
                BlendAdd => CanvasItemMaterial.BlendModeEnum.Add,
                BlendSubtract => CanvasItemMaterial.BlendModeEnum.Sub,
                _ => CanvasItemMaterial.BlendModeEnum.Mix,
            };

            return mode == CanvasItemMaterial.BlendModeEnum.Mix
                ? null
                : new CanvasItemMaterial { BlendMode = mode };
        }

        private static Transform2D ToTransform(SwfMatrix m) =>
            new(
                new Vector2(m.ScaleX, m.RotateSkew0),
                new Vector2(m.RotateSkew1, m.ScaleY),
                new Vector2(m.TranslateX, m.TranslateY) / SwfShapeGeometry.TwipsPerPixel);

        private static Color ToColor(SwfColor colour) =>
            new(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f);
    }
}
