using System;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// Thread-safe, zero-allocation snapshot publisher for the scene-referred <c>RGBA16F</c>
/// ray-marched volumetric light-shaft compute pass in <see cref="Engine_PostFxEffect"/>.
/// Pairs placed &amp; synthetic omni/spot lights (with baked <c>fable_static_shadow_atlas</c>
/// <see cref="Texture2DArray"/> tiles) with live moving character capsules and ground mist volumes.
/// </summary>
public static class Engine_VolumetricLightShafts
{
	public const int MaxShaftLights = 64;
	public const int MaxMistVolumes = 16;
	public const int MaxCapsules = 32;
	public const float MaxFogRangeMetres = 80.0f;

	public readonly record struct ShaftLightInput(
		Vector3 Position,
		float StatedRange,
		float NodeRange,
		Vector3 LinearColor,
		float Energy,
		float FogEnergyScale,
		Vector4 AtlasRect,
		bool IndoorFromOutside,
		Vector3 SpotDir = default,
		float SpotCosHalfAngle = -2.0f,
		uint CasterMask = 0u);

	public readonly record struct VolumetricCapsule(
		Vector3 SegA,
		float Radius,
		Vector3 SegB,
		float Feather);

	public readonly record struct MistVolumeSnapshot(
		Vector3 Min,
		float Extinction,
		Vector3 Max,
		float Flatten,
		Vector3 LinearAlbedo,
		Vector4 NoiseScaleOffset);

	private static readonly object SyncRoot = new();

	private static Vector3 _mediumScattering;
	private static float _mediumExtinction;
	private static float _mediumAnisotropy = 0.65f;
	private static float _fogStart;
	private static int _mediumGeneration;

	private static bool _active;
	private static int _lightCount;
	private static int _mistCount;
	private static int _capsuleCount;
	private static Vector3 _snapshotScattering;
	private static float _snapshotExtinction;
	private static float _snapshotAnisotropy = 0.65f;
	private static float _snapshotFogStart;

	private static readonly ShaftLightInput[] SnapshotLights = new ShaftLightInput[MaxShaftLights];
	private static readonly MistVolumeSnapshot[] SnapshotMists = new MistVolumeSnapshot[MaxMistVolumes];
	private static readonly VolumetricCapsule[] SnapshotCapsules = new VolumetricCapsule[MaxCapsules];

	/// <summary>Incremented whenever <see cref="SetMedium"/> updates the volumetric medium.</summary>
	public static int MediumGeneration => _mediumGeneration;

	/// <summary>
	/// Updates the current volumetric medium scattering coefficient, extinction, anisotropy, and fog start distance
	/// from <see cref="Engine_LevelLighting.ConfigureEnvironment"/>.
	/// </summary>
	public static void SetMedium(float density, Color linearTint, float anisotropy = 0.65f, float fogStart = 0.0f)
	{
		lock (SyncRoot)
		{
			if (density <= 1e-5f)
			{
				if (_mediumExtinction != 0.0f || _mediumScattering != Vector3.Zero)
				{
					_mediumGeneration++;
				}

				_mediumScattering = Vector3.Zero;
				_mediumExtinction = 0.0f;
				_mediumAnisotropy = anisotropy;
				_fogStart = 0.0f;
				_active = false;
				_lightCount = 0;
				return;
			}

			// Calibrated scattering coefficient for scene-referred HDR raymarching so both subtle
			// town hazes (density ~ 0.0014) and dense cave/swamp fogs (density ~ 0.015..0.030)
			// produce visible, physically balanced volumetric shafts without washing out contrast.
			float shaftDensity = MathF.Max(density * 2.2f, MathF.Sqrt(density) * 0.14f);
			float r = MathF.Max(linearTint.R, 0.0f);
			float g = MathF.Max(linearTint.G, 0.0f);
			float b = MathF.Max(linearTint.B, 0.0f);
			float peak = MathF.Max(r, MathF.Max(g, b));
			Vector3 tint = peak > 1e-4f
				? new Vector3(
					Mathf.Lerp(1.0f, r / peak, 0.25f),
					Mathf.Lerp(1.0f, g / peak, 0.25f),
					Mathf.Lerp(1.0f, b / peak, 0.25f))
				: Vector3.One;

			Vector3 nextScattering = tint * shaftDensity;
			float nextStart = MathF.Max(fogStart, 0.0f);
			if (_mediumScattering.DistanceSquaredTo(nextScattering) > 1e-8f ||
			    MathF.Abs(_mediumExtinction - density) > 1e-6f ||
			    MathF.Abs(_mediumAnisotropy - anisotropy) > 1e-4f ||
			    MathF.Abs(_fogStart - nextStart) > 1e-3f)
			{
				_mediumGeneration++;
			}

			_mediumScattering = nextScattering;
			_mediumExtinction = density;
			_mediumAnisotropy = anisotropy;
			_fogStart = nextStart;
		}
	}

	/// <summary>Whether the active level currently has a visible volumetric medium for light shafts.</summary>
	public static bool HasActiveMedium
	{
		get
		{
			lock (SyncRoot)
			{
				return _mediumScattering.X > 1e-6f || _mediumScattering.Y > 1e-6f || _mediumScattering.Z > 1e-6f;
			}
		}
	}

	/// <summary>
	/// Computes the 32-bit bitmask of dynamic character capsules in <paramref name="capsules"/> whose
	/// bounding volume intersects the sphere <c>(lightPos, lightRange)</c>.
	/// </summary>
	public static uint ComputeLightCapsuleMask(
		Vector3 lightPos,
		float lightRange,
		ReadOnlySpan<VolumetricCapsule> capsules)
	{
		if (lightRange <= 1e-3f || capsules.IsEmpty)
		{
			return 0u;
		}

		int count = Math.Min(capsules.Length, MaxCapsules);
		uint mask = 0u;
		for (int c = 0; c < count; c++)
		{
			ref readonly VolumetricCapsule cap = ref capsules[c];
			float pad = lightRange + cap.Radius + MathF.Max(cap.Feather, 0.0f);
			if (PointSegmentDistanceSquared(lightPos, cap.SegA, cap.SegB) <= pad * pad)
			{
				mask |= 1u << c;
			}
		}

		return mask;
	}

	private static float PointSegmentDistanceSquared(Vector3 p, Vector3 a, Vector3 b)
	{
		Vector3 ab = b - a;
		float lenSq = ab.LengthSquared();
		if (lenSq <= 1e-6f)
		{
			return p.DistanceSquaredTo(a);
		}

		float t = Math.Clamp((p - a).Dot(ab) / lenSq, 0.0f, 1.0f);
		Vector3 closest = a + (ab * t);
		return p.DistanceSquaredTo(closest);
	}

	/// <summary>
	/// Computes the tight world-space AABB (center and size) enclosing the union of active light spheres
	/// clamped to the camera's <paramref name="maxFogDistance"/> box. Returns <c>false</c> when empty.
	/// </summary>
	public static bool ComputeTightLightBounds(
		Vector3 eye,
		float maxFogDistance,
		ReadOnlySpan<ShaftLightInput> lights,
		out Vector3 center,
		out Vector3 size)
	{
		center = Vector3.Zero;
		size = Vector3.Zero;
		if (lights.IsEmpty || maxFogDistance <= 0f)
		{
			return false;
		}

		Vector3 clampMin = eye - new Vector3(maxFogDistance, maxFogDistance, maxFogDistance);
		Vector3 clampMax = eye + new Vector3(maxFogDistance, maxFogDistance, maxFogDistance);

		var bMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
		var bMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
		bool any = false;

		for (int i = 0; i < lights.Length; i++)
		{
			ref readonly ShaftLightInput l = ref lights[i];
			if (l.StatedRange <= 1e-3f || l.Energy <= 1e-4f)
			{
				continue;
			}

			var r = new Vector3(l.StatedRange, l.StatedRange, l.StatedRange);
			Vector3 lMin = (l.Position - r).Max(clampMin);
			Vector3 lMax = (l.Position + r).Min(clampMax);
			if (lMax.X <= lMin.X || lMax.Y <= lMin.Y || lMax.Z <= lMin.Z)
			{
				continue;
			}

			bMin = bMin.Min(lMin);
			bMax = bMax.Max(lMax);
			any = true;
		}

		if (!any)
		{
			return false;
		}

		size = bMax - bMin;
		if (size.X <= 0.05f || size.Y <= 0.05f || size.Z <= 0.05f)
		{
			return false;
		}

		center = (bMin + bMax) * 0.5f;
		return true;
	}

	/// <summary>Clears the active shaft snapshot when fog is off or no lights are in range.</summary>
	public static void HideVolume()
	{
		lock (SyncRoot)
		{
			_active = false;
			_lightCount = 0;
			_mistCount = 0;
			_capsuleCount = 0;
		}
	}

	/// <summary>Backward-compatible overload for callers passing only lights.</summary>
	public static void Update(Node hostNode, Vector3 eye, ReadOnlySpan<ShaftLightInput> lights)
	{
		_ = hostNode;
		Update(eye, lights, ReadOnlySpan<MistVolumeSnapshot>.Empty, ReadOnlySpan<VolumetricCapsule>.Empty);
	}

	/// <summary>
	/// Publishes the active omni/spot light shaft records, mist volumes, and dynamic character capsules
	/// for the render thread's <see cref="Engine_PostFxEffect"/> volumetric shaft pass.
	/// </summary>
	public static void Update(
		Vector3 eye,
		ReadOnlySpan<ShaftLightInput> lights,
		ReadOnlySpan<MistVolumeSnapshot> mists,
		ReadOnlySpan<VolumetricCapsule> capsules)
	{
		lock (SyncRoot)
		{
			bool hasMed = _mediumScattering.X > 1e-6f || _mediumScattering.Y > 1e-6f || _mediumScattering.Z > 1e-6f;
			bool hasMist = !mists.IsEmpty;
			if ((!hasMed && !hasMist) || lights.IsEmpty)
			{
				_active = false;
				_lightCount = 0;
				_mistCount = 0;
				_capsuleCount = 0;
				return;
			}

			int lCount = Math.Min(lights.Length, MaxShaftLights);
			ReadOnlySpan<ShaftLightInput> lSlice = lights.Slice(0, lCount);
			if (!ComputeTightLightBounds(eye, MaxFogRangeMetres, lSlice, out _, out _))
			{
				_active = false;
				_lightCount = 0;
				_mistCount = 0;
				_capsuleCount = 0;
				return;
			}

			int mCount = Math.Min(mists.Length, MaxMistVolumes);
			int cCount = Math.Min(capsules.Length, MaxCapsules);
			ReadOnlySpan<VolumetricCapsule> cSlice = capsules.Slice(0, cCount);

			for (int m = 0; m < mCount; m++)
			{
				SnapshotMists[m] = mists[m];
			}

			for (int c = 0; c < cCount; c++)
			{
				SnapshotCapsules[c] = cSlice[c];
			}

			for (int i = 0; i < lCount; i++)
			{
				ref readonly ShaftLightInput src = ref lSlice[i];
				uint mask = src.CasterMask != 0u || cCount == 0
					? src.CasterMask
					: ComputeLightCapsuleMask(src.Position, src.StatedRange, cSlice);
				SnapshotLights[i] = src with { CasterMask = mask };
			}

			_lightCount = lCount;
			_mistCount = mCount;
			_capsuleCount = cCount;
			_snapshotScattering = hasMed ? _mediumScattering : Vector3.Zero;
			_snapshotExtinction = hasMed ? _mediumExtinction : 0.0f;
			_snapshotAnisotropy = _mediumAnisotropy;
			_snapshotFogStart = _fogStart;
			_active = true;
		}
	}

	/// <summary>
	/// Copies the latest published volumetric shaft data on the render thread.
	/// Returns <c>false</c> when inactive or when Classic mode is active.
	/// </summary>
	public static bool TrySnapshot(
		Span<ShaftLightInput> lightsDst,
		out int lightCount,
		Span<MistVolumeSnapshot> mistsDst,
		out int mistCount,
		Span<VolumetricCapsule> capsulesDst,
		out int capsuleCount,
		out Vector3 mediumScattering,
		out float mediumExtinction,
		out float mediumAnisotropy,
		out float fogStart)
	{
		lightCount = 0;
		mistCount = 0;
		capsuleCount = 0;
		mediumScattering = Vector3.Zero;
		mediumExtinction = 0.0f;
		mediumAnisotropy = 0.65f;
		fogStart = 0.0f;

		if (Refii.Models.Engine_ClassicMaterial.Active || !Engine_LightingFrame.VolumetricFogEnabled)
		{
			return false;
		}

		lock (SyncRoot)
		{
			if (!_active || _lightCount <= 0)
			{
				return false;
			}

			lightCount = Math.Min(_lightCount, lightsDst.Length);
			mistCount = Math.Min(_mistCount, mistsDst.Length);
			capsuleCount = Math.Min(_capsuleCount, capsulesDst.Length);

			SnapshotLights.AsSpan(0, lightCount).CopyTo(lightsDst);
			SnapshotMists.AsSpan(0, mistCount).CopyTo(mistsDst);
			SnapshotCapsules.AsSpan(0, capsuleCount).CopyTo(capsulesDst);

			mediumScattering = _snapshotScattering;
			mediumExtinction = _snapshotExtinction;
			mediumAnisotropy = _snapshotAnisotropy;
			fogStart = _snapshotFogStart;
			return true;
		}
	}
}
