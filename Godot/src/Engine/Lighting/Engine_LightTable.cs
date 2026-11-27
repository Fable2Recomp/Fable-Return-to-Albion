using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// The placed lights' records for the surface shaders: one texel per light, (world x, y, z,
/// range), published as the <c>fable_light_table</c> global.
/// </summary>
/// <remarks>
/// Godot hands a custom <c>light()</c> a direction and one attenuation with the shadow already
/// multiplied in, and never the light's position. The one per-light scalar that does reach it is
/// SPECULAR_AMOUNT, which Godot fills with 2 × LightSpecular (light_storage.cpp,
/// <c>specular_amount = param[SPECULAR] * 2.0</c>, half precision in the light buffer — exact for
/// integers to 2048). So a registered light carries LightSpecular = slot + <see cref="SlotBase"/>,
/// the shader fetches its texel by that slot, measures the distance itself and recovers the
/// guest's linear ramp and the shadow separately. Our shaders never use SPECULAR_AMOUNT for
/// anything else.
///
/// Refreshed once per frame by <see cref="Engine_LightShadowBudget"/>: a flickering torch moves
/// its range every frame, and the texel has to say what the node says.
/// </remarks>
public static class Engine_LightTable
{
	public const string GlobalName = "fable_light_table";

	public const string ShadowAtlasGlobalName = "fable_static_shadow_atlas";

	/// <summary>Texels. Bounded by the half-precision slot channel: 2 × (Capacity + SlotBase) ≤ 2048.</summary>
	public const int Capacity = 1000;

	/// <summary>Added to the slot on the way into LightSpecular: 0 and 1 are below any default.</summary>
	public const float SlotBase = 2.0f;

	private static readonly object Gate = new();

	private static readonly Light3D?[] Slots = new Light3D?[Capacity];

	/// <summary>Deterministic 64-bit light key ((entity &lt;&lt; 32) | index) for each slot.</summary>
	private static readonly ulong[] Keys = new ulong[Capacity];

	/// <summary>Per-slot octahedral shadow atlas rect (AtlasU, AtlasV, AtlasScale, SliceIndex).</summary>
	private static readonly Vector4[] AtlasRects = new Vector4[Capacity];

	/// <summary>
	/// True when a registered light's world position and base node range are fixed in the level
	/// (unless <see cref="Engine_LevelLights.LightMetaGeneration"/> bumps), allowing <see cref="Refresh"/>
	/// to read <see cref="CachedPositions"/> and <see cref="CachedRanges"/> in pure C# without per-frame
	/// Godot C++ interop. Note: the Godot <see cref="Light3D.LightBakeMode"/> on the node itself MUST
	/// remain <see cref="Light3D.BakeMode.Dynamic"/> so SDFGI never accumulates static energy on walls.
	/// </summary>
	private static readonly bool[] FixedPlacement = new bool[Capacity];

	private static readonly bool[] HasCachedTransform = new bool[Capacity];

	private static readonly Vector3[] CachedPositions = new Vector3[Capacity];

	private static readonly float[] CachedRanges = new float[Capacity];

	private static readonly float[] CachedSoftCores = new float[Capacity];

	private static int _lastMetaGeneration = -1;

	private static StaticShadowMapContainer? _boundShadowMap;

	/// <summary>Three rows of Capacity texels: (x, y, z, Godot's range), (the STATED range, atlasU, atlasV, atlasScale), then (atlasLayer, 0, 0, 0) — or, on a phone, the one-fetch record (Refii.AndroidRuntime.AndroidLightTablePacking).</summary>
	private static readonly float[] Data = new float[Capacity * 4 * 3];

	private static readonly byte[] Bytes = new byte[Capacity * 16 * 3];

	/// <summary>
	/// The radius the guest STATES for a light this frame, where it differs from the node's. A
	/// flickering torch's radius breathes every step, and writing that to OmniRange moves the
	/// light's AABB: Godot re-pairs it against everything in reach, every frame, for every torch
	/// (measured 2026-09-21, Fairfax Gardens, 154 lights: frame 17.4 -> 11.7 ms with the writes
	/// held, same draw calls). The node keeps the flicker's ENVELOPE, which never moves, and the
	/// shader takes its falloff from here — the picture is the same. 0 = the node's own range.
	/// </summary>
	private static readonly float[] Stated = new float[Capacity];

	/// <summary>States <paramref name="light"/>'s radius for the shader's ramp. Main thread.</summary>
	public static void SetStatedRange(Light3D light, float range)
	{
		int slot = (int)MathF.Round(light.LightSpecular - SlotBase);
		lock (Gate)
		{
			if (slot >= 0 && slot < Capacity && ReferenceEquals(Slots[slot], light) &&
			    MathF.Abs(Stated[slot] - range) > 1e-3f)
			{
				Stated[slot] = range;
			}
		}
	}

	/// <summary>The radius the shader ramps over for <paramref name="light"/>: stated, else the node's.</summary>
	public static float StatedRangeOf(Light3D light, float nodeRange)
	{
		int slot = (int)MathF.Round(light.LightSpecular - SlotBase);
		lock (Gate)
		{
			return slot >= 0 && slot < Capacity && ReferenceEquals(Slots[slot], light) && Stated[slot] > 0f
				? Stated[slot] : nodeRange;
		}
	}

	/// <summary>
	/// Reads <paramref name="light"/>'s cached world position and effective falloff radius in O(1)
	/// without crossing C# to C++ interop when the slot has already cached its transform.
	/// </summary>
	public static bool TryGetCachedLightSample(Light3D light, out Vector3 position, out float effectiveRange)
	{
		int slot = (int)MathF.Round(light.LightSpecular - SlotBase);
		lock (Gate)
		{
			if (slot >= 0 && slot < Capacity && ReferenceEquals(Slots[slot], light) && HasCachedTransform[slot])
			{
				position = CachedPositions[slot];
				float nodeRange = CachedRanges[slot];
				effectiveRange = Stated[slot] > 0f ? Stated[slot] : nodeRange;
				return true;
			}
		}

		position = Vector3.Zero;
		effectiveRange = 0f;
		return false;
	}

	/// <summary>
	/// Reads <paramref name="light"/>'s stated falloff radius and baked octahedral static shadow
	/// atlas rect (<c>AtlasU, AtlasV, AtlasScale, SliceIndex</c>) in O(1) via its <c>LightSpecular</c> slot.
	/// </summary>
	public static bool TryGetLightShaftData(Light3D light, float nodeRange, out float statedRange, out Vector4 atlasRect)
	{
		int slot = (int)MathF.Round(light.LightSpecular - SlotBase);
		lock (Gate)
		{
			if (slot >= 0 && slot < Capacity && ReferenceEquals(Slots[slot], light))
			{
				statedRange = Stated[slot] > 0f ? Stated[slot] : nodeRange;
				atlasRect = !Refii.Models.Engine_ClassicMaterial.Active ? AtlasRects[slot] : Vector4.Zero;
				return true;
			}
		}

		statedRange = nodeRange;
		atlasRect = Vector4.Zero;
		return false;
	}

	private static ImageTexture? _texture;

	private static int _live;

	private static int _highWaterSlot = -1;

	/// <summary>Registered lights still alive at the last refresh.</summary>
	public static int Live => _live;

	/// <summary>
	/// Binds or clears the active scenario's static shadow map container and resolves each
	/// registered light slot's octahedral atlas rect.
	/// </summary>
	public static void RebindShadowMap(StaticShadowMapContainer? container)
	{
		lock (Gate)
		{
			_boundShadowMap = container;
			for (int slot = 0; slot <= _highWaterSlot; slot++)
			{
				ulong key = Keys[slot];
				AtlasRects[slot] = container is not null && key != 0ul &&
					container.LightRects.TryGetValue(key, out Vector4 rect)
					? rect : Vector4.Zero;
			}
		}
	}

	/// <summary>
	/// Gives <paramref name="light"/> a texel and writes its slot into LightSpecular. False when
	/// the table is full, in which case the light keeps Godot's default and the shader inverts.
	/// </summary>
	public static bool Register(Light3D light, ulong lightKey = 0ul, bool fixedPlacement = false)
	{
		lock (Gate)
		{
			for (int slot = 0; slot < Capacity; slot++)
			{
				if (Slots[slot] is { } held && GodotObject.IsInstanceValid(held))
				{
					continue;
				}

				Slots[slot] = light;
				Stated[slot] = 0f;
				Keys[slot] = lightKey;
				FixedPlacement[slot] = fixedPlacement;
				HasCachedTransform[slot] = false;
				CachedPositions[slot] = Vector3.Zero;
				CachedRanges[slot] = 0f;
				CachedSoftCores[slot] = 0f;
				AtlasRects[slot] = _boundShadowMap is not null && lightKey != 0ul &&
					_boundShadowMap.LightRects.TryGetValue(lightKey, out Vector4 rect)
					? rect : Vector4.Zero;
				if (slot > _highWaterSlot)
				{
					_highWaterSlot = slot;
				}

				light.LightSpecular = slot + SlotBase;
				return true;
			}
		}

		return false;
	}

	/// <summary>Writes every live light's position and range, and uploads the texture if any moved.</summary>
	public static void Refresh()
	{
		if (Refii.Core.Engine_AppRoot.IsTestHost)
		{
			return;
		}

		bool changed = false;
		int live = 0;
		bool packed = Refii.AndroidRuntime.AndroidLightTablePacking.On;
		bool useStaticShadows = !Refii.Models.Engine_ClassicMaterial.Active && !packed;
		int metaGen = Engine_LevelLights.LightMetaGeneration;

		lock (Gate)
		{
			bool metaChanged = metaGen != _lastMetaGeneration;
			_lastMetaGeneration = metaGen;
			int maxActiveSlot = -1;
			for (int slot = 0; slot <= _highWaterSlot; slot++)
			{
				Light3D? light = Slots[slot];
				float x = 0f, y = 0f, z = 0f, range = 0f, softCore = 0f;

				if (light is not null)
				{
					if (!GodotObject.IsInstanceValid(light))
					{
						Slots[slot] = null;
						Stated[slot] = 0f;
						Keys[slot] = 0ul;
						FixedPlacement[slot] = false;
						HasCachedTransform[slot] = false;
						CachedPositions[slot] = Vector3.Zero;
						CachedRanges[slot] = 0f;
						CachedSoftCores[slot] = 0f;
						AtlasRects[slot] = Vector4.Zero;
					}
					else
					{
						maxActiveSlot = slot;
						if (FixedPlacement[slot] && HasCachedTransform[slot] && !metaChanged)
						{
							Vector3 at = CachedPositions[slot];
							x = at.X;
							y = at.Y;
							z = at.Z;
							range = CachedRanges[slot];
							softCore = CachedSoftCores[slot];
							live++;
						}
						else if (light.IsInsideTree())
						{
							Vector3 at = light.GlobalPosition;
							x = at.X;
							y = at.Y;
							z = at.Z;
							range = light is OmniLight3D omni ? omni.OmniRange
								: light is SpotLight3D spot ? spot.SpotRange : 0f;
							softCore = Engine_LevelLights.SurfaceSoftCoreOf(light);
							CachedPositions[slot] = at;
							CachedRanges[slot] = range;
							CachedSoftCores[slot] = softCore;
							HasCachedTransform[slot] = true;
							live++;
						}
					}
				}

				float stated = range > 0f && Stated[slot] > 0f ? Stated[slot] : range;
				Vector4 rect = useStaticShadows && range > 0f ? AtlasRects[slot] : Vector4.Zero;
				float row2SoftCore = range > 0f ? softCore : 0f;
				int second = (Capacity + slot) * 4;
				if (Data[second] != stated ||
				    Data[second + 1] != rect.X ||
				    Data[second + 2] != rect.Y ||
				    Data[second + 3] != rect.Z)
				{
					Data[second] = stated;
					Data[second + 1] = rect.X;
					Data[second + 2] = rect.Y;
					Data[second + 3] = rect.Z;
					changed = true;
				}

				int third = (Capacity * 2 + slot) * 4;
				if (packed) changed |= Refii.AndroidRuntime.AndroidLightTablePacking.Write(Data, third, x, y, z, range, stated);
				else if (Data[third] != rect.W || Data[third + 1] != row2SoftCore)
				{
					Data[third] = rect.W;
					Data[third + 1] = row2SoftCore;
					Data[third + 2] = 0f;
					Data[third + 3] = 0f;
					changed = true;
				}

				int i = slot * 4;
				if (Data[i] != x || Data[i + 1] != y || Data[i + 2] != z || Data[i + 3] != range)
				{
					Data[i] = x;
					Data[i + 1] = y;
					Data[i + 2] = z;
					Data[i + 3] = range;
					changed = true;
				}
			}

			_highWaterSlot = maxActiveSlot;
			_live = live;
			if (!changed && _texture is not null)
			{
				return;
			}

			Buffer.BlockCopy(Data, 0, Bytes, 0, Bytes.Length);
			if (_texture is null)
			{
				var image = Image.CreateFromData(Capacity, 3, false, Image.Format.Rgbaf, Bytes);
				_texture = Refii.Textures.Engine_TextureUpload.CreateTexture(image);
				RenderingServer.GlobalShaderParameterSet(GlobalName, _texture);
			}
			else
			{
				Refii.Textures.Engine_TextureUpload.UpdateFromData(
					_texture, Capacity, 3, false, Image.Format.Rgbaf, Bytes);
			}
		}
	}
}
