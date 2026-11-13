using System;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_interior_no_sun`: in a level whose theme states no main light (a cave, a crypt: the
/// frame's <c>HasSky</c> false, which is what <c>Engine_LightingFrame.CurrentInterior</c> means
/// under the modern model), the sun/moon <see cref="DirectionalLight3D"/> is hidden instead of
/// left standing at no energy. Off by default. Android only.
/// </summary>
/// <remarks>
/// WHY. Godot's Mobile renderer runs every material's light() once per directional light on
/// every pixel, whatever its energy, and the directional count is a specialisation constant
/// of the pipeline: a level with no sun still paid for one.
/// <para>WHAT IT HIDES. A frame without a sky has a main light whose brightest channel is at most
/// 0.01 (<c>|colour|^2 &lt;= 1e-4</c>, Engine_LightingFrame), the dynamic sky never overrides the
/// sun there (Engine_DynamicSkyLighting.ConfigureSun needs a sky) and the sun casts no shadow
/// (SunShadow needs energy above 0.01), so the light it hides adds at most about 1% of a
/// channel, times <c>sun_light_scale</c>. Town building interiors are NOT this: they sit in an
/// outdoor level with a sky, where the sun's cull mask already leaves out the indoor layers and
/// the street seen through a door or window still takes the sun; this leaves them alone.</para>
/// <para>THE COST OF THE SWITCH. Godot compiles a pipeline per directional count, so a level
/// drawn without the sun compiles its no-sun pipelines once (under the loading screen when the
/// sun is hidden from the level's build, as here, since <c>BuildSun</c> runs the update).</para>
/// Live: a <c>cvar_set</c> of the key applies at the sun's next update.
/// </remarks>
public static class AndroidInteriorSun
{
	public const string Key = "android_interior_no_sun";

	private static bool? _enabled;
	private static bool _hooked;
	private static ulong _hidden;

	/// <summary>Whether the key is on, read once and again on each live change; always false off a phone.</summary>
	public static bool Enabled
	{
		get
		{
			if (_enabled is { } known) return known;
			bool on = false;
			try
			{
				on = Refii.Core.Engine_Platform.IsAndroid && Refii.Core.Engine_RefiiConfig.Bool(
					Refii.Core.Engine_RefiiConfig.Read(out _), Key, false);
			}
			catch (Exception)
			{
				on = false;
			}

			if (!_hooked)
			{
				_hooked = true;
				Refii.Core.Engine_RefiiConfig.OnConfigChanged += (name, _) =>
				{
					if (string.Equals(name, Key, StringComparison.OrdinalIgnoreCase)) _enabled = null;
				};
			}

			_enabled = on;
			return on;
		}
	}

	/// <summary>Whether the sun is hidden: the key on and the frame stating no main light. Pure.</summary>
	public static bool Hides(bool enabled, bool hasSky) => enabled && !hasSky;

	/// <summary>
	/// Hides or shows <paramref name="sun"/> for this frame. Shows only a sun this hid, so a
	/// light someone else turned off stays off. Main thread, from Engine_LevelLighting.UpdateSun.
	/// </summary>
	public static void Apply(DirectionalLight3D sun, bool hasSky)
	{
		bool hide = Hides(Enabled, hasSky);
		if (hide)
		{
			if (sun.Visible)
			{
				sun.Visible = false;
				_hidden = sun.GetInstanceId();
				GD.Print("[android] no sun indoors: the sun is hidden");
			}
		}
		else if (_hidden != 0 && _hidden == sun.GetInstanceId())
		{
			_hidden = 0;
			if (!sun.Visible) sun.Visible = true;
		}
	}
}
