using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// How much of Godot's instance-uniform buffer the scene holds: every geometry node whose materials
/// declare an <c>instance uniform</c>, by owner, against the slots the project's buffer has.
/// </summary>
/// <remarks>
/// For the debug channel's `instanceuniforms` op. Godot gives each such instance 16 vec4s of
/// `rendering/limits/global_shader_variables/buffer_size`, visible or not. Past the last slot it
/// logs "Too many instances using shader instance variables" and draws the instance with an offset
/// of -1, which reads the global shader variables in place of its own values: a character drawn
/// one flat colour that changes with the time of day. Instances the renderer holds without a node
/// (some effects) are not counted, so the total is a floor.
/// </remarks>
public static class Engine_InstanceUniformCensus
{
    /// <summary>The vec4s Godot reserves for one instance (ShaderLanguage::MAX_INSTANCE_UNIFORM_INDICES).</summary>
    public const int SlotsPerInstance = 16;

    /// <summary>The instances the project's buffer has room for, with its platform override as Godot reads it;
    /// the global variables take a few of them.</summary>
    public static int Capacity() =>
        ProjectSettings.GetSettingWithOverride("rendering/limits/global_shader_variables/buffer_size").AsInt32() / SlotsPerInstance;

    public static Dictionary<string, object?> Describe(Node root)
    {
        var owners = new Dictionary<ulong, string>();
        var tallies = new SortedDictionary<string, int>();
        int total = 0, nodes = 0;
        foreach (Node found in root.FindChildren("*", nameof(GeometryInstance3D), true, false))
        {
            if (found is not GeometryInstance3D g)
            {
                continue;
            }
            nodes++;
            if (RenderingServer.InstanceGeometryGetShaderParameterList(g.GetInstance()).Count == 0)
            {
                continue;
            }
            total++;
            string owner = Engine_DrawCensus.OwnerOf(g, owners) + (g is MultiMeshInstance3D ? " batch" : "");
            tallies[owner] = tallies.GetValueOrDefault(owner) + 1;
        }
        var rows = new List<Dictionary<string, object?>>();
        foreach ((string name, int count) in tallies)
        {
            rows.Add(new Dictionary<string, object?> { ["name"] = name, ["instances"] = count });
        }
        return new Dictionary<string, object?>
        {
            ["instances"] = total,
            ["capacity"] = Capacity(),
            ["geometryNodes"] = nodes,
            ["rows"] = rows,
        };
    }
}
