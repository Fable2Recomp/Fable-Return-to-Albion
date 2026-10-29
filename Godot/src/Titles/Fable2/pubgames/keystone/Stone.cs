using System;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2.PubGames.Keystone;

public sealed class Stone : IAnimPos, IAnimFrame
{
    public const double ExitDistance = 800;

    private readonly IPubClip _stone;
    private readonly int _skinFrame;

    public Stone(IPubClip clip, double angle, int skinIndex)
    {
        _stone = clip;
        BaseX = clip.X;
        BaseY = clip.Y;
        BaseRotation = clip.Rotation;
        Angle = angle;

        int frame = skinIndex + 1;
        _skinFrame = frame < 3 ? 3 - frame : frame;

        OffX = (float)(BaseX + (ExitDistance * Math.Cos(angle * Math.PI / 180)));
        OffY = (float)(BaseY - (ExitDistance * Math.Sin(angle * Math.PI / 180)));
        Removed = false;
        SetFrame(clip.TotalFrames);
    }

    public IPubClip Clip => _stone;

    public double Angle { get; }

    public float BaseX { get; }

    public float BaseY { get; }

    public float BaseRotation { get; }

    public float OffX { get; }

    public float OffY { get; }

    public bool Removed { get; set; }

    public float X
    {
        get => _stone.X;
        set => _stone.X = value;
    }

    public float Y
    {
        get => _stone.Y;
        set => _stone.Y = value;
    }

    public void SetX(float value) => X = value;

    public void SetY(float value) => Y = value;

    public void SetFrame(int frame)
    {
        _stone.GotoAndStop(frame);
        _stone.GetChild("trim")?.GotoAndStop(_skinFrame);
    }

    public void SetOffScreen()
    {
        _stone.X = OffX;
        _stone.Y = OffY;
        Removed = true;
    }

    public void SetOnArch()
    {
        _stone.X = BaseX;
        _stone.Y = BaseY;
        Removed = false;
    }
}
