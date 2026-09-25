using Godot;

namespace Refii.FlashGames.Framework;

/// <summary>
/// Ticks a pub game the way the guest's movie pump does, so every host drives it identically.
/// </summary>
/// <remarks>
/// <c>sub_82EF2708</c> advances only once at least 15 ms have passed since the last advance
/// (0x82ef281c). Each advance samples the pad (<c>sub_82EF1650</c>): a newly pressed Start on a
/// retail game calls <c>pubGame.ActivateShimPause</c>, then <c>pubGame.SetPadValue</c>; then the
/// movie advances, which is <c>PubGame::OnEnterFrame</c>. Coming back from a pause
/// (<c>sub_82EF2300</c>) restarts the clock, samples the pad and calls
/// <c>pubGame.UpdatePadAfterPause</c>, so a button held through the pause is not a new press;
/// a pause here is the host not being processed (the scene tree paused).
/// </remarks>
public sealed class PubGameDriver
{
    public const double MinimumAdvanceMs = 15;

    private double _elapsed;
    private int _lastButtons;
    private ulong _lastProcessFrame;

    /// <summary>Advances <paramref name="game"/> if it is due; returns the milliseconds advanced, or 0.</summary>
    public double Advance(PubGame game, double deltaSeconds, Node host)
    {
        ulong frame = Engine.GetProcessFrames();
        bool resumed = _lastProcessFrame != 0 && frame > _lastProcessFrame + 1;
        _lastProcessFrame = frame;
        if (resumed)
        {
            _elapsed = 0;
            SamplePad(game, host);
            game.UpdatePadAfterPause();
            return 0;
        }

        _elapsed += deltaSeconds * 1000.0;
        if (_elapsed < MinimumAdvanceMs)
        {
            return 0;
        }

        double ticks = _elapsed;
        _elapsed = 0;
        SamplePad(game, host);
        game.OnEnterFrame(ticks);
        return ticks;
    }

    private void SamplePad(PubGame game, Node host)
    {
        Window? window = host.IsInsideTree() ? host.GetWindow() : null;
        PubGameInput.Sample sample = PubGameInput.Read(window is null || window.HasFocus());

        int pressed = (_lastButtons ^ sample.Buttons) & sample.Buttons;
        _lastButtons = sample.Buttons;
        if (game.RetailGame && (pressed & GamePad.Start) != 0)
        {
            game.ActivateShimPause();
        }

        PubGameInput.Publish(game, sample);
    }
}
