using Godot;

namespace Refii.Launcher.Core;

public readonly struct LauncherLayout
{
	public Vector2 InfoPos { get; init; }
	public Vector2 InfoSize { get; init; }
	public Vector2 SettingsPos { get; init; }
	public Vector2 SettingsSize { get; init; }
	public Vector2 WarningPos { get; init; }
	public Vector2 WarningSize { get; init; }
	public Vector2 ActionPos { get; init; }
	public Vector2 ActionSize { get; init; }

	// Design-base geometry (multiplied by LauncherScale.Unit in Build).
	public const float BasePrimaryButtonHeight = 52.0f;
	public const float BasePanelPadding = 24.0f;
	public const float BasePanelTopClearance = 56.0f;

	private static float ClampFloat(float value, float minimum, float maximum) =>
		Mathf.Max(minimum, Mathf.Min(value, maximum));

	/// <summary>
	/// Floating overlay layout:
	/// - InfoDrawer (News / Changelog / Credits): compact card sitting above the warning pill/bottom area on the LEFT (over "Art by Mirai-Miki" location).
	/// - MainDrawer (Debug & System: Settings, Modding, Reinstall): full-height drawer on the RIGHT (default open).
	/// - Top Left: Wordmark on top row, File & About on sub-row directly under it.
	/// </summary>
	public static LauncherLayout Build(Vector2 viewportPos, Vector2 viewportSize, float menuBarHeight)
	{
		float viewportWidth = Mathf.Max(1.0f, viewportSize.X);
		float viewportHeight = Mathf.Max(1.0f, viewportSize.Y);

		float pad = LauncherScale.Px(BasePanelPadding);
		float topClear = LauncherScale.Px(BasePanelTopClearance);
		float buttonHeight = LauncherScale.Px(BasePrimaryButtonHeight);
		float warningHeight = LauncherScale.Px(32.0f);

		// Main full-height drawer on the RIGHT (Settings, Modding, Reinstall):
		float mainWidth = ClampFloat(
			viewportWidth * 0.40f, LauncherScale.Px(460.0f), LauncherScale.Px(580.0f));

		var mainPos = new Vector2(
			viewportPos.X + viewportWidth - mainWidth + pad,
			viewportPos.Y + topClear);
		var mainSize = new Vector2(
			mainWidth - (pad * 2.0f),
			viewportHeight - topClear - pad);

		// Floating primary action: bottom-left.
		var actionSize = new Vector2(LauncherScale.Px(280.0f), buttonHeight);
		var actionPos = new Vector2(
			viewportPos.X + pad,
			viewportPos.Y + viewportHeight - pad - buttonHeight);

		// Quiet notice pill floats above the action.
		float leftAvailableWidth = Mathf.Max(1.0f, viewportWidth - mainWidth);
		var warningSize = new Vector2(
			Mathf.Min(LauncherScale.Px(420.0f), leftAvailableWidth - (pad * 2.0f)), warningHeight);
		var warningPos = new Vector2(
			viewportPos.X + pad,
			actionPos.Y - warningHeight - LauncherScale.Px(14.0f));

		// Info / News card on the LEFT (Changelog & Credits):
		// Compact card anchored just above the warning pill (clearing the art
		// credit line), so the middle of the artwork stays visible.
		float infoWidth = ClampFloat(
			viewportWidth * 0.38f, LauncherScale.Px(400.0f), LauncherScale.Px(500.0f));
		float infoBottomLimit = warningPos.Y - LauncherScale.Px(40.0f);
		float infoHeight = Mathf.Clamp(
			LauncherScale.Px(280.0f), LauncherScale.Px(200.0f),
			infoBottomLimit - LauncherScale.Px(56.0f));

		var infoPos = new Vector2(
			viewportPos.X + pad,
			infoBottomLimit - infoHeight);
		var infoSize = new Vector2(
			infoWidth - (pad * 2.0f),
			infoHeight);

		return new LauncherLayout
		{
			InfoPos = infoPos,
			InfoSize = infoSize,
			SettingsPos = mainPos,
			SettingsSize = mainSize,
			WarningPos = warningPos,
			WarningSize = warningSize,
			ActionPos = actionPos,
			ActionSize = actionSize,
		};
	}
}
