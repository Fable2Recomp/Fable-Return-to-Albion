using Godot;
using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The attribute channel: which per-element attributes the host reads live, and the reads.
/// An element's authored attributes are only starting values — slides and the shipped Lua
/// write them — so anything drawn from a live presentation reads this as well as the file.
/// Ids are sdbm hashes of the attribute name masked to <see cref="Engine_GameHash.AttributeIdMask"/>.
/// </summary>
public static class Engine_UiLiveAttributes
{
	/// <summary>
	/// The opacity pass: alpha plus the tint that rides with it. Weight and colour are read in
	/// the same walk — `rgb -= (1 - tint) * amount` needs both, and
	/// `SetTintingAmountAndDiffuseColour` writes them together.
	/// </summary>
	public static readonly uint[] OpacityIds =
	{
		Engine_GameHash.Sdbm("opacity") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("TintingAmount") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("diffuse.r") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("diffuse.g") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("diffuse.b") & Engine_GameHash.AttributeIdMask,
	};

	/// <summary>Indices within <see cref="OpacityIds"/>.</summary>
	public const int OpacitySlot = 0;

	public const int TintingAmountSlot = 1;

	public const int DiffuseRSlot = 2;

	public const int DiffuseGSlot = 3;

	public const int DiffuseBSlot = 4;

	/// <summary>The guest stores diffuse attributes 0..255 and divides by this on the way in
	/// (at 0x8286EE18); the weight is stored verbatim.</summary>
	public const float DiffuseScale = 1f / 255f;

	/// <summary>
	/// The attributes that decide WHERE an element is, in the order
	/// <see cref="Engine_UiPresentationHost.LiveTransform"/> expects them.
	/// </summary>
	public static readonly uint[] TransformIds =
	{
		Engine_GameHash.Sdbm("position.x") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("position.y") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("position.z") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("scale.x") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("scale.y") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("scale.z") & Engine_GameHash.AttributeIdMask,
	};

	/// <summary>Indices within <see cref="TransformIds"/>.</summary>
	public const int ScaleXSlot = 3;

	public const int ScaleYSlot = 4;

	/// <summary>
	/// The spin channel: `rotation.x/y/z` in RADIANS, written onto a preview anchor every frame
	/// by `sub_824254C8`. Separate from <see cref="TransformIds"/> so the general live-transform
	/// read does not carry three more columns for one element per screen.
	/// </summary>
	public static readonly uint[] RotationIds =
	{
		Engine_GameHash.Sdbm("rotation.x") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("rotation.y") & Engine_GameHash.AttributeIdMask,
		Engine_GameHash.Sdbm("rotation.z") & Engine_GameHash.AttributeIdMask,
	};

	/// <summary>
	/// Reads the guest's live transform for one presentation, as
	/// <see cref="Engine_UiPresentationHost.LiveTransform"/> wants it. A row is null when the
	/// guest states none of the six, leaving the file's transform alone.
	/// </summary>
	public static float?[]?[]? Transform(uint presentation, int elementCount)
	{
		Engine_UiModel.LiveAttribute[,]? attributes =
			Engine_UiElementState.Current.Attributes(TransformIds, presentation);
		if (attributes is null)
		{
			return null;
		}

		int rows = System.Math.Min(attributes.GetLength(0), elementCount);
		var transform = new float?[]?[rows];
		for (int i = 0; i < rows; ++i)
		{
			float?[]? values = null;
			for (int a = 0; a < TransformIds.Length; ++a)
			{
				if (!attributes[i, a].Present)
				{
					continue;
				}

				values ??= new float?[TransformIds.Length];
				values[a] = attributes[i, a].AsFloat;
			}

			transform[i] = values;
		}

		return transform;
	}

	/// <summary>One element's stated rotation, or null when the guest states none of the three.</summary>
	public static Vector3? Rotation(uint presentation, int ordinal)
	{
		Engine_UiModel.LiveAttribute[,]? attributes =
			Engine_UiElementState.Current.Attributes(RotationIds, presentation);
		if (attributes is null || ordinal < 0 || ordinal >= attributes.GetLength(0))
		{
			return null;
		}

		if (!attributes[ordinal, 0].Present && !attributes[ordinal, 1].Present &&
			!attributes[ordinal, 2].Present)
		{
			return null;
		}

		var stated = new Vector3(
			attributes[ordinal, 0].Present ? attributes[ordinal, 0].AsFloat : 0f,
			attributes[ordinal, 1].Present ? attributes[ordinal, 1].AsFloat : 0f,
			attributes[ordinal, 2].Present ? attributes[ordinal, 2].AsFloat : 0f);
		return stated.IsFinite() ? stated : null;
	}
}
