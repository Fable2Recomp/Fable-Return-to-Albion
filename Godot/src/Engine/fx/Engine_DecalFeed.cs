using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Fx;

/// <summary>
/// The decals the guest states (RefiiHostGetDecals, taken from its render command `sub_82A40610`),
/// in its ring of <see cref="Slots"/>: the one reading of them both renderers draw from.
/// </summary>
/// <remarks>
/// The title keeps 50, the oldest replaced, none expiring. A decal with an owner was laid in the
/// owner's frame and follows the owner's matrix (twin+336, `sub_8220B490`) from where it was laid.
/// Positions and directions are guest axes (Godot is (-x, z, y)). Main thread.
/// </remarks>
public sealed class Engine_DecalFeed
{
    public const int Slots = 50;
    private const int Words = Refii.Host.HostBridge.DecalWords;

    /// <summary>One stated decal: the grid's centre and axis, its half sizes, its texture.</summary>
    public sealed class Decal
    {
        public uint Serial;
        public uint Owner;
        public uint Texture;
        public Vector3 Position;
        public Vector3 Direction;
        public Vector2 Half;
        internal Vector3 LocalPosition;
        internal Vector3 LocalDirection;
    }

    private readonly uint[] _words = new uint[Slots * Words];
    private readonly Decal?[] _slots = new Decal?[Slots];
    private uint _serial = uint.MaxValue;
    private int _count;

    /// <summary>The ring as last pumped; a slot keeps its decal until the guest drops it.</summary>
    public ReadOnlySpan<Decal?> Ring => _slots;

    /// <summary>Reads the guest's decals; owned ones follow their owners. Main thread, once a frame.</summary>
    public void Pump()
    {
        _count = (int)Refii.Host.HostBridge.GetDecals(_words, out uint serial);
        if (serial != _serial)
        {
            _serial = serial;
            Restate();
        }
        foreach (Decal? decal in _slots)
        {
            if (decal is not null)
            {
                Follow(decal);
            }
        }
    }

    /// <summary>The decal set moved: keep each one that is still stated, add the new ones.</summary>
    private void Restate()
    {
        var stated = new HashSet<uint>();
        for (int i = 0; i < _count; i++)
        {
            stated.Add(_words[i * Words]);
        }
        for (int s = 0; s < Slots; s++)
        {
            if (_slots[s] is { } decal && !stated.Contains(decal.Serial))
            {
                _slots[s] = null;
            }
        }
        for (int i = 0; i < _count; i++)
        {
            uint serial = _words[i * Words];
            if (Array.Exists(_slots, d => d is not null && d.Serial == serial))
            {
                continue;
            }
            int free = Array.FindIndex(_slots, d => d is null);
            if (free < 0)
            {
                break;
            }
            int o = i * Words;
            var decal = new Decal
            {
                Serial = serial,
                Owner = _words[o + 1],
                Texture = _words[o + 2],
                Position = new Vector3(F(o + 3), F(o + 4), F(o + 5)),
                Direction = new Vector3(F(o + 7), F(o + 8), F(o + 9)),
                Half = new Vector2(F(o + 11), F(o + 12)),
            };
            if (decal.Owner != 0u)
            {
                Transform3D inverse = OwnerMatrix(o).AffineInverse();
                decal.LocalPosition = inverse * decal.Position;
                decal.LocalDirection = inverse.Basis * decal.Direction;
            }
            _slots[free] = decal;
        }
    }

    private void Follow(Decal decal)
    {
        if (decal.Owner == 0u)
        {
            return;
        }
        for (int i = 0; i < _count; i++)
        {
            int o = i * Words;
            if (_words[o] != decal.Serial)
            {
                continue;
            }
            Transform3D now = OwnerMatrix(o);
            decal.Position = now * decal.LocalPosition;
            decal.Direction = (now.Basis * decal.LocalDirection).Normalized();
            return;
        }
    }

    /// <summary>The owner's matrix as `sub_8220B490` loads it: three rows (axis, translation).</summary>
    private Transform3D OwnerMatrix(int o)
    {
        int m = o + 15;
        var basis = new Basis(
            new Vector3(F(m), F(m + 4), F(m + 8)),
            new Vector3(F(m + 1), F(m + 5), F(m + 9)),
            new Vector3(F(m + 2), F(m + 6), F(m + 10)));
        return new Transform3D(basis, new Vector3(F(m + 3), F(m + 7), F(m + 11)));
    }

    private float F(int at) => BitConverter.UInt32BitsToSingle(_words[at]);
}
