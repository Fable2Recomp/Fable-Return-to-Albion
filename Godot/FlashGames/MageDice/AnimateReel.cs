using System;
using System.Collections.Generic;

using Refii.FlashGames.Framework;

using Action = Refii.FlashGames.Framework.Action;

namespace Refii.FlashGames.MageDice;

/// <summary>
/// Spins one reel from the symbol it was showing to the symbol it rolled.
/// </summary>
/// <remarks>
/// THE WHOLE SPIN IS PRECOMPUTED IN THE CONSTRUCTOR, as two parallel arrays: which timeline frame
/// to show, and which symbol to hang in it. <see cref="Run"/> then just walks them. That is what
/// makes a reel that has already decided its answer look like one still deciding.
///
/// THREE PHASES, and the frame counts are the artwork's, not chosen here: 60 start frames winding
/// up, a 28-frame loop section cycled for as long as the spin lasts, and whatever is left of the
/// clip as the settle. A new random symbol is dropped in every 8 frames of the loop, chosen so it
/// differs from its immediate neighbours — a reel that flashes the same face twice running reads
/// as a stutter rather than a spin.
///
/// THE DURATION IS QUANTISED TO WHOLE FRAMES AT 60 FPS and written back over what the caller
/// asked for, so the loop section always ends on an exact symbol boundary.
///
/// A 1:1 port of <c>AnimateReel.as</c>.
/// </remarks>
public sealed class AnimateReel : Action
{
    public const int NumStartFrames = 60;
    public const int NumLoopFrames = 28;
    public const int FramesPerImage = 8;
    public const int FramesPerSubImage = 2;

    private readonly IPubClip _clip;
    private readonly AnimatingSwitch _flitswitch;
    private readonly string? _endSound;
    private readonly List<int> _frames = new();
    private readonly List<int> _images = new();
    private readonly Framework.Audio _audio;

    private int _frame = -1;
    private bool _sound;

    public AnimateReel(
        IPubClip clip, AnimatingSwitch flitswitch, int startImageIndex, int finalImageIndex,
        string? endSound, double duration, object? group, Framework.Audio audio, IPubRandom random)
    {
        InitActionData();
        _clip = clip;
        _flitswitch = flitswitch;
        _endSound = endSound;
        _audio = audio;
        Duration = duration * 1000;
        Group = group;

        if (duration <= 0)
        {
            _sound = false;
            _frames.Add(clip.TotalFrames);
            _images.Add(finalImageIndex);
            return;
        }

        _sound = true;

        var total = (int)Math.Floor(duration * 60);
        int settle = clip.TotalFrames - NumStartFrames - NumLoopFrames;
        int loopFrames = total - settle - NumStartFrames;

        // ROUNDED UP TO A WHOLE SYMBOL, so the loop never stops half way through showing one.
        int remainder = loopFrames % FramesPerImage;
        if (remainder != 0)
        {
            loopFrames += FramesPerImage - remainder;
        }

        total = NumStartFrames + loopFrames + settle;
        int settleStart = NumStartFrames + loopFrames;
        Duration = total / 60.0 * 1000;

        int loopFirst = NumStartFrames + 1;
        int loopLast = loopFirst + NumLoopFrames;

        Resize(_frames, total);
        Resize(_images, total);

        for (var i = 0; i < NumStartFrames; i++)
        {
            _frames[i] = 1 + i;
            _images[i] = startImageIndex;
        }

        for (var i = 0; i < settle; i++)
        {
            _frames[settleStart + i] = loopLast + i;
            _images[settleStart + i] = finalImageIndex;
        }

        int cursor = loopFirst +
            ((int)Math.Floor(random.NextDouble() * (NumLoopFrames / (double)FramesPerSubImage)) *
                FramesPerSubImage);
        int shown = startImageIndex;
        int symbols = flitswitch.ReelImages.Count;

        for (var i = 0; i < loopFrames; i++)
        {
            int at = NumStartFrames + i;
            _frames[at] = cursor;
            cursor++;
            if (cursor >= loopLast)
            {
                cursor = loopFirst;
            }

            if (i % FramesPerImage == 0)
            {
                var pick = (int)Math.Floor(symbols * random.NextDouble());

                // WALKS FORWARD UNTIL IT DIFFERS from the one showing and from the symbols eight
                // frames either side. Reading past the end of the array is deliberate in the
                // shipped code and harmless: an unwritten slot compares unequal to everything.
                while (pick == shown || pick == At(_images, at - FramesPerImage) ||
                       pick == At(_images, at + FramesPerImage))
                {
                    pick++;
                    if (pick >= symbols)
                    {
                        pick = 0;
                    }
                }

                shown = pick;
            }

            _images[at] = shown;
        }
    }

    private static void Resize(List<int> list, int count)
    {
        while (list.Count < count)
        {
            list.Add(-1);
        }
    }

    private static int At(List<int> list, int index) =>
        index >= 0 && index < list.Count ? list[index] : int.MinValue;

    public static AnimateReel SpinTo(
        IPubClip clip, AnimatingSwitch flitswitch, int startImageIndex, int finalImageIndex,
        string? endSound, double duration, object? group, Framework.Audio audio, IPubRandom random) =>
        Add(new AnimateReel(
            clip, flitswitch, startImageIndex, finalImageIndex, endSound, duration, group,
            audio, random));

    /// <summary>Snaps a reel straight to a symbol, for setting the board up.</summary>
    public static AnimateReel Instant(
        IPubClip clip, AnimatingSwitch flitswitch, int finalImageIndex, string? endSound,
        object? group, Framework.Audio audio, IPubRandom random) =>
        Add(new AnimateReel(
            clip, flitswitch, finalImageIndex, finalImageIndex, endSound, 0, group, audio, random));

    public override bool Run(double ticks, object? callbackData, bool groupActive)
    {
        // THE PLAYER CAN HURRY A SPIN by holding A, which speeds the clock rather than skipping
        // frames — the reel still walks every symbol, just faster.
        if (MageDice.SpeedAnimation && MageDice.Game is not null)
        {
            ticks *= MageDice.Game.ReelSpeedBoost;
        }

        if (!IncrementTime(ticks))
        {
            return false;
        }

        bool done = PercentDone >= 1;
        int count = _frames.Count;
        var at = (int)Math.Floor(PercentDone * count);
        if (at >= count)
        {
            at = count - 1;
        }

        if (at != _frame && at >= 0)
        {
            _frame = at;
            _clip.GotoAndStop(_frames[_frame]);

            // THE FRAME CARRIES AN EMPTY `image` SLOT and the symbol is hung in it here. Stepping
            // the timeline rebuilds that slot, so `img` being absent is how the code knows this is
            // a frame it has not dressed yet.
            IPubClip? image = _clip.GetChild("image");
            if (image is not null && image.GetChild("img") is null)
            {
                int symbol = _images[_frame];
                image.AttachMovie(
                    symbol >= 0 ? _flitswitch.ReelImages[symbol] : "engraving", "img", 0);
            }

            if (_sound)
            {
                if (_frame > _frames.Count - 16)
                {
                    _sound = false;
                    _audio.PlaySound("M_flitSpinEnd");
                }
                else if (_frame > NumStartFrames)
                {
                    MageDice.RequestAudioTick = true;
                }
            }
        }

        if (done && _endSound is not null)
        {
            _audio.PlaySound(_endSound);
        }

        return done;
    }
}
