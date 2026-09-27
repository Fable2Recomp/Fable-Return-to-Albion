using Godot;

namespace Refii.Level;

/// <summary>
/// The visual layer that says "the water reflects this", and the masks that follow from it.
/// </summary>
/// <remarks>
/// THE GUEST STATES WHAT ITS WATER REFLECTS. Its 256² reflection tile draws the terrain, the sky
/// and clouds, and only the drawables whose definition sets IncludeInReflection (drawable bit 2,
/// `sub_82AA3C78`; every rig and every static-mesh entity definition defaults to 0), never a
/// translucent mesh, and it renders no sun cascades for the mirrored camera (the offscreen
/// renderer never runs the cascade pass — `sub_8228DA50` is reached only before the main scene,
/// and `sub_821DD2D0` skips the cascade loop when renderer+8 is null).
///
/// Godot shows an instance to a camera on ANY shared layer bit and picks a shadow pass's casters
/// as `camera.visible_layers & instance.layers & light.shadow_caster_mask`, so exclusion alone
/// cannot keep the cascades out of a mirror: the reflected world OPTS IN with this bit, the mirror
/// cameras see only this bit, and no light's caster mask ever includes it. The main camera sees
/// everything as before.
/// </remarks>
public static class Engine_ReflectionLayer
{
	/// <summary>Layer 16; 17-21 are the caster, highlight, lamp-host, displacement and additive layers.</summary>
	public const int Layer = 16;

	public const uint Mask = 1u << (Layer - 1);

	/// <summary>
	/// Live lever: follow the guest's contract (default) or reflect everything as before
	/// (`refii_render reflectContract=false`, an A/B only).
	/// </summary>
	public static bool FollowGuest = true;

	/// <summary>The layers of a world instance the water reflects: the main view's and this one.</summary>
	public const uint ReflectedLayers = 1u | Mask;

	/// <summary>What a mirror camera sees.</summary>
	public static uint MirrorCullMask => FollowGuest ? Mask : ~Engine_WaterReflection.WaterLayer;

	/// <summary>
	/// A light's shadow caster mask with the reflected bit cleared. Unconditional: the bit means
	/// "reflected", never "casts", whatever the lever says — with the lever off the mirrors see the
	/// main layer again and the cascades return through it.
	/// </summary>
	public static uint CasterMask(uint mask) => mask & ~Mask;
}
