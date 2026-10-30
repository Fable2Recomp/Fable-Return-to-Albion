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
///
/// Fable 3 packs differently: ONE group of at most eight in a 40-register block, see
/// <see cref="Fable3Group"/>.
/// </remarks>
public static class Engine_ClassicLights
{
    /// <summary>
    /// One active light as the manager holds it, in the guest's axes. <paramref name="ColourW"/>
    /// is the colour's w (Fable 3's row [19], L+220; Fable 2's row has 0 there).
    /// </summary>
    public readonly record struct Light(
        Vector3 Position, float Radius, Vector3 Colour, float Intensity, Vector3 Direction,
        int Type, float Range, float ConeAngle, float RangeScale, int Flags, uint Zone, int Index,
        float ColourW = 0f)
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
        /// <summary>Fable 3's L+343: in the manager's LM+40 mask, which a draw's group leaves out.</summary>
        public bool Static => (Flags & 16) != 0;
    }

    /// <summary>
    /// Fable 3: a point's or spot's stated cone and range terms as its packers read them —
    /// L+420 cos, L+424 1 / (1 - cos), L+428 1 / range (HostBridge.GetClassicLocalLights [13..15]).
    /// </summary>
    public readonly record struct Cone(float Cos, float Falloff, float InvRange);

    /// <summary>
    /// A local light as its packer and shadow object state it (HostBridge.GetClassicLocalLights):
    /// the slot's position L+288 (the manager entry is the light's bounding sphere, L+112) and,
    /// for a spot with a shadow object, its map's camera and depth biases.
    /// </summary>
    public readonly record struct Local(
        Vector3 Origin, Vector3 Direction, bool Mapped, float FovY, float Near, float Far, float Bias, float Slope);

    private const int MaxLocalIndex = 256;
    private static readonly Local[] _locals = new Local[MaxLocalIndex];
    private static readonly bool[] _hasLocal = new bool[MaxLocalIndex];
    private static readonly float[] _localRaw = new float[64 * Refii.Host.HostBridge.ClassicLocalLightStride];
    private static readonly Cone[] _cones = new Cone[MaxLocalIndex];
    private static readonly bool[] _hasCone = new bool[MaxLocalIndex];

    /// <summary>Fable 3: this frame's stated cone and range terms of the light at manager index <paramref name="index"/>.</summary>
    public static bool TryCone(int index, out Cone cone)
    {
        if (index >= 0 && index < MaxLocalIndex && _hasCone[index])
        {
            cone = _cones[index];
            return true;
        }
        cone = default;
        return false;
    }

    /// <summary>This frame's stated origin and spot map of the light at manager index <paramref name="index"/>.</summary>
    public static bool TryLocal(int index, out Local local)
    {
        if (index >= 0 && index < MaxLocalIndex && _hasLocal[index])
        {
            local = _locals[index];
            return true;
        }
        local = default;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="light"/> shadows with a map of its own (`Render_SpotLight_PackGroupSlot`:
    /// an active shadow object and not the screen-mask light, L+337 with L+484).
    /// </summary>
    public static bool OwnsSpotMap(in Light light) =>
        light.Type == Light.Spot && light.Shadowed && !(light.Flag337 && light.Primary) &&
        TryLocal(light.Index, out Local local) && local.Mapped;

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

    private static bool Fable3 => Refii.Core.Engine_Title.Active == Refii.Core.Engine_Title.Fable3;

    /// <summary>Reads the manager's list and writes the frame row's light block. Main thread, once a frame.</summary>
    public static void Pump(Camera3D? camera)
    {
        ++_frames;
        bool fable3 = Fable3;
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
        Array.Clear(_hasLocal);
        Array.Clear(_hasCone);
        if (Replay is null)
        {
            const int ls = Refii.Host.HostBridge.ClassicLocalLightStride;
            uint locals = Refii.Host.HostBridge.GetClassicLocalLights(_localRaw);
            for (int i = 0; i < locals; i++)
            {
                int at = i * ls;
                int index = (int)_localRaw[at];
                if (index < 0 || index >= MaxLocalIndex)
                {
                    continue;
                }
                _locals[index] = new Local(
                    new Vector3(_localRaw[at + 1], _localRaw[at + 2], _localRaw[at + 3]),
                    new Vector3(_localRaw[at + 4], _localRaw[at + 5], _localRaw[at + 6]),
                    _localRaw[at + 7] != 0f, _localRaw[at + 8], _localRaw[at + 9], _localRaw[at + 10],
                    _localRaw[at + 11], _localRaw[at + 12]);
                _hasLocal[index] = true;
                if (fable3)
                {
                    _cones[index] = new Cone(_localRaw[at + 13], _localRaw[at + 14], _localRaw[at + 15]);
                    _hasCone[index] = true;
                }
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
                (int)_raw[at + 15], BitConverter.SingleToUInt32Bits(_raw[at + 16]), (int)_raw[at + 17],
                fable3 ? _raw[at + 19] : 0f));
        }
        // The second view's gather is Fable 2's rule (`sub_821FF800`); Fable 3's gather
        // (`sub_821F3980`) is not modelled, so split screen adds no lights there.
        if (Replay is null && !fable3)
        {
            GatherSecondView((int)count, stride);
        }

        // The mods' lights (Engine_ModLights): point lights appended past the second view's
        // indices, each where its node stands this frame, taken by the draws its sphere meets.
        ModLights = Refii.Lighting.Engine_ModLights.AppendClassic(_lights, Refii.Lighting.Engine_ModLights.FirstClassicIndex);

        if (fable3)
        {
            PumpFable3Frame();
            return;
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
        Engine_ClassicSpotShadows.WriteFrameDefaults();
    }

    private const int MaxSceneLights = 2048;
    private const int SecondViewFirstIndex = 64;
    private static readonly float[] _scene = new float[MaxSceneLights * Refii.Host.HostBridge.ClassicSceneLightStride];
    private static readonly HashSet<uint> _active = new();
    private static readonly List<(float Distance, int At, float Intensity)> _second = new();

    /// <summary>Lights the second view's gather added on the last frame.</summary>
    public static int SecondViewLights { get; private set; }

    /// <summary>Lights the mods' list added on the last frame (Engine_ModLights.AppendClassic).</summary>
    public static int ModLights { get; private set; }

    /// <summary>
    /// SPLIT SCREEN: PLAYER TWO'S LIGHTS. The guest renders one view and its light manager gathers
    /// against that view's eye alone (`sub_82240CE8` from 0x821C3AF4; `sub_821FF800`: within
    /// min(L+328, 1000) of view+80, coloured, the box in the view's planes, intensity faded by the
    /// eye's distance in `sub_821F0A50`), so a lamp beside player two far from player one is never
    /// in the list. The scene's other lights (HostBridge.GetClassicSceneLights) are gathered here
    /// by the same rules against player two's eye and added after the guest's own, with indices
    /// from 64 (the manager has 64 slots). Each draw then takes the lights its bounds meet as
    /// before, so a draw near either player is lit by the lights near it.
    /// Deviations, stated: a light the guest did not gather has no frame update, so its flicker
    /// walk L+444 stands still (its brightness is read from it as the update computes it); when more
    /// pass than 64 the nearest are kept (the guest fills its slots in registry order); the second
    /// view's lights carry no spot receive frustum (InFrustum passes them).
    /// </summary>
    private static void GatherSecondView(int activeCount, int stride)
    {
        SecondViewLights = 0;
        if (!Refii.Core.Engine_Views.Split || Refii.Core.Engine_Views.Second is not { } second ||
            !GodotObject.IsInstanceValid(second))
        {
            return;
        }
        uint count = Refii.Host.HostBridge.GetClassicSceneLights(_scene);
        if (count == 0)
        {
            return;
        }

        _active.Clear();
        for (int i = 0; i < activeCount; i++)
        {
            _active.Add(BitConverter.SingleToUInt32Bits(_raw[i * stride + 18]));
        }

        const int s = Refii.Host.HostBridge.ClassicSceneLightStride;
        Vector3 eye = second.GlobalPosition;
        var guestEye = new Vector3(-eye.X, eye.Z, eye.Y);
        Godot.Collections.Array<Plane> frustum = second.GetFrustum();
        _second.Clear();
        for (int i = 0; i < count; i++)
        {
            int at = i * s;
            if (_active.Contains(BitConverter.SingleToUInt32Bits(_scene[at + 18])))
            {
                continue;
            }
            var position = new Vector3(_scene[at + 20], _scene[at + 21], _scene[at + 22]);
            float distance = position.DistanceTo(guestEye);
            float reach = MathF.Min(_scene[at + 23], 1000f);
            if (reach > 0f && distance >= reach)
            {
                continue;
            }
            float intensity = Math.Clamp(_scene[at + 26] * _scene[at + 25] + 1f, 0f, 2f);
            if (_scene[at + 24] > 0f)
            {
                intensity *= Math.Clamp((reach - distance) / _scene[at + 24], 0f, 1f);
            }
            var colour = new Vector3(_scene[at + 4], _scene[at + 5], _scene[at + 6]);
            float colourSq = colour.LengthSquared();
            if (colourSq <= 1e-4f || colourSq * intensity * intensity <= 1e-4f)
            {
                continue;
            }
            // The box in the view's planes, in Godot's axes: guest (x, y, z) is Godot (-x, z, y).
            var min = new Vector3(-_scene[at + 30], _scene[at + 29], _scene[at + 28]);
            var max = new Vector3(-_scene[at + 27], _scene[at + 32], _scene[at + 31]);
            if (!BoxInView(frustum, min, max))
            {
                continue;
            }
            _second.Add((distance, at, intensity));
        }

        if (_second.Count > SecondViewFirstIndex)
        {
            _second.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
        int index = SecondViewFirstIndex;
        foreach ((float _, int at, float intensity) in _second)
        {
            if (index >= SecondViewFirstIndex * 2)
            {
                break;
            }
            // Its packed position, L+288 ([20..22]); no map: the guest renders none for a light
            // it did not gather.
            if (index < MaxLocalIndex)
            {
                _locals[index] = new Local(new Vector3(_scene[at + 20], _scene[at + 21], _scene[at + 22]),
                    new Vector3(_scene[at + 8], _scene[at + 9], _scene[at + 10]), false, 0f, 0f, 0f, 0f, 0f);
                _hasLocal[index] = true;
            }
            _lights.Add(new Light(
                new Vector3(_scene[at], _scene[at + 1], _scene[at + 2]), _scene[at + 3],
                new Vector3(_scene[at + 4], _scene[at + 5], _scene[at + 6]), intensity,
                new Vector3(_scene[at + 8], _scene[at + 9], _scene[at + 10]),
                (int)_scene[at + 11], _scene[at + 12], _scene[at + 13], _scene[at + 14],
                (int)_scene[at + 15], BitConverter.SingleToUInt32Bits(_scene[at + 16]), index++));
        }
        SecondViewLights = index - SecondViewFirstIndex;
    }

    /// <summary>Whether a box meets every plane of a Godot frustum (planes face outward).</summary>
    private static bool BoxInView(Godot.Collections.Array<Plane> frustum, Vector3 min, Vector3 max)
    {
        foreach (Plane plane in frustum)
        {
            Vector3 n = plane.Normal;
            var nearest = new Vector3(n.X > 0f ? min.X : max.X, n.Y > 0f ? min.Y : max.Y, n.Z > 0f ? min.Z : max.Z);
            if (plane.DistanceTo(nearest) > 0f)
            {
                return false;
            }
        }
        return true;
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
            Vector3 colour = light.Primary ? Refii.Level.Engine_DynamicSkyLighting.ResolveRadiance(light.Colour) : light.Colour;
            Vector3 travel = light.Primary ? Refii.Level.Engine_DynamicSkyLighting.ResolveGuestTravel(light.Direction) : light.Direction;
            Set(block, positions + slot, -travel.X, -travel.Y, -travel.Z, 0f);
            Set(block, colors + slot, colour.X, colour.Y, colour.Z, 0f);
            Lane(block, type + quad, lane, 0f);
            Lane(block, invRadii + quad, lane, 0f);
            Set(block, spotDirections + slot, 0f, 0f, 0f, 0f);
            Lane(block, spotParameters + quad, lane, -1f);
            Lane(block, spotFalloff + quad, lane, 1f);
            Lane(block, screenShadow + quad, lane, light.Flag337 ? 1f : 0f);
            Lane(block, spotShadow + quad, lane, 0f);
            return;
        }

        // The packer's position is L+288; the light's Position is its query sphere (L+112),
        // which for a spot stands out along the cone.
        Vector3 origin = TryLocal(light.Index, out Local local) ? local.Origin : light.Position;
        Set(block, positions + slot, origin.X, origin.Y, origin.Z, 1f);
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
            Lane(block, spotShadow + quad, lane, OwnsSpotMap(light) ? 1f : 0f);
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
        Vector3 direction = light.Type == Light.Directional && light.Primary
            ? Refii.Level.Engine_DynamicSkyLighting.ResolveGuestTravel(light.Direction) : light.Direction;
        Set(block, 29, -direction.X, -direction.Y, -direction.Z, 0f);
    }

    private static readonly float[] _block3 = new float[Fable3Group.PixelRegisters * 4];
    private static readonly List<Light> _frameSet = new(MaxLights);
    private static readonly List<Light> _frameGroup = new(Fable3Group.MaxLights);
    private static readonly List<int> _frameIndices = new(Fable3Group.MaxLights);
    private static readonly Fable3Group.Stated[] _frameStated = new Fable3Group.Stated[Fable3Group.MaxLights];

    /// <summary>Fable 3: the frame group's lights by manager index, in slot order.</summary>
    public static IReadOnlyList<int> Fable3FrameGroup => _frameIndices;

    /// <summary>Fable 3: the frame group's shader key counts and block count (`sub_821FC990`'s +16).</summary>
    public static (Fable3Group.Key Key, int BlockCount) Fable3Frame { get; private set; }

    /// <summary>
    /// Fable 3's frame block: the directional group `sub_821AE068` builds from the manager's
    /// LM+32 mask (0x821AE078 `ld r5,0x20(r4)`; every live type-10 light, `sub_822327E8`
    /// 0x82232964), selected and packed as any group, uploaded by `sub_821FECC8` to PS c80
    /// (0x821FEE3C..0x821FEE50) and to VS c44 (`sub_82275578`). Fable 2's spot-shadow frame
    /// defaults are not written: their registers (c32, c48) are other constants in Fable 3.
    /// </summary>
    private static void PumpFable3Frame()
    {
        _frameSet.Clear();
        foreach (Light light in _lights)
        {
            if (light.Type == Light.Directional)
            {
                _frameSet.Add(light);
            }
        }
        Fable3Group.Key key = Fable3Group.Count(_frameSet);
        Fable3Group.Select(_frameSet, _frameGroup);
        _frameIndices.Clear();
        for (int i = 0; i < _frameGroup.Count; i++)
        {
            _frameIndices.Add(_frameGroup[i].Index);
            _frameStated[i] = StatedOf(_frameGroup[i]);
        }
        int blockCount = Fable3Group.PackGroup(_block3, _frameGroup, _frameStated);
        Fable3Frame = (key, blockCount);
        Engine_ClassicConstants.SetFrame(true, Engine_ClassicRegisters.Ps.LightPositions,
            _block3.AsSpan(0, Fable3Group.PixelRegisters * 4));
        Engine_ClassicConstants.SetFrame(false, Engine_ClassicRegisters.Vs.LightPositions,
            _block3.AsSpan(0, Fable3Group.VertexRegisters * 4));
    }

    /// <summary>
    /// Fable 3: what a light's packer reads besides its row. The position is L+288 (the local
    /// row; else the manager entry's centre, which `sub_82CCF180` sets from L+288). The spot's
    /// map is `sub_82CE09E0`(L, 0): S = L+176 up (S+8), L+340 set and L+342 clear (S+0x14 is the
    /// light). The cone and range terms are the stated L+420/+424/+428; a light the guest does
    /// not hold (the mods') states none, so they are formed from its cone and range.
    /// </summary>
    public static Fable3Group.Stated StatedOf(in Light light)
    {
        bool local = TryLocal(light.Index, out Local stated);
        Vector3 origin = local ? stated.Origin : light.Position;
        if (!TryCone(light.Index, out Cone cone))
        {
            float cos = MathF.Cos(light.ConeAngle);
            cone = new Cone(cos, 1f - cos != 0f ? 1f / (1f - cos) : 0f, light.Range != 0f ? 1f / light.Range : 0f);
        }
        return new Fable3Group.Stated(origin, local && stated.Mapped, cone);
    }

    /// <summary>
    /// FABLE 3'S LIGHT GROUP. A draw is lit by one group of at most eight lights packed into a
    /// 640-byte block of 40 registers (`sub_82CEEA38`: `li r3,0x28` constants, `li r3,0x280`
    /// bytes), uploaded whole to the pixel stage at c80 (`sub_8225E5A0`(dev, 1, 0x50, 0x28),
    /// 0x821FEE50) and its first 36 registers to the vertex stage at c44 (dev, 0, 0x2C, 0x24,
    /// 0x82201774). The shader bank names it g_GlobalLightParamsPS/VS with this layout (the
    /// D3DX type info of PSHADER_STANDARDMATERIAL_LGHTS_ANY_.. and VSHADER_TREEMATERIAL_LGHTS_ANY_..).
    /// </summary>
    /// <remarks>
    /// SELECTION (`sub_821E3CC0`): `sub_82CFCAC8` walks the mask highest index first into three
    /// lists — points (0x82CFCB60), spots (0x82CFCB34), directionals (0x82CFCB8C) — counting
    /// points, spots and every light (the shader key: points &lt;&lt; 14, spots &lt;&lt; 20, total
    /// &lt;&lt; 26, 0x821E3E2C..0x821E3E7C, counted BEFORE the cap). `sub_82CFCC08` then takes
    /// directionals, then points, then spots, each from its list's BACK (`sub_82CFCD18`
    /// `lwz r6,-8(r8)`), so lowest index first, until eight are taken (`cmplwi r10,8`), in one
    /// group (`cmplwi r11,1` on the group counter).
    ///
    /// PACKING (`sub_821FC990`): the selection is walked highest index first (`sub_822071A8`,
    /// `cntlzd`), slot n to the n-th; each light's vt+116 packer writes its slot; c32 is zeroed
    /// and then takes minus the direction of every directional, and of every spot with L+484, so
    /// the lowest-index one wins. The block count (group+16) is the lights packed, except that a
    /// group of one light that is not directional counts 2 (0x821FCAC4..0x821FCAD8). Empty slots
    /// keep the constructor's defaults (`sub_82CEEAE0`): position (0, 0, 0, 1), cos -1,
    /// falloff 1, the rest 0.
    /// </remarks>
    public static class Fable3Group
    {
        public const int MaxLights = 8;
        public const int PixelRegisters = 40;
        public const int VertexRegisters = 36;

        /// <summary>Block registers of each member (g_GlobalLightParamsPS c80 + n; VS c44 + n).</summary>
        public const int Positions = 0, Colors = 8, Type = 16, InvRadii = 18, SpotDirections = 20,
            SpotCos = 28, SpotFalloff = 30, PrimaryLightDirection = 32, ScreenSpaceShadowMask = 36,
            SpotLightShadowMask = 38;

        /// <summary>What a packer reads besides the light row (<see cref="StatedOf"/>).</summary>
        public readonly record struct Stated(Vector3 Origin, bool Mapped, Cone Cone);

        /// <summary>The group's counts as `sub_82CFCAC8` takes them, and the key word they form.</summary>
        public readonly record struct Key(int Points, int Spots, int Total)
        {
            public uint Bits =>
                (((uint)Points << 14) & 0xFC000u) | (((uint)Spots << 20) & 0x3F00000u) | ((uint)Total << 26);
        }

        /// <summary>The counts of a group's light mask, before the cap.</summary>
        public static Key Count(IReadOnlyList<Light> set)
        {
            int points = 0, spots = 0;
            foreach (Light light in set)
            {
                if (light.Type == Light.Point)
                {
                    ++points;
                }
                else if (light.Type == Light.Spot)
                {
                    ++spots;
                }
            }
            return new Key(points, spots, set.Count);
        }

        /// <summary>
        /// The group a mask makes: directionals, then points, then spots, each lowest index
        /// first, eight at most; <paramref name="into"/> gets them in slot order (highest index first).
        /// </summary>
        public static void Select(IReadOnlyList<Light> set, List<Light> into)
        {
            into.Clear();
            foreach (int type in Order)
            {
                int from = into.Count;
                foreach (Light light in set)
                {
                    if (light.Type == type)
                    {
                        into.Add(light);
                    }
                }
                into.Sort(from, into.Count - from, Ascending);
                if (into.Count >= MaxLights)
                {
                    into.RemoveRange(MaxLights, into.Count - MaxLights);
                    break;
                }
            }
            into.Sort(Descending);
        }

        private static readonly int[] Order = { Light.Directional, Light.Point, Light.Spot };
        private static readonly Comparer<Light> Ascending = Comparer<Light>.Create((a, b) => a.Index.CompareTo(b.Index));
        private static readonly Comparer<Light> Descending = Comparer<Light>.Create((a, b) => b.Index.CompareTo(a.Index));

        /// <summary>The block count `sub_821FC990` leaves at group+16 for <paramref name="slots"/>.</summary>
        public static int BlockCount(IReadOnlyList<Light> slots)
        {
            int directional = 0;
            foreach (Light light in slots)
            {
                if (light.Type == Light.Directional)
                {
                    ++directional;
                }
            }
            return slots.Count == 1 && directional != 1 ? 2 : slots.Count;
        }

        /// <summary>The constructor's block (`sub_82CEEAE0`).</summary>
        public static void Clear(Span<float> block)
        {
            block.Slice(0, PixelRegisters * 4).Clear();
            for (int slot = 0; slot < MaxLights; slot++)
            {
                block[(Positions + slot) * 4 + 3] = 1f;
                block[(SpotCos + slot / 4) * 4 + slot % 4] = -1f;
                block[(SpotFalloff + slot / 4) * 4 + slot % 4] = 1f;
            }
        }

        /// <summary>
        /// Packs <paramref name="slots"/> (slot order, <see cref="Select"/>) into a fresh block, as
        /// `sub_821FC990` does; returns the block count.
        /// </summary>
        public static int PackGroup(Span<float> block, IReadOnlyList<Light> slots, ReadOnlySpan<Stated> stated)
        {
            Clear(block);
            for (int slot = 0; slot < slots.Count && slot < MaxLights; slot++)
            {
                Light light = slots[slot];
                PackSlot(block, light, stated[slot], slot);
                if (light.Type == Light.Directional || (light.Type == Light.Spot && light.Primary))
                {
                    SetRegister(block, PrimaryLightDirection, -light.Direction.X, -light.Direction.Y, -light.Direction.Z, 0f);
                }
            }
            return BlockCount(slots);
        }

        /// <summary>
        /// One light's vt+116 packer. Point `sub_82223FD8`: position (L+288, 1), colour
        /// L+208 x L+444, spot direction 0, Type 1, InvRadii L+452 x L+428, cos -1, falloff 1,
        /// both masks 0. Spot `sub_82C86E68`: the same with spot direction (-L+304, 0), cos L+420,
        /// falloff L+424, ScreenSpaceShadowMask = map and L+341 and L+484, SpotLightShadowMask =
        /// map and not that (0x82C86F54..0x82C87018). Directional `sub_82288FF0`: position
        /// (-L+304, 0), colour L+208 without intensity, Type and InvRadii 0, ScreenSpaceShadowMask
        /// = L+341, SpotLightShadowMask 0. L+452 is 1 / L+448 (`sub_821F35F8` 0x821F37A8). Bit 1
        /// of the row's flags is L+341 on Fable 3.
        /// </summary>
        public static void PackSlot(Span<float> block, in Light light, in Stated stated, int slot)
        {
            int quad = slot / 4, lane = slot % 4;
            if (light.Type == Light.Directional)
            {
                SetRegister(block, Positions + slot, -light.Direction.X, -light.Direction.Y, -light.Direction.Z, 0f);
                SetRegister(block, Colors + slot, light.Colour.X, light.Colour.Y, light.Colour.Z, light.ColourW);
                SetRegister(block, SpotDirections + slot, 0f, 0f, 0f, 0f);
                SetLane(block, InvRadii + quad, lane, 0f);
                SetLane(block, Type + quad, lane, 0f);
                SetLane(block, ScreenSpaceShadowMask + quad, lane, light.Flag337 ? 1f : 0f);
                SetLane(block, SpotLightShadowMask + quad, lane, 0f);
                SetLane(block, SpotCos + quad, lane, -1f);
                SetLane(block, SpotFalloff + quad, lane, 1f);
                return;
            }
            if (light.Type != Light.Point && light.Type != Light.Spot)
            {
                // The global light's packer is `li r3,0` (0x82CE17A8): the slot keeps its defaults.
                return;
            }
            float i = light.Intensity;
            SetRegister(block, Positions + slot, stated.Origin.X, stated.Origin.Y, stated.Origin.Z, 1f);
            SetRegister(block, Colors + slot, light.Colour.X * i, light.Colour.Y * i, light.Colour.Z * i, light.ColourW * i);
            float invScale = light.RangeScale != 0f ? 1f / light.RangeScale : 0f;
            SetLane(block, InvRadii + quad, lane, invScale * stated.Cone.InvRange);
            SetLane(block, Type + quad, lane, 1f);
            if (light.Type == Light.Point)
            {
                SetRegister(block, SpotDirections + slot, 0f, 0f, 0f, 0f);
                SetLane(block, ScreenSpaceShadowMask + quad, lane, 0f);
                SetLane(block, SpotLightShadowMask + quad, lane, 0f);
                SetLane(block, SpotCos + quad, lane, -1f);
                SetLane(block, SpotFalloff + quad, lane, 1f);
                return;
            }
            SetRegister(block, SpotDirections + slot, -light.Direction.X, -light.Direction.Y, -light.Direction.Z, 0f);
            bool screen = stated.Mapped && light.Flag337 && light.Primary;
            SetLane(block, ScreenSpaceShadowMask + quad, lane, screen ? 1f : 0f);
            SetLane(block, SpotLightShadowMask + quad, lane, stated.Mapped && !screen ? 1f : 0f);
            SetLane(block, SpotCos + quad, lane, stated.Cone.Cos);
            SetLane(block, SpotFalloff + quad, lane, stated.Cone.Falloff);
        }

        private static void SetRegister(Span<float> block, int register, float x, float y, float z, float w)
        {
            int at = register * 4;
            block[at] = x; block[at + 1] = y; block[at + 2] = z; block[at + 3] = w;
        }

        private static void SetLane(Span<float> block, int register, int lane, float value) =>
            block[register * 4 + lane] = value;
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
