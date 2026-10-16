#if TOOLS
using Godot;

namespace Refii.Assets.UI;

/// <summary>
/// Dedicated floating editor window for the Fable 2 Mod Studio.
/// Launches centered on screen by default, providing a spacious, non-modal interface
/// for managing active mods, traversing overrides, and editing assets.
/// </summary>
public partial class ModdingHubWindow : Window
{
    private ModdingHubPanel? _panel;

    public ModdingHubWindow()
    {
        Title = "Fable 2 Mod Studio";
        Size = new Vector2I(960, 680);
        MinSize = new Vector2I(760, 500);
        Exclusive = false;
        Unresizable = false;
        Visible = false;

        CloseRequested += OnCloseRequested;

        _panel = new ModdingHubPanel();
        AddChild(_panel);
    }

    /// <summary>
    /// Displays the Mod Studio window centered over the Godot editor.
    /// </summary>
    public void Open()
    {
        _panel?.RefreshModList();
        PopupCentered();
    }

    /// <summary>
    /// Refreshes all active mod contents and metrics.
    /// </summary>
    public void Refresh()
    {
        _panel?.RefreshModList();
    }

    private void OnCloseRequested()
    {
        Hide();
    }
}
#endif
