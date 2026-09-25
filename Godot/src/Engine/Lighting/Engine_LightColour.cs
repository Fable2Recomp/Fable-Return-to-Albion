using Godot;

namespace Refii.Lighting;

/// <summary>
/// A guest light colour handed to Godot so that Godot's own decode gives it back.
/// </summary>
/// <remarks>
/// `Light3D.LightColor` is "the light's color in nonlinear sRGB encoding": the renderer runs it
/// through the sRGB decode before it shades. The guest's light colours are LINEAR and reach its
/// constant buffer raw — no pow, no clamp, no 8-bit pack (FX lights: `sub_8323FA50` →
/// `sub_82A49D08` desc+0x20 → the packer `sub_82A6CDD0` → `g_GlobalLightParamsPS` rows 7..10).
/// So the colour goes in ENCODED, by the exact inverse of that decode.
///
/// Unclamped on purpose: some shipped spell lights state their colour on a 0..255 scale.
/// </remarks>
public static class Engine_LightColour
{
    /// <summary>The guest's linear colour, kept on a light set through <see cref="Apply"/>.</summary>
    private static readonly StringName LinearMeta = "refii_linear_colour";

    /// <summary>The value to assign to <c>LightColor</c> for a linear guest colour.</summary>
    public static Color ForGodot(Color linear) =>
        new(Encode(linear.R), Encode(linear.G), Encode(linear.B), linear.A);

    /// <summary>Sets a light's colour from the guest's linear value, remembering that value.</summary>
    public static void Apply(Light3D light, Color linear)
    {
        light.LightColor = ForGodot(linear);
        light.SetMeta(LinearMeta, linear);
    }

    /// <summary>
    /// The linear colour a light shades with: the guest's value for a light set through
    /// <see cref="Apply"/>, otherwise <c>LightColor</c> as assigned.
    /// </summary>
    public static Color Linear(Light3D light) =>
        light.HasMeta(LinearMeta) ? light.GetMeta(LinearMeta).AsColor() : light.LightColor;

    /// <summary>Inverse of the sRGB decode, `x &lt;= 0.04045 ? x / 12.92 : ((x + 0.055) / 1.055)^2.4`.</summary>
    private static float Encode(float linear) =>
        linear <= 0.0031308f
            ? linear * 12.92f
            : (1.055f * Mathf.Pow(linear, 1f / 2.4f)) - 0.055f;
}
