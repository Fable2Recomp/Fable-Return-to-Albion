using System;
using System.Collections.Generic;
using Godot;
using Refii.FlashGames.Framework;

namespace Refii.FlashGames;

public sealed class SwfClip : IPubClip
{
    private readonly SwfStage _stage;
    private readonly SortedDictionary<int, SwfClip> _children = new();
    private readonly Dictionary<string, SwfClip> _byName = new(StringComparer.Ordinal);
    private readonly SwfSprite? _sprite;

    private int _currentFrame = 1;
    private bool _playing;
    private int _builtFrame;

    internal SwfClip(SwfStage stage, SwfClip? parent, int characterId, int depth, string name)
    {
        _stage = stage;
        Parent = parent;
        CharacterId = characterId;
        Depth = depth;
        Name = name;
        _sprite = stage.Document.Sprites.GetValueOrDefault(characterId);
        _playing = _sprite is not null && _sprite.Frames.Count > 1;
        _builtFrame = 0;
        RebuildFrame();
        ApplyFrameStop();
    }

    public SwfClip? Parent { get; }

    public int CharacterId { get; }

    public int Depth { get; private set; }

    public string Name { get; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Alpha { get; set; } = 100f;

    public float XScale { get; set; } = 100f;

    public float YScale { get; set; } = 100f;

    public float Rotation { get; set; }

    public bool Visible { get; set; } = true;

    public string Text { get; set; } = string.Empty;

    // RENAMED FROM `field`, which this codebase uses freely for an edit-text local. Inside a
    // PROPERTY ACCESSOR it is now a contextual keyword — C# 14 made `field` mean the compiler's
    // own backing field — so the two collide and the accessor no longer compiles. Only accessors
    // are affected, which is why the same name is left alone in SwfStage, SwfDocument and
    // SwfTextRenderer, where it is an ordinary local or parameter.
    //
    // The language level is not pinned down to hide this: this tree resolves to C# 14 because a
    // .NET 10 SDK is installed, and lowering LangVersion for the whole project to accommodate one
    // identifier would trade a two-line rename for a constraint on everything else.
    public float Height =>
        _stage.Document.EditTexts.TryGetValue(CharacterId, out SwfEditText? editText)
            ? editText.Bounds.Height / SwfShapeGeometry.TwipsPerPixel
            : 0f;

    public float TextHeight
    {
        get
        {
            if (!_stage.Document.EditTexts.TryGetValue(CharacterId, out SwfEditText? editText))
            {
                return 0f;
            }

            SwfFont? font = _stage.Fonts?.Resolve(editText.FontId)
                ?? _stage.Document.Fonts.GetValueOrDefault(editText.FontId);
            if (font is null)
            {
                return 0f;
            }

            string content = Text.Length != 0 ? Text : editText.InitialText;
            return SwfTextRenderer.MeasureHeight(
                editText, font, SwfTextRenderer.PlainText(content, editText.Html)) /
                SwfShapeGeometry.TwipsPerPixel;
        }
    }

    public SwfColorTransform Tint { get; set; } = SwfColorTransform.Identity;

    public int BlendMode { get; private set; }

    public int CurrentFrame => _currentFrame;

    public int TotalFrames => _sprite?.Frames.Count ?? 1;

    public IReadOnlyDictionary<int, SwfClip> Children => _children;

    public void GotoAndStop(int frame)
    {
        _currentFrame = Mathf.Clamp(frame, 1, TotalFrames);
        _playing = false;
        RebuildFrame();
    }

    public void GotoAndStop(string label)
    {
        int frame = FrameOfLabel(label);
        if (frame > 0)
        {
            GotoAndStop(frame);
        }
    }

    public void GotoAndPlay(int frame)
    {
        _currentFrame = Mathf.Clamp(frame, 1, TotalFrames);
        _playing = true;
        RebuildFrame();
    }

    public void Play() => _playing = true;

    public void Stop() => _playing = false;

    public void Advance()
    {
        if (_playing && TotalFrames > 1)
        {
            _currentFrame = _currentFrame >= TotalFrames ? 1 : _currentFrame + 1;
            RebuildFrame();
            ApplyFrameStop();
        }

        foreach (SwfClip child in _children.Values)
        {
            child.Advance();
        }
    }

    public IPubClip? GetChild(string name) => _byName.GetValueOrDefault(name);

    public IPubClip? AttachMovie(string exportName, string instanceName, int depth)
    {
        if (!_stage.Document.TryGetExport(exportName, out int id))
        {
            return null;
        }

        var clip = new SwfClip(_stage, this, id, depth, instanceName);
        Place(depth, clip);
        return clip;
    }

    public IPubClip CreateEmptyMovieClip(string name, int depth)
    {
        var clip = new SwfClip(_stage, this, -1, depth, name);
        Place(depth, clip);
        return clip;
    }

    public int GetDepth() => Depth;

    public void RemoveMovieClip()
    {
        Parent?.Remove(Depth);
    }

    public void SwapDepths(int depth)
    {
        if (Parent is null || depth == Depth)
        {
            return;
        }

        Parent.SwapChild(this, depth);
    }

    private void SwapChild(SwfClip clip, int depth)
    {
        int from = clip.Depth;
        _children.TryGetValue(depth, out SwfClip? other);
        _children.Remove(from);

        if (other is not null)
        {
            other.Depth = from;
            _children[from] = other;
        }

        clip.Depth = depth;
        _children[depth] = clip;
    }

    public int GetNextHighestDepth()
    {
        int highest = 0;
        foreach (int depth in _children.Keys)
        {
            if (depth >= highest)
            {
                highest = depth + 1;
            }
        }

        return highest;
    }

    private void Place(int depth, SwfClip clip)
    {
        if (_children.TryGetValue(depth, out SwfClip? existing))
        {
            _byName.Remove(existing.Name);
        }

        _children[depth] = clip;
        if (clip.Name.Length != 0)
        {
            _byName[clip.Name] = clip;
        }
    }

    private void Remove(int depth)
    {
        if (_children.TryGetValue(depth, out SwfClip? existing))
        {
            _byName.Remove(existing.Name);
            _children.Remove(depth);
        }
    }

    private int FrameOfLabel(string label)
    {
        if (_sprite is null)
        {
            return -1;
        }

        for (int i = 0; i < _sprite.Frames.Count; i++)
        {
            if (string.Equals(_sprite.Frames[i].Label, label, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return -1;
    }

    private void ApplyFrameStop()
    {
        if (_sprite is null)
        {
            return;
        }

        int index = _currentFrame - 1;
        if (index >= 0 && index < _sprite.Frames.Count && _sprite.Frames[index].Stops)
        {
            _playing = false;
        }
    }

    private void RebuildFrame()
    {
        if (_sprite is null || _sprite.Frames.Count == 0 || _builtFrame == _currentFrame)
        {
            return;
        }

        var wanted = new SortedDictionary<int, SwfPlaceObject>();
        for (int i = 0; i < _currentFrame && i < _sprite.Frames.Count; i++)
        {
            SwfFrame frame = _sprite.Frames[i];
            foreach (int depth in frame.Removes)
            {
                wanted.Remove(depth);
            }

            foreach (SwfPlaceObject place in frame.Places)
            {
                if (place.IsMove && place.CharacterId < 0 &&
                    wanted.TryGetValue(place.Depth, out SwfPlaceObject? previous))
                {
                    wanted[place.Depth] = Merge(previous, place);
                    continue;
                }

                wanted[place.Depth] = place;
            }
        }

        var stale = new List<int>();
        foreach (int depth in _children.Keys)
        {
            if (!wanted.ContainsKey(depth))
            {
                stale.Add(depth);
            }
        }

        foreach (int depth in stale)
        {
            Remove(depth);
        }

        foreach ((int depth, SwfPlaceObject place) in wanted)
        {
            if (_children.TryGetValue(depth, out SwfClip? existing) &&
                existing.CharacterId == place.CharacterId)
            {
                existing.ApplyPlacement(place);
                continue;
            }

            var clip = new SwfClip(_stage, this, place.CharacterId, depth, place.Name);
            clip.ApplyPlacement(place);
            Place(depth, clip);
        }

        _builtFrame = _currentFrame;
    }

    internal void AttachPlaced(SwfPlaceObject place) => Apply(place);

    private static SwfPlaceObject Merge(SwfPlaceObject previous, SwfPlaceObject move) =>
        new()
        {
            Depth = previous.Depth,
            CharacterId = previous.CharacterId,
            IsMove = true,
            HasMatrix = move.HasMatrix || previous.HasMatrix,
            Matrix = move.HasMatrix ? move.Matrix : previous.Matrix,
            HasColorTransform = move.HasColorTransform || previous.HasColorTransform,
            ColorTransform = move.HasColorTransform
                ? move.ColorTransform
                : previous.ColorTransform,
            Ratio = move.Ratio >= 0 ? move.Ratio : previous.Ratio,
            Name = move.Name.Length != 0 ? move.Name : previous.Name,
            ClipDepth = move.ClipDepth >= 0 ? move.ClipDepth : previous.ClipDepth,
            BlendMode = move.BlendMode > 0 ? move.BlendMode : previous.BlendMode,
        };

    private void Apply(SwfPlaceObject place)
    {
        _children.TryGetValue(place.Depth, out SwfClip? existing);

        if (place.IsMove && existing is not null && place.CharacterId < 0)
        {
            existing.ApplyPlacement(place);
            return;
        }

        if (place.CharacterId < 0)
        {
            return;
        }

        var clip = new SwfClip(_stage, this, place.CharacterId, place.Depth, place.Name);
        clip.ApplyPlacement(place);
        Place(place.Depth, clip);
    }

    private void ApplyPlacement(SwfPlaceObject place)
    {
        if (place.HasMatrix)
        {
            SwfMatrix m = place.Matrix;
            X = m.TranslateX / SwfShapeGeometry.TwipsPerPixel;
            Y = m.TranslateY / SwfShapeGeometry.TwipsPerPixel;

            float scaleX = Mathf.Sqrt((m.ScaleX * m.ScaleX) + (m.RotateSkew0 * m.RotateSkew0));
            float scaleY = Mathf.Sqrt((m.ScaleY * m.ScaleY) + (m.RotateSkew1 * m.RotateSkew1));
            XScale = scaleX * 100f;
            YScale = scaleY * 100f;
            Rotation = Mathf.RadToDeg(Mathf.Atan2(m.RotateSkew0, m.ScaleX));
        }

        if (place.BlendMode > 1)
        {
            BlendMode = place.BlendMode;
        }

        if (place.HasColorTransform)
        {
            Tint = place.ColorTransform with { AlphaMultiply = 1f };
            Alpha = place.ColorTransform.AlphaMultiply * 100f;
        }
    }

    public Transform2D LocalTransform()
    {
        float radians = Mathf.DegToRad(Rotation);
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        float sx = XScale / 100f;
        float sy = YScale / 100f;
        return new Transform2D(
            new Vector2(cos * sx, sin * sx),
            new Vector2(-sin * sy, cos * sy),
            new Vector2(X, Y) * SwfShapeGeometry.TwipsPerPixel);
    }
}
