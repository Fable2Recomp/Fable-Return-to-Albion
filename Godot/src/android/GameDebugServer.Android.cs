using System.Collections.Generic;

namespace Refii.Game;

/// <summary>The debug channel's handheld readouts.</summary>
public partial class GameDebugServer
{
	/// <summary>`rumble`'s pad 0 on the phone's own vibrator, played by touch on Android.</summary>
	private static Dictionary<string, object?> HandheldRumble()
	{
		(bool running, float amplitude) = GameInput.HandheldRumbleReadout();
		return new Dictionary<string, object?>
		{
			["running"] = running,
			["amplitude"] = amplitude,
		};
	}
}
