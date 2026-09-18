using System;
using System.Collections.Generic;
using Godot;
using Refii.Level;

namespace Refii.Lighting;

[Tool]
public partial class Engine_PostFxSunSources : Node
{
    public const string NodeName = "PostFxSunSources";
    public const float SunBeamsIntensityDivisor = 6.0f;
    public const int BeamsMode = 0;
    public const int DiscMode = 1;

    public struct Source
    {
        public Rid Texture;
        public Vector3 Colour;
        public Vector2 Half;
        public float Distance;
        public int Mode;
        public bool Active;
    }

    public struct Sources
    {
        public Vector3 SunDirection;
        public Source Beams;
        public Source Disc;
    }

    private static readonly object Gate = new();
    private static Engine_PostFxSunSources? _current;
    private static Sources _published = new() { SunDirection = Vector3.Up };

    private static readonly AtmosphereState _frameAtmosphere = new();

    public static void Snapshot(out Sources sources)
    {
        lock (Gate)
        {
            sources = _published;
        }
    }

    public static void Ensure(Node context)
    {
        if (RenderingServer.GetRenderingDevice() is null)
        {
            return;
        }

        if (_current is not null && GodotObject.IsInstanceValid(_current))
        {
            return;
        }

        SceneTree? tree = context.GetTree();
        if (tree is null)
        {
            return;
        }

        var node = new Engine_PostFxSunSources { Name = NodeName };
        _current = node;
        tree.Root.CallDeferred(Node.MethodName.AddChild, node);
    }

    private SkyTheme? _lastSky;
    private SkyFrame? _lastFrame;
    private Vector3 _sunDirection = Vector3.Up;
    private Source _beams;
    private Source _disc;
    private bool _beamsEnabled;
    private bool _discEnabled;
    private readonly Dictionary<uint, (Texture2D? Texture, Rid Rd)> _textures = new();
    private readonly HashSet<uint> _reported = new();

    public override void _ExitTree()
    {
        if (_current == this)
        {
            _current = null;
        }

        lock (Gate)
        {
            _published = new Sources { SunDirection = Vector3.Up };
        }
    }

    public override void _Process(double delta)
    {
    	using var _framePhase = Refii.Core.Engine_FramePhases.Measure("postfxsunsources");
        SkyTheme? sky = Engine_PostFxParams.Sky;
        SkyFrame? frame = Engine_PostFxParams.Frame;
        if (sky is not null && frame is not null)
        {
            bool changed = !ReferenceEquals(sky, _lastSky) || !ReferenceEquals(frame, _lastFrame);
            bool pending = _attempts < TextureAttempts &&
                ((sky.HasSunBeamsTexture && !_beams.Texture.IsValid) ||
                    (sky.HasSunDiscTexture && !_disc.Texture.IsValid));

            if (changed)
            {
                _attempts = 0;
            }
            else if (pending)
            {
                _attempts++;
            }

            if (changed || pending)
            {
                Apply(sky, frame);
                _lastSky = sky;
                _lastFrame = frame;
            }
        }

        bool chain = Engine_PostFxParams.Enabled;
        Source beams = _beams;
        beams.Active = chain && _beamsEnabled && Engine_PostFxParams.Rays;
        Source disc = _disc;
        disc.Active = chain && _discEnabled && Engine_PostFxParams.Bloom;

        lock (Gate)
        {
            _published = new Sources { SunDirection = _sunDirection, Beams = beams, Disc = disc };
        }
    }

    private const int TextureAttempts = 600;

    private int _attempts;

    private void Apply(SkyTheme sky, SkyFrame frame)
    {
        float[] element = Engine_CelestialElements.ElementParams(sky);
        // Reused: thirteen arrays behind that class, and this runs every frame.
        AtmosphereState state = _frameAtmosphere;
        Engine_SkyAtmosphere.Compute(Engine_SkyAtmosphere.FromFrame(frame, sky), state);
        float[] tint = state.SunElementColour;

        Vector3 sun = Refii.Level.Engine_SkyFrame.ToGodot(frame.SunDirection);
        _sunDirection = sun.LengthSquared() > 1e-12f ? sun.Normalized() : Vector3.Up;
        bool sunActive = frame.SkyParams[0] >= Engine_CelestialElements.IntensityGate;

        Rid beamsTexture = sky.HasSunBeamsTexture ? LoadTexture(sky.SunBeamsTextureHash) : default;
        float beamsIntensity = element[13] / SunBeamsIntensityDivisor;
        _beamsEnabled = beamsTexture.IsValid && sunActive &&
            element[13] >= Engine_CelestialElements.IntensityGate;
        _beams = new Source
        {
            Texture = beamsTexture,
            Colour = new Vector3(tint[0] * beamsIntensity, tint[1] * beamsIntensity, tint[2] * beamsIntensity),
            Half = new Vector2(
                element[11] * Engine_CelestialElements.SunBeamsSizeScale,
                element[12] * Engine_CelestialElements.SunBeamsSizeScale),
            Distance = Engine_CelestialElements.SunBeamsDistance,
            Mode = BeamsMode,
        };

        Rid discTexture = sky.HasSunDiscTexture ? LoadTexture(sky.SunDiscTextureHash) : default;
        _discEnabled = discTexture.IsValid && sunActive &&
            element[7] >= Engine_CelestialElements.IntensityGate;
        _disc = new Source
        {
            Texture = discTexture,
            Colour = new Vector3(element[8] * element[7], element[9] * element[7], element[10] * element[7]),
            Half = new Vector2(
                element[6] * Engine_CelestialElements.SunDiscSizeScale,
                element[6] * Engine_CelestialElements.SunDiscSizeScale),
            Distance = Engine_CelestialElements.SunDiscDistance,
            Mode = DiscMode,
        };
    }

    private Rid LoadTexture(uint hash)
    {
        if (_textures.TryGetValue(hash, out (Texture2D? Texture, Rid Rd) cached))
        {
            return cached.Rd;
        }

        Texture2D? texture = Engine_EnvironmentTextures.Load(
            hash, Refii.Models.Engine_BnkMeshLoader.ReadBytes, out string path);
        Rid rd = default;
        if (texture is not null)
        {
            rd = RenderingServer.TextureGetRdTexture(texture.GetRid());
        }

        if (rd.IsValid)
        {
            _textures[hash] = (texture, rd);
            return rd;
        }

        if (_attempts >= TextureAttempts - 1 && _reported.Add(hash))
        {
            GD.Print(texture is null
                ? (path.Length == 0
                    ? $"[postfx] sun source texture 0x{hash:X8} unresolved after {TextureAttempts} frame(s)"
                    : $"[postfx] sun source texture '{path}' did not decode")
                : $"[postfx] sun source texture '{path}' has no rendering-device texture");
        }

        return rd;
    }
}
