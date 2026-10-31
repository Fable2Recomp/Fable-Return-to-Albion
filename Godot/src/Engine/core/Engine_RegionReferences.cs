using System;
using System.Collections.Generic;

namespace Refii.Core;

/// <summary>Retires borrowed lighting references and an exclusively owned iterator before clearing a native binding.</summary>
public static class Engine_RegionReferences
{
    /// <summary>Drops roots before releasing the iterator, preserving that ordering even if release/publication fails.</summary>
    /// <typeparam name="TProbe">Borrowed probe-data type.</typeparam><typeparam name="TNode">Borrowed iterator element type.</typeparam>
    /// <typeparam name="TFrame">Borrowed lighting-frame type.</typeparam>
    /// <param name="probe">Previous probe data; never disposed.</param><param name="walk">Owned queued walk.</param>
    /// <param name="frame">Previous lighting state; never disposed.</param><param name="clearBinding">Main-thread binding reset.</param>
    public static void Release<TProbe, TNode, TFrame>(ref TProbe? probe, ref IEnumerator<TNode>? walk,
        ref TFrame? frame, Action clearBinding) where TProbe : class where TFrame : class
    {
        ArgumentNullException.ThrowIfNull(clearBinding);
        probe = null;
        frame = null;
        IEnumerator<TNode>? owned = walk;
        walk = null;
        try { owned?.Dispose(); }
        finally { clearBinding(); }
    }
}
