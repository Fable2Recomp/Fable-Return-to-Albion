using System;
using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// The debug channel's `lightpairs`: how many omni and spot lights each instance in view is paired
/// with, as Godot's Mobile renderer pairs them, and so how many light() calls each draw runs.
/// </summary>
/// <remarks>
/// <para>WHAT MOBILE DOES. The scene cull pairs a geometry instance with every light whose AABB meets
/// the instance's AABB and whose cull mask meets its layers (renderer_scene_cull.cpp), at most eight
/// of each type. Each frame the draw's omni and spot lists keep the paired lights that made the
/// light buffer: in the frustum and not past their distance fade (light_storage.cpp,
/// `update_light_buffers`). The colour pass then runs light() once per listed light on every pixel
/// of the draw, black ones included (FABLE_LITE_SKIP_BLACK_LIGHTS only skips our part of the call).
/// A MultiMesh is ONE instance: its whole box is paired, so every copy of a batched cell shades
/// every light that reaches any part of the cell.</para>
/// <para>WHAT THIS COUNTS. The Light3D nodes (placed lamps, effect lights, mods' lights) and the
/// GeometryInstance3D nodes in the main view (layer 1, frustum), paired by that rule. The
/// `stable_light_counts` fillers are server objects with no node: with them on, every instance's
/// omni and spot loops run at least once (slot 0 is read before the end test), which
/// <c>callsPerDrawWithFillers</c> adds. Pixels are not known here, so the per-draw figures weight
/// every surface alike; a wall and a cup count the same. Debug only: a whole-tree walk.</para>
/// </remarks>
public static class AndroidLightPairCensus
{
    /// <summary>Godot Mobile's per-instance limit for each light type.</summary>
    public const int MaxPerType = 8;

    /// <summary>A light as the cull sees it: its world box, cull mask, whether it is a spot, and whether it shades anything.</summary>
    public readonly record struct LightBox(string Name, bool Spot, Aabb Box, uint CullMask, bool Live);

    /// <summary>A drawn instance: its world box, layers and surface count (its draws).</summary>
    public readonly record struct InstanceBox(Aabb Box, uint Layers, int Surfaces, bool Batch);

    /// <summary>The pairing tallies.</summary>
    public sealed class Tally
    {
        public int Instances, Surfaces, Batches;
        public int WithOmni, WithSpot, WithAny, WithLive;
        public int OmniPairs, SpotPairs, LivePairs;
        /// <summary>Instances by paired omni (and spot) count: 0, 1, 2, 3, 4 or more.</summary>
        public readonly int[] OmniHistogram = new int[5], SpotHistogram = new int[5];
        /// <summary>Per surface: light() calls of the two loops, without and with the fillers' floor of one each.</summary>
        public long LoopCalls, LoopCallsWithFillers;
        /// <summary>Instances each light is paired with, by light index.</summary>
        public int[] PerLight = Array.Empty<int>();
    }

    /// <summary>An omni light's world box: the range cube around it. Pure.</summary>
    public static Aabb OmniBox(Transform3D at, float range) =>
        at * new Aabb(new Vector3(-range, -range, -range), new Vector3(range, range, range) * 2f);

    /// <summary>A spot's world box as Godot builds it (light_storage.cpp): the cone's box down -Z, or the range cube past 90 degrees. Pure.</summary>
    public static Aabb SpotBox(Transform3D at, float range, float angleDegrees)
    {
        if (angleDegrees > 89.99f)
        {
            return OmniBox(at, range);
        }

        float size = MathF.Tan(Mathf.DegToRad(angleDegrees)) * range;
        return at * new Aabb(new Vector3(-size, -size, -range), new Vector3(size * 2f, size * 2f, range));
    }

    /// <summary>
    /// Whether a light is in the light buffer this frame: shown, in the frustum (the caller's test), and
    /// not past its distance fade from the camera. Pure.
    /// </summary>
    public static bool InBuffer(bool visible, bool fadeEnabled, float fadeBegin, float fadeLength, float cameraDistance) =>
        visible && (!fadeEnabled || cameraDistance <= fadeBegin + fadeLength);

    /// <summary>Pairs every instance with the lights in the buffer, Mobile's way. Pure.</summary>
    public static Tally Count(IReadOnlyList<LightBox> lights, IReadOnlyList<InstanceBox> instances, int maxPerType = MaxPerType)
    {
        var t = new Tally { PerLight = new int[lights.Count] };
        foreach (InstanceBox g in instances)
        {
            int omni = 0, spot = 0, live = 0;
            for (int i = 0; i < lights.Count; i++)
            {
                LightBox l = lights[i];
                if ((l.CullMask & g.Layers) == 0 || !l.Box.Intersects(g.Box))
                {
                    continue;
                }

                if (l.Spot ? spot >= maxPerType : omni >= maxPerType)
                {
                    continue;
                }

                if (l.Spot) spot++;
                else omni++;
                if (l.Live) live++;
                t.PerLight[i]++;
            }

            t.Instances++;
            t.Surfaces += g.Surfaces;
            if (g.Batch) t.Batches++;
            if (omni > 0) t.WithOmni++;
            if (spot > 0) t.WithSpot++;
            if (omni + spot > 0) t.WithAny++;
            if (live > 0) t.WithLive++;
            t.OmniPairs += omni;
            t.SpotPairs += spot;
            t.LivePairs += live;
            t.OmniHistogram[Math.Min(omni, 4)]++;
            t.SpotHistogram[Math.Min(spot, 4)]++;
            t.LoopCalls += (long)g.Surfaces * (omni + spot);
            t.LoopCallsWithFillers += (long)g.Surfaces * (Math.Max(omni, 1) + Math.Max(spot, 1));
        }

        return t;
    }

    /// <summary>The census of the scene under <paramref name="root"/> seen from <paramref name="camera"/>.</summary>
    public static Dictionary<string, object?> Describe(Node root, Camera3D camera, int top = 12)
    {
        Godot.Collections.Array<Plane> planes = camera.GetFrustum();
        Vector3 eye = camera.GlobalPosition;
        var lights = new List<LightBox>();
        var ranges = new List<float>();
        var hosted = new List<bool>();
        int offBuffer = 0, hostedAll = 0;
        // Lights under the entities' root go with it when the shader ceiling hides the entities.
        Node? entities = Refii.Game.GameEntities.Current is { } current && GodotObject.IsInstanceValid(current) ? current : null;
        foreach (Node found in root.FindChildren("*", nameof(Light3D), true, false))
        {
            if (found is not Light3D light || light is DirectionalLight3D)
            {
                continue;
            }

            bool entityHosted = entities is not null && entities.IsAncestorOf(light);
            hostedAll += entityHosted ? 1 : 0;

            float range;
            Aabb box;
            if (light is OmniLight3D omni)
            {
                range = omni.OmniRange;
                box = OmniBox(light.GlobalTransform, range);
            }
            else if (light is SpotLight3D spot)
            {
                range = spot.SpotRange;
                box = SpotBox(light.GlobalTransform, range, spot.SpotAngle);
            }
            else
            {
                continue;
            }

            if (!InBuffer(light.IsVisibleInTree(), light.DistanceFadeEnabled, light.DistanceFadeBegin,
                    light.DistanceFadeLength, eye.DistanceTo(light.GlobalPosition)) || !Inside(planes, box))
            {
                offBuffer++;
                continue;
            }

            Color c = light.LightColor;
            bool live = light.LightEnergy > 0f && (c.R > 0f || c.G > 0f || c.B > 0f);
            lights.Add(new LightBox(light.Name, light is SpotLight3D, box, light.LightCullMask, live));
            ranges.Add(range);
            hosted.Add(entityHosted);
        }

        var instances = new List<InstanceBox>();
        foreach (Node found in root.FindChildren("*", nameof(GeometryInstance3D), true, false))
        {
            if (found is not GeometryInstance3D g || !g.IsVisibleInTree() || (g.Layers & 1u) == 0 ||
                g.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
            {
                continue;
            }

            Mesh? mesh = g switch { MeshInstance3D mi => mi.Mesh, MultiMeshInstance3D mm => mm.Multimesh?.Mesh, _ => null };
            Aabb world = g.GlobalTransform * g.GetAabb();
            if (mesh is null || !Inside(planes, world))
            {
                continue;
            }

            instances.Add(new InstanceBox(world, g.Layers, Math.Max(1, mesh.GetSurfaceCount()), g is MultiMeshInstance3D));
        }

        Tally t = Count(lights, instances);
        int liveOmni = 0, liveSpot = 0, hostedInBuffer = 0, hostedLive = 0, hostedPairs = 0;
        for (int i = 0; i < lights.Count; i++)
        {
            LightBox l = lights[i];
            if (hosted[i])
            {
                hostedInBuffer++;
                hostedLive += l.Live ? 1 : 0;
                hostedPairs += t.PerLight[i];
            }

            if (!l.Live) continue;
            if (l.Spot) liveSpot++;
            else liveOmni++;
        }

        var order = new List<int>(lights.Count);
        for (int i = 0; i < lights.Count; i++) order.Add(i);
        order.Sort((a, b) => t.PerLight[b].CompareTo(t.PerLight[a]));
        var busiest = new List<string>();
        foreach (int i in order.GetRange(0, Math.Min(top, order.Count)))
        {
            LightBox l = lights[i];
            busiest.Add($"{l.Name} {(l.Spot ? "spot" : "omni")} range={ranges[i]:0.0} live={l.Live} instances={t.PerLight[i]}" +
                (hosted[i] ? " entity-hosted" : string.Empty));
        }

        double surfaces = Math.Max(1, t.Surfaces);
        return new Dictionary<string, object?>
        {
            ["lightsInBuffer"] = lights.Count,
            ["liveOmni"] = liveOmni,
            ["liveSpot"] = liveSpot,
            ["lightsOffBuffer"] = offBuffer,
            // Lights under GameEntities (the shader ceiling's `entitylights`): all, in the buffer,
            // live, and the instances they pair with.
            ["entityHostedLights"] = hostedAll,
            ["entityHostedInBuffer"] = hostedInBuffer,
            ["entityHostedLive"] = hostedLive,
            ["entityHostedPairs"] = hostedPairs,
            ["instances"] = t.Instances,
            ["batches"] = t.Batches,
            ["surfaces"] = t.Surfaces,
            ["withOmni"] = t.WithOmni,
            ["withSpot"] = t.WithSpot,
            ["withAny"] = t.WithAny,
            ["withLive"] = t.WithLive,
            ["omniPairs"] = t.OmniPairs,
            ["spotPairs"] = t.SpotPairs,
            ["livePairs"] = t.LivePairs,
            ["omniHistogram0to4"] = t.OmniHistogram,
            ["spotHistogram0to4"] = t.SpotHistogram,
            ["callsPerDraw"] = Math.Round(t.LoopCalls / surfaces, 3),
            ["callsPerDrawWithFillers"] = Math.Round(t.LoopCallsWithFillers / surfaces, 3),
            ["stableLightCounts"] = Refii.Lighting.Engine_StableLightCounts.Enabled,
            ["busiest"] = busiest,
        };
    }

    private static bool Inside(Godot.Collections.Array<Plane> planes, Aabb box)
    {
        Vector3 c = box.GetCenter(), h = box.Size * 0.5f;
        foreach (Plane plane in planes)
        {
            float r = Mathf.Abs(plane.Normal.X) * h.X + Mathf.Abs(plane.Normal.Y) * h.Y + Mathf.Abs(plane.Normal.Z) * h.Z;
            if (plane.DistanceTo(c) > r)
            {
                return false;
            }
        }

        return true;
    }
}
