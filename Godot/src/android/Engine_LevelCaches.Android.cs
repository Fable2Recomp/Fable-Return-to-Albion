namespace Refii.Core;

/// <summary>Android's part of releasing a region.</summary>
public static partial class Engine_LevelCaches
{
	/// <summary>Retires Android's additional CPU/lighting owners: the hero's morph textures, cloth pieces and baked-light references.</summary>
	private static void ReleaseAndroidRegion()
	{
		Refii.Hero.Engine_HeroTextureMorph.ReleaseRegionTextures();
		Refii.Physics.Engine_ClothPieces.Invalidate();
		Refii.Lighting.Engine_LevelBakedLighting.ReleaseRegionReferences();
	}
}
