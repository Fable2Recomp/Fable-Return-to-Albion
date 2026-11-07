using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The attribute ids a host-side check reads off a presentation's elements
/// (<c>RefiiHostGetUiAttributes</c>). Ids are sdbm hashes of the attribute name masked to
/// <see cref="Engine_GameHash.AttributeIdMask"/>.
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

	/// <summary>The opacity column within <see cref="OpacityIds"/>.</summary>
	public const int OpacitySlot = 0;
}
