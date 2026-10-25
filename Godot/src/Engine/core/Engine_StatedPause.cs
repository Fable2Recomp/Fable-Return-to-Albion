namespace Refii.Core;

/// <summary>
/// The guest's statement that its world is paused, latched once per frame for everything Godot
/// draws on its own clock. While latched the drawn world holds: the shared pair does not swap
/// (<see cref="Refii.Entities.Engine_DrawnFrame.NoteRestated"/>), every playhead advances by
/// zero (<see cref="Engine_DrawAdvance"/>), and every `_Process` consumer's world delta is 0.
/// </summary>
/// <remarks>
/// THE STATEMENT, NOT THE SYMPTOM. The guest's tick counter is not a pause signal: on an
/// ordinary pause it starves and the world coasts to a stop over a second; under a paused
/// cutscene it keeps counting, so pairs swapped onto unchanged poses and every rig played its
/// last rate and snapped a quarter second later. The statement is the in-game GUI's active
/// screen (`RefiiHostGetPauseState`): a real screen up means the GameFlow GUI mode is in force.
/// `GameSession.Paused` is NOT touched — the guest's steps keep running the pause book's GUI tick.
/// </remarks>
public static class Engine_StatedPause
{
	/// <summary>Whether the guest states a pause this frame.</summary>
	public static bool Latched { get; private set; }

	/// <summary>Frames the latch has held in the current pause; 0 when not latched.</summary>
	public static long HeldFrames { get; private set; }

	/// <summary>Frames held over the run; for the debug channel.</summary>
	public static long HeldFramesTotal { get; private set; }

	/// <summary>Pauses latched over the run; for the debug channel.</summary>
	public static long Pauses { get; private set; }

	/// <summary>Once per frame, before anything swaps or draws: takes the guest's statement.</summary>
	public static void BeginFrame(bool stated)
	{
		if (stated)
		{
			if (!Latched)
			{
				Latched = true;
				HeldFrames = 0;
				++Pauses;
			}

			++HeldFrames;
			++HeldFramesTotal;
			return;
		}

		Latched = false;
		HeldFrames = 0;
	}

	/// <summary>
	/// The in-game GUI's screen read as a pause statement: a screen is up. 31 is the id
	/// `ExitScreen` leaves when nothing is up; `uint.MaxValue` is the bridge's "no screen manager".
	/// </summary>
	public static bool StatesPause(uint screenId) => screenId != 31u && screenId != uint.MaxValue;
}
