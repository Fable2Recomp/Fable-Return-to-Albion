using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// `terrain_lod_bias`: Godot's lod_bias on every ground chunk and vista patch, so the generated
/// terrain levels (`terrain_lod`) switch sooner or later than the props' `mesh_lod_pixels` rule.
/// </summary>
/// <remarks>
/// WHY A KNOB OF ITS OWN. The ground's levels are meshoptimizer's at 0.2%, 0.8% and 3% of a 32 m
/// chunk (6 cm, 26 cm, 1 m of error) with the border locked, so a coarser level changes no seam
/// and only the far relief; in open country the ground reaches the horizon and fills most of the
/// frame. Default 1 sets nothing (Godot's own default). New chunks take it as they are built; a
/// live change (cvar-set) walks the region's `Terrain` and `AdjacentTerrainN` containers.
/// </remarks>
public static class Engine_TerrainLodBias
{
    public const string Key = "terrain_lod_bias";

    static Engine_TerrainLodBias()
    {
        Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
        {
            if (name.Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                Current = Read();
                ApplyLive();
            }
        };
    }

    /// <summary>The bias as last read: at start-up, then on each live change. Held to [0.1, 4].</summary>
    public static float Current { get; private set; } = Read();

    /// <summary>Gives one chunk the bias; a chunk already at it is left alone.</summary>
    public static void Apply(GeometryInstance3D chunk)
    {
        if (chunk.LodBias != Current)
        {
            chunk.LodBias = Current;
        }
    }

    /// <summary>The configured value, held to Godot's accepted range.</summary>
    internal static float Clamp(float bias) => float.IsFinite(bias) && bias > 0f ? Mathf.Clamp(bias, 0.1f, 4f) : 1f;

    private static float Read()
    {
        try
        {
            return Clamp(Refii.Core.Engine_RefiiConfig.Float(Refii.Core.Engine_RefiiConfig.Read(out _), Key, 1f));
        }
        catch (Exception)
        {
            return 1f;
        }
    }

    /// <summary>Every live ground and vista chunk takes the current bias (main thread).</summary>
    private static void ApplyLive()
    {
        if (Engine.GetMainLoop() is not SceneTree { Root: { } root })
        {
            return;
        }

        foreach (string pattern in new[] { Engine_LevelScene.TerrainNodeName, Engine_LevelScene.AdjacentNodeName + "*" })
        {
            foreach (Node container in root.FindChildren(pattern, "Node3D", true, false))
            {
                if (container is GeometryInstance3D)
                {
                    continue;
                }

                foreach (Node child in container.GetChildren())
                {
                    if (child is GeometryInstance3D chunk)
                    {
                        Apply(chunk);
                    }
                }
            }
        }
    }
}
