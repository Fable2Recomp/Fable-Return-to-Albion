using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// The theme's sky overlays for the modern dome and everything reflecting it (sky_dome.gdshaderinc
/// globals): the guest's two heaviest SKY_OVERLAY slots and the second's weight
/// (Engine_ClassicTheme.Pick, as the classic dome binds them), or the level theme's one overlay
/// where no guest states them.
/// </summary>
public static class Engine_SkyOverlay
{
    private static readonly StringName Overlay0Name = "fable_sky_overlay0";
    private static readonly StringName Overlay1Name = "fable_sky_overlay1";
    private static readonly StringName OverlayName = "fable_sky_overlay";

    private static readonly Dictionary<uint, Texture2D?> Textures = new();
    private static ImageTexture? _empty;
    private static uint _hash0 = uint.MaxValue, _hash1 = uint.MaxValue;
    private static float _blend = -1f;

    /// <summary>Binds this frame's overlays. Main thread, once a frame, after the theme is read.</summary>
    public static void Pump()
    {
        uint hash0, hash1;
        float blend;
        System.Span<(float Weight, uint Hash)> pair = stackalloc (float, uint)[2];
        if (Refii.Models.Engine_ClassicTheme.TexturesLive)
        {
            Refii.Models.Engine_ClassicTheme.Pick(Refii.Models.Engine_ClassicTheme.SlotSkyOverlay, pair);
            (hash0, hash1, blend) = (pair[0].Hash, pair[1].Hash, pair[1].Weight);
        }
        else
        {
            SkyTheme? theme = Engine_SkyEnvironmentNode.Current?.Theme;
            hash0 = hash1 = theme is { HasSkyOverlayTexture: true } ? theme.SkyOverlayTextureHash : 0u;
            blend = 0f;
        }
        if (hash0 != _hash0 || hash1 != _hash1)
        {
            _hash0 = hash0;
            _hash1 = hash1;
            RenderingServer.GlobalShaderParameterSet(Overlay0Name, Load(hash0) ?? Empty());
            RenderingServer.GlobalShaderParameterSet(Overlay1Name, Load(hash1) ?? Empty());
            _blend = -1f;
        }
        if (blend != _blend)
        {
            _blend = blend;
            RenderingServer.GlobalShaderParameterSet(OverlayName, new Vector2(blend, 1f));
        }
    }

    /// <summary>What an empty slot reads: transparent, so the scattering shows (as the title's default).</summary>
    private static ImageTexture Empty()
    {
        if (_empty is null)
        {
            Image image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            image.Fill(new Color(0f, 0f, 0f, 0f));
            _empty = ImageTexture.CreateFromImage(image);
        }
        return _empty;
    }

    private static Texture2D? Load(uint hash)
    {
        if (hash == 0u)
        {
            return null;
        }
        if (!Textures.TryGetValue(hash, out Texture2D? texture))
        {
            texture = Engine_EnvironmentTextures.Load(hash, Refii.Models.Engine_BnkMeshLoader.ReadBytes, out string path);
            if (texture is null)
            {
                GD.Print($"[sky] overlay 0x{hash:X8} " + (path.Length == 0 ? "unresolved" : $"'{path}' did not decode"));
            }
            Textures[hash] = texture;
        }
        return texture;
    }
}
