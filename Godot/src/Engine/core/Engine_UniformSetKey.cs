using System;
using Godot;

namespace Refii.Core;

/// <summary>
/// A uniform set held by the resources it binds, so a render callback asks
/// <c>UniformSetCacheRD</c> again only when one of them changed.
/// </summary>
/// <remarks>
/// Asking means building <c>RDUniform</c>s and an <c>Array</c> of them: finalizable wrappers,
/// every frame, for an answer that is almost always the same set. The held set is checked with
/// <c>UniformSetIsValid</c>, as the cache frees a set with any resource it names.
/// </remarks>
public sealed class Engine_UniformSetKey
{
    private readonly Rid[] _resources;
    private Rid _set;

    public Engine_UniformSetKey(int resources) => _resources = new Rid[resources];

    /// <summary>The held set, when it still binds exactly <paramref name="resources"/>; else default, and the key takes them.</summary>
    public Rid For(RenderingDevice rd, ReadOnlySpan<Rid> resources)
    {
        bool same = _set.IsValid && resources.Length == _resources.Length;
        for (int i = 0; same && i < resources.Length; i++)
        {
            same = resources[i] == _resources[i];
        }
        if (same && rd.UniformSetIsValid(_set))
        {
            return _set;
        }
        resources.CopyTo(_resources);
        _set = default;
        return default;
    }

    /// <summary>Holds the set built for the resources the last <see cref="For"/> took.</summary>
    public Rid Hold(Rid set) => _set = set;
}
