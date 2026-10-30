using Refii.Core;

namespace Refii.Models;

/// <summary>
/// The fixed registers the title's shaders read their frame constants from, by the names the
/// shader bank declares them under, for the title running (<see cref="Engine_Title.Active"/>).
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
///
/// ONE MAP PER TITLE. Fable 3's 385 shaders (its .sbk inside levels.bnk) bind the same names by
/// the same rule but mostly elsewhere, and its light block is eight lights wide (<see cref="Fable3"/>).
/// A member is <see cref="Absent"/> where the title's banks declare no such name: a write to it
/// is dropped (<see cref="Engine_ClassicConstants"/> ignores a negative register) and a read
/// returns zero, so one feed writes every title's frame.
/// </remarks>
public static class Engine_ClassicRegisters
{
    /// <summary>A register the title's banks never declare. Negative even after an offset of a few registers.</summary>
    public const int Absent = -1024;

    /// <summary>One title's pixel-stage registers.</summary>
    public sealed class PsMap
    {
        public int TileOffsetSize { get; init; } = Absent;
        public int ShadowRangeConstants { get; init; } = Absent;
        public int ShadowMapResolution0 { get; init; } = Absent;
        public int FadeBeginDistanceAndRange { get; init; } = Absent;
        public int SimulationTime { get; init; } = Absent;
        public int PerspectiveConstants { get; init; } = Absent;
        public int EyePosition { get; init; } = Absent;
        public int GlobalAmbientAndBrightness { get; init; } = Absent;
        public int LightmapFactorAndGamma { get; init; } = Absent;
        public int AmbientNormalMapDarkeningColour { get; init; } = Absent;
        public int NormalMapLightAngleConstants { get; init; } = Absent;
        public int AtmosphericFactors { get; init; } = Absent;
        public int GlobalTextureGammaRamp { get; init; } = Absent;
        public int SpotLightShadowTransform { get; init; } = Absent;
        public int DirectionalLightShadowTransform0 { get; init; } = Absent;
        public int DirectionalLightShadowTransform1 { get; init; } = Absent;
        public int SpotShadowMapResolution { get; init; } = Absent;
        public int AlphaStippleScreenUVFactor { get; init; } = Absent;
        public int AtmosphereBetaRayleigh { get; init; } = Absent;
        public int AtmosphereBetaMie { get; init; } = Absent;
        public int AtmosphereSunDirection { get; init; } = Absent;
        public int AtmosphereRanges { get; init; } = Absent;
        public int AtmosphereCloseFogColour { get; init; } = Absent;
        public int MistColour { get; init; } = Absent;
        public int MistDensityTextureScaleAndOffset { get; init; } = Absent;
        public int MistHeightTextureScaleAndOffset { get; init; } = Absent;
        public int MistAnimationConstants { get; init; } = Absent;
        public int MistHeightScaleAndOffsetDensityFallOff { get; init; } = Absent;
        public int LightPositions { get; init; } = Absent;
        public int LightColors { get; init; } = Absent;
        public int LightType { get; init; } = Absent;
        public int LightInvRadii { get; init; } = Absent;
        public int LightSpotDirections { get; init; } = Absent;
        public int LightSpotParameters { get; init; } = Absent;
        public int LightPrimaryDirection { get; init; } = Absent;
        public int LightScreenSpaceShadowMask { get; init; } = Absent;
        public int LightSpotShadowMask { get; init; } = Absent;
        public int LightBlockEnd { get; init; } = Absent;
        public int AOContrast { get; init; } = Absent;
        public int OneOverRenderTargetSize { get; init; } = Absent;
        public int LightmapFlickeringFactor { get; init; } = Absent;
        public int HostInverseViewProjection { get; init; } = Absent;
        public int HostCloudViewer { get; init; } = Absent;
        public int HostCloudDirection { get; init; } = Absent;
        public int HostCloudLight { get; init; } = Absent;
        public int HostSunGlareColour { get; init; } = Absent;
        public int[] ViewRegisters { get; init; } = System.Array.Empty<int>();
    }

    /// <summary>One title's vertex-stage registers.</summary>
    public sealed class VsMap
    {
        public int AtmosphericAdditiveFactor { get; init; } = Absent;
        public int EyePosition { get; init; } = Absent;
        public int SimulationTime { get; init; } = Absent;
        public int TreeConstants { get; init; } = Absent;
        public int HostWindDirection { get; init; } = Absent;
        public int WindDirections { get; init; } = Absent;
        public int CameraPosition { get; init; } = Absent;
        public int CameraUpVector { get; init; } = Absent;
        public int CameraRightVector { get; init; } = Absent;
        public int LightmapFactorAndGamma { get; init; } = Absent;
        public int AmbientLightReferenceDirection { get; init; } = Absent;
        public int GlobalAmbientAndBrightness { get; init; } = Absent;
        public int LightPositions { get; init; } = Absent;
        public int LightColors { get; init; } = Absent;
        public int LightType { get; init; } = Absent;
        public int LightInvRadii { get; init; } = Absent;
        public int LightSpotDirections { get; init; } = Absent;
        public int LightSpotParameters { get; init; } = Absent;
        public int LightPrimaryDirection { get; init; } = Absent;
        public int LightBlockEnd { get; init; } = Absent;
        public int AtmosphereBetaRayleigh { get; init; } = Absent;
        public int AtmosphereBetaMie { get; init; } = Absent;
        public int AtmosphereSunDirection { get; init; } = Absent;
        public int AtmosphereRanges { get; init; } = Absent;
        public int AtmosphereCloseFogColour { get; init; } = Absent;
        public int CameraPixelOffset { get; init; } = Absent;
        public int ShadowRangeConstants { get; init; } = Absent;
        public int LightmapFlickeringFactor { get; init; } = Absent;
        public int AOContrast { get; init; } = Absent;
        public int HostDecalViewProjection { get; init; } = Absent;
        public int HostCameraZ { get; init; } = Absent;
        public int HostBillboardEye { get; init; } = Absent;
        public int HostBillboardForward { get; init; } = Absent;
        public int HostStarsEye { get; init; } = Absent;
        public int[] ViewRegisters { get; init; } = System.Array.Empty<int>();
    }

    /// <summary>One title's register map.</summary>
    public sealed class Map
    {
        public required PsMap Ps { get; init; }
        public required VsMap Vs { get; init; }
        /// <summary>Lights the block holds per draw.</summary>
        public required int LightsPerDraw { get; init; }
        /// <summary>Registers in the pixel light block.</summary>
        public required int LightBlockRegisters { get; init; }
    }

    /// <summary>Fable 2's registers, from its 1317 shaders' constant tables.</summary>
    public static readonly Map Fable2 = new()
    {
        Ps = new PsMap
        {
            TileOffsetSize = 0,
            ShadowRangeConstants = 12,
            ShadowMapResolution0 = 13,
            FadeBeginDistanceAndRange = 15,
            SimulationTime = 17,
            PerspectiveConstants = 18,
            EyePosition = 19,
            GlobalAmbientAndBrightness = 20,
            LightmapFactorAndGamma = 21,
            AmbientNormalMapDarkeningColour = 22,
            NormalMapLightAngleConstants = 23,
            AtmosphericFactors = 28,
            GlobalTextureGammaRamp = 30,
            SpotLightShadowTransform = 32,
            DirectionalLightShadowTransform0 = 36,
            DirectionalLightShadowTransform1 = 40,
            SpotShadowMapResolution = 48,
            AlphaStippleScreenUVFactor = 50,
            AtmosphereBetaRayleigh = 64,
            AtmosphereBetaMie = 65,
            AtmosphereSunDirection = 66,
            AtmosphereRanges = 67,
            AtmosphereCloseFogColour = 68,
            MistColour = 72,
            MistDensityTextureScaleAndOffset = 73,
            MistHeightTextureScaleAndOffset = 74,
            MistAnimationConstants = 75,
            MistHeightScaleAndOffsetDensityFallOff = 76,
            LightPositions = 80,
            LightColors = 87,
            LightType = 94,
            LightInvRadii = 96,
            LightSpotDirections = 98,
            LightSpotParameters = 105,
            LightPrimaryDirection = 109,
            LightScreenSpaceShadowMask = 112,
            LightSpotShadowMask = 114,
            LightBlockEnd = 116,
            HostInverseViewProjection = 224,
            HostCloudViewer = 228,
            HostCloudDirection = 229,
            HostCloudLight = 230,
            HostSunGlareColour = 231,
            ViewRegisters = new[]
            {
                0, 18, 19,
                36, 37, 38, 39,
                40, 41, 42, 43,
                50,
                224, 225, 226, 227,
                228, 229, 230, 231,
            },
        },
        Vs = new VsMap
        {
            AtmosphericAdditiveFactor = 8,
            EyePosition = 9,
            SimulationTime = 10,
            TreeConstants = 15,
            HostWindDirection = 200,
            WindDirections = 21,
            CameraPosition = 46,
            CameraUpVector = 114,
            CameraRightVector = 115,
            LightmapFactorAndGamma = 18,
            AmbientLightReferenceDirection = 19,
            GlobalAmbientAndBrightness = 54,
            LightPositions = 76,
            LightColors = 83,
            LightType = 90,
            LightInvRadii = 92,
            LightSpotDirections = 94,
            LightSpotParameters = 101,
            LightPrimaryDirection = 105,
            LightBlockEnd = 108,
            AtmosphereBetaRayleigh = 108,
            AtmosphereBetaMie = 109,
            AtmosphereSunDirection = 110,
            AtmosphereRanges = 111,
            AtmosphereCloseFogColour = 112,
            HostDecalViewProjection = 224,
            HostCameraZ = 228,
            HostBillboardEye = 229,
            HostBillboardForward = 230,
            HostStarsEye = 231,
            ViewRegisters = new[]
            {
                9, 46, 114, 115,
                224, 225, 226, 227,
                228, 229, 230, 231,
            },
        },
        LightsPerDraw = 7,
        LightBlockRegisters = 36,
    };

    /// <summary>
    /// Fable 3's registers, from its 385 shaders' constant tables by the same rule. Where a name
    /// keeps Fable 2's register the value is the same; the ones that moved, and the guest's own
    /// writes that put them there (device constant file *0x8330F08C, VS c[n] at +16(n + 120), PS
    /// c[n] at +16(n + 376), sub_8220F3D8):
    ///   PS c17 / VS c10 the render clock, PS c18, PS c19 = VS c9 the eye, PS c31 the 2.2 ramp,
    ///   VS c28: sub_821B2F40 (0x821B300C, 0x821B3070, 0x821B3234, 0x821B30E4, 0x821B32A4);
    ///   PS c64 / VS c38 the atmosphere rows: 0x821B31B4, 0x822000BC (sub_8220E4A0(dev, 38, 0x83561560, 5));
    ///   VS c27 / PS c20 the ambient (dword_832A4E50 = 27, dword_832A4E70 = 20, sub_82275250);
    ///   VS c18, PS c21, VS c19, PS c22, PS c23 (dword_832A4E90 = 23), PS c32, PS c51: sub_82259C38;
    ///   PS c12 / VS c112: sub_822664B0 (0x82266540, 0x82266568); PS c47: sub_82C7F130 (0x82C7F1B8);
    ///   PS c29: the first pass's (1, 1, 0, 0) (0x821FF680); VS c113 / c114: bindings 0x167 / 0x115
    ///   (sub_822018E8 at 0x8220163C / 0x8220162C); the particle camera bindings 0x12A / 0x128
    ///   (0x82C54590 / 0x82C545A0) land at face_me_vertex_shader's c100 / c101, its position at c43.
    /// The light block g_GlobalLightParamsPS c80 x40 = Positions[8] Colors[8] Type[2] InvRadii[2]
    /// SpotDirections[8] SpotParameters[4] PrimaryLightDirection padding[3] ScreenSpaceShadowMask[2]
    /// SpotLightShadowMask[2]; g_GlobalLightParamsVS c44 x36 the same up to the padding.
    /// No Fable 3 PS declares past c159 nor VS past c175, so the host registers stay where they are.
    /// </summary>
    public static readonly Map Fable3 = new()
    {
        Ps = new PsMap
        {
            ShadowRangeConstants = 12,
            FadeBeginDistanceAndRange = 15,
            SimulationTime = 17,
            PerspectiveConstants = 18,
            EyePosition = 19,
            GlobalAmbientAndBrightness = 20,
            LightmapFactorAndGamma = 21,
            AmbientNormalMapDarkeningColour = 22,
            NormalMapLightAngleConstants = 23,
            AtmosphericFactors = 29,
            GlobalTextureGammaRamp = 31,
            SpotLightShadowTransform = 33,
            DirectionalLightShadowTransform0 = 37,
            DirectionalLightShadowTransform1 = 41,
            AtmosphereBetaRayleigh = 64,
            AtmosphereBetaMie = 65,
            AtmosphereSunDirection = 66,
            AtmosphereRanges = 67,
            AtmosphereCloseFogColour = 68,
            MistColour = 72,
            MistDensityTextureScaleAndOffset = 73,
            MistHeightTextureScaleAndOffset = 74,
            MistAnimationConstants = 75,
            MistHeightScaleAndOffsetDensityFallOff = 76,
            LightPositions = 80,
            LightColors = 88,
            LightType = 96,
            LightInvRadii = 98,
            LightSpotDirections = 100,
            LightSpotParameters = 108,
            LightPrimaryDirection = 112,
            LightScreenSpaceShadowMask = 116,
            LightSpotShadowMask = 118,
            LightBlockEnd = 120,
            AOContrast = 32,
            OneOverRenderTargetSize = 47,
            LightmapFlickeringFactor = 51,
            HostInverseViewProjection = 224,
            HostCloudViewer = 228,
            HostCloudDirection = 229,
            HostCloudLight = 230,
            HostSunGlareColour = 231,
            ViewRegisters = new[]
            {
                18, 19,
                37, 38, 39, 40,
                41, 42, 43, 44,
                47,
                224, 225, 226, 227,
                228, 229, 230, 231,
            },
        },
        Vs = new VsMap
        {
            AtmosphericAdditiveFactor = 8,
            EyePosition = 9,
            SimulationTime = 10,
            TreeConstants = 15,
            HostWindDirection = 200,
            WindDirections = 21,
            CameraPosition = 43,
            CameraUpVector = 100,
            CameraRightVector = 101,
            LightmapFactorAndGamma = 18,
            AmbientLightReferenceDirection = 19,
            GlobalAmbientAndBrightness = 27,
            LightPositions = 44,
            LightColors = 52,
            LightType = 60,
            LightInvRadii = 62,
            LightSpotDirections = 64,
            LightSpotParameters = 72,
            LightPrimaryDirection = 76,
            LightBlockEnd = 80,
            AtmosphereBetaRayleigh = 38,
            AtmosphereBetaMie = 39,
            AtmosphereSunDirection = 40,
            AtmosphereRanges = 41,
            AtmosphereCloseFogColour = 42,
            CameraPixelOffset = 28,
            ShadowRangeConstants = 112,
            LightmapFlickeringFactor = 113,
            AOContrast = 114,
            HostDecalViewProjection = 224,
            HostCameraZ = 228,
            HostBillboardEye = 229,
            HostBillboardForward = 230,
            HostStarsEye = 231,
            ViewRegisters = new[]
            {
                9, 43, 100, 101,
                224, 225, 226, 227,
                228, 229, 230, 231,
            },
        },
        LightsPerDraw = 8,
        LightBlockRegisters = 40,
    };

    /// <summary>The map of <paramref name="title"/>.</summary>
    public static Map For(Engine_Title title) => ReferenceEquals(title, Engine_Title.Fable3) ? Fable3 : Fable2;

    /// <summary>The map of the title running.</summary>
    public static Map Active => For(Engine_Title.Active);

    /// <summary>Pixel-stage registers (<c>xr_ps_constants</c>).</summary>
    public static class Ps
    {
        /// <summary>g_TileOffsetSize: the screen-space shadow mask's addressing, (0, 0, 1/W, 1/H).</summary>
        public static int TileOffsetSize => Active.Ps.TileOffsetSize;
        public static int ShadowRangeConstants => Active.Ps.ShadowRangeConstants;
        public static int ShadowMapResolution0 => Active.Ps.ShadowMapResolution0;
        public static int FadeBeginDistanceAndRange => Active.Ps.FadeBeginDistanceAndRange;
        public static int SimulationTime => Active.Ps.SimulationTime;
        public static int PerspectiveConstants => Active.Ps.PerspectiveConstants;
        public static int EyePosition => Active.Ps.EyePosition;
        public static int GlobalAmbientAndBrightness => Active.Ps.GlobalAmbientAndBrightness;
        public static int LightmapFactorAndGamma => Active.Ps.LightmapFactorAndGamma;
        public static int AmbientNormalMapDarkeningColour => Active.Ps.AmbientNormalMapDarkeningColour;
        public static int NormalMapLightAngleConstants => Active.Ps.NormalMapLightAngleConstants;
        public static int AtmosphericFactors => Active.Ps.AtmosphericFactors;
        public static int GlobalTextureGammaRamp => Active.Ps.GlobalTextureGammaRamp;
        /// <summary>Four rows: the spot light's world-to-shadow-map transform.</summary>
        public static int SpotLightShadowTransform => Active.Ps.SpotLightShadowTransform;
        /// <summary>Four rows each: the sun's two cascades.</summary>
        public static int DirectionalLightShadowTransform0 => Active.Ps.DirectionalLightShadowTransform0;
        public static int DirectionalLightShadowTransform1 => Active.Ps.DirectionalLightShadowTransform1;
        public static int SpotShadowMapResolution => Active.Ps.SpotShadowMapResolution;
        /// <summary>g_AlphaStippleScreenUVFactor: (target width / 32, height / 32, 0, 0).</summary>
        public static int AlphaStippleScreenUVFactor => Active.Ps.AlphaStippleScreenUVFactor;
        /// <summary>g_AtmosphericParameters: BetaRayleigh, BetaMie, SunDirection, ranges, CloseFogColour.</summary>
        public static int AtmosphereBetaRayleigh => Active.Ps.AtmosphereBetaRayleigh;
        public static int AtmosphereBetaMie => Active.Ps.AtmosphereBetaMie;
        public static int AtmosphereSunDirection => Active.Ps.AtmosphereSunDirection;
        public static int AtmosphereRanges => Active.Ps.AtmosphereRanges;
        public static int AtmosphereCloseFogColour => Active.Ps.AtmosphereCloseFogColour;
        /// <summary>g_MistParameters: colour, density texture, height texture, animation, falloff.</summary>
        public static int MistColour => Active.Ps.MistColour;
        public static int MistDensityTextureScaleAndOffset => Active.Ps.MistDensityTextureScaleAndOffset;
        public static int MistHeightTextureScaleAndOffset => Active.Ps.MistHeightTextureScaleAndOffset;
        public static int MistAnimationConstants => Active.Ps.MistAnimationConstants;
        public static int MistHeightScaleAndOffsetDensityFallOff => Active.Ps.MistHeightScaleAndOffsetDensityFallOff;
        /// <summary>g_GlobalLightParamsPS, c80..c115 (Fable 3: c80..c119).</summary>
        public static int LightPositions => Active.Ps.LightPositions;
        public static int LightColors => Active.Ps.LightColors;
        public static int LightType => Active.Ps.LightType;
        public static int LightInvRadii => Active.Ps.LightInvRadii;
        public static int LightSpotDirections => Active.Ps.LightSpotDirections;
        public static int LightSpotParameters => Active.Ps.LightSpotParameters;
        public static int LightPrimaryDirection => Active.Ps.LightPrimaryDirection;
        public static int LightScreenSpaceShadowMask => Active.Ps.LightScreenSpaceShadowMask;
        public static int LightSpotShadowMask => Active.Ps.LightSpotShadowMask;
        public static int LightBlockEnd => Active.Ps.LightBlockEnd;

        /// <summary>Fable 3's g_AOContrast: the theme's AMBIENT_OCCLUSION_CONTRAST, splat (sub_82259C38 0x82259F00).</summary>
        public static int AOContrast => Active.Ps.AOContrast;
        /// <summary>Fable 3's g_OneOverRenderTargetSize: (1/W, 1/H, 0, 0) of the target drawn (sub_82C7F130).</summary>
        public static int OneOverRenderTargetSize => Active.Ps.OneOverRenderTargetSize;
        /// <summary>Fable 3's g_LightmapFlickeringFactor, splat (sub_82259C38 0x8225A050).</summary>
        public static int LightmapFlickeringFactor => Active.Ps.LightmapFlickeringFactor;

        /// <summary>
        /// The host's: a soft particle's g_InverseViewProjection (four rows) for the view drawing it,
        /// which the composition reads in place of the material's (Engine_ClassicViews). No title PS
        /// reads past c123 (Fable 3: c159).
        /// </summary>
        public static int HostInverseViewProjection => Active.Ps.HostInverseViewProjection;
        /// <summary>The host's: the cloud layers' g_ViewerPosition (eye, 1), g_ViewerDirection (forward, 0) and g_LightPosition for the view.</summary>
        public static int HostCloudViewer => Active.Ps.HostCloudViewer;
        public static int HostCloudDirection => Active.Ps.HostCloudDirection;
        public static int HostCloudLight => Active.Ps.HostCloudLight;
        /// <summary>The host's: the sun glare's c244 for the view (its facing term is the view's camera's).</summary>
        public static int HostSunGlareColour => Active.Ps.HostSunGlareColour;

        /// <summary>
        /// The registers that belong to the view, not the frame: the screen addressing, the depth
        /// terms, the eye, the sun cascades fitted to it, the stipple's target size and the host's
        /// view-space by-id params. On split screen each view holds its own (Engine_ClassicConstants).
        /// </summary>
        public static int[] ViewRegisters => Active.Ps.ViewRegisters;
    }

    /// <summary>Vertex-stage registers (<c>xr_vs_constants</c>).</summary>
    public static class Vs
    {
        public static int AtmosphericAdditiveFactor => Active.Vs.AtmosphericAdditiveFactor;
        public static int EyePosition => Active.Vs.EyePosition;
        public static int SimulationTime => Active.Vs.SimulationTime;
        /// <summary>g_TreeConstants, three registers (c15..c17).</summary>
        public static int TreeConstants => Active.Vs.TreeConstants;
        /// <summary>The host's: the guest-world wind direction (W+0xD38), which
        /// <c>xr_tree_constant</c> turns into each tree's c17.xy. No title VS reads past c144 (Fable 3: c175).</summary>
        public static int HostWindDirection => Active.Vs.HostWindDirection;
        /// <summary>g_WindDirections, two registers (the repeated family's WIND1 stage).</summary>
        public static int WindDirections => Active.Vs.WindDirections;
        /// <summary>The particle shaders' camera: g_CameraPosition c46, g_CameraUpVector c114, g_CameraRightVector c115 (Fable 3: c43, c100, c101).</summary>
        public static int CameraPosition => Active.Vs.CameraPosition;
        public static int CameraUpVector => Active.Vs.CameraUpVector;
        public static int CameraRightVector => Active.Vs.CameraRightVector;
        public static int LightmapFactorAndGamma => Active.Vs.LightmapFactorAndGamma;
        public static int AmbientLightReferenceDirection => Active.Vs.AmbientLightReferenceDirection;
        public static int GlobalAmbientAndBrightness => Active.Vs.GlobalAmbientAndBrightness;
        /// <summary>g_GlobalLightParamsVS, c76..c107: the pixel block's layout, 4 registers lower (Fable 3: c44..c79, 36 lower).</summary>
        public static int LightPositions => Active.Vs.LightPositions;
        public static int LightColors => Active.Vs.LightColors;
        public static int LightType => Active.Vs.LightType;
        public static int LightInvRadii => Active.Vs.LightInvRadii;
        public static int LightSpotDirections => Active.Vs.LightSpotDirections;
        public static int LightSpotParameters => Active.Vs.LightSpotParameters;
        public static int LightPrimaryDirection => Active.Vs.LightPrimaryDirection;
        public static int LightBlockEnd => Active.Vs.LightBlockEnd;
        /// <summary>g_VertexAtmosphericParameters, the pixel block's five registers at c108 (Fable 3: c38).</summary>
        public static int AtmosphereBetaRayleigh => Active.Vs.AtmosphereBetaRayleigh;
        public static int AtmosphereBetaMie => Active.Vs.AtmosphereBetaMie;
        public static int AtmosphereSunDirection => Active.Vs.AtmosphereSunDirection;
        public static int AtmosphereRanges => Active.Vs.AtmosphereRanges;
        public static int AtmosphereCloseFogColour => Active.Vs.AtmosphereCloseFogColour;

        /// <summary>Fable 3's g_CameraPixelOffset: the clip-space jitter every world VS adds (xy x w).</summary>
        public static int CameraPixelOffset => Active.Vs.CameraPixelOffset;
        /// <summary>Fable 3's g_ShadowRangeConstants for the tree billboards: the pixel stage's c12.</summary>
        public static int ShadowRangeConstants => Active.Vs.ShadowRangeConstants;
        /// <summary>Fable 3's g_LightmapFlickeringFactor (value, 0, 0, 0), the repeated family's.</summary>
        public static int LightmapFlickeringFactor => Active.Vs.LightmapFlickeringFactor;
        /// <summary>Fable 3's g_AOContrast (value, 0, 0, 0), the repeated family's.</summary>
        public static int AOContrast => Active.Vs.AOContrast;

        /// <summary>The host's: a decal's g_MatViewProj (four rows, guest world to the view's clip) for the view drawing it.</summary>
        public static int HostDecalViewProjection => Active.Vs.HostDecalViewProjection;
        /// <summary>The host's: the view camera's z axis, guest axes (a camera-aligned trail's g_CameraZVector).</summary>
        public static int HostCameraZ => Active.Vs.HostCameraZ;
        /// <summary>The host's: the eye, w = 1 (the billboard cards' g_CameraPos).</summary>
        public static int HostBillboardEye => Active.Vs.HostBillboardEye;
        /// <summary>The host's: the view's horizontal forward, normalised (the billboard cards' g_CameraZVector).</summary>
        public static int HostBillboardForward => Active.Vs.HostBillboardForward;
        /// <summary>The host's: the eye, w = 0 (the stars' g_CamPos).</summary>
        public static int HostStarsEye => Active.Vs.HostStarsEye;

        /// <summary>The registers that belong to the view (see Ps.ViewRegisters): the eye and the particle camera.</summary>
        public static int[] ViewRegisters => Active.Vs.ViewRegisters;
    }

    /// <summary>Lights the block holds per draw: 7 (Fable 3: 8).</summary>
    public static int LightsPerDraw => Active.LightsPerDraw;

    /// <summary>Registers in the light block (c80..c115; Fable 3 c80..c119).</summary>
    public static int LightBlockRegisters => Active.LightBlockRegisters;
}
