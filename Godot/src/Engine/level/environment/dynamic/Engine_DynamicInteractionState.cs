using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public readonly record struct DynamicSurfaceContact(Vector3 Position, Vector3 Normal);
public readonly record struct DynamicSnowTrack(DynamicSurfaceContact Contact, Vector2 Forward, float Strength);

public sealed class Engine_DynamicInteractionState
{
    public const int TrackLimit = 128;
    public const float MapExtent = 24f;
    public const int MapSize = 512;
    public const int InstanceStride = 12;
    public const float FootprintSize = 0.64f;
    public const float StepSpacing = 0.65f;
    private readonly List<DynamicSnowTrack> _tracks = new();
    private Vector3? _previous;
    private float _travel;
    private bool _left;
    private float _rainBudget;

    public IReadOnlyList<DynamicSnowTrack> Tracks => _tracks;
    public int Revision { get; private set; }
    public float Erosion { get; private set; }

    public void Reset()
    {
        Revision++;
        Erosion = 0f;
        _tracks.Clear();
        _previous = null;
        _travel = 0f;
        _rainBudget = 0f;
        _left = false;
    }

    public void Fade(float delta, float snowfall, float snow)
    {
        if (!float.IsFinite(delta) || delta <= 0f) return;
        if (snow < 0.02f || _tracks.Count == 0)
        {
            if (_tracks.Count > 0) { _tracks.Clear(); Revision++; }
            Erosion = 0f;
            return;
        }
        float loss = delta * (1f / 90f + Unit(snowfall) / 22f);
        Erosion += loss;
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var track = _tracks[i] with { Strength = _tracks[i].Strength - loss };
            if (track.Strength <= 0f) { _tracks.RemoveAt(i); Revision++; }
            else _tracks[i] = track;
        }
        if (Erosion > 32f) { Erosion = 0f; Revision++; }
    }

    public void Move(Vector3? actor, bool grounded, Action<Vector3, Vector2> step)
    {
        if (actor is not { } position || !position.IsFinite() || !grounded)
        {
            _previous = null;
            _travel = 0f;
            return;
        }
        Vector3? previous = _previous;
        _previous = position;
        if (previous is not { } from) return;
        Vector3 movement = position - from;
        float distance = new Vector2(movement.X, movement.Z).Length();
        if (distance > 3f || MathF.Abs(movement.Y) > 0.65f)
        {
            _travel = 0f;
            return;
        }
        if (distance < 0.0001f) return;
        Vector2 forward = new Vector2(movement.X, movement.Z) / distance;
        float along = StepSpacing - _travel;
        while (along <= distance)
        {
            Vector3 foot = from.Lerp(position, along / distance);
            Vector2 side = new Vector2(-forward.Y, forward.X) * (_left ? -0.11f : 0.11f);
            foot += new Vector3(side.X, 0, side.Y);
            step(foot, forward);
            _left = !_left;
            along += StepSpacing;
        }
        _travel = (float)((_travel + distance) % StepSpacing);
    }

    public void Stamp(DynamicSurfaceContact contact, Vector2 forward, float strength)
    {
        if (!contact.Position.IsFinite() || !contact.Normal.IsFinite() || contact.Normal.Y < 0.55f ||
            !forward.IsFinite() || forward.LengthSquared() < 0.001f || Unit(strength) <= 0f) return;
        if (_tracks.Count == TrackLimit) _tracks.RemoveAt(0);
        _tracks.Add(new DynamicSnowTrack(contact, forward.Normalized(), Unit(strength)));
        Revision++;
    }

    public int WriteInstances(float[] buffer, Vector3 origin)
    {
        if (buffer.Length != TrackLimit * InstanceStride) throw new ArgumentException(nameof(buffer));
        if (!origin.IsFinite()) return 0;
        float scale = MapSize / MapExtent;
        float half = MapExtent * 0.5f;
        int count = 0;
        foreach (var track in _tracks)
        {
            Vector3 p = track.Contact.Position - origin;
            if (MathF.Abs(p.X) > half + FootprintSize || MathF.Abs(p.Z) > half + FootprintSize) continue;
            Vector2 forward = track.Forward;
            Vector2 right = new(-forward.Y, forward.X);
            int i = count++ * InstanceStride;
            buffer[i] = right.X * FootprintSize * scale;
            buffer[i + 1] = forward.X * FootprintSize * scale;
            buffer[i + 2] = 0f;
            buffer[i + 3] = (p.X + half) * scale;
            buffer[i + 4] = right.Y * FootprintSize * scale;
            buffer[i + 5] = forward.Y * FootprintSize * scale;
            buffer[i + 6] = 0f;
            buffer[i + 7] = (p.Z + half) * scale;
            buffer[i + 8] = track.Strength + Erosion;
            buffer[i + 9] = p.Y;
            Vector3 n = track.Contact.Normal;
            Vector2 slope = new Vector2(-n.X, -n.Z) / n.Y;
            buffer[i + 10] = slope.Dot(right);
            buffer[i + 11] = slope.Dot(forward);
        }
        return count;
    }

    public int RainCount(float delta, float rain, bool enabled)
    {
        if (!enabled) { _rainBudget = 0; return 0; }
        if (!float.IsFinite(delta) || delta <= 0f) return 0;
        _rainBudget += MathF.Min(delta, 0.1f) * Unit(rain) * 80f;
        int count = Math.Min((int)_rainBudget, 8);
        _rainBudget -= count;
        return count;
    }

    public void Rasterize(float[] pixels, Vector2 centre)
    {
        if (pixels.Length != MapSize * MapSize * 2) throw new ArgumentException(nameof(pixels));
        Array.Clear(pixels);
        float cell = MapExtent / MapSize;
        Vector2 origin = centre - Vector2.One * (MapExtent * 0.5f);
        foreach (var track in _tracks)
        {
            Vector3 p = track.Contact.Position;
            Vector2 at = (new Vector2(p.X, p.Z) - origin) / cell;
            int x0 = Math.Max(0, (int)MathF.Floor(at.X - 6));
            int x1 = Math.Min(MapSize - 1, (int)MathF.Ceiling(at.X + 6));
            int z0 = Math.Max(0, (int)MathF.Floor(at.Y - 6));
            int z1 = Math.Min(MapSize - 1, (int)MathF.Ceiling(at.Y + 6));
            Vector2 right = new(-track.Forward.Y, track.Forward.X);
            for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                Vector2 offset = origin + new Vector2(x + 0.5f, z + 0.5f) * cell - new Vector2(p.X, p.Z);
                float across = offset.Dot(right);
                float along = offset.Dot(track.Forward);
                float toe = new Vector2(across / 0.105f, (along - 0.055f) / 0.15f).Length();
                float heel = new Vector2(across / 0.075f, (along + 0.12f) / 0.085f).Length();
                float coverage = Math.Clamp((1.15f - MathF.Min(toe, heel)) / 0.32f, 0f, 1f) * track.Strength;
                int index = (z * MapSize + x) * 2;
                if (coverage <= pixels[index]) continue;
                Vector3 n = track.Contact.Normal;
                float height = p.Y - (n.X * offset.X + n.Z * offset.Y) / n.Y;
                pixels[index] = coverage;
                pixels[index + 1] = height * coverage;
            }
        }
    }

    public static bool TryContact(Engine_LandscapeGround ground, Transform3D worldFromGround,
        Vector3 world, out DynamicSurfaceContact contact)
    {
        contact = default;
        if (!world.IsFinite()) return false;
        Vector3 local = worldFromGround.AffineInverse() * world;
        if (!ground.TryHeight(local.X, local.Z, out float h) || !float.IsFinite(h)) return false;
        if (!ground.TryHeight(local.X + 0.25f, local.Z, out float hx)) hx = h;
        if (!ground.TryHeight(local.X, local.Z + 0.25f, out float hz)) hz = h;
        Vector3 normal = new Vector3((h - hx) * 4f, 1f, (h - hz) * 4f).Normalized();
        contact = new DynamicSurfaceContact(worldFromGround * new Vector3(local.X, h, local.Z),
            (worldFromGround.Basis.Inverse().Transposed() * normal).Normalized());
        return contact.Position.IsFinite() && contact.Normal.Y > 0.55f;
    }

    public static float Unit(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
}
