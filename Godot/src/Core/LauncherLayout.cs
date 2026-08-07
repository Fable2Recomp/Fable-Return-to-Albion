using Godot;

namespace Refii.Launcher.Core;

public readonly struct LauncherLayout
{
    public Vector2 InfoPos { get; init; }
    public Vector2 InfoSize { get; init; }
    public Vector2 WarningPos { get; init; }
    public Vector2 WarningSize { get; init; }
    public Vector2 ActionPos { get; init; }
    public Vector2 ActionSize { get; init; }

    public const float PrimaryButtonHeight = 88.0f;
    public const float BottomPadding = 10.0f;

    public static LauncherLayout Build(Vector2 viewportPos, Vector2 viewportSize, float menuBarHeight)
    {
        float contentTop = viewportPos.Y + Mathf.Max(menuBarHeight, 0.0f);
        float contentHeight = Mathf.Max(1.0f, viewportSize.Y - menuBarHeight);

        float infoWidth = Mathf.Clamp(viewportSize.X * 0.265f, 440.0f, 560.0f);
        if (viewportSize.X - infoWidth < 640.0f)
        {
            infoWidth = Mathf.Max(320.0f, viewportSize.X - 640.0f);
        }

        if (viewportSize.X < 900.0f)
        {
            infoWidth = Mathf.Clamp(viewportSize.X * 0.42f, 300.0f, viewportSize.X * 0.5f);
        }

        float leftWidth = Mathf.Max(1.0f, viewportSize.X - infoWidth);
        float warningHeight = viewportSize.Y < 720.0f ? 36.0f : 44.0f;
        float actionHeight = PrimaryButtonHeight + BottomPadding * 2.0f;
        float bottomPanelHeight = warningHeight + actionHeight;
        float bottomTop = contentTop + Mathf.Max(0.0f, contentHeight - bottomPanelHeight);

        return new LauncherLayout
        {
            InfoPos = new Vector2(viewportPos.X + leftWidth, contentTop),
            InfoSize = new Vector2(infoWidth, contentHeight),
            WarningPos = new Vector2(viewportPos.X, bottomTop),
            WarningSize = new Vector2(leftWidth, warningHeight),
            ActionPos = new Vector2(viewportPos.X, bottomTop + warningHeight),
            ActionSize = new Vector2(leftWidth, actionHeight),
        };
    }
}
