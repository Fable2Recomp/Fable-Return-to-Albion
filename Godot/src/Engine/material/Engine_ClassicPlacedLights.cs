using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic light list's placed lights where no guest runs (the Godot editor, the level
/// viewer): each fixture's GraphicAppearanceLightComponent turned into the render light the
/// title's light manager would hold for it this frame, in the native export's 20-float layout.
/// </summary>
/// <remarks>
/// THE RECORD (`sub_825C2108`, verified): LightType 2 builds a point light (render type 8), 1 a
/// spot (9). Colour L+208 = (R, G, B) x IntensityFadeMultiplier x Intensity x 1/255
/// (0x825c2178..0x825c21e8; the multiplier rests at 1). Range L+320 is the bounding sphere's
/// radius, the half angle L+324 is in radians, CastShadows L+336 is a spot's alone. The spot
/// direction L+304 is the dummy's local +Y rotated by its orientation (`sub_821F5B58`,
/// `sub_821F59E0`), which under the placement conversion is the node's global +Z.
/// THE FRAME UPDATE (`sub_821F0A50`, verified): a seeded walk w += (rand(2) - w - 1) x Rate x
/// 30 dt, clamped to [-1, 1]; L+448 = clamp(w x BrightnessRange + 1, 0, 2); L+452 = w x
/// RadiusRange + 1 except on a shadow-casting spot; then, with FadeRange > 0, L+448 x=
/// clamp((min(FadeDistance, 1000) - d) / FadeRange, 0, 1), d the eye's distance. The seed is
/// (int)((pos.x + pos.y) x 1000) (`sub_82B1BFD0` 0x82b1c08c..98), the generator
/// seed = rotr(seed x 0x24A1 + 0x24DF, 13), rand(2) = fmod(seed, 131072) / 65536.
/// THE GATHER (`sub_821FF800`): a light beyond min(FadeDistance, 1000) of the eye is culled
/// (FadeDistance 0 never is), as is one whose sphere is outside the view or whose colour, or
/// colour x L+448, is under 1e-4 squared.
/// Deviations, stated: the zone is 0 (every draw's zones accept it) because the editor carries
/// no lighting zones; when more lights pass than the manager's 64 slots hold, the nearest are
/// kept; SpriteOnly and UseScreenSpaceShadowmap are not parsed, so both read clear.
/// </remarks>
public static class Engine_ClassicPlacedLights
{
    /// <summary>Marks a placed light node with its component's values (<see cref="Stamp"/>).</summary>
    public static readonly StringName Meta = "classic_placed_light";

    private const int Stride = Refii.Host.HostBridge.ClassicLightStride;
    private const float FadeCeiling = 1000f;

    private sealed class Fixture
    {
        public Light3D Node = null!;
        public float[] Values = null!;
        public uint Seed;
        public float Walk;
        public bool Started;
        public double Last;
    }

    private static Node? _level;
    private static readonly List<Fixture> _fixtures = new();
    private static readonly List<(float Distance, int Index, float Intensity, float RangeScale)> _passed = new();
    private static int _retry;

    /// <summary>The level whose fixtures feed the list; null where the guest states its own lights.</summary>
    public static Node? Level
    {
        get => _level;
        set
        {
            if (!ReferenceEquals(_level, value))
            {
                _level = value;
                _fixtures.Clear();
                _retry = 0;
            }
        }
    }

    /// <summary>The component's values on a light node, for the classic list: (r, g, b) in 0..1,
    /// Intensity, Range, render type, half angle, FadeDistance, FadeRange, the three flicker
    /// terms and the shadow flag.</summary>
    public static void Stamp(Node node, Vector3 colour, float intensity, float range, bool spot,
        float halfAngle, float fadeDistance, float fadeRange, float brightness, float radius, float rate, bool casts)
    {
        node.SetMeta(Meta, new float[]
        {
            colour.X, colour.Y, colour.Z, intensity, range, spot ? 9f : 8f, halfAngle,
            fadeDistance, fadeRange, brightness, radius, rate, casts ? 1f : 0f,
        });
    }

    /// <summary>
    /// Writes this frame's placed lights after the <paramref name="count"/> already in
    /// <paramref name="raw"/>; returns the new count.
    /// </summary>
    public static int Write(float[] raw, int count, int capacity, Camera3D camera)
    {
        if (_level is null || !GodotObject.IsInstanceValid(_level))
        {
            return count;
        }
        if (_fixtures.Count == 0 && _retry-- <= 0)
        {
            Collect(_level);
            _retry = 120;
        }
        if (_fixtures.Count == 0)
        {
            return count;
        }

        double now = Time.GetTicksUsec() / 1e6;
        Vector3 eye = camera.GlobalPosition;
        Godot.Collections.Array<Plane> frustum = camera.GetFrustum();
        _passed.Clear();
        for (int i = 0; i < _fixtures.Count; i++)
        {
            Fixture f = _fixtures[i];
            if (!GodotObject.IsInstanceValid(f.Node))
            {
                continue;
            }
            float[] v = f.Values;
            Vector3 at = f.Node.GlobalPosition;
            Vector3 guest = new(-at.X, at.Z, at.Y);
            if (!f.Started)
            {
                f.Seed = unchecked((uint)(long)((guest.X + guest.Y) * 1000f));
                f.Last = now;
                f.Started = true;
            }

            float dt = (float)((now - f.Last) * 30.0);
            f.Last = now;
            f.Walk += (Random2(ref f.Seed) - f.Walk - 1f) * v[11] * dt;
            f.Walk = Math.Clamp(f.Walk, -1f, 1f);
            float intensity = Math.Clamp(f.Walk * v[9] + 1f, 0f, 2f);
            bool castingSpot = v[5] == 9f && v[12] != 0f;
            float rangeScale = castingSpot ? 1f : f.Walk * v[10] + 1f;

            float distance = eye.DistanceTo(at);
            float reach = MathF.Min(v[7], FadeCeiling);
            if (reach > 0f && distance >= reach)
            {
                continue;
            }
            if (v[8] > 0f)
            {
                intensity *= Math.Clamp((reach - distance) / v[8], 0f, 1f);
            }
            float colourSq = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) * v[3] * v[3];
            if (colourSq <= 1e-4f || colourSq * intensity * intensity <= 1e-4f || !InView(frustum, at, v[4]))
            {
                continue;
            }
            _passed.Add((distance, i, intensity, rangeScale));
        }

        if (count + _passed.Count > capacity)
        {
            _passed.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
        foreach ((float _, int index, float intensity, float rangeScale) in _passed)
        {
            if (count >= capacity)
            {
                break;
            }
            Fixture f = _fixtures[index];
            float[] v = f.Values;
            Vector3 at = f.Node.GlobalPosition;
            Vector3 z = f.Node.GlobalBasis.Z.Normalized();
            int o = count * Stride;
            Array.Clear(raw, o, Stride);
            raw[o] = -at.X; raw[o + 1] = at.Z; raw[o + 2] = at.Y;
            raw[o + 3] = v[4];
            raw[o + 4] = v[0] * v[3]; raw[o + 5] = v[1] * v[3]; raw[o + 6] = v[2] * v[3];
            raw[o + 7] = intensity;
            if (v[5] == 9f)
            {
                raw[o + 8] = -z.X; raw[o + 9] = z.Z; raw[o + 10] = z.Y;
            }
            raw[o + 11] = v[5];
            raw[o + 12] = v[4];
            raw[o + 13] = v[6];
            raw[o + 14] = rangeScale;
            raw[o + 15] = v[12] != 0f ? 1f : 0f;
            raw[o + 17] = count;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Every lit fixture under <paramref name="level"/> with the farthest it can reach: its
    /// range at the widest flicker (L+452 tops at 1 + RadiusRange). Not the frame's list: no
    /// eye cut, no view cut. For the load hold's warm, which builds for every light that may come.
    /// </summary>
    public static void Reaches(Node level, List<(Vector3 Centre, float Reach)> into)
    {
        into.Clear();
        foreach (Node node in level.FindChildren("*", nameof(Light3D), true, false))
        {
            if (node is not Light3D light || !light.HasMeta(Meta) ||
                light.GetMeta(Meta).AsFloat32Array() is not { Length: >= 13 } v)
            {
                continue;
            }
            float colourSq = (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) * v[3] * v[3];
            if (colourSq <= 1e-4f)
            {
                continue;
            }
            bool castingSpot = v[5] == 9f && v[12] != 0f;
            into.Add((light.GlobalPosition, v[4] * (castingSpot ? 1f : 1f + MathF.Abs(v[10]))));
        }
    }

    /// <summary>`LHMath::GFFloatRandom(2.0, seed)` (0x821F09C8).</summary>
    private static float Random2(ref uint seed)
    {
        seed = unchecked(seed * 0x24A1u + 0x24DFu);
        seed = (seed >> 13) | (seed << 19);
        return (float)((seed % 131072u) / 65536.0);
    }

    private static bool InView(Godot.Collections.Array<Plane> frustum, Vector3 centre, float radius)
    {
        foreach (Plane plane in frustum)
        {
            if (plane.DistanceTo(centre) > radius)
            {
                return false;
            }
        }
        return true;
    }

    private static void Collect(Node level)
    {
        _fixtures.Clear();
        foreach (Node node in level.FindChildren("*", nameof(Light3D), true, false))
        {
            if (node is Light3D light && light.HasMeta(Meta) && light.GetMeta(Meta).AsFloat32Array() is { Length: >= 13 } values)
            {
                _fixtures.Add(new Fixture { Node = light, Values = values });
            }
        }
    }
}
