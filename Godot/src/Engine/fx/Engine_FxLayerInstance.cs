using Godot;

namespace Refii.Fx;

/// <summary>
/// An FX surface drawn by one of the layer viewports (<see cref="Engine_FxAdditiveLayer"/>,
/// <see cref="Engine_FxDisplacement"/>): a rendering-server instance in that viewport's OWN
/// World3D, not a node in the scene.
/// </summary>
/// <remarks>
/// In the layer's own world the main scene's spatial index never holds an FX surface, so no
/// shadow pass's convex query and no other viewport's cull walks it, and it moves only when its
/// effect is told to rather than through the scene tree. The layers' shaders take the scene
/// depth from the depth-capture global, so the world they live in does not matter to the picture.
/// </remarks>
public sealed class Engine_FxLayerInstance
{
	public Rid Rid { get; private set; }

	private Engine_FxLayerInstance(Rid rid) => Rid = rid;

	/// <summary>Null until the layer's world exists (the layer node is added deferred).</summary>
	public static Engine_FxLayerInstance? Create(Rid mesh, Rid scenario, uint layers, float cullMargin,
		Transform3D transform, bool visible, Rid materialOverride = default)
	{
		if (!mesh.IsValid || !scenario.IsValid)
		{
			return null;
		}

		Rid rid = RenderingServer.InstanceCreate2(mesh, scenario);
		RenderingServer.InstanceSetLayerMask(rid, layers);
		RenderingServer.InstanceGeometrySetCastShadowsSetting(rid, RenderingServer.ShadowCastingSetting.Off);
		RenderingServer.InstanceSetExtraVisibilityMargin(rid, cullMargin);
		if (materialOverride.IsValid)
		{
			RenderingServer.InstanceGeometrySetMaterialOverride(rid, materialOverride);
		}

		RenderingServer.InstanceSetTransform(rid, transform);
		RenderingServer.InstanceSetVisible(rid, visible);
		return new Engine_FxLayerInstance(rid);
	}

	public void SetTransform(Transform3D transform) => RenderingServer.InstanceSetTransform(Rid, transform);

	public void SetVisible(bool visible) => RenderingServer.InstanceSetVisible(Rid, visible);

	public void SetShaderParameter(StringName name, Variant value) =>
		RenderingServer.InstanceGeometrySetShaderParameter(Rid, name, value);

	public void Free()
	{
		if (Rid.IsValid)
		{
			RenderingServer.FreeRid(Rid);
		}

		Rid = default;
	}
}
