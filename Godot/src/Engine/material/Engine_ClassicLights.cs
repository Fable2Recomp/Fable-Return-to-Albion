using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The classic renderer's lights: the render scene's active light list as the guest's light
/// manager holds it, packed into the constant file's light block the way the title packs it.
/// </summary>
/// <remarks>
/// THE GUEST'S LIGHT MODEL, NOT GODOT'S. The title lights a draw with the lights whose spheres
/// meet its bounds, up to seven in a group and up to three groups drawn as passes
/// (<c>sub_82AB6360</c>, <c>sub_82B35D50</c>); each light writes its own slot of a 36-register
/// block through its virtual packer (<c>sub_82A6CDD0</c>):
///   Positions[s]      = (L+288, 1)                 c80+s   (VS c76+s)
///   Colors[s]         = L+208 * L+448              c87+s
///   Type              lane s of c94+s/4 = 1.0
///   InvRadii          lane s of c96+s/4 = 1 / (L+452 * L+320)
///   SpotDirections[s] = (-(L+304), 0)              c98+s
///   SpotParameters    lane s of c105+s/4 = cos(L+324); lane s of c107+s/4 = 1/(1-cos)
///   PrimaryLightDirection (c109) = L+304 of the primary light (type 10 or 9 with L+484)
///   ScreenSpaceShadowMask lane s of c112+s/4 = shadowed and L+337 and L+484
///   SpotLightShadowMask   lane s of c114+s/4 = shadowed and not that
/// The frame's own row (row 0 of <see cref="Engine_ClassicConstants"/>) carries the group every
/// draw shares: the directional lights, which have no bounds. Local lights are per draw and go
/// into per-instance rows.
/// </remarks>
public static class Engine_ClassicLights
{
    /// <summary>One active light as the manager holds it, in the guest's axes.</summary>
    public readonly record struct Light(
        Vector3 Position, float Radius, Vector3 Colour, float Intensity, Vector3 Direction,
        int Type, float Range, float ConeAngle, float RangeScale, int Flags, uint Zone, int Index)
    {
        public const int Point = 8, Spot = 9, Directional = 10;
        /// <summary>L+336: shadow casting requested.</summary>
        public bool Casts => (Flags & 1) != 0;
        /// <summary>L+337.</summary>
        public bool Flag337 => (Flags & 2) != 0;
        /// <summary>L+338: shadows disabled.</summary>
        public bool NoShadow => (Flags & 4) != 0;
        /// <summary>L+484: the primary light.</summary>
        public bool Primary => (Flags & 8) != 0;
        /// <summary>`sub_82B1C4B8`: the light's shadow helpers exist and are enabled.</summary>
        public bool Shadowed => Casts && !NoShadow;
    }

    private const int MaxLights = 64;
    private static readonly float[] _raw = new float[MaxLights * Refii.Host.HostBridge.ClassicLightStride];
    private static readonly List<Light> _lights = new(MaxLights);
    private static readonly float[] _block = new float[Engine_ClassicRegisters.LightBlockRegisters * 4];
    private static long _frames;

    /// <summary>The active lights read this frame.</summary>
    public static IReadOnlyList<Light> Lights => _lights;

    private static readonly float[] _frustumRaw = new float[1 + MaxLights * 66];
    private static readonly Dictionary<int, (int Count, int At)> _frustums = new();

    /// <summary>
    /// Whether a box (guest axes) is inside the spot's receive frustum, as `sub_822362F8` tests it
    /// (the light's +96 receive filter): no plane has the box's farthest corner along its normal
    /// behind it. True for a light with no frustum stated.
    /// </summary>
    private static int _frustumWords;

    /// <summary>A hash of this frame's spot frustums as fed.</summary>
    public static long FrustumHash()
    {
        long h = _frustumWords;
        for (int i = 0; i < _frustumWords; i++)
        {
            h = h * 1099511628211L ^ BitConverter.SingleToInt32Bits(_frustumRaw[i]);
        }
        return h;
    }

    public static bool InFrustum(int index, Vector3 min, Vector3 max)
    {
        if (!_frustums.TryGetValue(index, out (int Count, int At) f))
        {
            return true;
        }
        for (int p = 0; p < f.Count; p++)
        {
            int at = f.At + p * 4;
            float nx = _frustumRaw[at], ny = _frustumRaw[at + 1], nz = _frustumRaw[at + 2], w = _frustumRaw[at + 3];
            float d = nx * (nx >= 0f ? max.X : min.X) + ny * (ny >= 0f ? max.Y : min.Y) + nz * (nz >= 0f ? max.Z : min.Z);
            if (d + w < 0f)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Recorded lights (20 floats each) read instead of the bridge.</summary>
    public static float[]? Replay;

    private static int _rawCount;

    /// <summary>The raw lights as last read, for the debug channel's recorder.</summary>
    public static float[] RawLights()
    {
        var copy = new float[_rawCount * Refii.Host.HostBridge.ClassicLightStride];
        Array.Copy(_raw, copy, copy.Length);
        return copy;
    }

    /// <summary>Reads the manager's list and writes the frame row's light block. Main thread, once a frame.</summary>
    public static void Pump(Camera3D? camera)
    {
        ++_frames;
        _lights.Clear();
        int stride = Refii.Host.HostBridge.ClassicLightStride;
        uint count;
        if (Replay is { } replay)
        {
            count = (uint)Math.Min(replay.Length / stride, MaxLights);
            Array.Copy(replay, _raw, count * stride);
            if (camera is not null)
            {
                count = (uint)Engine_ClassicPlacedLights.Write(_raw, (int)count, MaxLights, camera);
            }
        }
        else
        {
            count = Refii.Host.HostBridge.GetClassicLights(_raw);
        }
        _rawCount = (int)count;
        _frustums.Clear();
        _frustumWords = 0;
        if (Replay is null)
        {
            uint got = Refii.Host.HostBridge.GetSpotFrustums(_frustumRaw);
            _frustumWords = (int)Math.Min(got, (uint)_frustumRaw.Length);
            int spots = got >= 1 ? (int)_frustumRaw[0] : 0;
            for (int s = 0; s < spots && 1 + (s + 1) * 66 <= got; s++)
            {
                int at = 1 + s * 66;
                _frustums[(int)_frustumRaw[at]] = (Math.Clamp((int)_frustumRaw[at + 1], 0, 16), at + 2);
            }
        }
        for (int i = 0; i < count; i++)
        {
            int at = i * stride;
            _lights.Add(new Light(
                new Vector3(_raw[at], _raw[at + 1], _raw[at + 2]), _raw[at + 3],
                new Vector3(_raw[at + 4], _raw[at + 5], _raw[at + 6]), _raw[at + 7],
                new Vector3(_raw[at + 8], _raw[at + 9], _raw[at + 10]),
                (int)_raw[at + 11], _raw[at + 12], _raw[at + 13], _raw[at + 14],
                (int)_raw[at + 15], BitConverter.SingleToUInt32Bits(_raw[at + 16]), (int)_raw[at + 17]));
        }

        // The shared group: every directional light. The group walker takes lights from the
        // manager's mask highest index first (`sub_8223B130`), so slot 0 is the highest index.
        int slot = 0;
        Array.Clear(_block);
        for (int i = _lights.Count - 1; i >= 0; i--)
        {
            Light light = _lights[i];
            if (light.Type != Light.Directional || slot >= Engine_ClassicRegisters.LightsPerDraw)
            {
                continue;
            }
            Pack(light, slot++);
            if (light.Primary)
            {
                PackCaster(_block, light);
            }
        }
        Engine_ClassicConstants.SetFrame(true, Engine_ClassicRegisters.Ps.LightPositions, _block);
        // The vertex block is c76..c107: the pixel block's layout without its last two entries
        // (screen and spot shadow masks), which would land on g_VertexAtmosphericParameters c108.
        Engine_ClassicConstants.SetFrame(false, Engine_ClassicRegisters.Vs.LightPositions, VertexBlock(_block));
    }

    /// <summary>The part of a pixel-layout light block the vertex stage has (c76..c107).</summary>
    public static ReadOnlySpan<float> VertexBlock(float[] pixelBlock) =>
        pixelBlock.AsSpan(0, (Engine_ClassicRegisters.Vs.LightBlockEnd - Engine_ClassicRegisters.Vs.LightPositions) * 4);

    /// <summary>Writes one light into slot <paramref name="slot"/> of the block, as its packer does.</summary>
    public static void Pack(in Light light, int slot)
    {
        PackInto(_block, light, slot);
    }

    /// <summary>
    /// The block as a flat array of float4 registers, c80 first; shared with per-instance rows.
    /// Each light type has its own packer (slot 28 of its class: point `sub_8223B1B8`, spot
    /// `sub_82A6CDD0`, directional `sub_82240B60`) and they differ: a directional light carries
    /// its direction in the POSITION slot with w = 0, its colour without the flicker intensity,
    /// zero range terms and cone cos -1; a point light has no cone (cos -1, falloff 1).
    /// </summary>
    public static void PackInto(float[] block, in Light light, int slot)
    {
        const int positions = 0, colors = 7, type = 14, invRadii = 16, spotDirections = 18,
            spotParameters = 25, spotFalloff = 27, screenShadow = 32, spotShadow = 34;
        int lane = slot % 4, quad = slot / 4;

        if (light.Type == Light.Directional)
        {
            Set(block, positions + slot, -light.Direction.X, -light.Direction.Y, -light.Direction.Z, 0f);
            Set(block, colors + slot, light.Colour.X, light.Colour.Y, light.Colour.Z, 0f);
            Lane(block, type + quad, lane, 0f);
            Lane(block, invRadii + quad, lane, 0f);
            Set(block, spotDirections + slot, 0f, 0f, 0f, 0f);
            Lane(block, spotParameters + quad, lane, -1f);
            Lane(block, spotFalloff + quad, lane, 1f);
            Lane(block, screenShadow + quad, lane, light.Flag337 ? 1f : 0f);
            Lane(block, spotShadow + quad, lane, 0f);
            return;
        }

        Set(block, positions + slot, light.Position.X, light.Position.Y, light.Position.Z, 1f);
        Set(block, colors + slot,
            light.Colour.X * light.Intensity, light.Colour.Y * light.Intensity, light.Colour.Z * light.Intensity, 0f);
        Lane(block, type + quad, lane, 1f);
        float range = light.RangeScale * light.Range;
        Lane(block, invRadii + quad, lane, range != 0f ? 1f / range : 0f);
        if (light.Type == Light.Spot)
        {
            Set(block, spotDirections + slot, -light.Direction.X, -light.Direction.Y, -light.Direction.Z, 0f);
            float cos = MathF.Cos(light.ConeAngle);
            Lane(block, spotParameters + quad, lane, cos);
            Lane(block, spotFalloff + quad, lane, 1f - cos != 0f ? 1f / (1f - cos) : 0f);
            bool a = light.Shadowed && light.Flag337 && light.Primary;
            Lane(block, screenShadow + quad, lane, a ? 1f : 0f);
            Lane(block, spotShadow + quad, lane, light.Shadowed && !a ? 1f : 0f);
        }
        else
        {
            Set(block, spotDirections + slot, 0f, 0f, 0f, 0f);
            Lane(block, spotParameters + quad, lane, -1f);
            Lane(block, spotFalloff + quad, lane, 1f);
            Lane(block, screenShadow + quad, lane, 0f);
            Lane(block, spotShadow + quad, lane, 0f);
        }
    }

    /// <summary>
    /// Register 29 (c109): minus the direction of the group's shadow caster — the directional
    /// light, or a spot light with L+484 — as `sub_82B1EE68` writes it after the packers.
    /// </summary>
    public static void PackCaster(float[] block, in Light light)
    {
        Set(block, 29, -light.Direction.X, -light.Direction.Y, -light.Direction.Z, 0f);
    }

    /// <summary>Frames pumped, and the lights seen on the last one.</summary>
    public static (long Frames, int Lights) Counts => (_frames, _lights.Count);

    private static void Set(float[] block, int register, float x, float y, float z, float w)
    {
        int at = register * 4;
        block[at] = x; block[at + 1] = y; block[at + 2] = z; block[at + 3] = w;
    }

    private static void Lane(float[] block, int register, int lane, float value) =>
        block[register * 4 + lane] = value;
}
