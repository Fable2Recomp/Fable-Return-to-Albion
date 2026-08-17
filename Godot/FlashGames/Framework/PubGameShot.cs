using Godot;

namespace Refii.FlashGames.Framework;

/// <summary>
/// Saves what a pub game's viewport actually drew, then quits.
/// </summary>
/// <remarks>
/// THE ONLY WAY TO CHECK THESE GAMES WITHOUT A PERSON WATCHING. Everything else a host can report —
/// that the clips exist, that they are visible, that the numbers are right — was all true of a
/// board that was rendering entirely behind an opaque black rectangle, and stayed true for as long
/// as it took to notice. A picture settles it in one run.
///
/// FROM INSIDE THE GAME rather than by grabbing the window: the viewport's own texture is exactly
/// what was rendered, and it needs neither focus nor a cooperative window manager.
///
/// Set <c>REFII_PUBGAME_SHOT</c> to a file path to arm it, and <c>REFII_PUBGAME_SHOT_FRAME</c> to
/// choose when — the default of 400 is comfortably past a startup fade and any forced card.
/// </remarks>
public sealed class PubGameShot
{
    private readonly string _path;
    private readonly int _frame;
    private int _frames;

    public PubGameShot()
    {
        _path = System.Environment.GetEnvironmentVariable("REFII_PUBGAME_SHOT") ?? string.Empty;
        _frame =
            int.TryParse(
                System.Environment.GetEnvironmentVariable("REFII_PUBGAME_SHOT_FRAME"),
                out int frame) && frame > 0
                ? frame
                : 400;
    }

    public bool Armed => _path.Length != 0;

    /// <summary>Call once a frame from the host's <c>_Process</c>.</summary>
    public void Tick(Node host)
    {
        if (!Armed || ++_frames != _frame)
        {
            return;
        }

        Image image = host.GetViewport().GetTexture().GetImage();
        image.SavePng(_path);
        GD.Print($"[pubgame] wrote {_path} ({image.GetWidth()}x{image.GetHeight()})");
        host.GetTree().Quit();
    }
}
