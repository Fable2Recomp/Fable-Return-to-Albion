using System;

namespace Refii.FlashGames.PubGames;

public sealed class Die : IAnimPos, IAnimScale, IAnimFrame
{
    public static readonly int[][] Sides =
    {
        new[] { 5, 4, 1, 6, 3, 2 },
        new[] { 4, 6, 2, 5, 1, 3 },
        new[] { 1, 2, 3, 4, 5, 6 },
        new[] { 6, 2, 4, 3, 5, 1 },
        new[] { 3, 6, 5, 2, 1, 4 },
        new[] { 2, 4, 6, 1, 3, 5 },
    };

    public static readonly string[] DieNames = { "die1", "die2", "die3", "die4", "die5" };

    public static readonly string[] CheatNames = { "cheat0" };

    private readonly IPubClip _parent;
    private readonly IPubClip _shadows;
    private readonly IPubRandom _random;
    private readonly int _id;
    private readonly float _x;
    private readonly float _y;
    private readonly float _xscale;
    private readonly float _yscale;

    private IPubClip? _die;
    private IPubClip? _shadow;

    public Die(
        int id, IPubClip parent, IPubClip shadows,
        float x, float y, float xscale, float yscale, IPubRandom random)
    {
        _id = id;
        _parent = parent;
        _shadows = shadows;
        _x = x;
        _y = y;
        _xscale = xscale;
        _yscale = yscale;
        _random = random;
        SetRoll(random.Range(6) + 1);
    }

    public int Roll { get; private set; }

    public IPubClip? Clip => _die;

    public static int GetPreCheatRoll(int finalRoll)
    {
        for (int i = 0; i < Sides.Length; i++)
        {
            if (Sides[i][5] == finalRoll)
            {
                return i + 1;
            }
        }

        return 1;
    }

    public void SetRoll(int roll, bool cheat = false, int frame = 0)
    {
        float x = _x;
        float y = _y;
        float xscale = _xscale;
        float yscale = _yscale;

        if (_die is not null)
        {
            x = _die.X;
            y = _die.Y;
            xscale = _die.XScale;
            yscale = _die.YScale;
            _die.RemoveMovieClip();
            _die = null;
        }

        if (_shadow is not null)
        {
            _shadow.RemoveMovieClip();
            _shadow = null;
        }

        if (cheat)
        {
            string name = CheatNames[_random.Range(CheatNames.Length)];
            _die = _parent.AttachMovie(name, "die" + _id, _id);
        }
        else
        {
            string name = DieNames[_random.Range(DieNames.Length)];
            _die = _parent.AttachMovie(name, "die" + _id, _id);
            _shadow = _shadows.AttachMovie(name + "shadow", "die" + _id, _id);
        }

        if (_die is not null)
        {
            _die.X = x;
            _die.Y = y;
            _die.XScale = xscale;
            _die.YScale = yscale;
        }

        if (_shadow is not null)
        {
            _shadow.X = x;
            _shadow.Y = y;
            _shadow.XScale = xscale;
            _shadow.YScale = yscale;
        }

        Roll = roll;
        if (frame < 1)
        {
            frame = _die?.TotalFrames ?? 1;
        }

        SetFrame(frame);

        IPubClip? centre = _die?.GetChild("center");
        if (centre is not null)
        {
            centre.Visible = false;
        }
    }

    public void SetFrame(int frame)
    {
        _die?.GotoAndStop(frame);
        _shadow?.GotoAndStop(frame);

        if (_die is null || Roll < 1 || Roll > Sides.Length)
        {
            return;
        }

        int[] faces = Sides[Roll - 1];
        for (int i = 0; i < 6; i++)
        {
            _die.GetChild("side" + i)?.GotoAndStop(faces[i]);
        }
    }

    public int TotalFrames => _die?.TotalFrames ?? 0;

    public float BaseX => _x - (_die?.GetChild("center")?.X ?? 0f);

    public float BaseY => _y - (_die?.GetChild("center")?.Y ?? 0f);

    public float X
    {
        get => _die?.X ?? _x;
        set => SetX(value);
    }

    public float Y
    {
        get => _die?.Y ?? _y;
        set => SetY(value);
    }

    public void SetX(float value)
    {
        if (_die is not null)
        {
            _die.X = value;
        }

        if (_shadow is not null)
        {
            _shadow.X = value;
        }
    }

    public void SetY(float value)
    {
        if (_die is not null)
        {
            _die.Y = value;
        }

        if (_shadow is not null)
        {
            _shadow.Y = value;
        }
    }

    public void SetXScale(float value)
    {
        if (_die is not null)
        {
            _die.XScale = value;
        }

        if (_shadow is not null)
        {
            _shadow.XScale = value;
        }
    }

    public void SetYScale(float value)
    {
        if (_die is not null)
        {
            _die.YScale = value;
        }

        if (_shadow is not null)
        {
            _shadow.YScale = value;
        }
    }

    public float Scale
    {
        get => _die?.XScale ?? 100f;
        set
        {
            SetXScale(value);
            SetYScale(value);
        }
    }
}
