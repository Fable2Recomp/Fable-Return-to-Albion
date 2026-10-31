using System.Collections.Generic;
using System.Text.Json;
using Godot;
using Refii.Core;

namespace Refii.Game;

/// <summary>Opt-in fixed-camera tree rendering diagnostic; normal gameplay never requests it.</summary>
public partial class GameDebugServer
{
    private readonly Engine_TreeDistanceDiagnostic _treeDistanceDiagnostic = new();

    /// <summary>Applies the reversible diagnostic on the Godot thread, retaining all nearby tree bounds.</summary>
    private void ApplyTreeDistanceDiagnostic(JsonElement request) => _treeDistanceDiagnostic.ApplyRequest(
        request, () => ObserveTrees(GetTree().Root, GetViewport().GetCamera3D()?.GlobalPosition ?? Vector3.Zero),
        (id, visible) =>
        {
            if (GodotObject.InstanceFromId(id) is GeometryInstance3D node && GodotObject.IsInstanceValid(node))
                node.Visible = visible;
        });

    /// <summary>Copies current node visibility and nearest world-box distance without reading instance transforms.</summary>
    private static IEnumerable<Engine_TreeDistanceDiagnostic.Entry> ObserveTrees(Node parent, Vector3 eye)
    {
        if (parent is GeometryInstance3D node && node.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta))
        {
            Aabb? local = node switch
            {
                MeshInstance3D single when single.Mesh is not null => single.Mesh.GetAabb(),
                MultiMeshInstance3D multi when multi.Multimesh is not null => multi.Multimesh.GetAabb(),
                _ => null
            };
            if (local is { } bounds)
            {
                Aabb world = node.GlobalTransform * bounds;
                Vector3 delta = Vector3.Zero.Max(world.Position - eye).Max(eye - world.End);
                yield return new(node.GetInstanceId(), node.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString(),
                    node.Visible, delta.Length());
            }
        }
        foreach (Node child in parent.GetChildren())
            foreach (var entry in ObserveTrees(child, eye)) yield return entry;
    }
}
