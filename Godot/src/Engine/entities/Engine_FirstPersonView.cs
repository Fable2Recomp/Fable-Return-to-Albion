namespace Refii.Entities;

/// <summary>
/// Whose head the camera is in: a hero is drawn without its head exactly while the camera mode the
/// game states for that hero's player is first person (<see cref="Engine_FirstPersonHead"/>).
/// </summary>
/// <remarks>
/// The mode is the game's ECameraMode as the core camera chunk states it after each
/// <c>CameraManager:FrameUpdate(hero)</c> (camera_changed, <c>RefiiHost.camera_mode</c>): 8 for the
/// game's own first-person look and for a first-person camera override alike (a mod's
/// <c>camera_override</c> hold, a quest's first-person shot). A hero's player is its key in the
/// guest's player map — the keys the camera managers are found by — so both players are served.
/// </remarks>
public static class Engine_FirstPersonView
{
	/// <summary>ECameraMode CAMERA_FIRST_PERSON (the catalog's <c>CAMERA_FIRST_PERSON</c>).</summary>
	public const int FirstPersonMode = 8;

	/// <summary>The player (1 or 2) whose hero is at <paramref name="address"/>, by the player map's two keys; 0 for any other entity.</summary>
	public static int PlayerOf(uint address, uint playerOne, uint playerTwo)
	{
		if (address == 0u)
		{
			return 0;
		}

		return address == playerOne ? 1 : address == playerTwo ? 2 : 0;
	}

	/// <summary>Whether <paramref name="player"/>'s hero is drawn without its head under the stated <paramref name="cameraMode"/>.</summary>
	public static bool HeadHidden(int player, int cameraMode) => player is 1 or 2 && cameraMode == FirstPersonMode;

	/// <summary>The split view that draws <paramref name="player"/>'s camera: view 0 is player one's, view 1 player two's (GameSplitView).</summary>
	public static int OwnView(int player) => player == 2 ? 1 : 0;
}
