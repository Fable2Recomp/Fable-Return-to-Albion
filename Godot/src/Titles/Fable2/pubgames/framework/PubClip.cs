using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Refii.Titles.Fable2.PubGames.Framework;

public interface IPubClip
{
    float X { get; set; }

    float Y { get; set; }

    float Alpha { get; set; }

    float XScale { get; set; }

    float YScale { get; set; }

    float Rotation { get; set; }

    bool Visible { get; set; }

    int CurrentFrame { get; }

    int TotalFrames { get; }

    string Text { get; set; }

    float Height { get; }

    float TextHeight { get; }

    string Name { get; }

    void GotoAndStop(int frame);

    void GotoAndStop(string label);

    void GotoAndPlay(int frame);

    void Play();

    void Stop();

    IPubClip? GetChild(string name);

    IPubClip? AttachMovie(string exportName, string instanceName, int depth);

    IPubClip CreateEmptyMovieClip(string name, int depth);

    int GetDepth();

    void RemoveMovieClip();

    void SwapDepths(int depth);

    int GetNextHighestDepth();

    void SetTint(
        float redMultiply, float greenMultiply, float blueMultiply,
        float redOffset, float greenOffset, float blueOffset)
    {
    }

    void SetTextFormat(PubTextFormat format)
    {
    }
}

public interface IAnimPos
{
    void SetX(float value);

    void SetY(float value);
}

public interface IAnimScale
{
    void SetXScale(float value);

    void SetYScale(float value);
}

public interface IAnimAlpha
{
    void SetAlpha(float value);
}

public interface IAnimFrame
{
    void SetFrame(int frame);
}

public interface IAnimValue
{
    double Value { get; set; }
}

/// <summary>
/// The expando members AS2 hangs on a MovieClip (<c>clip.xs</c>, <c>clip.baseXScale</c>), kept per
/// clip object for as long as the clip lives.
/// </summary>
public static class ClipMembers
{
    private static readonly ConditionalWeakTable<IPubClip, Dictionary<string, float>> Members = new();

    /// <summary><c>if (clip[name] == null) clip[name] = value; return clip[name]</c>.</summary>
    public static float Remember(IPubClip clip, string name, float value)
    {
        Dictionary<string, float> members = Members.GetOrCreateValue(clip);
        if (!members.TryGetValue(name, out float stored))
        {
            stored = value;
            members[name] = stored;
        }

        return stored;
    }
}
