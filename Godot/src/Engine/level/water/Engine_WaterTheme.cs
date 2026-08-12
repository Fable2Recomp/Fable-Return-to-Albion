using System.Collections.Generic;
using Godot;
using Refii.Lighting;

namespace Refii.Level;

/// <summary>
/// The day-set theme's <c>Water</c> group.
///
/// THE BODY'S OWN FLOATS NEVER REACH THE SHADER. `sub_821E4C88` is branch-free, reads only the
/// theme parameter object and writes the whole 28-float4 water constant block immediately before
/// the upload — the `.water` file's parameters are not consulted anywhere in that path. All 40
/// water bodies in levels.bnk carry an identical parameter set and the same normal map, which is an
/// untouched authoring default rather than per-lake tuning.
///
/// It also means some parameters have NOWHERE ELSE to come from. The body block ends at
/// GlitteringPower, so <c>EdgeBlendMin</c>, <c>EdgeBlendMax</c> and <c>EdgeBlendBias</c> — the
/// three that shape the shoreline — exist only here.
///
/// This deliberately reads only what is used. The full group is 29 floats plus six flat colour
/// channels, and lifting all of it belongs with retiring the positional body layout in
/// <see cref="Engine_WaterFile"/> — a larger change that does not block the shoreline.
/// </summary>
public static class Engine_WaterTheme
{
    /// <summary>FNV-1 of the field names, case-sensitive, basis 0x811C9DC5.</summary>
    private const uint WaterRecord = 0x1E6889DA;

    private const uint EdgeBlendMaxHash = 0x52233307;
    private const uint EdgeBlendMinHash = 0x5A234079;
    private const uint EdgeBlendBiasHash = 0x79F85F10;

    /// <summary>
    /// The engine's own defaults, used when a theme states no Water group.
    ///
    /// STATED AS OURS, NOT AS THE GAME'S. Unlike the numbers in Engine_WaterMaterial — which were
    /// traced to the shared base theme record — these three are a neutral fallback: a ramp from
    /// coverage 0 to coverage 1 with no bias, i.e. a plain linear fade across the blurred mask.
    /// A region whose theme carries the group overrides all three.
    /// </summary>
    public const float DefaultEdgeBlendMin = 0.0f;

    public const float DefaultEdgeBlendMax = 1.0f;

    public const float DefaultEdgeBlendBias = 0.0f;

    public readonly record struct EdgeBlend(float Min, float Max, float Bias)
    {
        public static EdgeBlend Default =>
            new(DefaultEdgeBlendMin, DefaultEdgeBlendMax, DefaultEdgeBlendBias);
    }

    /// <summary>
    /// Reads the edge-blend trio for the moment <paramref name="timeOfDay"/> names.
    ///
    /// Nearest day-set entry rather than the bracket blend the sky uses. The shoreline width is not
    /// something the eye tracks across a five-minute step, and taking the entry directly avoids a
    /// second extraction per re-evaluation on a path that already dominated the frame cost.
    /// </summary>
    public static bool TryRead(
        IReadOnlyList<byte[]> gdbs, float timeOfDay, out EdgeBlend blend)
    {
        blend = EdgeBlend.Default;

        Chain? chain = Chain.Create(gdbs);
        if (chain is null ||
            !chain.SelectThemeFromDaySet(timeOfDay, out RecordRef theme, out _))
        {
            return false;
        }

        RecordRef water = chain.ResolveRecordField(theme, WaterRecord);
        if (!water.Valid)
        {
            return false;
        }

        float min = chain.ReadFloat(water, EdgeBlendMinHash, out float readMin)
            ? readMin
            : DefaultEdgeBlendMin;

        float max = chain.ReadFloat(water, EdgeBlendMaxHash, out float readMax)
            ? readMax
            : DefaultEdgeBlendMax;

        float bias = chain.ReadFloat(water, EdgeBlendBiasHash, out float readBias)
            ? readBias
            : DefaultEdgeBlendBias;

        // A RAMP THAT CANNOT OPEN IS NOT A RAMP. `Max - Min` is the denominator and the shader
        // floors it at 1e-4, so an inverted or degenerate pair does not blow up — it quietly drives
        // ALPHA to zero and the body disappears. Refuse it rather than pass it to the shader.
        if (!float.IsFinite(min) || !float.IsFinite(max) || !float.IsFinite(bias) ||
            max - min <= 1e-4f)
        {
            GD.Print(
                $"[water] theme edge blend is degenerate (min={min}, max={max}, bias={bias}) — " +
                "using the neutral ramp");
            blend = EdgeBlend.Default;
            return false;
        }

        blend = new EdgeBlend(min, max, bias);
        return true;
    }
}
