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

    private readonly float[] _raw = new float[MaxVolumes * Refii.Host.HostBridge.ClassicMistStride];
    private readonly Refii.Models.Engine_ClassicMist.Volume _volume = new();
    private readonly List<(FogVolume Node, ShaderMaterial Material)> _drawn = new();
    private Shader? _shader;

    /// <summary>Volumes drawn at the last pump.</summary>
    public int Drawn { get; private set; }

    /// <summary>The modern mist under <paramref name="host"/>, made on first use.</summary>
    public static Engine_ModernMist Ensure(Node host)
    {
        if (host.GetNodeOrNull<Engine_ModernMist>(nameof(Engine_ModernMist)) is { } existing)
        {
            return existing;
        }
        var mist = new Engine_ModernMist { Name = nameof(Engine_ModernMist) };
        host.AddChild(mist);
        return mist;
    }

    /// <summary>Follows the guest's mist objects. Main thread, once a frame.</summary>
    public void Pump()
    {
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
                _drawn.Add((node, material));
            }
            Place(_drawn[i].Node, _drawn[i].Material, _volume, strength);
        }
        for (int i = count; i < _drawn.Count; i++)
        {
            _drawn[i].Node.Visible = false;
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

    private static void Place(FogVolume node, ShaderMaterial material, Refii.Models.Engine_ClassicMist.Volume v, float strength)
    {
        // Guest (x, y, z) is Godot (-x, z, y); the layer's top is floor + height when stated.
        float top = v.Extents.Z > 0f ? Mathf.Min(v.Max.Z, v.Origin.Z + v.Extents.Z) : v.Max.Z;
        var min = new Vector3(-v.Max.X, v.Min.Z, v.Min.Y);
        var max = new Vector3(-v.Min.X, top, v.Max.Y);
        Vector3 size = (max - min).Abs();
        bool shown = size.X > 0.01f && size.Y > 0.01f && size.Z > 0.01f && v.Density > 0f;
        node.Visible = shown;
        if (!shown)
        {
            return;
        }
        node.Size = size;
        node.GlobalPosition = (min + max) * 0.5f;
        float factor = (strength - 1f) * v.Weight + 1f;
        material.SetShaderParameter(AlbedoName, new Color(v.Colour.X, v.Colour.Y, v.Colour.Z));
        material.SetShaderParameter(ExtinctionName, v.Density * Mathf.Max(factor, 0f));
        material.SetShaderParameter(RemapName, v.Remap352);
        material.SetShaderParameter(MotionName, new Vector3(v.TilingX, v.TilingY, v.Speed));
        material.SetShaderParameter(FlattenName, v.NoiseOffset);
    }
}
