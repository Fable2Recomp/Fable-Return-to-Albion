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
	public const float BaseDualDockHeight = 114.0f;
	public const float BaseDualDockPipelineHeight = 166.0f;
	public const float BasePanelPadding = 24.0f;
	public const float BaseInfoHeight = 440.0f;
	public const float BasePanelTopClearance = 56.0f;

	private static float ClampFloat(float value, float minimum, float maximum) =>
		Mathf.Max(minimum, Mathf.Min(value, maximum));

	/// <summary>
	/// Floating overlay layout:
	/// - InfoDrawer (News / Changelog / Credits): compact card sitting above the warning pill/bottom area on the LEFT.
	/// - MainDrawer (Settings, Modding, Install, Saves): full-height drawer on the RIGHT (default open).
	/// - Bottom Left: either the single primary button (mobile) or the side-by-side Dual-Game Launch Dock (desktop).
	/// </summary>
	public static LauncherLayout Build(
		Vector2 viewportPos,
		Vector2 viewportSize,
		float menuBarHeight,
		bool dualDock = false,
		bool pipelineStripVisible = false)
	{
		float viewportWidth = Mathf.Max(1.0f, viewportSize.X);
		float viewportHeight = Mathf.Max(1.0f, viewportSize.Y);

		float pad = LauncherScale.Px(BasePanelPadding);
		float topClear = LauncherScale.Px(BasePanelTopClearance);
		float warningHeight = LauncherScale.Px(32.0f);

		// Main full-height drawer on the RIGHT (Settings, Modding, Install, Saves):
		float mainWidth = ClampFloat(
			viewportWidth * 0.44f, LauncherScale.Px(540.0f), LauncherScale.Px(680.0f));

		var mainPos = new Vector2(
			viewportPos.X + viewportWidth - mainWidth + pad,
			viewportPos.Y + topClear);
		var mainSize = new Vector2(
			mainWidth - (pad * 2.0f),
			viewportHeight - topClear - pad);

		float leftAvailableWidth = Mathf.Max(1.0f, viewportWidth - mainWidth);

		Vector2 actionSize;
		if (dualDock)
		{
			float dockHeight = LauncherScale.Px(
				pipelineStripVisible ? BaseDualDockPipelineHeight : BaseDualDockHeight);
			float dockWidth = ClampFloat(
				leftAvailableWidth - (pad * 2.0f),
				LauncherScale.Px(400.0f),
				LauncherScale.Px(548.0f));
			actionSize = new Vector2(dockWidth, dockHeight);
		}
		else
		{
			float buttonHeight = LauncherScale.Px(BasePrimaryButtonHeight);
			actionSize = new Vector2(LauncherScale.Px(280.0f), buttonHeight);
		}

		var actionPos = new Vector2(
			viewportPos.X + pad,
			viewportPos.Y + viewportHeight - pad - actionSize.Y);

		// Quiet notice pill floats above the action.
		float maxWarningWidth = dualDock ? actionSize.X : LauncherScale.Px(420.0f);
		var warningSize = new Vector2(
			Mathf.Min(maxWarningWidth, leftAvailableWidth - (pad * 2.0f)), warningHeight);
		var warningPos = new Vector2(
			viewportPos.X + pad,
			actionPos.Y - warningHeight - LauncherScale.Px(12.0f));

		// Info / News card on the LEFT (Changelog, Credits, About):
		// A card anchored just above the warning pill (clearing the art credit line).
		float infoWidth = ClampFloat(
			viewportWidth * 0.38f, LauncherScale.Px(400.0f), LauncherScale.Px(500.0f));
		float infoBottomLimit = warningPos.Y - LauncherScale.Px(36.0f);
		float headerFloor = viewportPos.Y + menuBarHeight + LauncherScale.Px(24.0f);
		float maxInfoHeight = Mathf.Max(LauncherScale.Px(150.0f), infoBottomLimit - headerFloor);
		// Tall enough for four commits or eight contributors without scrolling, where the
		// window has the room.
		float infoHeight = Mathf.Clamp(
			LauncherScale.Px(BaseInfoHeight),
			Mathf.Min(LauncherScale.Px(180.0f), maxInfoHeight),
			maxInfoHeight);

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
