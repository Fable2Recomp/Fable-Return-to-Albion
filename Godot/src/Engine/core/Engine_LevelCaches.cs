namespace Refii.Core;

/// <summary>
/// Releases the caches that are scoped to a region but outlive it.
/// </summary>
/// <remarks>
/// EVERY ONE OF THESE WAS PROCESS-LIFETIME. Measured 2026-09-08 on a live session: 6.1 GB
/// resident with a further 3.5 GB in swap, of which roughly half was texture payload, and
/// 1,914 of the game's 1,935 packed material maps were resident at once — the whole game's
/// set, because nothing ever dropped the previous region's.
///
/// Each of these dictionaries is keyed by an asset path and holds the decoded or built result.
/// That is the right shape for a cache and the wrong shape for an owner, and none of them had
/// a caller for the clear they already defined.
///
/// SAFE TO CALL WHILE THE GAME RUNS, because none of these maps OWNS what it holds. Materials
/// keep their own references to the textures they use, so clearing the library frees only what
/// nothing is drawing; the rest is rebuilt from the disk cache, which is what it is for.
/// Call it AFTER the region node is freed, so the entries the old region held go with it.
/// </remarks>
public static class Engine_LevelCaches
{
	/// <summary>Drops every region-scoped cache. Idempotent.</summary>
	public static void ReleaseRegion()
	{
		Refii.Textures.Engine_TexLibrary.Clear();
		Refii.Models.Engine_MdlMaterial.ClearSharedCache();
		Refii.Level.Engine_PropStreamer.ClearCache();
		Refii.Level.Engine_LevelFx.ClearCaches();
		Refii.Level.Engine_TerrainSplatMaterial.ClearSharedCache();
		Refii.Lighting.Engine_LevelBakedLighting.ClearContainerCache();
		Refii.Entities.Engine_EntityHighlight.Forget();
		Refii.Entities.Engine_EntityAppearance.ForgetAll();
		Refii.Entities.Engine_ShadowProxy.Forget();
		Refii.Level.Engine_PropShadow.Forget();
		Refii.Entities.Engine_EntityAttachments.ClearCaches();
	}
}
