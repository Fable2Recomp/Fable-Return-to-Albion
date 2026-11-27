using System;
using Godot;

namespace Refii.Fx;

/// <summary>
/// Shared DRY terrain- and bridge-hugging ground conformity helper used by both
/// <see cref="Engine_RemasterMagicCircle"/> (Will charging circle) and <see cref="Engine_FxSystem"/>
/// (flat AoE ground spell rings/bursts for Inferno, Chaos, and Shock).
/// </summary>
public static class Engine_FxGroundConform
{
	/// <summary>
	/// Delegate sampling the landscape heightfield at a Godot world position (returns <c>true</c>
	/// and sets <paramref name="groundY"/> when inside the loaded landscape grid).
	/// </summary>
	public delegate bool GroundHeightSampler(Vector3 worldPos, out float groundY);

	/// <summary>Vertical clearance above the terrain/bridge surface to eliminate depth z-fighting.</summary>
	public const float LiftMeters = 0.095f;

	/// <summary>
	/// Returns <c>true</c> for Fable II ground AoE spell effects whose flat horizontal rings/quads
	/// should conform to uneven terrain and bridges in Modern mode:
	/// Inferno (<c>FX_795222A2..A5</c>, <c>FX_795222A7</c>, <c>FX_A2E649EA</c>),
	/// Chaos (<c>FX_C02AE6F2..F6</c>), and Shock (<c>FX_96895970/72/74</c>).
	/// </summary>
	public static bool ShouldConformEffect(uint effectHash) => effectHash switch
	{
		// Inferno AoE concentric fire rings (R = 2m, 4m, 6m, 8m, 10m) & center burst
		0x795222A7u or 0x795222A4u or 0x795222A5u or 0x795222A2u or 0x795222A3u or 0xA2E649EAu => true,
		// Chaos AoE expanding ground rings (10m, 15m, 20m, 25m, 34m)
		0xC02AE6F6u or 0xC02AE6F5u or 0xC02AE6F4u or 0xC02AE6F3u or 0xC02AE6F2u => true,
		// Shock AoE ground glow & radial shockwave bursts (8m, 10m, 12m)
		0x96895970u or 0x96895972u or 0x96895974u => true,
		_ => false,
	};

	/// <summary>
	/// Returns the active region's landscape heightfield sampler, or <c>null</c> when no landscape
	/// ground grid is loaded.
	/// </summary>
	public static GroundHeightSampler? TryGetLiveGroundSampler()
	{
		Refii.Level.Engine_SkyEnvironmentNode? sky = Refii.Level.Engine_SkyEnvironmentNode.Current;
		return sky is not null && GodotObject.IsInstanceValid(sky) && sky.InteractionGround is { GridCount: > 0 }
			? sky.TrySampleGroundHeight
			: null;
	}

	/// <summary>
	/// Resolves the exact ground/floor Y height at world <c>(wx, wz)</c>, using the landscape
	/// heightfield directly when the effect center rests on natural terrain (hills and valleys) and
	/// taking <c>max(fallbackY, terrainY)</c> when crossing elevated bridges, stairs, or slabs.
	/// </summary>
	public static float ResolveSurfaceY(
		float wx,
		float wz,
		float fallbackY,
		float centerY,
		float centerGroundY,
		bool hasCenterGround,
		GroundHeightSampler? sampleGround)
	{
		if (sampleGround is null || !sampleGround(new Vector3(wx, fallbackY, wz), out float terrainY) || !float.IsFinite(terrainY))
		{
			return fallbackY;
		}

		bool onNaturalTerrain = hasCenterGround && MathF.Abs(centerY - centerGroundY) < 0.45f;
		if (onNaturalTerrain)
		{
			return terrainY;
		}

		if (terrainY > fallbackY && terrainY <= fallbackY + 3.0f)
		{
			return terrainY;
		}

		return fallbackY;
	}

	/// <summary>
	/// Zero-allocation per-emit ground conformity context for <see cref="Engine_FxSystem.Assemble"/>.
	/// Captures the effect's world transform and origin terrain classification once per emit.
	/// </summary>
	public readonly struct GroundContext
	{
		public readonly Transform3D LocalToWorld;
		public readonly Vector3 OriginWorld;
		public readonly float OriginGroundY;
		public readonly bool HasOriginGround;
		public readonly GroundHeightSampler Sampler;

		public GroundContext(Transform3D localToWorld, GroundHeightSampler sampler)
		{
			LocalToWorld = localToWorld;
			OriginWorld = localToWorld.Origin;
			Sampler = sampler;
			if (sampler(OriginWorld, out float ogY) && float.IsFinite(ogY))
			{
				HasOriginGround = true;
				OriginGroundY = ogY;
			}
			else
			{
				HasOriginGround = false;
				OriginGroundY = OriginWorld.Y;
			}
		}

		/// <summary>
		/// Computes the vertical terrain offset (<c>surfaceY - OriginWorld.Y</c>) at a node-local
		/// point <paramref name="localPos"/>. Returns <c>0</c> on flat ground or elevated bridge decks.
		/// </summary>
		public float ComputeTerrainDeltaY(Vector3 localPos)
		{
			Vector3 world = LocalToWorld * localPos;
			float surfaceY = ResolveSurfaceY(
				world.X, world.Z, OriginWorld.Y, OriginWorld.Y,
				OriginGroundY, HasOriginGround, Sampler);
			return surfaceY - OriginWorld.Y;
		}
	}
}
