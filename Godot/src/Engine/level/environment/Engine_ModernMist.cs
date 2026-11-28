using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>
/// The modern renderer's mist: every mist object the guest holds this frame (level mists and the
/// ones Lua spawns, with the scene's global strength) as a Godot <see cref="FogVolume"/> running
/// modern_mist.gdshader, so Godot's volumetric fog integrates and lights it.
/// </summary>
/// <remarks>
/// The title's mist (Engine_ClassicMist): a layer inside the volume's box below a top at
/// floor + height (c76.y + c76.x, the default full-height texture), with density
/// density * ((strength - 1) * weight + 1) shaped by the evolving 2D noise. While the guest states
/// any, the load-time bake of the level's .mist files (Engine_LevelScene) stands aside: the
/// guest's set already holds those volumes, and drawing both would double them.
/// </remarks>
public partial class Engine_ModernMist : Node3D
{
    private const string ShaderPath = "res://src/Engine/level/environment/modern_mist.gdshader";
    private const int MaxVolumes = 16;

    private static readonly StringName AlbedoName = "albedo";
    private static readonly StringName ExtinctionName = "extinction";
    private static readonly StringName RemapName = "remap";
    private static readonly StringName MotionName = "motion";
    private static readonly StringName NoisePhaseName = "noise_phase";
    private static readonly StringName FlattenName = "flatten";

    private sealed class DrawnMist
    {
        public required FogVolume Node;
        public required ShaderMaterial Material;
        public bool Applied;
        public bool Visible;
        public Vector3 Size;
        public Vector3 Position;
        public Vector3 Albedo;
        public float Extinction;
        public Vector4 Remap;
        public Vector3 Motion;
        public float Flatten;
        public double NoisePhase;
        public float PublishedNoisePhase = float.NaN;
    }

    private readonly float[] _raw = new float[MaxVolumes * Refii.Host.HostBridge.ClassicMistStride];
    private readonly Refii.Models.Engine_ClassicMist.Volume _volume = new();
    private readonly List<DrawnMist> _drawn = new();
    private Shader? _shader;
    private bool _pumpedOnce;

    /// <summary>The live instance.</summary>
    public static Engine_ModernMist? Instance { get; private set; }

    /// <summary>Volumes drawn at the last pump.</summary>
    public int Drawn { get; private set; }

    /// <summary>
    /// Copies up to <see cref="MaxVolumes"/> currently visible mist volumes into <paramref name="dst"/>
    /// with live time-scrolled procedural noise UV parameters for the volumetric light-shaft compute pass.
    /// </summary>
    public int SnapshotVolumes(System.Span<Refii.Lighting.Engine_VolumetricLightShafts.MistVolumeSnapshot> dst)
    {
        if (Drawn <= 0 || dst.IsEmpty)
        {
            return 0;
        }

        int limit = System.Math.Min(Drawn, _drawn.Count);
        int outCount = 0;
        for (int i = 0; i < limit && outCount < dst.Length; i++)
        {
            DrawnMist d = _drawn[i];
            if (!d.Visible || d.Extinction <= 1e-5f)
            {
                continue;
            }

            Vector3 half = d.Size * 0.5f;
            Vector3 min = d.Position - half;
            Vector3 max = d.Position + half;
            float phase = (float)d.NoisePhase;
            var scaleOffset = new Vector4(
                d.Remap.X * d.Motion.X,
                d.Remap.Y * d.Motion.Y,
                (d.Remap.Z * d.Motion.X) + phase,
                (d.Remap.W * d.Motion.Y) + phase);

            dst[outCount++] = new Refii.Lighting.Engine_VolumetricLightShafts.MistVolumeSnapshot(
                Min: min,
                Extinction: d.Extinction,
                Max: max,
                Flatten: d.Flatten,
                LinearAlbedo: new Vector3(
                    System.MathF.Max(d.Albedo.X, 0f),
                    System.MathF.Max(d.Albedo.Y, 0f),
                    System.MathF.Max(d.Albedo.Z, 0f)),
                NoiseScaleOffset: scaleOffset);
        }

        return outCount;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
        {
            Instance = null;
        }
    }

    /// <summary>The modern mist under <paramref name="host"/>, made on first use.</summary>
    public static Engine_ModernMist Ensure(Node host)
    {
        if (Instance is { } cached && IsInstanceValid(cached) && cached.GetParent() == host)
        {
            return cached;
        }
        if (host.GetNodeOrNull<Engine_ModernMist>(nameof(Engine_ModernMist)) is { } existing)
        {
            Instance = existing;
            return existing;
        }
        var mist = new Engine_ModernMist { Name = nameof(Engine_ModernMist) };
        host.AddChild(mist);
        Instance = mist;
        return mist;
    }

    /// <summary>Follows the guest's mist objects and advances host noise phases. Main thread, once a frame.</summary>
    public void Pump(double worldDelta = 0.0)
    {
        if (!_pumpedOnce || Refii.Core.Engine_GuestFrame.Changed)
        {
            _pumpedOnce = true;

            Engine_GuestAtmosphere.Read();
            int count = System.Math.Min((int)Refii.Host.HostBridge.GetClassicMists(_raw), MaxVolumes);
            _shader ??= GD.Load<Shader>(ShaderPath);
            float strength = Engine_GuestAtmosphere.MistStrength;
            int stride = Refii.Host.HostBridge.ClassicMistStride;
            for (int i = 0; i < count; i++)
            {
                Refii.Models.Engine_ClassicMist.Read(_raw, i * stride, _volume);
                if (i >= _drawn.Count)
                {
                    var material = new ShaderMaterial { Shader = _shader };
                    var node = new FogVolume
                    {
                        Name = $"LiveMist{i}",
                        Shape = RenderingServer.FogVolumeShape.Box,
                        Material = material,
                        TopLevel = true,
                    };
                    AddChild(node);
                    _drawn.Add(new DrawnMist { Node = node, Material = material });
                }
                Place(_drawn[i], _volume, strength);
            }
            for (int i = count; i < _drawn.Count; i++)
            {
                if (!_drawn[i].Applied || _drawn[i].Visible)
                {
                    _drawn[i].Node.Visible = false;
                    _drawn[i].Visible = false;
                    _drawn[i].Applied = true;
                }
            }

            if (count != Drawn)
            {
                Drawn = count;
                // The level's baked mist stands aside while the guest states its own set.
                if (Engine_LevelScene.BakedMist is { } baked && GodotObject.IsInstanceValid(baked))
                {
                    baked.Visible = count == 0;
                }
                if (count != 0 && Engine_SkyEnvironmentNode.Current?.Environment is { } environment)
                {
                    bool worldVolumetric = Refii.Lighting.Engine_LightingFrame.ModernModel &&
                                           Refii.Lighting.Engine_LightingFrame.VolumetricFogEnabled;
                    environment.VolumetricFogEnabled = true;
                    if (!worldVolumetric)
                    {
                        // The volumes are the only fog when world volumetric fog is off; global density stays at zero.
                        environment.VolumetricFogDensity = 0f;
                    }
                }
                else if (count == 0 && Engine_SkyEnvironmentNode.Current?.Environment is { } envOff)
                {
                    bool worldVolumetric = Refii.Lighting.Engine_LightingFrame.ModernModel &&
                                           Refii.Lighting.Engine_LightingFrame.VolumetricFogEnabled;
                    if (!worldVolumetric)
                    {
                        envOff.VolumetricFogEnabled = false;
                    }
                }
            }
        }

        if (Drawn <= 0)
        {
            return;
        }

        double dt = Engine_PhaseIntegrator.SanitizeDelta(worldDelta);
        for (int i = 0, limit = System.Math.Min(Drawn, _drawn.Count); i < limit; i++)
        {
            DrawnMist d = _drawn[i];
            if (!d.Visible)
            {
                continue;
            }

            if (dt > 0.0 && d.Motion.Z != 0f)
            {
                d.NoisePhase = Engine_PhaseIntegrator.AdvanceLinear(d.NoisePhase, d.Motion.Z, dt);
            }

            float phaseF = (float)d.NoisePhase;
            if (float.IsNaN(d.PublishedNoisePhase) || phaseF != d.PublishedNoisePhase)
            {
                d.PublishedNoisePhase = phaseF;
                d.Material.SetShaderParameter(NoisePhaseName, phaseF);
            }
        }
    }

    private static void Place(DrawnMist entry, Refii.Models.Engine_ClassicMist.Volume v, float strength)
    {
        // Guest (x, y, z) is Godot (-x, z, y); the layer's top is floor + height when stated.
        float top = v.Extents.Z > 0f ? Mathf.Min(v.Max.Z, v.Origin.Z + v.Extents.Z) : v.Max.Z;
        var min = new Vector3(-v.Max.X, v.Min.Z, v.Min.Y);
        var max = new Vector3(-v.Min.X, top, v.Max.Y);
        Vector3 size = (max - min).Abs();
        bool shown = size.X > 0.01f && size.Y > 0.01f && size.Z > 0.01f && v.Density > 0f;
        if (!entry.Applied || entry.Visible != shown)
        {
            entry.Node.Visible = shown;
            entry.Visible = shown;
        }
        if (!shown)
        {
            entry.Applied = true;
            return;
        }

        Vector3 pos = (min + max) * 0.5f;
        if (!entry.Applied || entry.Size.DistanceSquaredTo(size) > 1e-4f)
        {
            entry.Node.Size = size;
            entry.Size = size;
        }
        if (!entry.Applied || entry.Position.DistanceSquaredTo(pos) > 1e-4f)
        {
            entry.Node.GlobalPosition = pos;
            entry.Position = pos;
        }

        Vector3 albedo = v.Colour;
        float factor = (strength - 1f) * v.Weight + 1f;
        float extinction = v.Density * Mathf.Max(factor, 0f);
        var motion = new Vector3(v.TilingX, v.TilingY, v.Speed);

        if (!entry.Applied || entry.Albedo != albedo)
        {
            entry.Material.SetShaderParameter(AlbedoName, albedo);
            entry.Albedo = albedo;
        }
        if (!entry.Applied || Mathf.Abs(entry.Extinction - extinction) > 1e-5f)
        {
            entry.Material.SetShaderParameter(ExtinctionName, extinction);
            entry.Extinction = extinction;
        }
        if (!entry.Applied || entry.Remap != v.Remap352)
        {
            entry.Material.SetShaderParameter(RemapName, v.Remap352);
            entry.Remap = v.Remap352;
        }
        if (!entry.Applied || entry.Motion != motion)
        {
            entry.Material.SetShaderParameter(MotionName, motion);
            entry.Motion = motion;
        }
        if (!entry.Applied || entry.Flatten != v.NoiseOffset)
        {
            entry.Material.SetShaderParameter(FlattenName, v.NoiseOffset);
            entry.Flatten = v.NoiseOffset;
        }
        entry.Applied = true;
    }
}
