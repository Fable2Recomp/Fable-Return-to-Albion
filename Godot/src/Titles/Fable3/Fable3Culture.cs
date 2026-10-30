namespace Refii.Titles.Fable3;

using Godot;
using Refii.Host;

/// <summary>
/// Follows the culture the guest runs in, so every string and glyph Godot draws is the guest's
/// language. Read on a half-second clock, as Fable 2's UI does: a switch is rare and the guest
/// reloads behind its own screen anyway.
/// </summary>
public partial class Fable3Culture : Node
{
	private const double Period = 0.5;

	private double _since = Period;

	public override void _Process(double delta)
	{
		_since += delta;
		if (_since < Period)
		{
			return;
		}

		_since = 0;
		if (Refii.Text.Engine_GameCulture.Set(HostBridge.GetCurrentCulture()))
		{
			GD.Print($"[fable3-culture] the guest runs in {Refii.Text.Engine_GameCulture.Current}");
		}
	}
}
