using Godot;

namespace Refii.Lighting;

/// <summary>
/// A mod's SPOT light (<see cref="Engine_ModLights"/>, a <c>light</c> with a cone): the spot form
/// of the recipe glow <see cref="Refii.Fx.Engine_FxRecipeLight"/> is — zero decay, no shadow map,
/// its energy wavering by <see cref="Flicker"/>. It shines along its own −Z, which
/// <see cref="Engine_ModLights.SpotLocal"/> turns onto the direction the call stated.
/// </summary>
public partial class Engine_ModSpotLight : SpotLight3D
{
	public float BaseEnergy { get; set; } = 1f;

	public float Flicker { get; set; }

	private float _phase;

	public override void _Process(double delta)
	{
		if (Flicker <= 0f)
		{
			return;
		}

		_phase += (float)delta;
		float wave = (Mathf.Sin(_phase * 23f) * 0.6f) + (Mathf.Sin(_phase * 7.3f) * 0.4f);
		LightEnergy = BaseEnergy * (1f + (Flicker * wave));
	}
}
