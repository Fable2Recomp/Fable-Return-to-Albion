namespace Refii.Ui;

/// <summary>The decoration panel's words under the touch controls (GameDecorate.Touch.cs).</summary>
public partial class Engine_UiDecorate
{
    /// <summary>The room's prompt with nothing aimed at.</summary>
    private const string TouchHint = "Tap an object to pick it up";

    /// <summary>The controls help, for the buttons drawn on the screen.</summary>
    private const string TouchHelp =
        "Tap an object   Pick it up\nTap the room   Put it down there\nDrag the object   Slide it\nPlace   Put it down where it is\n" +
        "Left side   Move camera\nDrag   Look around\nRaise / Lower   Camera up / down\nTurn left / right   Rotate\n" +
        "Item up / down   Item height\nTap X / Y / Z   Then Turn: 15 degrees\nCancel   Put it back\nBack gesture   Back, or exit";
}
