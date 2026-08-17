using System;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.Keystone;

public class ChipStack : IAnimValue
{
    public static readonly int[] Denominations = { 50000, 10000, 2000, 500, 100, 25, 5, 1 };

    private readonly IPubRandom _random;
    private int _value;

    public ChipStack(
        IPubClip stackClip, IPubClip? shadowClip, IPubClip? poofClip,
        IPubClip? winClip, IPubClip? winBigClip, IPubClip? lossClip,
        bool lammer, IPubRandom random)
    {
        _random = random;
        Stack = stackClip;
        Shadow = shadowClip;
        Poof = poofClip;
        Win = winClip;
        WinBig = winBigClip;
        Loss = lossClip;
        Owner = -1;
        TopChipX = 0;
        TopChipY = 0;
        BaseX = 0;
        BaseY = 0;
        Lammer = lammer;

        if (Poof is not null)
        {
            Poof.GotoAndStop(Poof.TotalFrames);
            Poof.Visible = false;
        }

        ResetLabel(Win);
        ResetLabel(WinBig);
        ResetLabel(Loss);

        Value = 0;
    }

    public IPubClip Stack { get; set; }

    public IPubClip? Shadow { get; set; }

    public IPubClip? Poof { get; set; }

    public IPubClip? Win { get; set; }

    public IPubClip? WinBig { get; set; }

    public IPubClip? Loss { get; set; }

    public int Owner { get; set; }

    public bool Lammer { get; set; }

    public double Angle { get; set; }

    public float TopChipX { get; private set; }

    public float TopChipY { get; private set; }

    public float BaseX { get; set; }

    public float BaseY { get; set; }

    double IAnimValue.Value
    {
        get => Value;
        set => Value = (int)System.Math.Round(value);
    }

    public int Value
    {
        get => _value;
        set
        {
            _value = value;
            if (Lammer)
            {
                ApplyLammer();
            }
            else if (_value <= 0)
            {
                ApplyEmpty();
            }
            else
            {
                ApplyChips();
            }
        }
    }

    public static int[] Breakdown(int value)
    {
        var counts = new int[Denominations.Length];
        int remaining = value;
        for (int i = 0; i < Denominations.Length; i++)
        {
            counts[i] = (int)Math.Floor((double)remaining / Denominations[i]);
            remaining -= counts[i] * Denominations[i];
        }

        return counts;
    }

    public static int ChipCount(int value)
    {
        int total = 0;
        foreach (int count in Breakdown(value))
        {
            total += count;
        }

        return total;
    }

    private void ApplyLammer()
    {
        if (_value <= 0)
        {
            Stack.Visible = false;
            Stack.GotoAndStop(1);
            Stack.Alpha = 100;
            return;
        }

        Stack.GotoAndStop(Owner + 1);
        Stack.Visible = true;
        Stack.Rotation = (float)((_random.NextDouble() * 50) - 25);
        if (Loss is not null)
        {
            Loss.X = Stack.X;
            Loss.Y = Stack.Y;
        }
    }

    private void ApplyEmpty()
    {
        Stack.Visible = false;
        Stack.GotoAndStop(1);
        Stack.Alpha = 100;
        if (Shadow is not null)
        {
            Shadow.Visible = false;
            Shadow.GotoAndStop(1);
            Shadow.Alpha = 100;
        }
    }

    private void ApplyChips()
    {
        Stack.Visible = true;
        int[] counts = Breakdown(_value);

        int total = 0;
        foreach (int count in counts)
        {
            total += count;
        }

        Stack.GotoAndStop(total + 1);
        if (Shadow is not null)
        {
            Shadow.Visible = true;
            Shadow.GotoAndStop(total + 1);
        }

        int ownerOffset = Owner * 8;
        if (ownerOffset < 0)
        {
            ownerOffset = 0;
        }

        int placed = 0;
        double lean = Math.Cos(Angle) * 1.5;
        for (int i = 0; i < 8; i++)
        {
            int frame = 8 - i + ownerOffset;
            for (int n = counts[i]; n > 0; n--)
            {
                IPubClip? chip = Stack.GetChild("chip" + placed);
                if (chip is not null)
                {
                    chip.GotoAndStop(frame);
                    chip.X = (float)(BaseX + (lean * placed) + (_random.NextDouble() * 2) - 1);
                    chip.Y = (float)(BaseY - (5 * placed) - (_random.NextDouble() * 2.5));
                    chip.Rotation = (float)(_random.NextDouble() * 20);
                }

                placed++;
            }
        }

        TopChipX = (float)(BaseX + (lean * placed));
        TopChipY = BaseY - (5 * placed);

        if (Win is not null)
        {
            Win.X = Stack.X + TopChipX;
            Win.Y = Stack.Y + TopChipY;
        }

        if (WinBig is not null)
        {
            WinBig.X = Stack.X + TopChipX;
            WinBig.Y = Stack.Y + TopChipY;
        }
    }

    private static void ResetLabel(IPubClip? clip)
    {
        if (clip is null)
        {
            return;
        }

        clip.GotoAndStop(1);
        IPubClip? label = clip.GetChild("label");
        if (label is not null)
        {
            label.Text = string.Empty;
        }

        clip.Alpha = 0;
    }
}
