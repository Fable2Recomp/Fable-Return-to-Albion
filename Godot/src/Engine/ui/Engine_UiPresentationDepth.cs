namespace Refii.Ui;

/// <summary>
/// Host-side depth stacking of presentations; the guest does not state depth. Owned surfaces are
/// placed relative to the slab they stand over.
/// </summary>
public static class Engine_UiPresentationDepth
{
	/// <summary>
	/// Canvas units between one presentation's slab and the next; deeper than any screen
	/// (176 units, `frontendmainmenu`) and inside the canvas camera's far plane.
	/// </summary>
	public const float Step = 1000f;

	/// <summary>An owned book, in front of the slab it replaces.</summary>
	public const float BookOffset = Step / 4f;

	/// <summary>An owned overlay — a panel or a list — in front of its book.</summary>
	public const float OverlayOffset = Step / 2f;
}
