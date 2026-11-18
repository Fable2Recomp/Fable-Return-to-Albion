using System;
using Godot;

namespace Refii.Level;

public partial class Engine_PropStreamer
{
    private static bool PreferLowPrefetchConcurrency => Refii.Core.Engine_Platform.IsAndroid;
    /// <summary>
    /// Queues a streamed building prop for its occluders under `occlusion_culling`
    /// (Engine_BuildingOccluders); anything else, or the setting off, does nothing.
    /// </summary>
    private static void AddBuildingOccluder(GeometryInstance3D instance, Mesh mesh, string path)
    {
        if (Engine_BuildingOccluders.Enabled)
        {
            Vector3 size = mesh.GetAabb().Size;
            if (IsBuilding(instance, path, MathF.Max(size.X, MathF.Max(size.Y, size.Z))))
            {
                Engine_BuildingOccluders.Add(instance, mesh, instance is MultiMeshInstance3D placed
                    ? Engine_PropShadow.StatedTransforms(placed.Multimesh) ?? BatchTransforms(placed.Multimesh)
                    : null);
            }
        }
    }

    /// <summary>A batch's instance transforms read back from its buffer (one render-thread sync).</summary>
    private static Transform3D[] BatchTransforms(MultiMesh batch)
    {
        int count = batch.InstanceCount;
        float[] buffer = count > 0 ? Refii.Core.Engine_ServerSyncs.Buffer(batch) : Array.Empty<float>();
        int stride = count > 0 ? buffer.Length / count : 0;
        var placed = new Transform3D[stride >= 12 ? count : 0];
        for (int i = 0; i < placed.Length; i++)
        {
            int o = i * stride;
            placed[i] = new Transform3D(
                new Vector3(buffer[o], buffer[o + 4], buffer[o + 8]),
                new Vector3(buffer[o + 1], buffer[o + 5], buffer[o + 9]),
                new Vector3(buffer[o + 2], buffer[o + 6], buffer[o + 10]),
                new Vector3(buffer[o + 3], buffer[o + 7], buffer[o + 11]));
        }
        return placed;
    }
}
