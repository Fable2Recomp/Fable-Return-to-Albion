using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public sealed class Engine_DynamicRoofMap
{
    public const int Size = 256;
    public const float Extent = 160f;
    private readonly List<Engine_ClassicRoofs.Roof> _scratchRoofs = new();
    private bool _initialized;
    private int _lastRoofsVersion = -1;

    public float[] Pixels { get; } = new float[Size * Size * 2];
    public Vector2 Centre { get; private set; }
    public int Revision { get; private set; }
    public bool HasRoofs { get; private set; }

    public bool Update(Vector2 eye) => Update(null, eye);

    public bool Update(IReadOnlyList<Engine_ClassicRoofs.Roof>? externalRoofs, Vector2 eye)
    {
        if (!eye.IsFinite()) return false;
        Vector2 centre = _initialized && (eye - Centre).Abs().X <= 24f && (eye - Centre).Abs().Y <= 24f
            ? Centre : (eye / 16f).Round() * 16f;

        bool moved = !_initialized || centre != Centre;
        bool versionChanged = _lastRoofsVersion != Engine_ClassicRoofs.Version;
        if (!moved && !versionChanged) return false;

        _initialized = true;
        Centre = centre;
        _lastRoofsVersion = Engine_ClassicRoofs.Version;

        IReadOnlyList<Engine_ClassicRoofs.Roof> roofs;
        if (externalRoofs is not null)
        {
            roofs = externalRoofs;
        }
        else
        {
            float half = Extent * 0.5f;
            Vector3 qMin = new(-centre.X - half - 10f, centre.Y - half - 10f, -1000f);
            Vector3 qMax = new(-centre.X + half + 10f, centre.Y + half + 10f, 1000f);
            Engine_ClassicRoofs.CollectRoofsInBox(qMin, qMax, _scratchRoofs);
            roofs = _scratchRoofs;
        }

        Array.Clear(Pixels);
        HasRoofs = false;
        float cell = Extent / Size;
        Vector2 origin = Centre - Vector2.One * (Extent * 0.5f);
        foreach (var roof in roofs)
        {
            if (!float.IsFinite(roof.Top) || !roof.C0.IsFinite() || !roof.C1.IsFinite() ||
                !roof.C2.IsFinite() || !roof.C3.IsFinite()) continue;
            Vector2 min = roof.C0.Min(roof.C1).Min(roof.C2).Min(roof.C3);
            Vector2 max = roof.C0.Max(roof.C1).Max(roof.C2).Max(roof.C3);
            Vector2 start = (new Vector2(-max.X, min.Y) - origin) / cell;
            Vector2 end = (new Vector2(-min.X, max.Y) - origin) / cell;
            int x0 = Math.Clamp((int)MathF.Floor(start.X), 0, Size);
            int x1 = Math.Clamp((int)MathF.Ceiling(end.X), 0, Size);
            int y0 = Math.Clamp((int)MathF.Floor(start.Y), 0, Size);
            int y1 = Math.Clamp((int)MathF.Ceiling(end.Y), 0, Size);
            for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                Vector2 p = origin + new Vector2(x + 0.5f, y + 0.5f) * cell;
                if (!Engine_ClassicRoofs.Contains(roof, new Vector2(-p.X, p.Y))) continue;
                int index = (y * Size + x) * 2;
                if (Pixels[index + 1] > 0f && Pixels[index] >= roof.Top) continue;
                Pixels[index] = roof.Top;
                Pixels[index + 1] = 1f;
                HasRoofs = true;
            }
        }
        Revision++;
        return true;
    }
}
