using Godot;
using Refii.Textures;

namespace Refii.Hero;

/// <summary>Character composites on a phone: finished ones kept on disk (AndroidCompositeCache), and a
/// bound on the decoded sources in the low-memory tier.</summary>
public static partial class Engine_HeroTextureMorph
{
    /// <summary>A composite kept on disk by an earlier build; <paramref name="stored"/> is where this
    /// build's goes, or null when nothing is kept (off a phone).</summary>
    private static ImageTexture? TryStoredComposite(
        string cacheKey,
        uint original,
        Refii.Host.HostBridge.GuestTextureLayer[] layers,
        Refii.Host.HostBridge.GuestTextureLayer[] overlays,
        out string? stored)
    {
        stored = AndroidCompositeCache.Name(cacheKey, original, layers, overlays);
        return stored is null ? null : AndroidCompositeCache.TryLoad(stored);
    }

    /// <summary>Kept only under the name of what it was built from: a remount during the build changes it.</summary>
    private static string? KeepAs(
        string? stored,
        string cacheKey,
        uint original,
        Refii.Host.HostBridge.GuestTextureLayer[] layers,
        Refii.Host.HostBridge.GuestTextureLayer[] overlays) =>
        stored is not null && AndroidCompositeCache.Name(cacheKey, original, layers, overlays) == stored ? stored : null;

    /// <summary>Uploads <paramref name="image"/> and keeps it as <paramref name="stored"/>; null when there is nowhere to keep it.</summary>
    private static ImageTexture? StoredUpload(Image image, string? stored) =>
        stored is null ? null : AndroidCompositeCache.UploadAndStore(image, stored);

    /// <summary>What `hero_texture_sources_mb` 0 means: unbounded, or the low-memory tier's bound.</summary>
    private static int UnsetSourceMegabytes() =>
        Refii.Game.AndroidMemoryRelease.LowRamMode ? Refii.Game.AndroidMemoryRelease.LowRamHeroSourceMegabytes : 0;
}
