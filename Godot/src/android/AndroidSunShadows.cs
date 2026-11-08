using System.Collections.Generic;
using Godot;
using Refii.Core;
using Refii.Lighting;

namespace Refii.AndroidRuntime;

/// <summary>
/// The sun's shadow map size (<c>sun_shadow_map</c>) and edge filter (<c>sun_shadow_filter</c>) on a phone,
/// applied at start and again whenever the Options panel writes the file.
/// </summary>
/// <remarks>
/// WHY THE PHONE'S SHADOWS STAIR-STEPPED. Godot's directional filter has a phone-only default of Hard
/// (<c>soft_shadow_filter_quality.mobile</c> 0), so project.godot's Soft Medium reached only the desktop, and
/// the host fixed the map at 1024 whatever the file said. Two cascades halve the map's height, so the S25+'s
/// near cascade had texels about 4.5 times the desktop's (4096), each read with one tap.
/// Measured on the S25+ at an Oakfield save (GPU-bound at 734 MHz, a relaunch per filter): Soft Very Low cost
/// nothing measurable but is grainy without TAA, Soft Low about 3% of the GPU's work a frame (+0.7 ms),
/// Soft Medium about 9% (+2 ms). A 2048 map against 1024, switched live in three pairs, cost nothing
/// measurable. A filter change rebuilds the Mobile renderer's specialised pipelines: one ~0.3 s hitch.
/// A map change also restates the sun's constant bias, which Engine_LevelLighting scales with the texel.
/// </remarks>
public sealed class AndroidSunShadows
{
    /// <summary>refii.toml's edge filter: Godot's RenderingServer.ShadowQuality, 0 (Hard) to 5 (Soft Ultra).</summary>
    public const string FilterKey = "sun_shadow_filter";

    /// <summary>refii.toml's shadow map size, the desktop's key.</summary>
    public const string MapKey = "sun_shadow_map";

    /// <summary>The map when the file names none: the size the host fixed before it read the file.</summary>
    public const int DefaultMap = 1024;

    private int _map;
    private int _filter = -1;

    /// <summary>Applies whichever of the two changed since the last call.</summary>
    /// <param name="values">The runtime configuration's values.</param>
    /// <param name="root">Where the level's sun is looked for when the map changes.</param>
    public void Apply(IReadOnlyDictionary<string, string> values, Node root)
    {
        int map = Mathf.Clamp(Engine_RefiiConfig.Int(values, MapKey, DefaultMap), 512, 4096);
        if (map != _map)
        {
            _map = map;
            RenderingServer.DirectionalShadowAtlasSetSize(map, true);
            Engine_LevelLighting.SunShadowMap = map;
            // As Engine_LevelLighting.BuildSun states it, for the sun already standing.
            if (root.FindChild(Engine_LevelLighting.SunNodeName, true, false) is DirectionalLight3D sun)
                sun.ShadowBias = 0.03f * Mathf.Max(1f, 4096f / map);
            GD.Print($"[android] sun shadow map {map}");
        }

        int filter = Mathf.Clamp(Engine_RefiiConfig.Int(values, FilterKey, 0), 0, 5);
        if (filter != _filter)
        {
            _filter = filter;
            RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)filter);
            GD.Print($"[android] sun shadow filter {(RenderingServer.ShadowQuality)filter}");
        }
    }
}
