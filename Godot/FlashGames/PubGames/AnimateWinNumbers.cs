using System;
using System.Collections.Generic;

namespace Refii.FlashGames.PubGames;

public sealed class AnimateWinNumbers
{
    private sealed class Win
    {
        public IPubClip Clip = null!;
        public float Dx;
        public float Dy;
        public float Da;
        public double T;
        public float S;
    }

    private readonly List<Win> _wins = new();
    private readonly float _x;
    private readonly float _y;
    private readonly double _t1;

    public AnimateWinNumbers(float x, float y, double chipTravelTime)
    {
        _x = x;
        _y = y;
        _t1 = chipTravelTime;
    }

    public void AddWin(IPubClip clip, double delay)
    {
        _wins.Add(new Win
        {
            Clip = clip,
            Dx = clip.X - _x,
            Dy = clip.Y - _y,
            Da = clip.Alpha,
            T = -delay,
            S = clip.XScale,
        });
    }

    public void Update(double ticks)
    {
        for (int i = 0; i < _wins.Count; i++)
        {
            Win win = _wins[i];
            win.T += ticks;
            double t = win.T;
            if (t < 0)
            {
                continue;
            }

            bool done = false;
            IPubClip clip = win.Clip;
            if (t >= _t1)
            {
                t = _t1;
                done = true;
            }

            t /= _t1;
            double pulse = Math.Sin((t + 0.75) * Math.PI * 2);
            pulse = win.S + (75 * (1 + pulse));
            clip.XScale = (float)pulse;
            clip.YScale = (float)pulse;

            t = 1 - t;
            if (t < 0.333)
            {
                clip.Alpha = (float)(win.Da * t / 0.333);
            }

            clip.X = (float)(_x + (win.Dx * t));
            clip.Y = (float)(_y + (win.Dy * t));

            if (done)
            {
                clip.Stop();
                _wins.RemoveAt(i);
                i--;
            }
        }
    }
}
