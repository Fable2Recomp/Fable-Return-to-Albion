namespace Refii.AndroidRuntime;

/// <summary>A title UI that can build its main menu ahead, in the frame the host chooses.</summary>
public interface IAndroidMenuPrebuild
{
    /// <summary>Builds as much of the main menu as it can now; the title screen finishes whatever is left.</summary>
    void PrebuildMenuNow();
}
