using Godot;

namespace Refii.FlashGames.Framework;

/// <summary>
/// Where the guest puts a pub game's stage on the screen.
/// </summary>
/// <remarks>
/// <c>sub_82FED8B8</c> (CreateMovieView) builds the GViewport once. For the pub games' 1800x1300
/// stage it takes a special case (0x82FEDA74 <c>cmpwi 0x708</c>, 0x82FEDA84 <c>cmpwi 0x514</c>):
/// <c>s = min(H * 0.001, W * 0.00064285717)</c> (<c>flt_8203BE14</c>, <c>flt_820479CC</c>), so the
/// stage's centre 1555.6x1000 fills the screen and the rest bleeds off it — at 16:9 the board is
/// drawn at H/1000, not letterboxed at H/1300. Width and height truncate
/// (<c>fctiwz</c>) and the offsets halve toward zero (<c>srawi; addze</c>). Any other stage size
/// gets the ordinary uniform fit (scale mode 0, <c>sub_82FED4B0</c>).
/// </remarks>
public static class PubGameStageFit
{
    /// <summary>The screen rectangle the last stage was placed in; empty before the first.</summary>
    public static Rect2 Placed { get; private set; }

    public static float Scale(Vector2 viewport, Vector2 stage)
    {
        if (Mathf.IsEqualApprox(stage.X, 1800f) && Mathf.IsEqualApprox(stage.Y, 1300f))
        {
            return Mathf.Min(viewport.Y * 0.001f, viewport.X * 0.00064285717f);
        }

        return Mathf.Min(viewport.X / Mathf.Max(stage.X, 1f), viewport.Y / Mathf.Max(stage.Y, 1f));
    }

    public static void Apply(Node2D view, Vector2 viewport, Vector2 stage)
    {
        float scale = Scale(viewport, stage);
        int width = (int)(stage.X * scale);
        int height = (int)(stage.Y * scale);
        view.Scale = new Vector2(scale, scale);
        view.Position = new Vector2(((int)viewport.X - width) / 2, ((int)viewport.Y - height) / 2);
        Placed = new Rect2(view.Position, new Vector2(width, height));
    }
}
