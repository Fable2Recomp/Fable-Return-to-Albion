using System;
using Godot;

namespace Refii.Lighting;

public static partial class Engine_LevelBakedLighting
{
	/// <summary>Retires the departing region's borrowed probe data, owned relight iterator and object-atlas binding.</summary>
	/// <remarks>Called on the main thread after outgoing producers stop. Native materials keep
	/// their own references; no borrowed image, material, texture or node is disposed.</remarks>
	public static void ReleaseRegionReferences() => ReleaseRegionReferences(() =>
		RenderingServer.GlobalShaderParameterSet(GlobalPropLightmapsName, default(Variant)));

	/// <summary>Executes the same reference retirement through a native-free publication seam.</summary>
	/// <param name="clearObjectAtlas">Clears the previous object-atlas global on the rendering main thread.</param>
	internal static void ReleaseRegionReferences(Action clearObjectAtlas)
	{
		ArgumentNullException.ThrowIfNull(clearObjectAtlas);
		LightProbeField? probe = CurrentProbes;
		CurrentProbes = null;
		Refii.Core.Engine_RegionReferences.Release(ref probe, ref _relightWalk, ref _relightFrame, clearObjectAtlas);
	}
}
