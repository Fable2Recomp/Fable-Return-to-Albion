using System;
using System.Collections.Generic;
using System.Globalization;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed class ChipStack : Action
{
    private const int ChipsPerStack = 20;

    public static Localizer? Loc { get; set; }

    public int Player { get; set; }

    public double? StartChips { get; set; }

    public double FinalChips { get; set; }

    public double BetAmount { get; set; }

    public double PlaySoundPercent { get; set; }

    public bool PlayApplause { get; set; }

    public int OldA { get; set; }

    public int OldB { get; set; }

    public int OldC { get; set; }

    public int MaxAChipType { get; set; }

    public int MinBChipType { get; set; }

    public int MaxBChipType { get; set; }

    public int MinCChipType { get; set; }

    public int Num1s { get; set; }

    public int Num5s { get; set; }

    public int Num25s { get; set; }

    public int Num100s { get; set; }

    public int Num500s { get; set; }

    public int Num2000s { get; set; }

    public int Num10000s { get; set; }

    public int Num50000s { get; set; }

    public static new void Preallocate(int size) => ActionPool<ChipStack>.Preallocate(size);

    public static ChipStack Timed(
        IPubClip theChipTotal, int player, double finalChips, double betAmount,
        object? group = null)
    {
        ChipStack stack = ActionPool<ChipStack>.GetFromArray();
        stack.Init(theChipTotal, player, finalChips, 2, betAmount, group);
        return Add(stack);
    }

    public static ChipStack NoApplause(
        IPubClip theChipTotal, int player, double finalChips, double betAmount,
        object? group = null)
    {
        ChipStack stack = ActionPool<ChipStack>.GetFromArray();
        stack.Init(theChipTotal, player, finalChips, 2, betAmount, group);
        stack.PlayApplause = false;
        return Add(stack);
    }

    public static ChipStack Instant(
        IPubClip theChipTotal, int player, double finalChips, object? group = null)
    {
        ChipStack stack = ActionPool<ChipStack>.GetFromArray();
        stack.Init(theChipTotal, player, finalChips, 0, 1, group);
        return Add(stack);
    }

    public void Init(
        IPubClip theChipTotal, int player, double finalChips, double? duration, double betAmount,
        object? group)
    {
        InitActionData();
        DebugString = "ChipStack";
        CallbackData = theChipTotal;
        Duration = duration is null ? null : duration.Value * 1000;
        Player = player;
        StartChips = null;
        FinalChips = finalChips;
        BetAmount = betAmount;
        PlaySoundPercent = double.PositiveInfinity;
        PlayApplause = true;
        Group = group;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (callbackData is not IPubClip total)
        {
            return true;
        }

        IPubClip? change = WizardsTower.Root?.GetChild("ChipChange" + Player);

        if (StartChips is null)
        {
            StartChips = double.TryParse(
                total.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                ? parsed
                : double.NaN;

            double delta = FinalChips - StartChips.Value;
            IPubClip? changeText = change?.GetChild("ChipText");
            if (changeText is not null)
            {
                changeText.Text = string.Empty;
            }

            if (Duration is null)
            {
                Duration = delta == 0 ? 0 : 2000;
                CalculatePercentDone();
            }

            if (delta != 0)
            {
                PlaySoundPercent = (WizardsTower.Chance?.NextDouble() ?? 0) / 4;
            }

            CalculateChips(StartChips.Value);
            OldA = CalculateChipsInStack(1, MaxAChipType);
            OldB = CalculateChipsInStack(MinBChipType, MaxBChipType);
            OldC = CalculateChipsInStack(MinCChipType, 50000);
            CalculateChips(FinalChips);
        }

        if (PercentDone >= PlaySoundPercent)
        {
            double bets = (FinalChips - StartChips.Value) / BetAmount;
            int rate = Player == 0 ? 200 : 1600;

            if (bets >= 6)
            {
                if (Player == 0 && PlayApplause)
                {
                    WizardsTower.Sound?.PlaySound("applause04_large");
                }

                WizardsTower.Sound?.PlaySound("chips04BigWin?x=" + rate);
            }
            else if (bets >= 2)
            {
                if (Player == 0 && PlayApplause)
                {
                    WizardsTower.Sound?.PlaySound("applause03_medium");
                }

                WizardsTower.Sound?.PlaySound("chips03Medium?x=" + rate);
            }
            else if (bets > 0)
            {
                WizardsTower.Sound?.PlaySound("chips02Several?x=" + rate);
            }

            PlaySoundPercent = double.PositiveInfinity;
        }

        double showing = FinalChips;
        if (PercentDone < 0.5)
        {
            showing = Math.Floor(
                StartChips.Value + (PercentDone * 2 * (FinalChips - StartChips.Value)));
        }

        total.Text = Loc?.LocalizeString(
            "txtChipTotal",
            new Dictionary<string, string> { ["CHIPS"] = showing.ToString(CultureInfo.InvariantCulture) })
            ?? showing.ToString(CultureInfo.InvariantCulture);

        if (change is not null)
        {
            change.Alpha = PercentDone < 0.5 ? 100 : (float)(200 - (PercentDone * 200));
        }

        double fade = PercentDone < 0.5 ? PercentDone * 200 : 100;
        SetSingleStack("A", fade, OldA, 1, MaxAChipType);
        SetSingleStack("B", fade, OldB, MinBChipType, MaxBChipType);
        SetSingleStack("C", fade, OldC, MinCChipType, 50000);

        return PercentDone == 1;
    }

    public void SetSingleStack(string stack, double fade, int oldTotal, int smallestType, int largestType)
    {
        IPubClip? pile = WizardsTower.Root?.GetChild("Stack" + Player + stack);
        if (pile is null)
        {
            return;
        }

        if (pile.CurrentFrame != ChipsPerStack)
        {
            pile.GotoAndStop(ChipsPerStack);
        }

        int ones = InRange(smallestType, largestType, 1) ? Num1s : 0;
        int fives = InRange(smallestType, largestType, 5) ? Num5s : 0;
        int twentyFives = InRange(smallestType, largestType, 25) ? Num25s : 0;
        int hundreds = InRange(smallestType, largestType, 100) ? Num100s : 0;
        int fiveHundreds = InRange(smallestType, largestType, 500) ? Num500s : 0;
        int twoThousands = InRange(smallestType, largestType, 2000) ? Num2000s : 0;
        int tenThousands = InRange(smallestType, largestType, 10000) ? Num10000s : 0;
        int fiftyThousands = InRange(smallestType, largestType, 50000) ? Num50000s : 0;

        int height = ones + fives + twentyFives + hundreds + fiveHundreds + twoThousands
            + tenThousands + fiftyThousands;

        for (var i = 0; i < height; i++)
        {
            IPubClip? chip = pile.GetChild("chip" + i);
            if (chip is null)
            {
                continue;
            }

            int top = i == height - 1 ? 0 : 8;

            if (i < fiftyThousands)
            {
                chip.GotoAndStop(8 + top);
            }
            else if (i < fiftyThousands + tenThousands)
            {
                chip.GotoAndStop(7 + top);
            }
            else if (i < fiftyThousands + tenThousands + twoThousands)
            {
                chip.GotoAndStop(6 + top);
            }
            else if (i < fiftyThousands + tenThousands + twoThousands + fiveHundreds)
            {
                chip.GotoAndStop(5 + top);
            }
            else if (i < fiftyThousands + tenThousands + twoThousands + fiveHundreds + hundreds)
            {
                chip.GotoAndStop(4 + top);
            }
            else if (i < fiftyThousands + tenThousands + twoThousands + fiveHundreds + hundreds
                + twentyFives)
            {
                chip.GotoAndStop(3 + top);
            }
            else if (i < fiftyThousands + tenThousands + twoThousands + fiveHundreds + hundreds
                + twentyFives + fives)
            {
                chip.GotoAndStop(2 + top);
            }
            else
            {
                chip.GotoAndStop(1 + top);
            }

            if (i >= oldTotal)
            {
                chip.Alpha = (float)fade;
            }
        }

        for (int i = height; i < ChipsPerStack; i++)
        {
            IPubClip? chip = pile.GetChild("chip" + i);
            if (chip is null)
            {
                continue;
            }

            chip.Alpha = i >= oldTotal ? 0 : (float)(100 - fade);
        }

        pile.GetChild("Shadow")?.GotoAndStop(height);
    }

    public void CalculateChips(double totalchips)
    {
        double left = totalchips;
        MaxAChipType = 1;
        MinBChipType = 5;
        MaxBChipType = 5;
        MinCChipType = 25;

        if (totalchips >= 500)
        {
            MaxBChipType = 25;
            MinCChipType = 100;
        }

        if (totalchips >= 3635)
        {
            MaxAChipType = 5;
            MinBChipType = 25;
            MaxBChipType = 100;
            MinCChipType = 500;
        }

        if (totalchips >= 22630)
        {
            MaxAChipType = 25;
            MinBChipType = 100;
            MaxBChipType = 500;
            MinCChipType = 2000;
        }

        if (totalchips >= 112600)
        {
            MaxAChipType = 100;
            MinBChipType = 500;
            MaxBChipType = 2000;
            MinCChipType = 10000;
        }

        if (totalchips >= 562000)
        {
            MaxAChipType = 100;
            MinBChipType = 500;
            MaxBChipType = 10000;
            MinCChipType = 50000;
        }

        Num1s = (int)(left % 5);
        left -= left % 5;
        if (left >= 5 && totalchips < 22630)
        {
            Num1s += 5;
            left -= 5;
        }

        if (totalchips >= 500 && totalchips < 3635)
        {
            Num1s += 5;
            left -= 5;
        }

        left /= 5;
        Num5s = (int)(left % 5);
        left -= left % 5;
        if (left >= 5 && totalchips < 22630)
        {
            Num5s += 5;
            left -= 5;
        }

        left /= 5;
        Num25s = (int)(left % 4);
        left -= left % 4;
        if (left >= 4 && totalchips < 112600)
        {
            Num25s += 4;
            left -= 4;
        }

        left /= 4;
        Num100s = (int)(left % 5);
        left -= left % 5;
        if (left >= 5 && totalchips < 112600)
        {
            Num100s += 5;
            left -= 5;
        }

        left /= 5;
        Num500s = (int)(left % 4);
        left -= left % 4;
        if (left >= 4)
        {
            Num500s += 4;
            left -= 4;
        }

        left /= 4;
        Num2000s = (int)(left % 5);
        left -= left % 5;
        if (left >= 5 && totalchips < 562000)
        {
            Num2000s += 5;
            left -= 5;
        }

        left /= 5;
        Num10000s = (int)(left % 5);
        left -= left % 5;
        if (left >= 5 && totalchips < 562000)
        {
            Num10000s += 5;
            left -= 5;
        }

        left /= 5;
        Num50000s = (int)left;
        if (Num50000s > 16)
        {
            Num50000s = 16;
        }
    }

    public int CalculateChipsInStack(int smallestType, int largestType)
    {
        var count = 0;
        if (InRange(smallestType, largestType, 1))
        {
            count += Num1s;
        }

        if (InRange(smallestType, largestType, 5))
        {
            count += Num5s;
        }

        if (InRange(smallestType, largestType, 25))
        {
            count += Num25s;
        }

        if (InRange(smallestType, largestType, 100))
        {
            count += Num100s;
        }

        if (InRange(smallestType, largestType, 500))
        {
            count += Num500s;
        }

        if (InRange(smallestType, largestType, 2000))
        {
            count += Num2000s;
        }

        if (InRange(smallestType, largestType, 10000))
        {
            count += Num10000s;
        }

        if (InRange(smallestType, largestType, 50000))
        {
            count += Num50000s;
        }

        return count;
    }

    private static bool InRange(int smallestType, int largestType, int chipType) =>
        smallestType <= chipType && largestType >= chipType;
}
