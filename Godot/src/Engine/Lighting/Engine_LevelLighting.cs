using System;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

public static class Engine_LevelLighting
{
    public const string SunNodeName = "Sun";

    public const float SunFarDepth = 120.0f;

    public const float CascadeSplitNear = 35.0f;

    public const float NearSlopeBias = 2.0f;

    public static DirectionalLight3D BuildSun(LightingFrame frame)
    {
        var light = new DirectionalLight3D
        {
            Name = SunNodeName,
            DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel2Splits,
            DirectionalShadowMaxDistance = SunFarDepth,
            DirectionalShadowSplit1 = CascadeSplitNear / SunFarDepth,
            ShadowNormalBias = NearSlopeBias,
            ShadowBias = 0.03f,
        };

        UpdateSun(light, frame);
        return light;
    }

    public static void UpdateSun(DirectionalLight3D light, LightingFrame frame)
    {
        light.LightColor = new Color(frame.SunColour[0], frame.SunColour[1], frame.SunColour[2]);
        light.LightEnergy = frame.SunEnergy;
        light.ShadowEnabled = frame.SunShadow;
        light.Transform = new Transform3D(SunBasis(frame.SunDirection), Vector3.Zero);
    }

    /// <summary>
    /// The luminance that tone mapping maps to white.
    ///
    /// The guest's curve is a 512-entry 1D LUT that <c>sub_8217E428</c> rebuilds on the CPU
    /// every frame over the input domain [0, 4] — the entries are stored as a ratio against
    /// <c>((i + 0.5) / 512) * 4.0</c> at 0x8217E72C — so four is the luminance the curve is
    /// defined up to. The exact white comes from a theme scalar we have not named yet; this is
    /// the domain bound, which is the best-evidenced number available.
    /// </summary>
    public const float TonemapWhite = 4.0f;

    /// <summary>
    /// A STATIC STAND-IN FOR THE GUEST'S AUTO-EXPOSURE, which Godot 4 cannot reproduce.
    ///
    /// The guest's post-process is two stages and the curve is the second one. First
    /// <c>sub_822AA9A0</c> locks the previous frame's luminance surface, bins 1024 histogram
    /// buckets on the CPU and takes percentile thresholds; then exposure walks toward putting
    /// that average on MIDDLE_GRAY before the Reinhard curve sees anything. Shipping the curve
    /// without the exposure is worse than shipping neither — it is designed to receive a signal
    /// that has already been normalised, so everything lands on the toe and the region reads as
    /// dusk at two in the afternoon.
    ///
    /// The value comes from two stated defaults rather than from taste. MIDDLE_GRAY is 0.45 and
    /// AVG_LUMINANCE_LOWER_BOUND is 0.15 (both from the theme's default constructor
    /// <c>sub_82A45DD0</c>), and the measured median luminance of this region at midday is
    /// 0.026 — well under that floor, so the guest's own clamp is what would apply and the
    /// exposure it settles on is 0.45 / 0.15.
    ///
    /// It is a CONSTANT where the guest has a control loop, so a genuinely bright or genuinely
    /// dark region will not be compensated the way the original compensates.
    /// </summary>
    public const float TonemapExposure = 0.45f / 0.15f;

    public static void ConfigureEnvironment(Godot.Environment environment, LightingFrame frame)
    {
        if (environment is null)
        {
            return;
        }

        // EXTENDED REINHARD, because that is the guest's curve family.
        //
        // sub_8217E428 builds each LUT entry as `((v18*x + 1.0) * x) / (x + 1.0)` at
        // 0x8217E59C — that is `x * (1 + x/W^2) / (1 + x)` with v18 = 1/W^2, extended Reinhard
        // with a white point, which is what Godot's Reinhardt tonemapper and its TonemapWhite
        // are. Chosen from the decompile rather than because it flattered the image.
        //
        // Without it the region is rendered linearly against white = 1.0, and the theme states
        // values well past that — this level's main light alone is 1.58 — so every lit surface
        // clipped while everything else stayed crushed.
        //
        // What is NOT reproduced, and is the reason a scene can still read too dark or too
        // bright: the guest AUTO-EXPOSES. sub_822AA9A0 locks the previous frame's luminance
        // surface, bins 1024 histogram buckets on the CPU and drives exposure toward a middle
        // grey of 0.45 at a rate of ExposureSpeed (0.01) compensated to 30 Hz. Godot 4 has no
        // auto-exposure, so exposure stays fixed here.
        environment.TonemapMode = Godot.Environment.ToneMapper.Reinhardt;
        environment.TonemapWhite = TonemapWhite;
        environment.TonemapExposure = TonemapExposure;

        if (frame.HasAmbient)
        {
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            environment.AmbientLightColor = new Color(
                frame.AmbientColour[0], frame.AmbientColour[1], frame.AmbientColour[2]);
            environment.AmbientLightEnergy = frame.AmbientEnergy;
        }

        if (frame.HasFog)
        {
            environment.FogEnabled = true;
            environment.FogMode = Godot.Environment.FogModeEnum.Depth;
            environment.FogDepthBegin = frame.FogBegin;
            environment.FogDepthEnd = frame.FogEnd;
            environment.FogLightColor = new Color(
                frame.FogColour[0], frame.FogColour[1], frame.FogColour[2]);

            // Godot's depth fog is (ramp ^ curve) * density, so density is the amount reached
            // AT FogDepthEnd — which is exactly what the theme's far density states. Left at
            // Godot's 1.0 the ramp reaches total occlusion instead, and with this region's fog
            // colour of 5/255 that is a black wall rather than haze.
            environment.FogDensity = frame.FogDensity;

            // THE SKY IS NOT FOGGED. Godot blends the sky toward the fog colour like any other
            // surface, and the theme's own sky already paints its horizon haze from the stated
            // Rayleigh and Mie coefficients. Doing both is two atmosphere models fighting, and
            // the loser is visible: a black band between the cloud deck and the rooftops, which
            // is this region's near-black fog colour applied to the entire sky dome.
            environment.FogSkyAffect = 0.0f;
        }
    }

    /// <summary>
    /// Aims the sun so it agrees with the sun DISC in the sky.
    ///
    /// <see cref="Engine_SkyFrame"/> builds SunDirection as (groundX, up, groundZ) — component
    /// [1] is the vertical — so it is already Y-up and needs only the X flip that carries it
    /// into the level root's mirrored space. Engine_CelestialElements does exactly that,
    /// <c>(-d0, d1, d2)</c>, to place the visible disc.
    ///
    /// A DirectionalLight3D travels along its own -Z, and light travels FROM the sun, so the
    /// forward vector is the NEGATION of the direction to the disc: <c>(d0, -d1, -d2)</c>.
    ///
    /// This used to negate all three, which flips X relative to the disc. The sun was drawn on
    /// one side of the sky and lit the world from the other, so shadows fell the wrong way
    /// horizontally — subtle enough to read as "the lighting needs work" rather than as a sign
    /// error, especially under a heavily overcast sky where the disc is hard to locate.
    /// </summary>
    private static Basis SunBasis(float[] direction)
    {
        var forward = new Vector3(direction[0], -direction[1], -direction[2]);
        if (forward.LengthSquared() < 1e-8f)
        {
            forward = Vector3.Down;
        }

        forward = forward.Normalized();
        Vector3 up = MathF.Abs(forward.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        return Basis.LookingAt(forward, up);
    }
}
