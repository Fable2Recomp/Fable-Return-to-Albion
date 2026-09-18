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

	/// <summary>Texels. Bounded by the half-precision slot channel: 2 × (Capacity + SlotBase) ≤ 2048.</summary>
	public const int Capacity = 1000;

	/// <summary>Added to the slot on the way into LightSpecular: 0 and 1 are below any default.</summary>
	public const float SlotBase = 2.0f;

	private static readonly object Gate = new();

	private static readonly Light3D?[] Slots = new Light3D?[Capacity];

	private static readonly float[] Data = new float[Capacity * 4];

	private static readonly byte[] Bytes = new byte[Capacity * 16];

	private static ImageTexture? _texture;

	private static int _live;

	/// <summary>Registered lights still alive at the last refresh.</summary>
	public static int Live => _live;

	/// <summary>
	/// Gives <paramref name="light"/> a texel and writes its slot into LightSpecular. False when
	/// the table is full, in which case the light keeps Godot's default and the shader inverts.
	/// </summary>
	public static bool Register(Light3D light)
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
				light.LightSpecular = slot + SlotBase;
				return true;
			}
		}

		return false;
	}

	/// <summary>Writes every live light's position and range, and uploads the texture if any moved.</summary>
	public static void Refresh()
	{
		bool changed = false;
		int live = 0;

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
			var image = Image.CreateFromData(Capacity, 1, false, Image.Format.Rgbaf, Bytes);
			if (_texture is null)
			{
				_texture = ImageTexture.CreateFromImage(image);
				RenderingServer.GlobalShaderParameterSet(GlobalName, _texture);
			}
			else
			{
				_texture.Update(image);
			}
		}
	}
}
