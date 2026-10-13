namespace Refii.Models;

/// <summary>
/// The fixed registers the title's shaders read their frame constants from, by the names the
/// shader bank declares them under.
/// </summary>
/// <remarks>
/// READ FROM THE BANK, NOT INVENTED: every one of the 1317 shaders' D3DX constant tables binds
/// these names to the same registers (the standard material family alone binds 315 of them to
/// c80..c115), which is what lets the translated shaders read them by absolute register
/// (<c>xr_const(xr_ps_constants, N)</c>) and lets the frame feed write them by name here. A
/// name bound to different registers in different shaders is a MATERIAL constant and reaches
/// its shader as a <c>ps_</c>/<c>vs_</c> uniform instead; none of those is listed.
///
/// The light block is seven lights wide (<c>Positions[7]</c>, <c>Colors[7]</c>,
/// <c>SpotDirections[7]</c>), two packed <c>Type</c> and <c>InvRadii</c> registers and four of
/// <c>SpotParameters</c>: the guest fills it per draw with the lights it assigned to that draw,
/// which is why it is the per-instance row of the constant texture rather than the frame's.
/// </remarks>
public static class Engine_ClassicRegisters
{
    /// <summary>Pixel-stage registers (<c>xr_ps_constants</c>).</summary>
    public static class Ps
    {
        /// <summary>g_TileOffsetSize: the screen-space shadow mask's addressing, (0, 0, 1/W, 1/H).</summary>
        public const int TileOffsetSize = 0;
        public const int ShadowRangeConstants = 12;
        public const int ShadowMapResolution0 = 13;
        public const int FadeBeginDistanceAndRange = 15;
        public const int SimulationTime = 17;
        public const int PerspectiveConstants = 18;
        public const int EyePosition = 19;
        public const int GlobalAmbientAndBrightness = 20;
        public const int LightmapFactorAndGamma = 21;
        public const int AmbientNormalMapDarkeningColour = 22;
        public const int NormalMapLightAngleConstants = 23;
        public const int AtmosphericFactors = 28;
        public const int GlobalTextureGammaRamp = 30;
        /// <summary>Four rows: the spot light's world-to-shadow-map transform.</summary>
        public const int SpotLightShadowTransform = 32;
        /// <summary>Four rows each: the sun's two cascades.</summary>
        public const int DirectionalLightShadowTransform0 = 36;
        public const int DirectionalLightShadowTransform1 = 40;
        public const int SpotShadowMapResolution = 48;
        /// <summary>g_AlphaStippleScreenUVFactor: (target width / 32, height / 32, 0, 0).</summary>
        public const int AlphaStippleScreenUVFactor = 50;
        /// <summary>g_AtmosphericParameters: BetaRayleigh, BetaMie, SunDirection, ranges, CloseFogColour.</summary>
        public const int AtmosphereBetaRayleigh = 64;
        public const int AtmosphereBetaMie = 65;
        public const int AtmosphereSunDirection = 66;
        public const int AtmosphereRanges = 67;
        public const int AtmosphereCloseFogColour = 68;
        /// <summary>g_MistParameters: colour, density texture, height texture, animation, falloff.</summary>
        public const int MistColour = 72;
        public const int MistDensityTextureScaleAndOffset = 73;
        public const int MistHeightTextureScaleAndOffset = 74;
        public const int MistAnimationConstants = 75;
        public const int MistHeightScaleAndOffsetDensityFallOff = 76;
        /// <summary>g_GlobalLightParamsPS, c80..c115.</summary>
        public const int LightPositions = 80;
        public const int LightColors = 87;
        public const int LightType = 94;
        public const int LightInvRadii = 96;
        public const int LightSpotDirections = 98;
        public const int LightSpotParameters = 105;
        public const int LightPrimaryDirection = 109;
        public const int LightScreenSpaceShadowMask = 112;
        public const int LightSpotShadowMask = 114;
        public const int LightBlockEnd = 116;
    }

    /// <summary>Vertex-stage registers (<c>xr_vs_constants</c>).</summary>
    public static class Vs
    {
        public const int AtmosphericAdditiveFactor = 8;
        public const int EyePosition = 9;
        public const int SimulationTime = 10;
        /// <summary>g_TreeConstants, three registers (c15..c17).</summary>
        public const int TreeConstants = 15;
        /// <summary>The host's: the guest-world wind direction (W+0xD38), which
        /// <c>xr_tree_constant</c> turns into each tree's c17.xy. No title VS reads past c144.</summary>
        public const int HostWindDirection = 200;
        /// <summary>g_WindDirections, two registers (the repeated family's WIND1 stage).</summary>
        public const int WindDirections = 21;
        /// <summary>The particle shaders' camera: g_CameraPosition c46, g_CameraUpVector c114, g_CameraRightVector c115.</summary>
        public const int CameraPosition = 46;
        public const int CameraUpVector = 114;
        public const int CameraRightVector = 115;
        public const int LightmapFactorAndGamma = 18;
        public const int AmbientLightReferenceDirection = 19;
        public const int GlobalAmbientAndBrightness = 54;
        /// <summary>g_GlobalLightParamsVS, c76..c107: the pixel block's layout, 4 registers lower.</summary>
        public const int LightPositions = 76;
        public const int LightColors = 83;
        public const int LightType = 90;
        public const int LightInvRadii = 92;
        public const int LightSpotDirections = 94;
        public const int LightSpotParameters = 101;
        public const int LightPrimaryDirection = 105;
        public const int LightBlockEnd = 108;
        /// <summary>g_VertexAtmosphericParameters, the pixel block's five registers at c108.</summary>
        public const int AtmosphereBetaRayleigh = 108;
        public const int AtmosphereBetaMie = 109;
        public const int AtmosphereSunDirection = 110;
        public const int AtmosphereRanges = 111;
        public const int AtmosphereCloseFogColour = 112;
    }

    /// <summary>Lights the block holds per draw.</summary>
    public const int LightsPerDraw = 7;

    /// <summary>Registers in the light block (c80..c115).</summary>
    public const int LightBlockRegisters = 36;
}
