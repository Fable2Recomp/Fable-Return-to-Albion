using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml.Linq;
using Refii.Gdb;

namespace Refii.Level;

/// <summary>
/// One Fable 3 <c>.envtheme</c>: the XML theme the title keeps under
/// <c>Data\NewEnvironmentThemes\</c>, a timeline of keys per named parameter.
/// </summary>
/// <remarks>
/// THE TITLE'S OWN READER, verified in IDA (Fable 3). <c>sub_82C450F8</c> reads the root
/// <c>EnvironmentTheme</c>: <c>version</c>, <c>FadeInTime</c>, <c>FadeOutTime</c>,
/// <c>FullyBlendLayer</c>, then each <c>ThemeParams/ThemeParam</c> through <c>sub_82C460B8</c> —
/// <c>Name</c>, <c>param_type</c> (<c>sub_82C45E90</c>: float 0; rgbcolour, rgbacolour,
/// rgbcolourwithfactor 1; texture 2; anything else 0) and its <c>TimelineItem</c>s, each a
/// <c>Time</c> and a value: a float <c>Value</c> (<c>sub_82C47448</c>), a <c>Value</c> of
/// X/Y/Z/W (<c>sub_82C474A8</c>, an absent component is 0) or a <c>TexturePath</c> whose
/// extension becomes <c>.tex</c> (<c>sub_82C47660</c>). A float's text is
/// "<c>decimal HEXBITS</c>" and the bits after the space win (<c>sub_8310D2C0</c>: sscanf "%X"
/// past the first space, atof only without one). <c>GlobalTheme</c> is not read.
///
/// KEYS ARE PRUNED AS THEY LOAD (0x82C462D0..0x82C4635C): the first key is kept, a middle key
/// only when it differs from either neighbour, and the last key never — a timeline closes on its
/// first key at time 1 instead (<see cref="Timeline.Evaluate"/>).
///
/// ONLY REGISTERED NAMES COUNT. The evaluation walks the 151 descriptors <c>sub_82C416B8</c>
/// registers (<see cref="Params"/>); a name the table does not hold is parsed and never read.
///
/// A WITH-FACTOR COLOUR IS STORED PREMULTIPLIED: X/Y/Z already carry the factor and W is the
/// factor itself — every shipped key has max(X, Y, Z) &lt;= W, and so does the guest's default
/// Main Light Colour (1.5, 1.38, 1.38, 1.5). So X/Y/Z is the colour (INFERRED from the data;
/// the shader-side reader of the block was not traced).
/// </remarks>
public sealed class Engine_EnvThemeFile
{
    public enum ParamKind : byte
    {
        Float = 0,
        Vector = 1,
        Texture = 2,
    }

    /// <summary>A registered parameter: its name, value kind, byte offset in the guest's theme
    /// block and the value <c>sub_82C7F8D0</c> seeds that offset with (a float in X).</summary>
    public readonly record struct Param(string Name, ParamKind Kind, int Offset, Vector4 Default);

    /// <summary><c>sub_82C416B8</c>'s table, in its order; defaults from <c>sub_82C7F8D0</c>,
    /// which <c>sub_821794C8</c> repeats over the block before every evaluation. A texture's
    /// default is none.</summary>
    public static readonly IReadOnlyList<Param> Params = new Param[]
    {
        new("Main Light Colour", ParamKind.Vector, 0, new(1.5f, 1.38f, 1.38f, 1.5f)),
        new("Main Light Shadowed", ParamKind.Float, 16, new(1.0f, 0f, 0f, 0f)),
        new("Ambient Colour", ParamKind.Vector, 32, new(0.0f, 0.0f, 0.0f, 1.0f)),
        new("Ambient Normal Map Darkening", ParamKind.Vector, 48, new(0.0f, 0.0f, 0.0f, 0.0f)),
        new("Local Ambient Colour", ParamKind.Vector, 64, new(1.0f, 1.0f, 1.0f, 1.0f)),
        new("Distant Shadow Factor", ParamKind.Float, 80, new(0.5f, 0f, 0f, 0f)),
        new("Landscape Distant Shadow Factor", ParamKind.Float, 84, new(0.5f, 0f, 0f, 0f)),
        new("Enable Light Zone Shadow Culling", ParamKind.Float, 88, new(1.0f, 0f, 0f, 0f)),
        new("Ambient Occlusion Contrast", ParamKind.Float, 92, new(2.0f, 0f, 0f, 0f)),
        new("Flickering Speed", ParamKind.Float, 96, new(0.0f, 0f, 0f, 0f)),
        new("Flickering Range", ParamKind.Float, 100, new(0.0f, 0f, 0f, 0f)),
        new("Time Factor", ParamKind.Float, 104, new(1.0f, 0f, 0f, 0f)),
        new("Sky Beta Rayleigh Multiplier", ParamKind.Float, 108, new(1.0f, 0f, 0f, 0f)),
        new("Sky Beta Mie Multiplier", ParamKind.Float, 112, new(1.0f, 0f, 0f, 0f)),
        new("Sky Colour", ParamKind.Vector, 128, new(1.0f, 1.0f, 1.0f, 1.0f)),
        new("Sky Complementary Colour", ParamKind.Vector, 144, new(1.0f, 1.0f, 1.0f, 1.0f)),
        new("Sky Complementary Colour Bias", ParamKind.Float, 160, new(0.02f, 0f, 0f, 0f)),
        new("Sky Overlay Texture", ParamKind.Texture, 164, default),
        new("Sunset Colour", ParamKind.Vector, 192, new(1.0f, 0.27f, 0.1f, 1.0f)),
        new("Star Brightness", ParamKind.Float, 208, new(0.0f, 0f, 0f, 0f)),
        new("Snow Size", ParamKind.Float, 212, new(0.0f, 0f, 0f, 0f)),
        new("Snow Fall Speed", ParamKind.Float, 216, new(0.0f, 0f, 0f, 0f)),
        new("Rain Size", ParamKind.Float, 220, new(0.0f, 0f, 0f, 0f)),
        new("Rain Density", ParamKind.Float, 224, new(0.0f, 0f, 0f, 0f)),
        new("Hide Occlusion Errors", ParamKind.Float, 228, new(0.0f, 0f, 0f, 0f)),
        new("Main Light Elevation", ParamKind.Float, 232, new(50.0f, 0f, 0f, 0f)),
        new("Main Light XY Rotation", ParamKind.Float, 236, new(25.0f, 0f, 0f, 0f)),
        new("Sun Axis Elevation", ParamKind.Float, 240, new(70.0f, 0f, 0f, 0f)),
        new("Sun Axis XY Rotation Offset", ParamKind.Float, 244, new(25.0f, 0f, 0f, 0f)),
        new("Sun Intensity", ParamKind.Float, 248, new(1.0f, 0f, 0f, 0f)),
        new("Sun Disc Texture", ParamKind.Texture, 252, default),
        new("Sun Disc Size", ParamKind.Float, 268, new(0.6f, 0f, 0f, 0f)),
        new("Sun Disc Colour", ParamKind.Vector, 272, new(1.0f, 1.0f, 1.0f, 1.0f)),
        new("Sun Disc Intensity", ParamKind.Float, 288, new(1.0f, 0f, 0f, 0f)),
        new("Sun Beams Texture", ParamKind.Texture, 292, default),
        new("Sun Beams Width", ParamKind.Float, 308, new(2.0f, 0f, 0f, 0f)),
        new("Sun Beams Height", ParamKind.Float, 312, new(2.0f, 0f, 0f, 0f)),
        new("Sun Beams Intensity", ParamKind.Float, 316, new(1.0f, 0f, 0f, 0f)),
        new("Sun Glare Texture", ParamKind.Texture, 320, default),
        new("Sun Glare Intensity", ParamKind.Float, 336, new(0.3f, 0f, 0f, 0f)),
        new("Sun Glare Size", ParamKind.Float, 340, new(0.5f, 0f, 0f, 0f)),
        new("Moon Axis Elevation", ParamKind.Float, 344, new(50.0f, 0f, 0f, 0f)),
        new("Moon Axis XY Rotation Offset", ParamKind.Float, 348, new(48.0f, 0f, 0f, 0f)),
        new("Moon Texture", ParamKind.Texture, 352, default),
        new("Moon Glare Texture", ParamKind.Texture, 368, default),
        new("Moon Intensity", ParamKind.Float, 384, new(0.75f, 0f, 0f, 0f)),
        new("Moon Glare Intensity", ParamKind.Float, 388, new(0.75f, 0f, 0f, 0f)),
        new("Moon Size", ParamKind.Float, 392, new(1.0f, 0f, 0f, 0f)),
        new("Moon Glare Size", ParamKind.Float, 396, new(1.0f, 0f, 0f, 0f)),
        new("Moon Transparency", ParamKind.Float, 400, new(1.0f, 0f, 0f, 0f)),
        new("Exposure", ParamKind.Float, 404, new(0.45f, 0f, 0f, 0f)),
        new("Saturation", ParamKind.Float, 408, new(0.0f, 0f, 0f, 0f)),
        new("Saturation Masked", ParamKind.Float, 412, new(0.0f, 0f, 0f, 0f)),
        new("Brightness", ParamKind.Float, 416, new(0.0f, 0f, 0f, 0f)),
        new("Brightness Masked", ParamKind.Float, 420, new(0.0f, 0f, 0f, 0f)),
        new("Contrast", ParamKind.Float, 424, new(0.0f, 0f, 0f, 0f)),
        new("Contrast Clamping", ParamKind.Float, 428, new(1.0f, 0f, 0f, 0f)),
        new("Contrast Enhancement", ParamKind.Float, 432, new(0.1f, 0f, 0f, 0f)),
        new("Contrast Enhancement Exponent", ParamKind.Float, 436, new(2.0f, 0f, 0f, 0f)),
        new("Contrast Enhancement Max Deviation", ParamKind.Float, 440, new(4.0f, 0f, 0f, 0f)),
        new("Bloom Strength", ParamKind.Float, 444, new(1.0f, 0f, 0f, 0f)),
        new("Bloom Blur Size", ParamKind.Float, 448, new(2.5f, 0f, 0f, 0f)),
        new("Bloom Threshold", ParamKind.Float, 452, new(3.5f, 0f, 0f, 0f)),
        new("Radial Blur Factor", ParamKind.Float, 456, new(0.2f, 0f, 0f, 0f)),
        new("Filmic Exposure", ParamKind.Float, 460, new(0.0f, 0f, 0f, 0f)),
        new("Filmic Saturation", ParamKind.Float, 464, new(1.0f, 0f, 0f, 0f)),
        new("Filmic Contrast", ParamKind.Float, 468, new(1.0f, 0f, 0f, 0f)),
        new("Filmic Colour Balance", ParamKind.Vector, 480, new(1.0f, 1.0f, 1.0f, 1.0f)),
        new("Fogging Start", ParamKind.Float, 496, new(50.0f, 0f, 0f, 0f)),
        new("Near Distance", ParamKind.Float, 500, new(40.0f, 0f, 0f, 0f)),
        new("Near Density", ParamKind.Float, 504, new(0.3f, 0f, 0f, 0f)),
        new("Far Distance", ParamKind.Float, 508, new(1000.0f, 0f, 0f, 0f)),
        new("Far Density", ParamKind.Float, 512, new(0.3f, 0f, 0f, 0f)),
        new("Close Fog Colour", ParamKind.Vector, 528, new(1.0f, 1.0f, 1.0f, 1.0f)),
        new("Close Fog Max Distance", ParamKind.Float, 544, new(0.0f, 0f, 0f, 0f)),
        new("Wind Strength Minimum", ParamKind.Float, 548, new(1.5f, 0f, 0f, 0f)),
        new("Wind Strength Maximum", ParamKind.Float, 552, new(2.5f, 0f, 0f, 0f)),
        new("Wind Strength Variation", ParamKind.Float, 556, new(0.05f, 0f, 0f, 0f)),
        new("Wind XY Rotation Minimum", ParamKind.Float, 560, new(45.0f, 0f, 0f, 0f)),
        new("Wind XY Rotation Maximum", ParamKind.Float, 564, new(135.0f, 0f, 0f, 0f)),
        new("Wind Elevation Minimum", ParamKind.Float, 568, new(0.0f, 0f, 0f, 0f)),
        new("Wind Elevation Maximum", ParamKind.Float, 572, new(45.0f, 0f, 0f, 0f)),
        new("Wind Direction Variation", ParamKind.Float, 576, new(0.1f, 0f, 0f, 0f)),
        new("Wind Change Frequency", ParamKind.Float, 580, new(5.0f, 0f, 0f, 0f)),
        new("Wind Change Duration", ParamKind.Float, 584, new(1.0f, 0f, 0f, 0f)),
        new("Cloud Texture 1", ParamKind.Texture, 588, default),
        new("Cloud Transparency 1", ParamKind.Float, 604, new(0.8f, 0f, 0f, 0f)),
        new("Cloud Normal Strength 1", ParamKind.Float, 608, new(0.5f, 0f, 0f, 0f)),
        new("Cloud Height 1", ParamKind.Float, 612, new(500.0f, 0f, 0f, 0f)),
        new("Cloud Ambient Light 1", ParamKind.Float, 616, new(0.9f, 0f, 0f, 0f)),
        new("Cloud Brightness 1", ParamKind.Float, 620, new(1.0f, 0f, 0f, 0f)),
        new("Cloud Size X 1", ParamKind.Float, 624, new(3000.0f, 0f, 0f, 0f)),
        new("Cloud Size Y 1", ParamKind.Float, 628, new(3000.0f, 0f, 0f, 0f)),
        new("Cloud Scale X 1", ParamKind.Float, 632, new(5.0f, 0f, 0f, 0f)),
        new("Cloud Scale Y 1", ParamKind.Float, 636, new(5.0f, 0f, 0f, 0f)),
        new("Cloud Velocity X 1", ParamKind.Float, 640, new(10.0f, 0f, 0f, 0f)),
        new("Cloud Velocity Y 1", ParamKind.Float, 644, new(20.0f, 0f, 0f, 0f)),
        new("Cloud Texture 2", ParamKind.Texture, 648, default),
        new("Cloud Transparency 2", ParamKind.Float, 664, new(0.8f, 0f, 0f, 0f)),
        new("Cloud Normal Strength 2", ParamKind.Float, 668, new(0.5f, 0f, 0f, 0f)),
        new("Cloud Height 2", ParamKind.Float, 672, new(250.0f, 0f, 0f, 0f)),
        new("Cloud Ambient Light 2", ParamKind.Float, 676, new(1.0f, 0f, 0f, 0f)),
        new("Cloud Brightness 2", ParamKind.Float, 680, new(1.0f, 0f, 0f, 0f)),
        new("Cloud Size X 2", ParamKind.Float, 684, new(3000.0f, 0f, 0f, 0f)),
        new("Cloud Size Y 2", ParamKind.Float, 688, new(3000.0f, 0f, 0f, 0f)),
        new("Cloud Scale X 2", ParamKind.Float, 692, new(6.0f, 0f, 0f, 0f)),
        new("Cloud Scale Y 2", ParamKind.Float, 696, new(6.0f, 0f, 0f, 0f)),
        new("Cloud Velocity X 2", ParamKind.Float, 700, new(5.0f, 0f, 0f, 0f)),
        new("Cloud Velocity Y 2", ParamKind.Float, 704, new(10.0f, 0f, 0f, 0f)),
        new("Strength", ParamKind.Float, 708, new(1.0f, 0f, 0f, 0f)),
        new("Disable Vista Water", ParamKind.Float, 712, new(0.0f, 0f, 0f, 0f)),
        new("Ocean Water Height Offset", ParamKind.Float, 716, new(0.0f, 0f, 0f, 0f)),
        new("Water Normal Map", ParamKind.Texture, 720, default),
        new("Water Waves Direction 1", ParamKind.Float, 736, new(0.0f, 0f, 0f, 0f)),
        new("Water Waves Direction 2", ParamKind.Float, 740, new(0.25f, 0f, 0f, 0f)),
        new("Water Waves Speed 1", ParamKind.Float, 744, new(0.1f, 0f, 0f, 0f)),
        new("Water Waves Speed 2", ParamKind.Float, 748, new(0.4f, 0f, 0f, 0f)),
        new("Water Waves Scale 1", ParamKind.Float, 752, new(0.25f, 0f, 0f, 0f)),
        new("Water Waves Scale 2", ParamKind.Float, 756, new(1.0f, 0f, 0f, 0f)),
        new("Water Waves Blend Value", ParamKind.Float, 760, new(0.4f, 0f, 0f, 0f)),
        new("Water Colour Extinction Depth Red", ParamKind.Float, 764, new(4.0f, 0f, 0f, 0f)),
        new("Water Colour Extinction Depth Green", ParamKind.Float, 768, new(8.0f, 0f, 0f, 0f)),
        new("Water Colour Extinction Depth Blue", ParamKind.Float, 772, new(10.0f, 0f, 0f, 0f)),
        new("Water Fresnel Power", ParamKind.Float, 776, new(5.0f, 0f, 0f, 0f)),
        new("Water Perturbation Strength", ParamKind.Float, 780, new(0.5f, 0f, 0f, 0f)),
        new("Water Reflection Strength", ParamKind.Float, 784, new(1.0f, 0f, 0f, 0f)),
        new("Water Depth Fade Start", ParamKind.Float, 788, new(0.02f, 0f, 0f, 0f)),
        new("Water Depth Fade End", ParamKind.Float, 792, new(4.0f, 0f, 0f, 0f)),
        new("Water Glittering Scatter Factor", ParamKind.Float, 796, new(0.1f, 0f, 0f, 0f)),
        new("Water Glittering Strength", ParamKind.Float, 800, new(0.1f, 0f, 0f, 0f)),
        new("Water Glittering Shininess", ParamKind.Float, 804, new(128.0f, 0f, 0f, 0f)),
        new("Depth of Field Near Plane", ParamKind.Float, 808, new(0.0f, 0f, 0f, 0f)),
        new("Depth of Field Far Plane", ParamKind.Float, 812, new(60.0f, 0f, 0f, 0f)),
        new("Depth of Field Near Focal Plane", ParamKind.Float, 816, new(5.0f, 0f, 0f, 0f)),
        new("Depth of Field Far Focal Plane", ParamKind.Float, 820, new(5.0f, 0f, 0f, 0f)),
        new("Depth of Field Near Blur Amount", ParamKind.Float, 824, new(0.0f, 0f, 0f, 0f)),
        new("Depth of Field Far Blur Amount", ParamKind.Float, 828, new(0.0f, 0f, 0f, 0f)),
        new("Dust Alpha", ParamKind.Float, 832, new(0.0f, 0f, 0f, 0f)),
        new("Dust Brightness", ParamKind.Float, 836, new(1.0f, 0f, 0f, 0f)),
        new("Dust Multiply", ParamKind.Float, 840, new(0.0f, 0f, 0f, 0f)),
        new("Dust Wind Speed", ParamKind.Float, 844, new(30.0f, 0f, 0f, 0f)),
        new("Dust Wind Direction X", ParamKind.Float, 848, new(1.0f, 0f, 0f, 0f)),
        new("Dust Wind Direction Y", ParamKind.Float, 852, new(0.0f, 0f, 0f, 0f)),
        new("Dust Wind Direction Z", ParamKind.Float, 856, new(0.0f, 0f, 0f, 0f)),
        new("Dust Texture", ParamKind.Texture, 860, default),
        new("Dust Colour", ParamKind.Vector, 880, new(0.98f, 0.84f, 0.42f, 1.0f)),
        new("Fresnel Material Scale", ParamKind.Float, 896, new(1.0f, 0f, 0f, 0f)),
        new("Fresnel Material Distance Falloff Start", ParamKind.Float, 900, new(100.0f, 0f, 0f, 0f)),
        new("Fresnel Material Distance Falloff End", ParamKind.Float, 904, new(100.0f, 0f, 0f, 0f)),
        new("Tree Mesh Translucency Factor", ParamKind.Float, 908, new(1.0f, 0f, 0f, 0f)),
        new("Tree Billboard Translucency Factor", ParamKind.Float, 912, new(1.0f, 0f, 0f, 0f)),
    };

    /// <summary><c>Data\NewEnvironmentThemes\</c> + <c>EnvironmentThemeMap.txt</c>
    /// (<c>sub_8248B530</c>).</summary>
    public const string MapPath = "newenvironmentthemes/environmentthememap.txt";

    /// <summary>The map entry <c>sub_8248BDC0</c> loads into the Global Layer.</summary>
    public const string BaseThemeName = "envthemebase";

    private static readonly Dictionary<string, int> ParamIndex = BuildParamIndex();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], Engine_EnvThemeFile>
        Parsed = new();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], List<MapEntry>>
        ParsedMaps = new();

    private readonly Dictionary<string, Timeline> _timelines = new(StringComparer.Ordinal);

    private Engine_EnvThemeFile()
    {
    }

    public int Version { get; private set; }

    public float FadeInTime { get; private set; } = 5f;

    public float FadeOutTime { get; private set; } = 5f;

    public bool FullyBlendLayer { get; private set; }

    public IReadOnlyCollection<string> ParamNames => _timelines.Keys;

    public readonly record struct Key(float Time, Vector4 Value, string Texture);

    public sealed class Timeline
    {
        public Timeline(ParamKind kind, List<Key> keys)
        {
            Kind = kind;
            Keys = keys;
        }

        public ParamKind Kind { get; }

        public IReadOnlyList<Key> Keys { get; }

        /// <summary>
        /// <c>sub_82228BD0</c>: the first key at or after the time is the upper end and the one
        /// before it the lower; before the first key both ends are the first key; past the last
        /// the upper end is the FIRST key at time 1. f = (t - t0) / (t1 - t0) when t1 &gt; t0,
        /// else 1, and the value is lerped (float <c>0x82228B40</c>, vector <c>0x8223E828</c>
        /// per component). A texture cross-fades the two (<c>sub_822364B0</c>); no shipped
        /// timeline keeps two different textures, so the lower end's is the answer.
        /// </summary>
        public Key Evaluate(float time)
        {
            if (Keys.Count == 1)
            {
                return Keys[0];
            }

            Key lower = Keys[0];
            Key upper = Keys[0];
            float upperTime = 1f;
            bool found = false;
            foreach (Key key in Keys)
            {
                if (time <= key.Time)
                {
                    upper = key;
                    upperTime = key.Time;
                    found = true;
                    break;
                }

                lower = key;
            }

            if (!found)
            {
                upper = Keys[0];
                upperTime = 1f;
            }

            float f = upperTime > lower.Time ? (time - lower.Time) / (upperTime - lower.Time) : 1f;
            return new Key(time, lower.Value + ((upper.Value - lower.Value) * f), lower.Texture);
        }
    }

    /// <summary>The evaluated theme block: every registered parameter's value at one time,
    /// and whether a layer stated it or it is the guest's default.</summary>
    public sealed class Block
    {
        internal Block()
        {
            Values = new Vector4[Params.Count];
            Textures = new string[Params.Count];
            Stated = new bool[Params.Count];
            for (int i = 0; i < Params.Count; i++)
            {
                Values[i] = Params[i].Default;
                Textures[i] = string.Empty;
            }
        }

        public Vector4[] Values { get; }

        public string[] Textures { get; }

        public bool[] Stated { get; }

        public float Float(string name) => ParamIndex.TryGetValue(name, out int i) ? Values[i].X : 0f;

        public Vector4 Vector(string name) => ParamIndex.TryGetValue(name, out int i) ? Values[i] : default;

        public string Texture(string name) =>
            ParamIndex.TryGetValue(name, out int i) ? Textures[i] : string.Empty;

        public bool IsStated(string name) => ParamIndex.TryGetValue(name, out int i) && Stated[i];

        /// <summary>A texture slot as the hash <see cref="Engine_EnvironmentTextures"/> resolves:
        /// FNV-1 of the lowered <c>.tex</c> path. Zero when the slot holds none.</summary>
        public uint TextureHash(string name)
        {
            string path = Texture(name);
            return path.Length == 0 ? 0u : Engine_EnvironmentTextures.Hash(path, lowercase: true);
        }
    }

    public static bool IsEnvTheme(byte[] bytes) => TryGet(bytes, out _);

    /// <summary>The parsed theme for these bytes, or false when they are not an
    /// <c>EnvironmentTheme</c> document. Parsed once per array.</summary>
    public static bool TryGet(byte[] bytes, out Engine_EnvThemeFile theme)
    {
        theme = null!;
        if (bytes is null || bytes.Length < 5)
        {
            return false;
        }

        if (Parsed.TryGetValue(bytes, out Engine_EnvThemeFile? cached))
        {
            theme = cached;
            return true;
        }

        if (!LooksLikeXml(bytes) || !TryParse(bytes, out theme))
        {
            return false;
        }

        Parsed.AddOrUpdate(bytes, theme);
        return true;
    }

    public static bool TryParse(byte[] bytes, out Engine_EnvThemeFile theme)
    {
        theme = null!;
        XElement root;
        try
        {
            using var stream = new System.IO.MemoryStream(bytes, writable: false);
            root = XDocument.Load(stream).Root!;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }

        if (root is null || root.Name.LocalName != "EnvironmentTheme")
        {
            return false;
        }

        var parsed = new Engine_EnvThemeFile();
        _ = int.TryParse((string?)root.Attribute("version"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int version);
        parsed.Version = version;

        if (root.Element("FadeInTime") is XElement fadeIn)
        {
            parsed.FadeInTime = ReadFloat(fadeIn.Value);
        }

        if (root.Element("FadeOutTime") is XElement fadeOut)
        {
            parsed.FadeOutTime = ReadFloat(fadeOut.Value);
        }

        if (root.Element("FullyBlendLayer") is XElement fully)
        {
            parsed.FullyBlendLayer = string.Equals(fully.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }

        if (root.Element("ThemeParams") is XElement themeParams)
        {
            foreach (XElement param in themeParams.Elements("ThemeParam"))
            {
                string name = (string?)param.Attribute("Name") ?? string.Empty;
                ParamKind kind = KindOf((string?)param.Attribute("param_type") ?? string.Empty);
                var raw = new List<Key>();
                foreach (XElement item in param.Elements("TimelineItem"))
                {
                    raw.Add(ReadKey(item, kind));
                }

                parsed._timelines[name] = new Timeline(kind, Prune(raw, kind));
            }
        }

        theme = parsed;
        return true;
    }

    public bool TryGetTimeline(string name, out Timeline timeline) =>
        _timelines.TryGetValue(name, out timeline!);

    /// <summary>
    /// The block at a time of day (0..1) for a stack of themes, lowest layer first.
    /// </summary>
    /// <remarks>
    /// <c>sub_822288D0</c> resets the block to the defaults and asks <c>sub_82228D38</c> for each
    /// registered parameter: every layer whose themes state it replaces the value by its layer
    /// weight. The level stack <c>sub_82268600</c> builds is the Global Layer (the
    /// <c>envthemebase</c> map entry) then the Level Layer (the level's own theme), both at weight
    /// 1 with one theme at strength 1, so the level's key wins where it states one and the base's
    /// where only it does. A timeline whose kind is not the descriptor's is not used.
    /// </remarks>
    public static Block Evaluate(IReadOnlyList<Engine_EnvThemeFile> layers, float time)
    {
        var block = new Block();
        float t = time - MathF.Floor(time);
        for (int i = 0; i < Params.Count; i++)
        {
            Param param = Params[i];
            foreach (Engine_EnvThemeFile layer in layers)
            {
                if (!layer._timelines.TryGetValue(param.Name, out Timeline? timeline) ||
                    timeline.Keys.Count == 0 || timeline.Kind != param.Kind)
                {
                    continue;
                }

                Key key = timeline.Evaluate(t);
                block.Values[i] = key.Value;
                block.Textures[i] = key.Texture;
                block.Stated[i] = true;
            }
        }

        return block;
    }

    /// <summary>
    /// The level's theme stack, appended to a gdb chain: the base theme, then the theme the
    /// level states. False when the level states none.
    /// </summary>
    /// <remarks>
    /// <c>sub_8248BDC0</c>: the Global Layer's theme is the map entry named
    /// <c>envthemebase</c> (<c>sub_8248CA58</c>, matched on the lowered name); the level's is
    /// <c>LevelSpecificData.EnvironmentTheme</c> — FNV-1 record and field names, the record
    /// reached through the level gdb's alias table — whose raw value is a map entry's id
    /// (<c>sub_821766D0</c> matches entry+16). Each entry's path is relative to <c>Data\</c>.
    /// The world-map levels carry no LevelSpecificData and get the base alone.
    /// </remarks>
    public static bool AppendLevelThemes(
        byte[]? levelGdb, Func<string, byte[]?> read, List<byte[]> chain)
    {
        byte[]? mapBytes = read(MapPath) ?? read("data/" + MapPath);
        if (mapBytes is null)
        {
            return false;
        }

        List<MapEntry> map = ReadMap(mapBytes);
        RememberMap(map, read);
        bool any = false;

        foreach (MapEntry entry in map)
        {
            if (string.Equals(entry.Name, BaseThemeName, StringComparison.OrdinalIgnoreCase))
            {
                any |= AppendTheme(entry, read, chain);
                break;
            }
        }

        if (levelGdb is not null && TryLevelThemeId(levelGdb, out uint id))
        {
            foreach (MapEntry entry in map)
            {
                if (entry.Id == id)
                {
                    any |= AppendTheme(entry, read, chain);
                    break;
                }
            }
        }

        return any;
    }

    private static readonly object MapLock = new();
    private static List<MapEntry>? _map;
    private static Func<string, byte[]?>? _mapRead;
    private static readonly Dictionary<uint, (Engine_EnvThemeFile Theme, string Name)?> MapThemes = new();

    /// <summary>The title's map and the reader it was found through, for <see cref="TryMapTheme"/>.
    /// One map per install: every level's chain reads the same file.</summary>
    private static void RememberMap(List<MapEntry> map, Func<string, byte[]?> read)
    {
        lock (MapLock)
        {
            if (!ReferenceEquals(_map, map))
            {
                MapThemes.Clear();
            }

            _map = map;
            _mapRead = read;
        }
    }

    /// <summary>
    /// A theme by its <c>EnvironmentThemeMap.txt</c> id — what a painted cell holds — loaded once.
    /// False when no map has been read or no entry carries the id.
    /// </summary>
    /// <remarks>
    /// THE PAINTED LAYER'S THEME (Fable 3). <c>sub_82261768</c> samples the <c>.genv</c>'s
    /// day-set grid at the player (<c>sub_82298098</c> 0x8226185c) and hands the cell's word to
    /// <c>sub_821766D0</c> (0x82261868), which walks the map's 24-byte entries
    /// (<c>dword_83558008</c>..<c>dword_8355800C</c>, filled by <c>sub_8248B530</c> from this file)
    /// for <c>entry+16 == word</c>; <c>sub_8248CB10</c> then loads the entry's <c>.envtheme</c>
    /// into <c>entry+8</c> through <c>sub_82C450F8</c>. So a cell names a map id, exactly as
    /// <c>LevelSpecificData.EnvironmentTheme</c> does, and never a gdb record.
    /// </remarks>
    public static bool TryMapTheme(uint id, out Engine_EnvThemeFile theme, out string name)
    {
        theme = null!;
        name = string.Empty;
        lock (MapLock)
        {
            if (_map is null || _mapRead is null)
            {
                return false;
            }

            if (!MapThemes.TryGetValue(id, out (Engine_EnvThemeFile Theme, string Name)? found))
            {
                found = null;
                foreach (MapEntry entry in _map)
                {
                    if (entry.Id != id)
                    {
                        continue;
                    }

                    string path = VirtualPath(entry.Path);
                    byte[]? bytes = _mapRead(path) ?? _mapRead("data/" + path);
                    if (bytes is not null && TryGet(bytes, out Engine_EnvThemeFile file))
                    {
                        found = (file, entry.Name);
                    }

                    break;
                }

                MapThemes[id] = found;
            }

            if (found is not { } hit)
            {
                return false;
            }

            theme = hit.Theme;
            name = hit.Name;
            return true;
        }
    }

    /// <summary>The map id <c>LevelSpecificData.EnvironmentTheme</c> holds in a level gdb.</summary>
    public static bool TryLevelThemeId(byte[] levelGdb, out uint id)
    {
        id = 0;
        if (!GdbView.TryParseShared(levelGdb, out GdbView view, out _) ||
            !view.TryLookup(Refii.Core.Engine_GameHash.Fnv1("LevelSpecificData"), out int record) ||
            !view.TryFindLocal(record, Refii.Core.Engine_GameHash.Fnv1("EnvironmentTheme"), -1, out GdbField field))
        {
            return false;
        }

        id = field.Raw;
        return id != 0 && id != EnvHash.Null;
    }

    public readonly record struct MapEntry(uint Id, string Name, string Path, bool Preload);

    /// <summary>
    /// <c>EnvironmentThemeMap.txt</c> (<c>sub_8248B530</c>): an optional <c>version=</c> line,
    /// then per theme its id in hex, its name, its path, and — only under a version line — a
    /// preload flag (<c>true</c>).
    /// </summary>
    public static List<MapEntry> ReadMap(byte[] bytes)
    {
        if (ParsedMaps.TryGetValue(bytes, out List<MapEntry>? cached))
        {
            return cached;
        }

        string[] lines = Encoding.Latin1.GetString(bytes)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var entries = new List<MapEntry>();
        int at = 0;
        bool versioned = lines.Length != 0 &&
            lines[0].StartsWith("version=", StringComparison.OrdinalIgnoreCase);
        if (versioned)
        {
            at = 1;
        }

        int stride = versioned ? 4 : 3;
        for (; at + stride - 1 < lines.Length; at += stride)
        {
            if (!uint.TryParse(lines[at].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            {
                break;
            }

            bool preload = versioned &&
                string.Equals(lines[at + 3].Trim(), "true", StringComparison.OrdinalIgnoreCase);
            entries.Add(new MapEntry(id, lines[at + 1].Trim(), lines[at + 2].Trim(), preload));
        }

        ParsedMaps.AddOrUpdate(bytes, entries);
        return entries;
    }

    /// <summary>A map entry's path as a bank path: lowered, '/'-separated.</summary>
    public static string VirtualPath(string mapPath) =>
        mapPath.Replace('\\', '/').ToLowerInvariant();

    private static bool AppendTheme(MapEntry entry, Func<string, byte[]?> read, List<byte[]> chain)
    {
        string path = VirtualPath(entry.Path);
        byte[]? bytes = read(path) ?? read("data/" + path);
        if (bytes is null || !TryGet(bytes, out _))
        {
            return false;
        }

        chain.Add(bytes);
        return true;
    }

    private static Key ReadKey(XElement item, ParamKind kind)
    {
        float time = item.Element("Time") is XElement t ? ReadFloat(t.Value) : 0f;
        switch (kind)
        {
            case ParamKind.Vector:
            {
                XElement? value = item.Element("Value");
                if (value is null)
                {
                    return new Key(time, Vector4.Zero, string.Empty);
                }

                return new Key(time, new Vector4(
                    Component(value, "X"), Component(value, "Y"),
                    Component(value, "Z"), Component(value, "W")), string.Empty);
            }

            case ParamKind.Texture:
                return new Key(time, Vector4.Zero, TexturePath((string?)item.Element("TexturePath") ?? string.Empty));

            default:
                return new Key(time,
                    new Vector4(item.Element("Value") is XElement v ? ReadFloat(v.Value) : 0f, 0f, 0f, 0f),
                    string.Empty);
        }
    }

    private static float Component(XElement value, string name) =>
        value.Element(name) is XElement e ? ReadFloat(e.Value) : 0f;

    /// <summary>The 0x82C462D0 prune: key 0, then each middle key unless it equals both
    /// neighbours (the value's own equality, vtable +0x20); the last key is dropped.</summary>
    private static List<Key> Prune(List<Key> raw, ParamKind kind)
    {
        var keys = new List<Key>();
        if (raw.Count == 0)
        {
            return keys;
        }

        keys.Add(raw[0]);
        for (int i = 1; i + 1 < raw.Count; i++)
        {
            if (!SameValue(raw[i], raw[i - 1], kind) || !SameValue(raw[i], raw[i + 1], kind))
            {
                keys.Add(raw[i]);
            }
        }

        return keys;
    }

    private static bool SameValue(Key a, Key b, ParamKind kind) =>
        kind == ParamKind.Texture
            ? string.Equals(a.Texture, b.Texture, StringComparison.Ordinal)
            : a.Value == b.Value;

    /// <summary><c>sub_82C47660</c>: the path's extension replaced by <c>.tex</c>; empty stays
    /// empty.</summary>
    private static string TexturePath(string path)
    {
        path = path.Trim();
        if (path.Length == 0)
        {
            return string.Empty;
        }

        int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        int dot = path.LastIndexOf('.');
        string stem = dot > slash ? path[..dot] : path;
        return stem + ".tex";
    }

    /// <summary><c>sub_8310D2C0</c>: the hex bits after the first space, else the decimal.</summary>
    private static float ReadFloat(string text)
    {
        text = text.Trim();
        int space = text.IndexOf(' ');
        if (space >= 0 &&
            uint.TryParse(text[(space + 1)..].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits))
        {
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        string number = space >= 0 ? text[..space] : text;
        return float.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : 0f;
    }

    private static ParamKind KindOf(string type) => type switch
    {
        "rgbcolour" or "rgbacolour" or "rgbcolourwithfactor" => ParamKind.Vector,
        "texture" => ParamKind.Texture,
        _ => ParamKind.Float,
    };

    private static bool LooksLikeXml(byte[] bytes)
    {
        int i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            i = 3;
        }

        while (i < bytes.Length && (bytes[i] == ' ' || bytes[i] == '\t' || bytes[i] == '\r' || bytes[i] == '\n'))
        {
            i++;
        }

        return i < bytes.Length && bytes[i] == '<';
    }

    private static Dictionary<string, int> BuildParamIndex()
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Params.Count; i++)
        {
            index[Params[i].Name] = i;
        }

        return index;
    }
}
