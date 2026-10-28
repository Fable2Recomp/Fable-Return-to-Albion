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

	/// <summary>Per-slot octahedral shadow atlas rect (AtlasU, AtlasV, AtlasScale).</summary>
	private static readonly Vector3[] AtlasRects = new Vector3[Capacity];

	private static StaticShadowMapContainer? _boundShadowMap;

	/// <summary>Two rows of Capacity texels: (x, y, z, Godot's range), then (the STATED range, atlasU, atlasV, atlasScale).</summary>
	private static readonly float[] Data = new float[Capacity * 4 * 2];

	private static readonly byte[] Bytes = new byte[Capacity * 16 * 2];

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
			if (slot >= 0 && slot < Capacity && ReferenceEquals(Slots[slot], light))
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

	private static ImageTexture? _texture;

	private static int _live;

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
			for (int slot = 0; slot < Capacity; slot++)
			{
				ulong key = Keys[slot];
				AtlasRects[slot] = container is not null && key != 0ul &&
					container.LightRects.TryGetValue(key, out Vector3 rect)
					? rect : Vector3.Zero;
			}
		}
	}

	/// <summary>
	/// Gives <paramref name="light"/> a texel and writes its slot into LightSpecular. False when
	/// the table is full, in which case the light keeps Godot's default and the shader inverts.
	/// </summary>
	public static bool Register(Light3D light, ulong lightKey = 0ul)
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
				AtlasRects[slot] = _boundShadowMap is not null && lightKey != 0ul &&
					_boundShadowMap.LightRects.TryGetValue(lightKey, out Vector3 rect)
					? rect : Vector3.Zero;
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
		bool useStaticShadows = !Refii.Models.Engine_ClassicMaterial.Active;

		lock (Gate)
		{
			for (int slot = 0; slot < Capacity; slot++)
			{
				Light3D? light = Slots[slot];
				float x = 0f, y = 0f, z = 0f, range = 0f;

				if (light is not null)
				{
					if (!GodotObject.IsInstanceValid(light))
					{
						Slots[slot] = null;
						Stated[slot] = 0f;
						Keys[slot] = 0ul;
						AtlasRects[slot] = Vector3.Zero;
					}
					else if (light.IsInsideTree())
					{
						Vector3 at = light.GlobalPosition;
						x = at.X;
						y = at.Y;
						z = at.Z;
						range = light is OmniLight3D omni ? omni.OmniRange
							: light is SpotLight3D spot ? spot.SpotRange : 0f;
						live++;
					}
				}

				float stated = range > 0f && Stated[slot] > 0f ? Stated[slot] : range;
				Vector3 rect = useStaticShadows && range > 0f ? AtlasRects[slot] : Vector3.Zero;
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

			_live = live;
			if (!changed && _texture is not null)
			{
				return;
			}

			Buffer.BlockCopy(Data, 0, Bytes, 0, Bytes.Length);
			using var image = Image.CreateFromData(Capacity, 2, false, Image.Format.Rgbaf, Bytes);
			if (_texture is null)
			{
				_texture = Refii.Textures.Engine_AndroidTextureUpload.CreateTexture(image);
				RenderingServer.GlobalShaderParameterSet(GlobalName, _texture);
			}
			else
			{
				_texture.Update(image);
			}
		}
	}
}
