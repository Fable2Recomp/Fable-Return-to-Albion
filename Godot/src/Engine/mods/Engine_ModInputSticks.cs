using System;

namespace Refii.Mods;

/// <summary>
/// EACH PLAYER'S MOVE STICK, as the pads gathered it for the guest (<c>RefiiHost.move_stick</c>): the left
/// stick of the pad the title says carries that player (<c>GameTitleInput.ModPlayer</c>) — for player 1 the
/// keyboard's movement keys, the touch controls' stick and the joypads pad 0 merges, for player 2 their own
/// joypad — the same split the mods' input actions and claims use. Set every frame the pad is published,
/// before any mod withholds a button; a player whose pad was not published this frame or the last reads
/// zero, so a pad that left never leaves a stick pushed. x right and y up, each −1..1 (the Xbox frame).
/// </summary>
public static class ModInputSticks
{
	private static readonly (float X, float Y, ulong Frame, bool Set)[] Sticks = new (float, float, ulong, bool)[2];

	/// <summary>States <paramref name="player"/>'s (1 or 2) left stick in the guest's units (±32767) for <paramref name="frame"/>.</summary>
	public static void Set(int player, int leftX, int leftY, ulong frame)
	{
		if (player is not (1 or 2))
		{
			return;
		}

		Sticks[player - 1] = (Math.Clamp(leftX / 32767f, -1f, 1f), Math.Clamp(leftY / 32767f, -1f, 1f), frame, true);
	}

	/// <summary><paramref name="player"/>'s (1 or 2) move stick at <paramref name="frame"/>: zero for another player or a stale one.</summary>
	public static (float X, float Y) Move(int player, ulong frame)
	{
		if (player is not (1 or 2))
		{
			return (0f, 0f);
		}

		(float x, float y, ulong at, bool set) = Sticks[player - 1];
		if (!set || frame > at + 1)
		{
			return (0f, 0f);
		}

		float length = MathF.Sqrt(x * x + y * y);
		return length > 1f ? (x / length, y / length) : (x, y);
	}

	/// <summary>Forgets both sticks (tests; a session's end).</summary>
	public static void Clear() => Array.Clear(Sticks);
}
