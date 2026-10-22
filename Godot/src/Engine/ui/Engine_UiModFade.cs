using System;
using Godot;

namespace Refii.Ui;

/// <summary>
/// A MOD'S SCREEN FADE, per player (<c>fade</c> on every surface: <c>refii_mod_fade</c>,
/// <c>Refii.fade</c>, <c>RefiiHost.fade</c>): a black layer over the player's screen — their half
/// on split screen, the whole window alone or online — ramped to or from black over the seconds
/// asked. Drawn by <see cref="Engine_UiScreenFade"/> beside the game's own fade, which it never
/// touches: the guest's fade object at <c>*(GUI+64)</c> is read-only here because level changes
/// and save loads block on it (see <see cref="Engine_UiScreenFade"/>), so a mod's fade is the
/// host's own state, and a level change the game makes under it still completes.
/// </summary>
/// <remarks>Pure state and arithmetic (no Godot node): held by tests. One fade per player; a new
/// request restarts the ramp from the alpha standing.</remarks>
public static class Engine_UiModFade
{
	/// <summary>One player's fade: the alpha standing, where it is going and how fast.</summary>
	public sealed class Fade
	{
		public float Alpha;
		public float Target;

		/// <summary>Alpha per second; 0 snaps.</summary>
		public float Rate;

		public float Seconds;
	}

	private static readonly Fade[] _fades = { new(), new() };

	/// <summary>
	/// Fades <paramref name="player"/>'s screen (1 or 2; 0 both) to black or back over
	/// <paramref name="seconds"/> (0 or less: at once). True when a player was addressed.
	/// </summary>
	public static bool Request(int player, bool toBlack, float seconds)
	{
		if (player is not (0 or 1 or 2))
		{
			return false;
		}

		float length = float.IsFinite(seconds) && seconds > 0f ? Math.Min(seconds, 600f) : 0f;
		for (int p = 1; p <= 2; p++)
		{
			if (player != 0 && player != p)
			{
				continue;
			}

			Fade fade = _fades[p - 1];
			fade.Target = toBlack ? 1f : 0f;
			fade.Seconds = length;
			float distance = Math.Abs(fade.Target - fade.Alpha);
			fade.Rate = length > 0f && distance > 0.0001f ? distance / length : 0f;
			if (fade.Rate == 0f)
			{
				fade.Alpha = fade.Target;
			}
		}

		return true;
	}

	/// <summary>One frame: every ramp moves toward its target.</summary>
	public static void Advance(double delta)
	{
		float step = (float)Math.Max(0, delta);
		foreach (Fade fade in _fades)
		{
			if (fade.Alpha == fade.Target)
			{
				continue;
			}

			float move = fade.Rate > 0f ? fade.Rate * step : float.PositiveInfinity;
			fade.Alpha = fade.Target > fade.Alpha
				? Math.Min(fade.Target, fade.Alpha + move)
				: Math.Max(fade.Target, fade.Alpha - move);
		}
	}

	/// <summary>The black standing over <paramref name="player"/>'s screen, 0..1.</summary>
	public static float AlphaFor(int player) => player is 1 or 2 ? _fades[player - 1].Alpha : 0f;

	/// <summary>The player's fade state, for <c>RefiiHost.fade_state</c>: alpha, target, seconds of the last request.</summary>
	public static (float Alpha, float Target, float Seconds) Describe(int player) =>
		player is 1 or 2 ? (_fades[player - 1].Alpha, _fades[player - 1].Target, _fades[player - 1].Seconds) : (0f, 0f, 0f);

	/// <summary>Whether any player's fade covers anything.</summary>
	public static bool Covers => _fades[0].Alpha > 0.002f || _fades[1].Alpha > 0.002f;

	/// <summary>Everything clear (the session ends).</summary>
	public static void Clear()
	{
		foreach (Fade fade in _fades)
		{
			fade.Alpha = 0f;
			fade.Target = 0f;
			fade.Rate = 0f;
			fade.Seconds = 0f;
		}
	}

	/// <summary>
	/// The direction a call names: "to_black", "out", "black" and true fade to black; "from_black",
	/// "in", "clear" and false fade back. Null for a word that is neither.
	/// </summary>
	public static bool? ToBlack(string direction) => direction.Trim().ToLowerInvariant() switch
	{
		"to_black" or "out" or "black" or "to black" or "1" or "true" => true,
		"from_black" or "in" or "clear" or "from black" or "0" or "false" => false,
		_ => null,
	};
}
