using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// A <c>Vector4[]</c> shader parameter's value held as one Variant, rebuilt only when its values change.
/// </summary>
/// <remarks>
/// A packed array crosses into Godot as a Variant whose Disposer registers a weak reference with
/// Godot's DisposablesTracker: two finalizable objects per call, disposed or not. Made per draw
/// per frame they were most of the 140,000 objects each gen1 collection promoted for
/// finalization, and that finalization pass was most of its 40 ms pause (2026-10-15).
/// </remarks>
public sealed class Engine_ArrayParam : IDisposable
{
    private Vector4[] _held = Array.Empty<Vector4>();
    private Variant _value;
    private bool _live;

    /// <summary>Bumped each time the values change, so a reader can tell whether it sent the current ones.</summary>
    public int Version { get; private set; }

    /// <summary>The held values as a Variant; valid after the first <see cref="Take"/>.</summary>
    public Variant Value => _value;

    /// <summary>Holds <paramref name="values"/>; true when they differ from the held ones (or none were held).</summary>
    public bool Take(ReadOnlySpan<Vector4> values)
    {
        if (_live && values.SequenceEqual(_held))
        {
            return false;
        }
        if (_held.Length != values.Length)
        {
            _held = new Vector4[values.Length];
        }
        values.CopyTo(_held);
        if (_live)
        {
            _value.Dispose();
        }
        _value = Variant.CreateFrom(_held);
        _live = true;
        ++Version;
        return true;
    }

    public void Dispose()
    {
        if (_live)
        {
            _value.Dispose();
            _live = false;
        }
    }
}
