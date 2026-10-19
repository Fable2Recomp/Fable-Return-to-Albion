using Godot;

namespace Refii.Lighting;

/// <summary>
/// The classic sky's draws into the post layers, stated by the sky (main thread) and drawn by the
/// post chain (render thread), and the occlusion results that come back.
/// </summary>
/// <remarks>
/// THE TITLE DRAWS THREE SKY ELEMENTS OUTSIDE THE SCENE (verified 2026-10-14):
/// - the SUN BEAMS (PS 0121) into the sun layer off_8331A9A8, the radial blur's only source
///   (`sub_821CB750`: bind and clear 0x821CB924..38, draw 0x821CB958, resolve 0x821CB968);
/// - the MOON BLOOM (PS 0120) and then the SUN DISC (PS 0118) into the half-res glow layer that
///   becomes off_8331A9CC (`sub_82277380` 0x82277954 / 0x82277960), depth-tested against the
///   half-res scene depth, each inside an occlusion query.
/// The queries are the glares' visibility: sun = saturate(pixels / (SUN_DISC_SIZE^2 x 4000))
/// (0x821D8DC0..0x821D8E38, flt_82000C2C), moon = saturate(pixels / (MOON_SIZE^2 x 6000)), the
/// pixels counted at the title's half-res target (640 x 360). The glares (PS 0117, main scene)
/// read them a frame late, as the title's did. Here the counts come from the sprite passes and
/// the post chain writes the two visibilities into <see cref="VisibilityName"/>, which the
/// classic glare materials read.
/// </remarks>
public static class Engine_ClassicSkyLayers
{
    /// <summary>The global the glare materials read: texel 0 the sun's visibility, texel 1 the moon's.</summary>
    public const string VisibilityName = "fable_celestial_visibility";

    /// <summary>Player two's on split screen: each view's chain counts its own (the glares pick by the second view's layer).</summary>
    public const string VisibilitySecondName = "fable_celestial_visibility_second";

    /// <summary>The visibility global view <paramref name="view"/>'s post chain publishes.</summary>
    public static string VisibilityFor(int view) => view == 0 ? VisibilityName : VisibilitySecondName;

    /// <summary>The title's half-res target the query counts were measured on.</summary>
    public const float QueryPixels = 640f * 360f;

    /// <summary>One sky quad: guest-stated placement, colour and texture.</summary>
    public struct Sprite
    {
        public bool Active;
        public Vector3 Direction;   // Godot axes, unit
        public float Distance;
        public Vector2 Half;
        public Vector4 Colour;      // the pixel stage's colour constant, when it has one
        public Rid Texture;         // RenderingServer texture
        public Vector4 Uv;          // u0, v0, u1, v1
    }

    /// <summary>A frame's sky layer draws.</summary>
    public sealed class Frame
    {
        public Sprite SunBeams;
        public Sprite MoonBloom;
        public Sprite SunDisc;
        public float SunDiscSize;
        public float MoonSize;
    }

    private static readonly object Gate = new();
    private static readonly Frame _published = new();
    private static bool _any;

    /// <summary>The first view's visibility texture once its post chain made it.</summary>
    public static Texture2Drd? Visibility { get; private set; }

    private static readonly Texture2Drd?[] _visibilities = new Texture2Drd?[2];
    private static ImageTexture? _placeholder;

    /// <summary>A view's chain publishes its visibility texture into the view's global. Main thread.</summary>
    public static void PublishVisibility(int view, Texture2Drd texture)
    {
        EnsureRegistered();
        _visibilities[view] = texture;
        if (view == 0)
        {
            Visibility = texture;
        }
        RenderingServer.GlobalShaderParameterSet(VisibilityFor(view), texture);
    }

    /// <summary>
    /// A chain going away puts its view's global back on the placeholder (the second view's chain
    /// goes with every rejoin, and the glares bind both globals).
    /// </summary>
    public static void WithdrawVisibility(int view, Texture2Drd texture)
    {
        if (view is < 0 or > 1 || !ReferenceEquals(_visibilities[view], texture))
        {
            return;
        }
        _visibilities[view] = null;
        if (view == 0)
        {
            Visibility = null;
        }
        RenderingServer.GlobalShaderParameterSet(VisibilityFor(view), _placeholder ?? default(Variant));
    }

    /// <summary>States this frame's draws. Main thread.</summary>
    public static void Publish(in Sprite beams, in Sprite bloom, in Sprite disc, float sunDiscSize, float moonSize)
    {
        lock (Gate)
        {
            _published.SunBeams = beams;
            _published.MoonBloom = bloom;
            _published.SunDisc = disc;
            _published.SunDiscSize = sunDiscSize;
            _published.MoonSize = moonSize;
            _any = true;
        }
    }

    /// <summary>Copies the stated draws; false before the sky stated any. Render thread.</summary>
    public static bool Snapshot(Frame into)
    {
        lock (Gate)
        {
            if (!_any)
            {
                return false;
            }
            into.SunBeams = _published.SunBeams;
            into.MoonBloom = _published.MoonBloom;
            into.SunDisc = _published.SunDisc;
            into.SunDiscSize = _published.SunDiscSize;
            into.MoonSize = _published.MoonSize;
            return true;
        }
    }

    private static bool _registered;

    /// <summary>Registers the visibility globals (a placeholder until each view's chain publishes). Main thread.</summary>
    public static void EnsureRegistered()
    {
        // Once per process: the server's global list is editor-only and reads empty in the game.
        if (_registered)
        {
            return;
        }
        _registered = true;
        var image = Image.CreateEmpty(2, 1, false, Image.Format.Rf);
        image.Fill(new Color(1f, 0f, 0f));
        _placeholder = ImageTexture.CreateFromImage(image);
        RenderingServer.GlobalShaderParameterAdd(VisibilityName,
            RenderingServer.GlobalShaderParameterType.Sampler2D, _placeholder);
        RenderingServer.GlobalShaderParameterAdd(VisibilitySecondName,
            RenderingServer.GlobalShaderParameterType.Sampler2D, _placeholder);
    }
}
