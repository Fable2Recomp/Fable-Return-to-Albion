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
    private static readonly StringName FlattenName = "flatten";

    private sealed class DrawnMist
    {
        public required FogVolume Node;
        public required ShaderMaterial Material;
        public bool Applied;
        public bool Visible;
        public Vector3 Size;
        public Vector3 Position;
        public Color Albedo;
        public float Extinction;
        public Vector4 Remap;
        public Vector3 Motion;
        public float Flatten;
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

    /// <summary>Follows the guest's mist objects. Main thread, once a frame.</summary>
    public void Pump()
    {
        if (_pumpedOnce && !Refii.Core.Engine_GuestFrame.Changed)
        {
            return;
        }
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
                // The volumes are the only fog: the global density stays at zero.
                environment.VolumetricFogEnabled = true;
                environment.VolumetricFogDensity = 0f;
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

        var albedo = new Color(v.Colour.X, v.Colour.Y, v.Colour.Z);
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
