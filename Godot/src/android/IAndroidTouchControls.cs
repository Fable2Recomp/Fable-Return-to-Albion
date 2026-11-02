using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// What the Android host asks of a title's touch controls: whether they are on, which context
/// they are in, and the two hand-offs — the rectangles touches pass through, and the options
/// button. The controls themselves are the title's (Fable 2's lay out its wheel, spells and pub
/// games); the host never names them.
/// </summary>
public interface IAndroidTouchControls
{
	bool ControlsEnabled { get; }

	TouchContext CurrentContext { get; }

	System.Action<Rect2>? OptionsPressed { get; set; }

	System.Func<IEnumerable<Rect2>>? PassThrough { get; set; }

	/// <summary>The controls as last laid out, the player's own layout applied: what the touch editor outlines and drags.</summary>
	IReadOnlyList<TouchControl> LaidOut { get; }

	/// <summary>Rereads the touch settings shortly, once refii.toml's cache has seen a change just written (the touch editor's opacity).</summary>
	void RereadSettingsSoon();
}
