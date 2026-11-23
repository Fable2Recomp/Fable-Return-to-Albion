using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

/// <summary>Modern water quality tiers controlled by the <c>water_quality</c> CVar.</summary>
public enum WaterQualityTier
{
    Low = 0,
    Balanced = 1,
    High = 2,
    Ultra = 3,
}

/// <summary>Planar reflection viewport configuration for a water quality tier.</summary>
public readonly record struct WaterMirrorSpec(
    bool Enabled,
    int TileHeight,
    int Cadence,
    float FarPlane,
    float LodPixels);

/// <summary>World-space character sample with stable identity and drawn horizontal facing vector for water wakes.</summary>
public readonly record struct WaderSample(
    uint Id,
    Vector3 Position,
    Vector2 ForwardXz);

/// <summary>
/// Pure-managed zero-allocation ring buffer tracking up to 8 interactive wader wake impulses
/// (expanding damped wave packets + identity-locked velocity/heading state) for Balanced/High/Ultra water.
/// </summary>
public sealed class WaterWakeRingBuffer
{
    private const int Capacity = 8;
    private const float ImpulseLifetimeSeconds = 3.8f;
    private const float MoveEmitDistanceMetres = 0.20f;
    private const float IdleEmitIntervalSeconds = 1.10f;

    private struct TrackedWader
    {
        public uint Id;
        public Vector3 LastPos;
        public Vector3 PrevFramePos;
        public Vector2 SmoothedDir;
        public float SmoothedSpeed;
        public float ChurnEnergy;
        public float LastEmitClock;
        public bool Active;
    }

    private readonly Vector4[] _impulses = new Vector4[Capacity];
    private readonly TrackedWader[] _tracked = new TrackedWader[Capacity];
    private int _writeCursor;

    public float Clock { get; private set; }

    public int CountActiveImpulses()
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
        {
            Vector4 imp = _impulses[i];
            if (imp.W > 0.001f && (Clock - imp.Z) <= ImpulseLifetimeSeconds)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// Advances the local wake simulation clock, expires finished ripples, emits new wave packets
    /// for characters wading or standing in water, and packs GPU matrices + live body rings.
    /// Returns <see langword="true"/> while any impulse or wader remains active.
    /// </summary>
    public bool Step(
        float delta,
        ReadOnlySpan<Vector3> wadersInBody,
        float waterY,
        out Projection ripplesA,
        out Projection ripplesB,
        out Vector4 wader0,
        out Vector4 wader1) =>
        Step(delta, wadersInBody, waterY, out ripplesA, out ripplesB, out wader0, out wader1, out _, out _);

    /// <summary>
    /// Advances the local wake simulation clock and outputs smoothed horizontal movement vectors
    /// (<c>xy = normalized heading, z = speed m/s, w = decaying churn energy 0..1</c>) per active wader.
    /// </summary>
    public bool Step(
        float delta,
        ReadOnlySpan<Vector3> wadersInBody,
        float waterY,
        out Projection ripplesA,
        out Projection ripplesB,
        out Vector4 wader0,
        out Vector4 wader1,
        out Vector4 wader0Vel,
        out Vector4 wader1Vel)
    {
        Span<WaderSample> samples = stackalloc WaderSample[Capacity];
        int count = Math.Min(wadersInBody.Length, Capacity);
        for (int i = 0; i < count; i++)
        {
            samples[i] = new WaderSample(0u, wadersInBody[i], Vector2.Zero);
        }

        return Step(
            delta, samples[..count], waterY,
            out ripplesA, out ripplesB,
            out wader0, out wader1,
            out wader0Vel, out wader1Vel);
    }

    /// <summary>
    /// Advances the local wake simulation clock using identity-locked <see cref="WaderSample"/> entries
    /// so nearby companions (e.g. hero and dog swimming side-by-side) never steal each other's velocity slot,
    /// and blends horizontal movement with drawn character facing for responsive turn alignment.
    /// </summary>
    public bool Step(
        float delta,
        ReadOnlySpan<WaderSample> wadersInBody,
        float waterY,
        out Projection ripplesA,
        out Projection ripplesB,
        out Vector4 wader0,
        out Vector4 wader1,
        out Vector4 wader0Vel,
        out Vector4 wader1Vel)
    {
        float safeDt = MathF.Max(delta, 0f);
        Clock += safeDt;
        bool anyActive = false;

        for (int i = 0; i < Capacity; i++)
        {
            Vector4 imp = _impulses[i];
            if (imp.W > 0.001f)
            {
                if ((Clock - imp.Z) > ImpulseLifetimeSeconds)
                {
                    _impulses[i] = Vector4.Zero;
                }
                else
                {
                    anyActive = true;
                }
            }
        }

        wader0 = Vector4.Zero;
        wader1 = Vector4.Zero;
        wader0Vel = Vector4.Zero;
        wader1Vel = Vector4.Zero;
        int waderSlot = 0;
        uint claimedMask = 0u;

        for (int w = 0; w < wadersInBody.Length && w < Capacity; w++)
        {
            WaderSample sample = wadersInBody[w];
            Vector3 pos = sample.Position;
            if (!pos.IsFinite() || pos.Y < (waterY - 2.1f) || pos.Y > (waterY + 0.12f))
            {
                continue;
            }

            float submergence = Math.Clamp(((waterY + 0.12f) - pos.Y) / 1.1f, 0.15f, 1.0f);
            anyActive = true;
            int assignedSlot = -1;

            if (waderSlot == 0)
            {
                wader0 = new Vector4(pos.X, pos.Z, submergence, 1f);
                assignedSlot = 0;
                waderSlot = 1;
            }
            else if (waderSlot == 1)
            {
                wader1 = new Vector4(pos.X, pos.Z, submergence, 1f);
                assignedSlot = 1;
                waderSlot = 2;
            }

            bool hasFwd = sample.ForwardXz.IsFinite() && sample.ForwardXz.LengthSquared() > 1e-4f;
            Vector2 fwdDir = hasFwd ? sample.ForwardXz.Normalized() : Vector2.Zero;

            int match = -1;
            int freeSlot = -1;
            if (sample.Id != 0u)
            {
                for (int t = 0; t < Capacity; t++)
                {
                    if (!_tracked[t].Active)
                    {
                        if (freeSlot < 0)
                        {
                            freeSlot = t;
                        }

                        continue;
                    }

                    if ((claimedMask & (1u << t)) == 0u && _tracked[t].Id == sample.Id)
                    {
                        match = t;
                        break;
                    }
                }
            }
            else
            {
                float bestDistSq = 1.8f * 1.8f;
                for (int t = 0; t < Capacity; t++)
                {
                    if (!_tracked[t].Active)
                    {
                        if (freeSlot < 0)
                        {
                            freeSlot = t;
                        }

                        continue;
                    }

                    if ((claimedMask & (1u << t)) != 0u || _tracked[t].Id != 0u)
                    {
                        continue;
                    }

                    float dx = pos.X - _tracked[t].PrevFramePos.X;
                    float dz = pos.Z - _tracked[t].PrevFramePos.Z;
                    float d2 = (dx * dx) + (dz * dz);
                    if (d2 < bestDistSq)
                    {
                        bestDistSq = d2;
                        match = t;
                    }
                }
            }

            Vector4 velPacked;
            if (match >= 0)
            {
                claimedMask |= 1u << match;
                ref TrackedWader slot = ref _tracked[match];
                float frameDx = pos.X - slot.PrevFramePos.X;
                float frameDz = pos.Z - slot.PrevFramePos.Z;
                float frameDist = MathF.Sqrt((frameDx * frameDx) + (frameDz * frameDz));
                slot.PrevFramePos = pos;

                float dtVel = MathF.Max(safeDt, 1e-3f);
                float instSpeed = frameDist <= 8.0f ? Math.Clamp(frameDist / dtVel, 0f, 10f) : 0f;
                if (frameDist > 8.0f)
                {
                    slot.LastPos = pos;
                }

                float speedAlpha = 1f - MathF.Exp(-dtVel * 10.0f);
                slot.SmoothedSpeed += (instSpeed - slot.SmoothedSpeed) * speedAlpha;

                bool hasVel = frameDist > MathF.Max(0.006f, 0.18f * dtVel) && frameDist <= 8.0f;
                Vector2 velDir = hasVel ? new Vector2(frameDx / frameDist, frameDz / frameDist) : Vector2.Zero;
                bool hasDesired = false;
                Vector2 desiredDir = slot.SmoothedDir;
                if (hasFwd && hasVel)
                {
                    if (velDir.Dot(fwdDir) >= -0.15f)
                    {
                        Vector2 blended = (fwdDir * 0.72f) + (velDir * 0.28f);
                        desiredDir = blended.LengthSquared() > 1e-6f ? blended.Normalized() : fwdDir;
                    }
                    else
                    {
                        desiredDir = velDir;
                    }
                    hasDesired = true;
                }
                else if (hasFwd)
                {
                    desiredDir = fwdDir;
                    hasDesired = true;
                }
                else if (hasVel)
                {
                    desiredDir = velDir;
                    hasDesired = true;
                }

                if (hasDesired)
                {
                    Vector2 nextDir = slot.SmoothedDir.LengthSquared() > 0.25f
                        ? slot.SmoothedDir.Lerp(desiredDir, Math.Clamp(dtVel * 18f, 0.14f, 1f))
                        : desiredDir;
                    slot.SmoothedDir = nextDir.LengthSquared() > 1e-6f ? nextDir.Normalized() : desiredDir;
                }

                float targetChurn = slot.SmoothedSpeed > 0.36f
                    ? Math.Clamp((slot.SmoothedSpeed - 0.36f) * 0.46f, 0f, 1f) * Math.Max(submergence, 0.55f)
                    : 0f;
                float churnRate = targetChurn > slot.ChurnEnergy ? 8.0f : 4.5f;
                slot.ChurnEnergy += (targetChurn - slot.ChurnEnergy) * (1f - MathF.Exp(-dtVel * churnRate));

                float dx = pos.X - slot.LastPos.X;
                float dz = pos.Z - slot.LastPos.Z;
                float moveDist = MathF.Sqrt((dx * dx) + (dz * dz));
                float elapsed = Clock - slot.LastEmitClock;

                if (moveDist >= MoveEmitDistanceMetres)
                {
                    float speed = moveDist / MathF.Max(elapsed, 0.016f);
                    float strength = Math.Clamp(0.54f + (speed * 0.16f), 0.54f, 1.0f) * Math.Max(submergence, 0.70f);
                    Emit(pos.X, pos.Z, Math.Clamp(strength, 0.54f, 1.0f));
                    slot.LastPos = pos;
                    slot.LastEmitClock = Clock;
                }
                else if (elapsed >= IdleEmitIntervalSeconds)
                {
                    Emit(pos.X, pos.Z, 0.16f * submergence);
                    slot.LastEmitClock = Clock;
                }

                velPacked = new Vector4(slot.SmoothedDir.X, slot.SmoothedDir.Y, slot.SmoothedSpeed, slot.ChurnEnergy);
            }
            else
            {
                int slotIdx = freeSlot;
                if (slotIdx < 0)
                {
                    for (int t = 0; t < Capacity; t++)
                    {
                        if ((claimedMask & (1u << t)) == 0u)
                        {
                            slotIdx = t;
                            break;
                        }
                    }
                    if (slotIdx < 0)
                    {
                        slotIdx = w % Capacity;
                    }
                }

                claimedMask |= 1u << slotIdx;
                Vector2 initDir = hasFwd ? fwdDir : new Vector2(0f, 1f);
                _tracked[slotIdx] = new TrackedWader
                {
                    Id = sample.Id,
                    LastPos = pos,
                    PrevFramePos = pos,
                    SmoothedDir = initDir,
                    SmoothedSpeed = 0f,
                    ChurnEnergy = 0f,
                    LastEmitClock = Clock,
                    Active = true,
                };
                Emit(pos.X, pos.Z, 0.24f * submergence);
                velPacked = new Vector4(initDir.X, initDir.Y, 0f, 0f);
            }

            if (assignedSlot == 0)
            {
                wader0Vel = velPacked;
            }
            else if (assignedSlot == 1)
            {
                wader1Vel = velPacked;
            }
        }

        for (int t = 0; t < Capacity; t++)
        {
            if ((claimedMask & (1u << t)) == 0u)
            {
                _tracked[t].Active = false;
            }
        }

        ripplesA = new Projection(_impulses[0], _impulses[1], _impulses[2], _impulses[3]);
        ripplesB = new Projection(_impulses[4], _impulses[5], _impulses[6], _impulses[7]);
        return anyActive;
    }

    private void Emit(float x, float z, float strength)
    {
        _impulses[_writeCursor] = new Vector4(x, z, Clock, strength);
        _writeCursor = (_writeCursor + 1) & (Capacity - 1);
    }
}

/// <summary>
/// Pure-managed policy and frustum/reprojection math for <c>water_quality</c> tiers.
/// Safe to exercise from headless unit tests without initializing unmanaged Godot classes.
/// </summary>
public static class Engine_WaterQuality
{
    public const string CVarName = "water_quality";

    /// <summary>Parses a <c>water_quality</c> value, returning <paramref name="fallback"/> on unknown tokens.</summary>
    public static WaterQualityTier Parse(string? raw, WaterQualityTier fallback = WaterQualityTier.Balanced)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().Trim('"').ToLowerInvariant() switch
        {
            "low" or "0" => WaterQualityTier.Low,
            "balanced" or "medium" or "1" => WaterQualityTier.Balanced,
            "high" or "2" => WaterQualityTier.High,
            "ultra" or "3" => WaterQualityTier.Ultra,
            _ => fallback,
        };
    }

    /// <summary>Formats a <see cref="WaterQualityTier"/> to its canonical <c>refii.toml</c> string.</summary>
    public static string ToConfigString(WaterQualityTier tier) => tier switch
    {
        WaterQualityTier.Low => "low",
        WaterQualityTier.Balanced => "balanced",
        WaterQualityTier.High => "high",
        WaterQualityTier.Ultra => "ultra",
        _ => "balanced",
    };

    /// <summary>Resolves the effective water quality tier from parsed config values and platform.</summary>
    public static WaterQualityTier ResolveFromValues(
        IReadOnlyDictionary<string, string>? values,
        bool isAndroid)
    {
        WaterQualityTier fallback = isAndroid ? WaterQualityTier.Low : WaterQualityTier.Balanced;
        return values is not null && values.TryGetValue(CVarName, out string? raw) && raw.Length != 0
            ? Parse(raw, fallback)
            : fallback;
    }

    /// <summary>Tangential mirror frustum expansion so edge ripples sample genuine off-screen geometry.</summary>
    public const float MirrorOverscanFactor = 1.10f;

    /// <summary>
    /// Expands a camera field-of-view angle in degrees by <paramref name="overscan"/> in tangent space
    /// so planar mirrors capture an off-screen margin around all four edges for wave distortion and camera pans.
    /// </summary>
    public static float ComputeOverscanFov(float fovDeg, float overscan = MirrorOverscanFactor)
    {
        if (!(fovDeg > 1f) || !(overscan > 0f))
        {
            return fovDeg;
        }

        float clamped = Math.Clamp(fovDeg, 1f, 165f);
        float halfTan = MathF.Tan(clamped * (MathF.PI / 360f)) * overscan;
        return Math.Clamp(2f * MathF.Atan(halfTan) * (180f / MathF.PI), 1f, 168f);
    }

    /// <summary>Returns the planar mirror budget for <paramref name="tier"/>.</summary>
    public static WaterMirrorSpec MirrorFor(WaterQualityTier tier) => tier switch
    {
        WaterQualityTier.Low => new(false, 256, 4, 50f, 4f),
        WaterQualityTier.Balanced => new(true, 272, 2, 800f, 4f),
        WaterQualityTier.High => new(true, 352, 2, 1200f, 3.5f),
        WaterQualityTier.Ultra => new(true, 576, 1, float.PositiveInfinity, 2f),
        _ => new(true, 272, 2, 800f, 4f),
    };

    /// <summary>Returns the linear SSR march step budget for <paramref name="tier"/>.</summary>
    public static int SsrStepsFor(WaterQualityTier tier, bool hasPlanarMirror)
    {
        _ = hasPlanarMirror;
        return tier switch
        {
            WaterQualityTier.Low => 0,
            WaterQualityTier.Balanced => 12,
            WaterQualityTier.High => 18,
            WaterQualityTier.Ultra => 24,
            _ => 12,
        };
    }

    /// <summary>
    /// Computes exact Reversed-Z oblique near-plane parameters mapping <c>world.y == waterClipY</c>
    /// onto the hardware near plane (<c>clip.z == clip.w</c>) for a planar mirror camera while keeping
    /// every above-water point inside the frustum in <c>[0.05..1.0]</c> NDC depth.
    /// </summary>
    public static void ComputeObliqueMirrorParams(
        Vector3 eyeOrigin,
        Vector3 mirrorFwd,
        Vector3 mirrorUp,
        Vector3 mirrorRight,
        float waterHeight,
        float fovDeg,
        float aspect,
        bool keepWidth,
        out float waterClipY,
        out float obliqueAlpha)
    {
        waterClipY = waterHeight + 0.08f;
        float hAbove = eyeOrigin.Y - waterClipY;
        if (hAbove <= 0.08f || fovDeg > 145f || fovDeg <= 1f)
        {
            obliqueAlpha = 0f;
            return;
        }

        float halfTan = MathF.Tan(fovDeg * (MathF.PI / 360f));
        float safeAspect = MathF.Max(aspect, 1e-3f);
        float tanY = keepWidth ? (halfTan / safeAspect) : halfTan;
        float tanX = keepWidth ? halfTan : (halfTan * safeAspect);
        float denom = MathF.Max(mirrorFwd.Y, 0f) + (MathF.Abs(mirrorUp.Y) * tanY) + (MathF.Abs(mirrorRight.Y) * tanX);
        obliqueAlpha = 0.95f / MathF.Max(denom, 0.05f);
    }

    /// <summary>Evaluates the vertex shader's Reversed-Z oblique clip Z value for unit verification.</summary>
    public static float EvaluateObliqueClipZ(Vector3 worldPos, float clipW, float waterClipY, float obliqueAlpha) =>
        obliqueAlpha > 0f ? clipW - (obliqueAlpha * (worldPos.Y - waterClipY)) : clipW;

    /// <summary>
    /// Builds a projection matrix mapping world-space points into <paramref name="eye"/>'s
    /// <c>[0..1]</c> <c>SCREEN_UV</c> coordinates so held planar reflections track camera rotation smoothly.
    /// </summary>
    public static Projection BuildMainScreenReprojection(Transform3D eye, float fovDeg, float aspect, bool keepWidth)
    {
        Vector3 r = eye.Basis.Column0.Normalized();
        Vector3 u = eye.Basis.Column1.Normalized();
        Vector3 f = (-eye.Basis.Column2).Normalized();
        float halfTan = MathF.Max(MathF.Tan(fovDeg * (MathF.PI / 360f)), 1e-4f);
        float safeAspect = MathF.Max(aspect, 1e-3f);
        float tanY = keepWidth ? (halfTan / safeAspect) : halfTan;
        float tanX = keepWidth ? halfTan : (halfTan * safeAspect);

        Vector3 rowX = (0.5f * f) + ((0.5f / tanX) * r);
        Vector3 rowY = (0.5f * f) - ((0.5f / tanY) * u);
        Vector3 rowW = f;

        float dX = -rowX.Dot(eye.Origin);
        float dY = -rowY.Dot(eye.Origin);
        float dW = -rowW.Dot(eye.Origin);

        return new Projection(
            new Vector4(rowX.X, rowY.X, 0f, rowW.X),
            new Vector4(rowX.Y, rowY.Y, 0f, rowW.Y),
            new Vector4(rowX.Z, rowY.Z, 0f, rowW.Z),
            new Vector4(dX, dY, 0f, dW));
    }
}
