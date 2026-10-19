using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

namespace Refii.FlashGames.WizardsTower;

public sealed class MoveTo : Action
{
    public double? StartX { get; set; }

    public double? StartY { get; set; }

    public double? StartDepth { get; set; }

    public double FinalX { get; set; }

    public double FinalY { get; set; }

    public double? FinalDepth { get; set; }

    public double? Speed { get; set; }

    public static new void Preallocate(int size) => ActionPool<MoveTo>.Preallocate(size);

    public static MoveTo To(
        IPubClip thingToMove, double finalX, double finalY, double? finalDepth, double? duration,
        object? group = null)
    {
        MoveTo move = ActionPool<MoveTo>.GetFromArray();
        move.Init(thingToMove, finalX, finalY, finalDepth, duration, group);
        return Add(move);
    }

    public static MoveTo AtSpeed(
        IPubClip thingToMove, double finalX, double finalY, double? finalDepth, double speed,
        object? group = null)
    {
        MoveTo move = To(thingToMove, finalX, finalY, finalDepth, 0, group);
        move.Speed = speed;
        return move;
    }

    public static MoveTo Linear(
        IPubClip thingToMove, double finalX, double finalY, double speed, int animstyle,
        object? group = null)
    {
        MoveTo move = ActionPool<MoveTo>.GetFromArray();
        move.Init(thingToMove, finalX, finalY, null, null, group);
        MoveTo added = Add(move);
        added.Speed = speed;
        added.AnimStyle = animstyle;
        added.Duration = 1000 * CalculateDistance(
            thingToMove.X, thingToMove.Y, 0, finalX, finalY, 0) / speed;
        return added;
    }

    public static MoveTo Timed(
        IPubClip thingToMove, double finalX, double finalY, double duration, int animstyle,
        object? group = null)
    {
        MoveTo move = To(thingToMove, finalX, finalY, null, duration, group);
        move.AnimStyle = animstyle;
        return move;
    }

    public static MoveTo Instant(
        IPubClip thingToMove, double finalX, double finalY, object? group = null) =>
        To(thingToMove, finalX, finalY, null, 0, group);

    public static MoveTo Synched(
        IPubClip thingToMove, double finalX, double finalY, Action synch, int animstyle,
        object? group = null)
    {
        MoveTo move = ActionPool<MoveTo>.GetFromArray();
        move.Init(thingToMove, finalX, finalY, null, null, group);
        move.SynchedAction = synch;
        move.AnimStyle = animstyle;
        return Add(move);
    }

    public static MoveTo Depth(IPubClip thingToMove, double finalDepth, object? group = null) =>
        To(thingToMove, 0, 0, finalDepth, 0, group);

    public static MoveTo DepthDelayed(
        IPubClip thingToMove, double finalDepth, double delay, object? group = null)
    {
        MoveTo move = Depth(thingToMove, finalDepth, group);
        move.Time = -delay * 1000;
        return move;
    }

    public static double CalculateDistance(
        double x1, double y1, double d1, double x2, double y2, double d2) =>
        Math.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    public void Init(
        IPubClip thingToMove, double finalX, double finalY, double? finalDepth, double? duration,
        object? group)
    {
        InitActionData();
        DebugString = "MoveTo";
        CallbackData = thingToMove;
        StartX = null;
        StartY = null;
        StartDepth = null;
        FinalX = finalX;
        FinalY = finalY;
        FinalDepth = finalDepth;
        Duration = (duration ?? 0) * 1000;
        Group = group;
        Speed = null;
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

        if (StartX is null || StartY is null || StartDepth is null)
        {
            StartX = clip.X;
            StartY = clip.Y;
            StartDepth = clip.GetDepth();

            if (Speed is not null && Speed.Value != 0)
            {
                Duration = 1000 * CalculateDistance(
                    StartX.Value, StartY.Value, StartDepth.Value,
                    FinalX, FinalY, FinalDepth ?? StartDepth.Value) / Speed.Value;
            }
        }

        if (FinalDepth is null)
        {
            clip.X = (float)(StartX.Value + (PercentDone * (FinalX - StartX.Value)));
            clip.Y = (float)(StartY.Value + (PercentDone * (FinalY - StartY.Value)));
        }
        else
        {
            clip.SwapDepths(
                (int)(StartDepth.Value + (PercentDone * (FinalDepth.Value - StartDepth.Value))));
        }

        return PercentDone == 1;
    }
}

public sealed class Rotate : Action
{
    public double? StartAngle { get; set; }

    public double FinalAngle { get; set; }

    public int ForcedDirection { get; set; }

    public static new void Preallocate(int size) => ActionPool<Rotate>.Preallocate(size);

    public static Rotate To(
        IPubClip thingToRotate, double finalAngle, double? duration, object? group = null)
    {
        Rotate rotate = ActionPool<Rotate>.GetFromArray();
        rotate.Init(thingToRotate, finalAngle, duration, group);
        return Add(rotate);
    }

    public static Rotate Timed(
        IPubClip thingToRotate, double finalAngle, double duration, int animstyle,
        object? group = null)
    {
        Rotate rotate = To(thingToRotate, finalAngle, duration, group);
        rotate.AnimStyle = animstyle;
        return rotate;
    }

    public static Rotate TimedClockwise(
        IPubClip thingToRotate, double finalAngle, double duration, int animstyle,
        object? group = null)
    {
        Rotate rotate = Timed(thingToRotate, finalAngle, duration, animstyle, group);
        rotate.ForcedDirection = 1;
        return rotate;
    }

    public static Rotate TimedCounter(
        IPubClip thingToRotate, double finalAngle, double duration, int animstyle = StyleLinear,
        object? group = null)
    {
        Rotate rotate = Timed(thingToRotate, finalAngle, duration, animstyle, group);
        rotate.ForcedDirection = -1;
        return rotate;
    }

    public static Rotate Synched(
        IPubClip thingToRotate, double finalAngle, Action synch, int animstyle,
        object? group = null)
    {
        Rotate rotate = ActionPool<Rotate>.GetFromArray();
        rotate.Init(thingToRotate, finalAngle, null, group);
        rotate.SynchedAction = synch;
        rotate.AnimStyle = animstyle;
        return Add(rotate);
    }

    public static Rotate SynchedClockwise(
        IPubClip thingToRotate, double finalAngle, Action synch, int animstyle,
        object? group = null)
    {
        Rotate rotate = Synched(thingToRotate, finalAngle, synch, animstyle, group);
        rotate.ForcedDirection = 1;
        return rotate;
    }

    public static Rotate SynchedCounter(
        IPubClip thingToRotate, double finalAngle, Action synch, int animstyle,
        object? group = null)
    {
        Rotate rotate = Synched(thingToRotate, finalAngle, synch, animstyle, group);
        rotate.ForcedDirection = -1;
        return rotate;
    }

    public static Rotate Instant(
        IPubClip thingToRotate, double finalAngle, object? group = null) =>
        To(thingToRotate, finalAngle, 0, group);

    public void Init(IPubClip thingToRotate, double finalAngle, double? duration, object? group)
    {
        InitActionData();
        DebugString = "Rotate";
        CallbackData = thingToRotate;
        StartAngle = null;
        FinalAngle = finalAngle;
        Duration = (duration ?? 0) * 1000;
        Group = group;
        ForcedDirection = 0;
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

        if (StartAngle is null)
        {
            StartAngle = clip.Rotation;

            if (ForcedDirection == 0)
            {
                double delta = FinalAngle - StartAngle.Value;
                if (delta > 180)
                {
                    delta -= 360;
                }

                if (delta < -180)
                {
                    delta += 360;
                }

                ForcedDirection = delta > 0 ? 1 : delta < 0 ? -1 : 0;
            }

            while (StartAngle > FinalAngle && ForcedDirection > 0)
            {
                StartAngle -= 360;
            }

            while (StartAngle < FinalAngle && ForcedDirection < 0)
            {
                StartAngle += 360;
            }
        }

        clip.Rotation = (float)(StartAngle.Value + (PercentDone * (FinalAngle - StartAngle.Value)));
        return PercentDone == 1;
    }
}

public sealed class Scale : Action
{
    public double? StartXScale { get; set; }

    public double? StartYScale { get; set; }

    public double? FinalXScale { get; set; }

    public double? FinalYScale { get; set; }

    public static new void Preallocate(int size) => ActionPool<Scale>.Preallocate(size);

    public static Scale By(
        IPubClip thingToScale, double finalScale, double? duration, object? group = null)
    {
        Scale scale = ActionPool<Scale>.GetFromArray();
        scale.Init(thingToScale, finalScale, duration, group);
        return Add(scale);
    }

    public static Scale Exact(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null)
    {
        Scale scale = By(thingToScale, finalScale, duration, group);
        scale.AnimStyle = animstyle;
        return scale;
    }

    public static Scale Up(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null) =>
        Exact(thingToScale, finalScale, duration, animstyle, group);

    public static Scale Down(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null) =>
        Exact(thingToScale, 1 / finalScale, duration, animstyle, group);

    public static Scale UpSynched(
        IPubClip thingToScale, double finalScale, Action synch, int animstyle,
        object? group = null)
    {
        Scale scale = ActionPool<Scale>.GetFromArray();
        scale.Init(thingToScale, finalScale, null, group);
        scale.AnimStyle = animstyle;
        scale.SynchedAction = synch;
        return Add(scale);
    }

    public static Scale DownSynched(
        IPubClip thingToScale, double finalScale, Action synch, int animstyle,
        object? group = null) =>
        UpSynched(thingToScale, 1 / finalScale, synch, animstyle, group);

    public static Scale Thick(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null)
    {
        Scale scale = Exact(thingToScale, finalScale, duration, animstyle, group);
        scale.FinalYScale = null;
        return scale;
    }

    public static Scale Thin(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null)
    {
        Scale scale = Exact(thingToScale, 1 / finalScale, duration, animstyle, group);
        scale.FinalYScale = null;
        return scale;
    }

    public static Scale Tall(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null)
    {
        Scale scale = Exact(thingToScale, finalScale, duration, animstyle, group);
        scale.FinalXScale = null;
        return scale;
    }

    public static Scale Short(
        IPubClip thingToScale, double finalScale, double duration, int animstyle,
        object? group = null)
    {
        Scale scale = Exact(thingToScale, 1 / finalScale, duration, animstyle, group);
        scale.FinalXScale = null;
        return scale;
    }

    public static Scale Instant(
        IPubClip thingToScale, double finalScale, object? group = null) =>
        By(thingToScale, finalScale, 0, group);

    public void Init(IPubClip thingToScale, double finalScale, double? duration, object? group)
    {
        InitActionData();
        DebugString = "Scale";
        CallbackData = thingToScale;
        FinalXScale = finalScale;
        FinalYScale = finalScale;
        StartXScale = null;
        StartYScale = null;
        Duration = (duration ?? 0) * 1000;
        Group = group;
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

        if (StartXScale is null || StartYScale is null)
        {
            StartXScale = clip.XScale;
            StartYScale = clip.YScale;
            FinalXScale *= StartXScale.Value;
            FinalYScale *= StartYScale.Value;
        }

        if (FinalXScale is not null)
        {
            clip.XScale = (float)(StartXScale.Value
                + (PercentDone * (FinalXScale.Value - StartXScale.Value)));
        }

        if (FinalYScale is not null)
        {
            clip.YScale = (float)(StartYScale.Value
                + (PercentDone * (FinalYScale.Value - StartYScale.Value)));
        }

        return PercentDone == 1;
    }
}

public sealed class Tint : Action
{
    public double StartRedMult { get; set; }

    public double StartGreenMult { get; set; }

    public double StartBlueMult { get; set; }

    public double FinalRedMult { get; set; }

    public double FinalGreenMult { get; set; }

    public double FinalBlueMult { get; set; }

    public double StartRedTint { get; set; }

    public double StartGreenTint { get; set; }

    public double StartBlueTint { get; set; }

    public double FinalRedTint { get; set; }

    public double FinalGreenTint { get; set; }

    public double FinalBlueTint { get; set; }

    public static new void Preallocate(int size) => ActionPool<Tint>.Preallocate(size);

    public static Tint Apply(
        IPubClip thingToTint, double red, double green, double blue, double? duration,
        object? group = null)
    {
        Tint tint = ActionPool<Tint>.GetFromArray();
        tint.Init(thingToTint, red, green, blue, duration, group);
        return Add(tint);
    }

    private static bool IsMultiplier(double value) => value <= 2 && value > 0;

    public void Init(
        IPubClip thingToTint, double finalRedTint, double finalGreenTint, double finalBlueTint,
        double? duration, object? group)
    {
        InitActionData();
        DebugString = "Tint";
        CallbackData = thingToTint;

        StartRedTint = 0;
        StartGreenTint = 0;
        StartBlueTint = 0;
        FinalRedTint = IsMultiplier(finalRedTint) ? 0 : finalRedTint;
        FinalGreenTint = IsMultiplier(finalGreenTint) ? 0 : finalGreenTint;
        FinalBlueTint = IsMultiplier(finalBlueTint) ? 0 : finalBlueTint;

        StartRedMult = 1;
        StartGreenMult = 1;
        StartBlueMult = 1;
        FinalRedMult = IsMultiplier(finalRedTint) ? finalRedTint : 1;
        FinalGreenMult = IsMultiplier(finalGreenTint) ? finalGreenTint : 1;
        FinalBlueMult = IsMultiplier(finalBlueTint) ? finalBlueTint : 1;

        Duration = (duration ?? 0) * 1000;
        Group = group;
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

        clip.SetTint(
            (float)(StartRedMult + (PercentDone * (FinalRedMult - StartRedMult))),
            (float)(StartGreenMult + (PercentDone * (FinalGreenMult - StartGreenMult))),
            (float)(StartBlueMult + (PercentDone * (FinalBlueMult - StartBlueMult))),
            (float)(StartRedTint + (PercentDone * (FinalRedTint - StartRedTint))),
            (float)(StartGreenTint + (PercentDone * (FinalGreenTint - StartGreenTint))),
            (float)(StartBlueTint + (PercentDone * (FinalBlueTint - StartBlueTint))));

        return PercentDone == 1;
    }
}

public sealed class SetText : Action
{
    public static Localizer? Loc { get; set; }

    public string? Text { get; set; }

    public PubTextFormat? FormatText { get; set; }

    public bool ReplaceText { get; set; }

    public bool InsertText { get; set; }

    public bool LocalizeFirst { get; set; }

    public Dictionary<string, string>? LocVariables { get; set; }

    public static new void Preallocate(int size) => ActionPool<SetText>.Preallocate(size);

    public static SetText Replace(IPubClip field, string text, object? group = null)
    {
        SetText set = ActionPool<SetText>.GetFromArray();
        set.Init(field, text, replace: true, insert: false, group);
        return Add(set);
    }

    public static SetText Append(IPubClip field, string text, object? group = null)
    {
        SetText set = ActionPool<SetText>.GetFromArray();
        set.Init(field, text, replace: false, insert: false, group);
        return Add(set);
    }

    public static SetText Insert(IPubClip field, string text, object? group = null)
    {
        SetText set = ActionPool<SetText>.GetFromArray();
        set.Init(field, text, replace: false, insert: true, group);
        return Add(set);
    }

    public static SetText Localized(
        IPubClip field, string id, Dictionary<string, string>? variables, object? group = null)
    {
        SetText set = Replace(field, id, group);
        set.LocalizeFirst = true;
        set.LocVariables = variables;
        return set;
    }

    public static SetText Delayed(
        IPubClip field, string text, double delay, object? group = null)
    {
        SetText set = Replace(field, text, group);
        set.Time = -delay * 1000;
        return set;
    }

    public static SetText AddTo(IPubClip field, string text, object? group = null) =>
        Append(field, text, group);

    public static SetText Color(IPubClip field, int color, object? group = null)
    {
        SetText set = ActionPool<SetText>.GetFromArray();
        set.Init(field, null, replace: false, insert: false, group);
        set.FormatText = new PubTextFormat { Color = color };
        return Add(set);
    }

    public static SetText LocWithVars(
        IPubClip field, string id, Dictionary<string, string>? locvars, object? group = null)
    {
        SetText set = Replace(field, id, group);
        set.LocVariables = locvars;
        set.LocalizeFirst = true;
        return set;
    }

    public static SetText LocInsert(IPubClip field, string id, object? group = null)
    {
        SetText set = Insert(field, id, group);
        set.LocalizeFirst = true;
        return set;
    }

    public static SetText LocReplace(IPubClip field, string id, object? group = null)
    {
        SetText set = Replace(field, id, group);
        set.LocalizeFirst = true;
        return set;
    }

    public static SetText LocDelayed(
        IPubClip field, string id, double delay, object? group = null)
    {
        SetText set = Delayed(field, id, delay, group);
        set.LocalizeFirst = true;
        return set;
    }

    public static SetText LocAddTo(IPubClip field, string id, object? group = null)
    {
        SetText set = Append(field, id, group);
        set.LocalizeFirst = true;
        return set;
    }

    public void Init(IPubClip field, string? text, bool replace, bool insert, object? group)
    {
        InitActionData();
        DebugString = "SetText";
        CallbackData = field;
        Text = text;
        FormatText = null;
        ReplaceText = replace;
        InsertText = insert;
        LocalizeFirst = false;
        LocVariables = null;
        Group = group;
    }

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        if (!IncrementTime(ticks))
        {
            return false;
        }

        if (callbackData is not IPubClip field)
        {
            return true;
        }

        if (!LocalizeFirst || Loc is null || Text?.Length == 0)
        {
            if (FormatText is not null)
            {
                field.SetTextFormat(FormatText);
            }
            else if (ReplaceText)
            {
                field.Text = Text ?? string.Empty;
            }
            else if (InsertText)
            {
                field.Text = Text + field.Text;
            }
            else
            {
                field.Text += Text;
            }

            return PercentDone == 1;
        }

        if (Text is not null && FormatText is null)
        {
            FormatText = Loc.GetTextFormat(Text);
        }

        if (FormatText is not null)
        {
            field.SetTextFormat(FormatText);
        }

        if (Text is not null)
        {
            string value = Loc.LocalizeString(Text, LocVariables);
            if (ReplaceText)
            {
                field.Text = value;
            }
            else if (InsertText)
            {
                field.Text = value + field.Text;
            }
            else
            {
                field.Text += value;
            }
        }

        return PercentDone == 1;
    }
}
