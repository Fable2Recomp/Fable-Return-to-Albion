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
/// Clearing removes cache references; it does not deterministically release native/GPU storage.
/// Materials and active callers retain borrowed resources. Android's retirement boundary waits
/// for producers and queued node deletion before clearing, then measures bounded collection and
/// finalizer passes. Desktop keeps its existing clear timing. Never Dispose borrowed entries.
/// </remarks>
public static partial class Engine_LevelCaches
{
	/// <summary>Default cross-region texture cache budget in MB when <c>region_cache_budget_mb</c> is unset.</summary>
	public static int ResolveDefaultRegionCacheBudgetMb(bool isD3d12) => isD3d12 ? 256 : 512;

	/// <summary>Drops every region-scoped cache. Idempotent.</summary>
	public static void ReleaseRegion() => ReleaseRegion(Refii.Core.Engine_Platform.IsAndroid);

	/// <summary>Executes the platform retirement branch, also permitting an actual desktop Godot lifecycle fixture.</summary>
	/// <param name="android">Whether to retire Android's additional CPU/lighting owners.</param>
	internal static void ReleaseRegion(bool android)
	{
		// Materials and prop meshes pin underlying textures and hold 64 KB-aligned D3D12 buffers,
		// so clear them unconditionally while retaining only hot raw textures within budget.
		Refii.Models.Engine_MdlMaterial.ClearSharedCache();
		Refii.Level.Engine_PropStreamer.ClearCache();

		if (android || Refii.Models.Engine_ClassicMaterial.Active)
		{
			Refii.Textures.Engine_TexLibrary.Clear();
		}
		else
		{
			int defaultBudgetMb = ResolveDefaultRegionCacheBudgetMb(
				Refii.Textures.Engine_TextureUpload.IsD3D12Driver);
			int budgetMb = defaultBudgetMb;
			try
			{
				budgetMb = Refii.Core.Engine_RefiiConfig.Int(
					Refii.Core.Engine_RefiiConfig.Read(out _), "region_cache_budget_mb", defaultBudgetMb);
			}
			catch (System.Exception)
			{
				budgetMb = defaultBudgetMb;
			}

			if (budgetMb <= 0)
			{
				Refii.Textures.Engine_TexLibrary.Clear();
			}
			else
			{
				Refii.Textures.Engine_TexLibrary.TrimToBudget((long)budgetMb * 1024L * 1024L);
			}
		}
		Refii.Level.Engine_LevelFx.ClearCaches();
		Refii.Level.Engine_TerrainSplatMaterial.ClearSharedCache();
		Refii.Lighting.Engine_LevelBakedLighting.ClearContainerCache();
		Refii.Lighting.Engine_LevelShadowMaps.Clear();
		Refii.Lighting.Engine_LevelAmbientOcclusion.Clear();
		Refii.Models.Engine_ModelSelfAo.ClearMemoryCache();
		Refii.Fx.FableFx.ClearRegionCaches();
		Refii.Fx.Engine_FxMeshTwins.ClearRegionCaches();
		Refii.Entities.Engine_EntityHighlight.Forget();
		Refii.Entities.Engine_EntityAppearance.ForgetAll();
		Refii.Entities.Engine_ShadowProxy.Forget();
		Refii.Level.Engine_PropShadow.Forget();
		Refii.Level.Engine_PropFarShading.Forget();
		Refii.Level.Engine_BuildingOccluders.Clear();
		Refii.Entities.Engine_EntityAttachments.ClearCaches();
		if (android) ReleaseAndroidRegion();  // Engine_LevelCaches.Android.cs
	}
}
