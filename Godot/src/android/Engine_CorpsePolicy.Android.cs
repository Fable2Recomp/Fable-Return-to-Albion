using Refii.Core;

namespace Refii.Entities;

/// <summary>Android-side compact corpse cap hook for <see cref="Engine_CorpsePolicy"/>.</summary>
public static partial class Engine_CorpsePolicy
{
    static partial void ResolveCompactCap(ref bool compact) =>
        compact = Engine_Platform.IsAndroid;
}
