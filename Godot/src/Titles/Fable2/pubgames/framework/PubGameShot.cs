using Godot;

namespace Refii.Titles.Fable2.PubGames.Framework;

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
