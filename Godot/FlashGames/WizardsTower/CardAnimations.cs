using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed class CardClipData
{
    public bool Stacked { get; set; }

    public bool Bonus { get; set; }

    public bool Jackpot { get; set; }

    public bool Idle { get; set; }

    public int CardValue { get; set; }
}

public static class Cards
{
    private static readonly Dictionary<string, CardClipData> Data = new();
    private static readonly Dictionary<string, CardRef> Refs = new();

    public static IPubClip? CardClips { get; set; }

    public static CardClipData Get(string cardName)
    {
        if (!Data.TryGetValue(cardName, out CardClipData? data))
        {
            data = new CardClipData { CardValue = -1 };
            Data[cardName] = data;
        }

        return data;
    }

    public static CardRef Ref(string cardName)
    {
        if (!Refs.TryGetValue(cardName, out CardRef? reference))
        {
            reference = new CardRef(cardName);
            Refs[cardName] = reference;
        }

        return reference;
    }

    public static void Clear()
    {
        Data.Clear();
        Refs.Clear();
    }

    public static string SymbolFor(int cardValue) => cardValue switch
    {
        < 0 => "CardBack",
        0 => "CardKnight",
        _ => "Card" + cardValue,
    };

    public static IPubClip? Swap(IPubClip? container, string cardName, string symbol)
    {
        IPubClip? old = container?.GetChild(cardName);
        if (container is null || old is null)
        {
            return null;
        }

        int depth = old.GetDepth();
        float x = old.X;
        float y = old.Y;
        float xscale = old.XScale;
        float yscale = old.YScale;
        float rotation = old.Rotation;
        old.RemoveMovieClip();

        IPubClip? swapped = container.AttachMovie(symbol, cardName, depth);
        if (swapped is null)
        {
            return null;
        }

        swapped.X = x;
        swapped.Y = y;
        swapped.XScale = xscale;
        swapped.YScale = yscale;
        swapped.Rotation = rotation;
        return swapped;
    }
}

public sealed class DealCard : Action
{
    public double? StartX { get; set; }

    public double? StartY { get; set; }

    public double FinalX { get; set; }

    public double FinalY { get; set; }

    public double? StartAngle { get; set; }

    public double FinalAngle { get; set; }

    public double? FinalDepth { get; set; }

    public double? Speed { get; set; }

    public string? CardName { get; set; }

    public int CardValue { get; set; }

    public bool Flip { get; set; }

    public double? BaseXScale { get; set; }

    public double? BaseYScale { get; set; }

    public double Red { get; set; }

    public double Green { get; set; }

    public double Blue { get; set; }

    public static new void Preallocate(int size) => ActionPool<DealCard>.Preallocate(size);

    public static DealCard ToBoard(
        IPubClip thingToDeal, string cardName, int cardValue, double finalX, double finalY,
        double finalAngle, double finalDepth, double speed, bool flip,
        double red, double green, double blue, object? group = null)
    {
        DealCard deal = ActionPool<DealCard>.GetFromArray();
        deal.Init(
            thingToDeal, finalX, finalY, finalAngle, finalDepth, cardName, cardValue, speed, flip,
            red, green, blue, group);
        return Add(deal);
    }

    public void Init(
        IPubClip thingToDeal, double finalX, double finalY, double finalAngle, double? finalDepth,
        string cardName, int cardValue, double? speed, bool flip,
        double red, double green, double blue, object? group)
    {
        InitActionData();
        DebugString = "DealCard";
        CallbackData = thingToDeal;
        StartX = null;
        StartY = null;
        FinalX = finalX;
        FinalY = finalY;
        Group = group;
        Speed = speed;
        StartAngle = null;
        FinalAngle = finalAngle;
        FinalDepth = finalDepth;
        CardName = cardName;
        CardValue = cardValue;
        AnimStyle = StyleEaseIn;
        Duration = 1000 * MoveTo.CalculateDistance(
            thingToDeal.X, thingToDeal.Y, 0, finalX, finalY, 0) / (speed ?? 1);
        Blocking = true;
        Flip = flip;
        BaseXScale = null;
        BaseYScale = null;
        Red = red;
        Green = green;
        Blue = blue;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (callbackData is not IPubClip clip)
        {
            return true;
        }

        if (StartX is null || StartY is null || StartAngle is null)
        {
            StartX = clip.X;
            StartY = clip.Y;
            StartAngle = clip.Rotation;
            StartAngle = CardTurn.ShortestArc(StartAngle.Value, FinalAngle);

            if (Speed is not null)
            {
                Duration = 1000 * MoveTo.CalculateDistance(
                    StartX.Value, StartY.Value, 0, FinalX, FinalY, 0) / Speed.Value;
            }

            WizardsTower.DebugToScreen("W_cardDeal");
            WizardsTower.Sound?.PlaySound("W_cardDeal");
        }

        clip.X = (float)(StartX!.Value + (PercentDone * (FinalX - StartX.Value)));
        clip.Y = (float)(StartY!.Value + (PercentDone * (FinalY - StartY.Value)));
        clip.Rotation = (float)(StartAngle!.Value + (PercentDone * (FinalAngle - StartAngle.Value)));

        if (PercentDone >= 0.2 && FinalDepth is not null)
        {
            clip.SwapDepths((int)FinalDepth.Value);
            FinalDepth = null;
        }

        if (PercentDone >= 0.5 && CardName is not null)
        {
            IPubClip? swapped = Cards.Swap(Cards.CardClips, CardName, Cards.SymbolFor(CardValue));
            CardClipData data = Cards.Get(CardName);
            data.Stacked = false;
            data.Bonus = false;
            data.Jackpot = false;
            data.CardValue = CardValue;
            CardName = null;

            if (swapped is not null)
            {
                clip = swapped;
                CallbackData = swapped;
                if (Red != 0 || Green != 0 || Blue != 0)
                {
                    double redMult = IsMultiplier(Red) ? Red : 1;
                    double greenMult = IsMultiplier(Green) ? Green : 1;
                    double blueMult = IsMultiplier(Blue) ? Blue : 1;
                    Red = IsMultiplier(Red) ? 0 : Red;
                    Green = IsMultiplier(Green) ? 0 : Green;
                    Blue = IsMultiplier(Blue) ? 0 : Blue;
                    clip.SetTint(
                        (float)redMult, (float)greenMult, (float)blueMult,
                        (float)Red, (float)Green, (float)Blue);
                }
            }
        }

        BaseXScale ??= clip.XScale;
        BaseYScale ??= clip.YScale;

        double along = PercentDone <= 0.5 ? PercentDone : 1 - PercentDone;
        clip.XScale = (float)(BaseXScale.Value
            + (along * 2 * ((1.25 * BaseXScale.Value) - BaseXScale.Value)));
        clip.YScale = (float)(BaseYScale.Value
            + (along * 2 * (((Flip ? 0.25 : 1.25) * BaseYScale.Value) - BaseYScale.Value)));

        return PercentDone == 1;
    }

    private static bool IsMultiplier(double value) => value <= 2 && value > 0;

    public override void Destroy()
    {
        base.Destroy();
        StartX = null;
        StartY = null;
        Speed = null;
        StartAngle = null;
        CardName = null;
    }
}

public sealed class SweepCard : Action
{
    public double? StartX { get; set; }

    public double? StartY { get; set; }

    public double FinalX { get; set; }

    public double FinalY { get; set; }

    public double? StartAngle { get; set; }

    public double FinalAngle { get; set; }

    public double? Speed { get; set; }

    public string? CardName { get; set; }

    public static new void Preallocate(int size) => ActionPool<SweepCard>.Preallocate(size);

    public static SweepCard ToDeck(
        IPubClip thingToSweep, string cardName, double finalX, double finalY, double finalAngle,
        double speed, object? group = null)
    {
        SweepCard sweep = ActionPool<SweepCard>.GetFromArray();
        sweep.Init(thingToSweep, finalX, finalY, finalAngle, cardName, speed, group);
        return Add(sweep);
    }

    public void Init(
        IPubClip thingToSweep, double finalX, double finalY, double finalAngle, string cardName,
        double? speed, object? group)
    {
        InitActionData();
        DebugString = "SweepCard";
        CallbackData = thingToSweep;
        StartX = null;
        StartY = null;
        FinalX = finalX;
        FinalY = finalY;
        Group = group;
        Speed = speed;
        StartAngle = null;
        FinalAngle = finalAngle;
        CardName = cardName;
        AnimStyle = StyleEaseOut;
        Duration = 1000 * MoveTo.CalculateDistance(
            thingToSweep.X, thingToSweep.Y, 0, finalX, finalY, 0) / (speed ?? 1);
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (callbackData is not IPubClip clip)
        {
            return true;
        }

        if (StartX is null || StartY is null || StartAngle is null)
        {
            StartX = clip.X;
            StartY = clip.Y;
            StartAngle = CardTurn.ShortestArc(clip.Rotation, FinalAngle);

            if (Speed is not null)
            {
                Duration = 1000 * MoveTo.CalculateDistance(
                    StartX.Value, StartY.Value, 0, FinalX, FinalY, 0) / Speed.Value;
            }
        }

        clip.X = (float)(StartX!.Value + (PercentDone * (FinalX - StartX.Value)));
        clip.Y = (float)(StartY!.Value + (PercentDone * (FinalY - StartY.Value)));
        clip.Rotation = (float)(StartAngle!.Value + (PercentDone * (FinalAngle - StartAngle.Value)));

        if (PercentDone >= 0.5 && CardName is not null)
        {
            IPubClip? swapped = Cards.Swap(Cards.CardClips, CardName, "CardBack");
            if (swapped is not null)
            {
                CallbackData = swapped;
            }

            CardClipData data = Cards.Get(CardName);
            data.Stacked = false;
            data.Bonus = false;
            data.Jackpot = false;
            data.CardValue = -1;
            CardName = null;
        }

        return PercentDone == 1;
    }

    public override void Destroy()
    {
        base.Destroy();
        StartX = null;
        StartY = null;
        Speed = null;
        StartAngle = null;
        CardName = null;
    }
}

public sealed class SwitchCard : Action
{
    public string? CardName { get; set; }

    public int CardValue { get; set; }

    public string CardAnimation { get; set; } = string.Empty;

    public static new void Preallocate(int size) => ActionPool<SwitchCard>.Preallocate(size);

    public static SwitchCard Synched(
        IPubClip thingToSwitch, string cardName, int cardValue, string cardAnimation, Action synch,
        object? group = null)
    {
        SwitchCard switchCard = ActionPool<SwitchCard>.GetFromArray();
        switchCard.Init(thingToSwitch, cardName, cardValue, cardAnimation, null, group);
        switchCard.SynchedAction = synch;
        return Add(switchCard);
    }

    public static SwitchCard Delayed(
        IPubClip thingToSwitch, string cardName, int cardValue, string cardAnimation, double delay,
        object? group = null)
    {
        SwitchCard switchCard = ActionPool<SwitchCard>.GetFromArray();
        switchCard.Init(thingToSwitch, cardName, cardValue, cardAnimation, 0, group);
        SwitchCard added = Add(switchCard);
        added.Time = -delay * 1000;
        return added;
    }

    public static SwitchCard Instant(
        IPubClip thingToSwitch, string cardName, int cardValue, string cardAnimation,
        object? group = null)
    {
        SwitchCard switchCard = ActionPool<SwitchCard>.GetFromArray();
        switchCard.Init(thingToSwitch, cardName, cardValue, cardAnimation, 0, group);
        return Add(switchCard);
    }

    public void Init(
        IPubClip thingToSwitch, string cardName, int cardValue, string cardAnimation,
        double? duration, object? group)
    {
        InitActionData();
        DebugString = "SwitchCard";
        CallbackData = thingToSwitch;
        CardName = cardName;
        CardValue = cardValue;
        CardAnimation = cardAnimation;
        Duration = duration is null ? null : duration.Value * 1000;
        Group = group;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (PercentDone != 1)
        {
            return false;
        }

        if (callbackData is not IPubClip container || CardName is null)
        {
            return true;
        }

        CardClipData data = Cards.Get(CardName);
        IPubClip? swapped = Cards.Swap(
            container, CardName, Cards.SymbolFor(CardValue) + CardAnimation);
        data.CardValue = CardValue;

        if (swapped is not null && CardAnimation == "Bonus")
        {
            swapped.GotoAndPlay(
                (int)Math.Floor((WizardsTower.Chance?.NextDouble() ?? 0) * swapped.TotalFrames) + 1);
        }

        return true;
    }
}

public static class CardTurn
{
    public static double ShortestArc(double startAngle, double finalAngle)
    {
        var direction = 0;
        double delta = finalAngle - startAngle;
        if (delta > 180)
        {
            delta -= 360;
        }

        if (delta < -180)
        {
            delta += 360;
        }

        if (delta > 0)
        {
            direction = 1;
        }
        else if (delta < 0)
        {
            direction = -1;
        }

        while (startAngle > finalAngle && direction > 0)
        {
            startAngle -= 360;
        }

        while (startAngle < finalAngle && direction < 0)
        {
            startAngle += 360;
        }

        return startAngle;
    }
}

public sealed class CardRef : IPubClip
{
    private readonly string _name;

    public CardRef(string name) => _name = name;

    private IPubClip? Live => Cards.CardClips?.GetChild(_name);

    public float X
    {
        get => Live?.X ?? 0f;
        set { if (Live is { } c) { c.X = value; } }
    }

    public float Y
    {
        get => Live?.Y ?? 0f;
        set { if (Live is { } c) { c.Y = value; } }
    }

    public float Alpha
    {
        get => Live?.Alpha ?? 0f;
        set { if (Live is { } c) { c.Alpha = value; } }
    }

    public float XScale
    {
        get => Live?.XScale ?? 100f;
        set { if (Live is { } c) { c.XScale = value; } }
    }

    public float YScale
    {
        get => Live?.YScale ?? 100f;
        set { if (Live is { } c) { c.YScale = value; } }
    }

    public float Rotation
    {
        get => Live?.Rotation ?? 0f;
        set { if (Live is { } c) { c.Rotation = value; } }
    }

    public bool Visible
    {
        get => Live?.Visible ?? false;
        set { if (Live is { } c) { c.Visible = value; } }
    }

    public int CurrentFrame => Live?.CurrentFrame ?? 1;

    public int TotalFrames => Live?.TotalFrames ?? 1;

    public string Text
    {
        get => Live?.Text ?? string.Empty;
        set { if (Live is { } c) { c.Text = value; } }
    }

    public float Height => Live?.Height ?? 0f;

    public float TextHeight => Live?.TextHeight ?? 0f;

    public string Name => _name;

    public void GotoAndStop(int frame) => Live?.GotoAndStop(frame);

    public void GotoAndStop(string label) => Live?.GotoAndStop(label);

    public void GotoAndPlay(int frame) => Live?.GotoAndPlay(frame);

    public void Play() => Live?.Play();

    public void Stop() => Live?.Stop();

    public IPubClip? GetChild(string name) => Live?.GetChild(name);

    public IPubClip? AttachMovie(string exportName, string instanceName, int depth) =>
        Live?.AttachMovie(exportName, instanceName, depth);

    public IPubClip CreateEmptyMovieClip(string name, int depth) =>
        Live?.CreateEmptyMovieClip(name, depth) ?? this;

    public int GetDepth() => Live?.GetDepth() ?? 0;

    public void RemoveMovieClip() => Live?.RemoveMovieClip();

    public void SwapDepths(int depth) => Live?.SwapDepths(depth);

    public int GetNextHighestDepth() => Live?.GetNextHighestDepth() ?? 0;

    public void SetTint(
        float redMultiply, float greenMultiply, float blueMultiply,
        float redOffset, float greenOffset, float blueOffset) =>
        Live?.SetTint(redMultiply, greenMultiply, blueMultiply, redOffset, greenOffset, blueOffset);

    public void SetTextFormat(PubTextFormat format) => Live?.SetTextFormat(format);
}
