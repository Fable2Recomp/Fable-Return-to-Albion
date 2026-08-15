using System;

namespace Refii.FlashGames.PubGames;

public sealed class ChipStackMulti : IAnimValue
{
    private readonly IPubClip _stack1;
    private readonly IPubClip? _shadow1;
    private readonly IPubClip _stack2;
    private readonly IPubClip? _shadow2;
    private readonly IPubClip _stack3;
    private readonly IPubClip? _shadow3;
    private readonly IPubRandom _random;

    private int _value;
    private int _maxAChipType;
    private int _minBChipType;
    private int _maxBChipType;
    private int _minCChipType;
    private int _num1s;
    private int _num5s;
    private int _num25s;
    private int _num100s;
    private int _num500s;
    private int _num2000s;
    private int _num10000s;
    private int _num50000s;

    public ChipStackMulti(
        IPubClip clip1, IPubClip? shadowClip1,
        IPubClip clip2, IPubClip? shadowClip2,
        IPubClip clip3, IPubClip? shadowClip3, IPubRandom random)
    {
        _stack1 = clip1;
        _shadow1 = shadowClip1;
        _stack2 = clip2;
        _shadow2 = shadowClip2;
        _stack3 = clip3;
        _shadow3 = shadowClip3;
        _random = random;
        Owner = -1;
        Angle1 = Math.PI / 2;
        Angle2 = Math.PI / 2;
        Angle3 = Math.PI / 2;
        Value = 0;
    }

    /// <summary>Fades the whole stack, shadows included.</summary>
    /// <remarks>
    /// ALL SIX CLIPS. A multi-stack is three columns of chips with a shadow under each, and the
    /// shadows are siblings rather than children — fading only the columns leaves three shadows
    /// lying on the felt under nothing.
    /// </remarks>
    public void SetAlpha(float alpha)
    {
        _stack1.Alpha = alpha;
        _stack2.Alpha = alpha;
        _stack3.Alpha = alpha;

        if (_shadow1 is not null)
        {
            _shadow1.Alpha = alpha;
        }

        if (_shadow2 is not null)
        {
            _shadow2.Alpha = alpha;
        }

        if (_shadow3 is not null)
        {
            _shadow3.Alpha = alpha;
        }
    }

    public int Owner { get; set; }

    public double Angle1 { get; set; }

    public double Angle2 { get; set; }

    public double Angle3 { get; set; }

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
            if (value == _value)
            {
                return;
            }

            CalculateChips(_value);
            int oldA = CalculateChipsInStack(1, _maxAChipType);
            int oldB = CalculateChipsInStack(_minBChipType, _maxBChipType);
            int oldC = CalculateChipsInStack(_minCChipType, 50000);

            _value = value;
            CalculateChips(_value);

            SetSingleStack(_stack1, _shadow1, Angle1, oldA, 1, _maxAChipType);
            SetSingleStack(_stack2, _shadow2, Angle2, oldB, _minBChipType, _maxBChipType);
            SetSingleStack(_stack3, _shadow3, Angle3, oldC, _minCChipType, 50000);
        }
    }

    private void SetSingleStack(
        IPubClip stackClip, IPubClip? shadowClip, double angle,
        int oldTotal, int smallestType, int largestType)
    {
        int n1 = InRange(1, smallestType, largestType) ? _num1s : 0;
        int n5 = InRange(5, smallestType, largestType) ? _num5s : 0;
        int n25 = InRange(25, smallestType, largestType) ? _num25s : 0;
        int n100 = InRange(100, smallestType, largestType) ? _num100s : 0;
        int n500 = InRange(500, smallestType, largestType) ? _num500s : 0;
        int n2000 = InRange(2000, smallestType, largestType) ? _num2000s : 0;
        int n10000 = InRange(10000, smallestType, largestType) ? _num10000s : 0;
        int n50000 = InRange(50000, smallestType, largestType) ? _num50000s : 0;

        int total = n1 + n5 + n25 + n100 + n500 + n2000 + n10000 + n50000;
        stackClip.GotoAndStop(total + 1);
        shadowClip?.GotoAndStop(total + 1);

        double lean = Math.Cos(angle) * 1.5;
        int ownerOffset = Owner * 8;
        if (ownerOffset < 0)
        {
            ownerOffset = 0;
        }

        for (int i = 0; i < total; i++)
        {
            IPubClip? chip = stackClip.GetChild("chip" + i);
            if (chip is null)
            {
                continue;
            }

            int frame = ownerOffset + 1;
            if (i < n50000)
            {
                frame = ownerOffset + 8;
            }
            else if (i < n50000 + n10000)
            {
                frame = ownerOffset + 7;
            }
            else if (i < n50000 + n10000 + n2000)
            {
                frame = ownerOffset + 6;
            }
            else if (i < n50000 + n10000 + n2000 + n500)
            {
                frame = ownerOffset + 5;
            }
            else if (i < n50000 + n10000 + n2000 + n500 + n100)
            {
                frame = ownerOffset + 4;
            }
            else if (i < n50000 + n10000 + n2000 + n500 + n100 + n25)
            {
                frame = ownerOffset + 3;
            }
            else if (i < n50000 + n10000 + n2000 + n500 + n100 + n25 + n5)
            {
                frame = ownerOffset + 2;
            }

            chip.GotoAndStop(frame);
            chip.X = (float)((lean * i) + (_random.NextDouble() * 2) - 1);
            chip.Y = (float)((-5 * i) - (_random.NextDouble() * 2.5));
            chip.Rotation = (float)(_random.NextDouble() * 20);
        }
    }

    private static bool InRange(int type, int smallest, int largest) =>
        smallest <= type && largest >= type;

    private void CalculateChips(int totalChips)
    {
        int remaining = totalChips;
        _maxAChipType = 1;
        _minBChipType = 5;
        _maxBChipType = 5;
        _minCChipType = 25;

        if (totalChips >= 500)
        {
            _maxBChipType = 25;
            _minCChipType = 100;
        }

        if (totalChips >= 3635)
        {
            _maxAChipType = 5;
            _minBChipType = 25;
            _maxBChipType = 100;
            _minCChipType = 500;
        }

        if (totalChips >= 22630)
        {
            _maxAChipType = 25;
            _minBChipType = 100;
            _maxBChipType = 500;
            _minCChipType = 2000;
        }

        if (totalChips >= 112600)
        {
            _maxAChipType = 100;
            _minBChipType = 500;
            _maxBChipType = 2000;
            _minCChipType = 10000;
        }

        if (totalChips >= 562000)
        {
            _maxAChipType = 100;
            _minBChipType = 500;
            _maxBChipType = 10000;
            _minCChipType = 50000;
        }

        _num1s = remaining % 5;
        remaining -= remaining % 5;
        if (remaining >= 5 && totalChips < 22630)
        {
            _num1s += 5;
            remaining -= 5;
        }

        if (totalChips >= 500 && totalChips < 3635)
        {
            _num1s += 5;
            remaining -= 5;
        }

        remaining /= 5;
        _num5s = remaining % 5;
        remaining -= remaining % 5;
        if (remaining >= 5 && totalChips < 22630)
        {
            _num5s += 5;
            remaining -= 5;
        }

        remaining /= 5;
        _num25s = remaining % 4;
        remaining -= remaining % 4;
        if (remaining >= 4 && totalChips < 112600)
        {
            _num25s += 4;
            remaining -= 4;
        }

        remaining /= 4;
        _num100s = remaining % 5;
        remaining -= remaining % 5;
        if (remaining >= 5 && totalChips < 112600)
        {
            _num100s += 5;
            remaining -= 5;
        }

        remaining /= 5;
        _num500s = remaining % 4;
        remaining -= remaining % 4;
        if (remaining >= 4)
        {
            _num500s += 4;
            remaining -= 4;
        }

        remaining /= 4;
        _num2000s = remaining % 5;
        remaining -= remaining % 5;
        if (remaining >= 5 && totalChips < 562000)
        {
            _num2000s += 5;
            remaining -= 5;
        }

        remaining /= 5;
        _num10000s = remaining % 5;
        remaining -= remaining % 5;
        if (remaining >= 5 && totalChips < 562000)
        {
            _num10000s += 5;
            remaining -= 5;
        }

        remaining /= 5;
        _num50000s = remaining;
        if (_num50000s > 16)
        {
            _num50000s = 16;
        }
    }

    private int CalculateChipsInStack(int smallestType, int largestType)
    {
        int total = 0;
        if (InRange(1, smallestType, largestType))
        {
            total += _num1s;
        }

        if (InRange(5, smallestType, largestType))
        {
            total += _num5s;
        }

        if (InRange(25, smallestType, largestType))
        {
            total += _num25s;
        }

        if (InRange(100, smallestType, largestType))
        {
            total += _num100s;
        }

        if (InRange(500, smallestType, largestType))
        {
            total += _num500s;
        }

        if (InRange(2000, smallestType, largestType))
        {
            total += _num2000s;
        }

        if (InRange(10000, smallestType, largestType))
        {
            total += _num10000s;
        }

        if (InRange(50000, smallestType, largestType))
        {
            total += _num50000s;
        }

        return total;
    }
}
