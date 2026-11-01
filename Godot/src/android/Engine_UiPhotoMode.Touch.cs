namespace Refii.Ui;

public partial class Engine_UiPhotoMode
{
    /// <summary>The Controls dialog's lines for the touch controls (<see cref="RefreshDevice"/>).</summary>
    private static string[] TouchHelp() =>
        new[] { "Left thumb  Move     Drag  Look", "Raise / Lower  Up and down",
            "D-pad  Choose and adjust a setting", "Take photo  Saves to Pictures / Refii",
            "Hide UI  A clear view; tap to bring it back", "Reset  Back to where the camera was",
            "Back  Exit Photo Mode" };
}
